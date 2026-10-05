using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BlazblueJsPatch;

internal static class Patcher
{
    // 游戏自带的 PuerTS 加载器 (dump.cs: Namespace Js, TypeDefIndex 3303)
    private static readonly string[] TypeCandidates =
    {
        "Il2CppJs.RuntimeLoader",   // Il2CppInterop 常见前缀形式
        "Js.RuntimeLoader",         // 无前缀形式
    };

    /// <summary>
    /// ★★★ 只挂 `ReadFile`。**绝不要加回 `GetAssetBundleFile`。**
    ///
    /// 2026-10-02 启动崩溃的完整复盘（证据是两份日志的 hook 清单对比）：
    ///
    ///   能进游戏的版本:
    ///     已挂钩 RuntimeLoader.ReadFile(...)
    ///     [Error] Failed to patch GetAssetBundleFile: Parameter "filepath" not found   ← 挂【失败】
    ///     已挂钩 RuntimeLoader.FileExists(...)
    ///
    ///   崩溃的版本:
    ///     已挂钩 RuntimeLoader.ReadFile(...)
    ///     已挂钩 RuntimeLoader.GetAssetBundleFile(String requirePath, String& debugPath)  ← 多出来的
    ///     已挂钩 RuntimeLoader.FileExists(...)
    ///
    /// `GetAssetBundleFile` 的形参名是 `requirePath`, 而 postfix 写的是 `filepath`,
    /// Harmony 按名字绑定 → **IL Compile Error, 钩子从来没挂上过**。
    /// 这个"静默失败"一直是无害的 —— 直到我去"修"它: 把形参改成 `__0`, 挂上了, 游戏随即
    /// **一启动就硬崩**(coreclr.dll 访问违例), 崩点在 `[exist] puerts/init.mjs -> True` 之后、
    /// `[load ]` 之前。
    ///
    /// 机制很好理解: `.mjs` 文件在 AssetBundle 里, 加载器走的是
    /// `FileExists → GetAssetBundleFile`, **根本不经过 ReadFile** ——
    /// 所以这个钩子一挂上, 第一次取 JS 文件就会进到我们的 postfix 并当场崩掉,
    /// `ReadFile` 那条路自然永远到不了(这也正是 `[load ]` 那行从不出现的原因)。
    ///
    /// ⚠ 我先后试了两种"修法"(`__0` 和 `requirePath`), **两次都崩** ——
    ///   因为崩的原因是【挂了这个钩子本身】, 跟形参怎么命名无关。
    ///   所以正确做法是**不挂它**, 恢复成一直以来的实际状态。
    ///   代价: 走 bundle 的 JS 文件不会被改写(与历史行为一致, 不是回归)。
    ///
    /// 未查明: 为什么单单这个方法挂不得(ReadFile 同样是 `(string, out string)` 却没事)。
    /// 想再试, 请**单独一次部署**验证, 不要夹带。
    /// </summary>
    private static readonly string[] MethodCandidates = { "ReadFile" };

    internal static ModuleHost Host;

    /// <summary>
    /// 引导: 先挂"不属于任何模块"的两处基础补丁(JS 加载器转发 / DLC 解锁),
    /// 再把清单交给 <see cref="ModuleHost"/> 逐条挂载。
    ///
    /// 之所以这两处不进模块表: 它们没有独立开关, 而且是**其它一切的前提** ——
    /// JS 加载器转发不挂上, 整个插件等于没加载。
    /// </summary>
    public static int Apply(Harmony harmony)
    {
        int n = ApplyBootstrap(harmony);

        Host = ModuleTable.Build();
        n += Host.MountAll(harmony);
        return n;
    }

    private static int ApplyBootstrap(Harmony harmony)
    {
        int n = 0;
        var type = ResolveLoaderType();
        if (type == null)
        {
            // ⚠ 这里【不能】直接 return: 模块表还没挂。
            //   上一版就是这样写的 —— JS 加载器找不到 = 整张模块表一个都不挂,
            //   而日志上只看到一条"找不到 Loader", 完全看不出后面全被跳过了。
            Plugin.Log.LogError("找不到 Js.RuntimeLoader 类型, 列出所有名字含 Loader 且命名空间带 Js 的类型:");
            foreach (var t in AllTypes().Where(t => t.Name.Contains("Loader")))
                Plugin.Log.LogError($"   {t.FullName}");
        }
        else
        {
            Plugin.Log.LogInfo($"定位到加载器类型: {type.FullName}");

            var postfix = new HarmonyMethod(typeof(Patcher).GetMethod(nameof(ReadFilePostfix), BindingFlags.Static | BindingFlags.Public));

            // ⚠ 两个方法的第一个参数【名字不同】(filepath / requirePath), 所以各用各的 postfix。
            //   (2026-10-02 复盘: 曾经为了"修好"GetAssetBundleFile 而在这里加过第二个 postfix,
            //    并试过 `__0` 按位置绑定 —— **两种都让游戏一启动就崩**。
            //    结论见 MethodCandidates 上方: 那个钩子不能挂。)
            foreach (var mname in MethodCandidates)
            {
                var m = AccessTools.Method(type, mname);
                if (m == null) { Plugin.Log.LogWarning($"  {type.Name}.{mname} 未找到"); continue; }
                var body = postfix;   // 只剩 ReadFile 一个候选 —— 见 MethodCandidates 上面的说明
                try
                {
                    harmony.Patch(m, postfix: body);
                    Plugin.Log.LogInfo($"  已挂钩 {type.Name}.{mname}{Describe(m)}");
                    n++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"  挂钩 {type.Name}.{mname} 失败: {e.Message}");
                }
            }

            // FileExists 也一起挂(用于观察), 不影响返回内容
            var fe = AccessTools.Method(type, "FileExists");
            if (fe != null)
            {
                try
                {
                    harmony.Patch(fe, postfix: new HarmonyMethod(typeof(Patcher).GetMethod(nameof(FileExistsPostfix), BindingFlags.Static | BindingFlags.Public)));
                    Plugin.Log.LogInfo($"  已挂钩 {type.Name}.FileExists{Describe(fe)}");
                    n++;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"  挂钩 FileExists 失败: {e.Message}"); }
            }
        }

        // ---- DLC 解锁: SdkManager.IsDlcInstalled(ulong) -> 恒 true ----
        // 配色/皮肤/角色页的 DLC 检测全都走这一个方法(JS 侧 3 处调用点),
        // 内容是本地就有的, 所以改这一处即可全部解锁。
        var sdk = ResolveType("Il2CppSdkManager", "SdkManager");
        if (sdk == null)
        {
            Plugin.Log.LogWarning("  找不到 SdkManager 类型, DLC 解锁未生效");
        }
        else
        {
            var isDlc = AccessTools.Method(sdk, "IsDlcInstalled");
            if (isDlc == null)
            {
                Plugin.Log.LogWarning($"  找不到 {sdk.Name}.IsDlcInstalled");
            }
            else
            {
                try
                {
                    harmony.Patch(isDlc, postfix: new HarmonyMethod(
                        typeof(Patcher).GetMethod(nameof(IsDlcInstalledPostfix), BindingFlags.Static | BindingFlags.Public)));
                    Plugin.Log.LogInfo($"  已挂钩 {sdk.Name}.IsDlcInstalled{Describe(isDlc)} -> 恒 true (DLC 解锁)");
                    n++;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"  挂钩 IsDlcInstalled 失败: {e.Message}"); }
            }
        }

        return n;
    }

    private static Type ResolveType(params string[] names)
    {
        foreach (var n in names)
        {
            var t = AccessTools.TypeByName(n);
            if (t != null) return t;
        }
        return null;
    }

    private static string Describe(MethodBase m) =>
        "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")";

    // ------------------------------------------------------------------ 类型解析

    private static Type ResolveLoaderType()
    {
        foreach (var name in TypeCandidates)
        {
            var t = AccessTools.TypeByName(name);
            if (t != null) return t;
        }
        // 兜底: 全量扫描
        return AllTypes().FirstOrDefault(t =>
            t.Name == "RuntimeLoader" &&
            t.Namespace != null &&
            t.Namespace.EndsWith("Js", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<Type> AllTypes()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts;
            try { ts = asm.GetTypes(); }
            catch { continue; }
            foreach (var t in ts) yield return t;
        }
    }

    // ------------------------------------------------------------------ 补丁体

    private static int _logged;

    /// <summary>
    /// `RuntimeLoader.ReadFile(string filepath, out string debugpath)`
    ///
    /// ⚠⚠ **不要**把形参写成 `__0`(按位置)。2026-10-02 试过一次, 游戏在
    /// 【第一次 JS 文件加载】处硬崩(访问违例, 崩在 coreclr.dll —— 托管侧崩溃, try/catch 抓不到):
    /// 健康日志里 `[exist] puerts/init.mjs -> True` 后面**紧跟** `[load ] puerts/init.mjs`,
    /// 而那一次那行 `[load ]` 再也没出现。
    /// 换成按位置名之后就复现不出来了 —— 所以**按名字绑定**是这条路上唯一验证过的写法。
    ///
    /// ⚠ 同类的 `GetAssetBundleFile` **不要再挂钩子** —— 原因见 `MethodCandidates` 上方的完整复盘。
    /// </summary>
    public static void ReadFilePostfix(string filepath, ref string __result)
    {
        try
        {
            if (__result == null)
            {
                // ⚠ 这里以前是【静默 return】—— 日志上什么都不留, 事后完全分不清
                //   "这个方法没被调用" 和 "调用了但原始返回是 null"。每个 return 都要说明原因。
                LogEx.Once("js|readfile|null|" + filepath, $"[load ] {filepath} 原始返回 null, 跳过改写");
                return;
            }
            if (_logged < 40 && Plugin.Log != null)
            {
                _logged++;
                Plugin.Log.LogInfo($"[load ] {filepath}  ({__result.Length} 字符)");
            }
            __result = JsPatchManager.Process(filepath, __result, "ReadFile");
        }
        catch (Exception e)
        {
            Plugin.Log?.LogError($"ReadFilePostfix 异常: {e}");
        }
    }

    /// <summary>SdkManager.IsDlcInstalled(ulong dlcId) -> 恒 true</summary>
    public static void IsDlcInstalledPostfix(ref bool __result)
    {
        __result = true;
    }

    /// <summary>`RuntimeLoader.FileExists(string filepath)` —— 按名字绑定, 同 <see cref="ReadFilePostfix"/>。</summary>
    public static void FileExistsPostfix(string filepath, ref bool __result)
    {
        // 目前只观察; 保留钩子以便后续支持"注入新模块"
        if (Plugin.Log != null && _logged < 40)
            Plugin.Log.LogInfo($"[exist] {filepath} -> {__result}");
    }
}
