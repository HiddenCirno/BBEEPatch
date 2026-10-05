using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Unity.Mathematics.FixedPoint;

namespace BlazblueJsPatch;

/// <summary>
/// 动作变速：把指定动作（第一个用例 = 平A）整体加速。
///
/// 已经排除掉的两个嫌疑
/// ──────────────────
/// 反汇编 GameAssembly.dll 确认了 `DeltaTimeAndScale` 的读写没毛病：
///     get_RealDeltaTime  →  mov rax, [rcx]        // 0x0
///     get_DeltaTime      →  mov rax, [rcx+0x10]   // 0x10，就是个裸字段读
///     SetRealTimeAndScale→  [rsi]=realDt; [rsi+8]=scale; [rsi+0x10]=f(realDt,scale)
/// 也就是说 SetRealTimeAndScale **三个字段全写了**，我们在托管侧看到的
/// "0.0358 -> 0.0537" 是真的写进去了。托管镜像 Marshal.SizeOf = 24 也说明布局一致。
///
/// 所以第一次没效果的原因只剩两种，这个模块就是把它们分开：
///   ① dt 确实写进去了，但 ActionMgr.Update 根本不拿它推进动作时钟
///   ② 动作时钟推进了，但动画不由它驱动
///
/// 三个杠杆，一次跑完就能定死是哪个
/// ────────────────────────────
///   Lever=Dt     改 ActionMgr.Update 的 dt            （第 1 版用的，没效果）
///   Lever=Time   绕过 dt，直接按指针写 ActionMgr.Time   （0x80，动作时钟本体）
///   Lever=Actor  改 ActorBase.UpdateAll 的 dt          （ActorBase 是逻辑+视觉的总入口）
///   ScaleModel   额外改 ActorModel.UpdateModel 的 dt   （动画/特效层，可叠加）
///
/// ⚠ 别同时开 Dt 和 Time —— 会双倍加速，反而分不清是谁干的。
///
/// 客观判据
/// ────────
/// 不靠手感。每次进入/离开名单里的动作，都打印它的**实际持续时间**和
/// 动作时钟的最终读数。开了倍率之后这两个数没变小 = 这个杠杆没用。
/// </summary>
internal static class EsActionSpeed
{
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;
    /// <summary>多组配置: 每组一个字符串 "倍率 | 动作1,动作2,..."。</summary>
    internal static BepInEx.Configuration.ConfigEntry<string>[] CfgGroups;
    internal const int GroupCount = 7;

    private sealed class Group
    {
        public float K = 1f;
        public readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// 配置里写成 `!名字` 的那些（存的是去掉 `!` 的名字）= **只作用于【原版】那一份**。
        /// 用途：把"原版动作"和"我们链里的克隆段"彻底分开调倍率。见 Match 的分流说明。
        /// </summary>
        public readonly HashSet<string> Bang = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
    private static List<Group> _groups;
    private static string _groupsRaw;

    /// <summary>解析全部组；任一组的字符串变了就整表重建。</summary>
    private static List<Group> Groups()
    {
        var sb = new System.Text.StringBuilder();
        if (CfgGroups != null) foreach (var e in CfgGroups) sb.Append(e?.Value).Append((char)1);
        var raw = sb.ToString();
        // ★ 按【值】比较，不能用 ReferenceEquals —— raw 是每次新拼出来的字符串，
        //   引用比较永远不成立 → 缓存全失效 → 每帧重解析 + 每帧刷日志（实测刷屏）。
        if (raw == _groupsRaw && _groups != null) return _groups;
        _groupsRaw = raw;

        var list = new List<Group>();
        if (CfgGroups != null)
        {
            foreach (var e in CfgGroups)
            {
                var g = new Group();
                var v = e?.Value;
                if (!string.IsNullOrWhiteSpace(v))
                {
                    int bar = v.IndexOf('|');
                    string kpart = bar < 0 ? v : v.Substring(0, bar);
                    string npart = bar < 0 ? "" : v.Substring(bar + 1);
                    float k;
                    if (float.TryParse(kpart.Trim(), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out k))
                        g.K = k;
                    foreach (var n in npart.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var t = n.Trim();
                        if (t.Length == 0) continue;
                        // `!名字` = 只作用于【原版】那一份（克隆不受影响）。不加 `!` 的走原路，行为不变。
                        if (t[0] == '!')
                        {
                            var bare = t.Substring(1).Trim();
                            if (bare.Length > 0) g.Bang.Add(bare);
                        }
                        else g.Names.Add(t);
                    }
                }
                list.Add(g);
            }
        }
        _groups = list;
        try
        {
            int nz = 0; foreach (var g in list) if (g.Names.Count > 0) nz++;
            Plugin.Log?.LogInfo($"  [动作变速] 组表已解析: {nz} 组非空 / 共 {list.Count}");
            for (int i = 0; i < list.Count; i++)
                if (list[i].Names.Count > 0)
                    Plugin.Log?.LogInfo($"     组{i + 1}: 倍率={list[i].K:F2}  动作 {list[i].Names.Count} 个");
        }
        catch { }
        return list;
    }

    /// <summary>当前帧命中的倍率（Match 命中时写入，Factor 读它）。</summary>
    private static float _curFactor = 1f;

    /// <summary>某个动作的倍率；不在任何组里 = 1（不加速）。给连段模组算窗口用。</summary>
    internal static float SpeedOf(string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name)) return 1f;
            // ★ 与 Match 用【同一套】分流规则 —— 两条路径判据不同步的话，
            //   "给连段模组算窗口"会和"实际变速"打架（窗口按 1 算、动作按 2.5 跑之类）。
            string src = EsComboChain.SourceOf(name);
            bool isClone = src != null;
            bool hij = EsComboChain.IsHijackedSource(name);
            bool bangOnly = !isClone && HasBang(name);      // ⚠ 同 Match：不能依赖 hij
            if (hij && !bangOnly) return 1f;
            foreach (var g in Groups())
            {
                if (isClone) { if (g.Names.Contains(src)) return g.K; }
                else if (bangOnly) { if (g.Bang.Contains(name)) return g.K; }
                else if (g.Names.Contains(name)) return g.K;
            }
        }
        catch { }
        return 1f;
    }
    internal static BepInEx.Configuration.ConfigEntry<string> CfgLever;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgScaleModel;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgScaleAnim;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgAnimProbe;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgClamp;
    internal static BepInEx.Configuration.ConfigEntry<float> CfgMaxStep;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgRedispatch;
    internal static BepInEx.Configuration.ConfigEntry<float> CfgAccelRatio;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgSplitByHit;
    internal static BepInEx.Configuration.ConfigEntry<float> CfgPostHitMargin;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgDiag;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgMeasure;

    // GameActionLogic 的关键字段偏移（从 dump.cs 抄的，反射拿不到就走指针）
    private const int OFF_ANIMATE_DUR = 0x30;   // Fp AnimateDuration
    private const int OFF_TOTAL_DUR = 0x40;     // Fp TotalDuration
    private const int OFF_MGR_TIME = 0x80;      // ActionMgr.<Time>k__BackingField
    private const int OFF_TIMEMAP = 0x48;       // bool EnableAnimationTimeMap
    private const int OFF_AUTOEND = 0xC8;       // bool AutoEndActionWhenMotionEnds ★
    private const double FP_ONE = 4294967296.0; // Q32.32 的 1.0

    private static readonly HashSet<string> _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static string _namesRaw;
    private static readonly HashSet<string> _hitLogged = new HashSet<string>();
    private static int _diagLeft;
    private static bool _sizeLogged;

    // ---- 时长测量 ----
    private static string _mName;
    private static float _mStart;
    private static float _mMaxTime;
    private static readonly HashSet<string> _mDone = new HashSet<string>();

    public static int Apply(Harmony harmony)
    {
        int n = 0;
        _diagLeft = 20;

        var dtType = AccessTools.TypeByName("GamePlay.DeltaTimeAndScale");
        if (dtType == null) { Plugin.Log?.LogError("  [动作变速] 找不到 GamePlay.DeltaTimeAndScale"); return 0; }

        // ---- 杠杆 A：ActionMgr.Update 的 dt（前缀）+ 计时/测量（后缀）----
        try
        {
            var t = AccessTools.TypeByName("GamePlay.ActionMgr");
            var m = t == null ? null : AccessTools.Method(t, "Update", new[] { dtType });
            if (m != null)
            {
                harmony.Patch(m,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(EsActionSpeed), nameof(ActionMgrPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(EsActionSpeed), nameof(ActionMgrPostfix))));
                Plugin.Log.LogInfo("  [动作变速] 已挂钩 ActionMgr.Update (pre=杠杆A dt / post=杠杆B 推Time + 计时)");
                n++;
            }
            else Plugin.Log.LogWarning("  [动作变速] 找不到 ActionMgr.Update(DeltaTimeAndScale)");
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [动作变速] 挂 ActionMgr.Update 失败: {e.Message}"); }

        // ---- 杠杆 C：ActorBase 的 dt（逻辑+视觉总入口）----
        // ⚠ UpdateAll / UpdateLogic / UpdateVisual 三个都在 ActorBase 上。
        //   到底游戏调的是 UpdateAll（内部再分派），还是直接调 UpdateLogic+UpdateVisual，
        //   从 dump 看不出来 —— 所以两个都挂，用"本帧已注入"的令牌去重，避免双倍加速。
        try
        {
            var t = AccessTools.TypeByName("GamePlay.ActorBase");
            bool any = false;
            foreach (var mn in new[] { "UpdateAll", "UpdateLogic" })
            {
                var m = t == null ? null : AccessTools.Method(t, mn, new[] { dtType });
                if (m == null) { Plugin.Log?.LogWarning($"  [动作变速] 找不到 ActorBase.{mn}(DeltaTimeAndScale)"); continue; }
                harmony.Patch(m, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsActionSpeed), nameof(ActorBasePrefix))));
                Plugin.Log.LogInfo($"  [动作变速] 已挂钩 ActorBase.{mn} (杠杆C)");
                n++; any = true;
            }
            if (!any) Plugin.Log?.LogWarning("  [动作变速] 杠杆C 一个都没挂上");
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [动作变速] 挂 ActorBase 失败: {e.Message}"); }

        // ---- 动画层：ActorModel.UpdateModel 的 dt ----
        // ⚠ 第一次挂失败的原因: Harmony 按【形参名】绑定, 而它的形参叫 delta 不叫 dt。
        //    日志原文: Parameter "dt" not found in method UpdateModel(GamePlay.DeltaTimeAndScale delta)
        try
        {
            var t = AccessTools.TypeByName("GamePlay.ActorModel");
            var m = t == null ? null : AccessTools.Method(t, "UpdateModel", new[] { dtType });
            if (m != null)
            {
                harmony.Patch(m, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsActionSpeed), nameof(ModelPrefix))));
                Plugin.Log.LogInfo("  [动作变速] 已挂钩 ActorModel.UpdateModel (动画/特效层)");
                n++;
            }
            else Plugin.Log.LogWarning("  [动作变速] 找不到 ActorModel.UpdateModel(DeltaTimeAndScale)");
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [动作变速] 挂 ActorModel.UpdateModel 失败: {e.Message}"); }

        // ---- 本地玩家的"视觉组件指针"登记（每帧刷新）----
        // 上一版靠 vis.transform 往上找 ActorModel, 结果探针一条都没输出 ——
        // 说明要么这条路找不到宿主, 要么 ES 用的根本不是 ActorVisualSpine。
        // 改成从上游拿: ActorModel.Renderer(0x38) 就是它的视觉组件, 直接登记指针。
        try
        {
            var t = AccessTools.TypeByName("GamePlay.ActorModel");
            var dtT2 = AccessTools.TypeByName("GamePlay.DeltaTimeAndScale");
            var m = (t == null || dtT2 == null) ? null : AccessTools.Method(t, "UpdateModel", new[] { dtT2 });
            if (m != null)
            {
                harmony.Patch(m, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsActionSpeed), nameof(ModelTrackPrefix))));
                Plugin.Log.LogInfo("  [动作变速] 已挂钩 ActorModel.UpdateModel (登记玩家视觉组件)");
                n++;
            }
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [动作变速] 挂视觉登记失败: {e.Message}"); }

        // ---- 动画层：Spine 和 3D 两种实现都挂 ----
        // ES 到底用哪个, 光看 dump 定不下来: ActorVisualSpine(Spine 2D) 和
        // ActorVisual3D(legacy Unity Animation) 都有 UpdateAnimation/SetAnimationTime。
        // 两个都挂 + 探针【无条件】打印, 一次就能看出是谁在动。
        n += PatchVisual(harmony, "GamePlay.ActorVisualSpine", "Spine");
        n += PatchVisual(harmony, "GamePlay.ActorVisual3D", "3D");

        // ---- 「无法出招」探针：动作切换的闸门 ----
        // "卡在原地无法出招"是逻辑层的事, 和动画无关。这里直接看闸门:
        // 玩家想切到某个动作时 CheckCanChangeToAction 给的是 true 还是 false。
        // 全程 false = 输入到了但被拒; 一条都没有 = 输入压根没到这一层。
        try
        {
            var t = AccessTools.TypeByName("GamePlay.ActionMgr");
            var m = t == null ? null : AccessTools.Method(t, "CheckCanChangeToAction", new[] { typeof(string) });
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsActionSpeed), nameof(CheckCanChangePostfix))));
                Plugin.Log.LogInfo("  [动作变速] 已挂钩 ActionMgr.CheckCanChangeToAction (出招闸门探针)");
                n++;
            }

            var mc = t == null ? null : AccessTools.Method(t, "ChangeAction", new[] { typeof(string) });
            if (mc != null)
            {
                harmony.Patch(mc, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsActionSpeed), nameof(ChangeActionResultPostfix))));
                Plugin.Log.LogInfo("  [动作变速] 已挂钩 ActionMgr.ChangeAction (切换结果探针)");
                n++;
            }
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [动作变速] 挂出招闸门失败: {e.Message}"); }

        Plugin.Log.LogInfo($"  [动作变速] 生效配置: Enabled={CfgEnabled?.Value} 倍率={_curFactor} " +
                           $"杠杆={CfgLever?.Value} 组表见上方日志 挂Model={CfgScaleModel?.Value} " +
                           $"挂动画={CfgScaleAnim?.Value} 动画探针={CfgAnimProbe?.Value}");
        return n;
    }

    // ------------------------------------------------------------------ 命中判定

    /// <summary>这个动作此刻是不是正在被加速（给连段模组放大衔接窗口用）。</summary>
    internal static bool IsSpeeding(string name)
    {
        try { return CfgEnabled?.Value == true && Match(name); } catch { return false; }
    }

    private static bool Match(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        // ★【隔离】按【方向】分流 —— 让"原版"和"克隆"能各自独立配倍率。
        //
        //   老行为: 被劫持了源名的原版动作【无条件】不碰
        //           （配置里写 attackD2 指的是我们链上那一段，不是原版佩利诺尔）。
        //   新增:   配置里写 `!名字` = 明确要求"连原版一起加速"。
        //     · 该名字在任一组的 `!` 形式里出现过 → 这个原版【只认 `!` 条目】。
        //       必须这样，否则它会被更靠前的组（Group1 里那个普通 `attackAEX`）先命中，
        //       而我们想让它落到专门给原版的那一组去。
        //     · 从没写过 `!` → **完全沿用老行为** → 现有配置零影响。
        //
        //   于是: 普通条目 = 只命中克隆;  `!` 条目 = 只命中原版。两边彻底独立。
        string src = EsComboChain.SourceOf(name);
        bool isClone = src != null;
        bool hij = EsComboChain.IsHijackedSource(name);
        // ⚠ `bangOnly` **不能**依赖 `hij`。
        //   `!` 的语义是"只作用于原版"，而"原版"根本不需要"被我们克隆过"这个前提。
        //   实测栽过一次: 空中布鲁诺(attackA_AirEX 等)**从没被克隆**，于是 hij=false
        //   → bangOnly=false → 走老路 → Names 里没有它（它在 Bang 里）→ **静默不加速** ✗
        bool bangOnly = !isClone && HasBang(name);
        if (hij && !bangOnly) { _curFactor = 1f; return false; }

        var gs = Groups();
        for (int gi = 0; gi < gs.Count; gi++)
        {
            var g = gs[gi];
            bool hit = false;
            if (isClone) hit = g.Names.Contains(src);          // 克隆 → 只认普通条目
            else if (bangOnly) hit = g.Bang.Contains(name);    // 原版 → 只认 `!`
            else hit = g.Names.Contains(name);                 // 老路
            if (hit) { _curFactor = g.K; LogSpeed(name, g.K, gi); return true; }
        }
        _curFactor = 1f;
        LogSpeed(name, 1f, -1);   // ★ 配置里提到过、却没命中任何组 —— 这就是"配了不生效"
        // ★ 我们造的「新平A」动作也要一起变速。
        //   否则链上原生平A段加速、克隆出来的段（佩利诺尔/布鲁诺/纹章解放）不加速，
        //   "全部加速"就只加了一半，连段节奏会在交界处断掉。
        return EsComboChain.IsOurs(name);
    }

    // ---------------- 变速命中日志：证明"配了到底生不生效" ----------------

    private static readonly HashSet<string> _speedLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 这个名字在配置里被提到过吗（普通条目 / `!` 条目 / 或作为某个克隆的源名）。
    /// 只报"提到过"的，不然全游戏的动作都会刷进来。
    /// </summary>
    private static bool IsConfigured(string name)
    {
        try
        {
            var gs = Groups();
            foreach (var g in gs)
                if (g.Names.Contains(name) || g.Bang.Contains(name)) return true;
            string src = EsComboChain.SourceOf(name);
            if (src != null)
                foreach (var g in gs)
                    if (g.Names.Contains(src) || g.Bang.Contains(src)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 报一次"这个名字实际拿到了什么倍率"。
    ///
    /// 为什么必须有它：**"配了不生效"在这个项目里已经栽了四次**，每次形态不同
    /// （名字被别的链抢 / 名字根本不存在 / 配错对象 / 判据条件多余），
    /// 而共同点都是 —— **不报错、静默**。有一行日志就能当场看出来。
    ///
    /// 按 (名字, 组, 倍率) **配对去重**：新配对永远打得出来，老配对只打一次
    /// （不用"限条数"那套 —— 那会制造假阴性，本项目栽过 5 次）。上限 300 兜底。
    /// </summary>
    private static void LogSpeed(string name, float k, int gi)
    {
        try
        {
            if (string.IsNullOrEmpty(name) || !IsConfigured(name)) return;
            var key = name + "|" + gi + "|" + k.ToString("F2");
            if (_speedLogged.Count >= 300 || !_speedLogged.Add(key)) return;
            Plugin.Log?.LogInfo(gi >= 0
                ? $"[动作变速] \"{name}\" → 组{gi + 1} 倍率 {k:F2}"
                : $"[动作变速] \"{name}\" → ★倍率 1.00（配置里提到了它，但一个组都没命中）");
        }
        catch { }
    }

    /// <summary>配置里有没有给这个名字写过 `!` 形式（任一组写了就算）。见 Match 的分流说明。</summary>
    private static bool HasBang(string name)
    {
        try
        {
            foreach (var g in Groups()) if (g.Bang.Contains(name)) return true;
        }
        catch { }
        return false;
    }

    private static bool Lever(string want)
    {
        var v = CfgLever?.Value;
        if (string.IsNullOrWhiteSpace(v)) v = "Dt";
        return string.Equals(v.Trim(), want, StringComparison.OrdinalIgnoreCase);
    }

    private static string LeverName() { var v = CfgLever?.Value; return string.IsNullOrWhiteSpace(v) ? "Dt" : v.Trim(); }

    // ------------------------------------------------------------------ 入口们

    public static void ActionMgrPrefix(GamePlay.ActionMgr __instance, ref GamePlay.DeltaTimeAndScale dt)
    {
        try
        {
            // ★★★ 撤销检查必须放在【所有提前 return 之前】。
            //   之前的写法是 `if (CfgEnabled != true) return;` 之后才检查，
            //   结果 Enabled=false 时直接就返回了，注入的区间永远撤不掉 ——
            //   表现正是"把变速关了，动作还是快"。
            if (CfgEnabled?.Value != true || !Lever("Inject")) ClearInjection();

            if (CfgEnabled?.Value != true) return;
            var act = __instance?.CurrentAction;
            if (act == null) return;
            string name = act.Name;
            if (name == null) return;

            var owner = __instance.Owner;
            if (!DashInvincible.IsLocalPlayerActor(owner)) return;

            // 记录进入 ActionMgr.Update 那一刻的 Time，后缀里对比 ——
            // 用来回答"动作时钟到底是不是在这里推进的"。
            // 已知：改这里的 dt 完全没用（实测），却不知道 Time 是不是另有其人推进。
            try { _tBefore = (float)__instance.Time; _tPtr = __instance.Pointer; }
            catch { _tPtr = IntPtr.Zero; }

            bool hit = Match(name);
            MeasureTick(__instance, name, hit, act);
            if (!hit) return;

            // ---- 杠杆 D：注入原生变速区间（正道）----
            if (Lever("Inject"))
            {
                InjectTimeScale(act, name);
                return;   // 不再推 Time，也不需要补派发
            }

            if (Lever("Dt")) Apply(ref dt, Factor(), "A:ActionMgr.dt", name, act);
        }
        catch (Exception e) { Once("ActionMgrPre", e); }
    }

    private static float _tBefore;
    private static IntPtr _tPtr = IntPtr.Zero;
    private static readonly Dictionary<string, int> _advPairs = new Dictionary<string, int>();

    /// <summary>后缀：杠杆 B —— 绕过 dt, 直接按指针把动作时钟往前推。</summary>
    public static void ActionMgrPostfix(GamePlay.ActionMgr __instance, GamePlay.DeltaTimeAndScale dt)
    {
        try
        {
            if (CfgEnabled?.Value != true) return;
            if (__instance == null) return;

            // ★ 先回答一个基础问题：ActionMgr.Update 自己有推进 Time 吗？
            //   （不夹带我们的推进量，所以要在推之前读）
            var a0 = __instance.CurrentAction;
            if (a0 != null && __instance.Pointer == _tPtr && CfgAnimProbe?.Value == true)
            {
                string key = "adv|" + a0.Name;
                _advPairs.TryGetValue(key, out int c);
                _advPairs[key] = ++c;
                if (c == 1 || c % 200 == 0)
                {
                    float now = 0f;
                    try { now = (float)__instance.Time; } catch { }
                    Plugin.Log?.LogInfo($"[动作变速:时钟] \"{a0.Name}\" 本次 Update 内 Time 推进 " +
                                        $"{(now - _tBefore):F5}s  (dt={SafeDelta(dt):F5})  (第 {c} 次)");
                }
            }

            if (!Lever("Time")) return;
            var act = a0;
            if (act == null || !Match(act.Name)) return;
            if (!DashInvincible.IsLocalPlayerActor(__instance.Owner)) return;

            float d = 0f;
            try { d = (float)dt.DeltaTime; } catch { }
            float extra = d * (Factor() - 1f);

            // 单次推进上限：动作里有"按动作时间触发"的事件（纹章释放、判定窗口），
            // 一次跨太大步会把落在中间的事件整段跳过去。掉帧时 dt 会突然变大
            // （实测见过 0.0358），正是"有时候丢纹章"的高发时刻。
            float cap = CfgMaxStep?.Value ?? 0.016f;
            if (cap > 0f && extra > cap) extra = cap;
            if (extra <= 0f) return;

            IntPtr p = __instance.Pointer;
            if (p == IntPtr.Zero) return;
            long cur = Marshal.ReadInt64(p, OFF_MGR_TIME);
            long next = cur + (long)Math.Round(extra * FP_ONE);

            // ⚠ 不越过 TotalDuration。原生代码是靠 `Time >= TotalDuration` 收尾的，
            //   把它推过头没有好处，却可能踩到按时间去做查表的逻辑
            //   （GameActionLogic.GetTimeScaleByTime / TimeScales 区间查找，
            //    以及各处 "按动作时间取第几段判定" 的索引）。
            if (CfgClamp?.Value != true)
            {
                long total = Marshal.ReadInt64(act.Pointer, OFF_TOTAL_DUR);
                if (total > 0 && next > total) next = total;
            }
            if (next <= cur) return;
            Marshal.WriteInt64(p, OFF_MGR_TIME, next);

            // ★★★ 关键一步：把"被跳过的那段时间区间"补派发一次。
            //
            // 为什么必须补：动作里的事件（纹章释放 = 逻辑里的 CreateBullet 指令、判定窗口、
            // 收招标志）不是靠读 Time 触发的，而是靠 `ActionLogicRunner.ActionUpdate(logic, t0, t1)`
            // 按【这一帧从第几秒走到第几秒】这个区间派发的。
            //
            // 游戏自己那次 Update 只派发了 [旧Time, 旧Time+dt]；我们把 Time 又往前推了 extra，
            // 那一段区间就【没人派发】—— Time 变了，事件没发生。
            // 表现正是：纹章丢失、"这一招完成了"的标志没置起来、平A接不上。
            //
            // 补上 [旧Time, 新Time] 这一段的派发，就等于让这段被加速的时间"照常发生过"。
            if (CfgRedispatch?.Value == true)
            {
                var runner = __instance.ActionRunner;
                if (runner != null)
                    runner.ActionUpdate(act, (Fp)(cur / (float)FP_ONE), (Fp)(next / (float)FP_ONE));
            }

            if (CfgDiag?.Value == true && _diagLeft > 0)
            {
                _diagLeft--;
                Plugin.Log?.LogInfo($"[动作变速:B:推Time] \"{act.Name}\" +{extra:F5}s " +
                                    $"Time {cur / FP_ONE:F4} -> {(cur + (long)Math.Round(extra * FP_ONE)) / FP_ONE:F4}");
            }
        }
        catch (Exception e) { Once("ActionMgrPost", e); }
    }

    /// <summary>杠杆 C。UpdateAll 和 UpdateLogic 共用这个入口，用【本帧令牌】去重 ——
    /// 万一游戏是 UpdateAll 里面再调 UpdateLogic，不去重就会叠乘两次。</summary>
    public static void ActorBasePrefix(GamePlay.ActorBase __instance, ref GamePlay.DeltaTimeAndScale dt)
    {
        try
        {
            if (CfgEnabled?.Value != true) return;
            if (!Lever("Actor")) return;
            if (__instance == null) return;
            if (!DashInvincible.IsLocalPlayerActor(__instance)) return;

            var am = __instance.ActionMgr;
            var act = am?.CurrentAction;
            if (act == null) return;
            string name = act.Name;
            if (!Match(name)) return;

            if (!ClaimFrame(__instance.Pointer)) return;

            Apply(ref dt, Factor(), "C:ActorBase", name, act);
        }
        catch (Exception e) { Once("ActorBasePre", e); }
    }

    public static void ModelPrefix(GamePlay.ActorModel __instance, ref GamePlay.DeltaTimeAndScale delta)
    {
        try
        {
            if (CfgEnabled?.Value != true || CfgScaleModel?.Value != true) return;
            var owner = __instance?.Owner;
            if (owner == null || !DashInvincible.IsLocalPlayerActor(owner)) return;
            var am = owner.ActionMgr;
            if (am == null) return;
            string name = am.CurrentActionName;
            if (!Match(name)) return;

            Apply(ref delta, Factor(), "M:UpdateModel", name, am.CurrentAction);
        }
        catch (Exception e) { Once("ModelPre", e); }
    }

    // ------------------------------------------------------------------ 动画钟

    /// <summary>登记本地玩家的视觉组件指针 + 当前动作名。每帧由 ActorModel.UpdateModel 刷新。
    /// 拿宿主不再靠 transform 上溯（上一版就是这么失败的，探针一条没出），
    /// 而是走 ActorModel.Renderer(0x38) —— 这是确定关系，不是猜的。</summary>
    public static void ModelTrackPrefix(GamePlay.ActorModel __instance)
    {
        try
        {
            if (__instance == null) return;
            var owner = __instance.Owner;
            if (owner == null || !DashInvincible.IsLocalPlayerActor(owner)) return;

            // ⚠ 集合【不再每帧清空】。之前每帧 clear+重建，结果和 UpdateAnimation 的调用顺序
            //   形成竞态：谁先谁后就决定看不看得见玩家。改成登记一次就长期留着。
            var r = __instance.Renderer;
            if (r != null) _playerVisuals.Add(r.Pointer);

            string act = null;
            try { act = owner.ActionMgr?.CurrentActionName; } catch { }
            _playerAction = act;

            // ★ 动画加速改用 Spine 自带的 timeScale，不再去乘 delta。
            //   为什么换：实测 SetAnimationTime/UpdateAnimation 在出招期间**根本看不到玩家**
            //   （探针 102 条里只有 born/run/stand/drop/dashAir，attack1~4 一次都没有），
            //   所以乘 delta 这条路根本落不到实处。Spine 的 timeScale 是它自己的全局倍率，
            //   挂上去就一直有效，和调用顺序、实参都无关。
            ApplySpineTimeScale(__instance, act);

            // 顺便登记这个模型的 Renderer，供 UpdateAnimation 探针用
        }
        catch (Exception e) { Once("ModelTrack", e); }
    }

    private static void ApplySpineTimeScale(GamePlay.ActorModel model, string act)
    {
        try
        {
            float want = 1f;
            if (CfgEnabled?.Value == true && CfgScaleAnim?.Value == true && Match(act)) want = Factor();
            if (Math.Abs(_spineScale - want) < 0.001f) return;   // 值没变就不写, 省得每帧跨边界

            var vis = model?.Renderer;
            if (vis == null) return;
            // ⚠ Il2CppInterop 的多态返回是声明的基类包装, 必须 TryCast
            var spine = vis.TryCast<GamePlay.ActorVisualSpine>();
            if (spine == null) return;
            var anim = spine.SkeletonAnim;
            if (anim == null) return;

            anim.timeScale = want;
            _spineScale = want;
            Plugin.Log?.LogInfo($"[动作变速:动画钟] Spine.timeScale = {want} (动作=\"{act}\")");
        }
        catch (Exception e) { Once("SpineScale", e); }
    }

    private static float _spineScale = 1f;

    private static readonly HashSet<IntPtr> _playerVisuals = new HashSet<IntPtr>();
    private static string _playerAction;

    /// <summary>动画钟闸门：探针 + 是否该加速。两个实现（Spine / 3D）共用。
    /// 返回 true = 调用方应该把 delta 乘上倍率。</summary>
    private static bool AnimGate(IntPtr ptr, string label, float arg)
    {
        bool isPlayer = _playerVisuals.Contains(ptr);
        string act = isPlayer ? _playerAction : null;

        // 只报玩家的, 且按"动作名"去重 —— 非玩家每帧都在刷, 上一版把预算全刷掉了
        if (CfgAnimProbe?.Value == true && isPlayer)
        {
            string key = label + "|" + (act ?? "?");
            _animPairs.TryGetValue(key, out int cnt);
            _animPairs[key] = ++cnt;
            if (cnt == 1 || cnt % 300 == 0)
                Plugin.Log?.LogInfo($"[动作变速:动画钟] {label} arg={arg:F5} 动作=\"{act}\" " +
                                    $"命中={Match(act)} (第 {cnt} 次)");
        }

        // 不再走"乘 delta"这条路 —— 实测出招期间这两个钩子根本看不到玩家（见 ModelTrackPrefix 注释）。
        // 动画加速已改由 Spine 的 timeScale 承担，这里只留探针。
        return false;
    }

    private static readonly Dictionary<string, int> _animPairs = new Dictionary<string, int>();

    // 本帧去重：同一实例一帧只注入一次倍率。
    // ⚠ 不能用"全局帧号"——那会把同一个角色身上第二个视觉组件(武器/披风)挡掉，
    //   变成身体加速、配件原速，反而更难看。
    private static int _claimFrame = -1;
    private static readonly HashSet<IntPtr> _claimed = new HashSet<IntPtr>();

    private static bool ClaimFrame(IntPtr key)
    {
        int fc;
        try { fc = UnityEngine.Time.frameCount; } catch { return true; }
        if (fc != _claimFrame) { _claimFrame = fc; _claimed.Clear(); }
        return _claimed.Add(key);
    }

    private static float Factor() { return Clamp(_curFactor); }

    private static float SafeDelta(GamePlay.DeltaTimeAndScale dt)
    {
        try { return (float)dt.DeltaTime; } catch { return -1f; }
    }

    // ---- 具体钩子：两个实现各写一份小包装，只因为 ref 参数没法共用 ----

    public static void SpineDeltaPrefix(GamePlay.ActorVisualSpine __instance, ref float delta)
    {
        try
        {
            if (CfgEnabled?.Value != true || __instance == null) return;
            if (AnimGate(__instance.Pointer, "Spine.UpdateAnimation", delta)) delta *= Factor();
        }
        catch (Exception e) { Once("SpineDelta", e); }
    }

    public static void V3DeltaPrefix(GamePlay.ActorVisual3D __instance, ref float delta)
    {
        try
        {
            if (CfgEnabled?.Value != true || __instance == null) return;
            if (AnimGate(__instance.Pointer, "3D.UpdateAnimation", delta)) delta *= Factor();
        }
        catch (Exception e) { Once("V3Delta", e); }
    }

    public static void SpineSetTimePrefix(GamePlay.ActorVisualSpine __instance, float time)
    {
        SetTimeNote("Spine", __instance.Pointer, time);
    }

    public static void V3SetTimePrefix(GamePlay.ActorVisual3D __instance, float time)
    {
        SetTimeNote("3D", __instance.Pointer, time);
    }

    private static readonly Dictionary<string, int> _setTimePairs = new Dictionary<string, int>();

    private static void SetTimeNote(string label, IntPtr ptr, float time)
    {
        try
        {
            if (CfgEnabled?.Value != true || CfgAnimProbe?.Value != true) return;
            if (!_playerVisuals.Contains(ptr)) return;
            string key = label + "|" + (_playerAction ?? "?");
            _setTimePairs.TryGetValue(key, out int cnt);
            _setTimePairs[key] = ++cnt;
            if (cnt != 1 && cnt % 300 != 0) return;
            Plugin.Log?.LogInfo($"[动作变速:动画钟] {label}.SetAnimationTime({time:F4}) " +
                                $"动作=\"{_playerAction}\" (第 {cnt} 次)");
        }
        catch { }
    }

    /// <summary>出招闸门探针。**按 (当前动作 → 目标动作) 配对去重**，而不是"只记前 N 条" ——
    /// 上一版就是这么栽的: 40 条预算被 `rush` 里狂按上+A 的一次爆发吃光，
    /// 等到真正要查的"卡住时按 A"时，探针已经哑了。
    /// 去重后每一对只会打一条，新的配对永远能打出来，不受爆发影响。</summary>
    public static void CheckCanChangePostfix(GamePlay.ActionMgr __instance, string name, bool __result)
    {
        try
        {
            if (CfgEnabled?.Value != true || CfgAnimProbe?.Value != true) return;
            if (__instance == null) return;
            if (!DashInvincible.IsLocalPlayerActor(__instance.Owner)) return;

            string cur = null;
            try { cur = __instance.CurrentActionName; } catch { }
            string key = (cur ?? "?") + " -> " + (name ?? "?");
            _gatePairs.TryGetValue(key, out int cnt);
            _gatePairs[key] = ++cnt;

            if (__result)
            {
                // 允许: 只记每对的第一次。允许是常态, 全记会淹掉日志。
                if (cnt != 1) return;
            }
            else
            {
                // ⚠ 拒绝: **一律记**(限流 10 条/秒)。
                //   上一版按 "第 1 次 + 每 100 次" 打, 结果第 2~99 次全被静默 ——
                //   也就是说"卡住之后才开始出现的拒绝"正好落在盲区里, 白跑一轮。
                //   拒绝本来就是稀有事件, 没有理由再压它。
                if (_rejectLogged >= 300) return;
                if (Now() - _lastRejectT < 0.1f) return;
                _lastRejectT = Now();
                _rejectLogged++;
            }

            float t = 0f;
            try { t = (float)__instance.Time; } catch { }
            Plugin.Log?.LogInfo($"[动作变速:闸门] {(__result ? "○ 允许" : "✗ 拒绝")} \"{cur}\" -> \"{name}\"  " +
                                $"(第 {cnt} 次, 动作时钟={t:F3}s/总时长={FpAt(__instance.CurrentAction, OFF_TOTAL_DUR):F3}s)");
        }
        catch (Exception e) { Once("GateProbe", e); }
    }

    private static readonly Dictionary<string, int> _gatePairs = new Dictionary<string, int>();
    private static float _lastRejectT = -1f;
    private static int _rejectLogged;

    /// <summary>ChangeAction 本身的返回值。闸门放行 ≠ 真的切成功 ——
    /// 这中间还有 changeActionPrecheck 的其它条件。</summary>
    public static void ChangeActionResultPostfix(GamePlay.ActionMgr __instance, string name, bool __result)
    {
        try
        {
            if (CfgEnabled?.Value != true || CfgAnimProbe?.Value != true) return;
            if (__instance == null || __result) return;
            if (!DashInvincible.IsLocalPlayerActor(__instance.Owner)) return;
            // ⚠ 空名字必须跳过：stand 之类的待机每帧都在 ChangeAction("") 并失败。
            if (string.IsNullOrEmpty(name)) return;

            // ⚠⚠ 一律按 (当前动作 -> 目标动作) 配对去重, **不设总条数上限**。
            //   本项目已经四次栽在同一个坑上：加了"只记前 N 条", 预算被前面的噪声吃光
            //   （第 1 次 rush→AttackUp2 爆发、第 2 次 ChangeAction("")、第 3 次
            //    stand→squat 每帧重试），真正要看的时刻反而一片空白 —— 假阴性。
            //   配对去重后：新配对永远打得出来, 旧配对每 200 次提醒一次。
            string cur = null;
            try { cur = __instance.CurrentActionName; } catch { }
            string key = (cur ?? "?") + " -> " + name;
            _changeFailPairs.TryGetValue(key, out int fc);
            _changeFailPairs[key] = ++fc;
            if (fc > 3 && fc % 200 != 0) return;

            Plugin.Log?.LogInfo($"[动作变速:切换] ✗ ChangeAction(\"{name}\") 返回 false " +
                                $"(当前动作=\"{cur}\", 第 {fc} 次)");
        }
        catch (Exception e) { Once("ChangeResProbe", e); }
    }

    private static readonly Dictionary<string, int> _changeFailPairs = new Dictionary<string, int>();

    private static int PatchVisual(Harmony harmony, string typeName, string label)
    {
        int n = 0;
        try
        {
            var t = AccessTools.TypeByName(typeName);
            if (t == null) { Plugin.Log?.LogWarning($"  [动作变速] 找不到 {typeName}"); return 0; }

            var m1 = AccessTools.Method(t, "UpdateAnimation", new[] { typeof(float) });
            if (m1 != null)
            {
                var hook = label == "Spine" ? nameof(SpineDeltaPrefix) : nameof(V3DeltaPrefix);
                harmony.Patch(m1, prefix: new HarmonyMethod(AccessTools.Method(typeof(EsActionSpeed), hook)));
                Plugin.Log.LogInfo($"  [动作变速] 已挂钩 {typeName}.UpdateAnimation");
                n++;
            }
            var m2 = AccessTools.Method(t, "SetAnimationTime", new[] { typeof(float) });
            if (m2 != null)
            {
                var hook = label == "Spine" ? nameof(SpineSetTimePrefix) : nameof(V3SetTimePrefix);
                harmony.Patch(m2, prefix: new HarmonyMethod(AccessTools.Method(typeof(EsActionSpeed), hook)));
                Plugin.Log.LogInfo($"  [动作变速] 已挂钩 {typeName}.SetAnimationTime");
                n++;
            }
            if (n == 0) Plugin.Log?.LogWarning($"  [动作变速] {typeName} 两个入口都没找到");
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [动作变速] 挂 {typeName} 失败: {e.Message}"); }
        return n;
    }

    // ------------------------------------------------------------------ 时长测量（客观判据）

    private static void MeasureTick(GamePlay.ActionMgr mgr, string name, bool hit, GamePlay.GameActionLogic act)
    {
        if (CfgMeasure?.Value != true) return;

        if (!string.Equals(name, _mName, StringComparison.Ordinal))
        {
            // 上一个动作结束 —— 结算它的实际时长
            if (_mName != null && _mDone.Add(_mName + "@" + _mStart))
            {
                float dur = Now() - _mStart;
                Plugin.Log?.LogInfo($"[动作变速:计时] \"{_mName}\" 实测持续 {dur:F3}s, " +
                                    $"动作时钟走到 {_mMaxTime:F3}s, 原生时长={FpAt(act_Prev, OFF_TOTAL_DUR):F3}s " +
                                    $"(杠杆={LeverName()} 倍率={_curFactor})");
            }
            _mName = name;
            _mStart = Now();
            _mMaxTime = 0f;
            act_Prev = act;
        }

        if (hit)
        {
            float t = 0f;
            try { t = (float)mgr.Time; } catch { }
            if (t > _mMaxTime) _mMaxTime = t;
        }
    }

    private static GamePlay.GameActionLogic act_Prev;

    /// <summary>读 GameActionLogic 上的 bool 字段。
    /// AutoEndActionWhenMotionEnds 是最关键的一个: 为 true 说明这个动作是
    /// **「动画播完就收招」** —— 那动作时长其实是跟着动画走的, 只推动作时钟必然把
    /// 动画甩在后面, 纹章释放这类挂在动画某帧上的事件就再也不会触发。</summary>
    private static bool BoolAt(GamePlay.GameActionLogic a, int off)
    {
        try
        {
            if (a == null) return false;
            IntPtr p = a.Pointer;
            if (p == IntPtr.Zero) return false;
            return Marshal.ReadByte(p, off) != 0;
        }
        catch { return false; }
    }

    private static float FpAt(GamePlay.GameActionLogic a, int off)
    {
        try
        {
            if (a == null) return -1f;
            IntPtr p = a.Pointer;
            if (p == IntPtr.Zero) return -1f;
            return (float)(Marshal.ReadInt64(p, off) / FP_ONE);
        }
        catch { return -1f; }
    }

    // ------------------------------------------------------------------ 杠杆 D：原生变速区间

    /// <summary>
    /// 往 `GameActionLogic.TimeScales` 里塞一条「(0 ~ 总时长) 以 X 倍率播放」的记录。
    ///
    /// 为什么这条路和推 Time 有本质区别
    /// ────────────────────────────────
    /// 实测 `GameActionLogic.GetTimeScaleByTime(Fp time)` **每帧都在被调用**
    /// （attack1/2/3/4 全都看得到，t 就是当前动作时间），只是动作表里 TimeScales 是空的，
    /// 所以一直返回 null。
    ///
    /// 也就是说：**游戏本来就支持给单个动作配变速**，而且这个倍率是由游戏自己的代码
    /// 作用在 dt 上的。我们只需要把数据填进去 —— 不去拦任何函数参数，
    /// 所以完全绕开了"Harmony 对值类型形参的 ref 写回传不到原生"这个死结
    /// （Lever=Dt 失效的根因）。
    ///
    /// 好处：动作时钟、动画、位移、事件派发、判定窗口 **全部由同一个 dt 驱动**，
    /// 天然同步。不需要推 Time，不需要手工补派发，也不会出现"招没完成"。
    /// </summary>
    private static void InjectTimeScale(GamePlay.GameActionLogic act, string name)
    {
        try
        {
            IntPtr ap = act.Pointer;
            if (ap == IntPtr.Zero) return;

            // ⚠ 倍率或比例改了就必须【撤销重注入】。
            //   注入是"每个 GameActionLogic 只做一次"的，不这样处理的话，
            //   在面板里调 Speed / AccelRatio 会毫无反应 —— 会让人以为参数无效。
            float newFactor = Factor();
            float newRatio = Clamp01(CfgAccelRatio?.Value ?? 1f);
            if (Math.Abs(newFactor - _injFactor) > 0.001f || Math.Abs(newRatio - _injRatio) > 0.001f)
            {
                ClearInjection();
                _injFactor = newFactor;
                _injRatio = newRatio;
            }

            if (!_injectedAct.Add(ap)) return;      // 同一个 GameActionLogic 只注入一次

            float factor = newFactor;
            long end = Marshal.ReadInt64(ap, OFF_TOTAL_DUR);

            // 只加速前 ratio 段，剩下的后摇留在原速 ——
            // 取消窗口在动作时间上是【全程敞开】的（ChangeSkill 的 TimeCheck 是 0.000~2.000），
            // 所以没法靠"加宽窗口"补偿；唯一能保住实时手感的办法就是让后摇段不加速。
            // ★ 攻击段 / 后摇 的分界：优先用**原生数据**，而不是拍脑袋的比例。
            //   GameActionLogic.HitDataList(0x190) 就是攻击判定的时间段列表 ——
            //   最后一段判定结束的那一刻，就是"攻击"结束、"后摇"开始的地方。
            long split;
            string splitWhy;
            float lastHit = LastHitEnd(ap);
            float endF = end / (float)FP_ONE;
            if (CfgSplitByHit?.Value != true || lastHit <= 0f || lastHit >= endF)
            {
                float ratio = newRatio;
                split = ratio >= 1f ? end : (long)(end * ratio);
                splitWhy = $"比例 {ratio:F2}" + (lastHit > 0f ? $" (判定段到 {lastHit:F3}s)" : " (无判定段数据)");
            }
            else
            {
                float s = lastHit + Math.Max(0f, CfgPostHitMargin?.Value ?? 0f);
                if (s > endF) s = endF;
                split = (long)Math.Round(s * FP_ONE);
                splitWhy = $"判定段结束 {lastHit:F3}s" +
                           (CfgPostHitMargin?.Value > 0f ? $" + 余量{CfgPostHitMargin?.Value:F2}s" : "");
            }
            if (split < 0) split = 0;

            var list = act.TimeScales;
            int before = 0;
            if (list == null)
            {
                list = new Il2CppSystem.Collections.Generic.List<GamePlay.ActionTimeScaleRange>();
                act.TimeScales = list;
            }
            else { try { before = list.Count; } catch { } }

            // ActionTimeScaleRange 的布局（dump.cs）:
            //   0x10 TimeStart   0x18 TimeEnd      （基类 ActionTimeRangeTrigerer）
            //   0x20 TimeRange.Start   0x28 TimeRange.Finish
            //   0x30 TimeScale
            // 基类的 TimeStart/TimeEnd 和 TimeRange 两套都填上 —— 不知道游戏读哪一套。
            var r1 = MakeRange(0, split, factor);
            var r2 = MakeRange(split, end, 1f);
            if (r1 == null) return;
            list.Add(r1); _injectedOwner[r1] = act;
            if (r2 != null && split < end) { list.Add(r2); _injectedOwner[r2] = act; }

            Plugin.Log?.LogInfo($"[动作变速:注入] \"{name}\" 变速区间: " +
                                $"0 ~ {split / FP_ONE:F3}s x{factor}" +
                                (split < end ? $",  {split / FP_ONE:F3} ~ {end / FP_ONE:F3}s x1(后摇保原速)" : " (全程)")
                                + $"   分界依据={splitWhy}  (原有 {before} 条)");
        }
        catch (Exception e) { Once("Inject", e); }
    }

    /// <summary>造一条 ActionTimeScaleRange。倍率为 1 的那条也要显式给出来 ——
    /// 万一游戏是"取第一个命中的区间"而不是"取包含 time 的区间", 显式给出才不会有歧义。</summary>
    // ---- 攻击判定段（决定"攻击"到第几秒结束）----
    private const int OFF_HITDATA = 0x190;      // List<ActionHitData>
    private const int HIT_TIMEEND = 0x18;       // 基类 ActionTimeRangeTrigerer 的 TimeEnd
    private const int HIT_TICKRANGE = 0x20;     // TimeRepeatOrRange{ Start@0x10, End@0x18 }
    private const int TR_END = 0x18;

    /// <summary>这个动作最后一个"攻击判定段"在动作时间的第几秒结束。
    /// 返回 &lt;= 0 表示拿不到（判定段为空或读失败），调用方退回比例模式。
    ///
    /// 这就是"攻击 vs 后摇"的**原生分界** —— 不需要我们拍脑袋定比例。</summary>
    private static float LastHitEnd(IntPtr ap)
    {
        try
        {
            int n = ListCountAt(ap, OFF_HITDATA);
            if (n <= 0) return -1f;
            float max = -1f;
            for (int i = 0; i < n && i < 64; i++)
            {
                IntPtr h = ListElemAt(ap, OFF_HITDATA, i);
                if (h == IntPtr.Zero) continue;

                float a = FpOff(h, HIT_TIMEEND);
                if (a > max) max = a;

                IntPtr tr = PtrAt(h, HIT_TICKRANGE);
                if (tr != IntPtr.Zero)
                {
                    float b = FpOff(tr, TR_END);
                    if (b > max) max = b;
                }
            }
            return max;
        }
        catch { return -1f; }
    }

    private static float FpOff(IntPtr p, int off)
    {
        if (p == IntPtr.Zero) return 0f;
        return (float)(Marshal.ReadInt64(p, off) / FP_ONE);
    }

    private static IntPtr PtrAt(IntPtr p, int off)
    {
        if (p == IntPtr.Zero) return IntPtr.Zero;
        IntPtr v = Marshal.ReadIntPtr(p, off);
        long x = v.ToInt64();
        if (x < 0x10000 || x > 0x7FFFFFFFFFFF || (x & 7) != 0) return IntPtr.Zero;
        return v;
    }

    private static int ListCountAt(IntPtr p, int off)
    {
        var l = PtrAt(p, off);
        if (l == IntPtr.Zero) return 0;
        int n = Marshal.ReadInt32(l, 0x18);
        return (n < 0 || n > 4096) ? 0 : n;
    }

    private static IntPtr ListElemAt(IntPtr p, int off, int i)
    {
        var l = PtrAt(p, off);
        if (l == IntPtr.Zero) return IntPtr.Zero;
        var arr = PtrAt(l, 0x10);
        if (arr == IntPtr.Zero) return IntPtr.Zero;
        return PtrAt(arr, 0x20 + i * 8);
    }

    private static GamePlay.ActionTimeScaleRange MakeRange(long t0, long t1, float scaleF)
    {
        var r = new GamePlay.ActionTimeScaleRange();
        IntPtr rp = r.Pointer;
        if (rp == IntPtr.Zero) { Once("Inject", new Exception("range.Pointer 为空")); return null; }
        Marshal.WriteInt64(rp, 0x10, t0);
        Marshal.WriteInt64(rp, 0x18, t1);
        Marshal.WriteInt64(rp, 0x20, t0);
        Marshal.WriteInt64(rp, 0x28, t1);
        Marshal.WriteInt64(rp, 0x30, (long)Math.Round(scaleF * FP_ONE));
        return r;
    }

    private static float Clamp01(float v)
    {
        if (v < 0.1f) return 0.1f;
        if (v > 1f) return 1f;
        return v;
    }

    /// <summary>把注入过的区间全部撤掉（切走 Lever / 改参数时调用），让动作恢复原速。</summary>
    private static void ClearInjection()
    {
        if (_injectedOwner.Count == 0) return;
        int n = 0;
        foreach (var kv in _injectedOwner)
        {
            try
            {
                var list = kv.Value.TimeScales;
                if (list != null && list.Remove(kv.Key)) n++;
            }
            catch { }
        }
        Plugin.Log?.LogInfo($"[动作变速:注入] 已撤销 {n} 条注入的变速区间");
        _injectedAct.Clear();
        _injectedOwner.Clear();
    }

    private static readonly HashSet<IntPtr> _injectedAct = new HashSet<IntPtr>();
    private static readonly Dictionary<GamePlay.ActionTimeScaleRange, GamePlay.GameActionLogic> _injectedOwner
        = new Dictionary<GamePlay.ActionTimeScaleRange, GamePlay.GameActionLogic>();
    private static float _injFactor = -1f, _injRatio = -1f;

    // ------------------------------------------------------------------ 核心：改 dt

    private static void Apply(ref GamePlay.DeltaTimeAndScale dt, float factor,
                              string layer, string name, GamePlay.GameActionLogic act)
    {
        // ⚠ ref 参数不能进 lambda，只能老实写 try/catch
        float before = 0f, real = 0f, after = 0f;
        Fp ts = (Fp)1f;
        try
        {
            before = (float)dt.DeltaTime;
            real = (float)dt.RealDeltaTime;
            ts = dt.TimeScale;
        }
        catch { return; }

        try
        {
            dt.SetRealTimeAndScale((Fp)(real * factor), ts);
            after = (float)dt.DeltaTime;
        }
        catch { return; }

        if (!_sizeLogged)
        {
            _sizeLogged = true;
            int sz = 0;
            try { sz = Marshal.SizeOf<GamePlay.DeltaTimeAndScale>(); } catch { }
            Plugin.Log?.LogInfo($"  [动作变速] DeltaTimeAndScale 托管镜像 = {sz} 字节 (原生 24 = 3x Fp)。");
        }

        if (_hitLogged.Add(layer + "|" + name))
        {
            Plugin.Log?.LogInfo($"[动作变速] ★命中 [{layer}] \"{name}\" —— " +
                                $"总时长={FpAt(act, OFF_TOTAL_DUR):F3}s 动画时长={FpAt(act, OFF_ANIMATE_DUR):F3}s " +
                                $"动画结束即收招={BoolAt(act, OFF_AUTOEND)} 动画时间映射={BoolAt(act, OFF_TIMEMAP)}");
        }

        if (CfgDiag?.Value == true && _diagLeft > 0)
        {
            _diagLeft--;
            Plugin.Log?.LogInfo($"[动作变速:{layer}] \"{name}\" x{factor}  dt {before:F6} -> {after:F6}");
        }
    }

    // ------------------------------------------------------------------ 小工具

    private static float Now()
    {
        try { return UnityEngine.Time.realtimeSinceStartup; } catch { return 0f; }
    }

    private static float Clamp(float f)
    {
        if (f < 0.1f) return 0.1f;
        if (f > 10f) return 10f;
        return f;
    }

    private static readonly HashSet<string> _errLogged = new HashSet<string>();
    private static void Once(string layer, Exception e)
    {
        if (_errLogged.Add(layer + e.GetType().Name))
            Plugin.Log?.LogWarning($"[动作变速:{layer}] 异常(只报一次): {e.Message}");
    }
}
