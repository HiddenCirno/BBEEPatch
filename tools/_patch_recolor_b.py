# -*- coding: utf-8 -*-
"""按用户拍板的 B 设计改三处：

① 撤销材质路（TintRenderers=false）
   —— 引擎对特效**从不写材质**（颜色烘在 prefab 的 MaterialTinter 插值器里，播放时逐帧写材质属性）。
      我们越过它去写材质 = 比引擎更低一层 ⇒ tinter 每帧覆盖 + 实例材质挂到共用渲染器上就污染别人
      （实测症状：角色着色、高文特效被染）。

② AssetExclude 接进统一判据（AllowBattleEffect / AllowInterpolator）
   —— 以前那份名单只挡资产路，实例路/插值器路照样染 `es_stand_01` 这类**挂在角色身上的常驻特效**
      ⇒ 角色被染红。现在任何入口都不碰。

③ 钩全所有资产加载重载（LoadAsset / LoadAllAssets / LoadAssetAsync）
   —— 私有副本(复制→只染副本→返回副本)只有能被拦到的资产才走得到。
      实测原来只挂了 `LoadAsset(Type,String,String,Object,AssetLogType)` 一个重载，
      ES 的特效资产大量从别的重载/别的 API 进来 ⇒ 副本路形同虚设。
"""
import io

BASE = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch"
PIPE = BASE + r"\Pipelines\Recolor\RecolorPipeline.cs"
POL = BASE + r"\Pipelines\Recolor\TintPolicy.cs"
PLG = BASE + r"\Plugin.cs"
CFG = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\BepInEx\config\ace.bbee.jspatch.cfg"


def rw(path, subs):
    s = io.open(path, encoding="utf-8").read()
    for old, new in subs:
        assert old in s, "锚点没找到: %r" % old[:60]
        s = s.replace(old, new, 1)
    io.open(path, "w", encoding="utf-8").write(s)


# ---------------------------------------------------------------- ① 撤销材质路（开关在 TintBrush 与 cfg 里）

# TintBrush.TintGameObject 里的材质路开关
TB = BASE + r"\Pipelines\Recolor\TintBrush.cs"
s = io.open(TB, encoding="utf-8").read()
old = "        if (RecolorConfig.TintRenderers?.Value == true) ok += TintMaterials(go, t);"
if old in s:
    s = s.replace(old, "        // ⚠ 材质路默认关(见 TintMaterials 的事故记录): 引擎对特效从不写材质。\n"
                       "        if (RecolorConfig.TintRenderers?.Value == true) ok += TintMaterials(go, t);", 1)
    io.open(TB, "w", encoding="utf-8").write(s)
    print("① TintBrush 材质路已标注默认关")

# ---------------------------------------------------------------- ② 排除名单进统一判据
rw(POL, [(
    "        var kw = RecolorConfig.NameFilter?.Value ?? \"es,hit_\";",
    "        // ⚠ 常驻特效(挂在角色身上的待机光效 es_stand_01 / es_stand_02 / es_standby_001)必须在\n"
    "        // **所有入口**都排除 —— 以前只有资产路看 AssetExclude, 实例路照样染它 ⇒ 角色被染红。\n"
    "        if (Excluded(effectName)) return false;\n"
    "        var kw = RecolorConfig.NameFilter?.Value ?? \"es,hit_\";"
)])

rw(POL, [(
    "        var ex = RecolorConfig.TintExclude?.Value;",
    "        if (Excluded(rootName))\n"
    "        {\n"
    "            LogEx.Once(\"recolor|skip|assetex|' + Reflect.Normalize(rootName) + '\",\n"
    "                       $\"[特效换色] \\\"{rootName}\\\" 在 AssetExclude 里(常驻特效), 任何入口都不染\");\n"
    "            return false;\n"
    "        }\n"
    "        var ex = RecolorConfig.TintExclude?.Value;"
)])

# 追加 Excluded 实现
s = io.open(POL, encoding="utf-8").read()
marker = "    /// <summary>取特效自己的名字(最近的 VFXEffectHub 节点名), 取不到就退回组件自己的节点名。</summary>"
helper = '''    /// <summary>
    /// 常驻特效排除名单（`AssetExclude`）—— **所有入口共用**。
    /// 名单里的东西（`es_stand_01` / `es_stand_02` / `es_standby_001` 这类挂在角色身上的待机光效）
    /// 一旦被染, 表现就是"角色变成一个发光的球"（PROJECT_STATE 里记过）。
    /// </summary>
    internal static bool Excluded(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        try
        {
            foreach (var k in Cfg.List(RecolorConfig.AssetExclude?.Value))
                if (k.Length > 0 && name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        catch { }
        return false;
    }

''' + marker
assert marker in s
s = s.replace(marker, helper, 1)
io.open(POL, "w", encoding="utf-8").write(s)
print("② 排除名单已接进统一判据")

# ---------------------------------------------------------------- ③ 钩全资产加载重载
rw(PIPE, [(
    """        var m = FindMethod(abp, "LoadAsset", Reflect.All);
        if (m == null || m.DeclaringType != abp || m.ReturnType.Name != "Object")
        {
            Plugin.Log?.LogWarning($"  [换色] AssetBundleProvider.LoadAsset 不宜挂 " +
                                   $"(found={m != null}, decl={m?.DeclaringType?.Name}, ret={m?.ReturnType.Name})");
            return 0;
        }
        int n = 0;
        if (Patch(harmony, m, new HarmonyMethod(Mi(nameof(LoadAssetPrefix))),
                            new HarmonyMethod(Mi(nameof(LoadAssetPostfix))))) n++;
        return n;""",
    """        // ★ 钩【所有】加载入口, 不是一个重载:
        //   实测原来只挂了 LoadAsset(Type,String,String,Object,AssetLogType) 一个,
        //   ES 的特效资产大量从别的重载/别的 API 进来 ⇒ 资产重定向 + 私有副本形同虚设
        //   (整局只命中 2 个资产)。这里把 LoadAsset / LoadAllAssets / LoadAssetAsync
        //   在本类声明的方法全挂上, 并且**标出哪个重载真的在被使用**(日志里能看出来)。
        int n = 0;
        var pre = new HarmonyMethod(Mi(nameof(LoadAssetPrefix)));
        var post = new HarmonyMethod(Mi(nameof(LoadAssetPostfix)));
        foreach (var m in abp.GetMethods(Reflect.All))
        {
            if (m.DeclaringType != abp) continue;
            string nm = m.Name;
            if (nm != "LoadAsset" && nm != "LoadAllAssets" && nm != "LoadAssetAsync"
                && nm != "LoadAssetWithSubAssets") continue;
            var rt = m.ReturnType.Name;
            if (rt != "Object" && rt != "Object[]" && rt != "GameObject" && rt != "AssetBundleRequest") continue;
            if (Patch(harmony, m, pre, post)) n++;
        }
        if (n == 0)
            Plugin.Log?.LogWarning("  [换色] AssetBundleProvider 上一个加载入口都没挂上");
        return n;"""
)])
print("③ 资产加载入口已扩到全部重载")

# ---------------------------------------------------------------- cfg: 关掉材质路
L = io.open(CFG, encoding="utf-8").read().split("\n")
for i, l in enumerate(L):
    if l.startswith("TintRenderers = "):
        L[i] = "TintRenderers = false"
io.open(CFG, "w", encoding="utf-8").write("\n".join(L))
print("cfg: TintRenderers=false")
