using System;
using System.Collections.Generic;
using System.Reflection;
using GamePlay;
using HarmonyLib;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 特效换色管线。
///
/// 一条管线, 四种载体, 一个总开关
/// ────────────────────────────
///   ① 粒子路      ParticleSystem.startColor —— 颜色烘在**实例**的粒子系统里(材质是共享的, 不动)
///   ② 插值器路    MaterialColorInterpolator.startValue/endValue —— Mesh 型特效唯一的颜色来源,
///                 同时也是粒子型特效的**第二层**颜色(只改 startColor 会"一半变色")
///   ③ Prefab 路   AssetBundleProvider.LoadAsset —— 加载时改内存里的 prefab, 实例自动继承
///   ④ 角色本体    ActorMaterialOverride —— **默认关**(会让角色变成刺眼的洋红, 且对特效无效)
///
/// ★ 「配置改了要立刻变色」是怎么做到的
/// ──────────────────────────────────
/// 关键事实(反汇编, 别当假设):
///   · `MaterialTinter.Play(entries, proxy)` 尾部【就是】`CreateInterpolatorSets(entries, ...)`
///     —— 每次播放都会重建集合;
///   · `InterpolatorSet::.ctor` 把 `InterpolatorBase[]` **按引用**存进 +0x40(`mov [r14+0x40], rdi`), 没克隆。
/// 所以集合和源数组指向同一批插值器对象 —— 就地改写源数组, 对**已经在播**的特效一样生效。
///
/// 于是策略是: 每帧比对配置【指纹】(颜色+亮度策略+通道列表), 一变就在接下来的一小段时间里
/// 顺着 `VFXEffectBase.Update` 把所有在播的特效重染一遍。
/// 用 Update 做追染而不是维护一张"活对象表", 是因为持有一张表就等于**持有原生对象的托管引用**,
/// 特效是池化复用的, 那样会把已经回池的对象钉住不放 —— 泄漏且难查。Update 只在"配置刚改过"的
/// 那几十帧里干活, 平时只做一次整数比较。
/// </summary>
internal static class RecolorPipeline
{
    // ------------------------------------------------------------------ 安装

    internal static int Apply(Harmony harmony)
    {
        int n = 0;
        n += HookBattleEffects(harmony);
        n += HookBullets(harmony);
        n += HookTrails(harmony);
        n += HookInterpolatorConsumers(harmony);
        n += HookLiveFollow(harmony);
        n += HookPrefabAssets(harmony);
        n += HookCharacter(harmony);

        if (n == 0) Plugin.Log?.LogWarning("  [换色] 一个探针都没挂上 —— 版本可能不匹配");
        else
        {
            Plugin.Log?.LogInfo($"  [换色] 管线就绪, 共 {n} 处; " +
                                 $"总开关={RecolorConfig.On}, 目标色={Cfg.ToHex(RecolorConfig.Current.Color)}");
            // ★★ 【启动时把"生效口径"整条打出来】——今天吃过的亏：
            //   配置有**两条**改法(改文件 / 在 F8 面板里改)，而改文件**只在启动时读一次**。
            //   于是"我改了文件"和"你游戏里的实际值"可以完全不是一回事，
            //   排查时行为对不上账，只能瞎猜是缓存/池/线程。
            //   把生效值打在启动日志里，这种事下次一眼就能对。
            try
            {
                Plugin.Log?.LogInfo(
                    $"  [换色] 生效配置: 口径={RecolorConfig.Current.Mode}  上限={RecolorConfig.MaxScale?.Value}  " +
                    $"保亮度={RecolorConfig.KeepBrightness?.Value}  亮度倍率={RecolorConfig.BrightnessScale?.Value}  " +
                    $"点名口径={RecolorConfig.ObjectTarget.Mode}");
                Plugin.Log?.LogInfo(
                    $"  [换色] 点名子物体=\"{RecolorConfig.TintObjects?.Value}\"  " +
                    $"宿主=\"{RecolorConfig.TintObjectHosts?.Value}\"  属性名单=\"{RecolorConfig.TintProps?.Value}\"");
            }
            catch (Exception e) { LogEx.Err("Recolor.DumpEffectiveConfig", e); }
        }
        return n;
    }

    // ---------------------------------------------------------------- ① 战斗特效

    private static MemberInfo _aemOwner;

    /// <summary>私有副本缓存: 同一个资产只复制一次, 之后都返回同一份副本。</summary>
    private static readonly Dictionary<string, GameObject> _cloneCache = new Dictionary<string, GameObject>();

    private static int HookBattleEffects(Harmony harmony)
    {
        int n = 0;
        var aem = AccessTools.TypeByName("GamePlay.ActorEffectMgr");
        if (aem == null) { Plugin.Log?.LogWarning("  [换色] 找不到 GamePlay.ActorEffectMgr"); return 0; }

        _aemOwner = Reflect.Member(aem, "Owner", Reflect.All);
        if (_aemOwner == null) Plugin.Log?.LogWarning("  [换色] ActorEffectMgr.Owner 解析不到");

        var post = new HarmonyMethod(Mi(nameof(CreateFxPostfix)));
        var pre = new HarmonyMethod(Mi(nameof(CreateFxPrefix)));
        foreach (var m in aem.GetMethods(Reflect.All))
        {
            // ⚠ 只挂本类自己声明的方法。挂继承来的方法在 IL2CPP 下会踩到
            //   "not implemented" 的坑(本项目挂 ActorVisualSpine.SetSkin 时直接 SEHException 崩进程,
            //   而 HarmonyX 当时已经警告过 "You should only patch implemented methods")。
            if (m.Name != "createVisualEffect") continue;
            if (m.DeclaringType != aem) continue;
            if (m.ReturnType.Name != "VFXEffectHub") continue;
            if (Patch(harmony, m, pre, post)) n++;
        }

        // 池化复用: 同一个 hub 会被反复 Reactivate。粒子的 startColor 是留在实例上的,
        // 不复位的话下一次用还是上一把的颜色。这里顺手重染一遍。
        //
        // ★ 2026-10-04 补: 只挂 Reactivate 是不够的 —— 那只是"回收后再用"。
        //   首次激活走的是 `DoStart`(private) / `EnableVisualElements`, 而
        //   `ActorEffectMgr.createVisualEffect` 也不是唯一的出生点:
        //   受击特效、纹章/剑气(经 BulletMgr)、跳跃脚下粒子这些是别的系统拉起来的 hub,
        //   从没经过那条漏斗 ⇒ 一个钩子都没碰上 ⇒ "有一部分没被染色"。
        //   挂到 hub 自己的激活口上, 就与"谁生的"无关了 ✓
        var hub = AccessTools.TypeByName("NOAH.VFX.VFXEffectHub");
        if (hub != null)
        {
            var rpost = new HarmonyMethod(Mi(nameof(HubReactivatePostfix)));
            foreach (var m in hub.GetMethods(Reflect.All))
            {
                if (m.DeclaringType != hub) continue;
                // ★ Awake / Start 是【最强的一道口】: 只要 hub 被实例化, Unity 一定会调 Awake。
                //   DoStart/EnableVisualElements/Reactivate 都可能被某些调用方跳过,
                //   Awake 不会 —— 多段跳的环、剑气拖影就是"出生口一直找不到"的那两类。
                if (m.Name != "Reactivate" && m.Name != "DoStart" && m.Name != "EnableVisualElements"
                    && m.Name != "Awake" && m.Name != "Start") continue;
                if (Patch(harmony, m, null, rpost)) n++;
            }
        }
        return n;
    }

    /// <summary>
    /// ★ C 方案的第一步: 看**解析结果**（effName）长什么样。
    ///
    /// 依据: 皮肤的"专属特效"是配置里的两处 —— `AvatarSkinConf.effects`(:SkinEffect.skinToEffect)
    /// 与 `ClientSkinConf.effectSubPath`; 引擎读完之后把这个结果作为 `effName` 交给
    /// `ActorEffectMgr.createVisualEffect`。所以只要在**这里**改字符串, 就等于"让所有皮肤都用我们指定的那套特效",
    /// 而且**完全不用复制 prefab**(游戏自己按这个路径去加载) —— 这正是用户要的"启用时覆盖所有皮肤的自定义换色特效"。
    ///
    /// 本行只观察: 每个不同的 effName 打一次, 看清里面**有没有皮肤号**(esskin_XX)。
    /// </summary>
    public static void CreateFxPrefix(ref string effName)
    {
        try
        {
            if (effName == null) return;
            if (_effNames.Add(effName))
                Plugin.Log?.LogInfo($"[特效换色:eff] createVisualEffect effName=\"{effName}\"");

            // ★★ C 方案本体: 在这里把"皮肤号"插进去。
            //
            // 实测(2026-10-04): effName 是**逻辑名、不含皮肤号**
            //   Role/Es/es_attack1_01  /  Role/Es/es_dash_01  /  Hit/hit_018
            // 而皮肤那套资产的实际路径是
            //   Effect/Prefab/Role/Es/esskin_12/es_dash_01   ← 已经在离线资产表里核对过存在
            // ⇒ 只要在这里把 effName 改成 `Role/Es/esskin_12/es_dash_01`,
            //   加载器就会自己去取那套资产 ⇒ **不用复制 prefab、不污染、且对任何皮肤都成立**
            //   (这正是"启用时覆盖所有皮肤的自定义换色特效")。
            //
            // 只动 `Role/Es/` 这一族 —— Avatar/Hit/Common/别的角色一概不碰。
            if (RecolorConfig.ForceSkinSet?.Value != true) return;
            string skin = (RecolorConfig.ForceSkinSetName?.Value ?? "").Trim();
            if (skin.Length == 0) return;
            if (TintPolicy.Excluded(effName)) return;

            const string key = "Role/Es/";
            int at = effName.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return;
            if (effName.IndexOf("esskin_", StringComparison.OrdinalIgnoreCase) >= 0) return;   // 已经是皮肤套
            string rewritten = effName.Substring(0, at + key.Length) + skin + "/" + effName.Substring(at + key.Length);
            effName = rewritten;
            LogEx.Once("recolor|effredirect|" + Reflect.Normalize(rewritten),
                       $"[特效换色] 特效重定向 -> \"{rewritten}\"");
        }
        catch (Exception e) { LogEx.Err("Recolor.CreateFxPrefix", e); }
    }

    private static readonly HashSet<string> _effNames = new HashSet<string>();

    /// <summary>战斗特效的唯一漏斗。`__instance.Owner` 就是归属角色, 所以"只改 ES"不依赖名字匹配。</summary>
    public static void CreateFxPostfix(object __instance, object __result)
    {
        try
        {
            if (__instance == null || __result == null) return;
            if (!RecolorConfig.On) return;

            var owner = Reflect.Read(__instance, _aemOwner) as ActorBase;
            string effName = Reflect.Name(Reflect.Cast<MonoBehaviour>(__result)?.gameObject);

            if (RecolorConfig.Verbose)
                LogEx.Once("recolor|seen|" + Reflect.Normalize(effName),
                           $"[特效换色:seen] createVisualEffect \"{effName}\" " +
                           $"owner={(owner == null ? "null" : owner.GetType().Name)} " +
                           // ★ 把【当前动作名】和特效名绑在这一行上 —— 这样"某个动作到底拉的是哪个特效"
                           //   就不用猜了（用户问:"这个动作真的是 dash01 吗?"）。
                           //   `ActorBase.ActionMgr.CurrentActionName` 是本项目现成的取法(见 DashInvincible)。
                           $"当前动作=\"{SafeActionName(owner)}\"");

            // ★★ 同样必须放在排除名单之前（原因见 HubReactivatePostfix 里的长注释:
            //   `es_stand_01` 在 AssetExclude 里, 不放前面就等于这条路不存在）。
            //   注意 go 要从 __result 直接取 —— 原来它是在排除名单**之后**才算的。
            MaybeTintObjects(Reflect.Cast<MonoBehaviour>(__result)?.gameObject, effName);

            if (!TintPolicy.AllowBattleEffect(owner, effName)) return;

            var go = Reflect.Cast<MonoBehaviour>(__result)?.gameObject;
            var t = RecolorConfig.Current;
            int ok = TintBrush.TintGameObject(go, t);
            if (ok > 0)
                LogEx.Once("recolor|done|" + Reflect.Normalize(effName),
                           $"[特效换色] 已改战斗特效 \"{effName}\" ({ok} 处) -> {Cfg.ToHex(t.Color)}" +
                           $"  [载体 {TintBrush.CarrierReport(go)}]");
            else
                LogEx.WarnOnce("recolor|nops|" + Reflect.Normalize(effName),
                               $"[特效换色] \"{effName}\" 命中但一处都没改写 (nops) —— 载体清单 " +
                               $"{TintBrush.CarrierReport(go)}; " +
                               $"若清单非空, 说明颜色不在我们认的属性里(看 TintProperties 与 propmiss 行)");
            MaybeTintSubTex(go, effName);
        }
        catch (Exception e) { LogEx.Err("Recolor.CreateFxPostfix", e); }
    }

    public static void HubReactivatePostfix(object __instance)
    {
        try
        {
            if (__instance == null || !RecolorConfig.On) return;
            var go = Reflect.Cast<MonoBehaviour>(__instance)?.gameObject;
            if (go == null) return;
            // ⚠ 不要直接读 `go.name` —— 池化对象被回收/销毁时这会抛 NullReferenceException,
            //   而它抛在 `VFXEffectHub.Reactivate` 的 native->managed trampoline 里,
            //   会把**整个 Reactivate 打断**（日志实证: 按 Summon 之后 Reactivate 抛 NRE,
            //   特效状态停在半途 ⇒ 角色身上留下没被回收的颜色叠加 = "角色被染色"）。
            //   Reflect.Name 内部有 try/catch, 读不到就返回 ""。
            string rawName = Reflect.Name(go);
            string name = Reflect.Normalize(rawName);
            if (name.Length == 0) return;

            // ★★ 这一行**必须放在排除名单之前**（2026-10-05 踩的坑，日志实证）：
            //   `es_stand_01` 在 `AssetExclude` 里（当初为了不让角色站姿被整体染色而加的），
            //   于是它被 `AllowBattleEffect` 拦在门外 —— 而我新加的"按子物体名下刀"那条路
            //   挂在后面，**一次都没被调用过**（日志里 `特效换色:子物体` 出现 0 次，
            //   只有一行 `按排除名单跳过 "es_stand_01"`）。查了半天才发现是自己的门挡的。
            //   放宽的依据: `MaybeTintObjects` 自己有两道**精确**闸门
            //   （宿主名必须命中 `TintObjectHosts`，子物体名必须命中 `TintObjects`），
            //   比"整棵子树染色"窄得多 ⇒ 让它越过粗粒度的排除名单是安全的。
            MaybeTintObjects(go, rawName);

            // 名字过滤同样要过 —— 否则池化复用这条会把 UI 特效也染了
            if (!TintPolicy.AllowBattleEffect(null, rawName)) return;
            int k = TintBrush.TintGameObject(go, RecolorConfig.Current);
            LogEx.Once("recolor|hub|" + name,
                       $"[特效换色] hub 激活 \"{name}\" ({k} 处)  [载体 {TintBrush.CarrierReport(go)}]");
            MaybeTintSubTex(go, rawName);
            DumpTinterArrays(go, name);
        }
        catch (Exception e) { LogEx.Err("Recolor.HubReactivatePostfix", e); }
    }

    // ---------------------------------------------------------------- ② 插值器消费点

    private static int HookInterpolatorConsumers(Harmony harmony)
    {
        int n = 0;
        n += PatchRegex(harmony, "NOAH.VFX.MaterialTinter", "Play", nameof(TinterPlayPrefix));
        n += PatchRegex(harmony, "NOAH.VFX.MaterialTinter", "CreateInterpolatorSets", nameof(TinterCreateSetsPrefix));

        // MaterialTinterProxy 自带一份 MaterialInterpolators, 播的时候会用【它自己那份】,
        // 所以必须单独改 —— 只改 tinter 的数组会漏掉 proxy 这条。
        //
        // ⚠ 不要为了"精确匹配签名"去 GetMethod(name, flags, binder, new[]{ 某个Type }, null):
        //   那个 type 一旦解析成 null, GetMethod 会当场抛 ArgumentNullException ——
        //   而它会把【整个换色模块】一起拖死(异常冒到 ModuleHost 那层就是"模块挂载失败")。
        //   proxy 上只有 Restart 这一个候选, 按名字取参数最多的那个就够了。
        var proxy = AccessTools.TypeByName("NOAH.VFX.MaterialTinterProxy");
        if (proxy == null)
        {
            Plugin.Log?.LogWarning("  [换色] 找不到 NOAH.VFX.MaterialTinterProxy, proxy 那条路不可用");
        }
        else
        {
            var m = FindMethod(proxy, "Restart", Reflect.All);
            if (m == null || m.DeclaringType != proxy)
                Plugin.Log?.LogWarning($"  [换色] MaterialTinterProxy.Restart 不可挂 " +
                                       $"(found={m != null}, decl={m?.DeclaringType?.Name}) —— proxy 那条路静默失效");
            else if (Patch(harmony, m, null, new HarmonyMethod(Mi(nameof(ProxyRestartPrefix)))))
                n++;
        }

        return n;
    }

    public static void TinterPlayPrefix(object[] __args, object __instance)
        => ConsumeEntries(__args, __instance, "MaterialTinter.Play");

    public static void TinterCreateSetsPrefix(object[] __args, object __instance)
        => ConsumeEntries(__args, __instance, "MaterialTinter.CreateInterpolatorSets");

    /// <summary>消费点共用: 第一个参数就是即将被使用的 InterpolatorBase[] —— 就地改写它。</summary>
    private static void ConsumeEntries(object[] __args, object __instance, string where)
    {
        try
        {
            // ★ 总开关。插值器路曾经【独立于粒子路】, 没被总开关管住,
            //   于是"关掉换色后特效还在变色"。这条判据现在只在这里写一次。
            if (!RecolorConfig.On) return;
            if (__args == null || __args.Length < 1 || __args[0] == null) return;

            if (!TintPolicy.AllowInterpolator(__instance, where, out var rootName)) return;

            // ⚠ 这里【不做】整棵子树扫描。Play 是每条特效每次播放都会走的路径,
            //   GetComponentsInChildren 两遍(x2 类型)放在这里是纯浪费 ——
            //   实例级的那一遍已经由 createVisualEffect / Reactivate / Update 追染负责了,
            //   这里只需要把"这一次播放要用的那个数组"改对。
            int n = TintBrush.RewriteArray(__args[0], RecolorConfig.Current, where);
            DumpArraysAtConsume(__instance, where);

            LogEx.Once("recolor|consume|" + where + "|" + Reflect.Normalize(rootName),
                       $"[特效换色:tint] 消费点 {where} 触发, 对象=\"{rootName}\", 改写 {n} 条插值器");
        }
        catch (Exception e) { LogEx.Err("Recolor.ConsumeEntries/" + where, e); }
    }

    private static MemberInfo _proxyInterps;

    /// <summary>
    /// ★ 消费点旁路探针 —— `MaterialTinter`/`MaterialTinterProxy` **真正开始播放**的那一刻，
    /// 把它们的插值器数组逐条打出来（长度 + 每个元素的真实类名 + propName）。
    ///
    /// 为什么必须在这里再打一次：出生时的日志是 `Tinter属性=&lt;无插值器&gt;`，
    /// 而**离线读 prefab 明明是有的**（`es_esbullet_dasha_001` 里那条蓝色 `_TintColor`）。
    /// 要么数组是**播放时才有**，要么我们读错了成员 —— 这两种在"播放当口"一打就分得清。
    /// </summary>
    private static void DumpArraysAtConsume(object __instance, string where)
    {
        try
        {
            if (!RecolorConfig.Verbose) return;
            var go = Reflect.Cast<MonoBehaviour>(__instance)?.gameObject;
            if (go == null) return;
            DumpTinterArrays(go, Reflect.Name(go) + "@" + where);
        }
        catch { }
    }

    public static void ProxyRestartPrefix(object __instance)
    {
        try
        {
            if (__instance == null || !RecolorConfig.On) return;
            DumpArraysAtConsume(__instance, "proxy.Restart");
            if (!TintPolicy.AllowInterpolator(__instance, "MaterialTinterProxy.Restart", out var rootName)) return;

            _proxyInterps = _proxyInterps ?? Reflect.Member(__instance.GetType(), "MaterialInterpolators", Reflect.All);
            var arr = Reflect.Read(__instance, _proxyInterps);
            if (arr == null) return;

            int n = TintBrush.RewriteArray(arr, RecolorConfig.Current, "MaterialTinterProxy.MaterialInterpolators");
            LogEx.Once("recolor|proxy|" + Reflect.Normalize(rootName),
                       $"[特效换色:tint] 消费点 MaterialTinterProxy.Restart 改写 {n} 条插值器");
        }
        catch (Exception e) { LogEx.Err("Recolor.ProxyRestartPrefix", e); }
    }

    // ---------------------------------------------------------------- ③ 追染(配置改了立刻生效)

    private static int _liveRev = int.MinValue;
    /// <summary>追染截止帧。⚠ 必须记【帧号】而不是"再干 N 次": 这个回调是**每个特效每帧**各调一次,
    /// 用次数当预算的话, 屏幕上超过 N 个特效时一个帧内就把预算吃光,
    /// 排在后面的特效永远追不到, 而且计数器归零后再也不会重试。</summary>
    private static int _liveUntilFrame = -1;
    private static int _liveRuns;

    private static int HookLiveFollow(Harmony harmony)
    {
        var t = AccessTools.TypeByName("NOAH.VFX.VFXEffectBase");
        if (t == null) { Plugin.Log?.LogWarning("  [换色] 找不到 VFXEffectBase, 追染不可用"); return 0; }
        var m = FindMethod(t, "Update", Reflect.All);
        if (m == null || m.DeclaringType != t)
        {
            Plugin.Log?.LogWarning("  [换色] VFXEffectBase.Update 找不到或不是本类声明的, 追染不可用");
            return 0;
        }
        if (!Patch(harmony, m, null, new HarmonyMethod(Mi(nameof(VfxUpdatePostfix))))) return 0;
        _liveRev = RecolorConfig.Revision;
        Plugin.Log?.LogInfo("  [换色] 已挂 VFXEffectBase.Update —— 配置改动会在 ~1 秒内追染到正在播的特效");
        return 1;
    }

    /// <summary>
    /// 每帧一次整数比较; 只有【配置指纹变了】之后的若干帧才真正干活。
    /// 这样代价是常数级, 又不需要维护"活对象表"(那会钉住池化对象, 见类注释)。
    /// </summary>
    public static void VfxUpdatePostfix(object __instance)
    {
        try
        {
            // ★★ 普查: 每个特效实例"第一次见到就染一次"。
            //
            // 为什么非要有这一步（2026-10-04）:
            //   挂出生口的路子永远追不完 —— `es_rushup_*`(多段跳的环) 和
            //   `es_esbullet_dasha_*`(剑气拖影) 在本局【一次都没被任何出生钩子看到】
            //   (既没进 createVisualEffect, 也没触发 hub 的 DoStart/EnableVisualElements/Reactivate)。
            //   而 `VFXEffectBase.Update` 是每个活着显示的特效都会走的 —— 从这儿兜底,
            //   就与"谁生的、走哪条路"彻底无关了。
            //   顺带它也是一张【完整清单】: 日志里能看到场上每一个特效的真名,
            //   不用再靠"猜它叫什么、是不是粒子"。
            CensusOnce(__instance);

            // ★★ 点名特效的**持续重写**（2026-10-05 冲刺解剖实测）。
            //
            // 依据：把 `es_attackAir_02`（消耗 MP 的冲刺；弹幕 `esbullet` 的视觉就是它）
            //   整个递归展开后看到——
            //     "11" pattern_023_a  _TintColor=(2.263,2.828,2.828,1) 蓝白
            //     "44" pattern_025_a  _TintColor=(2.263,2.828,2.828,1)
            //     "sword"~"sword(7)" pattern_026_a_wb _TintColor=(2.397,2.996,2.996,1)
            //   而那层蓝就在 `_TintColor`（乘在 pattern_* 贴图上）上 ✓
            //   更要紧的是：**出生那一刻我们写进去的值，0.4s 后已经被游戏覆写回去了**
            //   ⇒ 只在出生口写 = 白写。必须**每帧重写**。
            //   `Tint()` 用"目标色归一 + 保原亮度"⇒ 幂等(不动点), 重复写不会越写越偏。
            try
            {
                var mb2 = Reflect.Cast<MonoBehaviour>(__instance);
                var go2 = mb2?.gameObject;
                if (go2 != null) MaybeTintSubTex(go2, Reflect.Name(go2));
            }
            catch { }

            if (!RecolorConfig.FollowLiveOn) return;

            int rev = RecolorConfig.Revision;
            if (rev != _liveRev)
            {
                _liveRev = rev;
                // 本作逻辑帧 30fps, 30 帧 ≈ 1 秒 —— 够把所有在播的特效各自轮到至少一次
                _liveUntilFrame = SafeFrame() + 30;
                _liveRuns++;
                Plugin.Log?.LogInfo($"[特效换色] 配置变化 -> 开始追染正在播的特效 " +
                                    $"(目标色 {Cfg.ToHex(RecolorConfig.Current.Color)}, 第 {_liveRuns} 次)");
            }
            if (SafeFrame() > _liveUntilFrame) return;
            if (!RecolorConfig.On) return;

            var mb = Reflect.Cast<MonoBehaviour>(__instance);
            var go = mb?.gameObject;
            if (go == null) return;

            if (!TintPolicy.AllowInterpolator(__instance, "追染", out var rootName)) return;
            int n = TintBrush.TintTintersOn(go, RecolorConfig.Current);
            if (n > 0)
                LogEx.Once("recolor|follow|" + Reflect.Normalize(rootName),
                           $"[特效换色] 追染 \"{rootName}\" ({n} 条插值器)");
        }
        catch (Exception e) { LogEx.Err("Recolor.VfxUpdatePostfix", e); }
    }

    private static int SafeFrame()
    {
        try { return Time.frameCount; } catch { return Environment.TickCount; }
    }

    // ---------------------------------------------------------------- ★ 普查兜底

    private static readonly Dictionary<IntPtr, int> _census = new Dictionary<IntPtr, int>();

    /// <summary>
    /// ★★ 普查: 每个特效实例"见到就染, 没染到就接着试"。
    ///
    /// 为什么必须【重试】(2026-10-04, 踩到之后才明白):
    ///   特效的 MaterialTinter / ActorTrail / ParticleTinter 那批插值器是**运行时才填的**
    ///   （prefab 里常常是空数组 —— 日志里那一片 `Tinter属性=<无插值器>` 就是它）。
    ///   而上一版是"第一次见到染一次就完事"(HashSet.Add 一次),
    ///   偏偏第一次见到往往【早于填充】⇒ 改写 0 处 ⇒ 然后再也不看 ⇒
    ///   表现成"访问到了、有载体、就是永远 0 处"(silhouette_601 就是这么被坑的)。
    ///   现在: 没染到就每帧再试, 试到成功(标记 0)或试够 40 次为止。
    /// </summary>
    private static void CensusOnce(object instance)
    {
        try
        {
            var mb = Reflect.Cast<MonoBehaviour>(instance);
            var go = mb?.gameObject;
            if (go == null) return;

            IntPtr key;
            try { key = go.Pointer; } catch { return; }
            if (key == IntPtr.Zero) return;
            if (!CensusShouldTint(key)) return;

            if (!RecolorConfig.On) return;

            // 同一套判据(屏幕空间/排除名单/名字过滤) —— 不因为"换了入口"就放宽任何一条
            if (!TintPolicy.AllowInterpolator(instance, "普查", out var rootName))
            {
                CensusDone(key);
                return;
            }

            int n = TintBrush.TintGameObject(go, RecolorConfig.Current);
            // ⚠ 按【名字】去重而不是"只打前 N 条" —— 上一版卡了 80 条, 结果后面的特效
            //   一个都没留下名字(正是本项目栽过五次的"限条数=制造假阴性")。
            if (n > 0)
            {
                CensusDone(key);
                LogEx.Once("recolor|census|" + Reflect.Normalize(rootName),
                           $"[特效换色:普查] \"{rootName}\" 染上了 ({n} 处)  [载体 {TintBrush.CarrierReport(go)}]");
            }
            else
            {
                LogEx.Once("recolor|census0|" + Reflect.Normalize(rootName),
                           $"[特效换色:普查] \"{rootName}\" 这一帧一无所获(插值器可能还没填), 继续重试  " +
                           $"[载体 {TintBrush.CarrierReport(go)}]");
            }
        }
        catch (Exception e) { LogEx.Err("Recolor.CensusOnce", e); }
    }

    /// <summary>去重 + 重试闸门。true = 这次该试着染。0 = 已成功(收工); &gt;40 = 试够放弃。</summary>
    private static bool CensusShouldTint(IntPtr key)
    {
        if (_census.TryGetValue(key, out int st))
        {
            if (st == 0 || st > 40) return false;
            _census[key] = st + 1;
            return true;
        }
        if (_census.Count > 4000)
        {
            _census.Clear();
            LogEx.Once("recolor|censuscap", "[特效换色] 普查表到 4000 条, 已清空重建(长局正常现象)");
        }
        _census[key] = 1;
        return true;
    }

    private static void CensusDone(IntPtr key) { _census[key] = 0; }


    private static int HookBullets(Harmony harmony)
    {
        var bm = AccessTools.TypeByName("GamePlay.BulletMgr");
        if (bm == null) { Plugin.Log?.LogWarning("  [换色] 找不到 GamePlay.BulletMgr, 子弹路不可用"); return 0; }
        var m = AccessTools.Method(bm, "createBulletImp");
        if (m == null || m.DeclaringType != bm)
        {
            Plugin.Log?.LogWarning($"  [换色] BulletMgr.createBulletImp 不可挂 (found={m != null})");
            return 0;
        }
        return Patch(harmony, m, null, new HarmonyMethod(Mi(nameof(BulletPostfix)))) ? 1 : 0;
    }

    // ---------------------------------------------------------------- ②e 残影路(ActorTrail)

    /// <summary>
    /// 残影路 —— 让"↓冲刺残影"跟着我们的颜色走。
    ///
    /// ★ 为什么是这个设计（全部有实测依据，别改）：
    ///   1. **颜色在角色材质上**：残影渲染器用的是**角色自己的材质**（探针实测 `SpritePalette`,
    ///      shader `Custom/LitRole`），决定观感的是 `_Emission / _EmissionX/Y/Z / _Skin1..4`
    ///      —— 也就是**皮肤调色板**那套属性（原色皮肤=ES 默认蓝 ⇒ 你看到的蓝）。
    ///      `es_dodge_01/02` 里那个 `ActorTrailProxy._AddColor` 运行时是 (0,0,0,0)，**不起作用**。
    ///   2. **识别靠指针不靠名字**：`ActorTrail` 挂在角色渲染器副本上（节点名就叫 "Renderer"），
    ///      父链里没有 VFXEffectHub ⇒ 名字拿不到。而 `ActorTrailProxy`（在特效自己身上，名字正常）
    ///      有 `m_trail`(0x70) 指回它创建的 trail ⇒ 先认 proxy、再按指针比 trail。
    ///   3. **只在名单内生效**：`TrailNameFilter`（默认 `es_dodge`）。
    ///   4. **写 `rt.material`（实例）**，绝不碰 `sharedMaterial` —— 那条路会把角色本体一起染了（踩过）。
    /// </summary>
    private static int HookTrails(Harmony harmony)
    {
        int n = 0;
        var post = new HarmonyMethod(Mi(nameof(TrailPostfix)));

        var t = AccessTools.TypeByName("NOAH.VFX.ActorTrail");
        if (t == null) Plugin.Log?.LogInfo("  [特效换色] 没找到 NOAH.VFX.ActorTrail, 残影路不参与");
        else
            foreach (var m in t.GetMethods(Reflect.All))
            {
                if (m.DeclaringType != t) continue;
                if (m.Name != "CreateTrail" && m.Name != "Play" && m.Name != "UpdateOnce") continue;
                if (Patch(harmony, m, null, post)) n++;
            }

        var px = AccessTools.TypeByName("NOAH.VFX.ActorTrailProxy");
        var ppost = new HarmonyMethod(Mi(nameof(ProxyPostfix)));
        // ★ Prefix 是**必须的** —— 值是在方法体内部被读走/快照的，Postfix 改太晚（见 ProxyPrefix 注释）。
        var ppre = new HarmonyMethod(Mi(nameof(ProxyPrefix)));
        // `MaterialColorInterpolator.target` —— 挑插值器"到底在驱动哪块材质"的 public 属性。
        _interpTargetMember ??= Reflect.Member(typeof(NOAH.VFXInterpolator.MaterialColorInterpolator), "target", Reflect.All);
        if (_interpTargetMember == null)
            Plugin.Log?.LogWarning("  [特效换色] MaterialColorInterpolator.target 解析不到 —— 残影探针会缺关键一列");
        if (px == null) Plugin.Log?.LogInfo("  [特效换色] 没找到 NOAH.VFX.ActorTrailProxy");
        else
            foreach (var m in px.GetMethods(Reflect.All))
            {
                if (m.DeclaringType != px) continue;
                if (m.Name != "Restart" && m.Name != "Play" && m.Name != "Awake" && m.Name != "EnableVisualElements") continue;
                if (Patch(harmony, m, ppre, ppost)) n++;
            }

        if (n > 0)
            Plugin.Log?.LogInfo($"  [特效换色] 残影路就绪 {n} 处; 名单={RecolorConfig.TrailNameFilter?.Value}");
        return n;
    }

    // ★★ 2026-10-04 重做：**彻底不再跨帧持有裸指针**。
    //
    // 为什么（有 dump 支撑，别退回去）：
    //   上一版用 `HashSet<IntPtr> _wantProxies` 把命中的 `ActorTrailProxy` 指针**永久留着**,
    //   每个 `CreateTrail`（**每帧都调**）都拿它去 `Marshal.ReadIntPtr(p + 0x70)`。
    //   而残影特效是**池化/用完即毁**的 ⇒ 那些指针很快就是**已释放内存**。
    //   读还好，坏在命中之后 `new ActorTrailProxy(p)` 再往它的插值器数组里写 ⇒ **写进已释放内存**。
    //   实测证据：崩溃 dump 的调用帧停在 `ActorTrail.CreateTrail` 里
    //   （反汇编 `ActorTrail.Update`：`0x7c59da call CreateTrail; 0x7c59df add rsp,0x30`，
    //    帧上那个返回地址 0x7C59DF 正是这条 call 的下一句），
    //   而且历史 7 份 dump 全部死在 coreclr 同一个偏移 ⇒ 托管异常逃进原生代码的统一死法。
    //
    // 现在的识别方式：**当场按名字认**，不留指针。
    //   `ActorTrail._visual`(0x58) → `ActorVisualBase.resName`(0x48) 就是这个 trail 的特效资源名
    //   ⇒ 直接和名单比。副产品：再也不用先认 proxy 再回找 trail 了。
    private const int OFF_PROXY_TRAIL = 0x70;      // ActorTrailProxy.m_trail
    private const int OFF_TRAIL_VISUAL = 0x58;     // ActorTrail._visual
    private const int OFF_VISUAL_RESNAME = 0x48;   // ActorVisualBase.resName
    private const int OFF_TRAIL_MATIPP = 0x78;     // ActorTrail._materialIpp
    private const int OFF_TRAIL_TAG = 0x88;        // ActorTrail.m_tag
    /// <summary>ActorTrailProxy.MaterialInterpolators 的成员缓存（单独一个, 别和 MaterialTinterProxy 那个混用）。</summary>
    private static MemberInfo _trailProxyInterps;

    /// <summary>il2cpp 字符串: klass@0 monitor@8 length@0x10 chars@0x14 (UTF-16)。</summary>
    private static string RawStr(IntPtr s)
    {
        if (s == IntPtr.Zero) return null;
        try
        {
            int len = System.Runtime.InteropServices.Marshal.ReadInt32(s + 0x10);
            if (len <= 0 || len > 512) return null;
            return System.Runtime.InteropServices.Marshal.PtrToStringUni(s + 0x14, len);
        }
        catch { return null; }
    }

    /// <summary>这个 trail 属于哪个特效（`_visual.resName`）—— 认它就不需要留指针。</summary>
    private static string TrailEffectName(IntPtr trailPtr)
    {
        try
        {
            IntPtr vis = System.Runtime.InteropServices.Marshal.ReadIntPtr(trailPtr + OFF_TRAIL_VISUAL);
            if (vis == IntPtr.Zero) return null;
            return RawStr(System.Runtime.InteropServices.Marshal.ReadIntPtr(vis + OFF_VISUAL_RESNAME));
        }
        catch { return null; }
    }

    private static bool NameHits(string filter, string name)
    {
        if (string.IsNullOrWhiteSpace(filter) || string.IsNullOrEmpty(name)) return false;
        foreach (var k in Cfg.List(filter))
            if (k.Length > 0 && name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>
    /// proxy 路 —— **在对象还活着的时候当场改完**（这里是同步的, 没有任何跨帧指针）。
    /// 改的是 proxy 自己的 `MaterialInterpolators`：`TrailContext.Play` 建 `DualInterpolator` 时
    /// 会用到它, 所以必须在 Play 之前/当口改掉（挂在 Restart/Play/Awake/EnableVisualElements 上）。
    /// </summary>
    public static void ProxyPostfix(object __instance)
    {
        try
        {
            if (__instance == null || !RecolorConfig.On) return;
            if (RecolorConfig.TrailTint?.Value == false) return;

            string name = TintPolicy.EffectName(__instance);
            string filter = RecolorConfig.TrailNameFilter?.Value ?? "es_dodge";
            if (!NameHits(filter, name))
            {
                if (!string.IsNullOrEmpty(name))
                    LogEx.Once("trail|pxskip|" + Reflect.Normalize(name),
                        $"[特效换色:残影] 特效 \"{name}\" 不在残影名单 \"{filter}\" 里, 跳过");
                return;
            }

            _trailProxyInterps ??= Reflect.Member(typeof(NOAH.VFX.ActorTrailProxy), "MaterialInterpolators", Reflect.All);
            if (_trailProxyInterps == null)
            {
                LogEx.Once("trail|nopxinterp", "[特效换色:残影] ActorTrailProxy 上找不到 MaterialInterpolators 成员");
                return;
            }

            RewriteProxyInterps(__instance, name);

            // ★★ 就在这里把 trail 一起办掉 —— **同一个 tick 内**, 对象必然活着, 不留任何指针。
            //
            // 为什么放这儿（换过两次方案，都有理由）：
            //   · 旧方案 A：把命中的 proxy 指针**永久留着**, 在 `CreateTrail` 里回找
            //     ⇒ 池化对象释放后仍在读写 ⇒ 崩（见 PROJECT_STATE §12，有 dump 铁证）。
            //   · 方案 B：靠 `ActorTrail._visual.resName` 认 —— 实测它的值是 **"es"**（前缀），
            //     不是特效名 ⇒ 一条都认不出来（2026-10-04 实测）。
            //   现在：名字在 proxy 上是**确定有**的（`Role.Es.es_dodge_01`），
            //   而 `m_trail`(0x70) 就在手边 ⇒ 当场读、当场改、当场丢。
            // 只打印，不改（先确认它是不是权威值 —— 静态表是全局共享的，动它会波及所有用 _AddColor 的特效）
            if (_refProbed < 3) { _refProbed++; ProbeRefCounter("proxy命中:" + Reflect.Normalize(name)); }

            var mine = __instance as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
            IntPtr trailPtr = mine == null ? IntPtr.Zero
                : System.Runtime.InteropServices.Marshal.ReadIntPtr(mine.Pointer + OFF_PROXY_TRAIL);
            LogEx.Once("trail|mtrail|" + Reflect.Normalize(name),
                $"[特效换色:残影] \"{name}\" 的 m_trail = " +
                (trailPtr == IntPtr.Zero ? "还没建（等下一帧 CreateTrail）" : $"0x{trailPtr.ToInt64():X}"));
            if (trailPtr != IntPtr.Zero) ApplyTrailMat(trailPtr, name);

            // 记下"刚刚有一个命中名单的残影在播"。
            // TrailPostfix（CreateTrail，**每帧都调**）靠这个**时间窗**判断"这条 trail 大概是它的",
            // 从而能在残影**已经显形之后**再采样 —— 见 ProbeLiveMaterial 的说明。
            _lastHitName = name;
            _lastHitTime = Time.time;
            _lateSamples = 0;
        }
        catch (Exception e) { LogEx.Err("Recolor.ProxyPostfix", e); }
    }

    /// <summary>
    /// ★★ **Prefix** —— 在 proxy 的方法体跑之前就把插值器值改掉。
    ///
    /// 为什么必须加这一道（2026-10-04 实测）：
    ///   我们在 Postfix 里改了插值器的 endValue（日志确认 `原 end=(0,0.38,1,0) -> 现在 end=(1,0,0,0)`），
    ///   可 `ReferenceCounterMap` 里对应目标的值**还是 (0,0,0,0)**（打印出来的那几条 `_AddColor` counter=1）。
    ///   而这张表正是"多插值器合成后落给材质的最终值"（字段名 `finalInterpolationEndValue` 写脸上了）。
    ///   ⇒ 结论：**值是在方法体内部被读取/快照的**，Postfix 改**太晚**。
    ///   所以要在**进来之前**改 —— 同一个改写逻辑，挂 Prefix + Postfix 两道（Postfix 那道留着兜底）。
    ///
    /// ⚠ 只改插值器对象，一个材质都不写（写材质就是染角色本体，本项目踩过）。
    /// </summary>
    public static void ProxyPrefix(object __instance)
    {
        try
        {
            if (__instance == null || !RecolorConfig.On) return;
            if (RecolorConfig.TrailTint?.Value == false) return;
            string name = TintPolicy.EffectName(__instance);
            if (!NameHits(RecolorConfig.TrailNameFilter?.Value ?? "es_dodge", name)) return;
            RewriteProxyInterps(__instance, name);
        }
        catch (Exception e) { LogEx.Err("Recolor.ProxyPrefix", e); }
    }

    private static void RewriteProxyInterps(object proxy, string name)
    {
        try
        {
            _trailProxyInterps ??= Reflect.Member(typeof(NOAH.VFX.ActorTrailProxy), "MaterialInterpolators", Reflect.All);
            if (_trailProxyInterps == null)
            {
                LogEx.Once("trail|nopxinterp", "[特效换色:残影] ActorTrailProxy 上找不到 MaterialInterpolators 成员");
                return;
            }
            var arr = Reflect.Read(proxy, _trailProxyInterps);
            int n = TintBrush.RewriteArray(arr, RecolorConfig.Current, "ActorTrailProxy");
            LogEx.Once("trail|pxhit|" + Reflect.Normalize(name),
                $"[特效换色:残影] 当场改写 \"{name}\" 的 proxy 插值器 {n} 处");
        }
        catch (Exception e) { LogEx.Err("Recolor.RewriteProxyInterps", e); }
    }

    /// <summary>当场改写某个 trail 的 `_materialIpp` 并实测它渲染器上的材质（全部同一个 tick 内完成）。</summary>
    private static void ApplyTrailMat(IntPtr trailPtr, string name)
    {
        int n = TintBrush.TintRawInterpolatorArray(trailPtr + OFF_TRAIL_MATIPP,
                                                  RecolorConfig.Current, "ActorTrail._materialIpp");
        LogEx.Once("trail|done|" + Reflect.Normalize(name),
            $"[特效换色:残影] 命中 \"{name}\" 的 trail, 插值器改写 {n} 处");
        ProbeLiveMaterial(trailPtr, name);
        ProbeInterpolators(trailPtr, name);
    }

    /// <summary>
    /// 逐个插值器问它：**你驱动的是哪块材质？**
    ///
    /// `MaterialColorInterpolator.target` 是 public 属性（dump.cs: `public Material target { get; }`），
    /// 所以不用猜 —— 直接读出来就知道"我们改的那个对象"和"它在用的那个对象"是不是同一个。
    /// 这是把上一版"改了没反应"这个现象**变成可测量事实**的关键一步。
    ///
    /// ⚠ 每次都**在读的当下**读（`_materialIpp` 数组 + 元素都是活的），不跨帧。
    /// </summary>
    private static void ProbeInterpolators(IntPtr trailPtr, string name)
    {
        if (_probeLines > 80) return;
        IntPtr arr = IntPtr.Zero;
        int len = 0;
        try
        {
            arr = System.Runtime.InteropServices.Marshal.ReadIntPtr(trailPtr + OFF_TRAIL_MATIPP);
            if (arr != IntPtr.Zero) len = System.Runtime.InteropServices.Marshal.ReadInt32(arr + 0x18);
        }
        catch { return; }
        if (arr == IntPtr.Zero || len <= 0 || len > 32) return;

        _probeLines++;
        Plugin.Log?.LogInfo($"[特效换色:残影插值器] \"{name}\" _materialIpp=0x{arr.ToInt64():X} 共 {len} 条");
        for (int i = 0; i < len; i++)
        {
            try
            {
                IntPtr item = System.Runtime.InteropServices.Marshal.ReadIntPtr(arr + 0x20 + i * IntPtr.Size);
                if (item == IntPtr.Zero) continue;
                // 同 TintBrush.TintRawInterpolatorArray：**先验类型再碰字段**。
                // 上一版就是没验 —— `_materialIpp` 的第 2 条不是颜色插值器, 按错误类型读出来的
                // "Material 指针"是垃圾地址, 一解引用就崩（23:17 那一崩就是这儿）。
                var mc = new Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase(item)
                             .TryCast<NOAH.VFXInterpolator.MaterialColorInterpolator>();
                if (mc == null)
                {
                    Plugin.Log?.LogInfo($"[特效换色:残影插值器]   [{i}] \"{Reflect.KlassName(item)}\"" +
                                        $"（不是颜色插值器, 跳过 —— 硬解释成颜色插值器正是上次崩的原因）");
                    continue;
                }
                Material tgt = null;
                try { tgt = Reflect.Read(mc, _interpTargetMember) as Material; } catch { }
                Plugin.Log?.LogInfo($"[特效换色:残影插值器]   [{i}] prop=\"{mc.propName}\" " +
                                    $"start={Reflect.Fmt(mc.startValue)} end={Reflect.Fmt(mc.endValue)} " +
                                    $"target={(tgt == null ? "null" : "\"" + Reflect.Name(tgt) + "\"")}" +
                                    (tgt == null ? "" : $" 目标上 _AddColor={PropOf(tgt, "_AddColor")} _TintColor={PropOf(tgt, "_TintColor")}"));
            }
            catch (Exception e) { Plugin.Log?.LogInfo($"[特效换色:残影插值器]   [{i}] 读失败: {e.Message}"); }
        }
    }

    private static MemberInfo _interpTargetMember;

    /// <summary>
    /// trail 路 —— 只改 trail 自己的 `_materialIpp`(0x78)，**一个材质都不写**（写了会污染角色，踩过）。
    /// 识别靠 `_visual.resName`，所以**不再留任何指针**。
    /// </summary>
    public static void TrailPostfix(object __instance)
    {
        try
        {
            if (__instance == null || !RecolorConfig.On) return;
            if (RecolorConfig.TrailTint?.Value == false) return;

            var o = __instance as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
            if (o == null) return;
            IntPtr trailPtr = o.Pointer;

            // ⚠ 这里**只做诊断**，不写字 —— 改写已经在 proxy 那边当场做完了（见 ProxyPostfix）。
            //   所以本来不需要这个 postfix；留着是为了**找出一个能按名字认 trail 的判据**
            //   （`resName` 已证实是 "es" 前缀，没用；`m_tag` 是空串）。
            if (!RecolorConfig.Verbose) return;

            string eff = TrailEffectName(trailPtr);
            string tag = RawStr(System.Runtime.InteropServices.Marshal.ReadIntPtr(trailPtr + OFF_TRAIL_TAG));
            IntPtr ipp = System.Runtime.InteropServices.Marshal.ReadIntPtr(trailPtr + OFF_TRAIL_MATIPP);
            LogEx.Once("trail|id|" + eff + "|" + tag,
                $"[特效换色:残影] trail 0x{trailPtr.ToInt64():X} resName=\"{eff}\" tag=\"{tag}\" " +
                $"_materialIpp=0x{ipp.ToInt64():X}");
            DumpTrailHierarchy(trailPtr);

            // ★ 迟到采样：只有在"刚刚看到过命中名单的残影"的时间窗内才做（见 ApplyTrailMat 里的说明）。
            //   **纯读**，一个字都不写 —— 所以不存在写坏池化对象的风险。
            if (_lateSamples < LateSampleMax && Time.time - _lastHitTime < 3f && !string.IsNullOrEmpty(_lastHitName))
            {
                _lateSamples++;
                SampleTrailMaterials(trailPtr, _lastHitName + $"@迟{_lateSamples}");
            }
        }
        catch (Exception e) { LogEx.Err("Recolor.TrailPostfix", e); }
    }

    /// <summary>
    /// 把一个 trail 所在的 GameObject 层级 + 渲染器材质打出来（**只在创建它的那一 tick 内读**）。
    /// 目的：给"按名字认 trail"找一个可靠判据 —— 现在的两个候选（`resName` / `m_tag`）都不行。
    /// </summary>
    private static readonly HashSet<string> _hierDone = new HashSet<string>();
    private static int _probeLines;

    private static void DumpTrailHierarchy(IntPtr trailPtr)
    {
        if (_probeLines > 60) return;
        GameObject go = null;
        try { go = new NOAH.VFX.ActorTrail(trailPtr).gameObject; } catch { }
        if (go == null) { LogEx.Once("trail|nogo", "[特效换色:残影] ActorTrail 上拿不到 gameObject"); return; }

        string root = Reflect.Name(go);
        if (!_hierDone.Add(root)) return;

        var sb = new System.Text.StringBuilder();
        try
        {
            var tr = go.transform;
            for (int i = 0; i < 8 && tr != null; i++)
            {
                sb.Append(i == 0 ? "" : " <- ").Append('"').Append(Reflect.Name(tr.gameObject)).Append('"');
                tr = tr.parent;
            }
        }
        catch (Exception e) { sb.Append("(父链读失败:").Append(e.Message).Append(')'); }
        _probeLines++;
        Plugin.Log?.LogInfo($"[特效换色:残影层级] {sb}");
    }

    // ---------------------------------------------------------------- ②f 残影实测(同步探针)

    /// <summary>
    /// **同步**实测 —— 就在 trail 刚建好这一刻, 把这个 trail 渲染器上**实际在用的材质**
    /// 和它的 `_AddColor` 打出来。
    ///
    /// 为什么必须同步: 上一版是"隔 0.45s 再采", 那会儿 trail 很可能**已经被回收**,
    /// 拿一个已销毁的 GameObject 去 `GetComponentsInChildren` ⇒ 原生异常 ⇒
    /// 本项目栽过不止一次的"托管异常逃进原生代码"死法(不可 catch, 直接崩)。
    /// 现在对象是**刚刚创建**的, 当场读, 不可能踩到已释放对象。
    ///
    /// 它要回答的还是那两个问题:
    ///   打出来是**红 (1,0,0)** ⇒ 值进去了、材质也收到了, 可画面不变 ⇒ 电弧压根不看这个属性;
    ///   打出来还是**蓝 (0,0.38,1)** ⇒ 我们改的对象不是它在用的那个。
    /// </summary>
    /// <summary>
    /// 每个特效名允许采几次 —— **不能只采一次**。
    /// 我们上一次就是只采了"特效刚出生那一刻"，那时材质的 `_AddColor` 还没被写进去，
    /// 于是看到一排 (0,0,0,0)，差点得出"这个属性是死的"的错误结论。
    /// `Restart/Play/Awake/EnableVisualElements` 在特效一生里会被调多次 ⇒ 多采几次就能看到值落下去。
    /// </summary>
    private static readonly Dictionary<string, int> _liveProbed = new Dictionary<string, int>();
    /// <summary>出生那一刻最多采几次。**别调大** —— 这个窗口里材质还没被写，采多了只是刷屏。</summary>
    private const int ProbePerName = 3;
    /// <summary>迟到采样（残影显形期间）的次数上限 —— 这才是能看到"值有没有写进材质"的窗口。</summary>
    private const int LateSampleMax = 24;

    private static string _lastHitName;
    private static float _lastHitTime = -999f;
    private static int _lateSamples;

    private static void ProbeLiveMaterial(IntPtr trailPtr, string eff)
    {
        if (_probeLines > 60) return;
        eff ??= "(null)";
        _liveProbed.TryGetValue(eff, out int seen);
        if (seen >= ProbePerName) return;
        _liveProbed[eff] = seen + 1;
        SampleTrailMaterials(trailPtr, eff);
    }

    /// <summary>把一个 trail 渲染器上的材质读一遍（**纯读**，不写任何东西）。</summary>
    private static void SampleTrailMaterials(IntPtr trailPtr, string label)
    {
        if (_probeLines > 200) return;
        GameObject go = null;
        try { go = new NOAH.VFX.ActorTrail(trailPtr).gameObject; } catch { }
        if (go == null) { Plugin.Log?.LogInfo("[特效换色:残影实测] GameObject 拿不到"); return; }

        var ty = Il2CppInterop.Runtime.Il2CppType.From(typeof(Renderer));
        foreach (var c in Reflect.Items(go.GetComponentsInChildren(ty, true)))
        {
            var rt = Reflect.Cast<Renderer>(c);
            if (rt == null) continue;
            Material sm = null, im = null;
            string smn = "null", imn = "null";
            try { sm = rt.sharedMaterial; if (sm != null) smn = sm.name; } catch { }
            try { im = rt.material; if (im != null) imn = im.name; } catch { }
            _probeLines++;
            // ⚠ 只在**非零**时才值得看 —— 出生期那一堆 (0,0,0,0) 是噪音，
            //   迟到采样里如果还是全 0，那才是"这个属性压根没被写"的证据。
            string add = PropOf(sm, "_AddColor");
            Plugin.Log?.LogInfo($"[特效换色:残影实测] \"{label}\" 渲染器\"{Reflect.Name(rt.gameObject)}\" " +
                                $"shared=\"{smn}\" _AddColor={add} _TintColor={PropOf(sm, "_TintColor")}" +
                                $" | inst=\"{imn}\" _AddColor={PropOf(im, "_AddColor")}");
        }
    }

    private static string PropOf(Material m, string prop)
    {
        if (m == null) return "-";
        try
        {
            if (!m.HasProperty(prop)) return "(无此属性)";
            return Reflect.Fmt(m.GetColor(prop));
        }
        catch (Exception e) { return "(读失败:" + e.Message + ")"; }
    }

    // ---------------------------------------------------------------- ②g 引用计数表探针（只打印）

    /// <summary>
    /// ★★ 把 `MaterialColorInterpolator.ReferenceCounterMap`（**静态**字典）里 `_AddColor` 的条目打出来。
    ///
    /// 为什么：离线全量拆解证明——原色那层蓝就是 `ActorTrailProxy` 里 `_AddColor` 的 **(0,0.38,1)**，
    /// 皮肤的做法就是把它设成 (0,0,0,0)。而运行时我们把同一个插值器的 endValue 改掉了却**画面不变**
    /// （日志里明明白白 `原 end=(0,0.38,1,0) -> 现在 end=(1,0,0,0)`）。
    /// dump.cs 里 `MaterialColorReferenceCounter` 的字段名把答案写脸上了：
    ///   `referenceValue` / `finalInterpolationEndValue` / `counter`
    /// —— 多个插值器写同一个属性时，游戏用**引用计数**合成一个"最终值"，落盘到材质的很可能是它，
    /// 而不是我们改的那个 endValue。★ 这也正是「叠色算法」的本体。
    ///
    /// 这一步**只读不写**：先把键和值打出来，确认它是不是画面上的那个值，再决定动不动它。
    /// 访问方式：静态字段拿不到托管包装（`internal static`，Il2CppInterop 不生成）
    /// ⇒ `il2cpp_class_get_field_from_name` + `il2cpp_field_static_get_value` 取裸指针。
    /// </summary>
    /// <summary>
    /// ★ 对外入口: **随时抓一次** ReferenceCounterMap（热键 / 动作窗口都走这里）。
    /// 顺手把 `_refProbed` 的"只打 3 次"限流清零 —— 那是给自动路径防刷屏用的,
    /// 手动抓必须每次都能出（否则用户按了 F11 却什么都没有, 又是一次假阴性）。
    /// </summary>
    internal static void DumpRefCounters(string where)
    {
        _refProbed = 0;
        ProbeRefCounter(where);
    }

    private static void ProbeRefCounter(string where)
    {
        try
        {
            IntPtr klass = Il2CppInterop.Runtime.Il2CppClassPointerStore<
                NOAH.VFXInterpolator.MaterialColorInterpolator>.NativeClassPtr;
            if (klass == IntPtr.Zero) { Plugin.Log?.LogInfo("[叠色探针] 拿不到 MaterialColorInterpolator 的 klass"); return; }
            IntPtr field = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, "ReferenceCounterMap");
            if (field == IntPtr.Zero) { Plugin.Log?.LogInfo("[叠色探针] 找不到静态字段 ReferenceCounterMap"); return; }

            // ⚠ 这个 il2cpp 导出要的是 `void*`（不是 IntPtr）⇒ 必须 unsafe 块。
            //   csproj 里本来就开了 AllowUnsafeBlocks。
            IntPtr mapPtr;
            unsafe { Il2CppInterop.Runtime.IL2CPP.il2cpp_field_static_get_value(field, &mapPtr); }

            if (mapPtr == IntPtr.Zero)
            {
                Plugin.Log?.LogInfo($"[叠色探针|{where}] ReferenceCounterMap = null（还没被填过）");
                return;
            }

            // 键是 System.ValueTuple<int,string> ⇒ Il2CppInterop 侧叫 Il2CppSystem.ValueTuple
            var dict = new Il2CppSystem.Collections.Generic.Dictionary<
                Il2CppSystem.ValueTuple<int, string>,
                NOAH.VFXInterpolator.MaterialColorInterpolator.MaterialColorReferenceCounter>(mapPtr);

            // ⚠ 上一版直接把前 40 条打出来 —— 结果**全被 `_TintColor` 占满**，真正要看的 `_AddColor`
            //   根本没轮到。表里有 600+ 条，必须**按属性名筛**，先直方图再挑。
            var hist = new Dictionary<string, int>();
            var wanted = new List<string>();
            foreach (var kv in dict)
            {
                if (kv.Key == null || kv.Value == null) continue;
                string prop = "";
                try { prop = kv.Key.Item2 ?? ""; } catch { }
                hist.TryGetValue(prop, out int c);
                hist[prop] = c + 1;
                if (prop.IndexOf("_AddColor", StringComparison.OrdinalIgnoreCase) >= 0) wanted.Add(prop);
            }

            Plugin.Log?.LogInfo($"[叠色探针|{where}] ReferenceCounterMap 共 {dict.Count} 条；按属性分：");
            int hn = 0;
            foreach (var p in System.Linq.Enumerable.OrderByDescending(hist, x => x.Value))
            {
                if (++hn > 12) { Plugin.Log?.LogInfo("      ...（其余略）"); break; }
                // ⚠ 插值字符串**不支持负对齐** `{x,-24}`（CS1739），本项目栽过 —— 用 PadRight。
                Plugin.Log?.LogInfo("      " + (p.Key ?? "").PadRight(24) + " ×" + p.Value);
            }

            Plugin.Log?.LogInfo($"[叠色探针|{where}] === 关心属性的明细 ===");
            int n = 0;
            // ★ 属性名从配置来（原来写死 _AddColor 那一组 —— 要查别的东西就得改代码重编）。
            var care = Cfg.List(RecolorConfig.RefCounterProps?.Value);
            if (care.Length == 0) care = new[] { "_AddColor", "_HighlightColor", "_TintColor" };
            int cap = RecolorConfig.RefCounterMax?.Value ?? 60;
            foreach (var kv in dict)
            {
                if (kv.Key == null || kv.Value == null) continue;
                // ⚠ 键**只能**走包装的 `Item2`：直方图就是用它数出来的（`_AddColor` ×3 对得上），
                //   而我"按内存布局自己读"的版本筛出 0 条 ⇒ 布局猜错了。别再自己算偏移。
                string prop = "";
                int pid = 0;
                try { prop = kv.Key.Item2 ?? ""; pid = kv.Key.Item1; } catch { }
                bool hitCare = false;
                foreach (var c in care)
                    if (prop.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0) { hitCare = true; break; }
                if (!hitCare) continue;

                // referenceValue 是 public ✓；finalInterpolationEndValue / counter 是 internal ✗ ⇒ 按偏移读
                // (dump.cs: referenceValue 0x10 / finalInterpolationEndValue 0x20 / counter 0x30)
                Color refv = default, finv = default;
                int cnt = 0;
                try { refv = kv.Value.referenceValue; } catch { }
                try
                {
                    IntPtr vp = kv.Value.Pointer;
                    finv = ReadColorAt(vp + 0x20);
                    cnt = System.Runtime.InteropServices.Marshal.ReadInt32(vp + 0x30);
                }
                catch { }

                n++;
                if (n > cap) { Plugin.Log?.LogInfo($"      ...（其余略；上限 RefCounterMax={cap}）"); break; }
                Plugin.Log?.LogInfo($"      [{n}] key1={pid} prop=\"{prop}\" " +
                                    $"referenceValue={Reflect.Fmt(refv)} finalEnd={Reflect.Fmt(finv)} counter={cnt}");
            }
            if (n == 0) Plugin.Log?.LogInfo("      ⇒ 这几个发光/加法属性一条都没有");

            // 另一条线索：`_TintColor` 里**还在被引用**（counter != 0）的条目。
            // 表里有 600+ 条 `_TintColor`，绝大多数是历史残留（counter=0）；活的那几条才是当前在用的。
            int live = 0;
            foreach (var kv in dict)
            {
                if (kv.Key == null || kv.Value == null) continue;
                string prop = "";
                try { prop = kv.Key.Item2 ?? ""; } catch { }
                if (prop != "_TintColor") continue;
                int cnt;
                Color refv, finv;
                try
                {
                    IntPtr vp = kv.Value.Pointer;
                    refv = kv.Value.referenceValue;
                    finv = ReadColorAt(vp + 0x20);
                    cnt = System.Runtime.InteropServices.Marshal.ReadInt32(vp + 0x30);
                }
                catch { continue; }
                if (cnt == 0) continue;
                live++;
                if (live > cap) { Plugin.Log?.LogInfo($"      ...（活条目其余略；上限 RefCounterMax={cap}）"); break; }
                Plugin.Log?.LogInfo($"      [活{live}] _TintColor key1={SafeKey1(kv.Key)} ref={Reflect.Fmt(refv)} " +
                                    $"finalEnd={Reflect.Fmt(finv)} counter={cnt}");
            }
            Plugin.Log?.LogInfo($"      _TintColor 里 counter!=0 的（当前在用）: {live} 条");
        }
        catch (Exception e) { LogEx.Err("Recolor.ProbeRefCounter", e); }
    }

    private static string SafeKey1(Il2CppSystem.ValueTuple<int, string> k)
    {
        try { return k.Item1.ToString(); } catch { return "?"; }
    }

    private static Color ReadColorAt(IntPtr p)
    {
        float r = BitConverter.ToSingle(BitConverter.GetBytes(System.Runtime.InteropServices.Marshal.ReadInt32(p)), 0);
        float g = BitConverter.ToSingle(BitConverter.GetBytes(System.Runtime.InteropServices.Marshal.ReadInt32(p + 4)), 0);
        float b = BitConverter.ToSingle(BitConverter.GetBytes(System.Runtime.InteropServices.Marshal.ReadInt32(p + 8)), 0);
        float a = BitConverter.ToSingle(BitConverter.GetBytes(System.Runtime.InteropServices.Marshal.ReadInt32(p + 12)), 0);
        return new Color(r, g, b, a);
    }

    private static int _refProbed;

    // ---------------------------------------------------------------- ②h tinter 数组探针

    /// <summary>
    /// 把 hub 里每个 `MaterialTinter` / `MaterialTinterProxy` 的**插值器数组逐条**打出来：
    /// 长度 + 每个元素的**真实类名** + propName。
    ///
    /// 为什么需要：`es_dash_01` 的官方换色配方（`_RemapColorFrom`）**离线确实在 prefab 里**
    /// （路径实测 = `es_dash_01/Other`），但线上日志是
    /// `hub 激活 "es_dash_01" (3 处) … Tinter属性=&lt;非颜色&gt;` + `改写 0 条插值器`
    /// —— 也就是**运行时那个数组里没有颜色插值器**。
    /// 到底是"数组空了"、"元素被换成了别的类型"、还是"内容被搬到了 m_tinter 那边"，
    /// 只有把真实类名打出来才能定，别再猜（`<非颜色&gt;` 这个标签只说明 TryCast 失败过）。
    /// </summary>
    private static readonly HashSet<string> _arrDumped = new HashSet<string>();

    private static void DumpTinterArrays(GameObject go, string effName)
    {
        try
        {
            if (!RecolorConfig.Verbose) return;
            if (_arrDumped.Count > 40) return;
            // 关心的目标：官方换色点 + 【消耗 MP 的冲刺】那两只（特效和弹幕）
            //   实测动作→特效：dashSkill → es_attackAir_02；同动作还发射 LogicRes="esbullet" 的弹幕。
            //   离线证据：`es_esbullet_dasha_001` 的 MaterialTinter 里**原色就带一条蓝色 `_TintColor` 插值器**
            //   （(0,0.327,2.0)→(0.273,0.714,1.864)），皮肤把它改成别的色（esskin_10 = (1.536,2.706,1.020) 绿）。
            //   但线上它是 `Tinter属性=<无插值器>` ⇒ 必须看清运行时那个数组里到底有什么。
            if (!effName.Contains("dash") && !effName.Contains("holdfull") && !effName.Contains("attackhlod_hit")
                && !effName.Contains("attackAir") && !effName.Contains("bullet"))
                return;
            if (!_arrDumped.Add(effName)) return;

            Dump("MaterialTinter", go, typeof(NOAH.VFX.MaterialTinter), "MaterialInterpolators");
            Dump("MaterialTinterProxy", go, typeof(NOAH.VFX.MaterialTinterProxy), "MaterialInterpolators");
        }
        catch (Exception e) { LogEx.Err("Recolor.DumpTinterArrays", e); }
    }

    private static void Dump(string label, GameObject go, Type t, string memberName)
    {
        var ty = Il2CppInterop.Runtime.Il2CppType.From(t);
        foreach (var c in Reflect.Items(go.GetComponentsInChildren(ty, true)))
        {
            var comp = Reflect.Cast<Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase>(c);
            if (comp == null) continue;
            var mi = Reflect.Member(t, memberName, Reflect.All);
            if (mi == null) { Plugin.Log?.LogInfo($"[换色数组探针] {label}.{memberName} 解析不到"); return; }
            var arr = Reflect.Read(comp, mi);
            if (arr == null) { Plugin.Log?.LogInfo($"[换色数组探针] {label} 的数组 = null"); return; }
            IntPtr ap = IntPtr.Zero;
            try { ap = ((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)arr).Pointer; } catch { }
            int len = -1;
            try { len = System.Runtime.InteropServices.Marshal.ReadInt32(ap + 0x18); } catch { }
            Plugin.Log?.LogInfo($"[换色数组探针] {label} 数组=0x{ap.ToInt64():X} 长度={len}");
            for (int i = 0; i < len && i < 8; i++)
            {
                IntPtr item;
                try { item = System.Runtime.InteropServices.Marshal.ReadIntPtr(ap + 0x20 + i * IntPtr.Size); }
                catch { break; }
                if (item == IntPtr.Zero) { Plugin.Log?.LogInfo($"      [{i}] null"); continue; }
                string cls = Reflect.KlassName(item);
                string prop = "-";
                try { prop = RawStr(System.Runtime.InteropServices.Marshal.ReadIntPtr(item + 0x78)) ?? "-"; } catch { }
                Plugin.Log?.LogInfo($"      [{i}] 真实类名=\"{cls}\"  @0x78 propName=\"{prop}\"");
            }
            return;                                       // 每类只看第一个
        }
    }

    /// <summary>当前动作名（拿不到就返回 "-"）。取法同 DashInvincible.SafeActionName。</summary>
    private static string SafeActionName(ActorBase a)
    {
        try { return a?.ActionMgr?.CurrentActionName ?? "-"; } catch { return "-"; }
    }

    // ---------------------------------------------------------------- ②i 副贴图着色路

    /// <summary>
    /// 点名特效的副贴图着色（照官方配方直写材质）。**只对 `SubTexEffects` 里的名字生效**。
    /// 名字比法：取最后一段（`Role.Es.es_attackAir_02` → `es_attackAir_02`）、去掉 `(Clone)`、
    /// **精确**比对（不用子串 —— 否则 `es_attackAir_02a` 会被 `es_attackAir_02` 误伤）。
    /// </summary>
    private static void MaybeTintSubTex(GameObject go, string rawName)
    {
        try
        {
            if (go == null || RecolorConfig.SubTex?.Value != true) return;
            string leaf = LeafName(rawName);
            if (leaf.Length == 0) return;
            if (TintPolicy.Excluded(rawName)) return;

            bool hit = false;
            foreach (var k in Cfg.List(RecolorConfig.SubTexEffects?.Value))
                if (k.Length > 0 && string.Equals(k, leaf, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
            if (!hit) return;

            int n = 0;
            foreach (var p in Cfg.List(RecolorConfig.SubTexProps?.Value))
                if (!string.IsNullOrWhiteSpace(p)) n += TintBrush.TintSubTexMaterials(go, RecolorConfig.Current, p.Trim());
            LogEx.Once("subtex|eff|" + leaf, $"[特效换色:副贴图] 点名特效 \"{leaf}\" 共写 {n} 处");
        }
        catch (Exception e) { LogEx.Err("Recolor.MaybeTintSubTex", e); }
    }

    /// <summary>
    /// ★★ 按【子物体名】下刀 —— 找子树里名字命中 `TintObjects` 的对象，改写**它自己的**着色插值器。
    ///
    /// 与 <see cref="MaybeTintSubTex"/> 的分工（两者是**不同的载体**，别混）：
    ///   · MaybeTintSubTex —— 写**材质属性**（`_SubTexTintColor` 之类），作用在特效整棵子树上；
    ///   · 本方法        —— 写**组件插值器**（`MaterialTinter.MaterialInterpolators`），只作用在命中对象上。
    ///   而 `guangzhu01` 那道电弧属于后者（官方皮肤动的是它的 `_TintColor` 插值器）。
    ///
    /// ⚠ 为什么只在**事件点**调用、不做每帧调用：这里要遍历子树找名字，
    ///   每帧做一遍就是白烧 CPU（本项目在 `AccessTools.TypeByName` / `Cfg.List` 上栽过同类坑）。
    ///   hub 激活 / 弹幕出生都是事件，代价可忽略。
    /// </summary>
    private static void MaybeTintObjects(GameObject root, string hostRawName)
    {
        try
        {
            if (root == null || RecolorConfig.TintObjects == null) return;
            var objNames = Cfg.List(RecolorConfig.TintObjects?.Value);
            if (objNames.Length == 0) return;

            // 宿主过滤：只在点名的特效子树里找（空 = 任何子树，慎用）
            var hosts = Cfg.List(RecolorConfig.TintObjectHosts?.Value);
            if (hosts.Length > 0)
            {
                string leafHost = LeafName(hostRawName);
                bool ok = false;
                foreach (var h in hosts)
                    if (h.Length > 0 && string.Equals(h, leafHost, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
                if (!ok) return;
            }

            int hits = 0;
            // 先把 root 自己算进去（有些特效的根就是那个对象）
            hits += TintOneObject(root, objNames, hostRawName);

            var tt = Il2CppInterop.Runtime.Il2CppType.From(typeof(Transform));
            foreach (var o in Reflect.Items(root.GetComponentsInChildren(tt, true)))
            {
                var t = Reflect.Cast<Transform>(o);
                if (t == null) continue;
                GameObject g = null;
                try { g = t.gameObject; } catch { continue; }
                if (g == null || g.Pointer == root.Pointer) continue;
                hits += TintOneObject(g, objNames, hostRawName);
            }

            if (hits > 0 && RecolorConfig.TintObjectLog?.Value != false)
                Plugin.Log?.LogInfo($"[特效换色:子物体] \"{hostRawName}\" 子树命中 {hits} 处");
        }
        catch (Exception e) { LogEx.Err("Recolor.MaybeTintObjects", e); }
    }

    /// <summary>单个对象：名字命中就改写它自己的插值器（+ 保住材质属性那条路）。</summary>
    private static int TintOneObject(GameObject go, string[] objNames, string hostRawName)
    {
        try
        {
            string leaf = LeafName(Reflect.Name(go));
            if (leaf.Length == 0) return 0;
            bool hit = false;
            foreach (var k in objNames)
                if (k.Length > 0 && string.Equals(k, leaf, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
            if (!hit) return 0;
            if (TintPolicy.Excluded(leaf)) return 0;

            // ★ 用 ObjectTarget 而不是 Current：点名名单里的都是**中性亮度乘数**(如 ring02 的 4.237)，
            //   必须用 `peak` 才能着色；全局口径一旦设成 `hue`(官方式)，`hue` 对 S=0 是**不动**的
            //   ⇒ 用 Current 会把这条路的成果整个撤销。
            var ot = RecolorConfig.ObjectTarget;
            int k2 = TintBrush.TintTintersOn(go, ot);
            // 材质属性那条也走一遍：官方对这类对象有时走材质、有时走插值器，两条都覆盖才不会"改了一半"
            foreach (var p in Cfg.List(RecolorConfig.SubTexProps?.Value))
                if (!string.IsNullOrWhiteSpace(p)) k2 += TintBrush.TintSubTexMaterials(go, ot, p.Trim());

            LogEx.Once("objtint|" + hostRawName + "|" + leaf,
                       $"[特效换色:子物体] 命中对象 \"{leaf}\"（宿主 {hostRawName}）共写 {k2} 处");
            return k2;
        }
        catch (Exception e) { LogEx.Err("Recolor.TintOneObject", e); return 0; }
    }

    /// <summary>`Role.Es.es_attackAir_02` / `es_attackAir_02(Clone)` → `es_attackAir_02`</summary>
    private static string LeafName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        string s = raw;
        int par = s.IndexOf('(');
        if (par >= 0) s = s.Substring(0, par);
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < s.Length) s = s.Substring(dot + 1);
        return s.Trim();
    }

    private static int _bulletLogged;
    /// <summary>上一个新建的子弹对象 —— 探针要"延迟一拍"才读得到已就绪的材质。</summary>
    private static GameObject _lastBullet;

    /// <summary>
    /// 子弹的可视化 —— 剑气拖影的出生口。
    ///
    /// 证据（2026-10-04）: `es_esbullet_dasha_*` 那类 prefab **从来没走过 `AssetBundleProvider.LoadAsset`**
    /// （48 条 asset 行里一条都没有），也不触发 hub 的任何一条出生口 ⇒ 它的 prefab 引用是烘在
    /// `BulletConfig` 里的，子弹系统直接实例化。所以挂子弹的唯一漏斗 `BulletMgr.createBulletImp`。
    /// </summary>
    public static void BulletPostfix(object __result)
    {
        try
        {
            if (__result == null || !RecolorConfig.On) return;
            var b = Reflect.Cast<GamePlay.BulletObj>(__result);
            var go = b?.gameObject;                       // ActorBase.gameObject 是公开属性
            if (go == null) return;

            IntPtr key;
            try { key = go.Pointer; } catch { return; }
            if (key == IntPtr.Zero || !CensusShouldTint(key)) return;   // 同一套去重+重试

            string goName = Reflect.Name(go);
            if (!TintPolicy.AllowBattleEffect(null, goName))          // 同一套名字判据
            {
                CensusDone(key);
                return;
            }
            int k = TintBrush.TintGameObject(go, RecolorConfig.Current);
            if (k > 0) CensusDone(key);
            // 剑气拖尾: tinter 是空的、粒子写了 11~13 处、画面就是不变 ⇒ 颜色在**材质**上。
            //
            // ⚠ 探针必须【延迟一拍】: 上一版在子弹刚创建那一瞬间就探, 读到的是
            //   `mainTexture="Default-Checker"`(= Unity 的"贴图缺失"占位图) + 一个
            //   Hidden/InternalErrorShader —— 那是**还没初始化**的材质, 不是真相。
            //   做法: 这次创建时, 回头探**上一个**同类型对象(它已经活了一会儿, 材质是就绪的)。
            if (goName.IndexOf("bullet", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (_lastBullet != null && _lastBullet.Pointer != go.Pointer)
                    TintBrush.ProbeMaterials(_lastBullet, RecolorConfig.Current);
                _lastBullet = go;
            }
            if (_bulletLogged++ < 40)
                Plugin.Log?.LogInfo($"[特效换色:子弹] 新建子弹 \"{goName}\" 改写 {k} 处  " +
                                    $"[载体 {TintBrush.CarrierReport(go)}]");

            // ★★ 弹幕的**整棵子树**也要走一遍副贴图着色。
            //
            // 依据（2026-10-05 实测）：`Refrac` / `guangzhu01` / `glass` 这些渲染器**挂在弹幕自己的层级下**
            // （`esbullet → ActorAnim → Motor → Renderer → …`），而它们身上**没有 VFXEffectHub**
            // ⇒ 只在"hub 激活"时走特效自己子树的那条路**永远够不到它们**。
            // 而 `Refrac` 用的还是 `NOAH/Effect/Variant/Refract`（折射环）—— 那道"向外扩散的圆弧"的强候选。
            MaybeTintSubTex(go, goName);
            MaybeTintObjects(go, goName);
        }
        catch (Exception e) { LogEx.Err("Recolor.BulletPostfix", e); }
    }

    // ---------------------------------------------------------------- ④ JIT prefab

    private static int HookPrefabAssets(Harmony harmony)
    {
        var abp = AccessTools.TypeByName("NOAH.Asset.AssetBundleProvider")
               ?? AccessTools.TypeByName("AssetBundleProvider");
        if (abp == null) { Plugin.Log?.LogWarning("  [换色] 找不到 AssetBundleProvider"); return 0; }

        // ★ 钩【所有】加载入口, 不是一个重载:
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
        return n;
    }

    /// <summary>
    /// ★★ 资产重定向（"推倒重来"的核心，2026-10-04）:
    /// 把【原色特效】的加载请求改成【皮肤特效】的那一份。
    ///
    /// 依据（离线 diff，`effect/prefab/role/es/`）:
    ///   · 原色 `es_*` 共 105 个；`esskin_12` / `esskin_13` 各有 **105 个同名副本**（完整覆盖）
    ///   · 任何皮肤都**没有**原色之外的特效（没有独占项）⇒ 换过去不丢东西
    ///   · 而皮肤那套**我们本来就能染**（日志: `Role.Es.EsSkin_10.es_attack1_02 染上了 (4 处)
    ///     Tinter属性=_SubTexTintColor`），原色那套则有一部分染不动（颜色烘在共享材质上）
    /// ⇒ 与其去啃原色特效的载体, 不如**让它们变成那套能染的资产** —— 一次覆盖全部特效,
    ///   不用再逐个解析(这正是用户提的"用换色的专属特效替换掉原特效")。
    ///
    /// ⚠ 代价说清楚: 换的是**皮肤那套资产**(造型同名、贴图是那个皮肤自己的), 所以要挑一个
    ///   观感可接受的皮肤集；不合格就改 ForceSkinSetName 换另一套(06/10/12/13)。
    /// </summary>
    public static void LoadAssetPrefix(object[] __args)
    {
        try
        {
            if (!RecolorConfig.On || RecolorConfig.ForceSkinSet?.Value != true) return;
            if (__args == null || __args.Length < 2) return;

            string skin = (RecolorConfig.ForceSkinSetName?.Value ?? "").Trim();
            if (skin.Length == 0) return;

            for (int i = 1; i <= 2 && i < __args.Length; i++)     // 1 = refPath, 2 = assetName
            {
                var s = __args[i] as string;
                if (string.IsNullOrEmpty(s)) continue;
                if (s.IndexOf("Role/Es/", StringComparison.OrdinalIgnoreCase) < 0 &&
                    s.IndexOf("Role.Es.", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (s.IndexOf("esskin_", StringComparison.OrdinalIgnoreCase) >= 0) continue;   // 已是皮肤套
                // 排除名单同样管住重定向 —— 挂在角色身上的待机常驻特效(es_stand_01 那类)不能换,
                // 换了角色的样子就变了(实测: 私有副本染 es_stand_01 → 角色变成一个红球)
                bool skipped = false;
                foreach (var k in Cfg.List(RecolorConfig.AssetExclude?.Value))
                    if (k.Length > 0 && s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { skipped = true; break; }
                if (skipped) continue;

                string sep = s.IndexOf("Role/Es/", StringComparison.OrdinalIgnoreCase) >= 0 ? "/" : ".";
                string key = s.IndexOf("Role/Es/", StringComparison.OrdinalIgnoreCase) >= 0 ? "Role/Es/" : "Role.Es.";
                int at = s.IndexOf(key, StringComparison.OrdinalIgnoreCase) + key.Length;
                string rewritten = s.Substring(0, at) + skin + sep + s.Substring(at);
                __args[i] = rewritten;
                LogEx.Once("recolor|redirect|" + Reflect.Normalize(rewritten),
                           $"[特效换色] 资产重定向 -> \"{rewritten}\"  (原请求 {s})");
            }
        }
        catch (Exception e) { LogEx.Err("Recolor.LoadAssetPrefix", e); }
    }

    /// <summary>
    /// 直接改内存里的 prefab。加载一次就缓存, 之后所有实例自动继承 ——
    /// 不用管实例化时机, 也不碰实例。
    /// __args: [0]=Type [1]=refPath [2]=assetName [3]=retainer [4]=AssetLogType
    /// </summary>
    public static void LoadAssetPostfix(object[] __args, ref object __result)
    {
        try
        {
            if (__result == null || !RecolorConfig.On) return;

            // ★★ 材质路（2026-10-04 补，这是"按游戏的方式重做"的第一步）
            //   原来这里 `go == null 就 return`，**材质资产根本没进过门** ——
            //   而特效的颜色大量烘在共享材质上(它们在 Effect/Common、Effect/Material 这些共享 bundle 里,
            //   实例侧的 tinter 全是空的, 所以"tinter 改了 3~8 处、画面就是不变", 剑气拖尾就是这一类)。
            //   做法: 加载时就把白名单属性按【原值算出来的目标色】写进材质 —— 和插值器每帧写的是同一个值,
            //   所以不会互相打架; 而凡是没插值器的材质(烘死的), 就靠这一步。
            var mat = RecolorConfig.TintSharedMaterials?.Value == true ? Reflect.Cast<Material>(__result) : null;
            if (mat != null)
            {
                string matName = Reflect.Name(mat) ?? "";
                string matPath = __args != null && __args.Length > 1 ? __args[1] as string ?? "" : "";
                if (matPath.IndexOf("Effect", StringComparison.OrdinalIgnoreCase) < 0 &&
                    matName.IndexOf("Effect", StringComparison.OrdinalIgnoreCase) < 0)
                    return;                               // 只碰特效材质 —— 别把 UI/场景材质一起染了
                int km = TintBrush.TintMaterial(mat, RecolorConfig.Current);
                if (km > 0)
                    LogEx.Once("recolor|mat|" + Reflect.Normalize(matName) + "|" + Reflect.Normalize(matPath),
                               $"[特效换色] 已改材质 \"{matName}\" ({km} 条属性) ref=\"{matPath}\"");
                return;
            }

            var go = Reflect.Cast<GameObject>(__result);
            if (go == null) return;                       // 其它类型不管

            string refPath = __args != null && __args.Length > 1 ? __args[1] as string ?? "" : "";
            string assetName = __args != null && __args.Length > 2 ? __args[2] as string ?? "" : "";
            string goName = Reflect.Name(go);

            // ★ 逗号分隔多关键字(任一命中即可)。默认 = ES 角色特效 + 受击特效
            //   (受击 prefab 在 Effect/Prefab/Hit/<皮肤>/ 下, 原来 "Role/Es" 一条都盖不到)。
            var filter = RecolorConfig.AssetFilter?.Value ?? "Role/Es,Prefab/Hit";
            bool match = string.IsNullOrWhiteSpace(filter);
            if (!match)
                foreach (var k in Cfg.List(filter))
                {
                    if (k.Length == 0) continue;
                    if (refPath.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        assetName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        goName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { match = true; break; }
                }

            // 排除名单 —— 逗号分隔。用来跳过"待机光效"这类挂在角色身上的常驻特效
            // (实测 es_stand_01 会把角色变成一个发光球)。
            if (match)
            {
                foreach (var k in Cfg.List(RecolorConfig.AssetExclude?.Value))
                    if (goName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        refPath.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        match = false;
                        LogEx.Once("recolor|assetexcl|" + k, $"[特效换色] 按排除名单跳过 \"{goName}\" (含 \"{k}\")");
                        break;
                    }
            }

            if (RecolorConfig.Verbose && IsEffectLike(goName, refPath))
                LogEx.Once("recolor|asset|" + Reflect.Normalize(goName),
                           $"[特效换色:asset] LoadAsset go=\"{goName}\" ref=\"{refPath}\" 命中={match}");

            if (!match) return;

            var tgt = RecolorConfig.Current;

            // ★ 私有副本(用户要求: 不能污染游戏自己的换色资产)
            //   复制一套 -> 只染副本 -> 把副本当资产返回。原资产(含共享材质)一个字节都不动。
            if (RecolorConfig.PrivateCopy?.Value != false)
            {
                string ckey = (refPath ?? "") + "|" + (assetName ?? "") + "|" + goName;
                if (_cloneCache.TryGetValue(ckey, out var cached) && cached != null)
                {
                    __result = cached;
                    return;
                }
                var clone = TintBrush.ClonePrivate(go);
                if (clone != null)
                {
                    int kc = TintBrush.TintGameObject(clone, tgt);
                    kc += TintBrush.TintMaterialsOnClone(clone, tgt);
                    if (_cloneCache.Count < 400) _cloneCache[ckey] = clone;
                    __result = clone;
                    if (kc > 0)
                        LogEx.Once("recolor|clone|" + Reflect.Normalize(goName),
                                   $"[特效换色] 私有副本 \"{goName}\" 已染色 ({kc} 处) -> {Cfg.ToHex(tgt.Color)}  [ref={refPath}]");
                    return;
                }
                // 复制失败 -> 落到老行为(直接染原资产), 但日志里已留痕
            }

            int ok = TintBrush.TintGameObject(go, RecolorConfig.Current);
            if (ok > 0)
                LogEx.Once("recolor|assetdone|" + Reflect.Normalize(goName),
                           $"[特效换色] 已改 Prefab \"{goName}\" ({ok} 处) -> {Cfg.ToHex(RecolorConfig.Current.Color)}");
        }
        catch (Exception e) { LogEx.Err("Recolor.LoadAssetPostfix", e); }
    }

    private static bool IsEffectLike(string goName, string refPath)
        => goName.StartsWith("es_", StringComparison.OrdinalIgnoreCase)
        || refPath.IndexOf("Role/Es", StringComparison.OrdinalIgnoreCase) >= 0
        || refPath.IndexOf("Role.Es", StringComparison.OrdinalIgnoreCase) >= 0
        || refPath.IndexOf("effect", StringComparison.OrdinalIgnoreCase) >= 0;

    // ---------------------------------------------------------------- ⑤ 角色本体(默认关)

    private static Type _mcType;
    private static MemberInfo _emission, _ex, _ey, _ez, _ea, _mcSkin;

    private static int HookCharacter(Harmony harmony)
    {
        var ov = Resolve("GamePlay.ActorMaterialOverride", "ACERender.ActorMaterialOverride", "ActorMaterialOverride");
        if (ov == null) { Plugin.Log?.LogWarning("  [换色] 找不到 ActorMaterialOverride, 角色本体染色不可用"); return 0; }

        _mcType = ov.GetNestedType("MaterialColors", BindingFlags.Public | BindingFlags.NonPublic);
        if (_mcType == null)
        {
            Plugin.Log?.LogWarning("  [换色] 找不到 ActorMaterialOverride.MaterialColors, 角色本体路不可用");
            return 0;
        }
        _mcSkin = Reflect.Member(_mcType, "Skin", Reflect.All);
        _emission = Reflect.Member(_mcType, "Emission", Reflect.All);
        _ex = Reflect.Member(_mcType, "EmissionX", Reflect.All);
        _ey = Reflect.Member(_mcType, "EmissionY", Reflect.All);
        _ez = Reflect.Member(_mcType, "EmissionZ", Reflect.All);
        _ea = Reflect.Member(_mcType, "EmissionA", Reflect.All);

        int n = 0;
        var postStr = new HarmonyMethod(Mi(nameof(TryPostfixStr)));
        var postBool = new HarmonyMethod(Mi(nameof(TryPostfixBool)));
        foreach (var m in ov.GetMethods(Reflect.All))
        {
            if (m.Name != "TryGetOverride") continue;
            var ps = m.GetParameters();
            if (ps.Length < 2) continue;
            var last = ps[ps.Length - 1].ParameterType;
            var elem = last.IsByRef ? last.GetElementType() : last;
            if (elem == null || (elem != _mcType && elem.Name != "MaterialColors")) continue;

            // ⚠ 两个重载的**最后一个**参数都是 `out MaterialColors`, 靠它分不出是谁:
            //     TryGetOverride(int id, string skin, out MaterialColors val)
            //     TryGetOverride(int id, bool   isSpine, out MaterialColors value)
            //   区别在【第二个】参数。上一版我拿最后一个参数去比 bool&, 恒 false,
            //   于是 bool 那个重载被套上了 string 版的 Postfix —— 形参名对不上, Harmony
            //   直接 IL Compile Error, 那条路从来没挂上过。
            //   (第 3 个重载 TryGetOverride(int, out AvatarSkinColors.General) 已被上面的
            //    MaterialColors 过滤挡掉, 不会误挂。)
            var which = ps[1].ParameterType == typeof(bool) ? postBool : postStr;
            if (Patch(harmony, m, null, which)) n++;
        }

        // ⚠ Apply 在【嵌套类 MaterialColors】上, 不在 ActorMaterialOverride 上:
        //     ActorMaterialOverride.MaterialColors.Apply(Material mat, object textureRetainer)
        //   挂到外层类型是空转(那里根本没有 Apply), 而且不报错 —— 上一版就是这么写的。
        var apply = FindMethod(_mcType, "Apply", Reflect.All);
        if (apply != null && apply.DeclaringType == _mcType &&
            Patch(harmony, apply, null, new HarmonyMethod(Mi(nameof(ApplyPostfix))))) n++;
        else Plugin.Log?.LogWarning("  [换色] MaterialColors.Apply 没挂上 (角色本体路只剩 TryGetOverride)");
        return n;
    }

    // ⚠⚠ Harmony 是**按形参名**绑定注入参数的, 名字必须跟目标方法逐字一致。
    //   两个重载的 out 参数名【不一样】(dump.cs 原文):
    //     TryGetOverride(int id, string skin,    out MaterialColors val)
    //     TryGetOverride(int id, bool   isSpine, out MaterialColors value)
    //   写成同一个名字, 其中一个必然 IL Compile Error 且【静默不生效】(只留一条警告)。
    public static void TryPostfixStr(int id, string skin, ref ActorMaterialOverride.MaterialColors val, ref bool __result)
        => HandleCharacter(id, skin ?? "", val, __result);

    public static void TryPostfixBool(int id, bool isSpine, ref ActorMaterialOverride.MaterialColors value, ref bool __result)
        => HandleCharacter(id, isSpine ? "<spine:true>" : "<spine:false>", value, __result);

    private static void HandleCharacter(int id, string skin, ActorMaterialOverride.MaterialColors mc, bool ok)
    {
        try
        {
            if (!ok || mc == null) return;
            if (RecolorConfig.Verbose)
                LogEx.Once($"recolor|try|{id}|{skin}", $"[特效换色:trace] TryGetOverride id={id} skin={skin} -> {ok}");
            if (RecolorConfig.RecolorCharacter?.Value != true) return;   // 角色本体染色默认关
            if (id != (RecolorConfig.ActorId?.Value ?? 103401)) return;

            var filter = RecolorConfig.SkinFilter?.Value;
            if (!string.IsNullOrWhiteSpace(filter) &&
                skin.IndexOf(filter.Trim(), StringComparison.OrdinalIgnoreCase) < 0) return;

            RecolorCharacter(mc, $"id={id} skin=\"{skin}\"");
        }
        catch (Exception e) { LogEx.Err("Recolor.HandleCharacter", e); }
    }

    public static void ApplyPostfix(object __instance, object[] __args)
    {
        try
        {
            if (__instance == null) return;
            if (RecolorConfig.RecolorCharacter?.Value != true) return;   // 默认关
            // Apply 拿不到 actorId, 只能靠 skin 名过滤
            var skin = Reflect.Read(__instance, _mcSkin) as string ?? "";
            var filter = RecolorConfig.SkinFilter?.Value;
            if (!string.IsNullOrWhiteSpace(filter) &&
                skin.IndexOf(filter.Trim(), StringComparison.OrdinalIgnoreCase) < 0) return;
            RecolorCharacter(__instance, $"skin=\"{skin}\" (Apply)");
        }
        catch (Exception e) { LogEx.Err("Recolor.ApplyPostfix", e); }
    }

    private static void RecolorCharacter(object mc, string what)
    {
        var c = RecolorConfig.Current.Color;
        Reflect.Write(mc, _emission, c);
        Reflect.Write(mc, _ex, c);
        Reflect.Write(mc, _ey, c);
        Reflect.Write(mc, _ez, c);
        Reflect.Write(mc, _ea, c);
        LogEx.Once("recolor|char|" + what, $"[特效换色] 已改角色本体 {what} -> {Cfg.ToHex(c)}");
    }

    // ---------------------------------------------------------------- 工具

    private static Type Resolve(params string[] names)
    {
        foreach (var n in names)
        {
            var t = AccessTools.TypeByName(n);
            if (t != null) return t;
        }
        return null;
    }

    internal static MethodInfo Mi(string name) =>
        typeof(RecolorPipeline).GetMethod(name, BindingFlags.Static | BindingFlags.Public);

    private static MethodInfo FindMethod(Type t, string name, BindingFlags f)
    {
        MethodInfo best = null;
        foreach (var m in t.GetMethods(f))
            if (m.Name == name && (best == null || m.GetParameters().Length > best.GetParameters().Length))
                best = m;
        return best;   // 重载多时取参数最多的 —— 免得 AccessTools.Method 抛歧义异常
    }

    private static int PatchRegex(Harmony harmony, string typeName, string method, string prefixName)
    {
        var t = AccessTools.TypeByName(typeName);
        if (t == null) { Plugin.Log?.LogWarning($"  [换色] 找不到 {typeName}"); return 0; }
        int n = 0;
        var pre = new HarmonyMethod(Mi(prefixName));
        foreach (var m in t.GetMethods(Reflect.All))
        {
            if (m.Name != method || m.DeclaringType != t) continue;
            if (Patch(harmony, m, pre, null)) n++;
        }
        if (n == 0) Plugin.Log?.LogWarning($"  [换色] {typeName}.{method} 没挂上");
        return n;
    }

    private static bool Patch(Harmony h, MethodBase m, HarmonyMethod pre = null, HarmonyMethod post = null)
    {
        try
        {
            h.Patch(m, prefix: pre, postfix: post);
            Plugin.Log?.LogInfo($"  [换色] 已挂钩 {m.DeclaringType?.Name}.{m.Name}" +
                                $"({string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name))})");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log?.LogWarning($"  [换色] 挂钩 {m.Name} 失败: {LogEx.Unwrap(e)}");
            return false;
        }
    }
}
