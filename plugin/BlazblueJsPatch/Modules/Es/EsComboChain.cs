using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Unity.Mathematics.FixedPoint;

namespace BlazblueJsPatch;

/// <summary>
/// 普攻连段模组：把平A那几段换成任意动作序列。
///
/// 转储出来的真实结构（ES）
/// ──────────────────────
/// `m_SkChains[1]` (Attack) 里是**一条 13 段的链**，按槽位号排，
/// 靠每段的 `InputDir` 区分是哪种输入起手：
///
///     [0..2]   AttackUp / AttackUp2   1.1~1.3   InputDir=Up     上+攻击
///     [3..6]   attackD1/D1/D2/D3      1.4~1.7   InputDir=Down   下+攻击 = 佩利诺尔 1~4 段
///     [7]      atkAirX                1.8       InputDir=Down
///     [8..11]  attack1..attack4       1.9~1.12  InputDir=Any    ★ 平A 1~4 段
///     [12]     atkAirX                1.13      InputDir=Any
///
/// 所以"平1-平2-平3-平4"= 这条链里 InputDir=Any 的那四段。
/// 按普攻键时，游戏从链里挑 InputDir 匹配的下一段推进。
///
/// 改法（★ 2026-10-02 大改，见下）
/// ──────────────────────
/// 把 [8..11] 这四段**替换成配置里的任意段序列**，其余原样保留
/// （上+攻击 / 下+攻击 / 空中 全都不受影响）。
///
/// ★★★ **每一新段克隆的是【目标动作自己的原生行】，不是平A模板。**
/// 只覆盖五个"链位置"字段：`Group` / `Input` / `InputDir` / `Order` / `PreSkillOrder`；
/// **触发器(StartTrigger/ExitTrigger/ReqTriggerId)、自带脚本、自带弹幕、时序 全部保留它自己的。**
///
/// 为什么必须这样（血泪，前一版就是这么错的）：
///   一行技能不等于"播哪个动作"，它是
///   **【动作 + 触发器握手 + 自带脚本 + 自带弹幕】** 捆在一起的整体。
///   旧版克隆平A那一行、只把 Action 换掉，于是造出来的段
///   **播着布鲁诺的动作，却发着平A的 `Start_1` 触发器**，
///   而布鲁诺自己该发的 `Start_2` 从没发出去 ——
///   等于**把动作和它的状态机劈开了**，表现就是"剑气/触发器把连段衔接打乱"。
///   （实测触发器: attack1=Start_1, attack4=End_1, attackAEX=Start_2+ReqTriggerId[5607,5626]）
///
/// 因为改的是**克隆体**，共享的表行一个字节都没动 ——
/// 下+攻击原本的佩利诺尔连段、技能键的布鲁诺，全都照常能用。
///
/// 段数不受原来的 4 段限制：`SkillList` 是普通 List，要几段插几段。
/// </summary>
internal static class EsComboChain
{
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;

    /// <summary>只关「改写链路」、保留探针。
    /// CfgEnabled=false 会把模块连同探针一起停掉，于是「关掉连段改动、观察原生行为」这件事做不了 ——
    /// 所以拆出一个独立开关：false = 不改链，但选段/汇点/段闸门等探针照常工作。</summary>
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgRewrite;
    internal static BepInEx.Configuration.ConfigEntry<string> CfgSequence;
    /// <summary>多链声明。Chain1..Chain6，格式 "组号 | 动作[方向][+], ..."</summary>
    internal static BepInEx.Configuration.ConfigEntry<string>[] CfgChains;
    internal const int ChainCount = 6;
    /// <summary>Sequence 里出现的动作，是否移除原生同动作段（见 §3.6.1e E）。</summary>
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgTakeOver;

    /// <summary>
    /// 哪些链用【原地替换】而不是【移除 + 追加】。逗号分隔的组号，如 "4"。
    ///
    /// 为什么需要两种模式（2026-10-04 实测基线）：
    ///   两条链的推进机制**根本不同** ——
    ///     · 地面链：走 `findNextSkillMatchPreOrderAndInputDir`，按【段号】匹配 → 不看数组位置
    ///     · 空中链：**按数组顺序挪游标**，不走上面那条搜索（选段探针全日志只服务 dash 链）
    ///   证据：`输入推进 -> True  链段数=16  Cur@12("AttackUp2")`
    ///         → `Cur@N` 的 N 是【数组下标】；下标 11=atkAir12、12=AttackUp2、13=atkAir3，顺次推进。
    ///   所以对空中链做"把原生段从中间挖掉、克隆追加到末尾"会**让整条数组错位**，
    ///   游标走到那一格踩空 → 连段整条断掉（就是"空中只剩一段"的真因）。
    /// </summary>
    internal static BepInEx.Configuration.ConfigEntry<string> CfgInPlace;
    /// <summary>显式接线。Link1..Link6，格式 "目标Order &lt;- 前驱Order,前驱Order | ..."</summary>
    internal static BepInEx.Configuration.ConfigEntry<string>[] CfgLinks;

    /// <summary>
    /// **起跳时**清空中链游标 —— 复刻原版"空中平A永远从空1开始"的规则。
    ///
    /// 为什么判据是【起跳】而不是【落地】：
    ///   原版规则的本质是"每次跳起来，空中平A都从空1开始"。
    ///   而"落地"这个判据实测**必然误伤** —— `drop*` / `fallmdown*` 这些名字在空中也会出现，
    ///   于是连段打到一半就被清掉（空4 直接没了）。想按"真的落地"判又找不到判据：
    ///   `ActorBase` 的 0x10~0x200 范围里**没有任何 Ground 标志**。
    ///
    ///   **起跳是干净的**：`jump*` 是明确动作名，而且连段打到一半**不会再跳** → 零误伤。
    /// </summary>
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgJumpReset;
    internal const int LinkCount = 6;

    /// <summary>Order -> 我们造的那一格的 proto 指针（供 Link 用）。</summary>
    private static readonly Dictionary<int, IntPtr> _madeByOrder = new Dictionary<int, IntPtr>();

    /// <summary>本次 ApplyChain 里【当前这条链】的段号起点。
    /// 每条链分到 100 个号（Chain1 从 900 起、Chain2 从 1000 起…），
    /// 否则各链都从 900 开始，会把全局的 _madeByOrder 冲掉 —— 见 ApplyChain 的注释。</summary>
    private static int _chainOrderBase = 900;
    internal static BepInEx.Configuration.ConfigEntry<int> CfgGroup;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgAdvance;
    internal static BepInEx.Configuration.ConfigEntry<float> CfgHold;
    internal static BepInEx.Configuration.ConfigEntry<float> CfgPreInput;
    internal static BepInEx.Configuration.ConfigEntry<float> CfgTimeWin;
    internal static BepInEx.Configuration.ConfigEntry<string> CfgCloneMode;
    internal static BepInEx.Configuration.ConfigEntry<float> CfgPreInputScale;

    /// <summary>逐动作窗口覆盖。格式见 Plugin.cs 的说明。</summary>
    internal static BepInEx.Configuration.ConfigEntry<string> CfgWindowOverrides;
    /// <summary>克隆时剔除的接招窗口: "源动作:目标动作,目标动作 | ..."</summary>
    internal static BepInEx.Configuration.ConfigEntry<string> CfgDropSwitches;

    private static string _winRaw;
    private static readonly Dictionary<string, double[]> _winOv =
        new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>取某个动作的窗口覆盖 [后摇秒, 前置输入秒, 硬地板秒]，-1 = 不覆盖；没有则 null。</summary>
    private static double[] WinOv(string action)
    {
        try
        {
            var raw = CfgWindowOverrides?.Value;
            if (!ReferenceEquals(raw, _winRaw))
            {
                _winRaw = raw;
                _winOv.Clear();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    foreach (var part in raw.Split('|'))
                    {
                        var t = part.Trim();
                        if (t.Length == 0) continue;
                        int c = t.IndexOf(':');
                        if (c <= 0) continue;
                        string nm = t.Substring(0, c).Trim();
                        var vals = t.Substring(c + 1).Split(',');
                        var arr = new double[] { -1, -1, -1, -1, -1, -1 };
                        for (int i = 0; i < 6 && i < vals.Length; i++)
                        {
                            double d;
                            if (double.TryParse(vals[i].Trim(),
                                    System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out d))
                                arr[i] = d;
                        }
                        _winOv[nm] = arr;
                    }
                }
                Plugin.Log?.LogInfo($"[连段模组:窗口] 覆盖表已解析: {_winOv.Count} 条");
            }
        }
        catch (Exception e) { Once("WinOv|parse", e); }

        double[] r;
        return _winOv.TryGetValue(action, out r) ? r : null;
    }

    /// <summary>该动作此刻的速度倍率（窗口覆盖值按现实秒给，内部乘它）。</summary>
    private static float SpeedK(string action)
    {
        try
        {
            var cfg = CfgPreInputScale?.Value ?? -1f;
            if (cfg < 0f) return EsActionSpeed.SpeedOf(action);
            return cfg > 0f ? cfg : 1f;
        }
        catch { return 1f; }
    }

    private const int OFF_CHAINS = 0x30;        // PlayerSkillMgr.m_SkChains
    private const int OFF_SKILLLIST = 0x18;     // PlayerSkillChain.SkillList
    private const int OFF_SKILLACT = 0x20;      // PlayerSkill.SkillActivate
    private const int OFF_WRAP_DATA = 0x10;     // SkillActivateFixedPointWrap.data
    private const int OFF_INPUT_DIR = 0x48;     // SkillActivateFixedPoint.inputDir_
    private const int OFF_ORDER = 0x20;         // SkillActivateFixedPoint.order_
    private const int OFF_ACTION = 0x28;        // SkillActivateFixedPoint.action_
    // ⚠ SkillActivateFixedPoint 有两个同名类！代码里用的是 dump.cs:143365 那个
    //   （TypeDefIndex 1721，Q32.32 的 long 字段），
    //   不是 dump.cs:76850 那个嵌套的 `SkillActivate.Types.SkillActivateFixedPoint`（float 字段）。
    //   照着嵌套那份找偏移会全错 —— 2026-10-02 就在这上面栽了一次。
    private const int OFF_ACTDUR = 0x70;        // SkillActivateFixedPoint.actdurStrict_ (Q32.32 long)
    private const int OFF_TIMEOUT = 0x78;       // SkillActivateFixedPoint.timeout_      (Q32.32 long)
    private const int OFF_PRECD = 0xB8;         // SkillActivateFixedPoint.precheckActionCd_ (int)

    private static readonly HashSet<int> _applied = new HashSet<int>();

    /// <summary>我们造出来的 PlayerSkill 指针，用来在选中探针里比对。</summary>
    private static readonly HashSet<IntPtr> _ours = new HashSet<IntPtr>();
    private static float _lastRetryT = -1f;

    public static int Apply(Harmony harmony)
    {
        int n = 0;
        var t = AccessTools.TypeByName("GamePlay.PlayerSkillMgr");
        if (t == null) { Plugin.Log?.LogWarning("  [连段模组] 找不到 PlayerSkillMgr"); return 0; }

        try
        {
            var m = AccessTools.Method(t, "InitSkills", Type.EmptyTypes);
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(Postfix))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 PlayerSkillMgr.InitSkills");
                n++;
            }
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 InitSkills 失败: {e.Message}"); }

        // ★★★★★ 【JS 驱动连段的唯一漏斗】(2026-10-03)
        //   反汇编确认：FindAndStartSkillFromMidByOrder 的调用者只有两处 ——
        //       PlayerSkillChain_Wrap::M_FindAndStartSkillFromMidByOrder   (JsPort 桥)
        //       ActorJs_hz.<>c.<b__12_4>                                     (ES 角色 JS)
        //   而 b__12_4 的逻辑（RVA 0x17343A0）是：
        //       if (BulletMgr.GetBulletsByCasterAndActionGroupAndTag(...).Count == 0)
        //           chain.FindAndStartSkillFromMidByOrder(HeroConfig.Quality + 1);
        //   也就是说：**连段推进的条件是"弹幕清空"，Order 来源是 Quality+1**，
        //   跟 preSkillOrder 那套搜索无关（那只是输入驱动的支线）。
        //   打在这个漏斗上，一次回答两件事：JS 驱动了哪些 Order / 认不认我们的 aceN_*。
        try
        {
            var ct3 = AccessTools.TypeByName("GamePlay.PlayerSkillChain");
            var m = ct3 == null ? null : AccessTools.Method(ct3, "FindAndStartSkillFromMidByOrder");
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(MidByOrderPostfix))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 FindAndStartSkillFromMidByOrder (JS驱动漏斗探针)");
                n++;
            }
            else Plugin.Log?.LogWarning("  [连段模组] 找不到 FindAndStartSkillFromMidByOrder");
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 漏斗探针 失败: {e.Message}"); }

        // ★★ 搜索调用计数器（交叉验证用）—— 上次"入口动 41 次、搜索只记 1 次"无法判断真假，
        //   这个计数器就是第三方证据：入口次数与搜索次数应当同量级。
        try
        {
            var ct4 = AccessTools.TypeByName("GamePlay.PlayerSkillChain");
            foreach (var pair in new[] {
                new[] { "findNextSkillMatchPreOrderAndInputDir", "主搜索" },
                new[] { "findStartingSkillMatchInputDir", "兜底搜索" } })
            {
                var m = ct4 == null ? null : AccessTools.Method(ct4, pair[0]);
                if (m == null) continue;
                harmony.Patch(m, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(SearchCountPrefix))));
                Plugin.Log.LogInfo($"  [连段模组] 已挂钩 {pair[0]} (调用计数器)");
                n++;
            }
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 计数器 失败: {e.Message}"); }

        // ★★★★★ 【真正的汇点】SkillStartImplement —— 三条起招路径全到这里。
        //   上一轮挂在 PlayerSkillChain::StartSkill 上，只捕到路径 B（空中动作）；
        //   地面布鲁诺走 JS 桥（路径 C）绕开了它。这里才是唯一不分来路的观察点。
        try
        {
            var mss = AccessTools.Method(t, "SkillStartImplement");
            if (mss != null)
            {
                harmony.Patch(mss, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(StartImplChainProbe))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 SkillStartImplement (汇点探针: 目标+链)");
                n++;
            }
            else Plugin.Log?.LogWarning("  [连段模组] 找不到 SkillStartImplement（汇点探针不可用）");
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 汇点探针 失败: {e.Message}"); }

        // ★★ 动作表注入：必须先于"游戏按名字查动作表"。
        //   ActionMgr.ChangeAction(string) 就是那个按名解析的地方，挂 prefix。
        try
        {
            var at = AccessTools.TypeByName("GamePlay.ActionMgr");
            var m = at == null ? null : AccessTools.Method(at, "ChangeAction", new[] { typeof(string) });
            if (m != null)
            {
                harmony.Patch(m, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(InjectPrefix))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 ActionMgr.ChangeAction(prefix) (合成动作注入)");
                n++;
            }
            else Plugin.Log?.LogWarning("  [连段模组] 找不到 ActionMgr.ChangeAction(string)");
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 ChangeAction 注入失败: {e.Message}"); }

        // NotifyActionChanged 单独一份 postfix —— 它带参数，正好用来观察【链推进到哪了】
        try
        {
            var m = AccessTools.Method(t, "NotifyActionChanged");
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(NotifyPostfix))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 PlayerSkillMgr.NotifyActionChanged (推进探针)");
                n++;
            }
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 NotifyActionChanged 失败: {e.Message}"); }

        // ★ 链推进的真正闸门：SkillChangePreCall(psk, chain) -> bool
        //   Cur 卡在 index 8 不动 = 游戏拒绝了"切到下一段"。这里看它拒哪个、返回值是什么。
        try
        {
            var m = AccessTools.Method(t, "SkillChangePreCall");
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(PreCallPostfix))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 PlayerSkillMgr.SkillChangePreCall (段闸门探针)");
                n++;
            }
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 SkillChangePreCall 失败: {e.Message}"); }

        try
        {
            var m = AccessTools.Method(t, "checkSkillValid");
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(ValidPostfix))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 PlayerSkillMgr.checkSkillValid (行合法性探针)");
                n++;
            }
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 checkSkillValid 失败: {e.Message}"); }

        // ---- 观测: 候选循环里唯一没有日志的一道过滤 + 选段结果 ----
        try
        {
            var ct0 = AccessTools.TypeByName("GamePlay.PlayerSkillChain");
            var mNext = ct0 != null ? AccessTools.Method(ct0, "findNextSkillMatchPreOrderAndInputDir") : null;
            if (mNext != null)
            {
                harmony.Patch(mNext,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(EsComboChain), nameof(MarkSearchPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(EsComboChain), nameof(NextSkillPostfix))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 findNextSkillMatchPreOrderAndInputDir (选段探针)");
                n++;
            }
            var mStart = ct0 != null ? AccessTools.Method(ct0, "findStartingSkillMatchInputDir") : null;
            if (mStart != null)
            {
                harmony.Patch(mStart,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(EsComboChain), nameof(MarkSearchPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(EsComboChain), nameof(StartSkillPostfix))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 findStartingSkillMatchInputDir (兜底路径探针)");
                n++;
            }
            foreach (var cn in new[] { "ClearCurSkill", "Reset" })
            {
                var mc = ct0 != null ? AccessTools.Method(ct0, cn) : null;
                if (mc == null) continue;
                harmony.Patch(mc, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(ClearCurPrefix))));
                Plugin.Log.LogInfo($"  [连段模组] 已挂钩 PlayerSkillChain.{cn} (清游标探针)");
                n++;
            }

            // ★ 清游标的【另一条路】—— 2026-10-02 日志证明:
            //   链被清空时 `PlayerSkillChain.ClearCurSkill/Reset` **一次都没被调用**,
            //   但 `Cur.Skill` 确实变成了 null(`[兜底] 当前 -1("")` 出现 5 次)。
            //   剩下的可能只有【游标自己】的 Reset —— 即 `PlayerSkillChainCurSkill.Reset`。
            var curT = AccessTools.TypeByName("GamePlay.PlayerSkillChainCurSkill");
            var mCurReset = curT != null ? AccessTools.Method(curT, "Reset") : null;
            if (mCurReset != null)
            {
                harmony.Patch(mCurReset, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(CurSkillResetPrefix))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 PlayerSkillChainCurSkill.Reset (游标重置探针)");
                n++;
            }
            else Plugin.Log?.LogWarning("  [连段模组] 找不到 PlayerSkillChainCurSkill.Reset");

            var am = AccessTools.TypeByName("GamePlay.ActionMgr");
            var mCd = am != null ? AccessTools.Method(am, "CheckCanChangeToAction") : null;
            if (mCd != null)
            {
                harmony.Patch(mCd, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(CdScopedPrefix))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 ActionMgr.CheckCanChangeToAction (带作用域的CD放行)");
                n++;
            }
            else Plugin.Log?.LogWarning("  [连段模组] 找不到 ActionMgr.CheckCanChangeToAction");

            var pu = AccessTools.TypeByName("GamePlay.PlayerSkillUtility");
            var mCast = pu != null ? AccessTools.Method(pu, "CheckSkillCanCast") : null;
            if (mCast != null)
            {
                harmony.Patch(mCast, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(CanCastPostfix))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 CheckSkillCanCast (过滤探针)");
                n++;
            }
            else Plugin.Log?.LogWarning("  [连段模组] 找不到 PlayerSkillUtility.CheckSkillCanCast");
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂选段/过滤探针失败: {e.Message}"); }

        // ★★ 真正决定"下一段是谁"的地方 —— 动作层(ChangeSkill)到这里来问技能系统。
        //    普攻键按下 → 动作的 ActionSwitchs 命中 ChangeSkill → 调这两个之一。
        try
        {
            var m = AccessTools.Method(t, "ChangeSkillByActionName");
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(ChangeSkillPostfix))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 ChangeSkillByActionName (选招入口)");
                n++;
            }
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 ChangeSkillByActionName 失败: {e.Message}"); }

        try
        {
            var m = AccessTools.Method(t, "FindSkillAndChainByActionName");
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(FindSkillPostfix))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 FindSkillAndChainByActionName (按名查招)");
                n++;
            }
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 FindSkillAndChainByActionName 失败: {e.Message}"); }

        // ★★★ 链推进的真身：PlayerSkillChain 是【按 Order 找下一段】，不是按索引。
        //    看它每次传进来的 startOrder 是多少、找没找到 —— 这决定了我们该给新段编什么 Order。
        var ct = AccessTools.TypeByName("GamePlay.PlayerSkillChain");
        if (ct != null)
        {
            foreach (var pair in new[] {
                new[] { "findAndStartSkill_Imp", "按Order找" },
                new[] { "FindAndStartSkillFromMidByOrder", "按Order接续" } })
            {
                try
                {
                    var m = AccessTools.Method(ct, pair[0]);
                    if (m == null) { Plugin.Log?.LogWarning($"  [连段模组] 找不到 PlayerSkillChain.{pair[0]}"); continue; }
                    harmony.Patch(m, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(EsComboChain), nameof(ChainFindPostfix))));
                    Plugin.Log.LogInfo($"  [连段模组] 已挂钩 PlayerSkillChain.{pair[0]} ({pair[1]})");
                    n++;
                }
                catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 {pair[0]} 失败: {e.Message}"); }
            }
        }
        else Plugin.Log?.LogWarning("  [连段模组] 找不到 GamePlay.PlayerSkillChain");

        // ★★★★ 最后的候选：每帧的"输入检查"，返回值语义就是"这次输入接上了"。
        //   已经排掉的六个候选入口都不在路径上，那"按了攻击键 -> 决定切哪段"只可能在这里。
        if (ct != null)
        {
            // ⚠ 只挂返回 bool 的那两个。`DoUpdate(Fp dt)` 返回 void，
            //   和这个 postfix 的 `bool __result` 对不上 → Harmony "IL Compile Error"。
            // ★ StartSkill 单独一份全量探针（原先把它们合在一起、还带节流，漏掉了大部分调用）
            try
            {
                var ms = AccessTools.Method(ct, "StartSkill");
                if (ms != null)
                {
                    harmony.Patch(ms, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(EsComboChain), nameof(StartSkillProbe))));
                    Plugin.Log.LogInfo("  [连段模组] 已挂钩 PlayerSkillChain.StartSkill (全量起招探针)");
                    n++;
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 StartSkill 全量探针 失败: {e.Message}"); }

            // 来路标记：给两个上游入口挂 prefix，StartSkill 时读标记
            try
            {
                var mB = AccessTools.Method(t, "ChangeSkillByActionName");
                if (mB != null)
                {
                    harmony.Patch(mB, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(EsComboChain), nameof(ViaChangePrefix))));
                    Plugin.Log.LogInfo("  [连段模组] 已挂钩 ChangeSkillByActionName(prefix) (来路标记B)");
                    n++;
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 来路标记B 失败: {e.Message}"); }
            try
            {
                var wt = AccessTools.TypeByName("GamePlay.PlayerSkillChain_Wrap");
                var mC = wt == null ? null : AccessTools.Method(wt, "M_StartSkill");
                if (mC != null)
                {
                    harmony.Patch(mC, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(EsComboChain), nameof(ViaJsPrefix))));
                    Plugin.Log.LogInfo("  [连段模组] 已挂钩 PlayerSkillChain_Wrap.M_StartSkill(prefix) (来路标记C)");
                    n++;
                }
                else Plugin.Log?.LogWarning("  [连段模组] 找不到 PlayerSkillChain_Wrap.M_StartSkill（来路C标记不可用）");
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 来路标记C 失败: {e.Message}"); }

            foreach (var mn in new[] { "DoUpdateAndCheckInputSucc" })
            {
                try
                {
                    var m = AccessTools.Method(ct, mn);
                    if (m == null) { Plugin.Log?.LogWarning($"  [连段模组] 找不到 PlayerSkillChain.{mn}"); continue; }
                    harmony.Patch(m, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(EsComboChain), nameof(ChainUpdatePostfix))));
                    Plugin.Log.LogInfo($"  [连段模组] 已挂钩 PlayerSkillChain.{mn} (输入推进探针)");
                    n++;
                }
                catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 {mn} 失败: {e.Message}"); }
            }
        }

        // ★ 决定性探针：游戏真正选中要执行的是【哪一个 PlayerSkill】。
        //   校验已经证明我们改的克隆体是对的，那问题只剩"游戏读的不是它"。
        //   SkillStartImplement(PlayerSkill psk, ...) 拿到的就是被选中的那一个 ——
        //   把它的指针和我们造的那批比对，立刻知道我们的段有没有被走。
        try
        {
            var m = AccessTools.Method(t, "SkillStartImplement");
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(StartImplPostfix))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 PlayerSkillMgr.SkillStartImplement (选中探针)");
                n++;
            }
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 SkillStartImplement 失败: {e.Message}"); }

        Plugin.Log.LogInfo($"  [连段模组] 生效配置: Enabled={CfgEnabled?.Value} 组={CfgGroup?.Value} " +
                           $"序列=[{CfgSequence?.Value}]");
        return n;
    }

    public static void Postfix(GamePlay.PlayerSkillMgr __instance)
    {
        try
        {
            // ★★★ 还原检查必须在【所有提前 return 之前】。
            //   本项目第三次栽在这个形状上了（EsActionSpeed 的 ClearInjection 也是）：
            //   关掉开关只是"不再注入"，**已经注入的东西不会自己消失**。
            //   因为把 `if (CfgEnabled != true) return;` 写在最前面，
            //   关闭时从这里就出去了，还原代码永远执行不到 ——
            //   表现就是"我关了 Rewrite 还是鬼畜连段"。
            if (CfgEnabled?.Value != true) { Revert(__instance); return; }
            if (__instance == null) return;
            var o = __instance.Owner;
            if (o == null || !DashInvincible.IsLocalPlayerActor(o)) return;

            // 链会被重建（换潜能/换装备），所以每次动作变化都检查一遍。
            // 但别每帧都去拆装 —— 加个节流。
            float now = Now();
            if (now - _lastRetryT < 0.5f) return;
            _lastRetryT = now;

            ApplyChain(__instance);
        }
        catch (Exception e) { Once("Postfix", e); }
    }

    /// <summary>动作变化时：看链的【当前位置指针 Cur.Skill】落在哪一段。
    /// 一直停在 index 0 = 链没推进，这才是"全 attack1"的直接原因。</summary>
    public static void NotifyPostfix(GamePlay.PlayerSkillMgr __instance,
                                     GamePlay.GameActionLogic actionOld, GamePlay.GameActionLogic actionCur)
    {
        try
        {
            // ★ 同上：还原检查放最前，且不受节流影响
            if (CfgEnabled?.Value != true) { Revert(__instance); return; }
            if (__instance == null) return;
            var o = __instance.Owner;
            if (o == null || !DashInvincible.IsLocalPlayerActor(o)) return;

            // ★★★ 复刻原版的【空中平A永远从空1开始】规则（2026-10-04）。
            //
            // 原版实测: 空1 之后【不】跟空2；空1 → 落地 → 起跳 → 仍是空1；
            //   空1 → 空中跳跃 → 仍是空1。空2 只能靠上挑进 —— 是一条**主动规则**。
            // 我们往空中链插了克隆段（原地替换占了下标 13/14/15），数组推进有了落脚点，
            // 于是"落地-起跳-接着往下打"。
            //
            // 判据用【起跳】而不是【落地】：
            //   · 落地 —— `drop*`/`fallmdown*` 在空中也会出现 → **必然误伤**（实测把空4掐掉）
            //   · 起跳 —— `jump*` 名字明确，且连段打到一半不会再跳 → **零误伤**
            // ⚠ 【延后一拍】—— 这是关键，不能见到 jump* 就立刻重置。
            //
            //   实测: 上挑的输入序列也是 `jump* → AttackUp*`（日志里出现过多次），
            //   而"普通跳"和"上挑的跳"在【跳到那一刻】动作名完全一样，分不出来。
            //   所以见到 jump* 先只挂起一个标记，**等下一个动作**再决定：
            //     · 下一个是 AttackUp*（上挑）→ 取消，**不重置**（否则上挑接不上空2）
            //     · 下一个是别的            → 确实是一次普通跳 → 重置
            //   `jump → AttackUp` 是确定的序列，所以这个判据没有歧义。
            try
            {
                string cur = null;
                try { cur = actionCur?.Name; } catch { }

                if (_pendingJumpReset)
                {
                    _pendingJumpReset = false;
                    if (!IsUpAttackAction(cur)) ClearAirCursor(__instance);
                }
                if (CfgJumpReset?.Value == true && IsJumpAction(cur)) _pendingJumpReset = true;
            }
            catch (Exception e) { Once("起跳重置", e); }

            if (_probeLeft > 0)
            {
                _probeLeft--;
                string an = ""; try { an = actionCur?.Name ?? ""; } catch { }
                string ao = ""; try { ao = actionOld?.Name ?? ""; } catch { }

                IntPtr arr = ReadPtr(__instance.Pointer, OFF_CHAINS);
                int g = CfgGroup?.Value ?? 1;
                IntPtr chain = arr == IntPtr.Zero ? IntPtr.Zero : ReadPtr(arr, 0x20 + g * 8);
                IntPtr curSkill = IntPtr.Zero;
                int idx = -1, cnt = 0;
                if (chain != IntPtr.Zero)
                {
                    // Cur @0x28 → PlayerSkillChainCurSkill.Skill @0x10
                    curSkill = ReadPtr(ReadPtr(chain, 0x28), 0x10);
                    var list = GetList(chain);
                    cnt = SafeCount(list);
                    for (int i = 0; i < cnt; i++)
                    {
                        var sk = TryGet(list, i);
                        if (sk != null && sk.Pointer == curSkill) { idx = i; break; }
                    }
                }
                Plugin.Log?.LogInfo($"[连段模组:推进] \"{ao}\" -> \"{an}\"   链[{g}] 段数={cnt} " +
                                    $"Cur指针=0x{curSkill.ToInt64():X} 落在 index={idx} 属于我们={_ours.Contains(curSkill)}");
            }

            // 之后再考虑补丁（放到探针后面，避免它把 Cur 重置掉影响观察）
            float now = Now();
            if (now - _lastRetryT < 0.5f) return;
            _lastRetryT = now;
            ApplyChain(__instance);
        }
        catch (Exception e) { Once("NotifyPost", e); }
    }

    private static int _probeLeft = 40;

    /// <summary>切段闸门：游戏要不要允许换到这一招。</summary>
    public static void PreCallPostfix(GamePlay.PlayerSkillMgr __instance, GamePlay.PlayerSkill psk, bool __result)
    {
        try
        {
            if (CfgEnabled?.Value != true || psk == null) return;
            var o = __instance?.Owner;
            if (o == null || !DashInvincible.IsLocalPlayerActor(o)) return;
            if (_gateLeft-- <= 0) return;

            IntPtr pp = psk.Pointer;
            IntPtr proto = ReadPtr(ReadPtr(pp, OFF_SKILLACT), OFF_WRAP_DATA);
            Plugin.Log?.LogInfo($"[连段模组:段闸门] SkillChangePreCall -> {__result}  " +
                                $"psk=0x{pp.ToInt64():X} 我们造的={_ours.Contains(pp)}  " +
                                $"Action=\"{MStr(proto, OFF_ACTION)}\" Order={ReadI32(proto, OFF_ORDER)} " +
                                $"InputDir={ReadI32(proto, OFF_INPUT_DIR)}");
        }
        catch (Exception e) { Once("PreCall", e); }
    }

    private static int _gateLeft = 60;

    /// <summary>行合法性：这一行技能配置本身能不能用（MP/CD/地面空中/触发条件…）。</summary>
    public static void ValidPostfix(object __instance, object skact, bool __result)
    {
        try
        {
            if (CfgEnabled?.Value != true || __result) return;   // 只看失败
            if (_validLeft-- <= 0) return;
            string a = ""; int ord = -1, dir = -1;
            try
            {
                var t = skact?.GetType();
                var pa = t?.GetProperty("Action"); a = pa?.GetValue(skact)?.ToString() ?? "";
                var po = t?.GetProperty("Order"); ord = po == null ? -1 : Convert.ToInt32(po.GetValue(skact));
                var pd = t?.GetProperty("InputDir"); dir = pd == null ? -1 : Convert.ToInt32(pd.GetValue(skact));
            }
            catch { }
            Plugin.Log?.LogInfo($"[连段模组:行合法性] checkSkillValid -> False  \"{a}\" Order={ord} InputDir={dir}");
        }
        catch { }
    }

    private static int _validLeft = 40;

    /// <summary>选招入口：动作层问"该切哪一招"。</summary>
    /// <summary>⚠ 形参必须和方法签名严格一致 ——
    /// 上一版多写了 `chain` / `skill` 两个不存在的参数，Harmony 直接报 "IL Compile Error"，
    /// 结果这个探针从头到尾没工作过（又是"探针静默 = 假阴性"）。</summary>
    public static void ChangeSkillPostfix(GamePlay.PlayerSkillMgr __instance, string actionName,
                                          GamePlay.PlayerSkillGroup skGroupReq, bool __result)
    {
        try
        {
            if (CfgEnabled?.Value != true || __instance == null) return;
            var o = __instance.Owner;
            if (o == null || !DashInvincible.IsLocalPlayerActor(o)) return;
            if (_pickLeft-- <= 0) return;

            Plugin.Log?.LogInfo($"[连段模组:选招:ChangeSkill] \"{actionName}\" 组={skGroupReq} -> {__result}");
        }
        catch (Exception e) { Once("ChangeSkill", e); }
    }

    public static void FindSkillPostfix(GamePlay.PlayerSkillMgr __instance, string actionName,
                                        bool __result, GamePlay.PlayerSkillChain skChain, GamePlay.PlayerSkill skill)
    {
        try
        {
            if (CfgEnabled?.Value != true || __instance == null) return;
            var o = __instance.Owner;
            if (o == null || !DashInvincible.IsLocalPlayerActor(o)) return;
            if (_pickLeft-- <= 0) return;

            Describe("FindSkill", $"\"{actionName}\" -> {__result}", skChain, skill);
        }
        catch (Exception e) { Once("FindSkill", e); }
    }

    /// <summary>把"选了哪一段、在链里第几位、Cur 又在第几位"一次打全。</summary>
    private static void Describe(string tag, string head, GamePlay.PlayerSkillChain chain, GamePlay.PlayerSkill skill)
    {
        IntPtr cp = chain?.Pointer ?? IntPtr.Zero;
        int cnt = 0, curIdx = -1, pickIdx = -1;
        string curAct = "", pickAct = "";
        if (cp != IntPtr.Zero)
        {
            var list = GetList(cp);
            cnt = SafeCount(list);
            IntPtr curSkill = ReadPtr(ReadPtr(cp, 0x28), 0x10);
            IntPtr pickPtr = skill?.Pointer ?? IntPtr.Zero;
            for (int i = 0; i < cnt; i++)
            {
                var sk = TryGet(list, i);
                if (sk == null) continue;
                if (sk.Pointer == curSkill) { curIdx = i; curAct = ActionOf(sk) ?? ""; }
                if (pickPtr != IntPtr.Zero && sk.Pointer == pickPtr) { pickIdx = i; pickAct = ActionOf(sk) ?? ""; }
            }
        }
        Plugin.Log?.LogInfo($"[连段模组:选招:{tag}] {head}   链段数={cnt} " +
                            $"Cur@{curIdx}(\"{curAct}\")  选中@{pickIdx}(\"{pickAct}\")");
    }

    private static int _pickLeft = 60;

    /// <summary>按 Order 找段的入口。看它传什么 startOrder、结果如何、以及找到的是哪一段。</summary>
    public static void ChainFindPostfix(GamePlay.PlayerSkillChain __instance, int startOrder, bool __result)
    {
        try
        {
            if (CfgEnabled?.Value != true || __instance == null) return;
            var o = SkillChainDump_OwnerOf(__instance);
            if (o == null || !DashInvincible.IsLocalPlayerActor(o)) return;
            if (_chainFindLeft-- <= 0) return;

            IntPtr cp = __instance.Pointer;
            var list = GetList(cp);
            int cnt = SafeCount(list);
            IntPtr curSkill = ReadPtr(ReadPtr(cp, 0x28), 0x10);
            int curIdx = -1, curOrd = -1;
            string curAct = "";
            for (int i = 0; i < cnt; i++)
            {
                var sk = TryGet(list, i);
                if (sk == null) continue;
                if (sk.Pointer == curSkill)
                {
                    curIdx = i;
                    IntPtr pr = ReadPtr(sk.SkillActivate?.Pointer ?? IntPtr.Zero, OFF_WRAP_DATA);
                    curOrd = ReadI32(pr, OFF_ORDER); curAct = MStr(pr, OFF_ACTION);
                    break;
                }
            }
            // 链里所有段的 Order 一览，方便一眼看出"下一段该是哪个"
            var ords = new List<string>();
            for (int i = 0; i < cnt && i < 24; i++)
            {
                var sk = TryGet(list, i);
                if (sk == null) { ords.Add("?"); continue; }
                IntPtr pr = ReadPtr(sk.SkillActivate?.Pointer ?? IntPtr.Zero, OFF_WRAP_DATA);
                ords.Add(ReadI32(pr, OFF_ORDER) + ":" + MStr(pr, OFF_ACTION));
            }
            Plugin.Log?.LogInfo($"[连段模组:按Order找] startOrder={startOrder} -> {__result}   " +
                                $"Cur@{curIdx}(Order={curOrd} \"{curAct}\")\n" +
                                $"      链: {string.Join("  ", ords.ToArray())}");
        }
        catch (Exception e) { Once("ChainFind", e); }
    }

    private static int _chainFindLeft = 40;

    /// <summary>每帧输入检查 / 起手。按 (方法名|结果|Cur段名) 配对去重，
    /// 新组合永远打得出来，老的每 300 次提醒一次 —— 不设总条数上限（那条路栽过太多次）。</summary>
    public static void ChainUpdatePostfix(GamePlay.PlayerSkillChain __instance, object[] __args,
                                          System.Reflection.MethodBase __originalMethod, bool __result)
    {
        try
        {
            if (CfgEnabled?.Value != true || __instance == null) return;
            var o = SkillChainDump_OwnerOf(__instance);
            if (o == null || !DashInvincible.IsLocalPlayerActor(o)) return;

            IntPtr cp = __instance.Pointer;
            var list = GetList(cp);
            int cnt = SafeCount(list);
            IntPtr curSkill = ReadPtr(ReadPtr(cp, 0x28), 0x10);
            int idx = -1; string act = "";
            for (int i = 0; i < cnt; i++)
            {
                var sk = TryGet(list, i);
                if (sk != null && sk.Pointer == curSkill) { idx = i; act = ActionOf(sk) ?? ""; break; }
            }
            string who = __originalMethod?.Name ?? "?";
            string key = who + "|" + __result + "|" + idx + "|" + act;
            _updPairs.TryGetValue(key, out int c);
            _updPairs[key] = ++c;
            if (c != 1 && c % 300 != 0) return;

            // ★ 把 DoUpdateAndCheckInputSucc 的三个"直接 return false"闸门读出来：
            //   反汇编（RVA 0x1BB10F0）确认它开头就是
            //       Status(+0x20) == 4 或 == 5   -> 拒
            //       Fp([+0x54]) > 0              -> 拒     ← 链上的计时器（疑似冷却）
            //       TimeEllaps < ActdurStrict    -> 拒     （硬地板没到）
            //   "输入全 False 但不是没按"必须区分是哪一个：不看这三个数就只能猜。
            string gate = "";
            try
            {
                int st = ReadI32(__instance.Pointer, 0x20);
                long cd = Marshal.ReadInt64(__instance.Pointer, 0x54);
                gate = $" 闸门[Status={st} CD={cd / 4294967296.0:F3}]";
            }
            catch { }
            Plugin.Log?.LogInfo($"[连段模组:输入推进] {who} -> {__result}  链段数={cnt} " +
                                $"Cur@{idx}(\"{act}\"){gate}  (第 {c} 次)");
        }
        catch (Exception e) { Once("ChainUpdate", e); }
    }

    private static readonly Dictionary<string, int> _updPairs = new Dictionary<string, int>();

    /// <summary>PlayerSkillChain 拿不到 Owner 属性时，从 mgr 反查（这里用 Owner 字段 0x18 指回 mgr）。</summary>
    private static GamePlay.PlayerObj SkillChainDump_OwnerOf(GamePlay.PlayerSkillChain chain)
    {
        try
        {
            // PlayerSkillChain.Owner(0x10) = PlayerSkillMgr；PlayerSkillMgr.Owner(0x10) = PlayerObj
            IntPtr mgr = ReadPtr(chain.Pointer, 0x10);
            if (mgr == IntPtr.Zero) return null;
            IntPtr pl = ReadPtr(mgr, 0x10);
            if (pl == IntPtr.Zero) return null;
            return new GamePlay.PlayerObj(pl);
        }
        catch { return null; }
    }

    /// <summary>游戏选中了哪个 PlayerSkill 去执行。和造出来的那批比指针。</summary>
    public static void StartImplPostfix(GamePlay.PlayerSkillMgr __instance, GamePlay.PlayerSkill psk)
    {
        try
        {
            if (CfgEnabled?.Value != true || psk == null) return;
            var o = __instance?.Owner;
            if (o == null || !DashInvincible.IsLocalPlayerActor(o)) return;
            if (_startLogLeft-- <= 0) return;

            IntPtr pp = psk.Pointer;
            string act = MStr(ReadPtr(pp, OFF_SKILLACT) != IntPtr.Zero
                              ? ReadPtr(ReadPtr(pp, OFF_SKILLACT), OFF_WRAP_DATA) : IntPtr.Zero, OFF_ACTION);
            int ord = ReadI32(ReadPtr(ReadPtr(pp, OFF_SKILLACT), OFF_WRAP_DATA), OFF_ORDER);
            bool mine = _ours.Contains(pp);

            Plugin.Log?.LogInfo($"[连段模组:选中] psk=0x{pp.ToInt64():X} 是我们造的={mine}  " +
                                $"读到的Action=\"{act}\" Order={ord}");

            // ★★★ 直接推进 Cur —— 不再去找"游戏在哪推进"。
            //   四个候选入口（ChangeSkillByActionName / FindSkillAndChainByActionName /
            //   findAndStartSkill_Imp / FindAndStartSkillFromMidByOrder）实测【全都没有被调用】，
            //   说明推进发生在我们看不到的地方。那就别找了：
            //   既然游戏确实选中了我们第 N 段，那就在它起手之后，
            //   手动把链的当前位置 Cur 指到第 N+1 段 —— 下一次按键自然就接到下一段。
            if (mine && CfgAdvance?.Value == true) AdvanceCur(psk);
        }
        catch (Exception e) { Once("StartImpl", e); }
    }

    /// <summary>把链的当前位置 Cur.Skill 手动挪到该段的下一个。</summary>
    private static void AdvanceCur(GamePlay.PlayerSkill psk)
    {
        // ⚠ 每个 return 都要说明原因。上一版这几个静默 return 让整轮日志一片空白，
        //   我又得靠猜 —— 这正是"探针不留痕 = 制造假阴性"的老毛病。

        try
        {
            var chain = psk.Owner;                        // PlayerSkill.Owner @0x10
            IntPtr cp = chain?.Pointer ?? IntPtr.Zero;
            if (cp == IntPtr.Zero) { Bail("psk.Owner 为空(自己 new 的段没填 Owner)"); return; }
            var list = GetList(cp);
            int cnt = SafeCount(list);
            if (cnt <= 0) { Bail("链的 SkillList 为空"); return; }

            int idx = -1;
            for (int i = 0; i < cnt; i++)
            {
                var sk = TryGet(list, i);
                if (sk != null && sk.Pointer == psk.Pointer) { idx = i; break; }
            }
            if (idx < 0) { Bail($"这段不在它自己的链里(链 {cnt} 段都找过)"); return; }

            int next = idx + 1;
            if (next >= cnt)
            {
                // 到了链尾：回到第一段（普攻循环），而不是卡死
                next = 0;
                for (int i = 0; i < cnt; i++)
                {
                    var sk = TryGet(list, i);
                    if (sk != null && _ours.Contains(sk.Pointer)) { next = i; break; }
                }
            }

            var nx = TryGet(list, next);
            if (nx == null) { Bail($"取不到下一段 index={next}"); return; }

            IntPtr curObj = ReadPtr(cp, 0x28);            // PlayerSkillChainCurSkill
            if (curObj == IntPtr.Zero) { Bail("chain+0x28 (Cur) 为空"); return; }
            Marshal.WriteIntPtr(curObj, 0x10, nx.Pointer); // Cur.Skill @0x10

            if (_advLogLeft > 0)
            {
                _advLogLeft--;
                Plugin.Log?.LogInfo($"[连段模组:手动推进] Cur: index {idx}(\"{ActionOf(psk)}\") " +
                                    $"-> {next}(\"{ActionOf(nx)}\")");
            }
        }
        catch (Exception e) { Once("AdvanceCur", e); }
    }

    private static int _advLogLeft = 30;
    private static int _ownerLogLeft = 12;
    private static int _bailLogLeft = 20;

    private static void Bail(string reason)
    {
        if (_bailLogLeft-- <= 0) return;
        Plugin.Log?.LogInfo($"[连段模组:手动推进] 放弃: {reason}");
    }

    private static int _startLogLeft = 30;

    // ------------------------------------------------------------------ 原生值查表

    /// <summary>动作名 -&gt; 原生技能行（用来照抄衔接相关的三个字段）。</summary>
    private static readonly Dictionary<string, IntPtr> _native = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);

    /// <summary>动作名 → 它的原生技能行属于【哪条链(组)】。用来判断"这个名字会不会被别的链认领"。</summary>
    private static readonly Dictionary<string, int> _nativeGroup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>已经写了合成名的段：proto 指针 → 合成名。
    /// 注入失败时要靠它把名字改回去 —— 否则段会指着一个动作表里不存在的名字，
    /// 表现是"这一按完全没反应"（实测就是这么翻车的）。</summary>
    private static readonly Dictionary<IntPtr, string> _synthApplied = new Dictionary<IntPtr, string>();

    /// <summary>合成动作名 → 它替换掉的那条平A的动作名（用来借用宿主的连锁冷却表）。</summary>
    private static readonly Dictionary<string, string> _synthHost = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>合成动作名 → 源动作名。见 SynthName 的说明。</summary>
    private static readonly Dictionary<string, string> _synth = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>"源动作|Order" → 合成动作名。**名字唯一性的依据**，见 SynthName 的说明。</summary>
    private static readonly Dictionary<string, string> _synthBySeg =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>确认过"里面没有我们的源动作"的表 —— 那不是目标表（每个角色/模型都有一张），
    /// 直接拉黑不再扫描。★ 关键：这种表上**绝对不能触发回滚**，见 EnsureInjected 的注释。</summary>
    private static readonly HashSet<IntPtr> _foreignGroups = new HashSet<IntPtr>();

    /// <summary>ActionLogicGroup 原生指针 → 已经注入进去的合成动作名集合。
    /// ★ 记"名字"而不是"组是否处理过"：运行时改配置会往序列里加新动作，
    ///   那时组早就标记过了，只记布尔值就再也注不进去（新段查不到动作表 → 静默不出招）。</summary>
    private static readonly Dictionary<IntPtr, HashSet<string>> _injected =
        new Dictionary<IntPtr, HashSet<string>>();

    /// <summary>★ 必须 root 住克隆出来的 GameActionLogic ——
    /// 只把指针塞进原生 List/Dictionary 的话，托管侧没有引用，
    /// GC 一跑就会把这个原生对象收走，然后游戏访问到野指针。
    /// 这是本项目的老坑（见 PROJECT_STATE 的"GC 回收原生对象"），这次提前防住。</summary>
    private static readonly List<GamePlay.GameActionLogic> _injectedRoots = new List<GamePlay.GameActionLogic>();

    private static int _synthSeq;
    private static int _synthLogLeft = 12;

    /// <summary>被我们重写的那几个【原生平A槽位】的动作名。
    /// 只有这几个动作是"真正的平A动作"，可以直接照抄原生行；
    /// 其它任何动作（佩利诺尔 / 空中 / 布鲁诺 / 纹章解放 …）一律克隆成独有的"新平A"动作。</summary>
    private static readonly HashSet<string> _hostSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>这个动作要不要克隆成"新平A动作"。
    ///
    /// ★ 2026-10-02 规范化：以前是"只在动作名会跟别的链碰撞时才克隆"（启发式），
    ///   于是佩利诺尔(attackD2)、atkAirX 这类**同链内**的动作仍然是照抄原生行 ——
    ///   照抄就意味着连它的"链[1] 之外的那部分假设"也一起抄了进来，
    ///   我们前面四个卡点（Action 认领 / PrecheckActionCd / Timeout / Input）全是这个来源。
    ///   现在改成：**只有原生平A四段照抄，其余一律克隆**，规则单一、不再看运气。</summary>
    private static string CloneMode => (CfgCloneMode?.Value ?? "All").Trim();

    private static bool NeedClone(string action)
    {
        switch (CloneMode.ToLowerInvariant())
        {
            case "collision":
                // 最早的行为：只在动作名会跟别的链碰撞时才克隆
                return _nativeGroup.TryGetValue(action, out var g) && g != (CfgGroup?.Value ?? 1);
            case "excepthost":
                // 上一版：非平A槽位的动作克隆，原生平A四段保留原名
                return !_hostSlots.Contains(action);
            default:
                return true;   // All: 整条链全部克隆
        }
    }

    /// <summary>我们造的动作名 -> 它的源动作名（ace1_902 -> attackD2）。不是我们造的返回 null。</summary>
    internal static string SourceOf(string effName)
    {
        try
        {
            string v;
            return (!string.IsNullOrEmpty(effName) && _synth.TryGetValue(effName, out v)) ? v : null;
        }
        catch { return null; }
    }

    /// <summary>这个动作名是不是【被我们劫持了源名的原版动作】。
    ///
    /// 我们把 attackD2 复制成了 ace1_902 挂进平A链；原版那个 attackD2 还在。
    /// 配置里写 attackD2 指的是【我们的段】—— 所以原版那个必须被挡掉，
    /// 否则变速/窗口会串到原版佩利诺尔上（实测就是这么翻车的）。</summary>
    internal static bool IsHijackedSource(string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name) || _synth.Count == 0) return false;
            foreach (var kv in _synth)
                if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>这个名字是不是我们造的"新平A动作"。给动作变速模块用。</summary>
    internal static bool IsOurs(string action)
    {
        try { return !string.IsNullOrEmpty(action) && _synth.ContainsKey(action); } catch { return false; }
    }

    /// <summary>把 m_SkChains 里【所有链】的每个技能行按动作名登记下来。</summary>
    private static void BuildNativeLookup(GamePlay.PlayerSkillMgr mgr)
    {
        _native.Clear();
        _nativeGroup.Clear();
        try
        {
            IntPtr arr = ReadPtr(mgr.Pointer, OFF_CHAINS);
            if (arr == IntPtr.Zero) return;
            int n = ReadI32(arr, 0x18);
            for (int g = 0; g < n; g++)
            {
                IntPtr chain = ReadPtr(arr, 0x20 + g * 8);
                if (chain == IntPtr.Zero) continue;
                var list = GetList(chain);
                int cnt = SafeCount(list);
                for (int i = 0; i < cnt; i++)
                {
                    var sk = TryGet(list, i);
                    if (sk == null) continue;
                    IntPtr proto = ReadPtr(sk.SkillActivate?.Pointer ?? IntPtr.Zero, OFF_WRAP_DATA);
                    if (proto == IntPtr.Zero) continue;
                    // 跳过我们自己造的（Order>=900），别把自己的值当成"原生值"
                    if (ReadI32(proto, OFF_ORDER) >= 900) continue;
                    string a = MStr(proto, OFF_ACTION);
                    if (!string.IsNullOrEmpty(a) && !_native.ContainsKey(a))
                    {
                        _native[a] = proto;
                        _nativeGroup[a] = g;      // ★ 记住了"这个名字归哪条链"，SynthName 靠它判碰撞
                    }
                }
            }
            Plugin.Log?.LogInfo($"[连段模组] 原生技能行查表: 共 {_native.Count} 个动作名");
        }
        catch (Exception e) { Once("NativeLookup", e); }
    }

    // ------------------------------------------------------------------ 核心

    private static void ApplyChain(GamePlay.PlayerSkillMgr mgr)
    {
        // ★ 只关改写、保留探针（给"关掉连段改动、观察原生行为"用）
        if (CfgRewrite != null && CfgRewrite.Value != true) return;

        var specs = ChainsOrLegacy();
        if (specs.Count == 0) return;

        // ★ 必须在动手改之前扫全部链的原生行（读原生值用；改完再扫会读到自己造的东西）
        BuildNativeLookup(mgr);

        // ★★★ 每条链分到【独立的段号段】：Chain1 = 900~999，Chain2 = 1000~1099，……
        //
        //   为什么必须这样：MakeSkill 里的 orderSeq 是**每条链各自从 0 开始**的，
        //   于是每条链的段号都从 900 起 —— 游戏侧无所谓（PreSkillOrder 只在【当前链内】匹配），
        //   但我们的 _madeByOrder 是**全局**字典，两条链的 900/901/902 会互相覆盖，
        //   ApplyLinks（按 Order 接线）和 NameOfOrder（按 Order 挂弹幕）立刻全错。
        //   ⚠ 分段的后果只有一处要注意：`Order >= 900` 这个"这是我们造的"哨兵判据仍然成立。
        _chainOrderBase = 900;

        foreach (var spec in specs) { ApplyOneChain(mgr, spec); _chainOrderBase += 100; }

        ApplyLinks();
    }

    /// <summary>应用 Link 显式接线（在所有链造完之后）。</summary>
    private static void ApplyLinks()
    {
        if (CfgLinks == null) return;
        foreach (var e in CfgLinks)
        {
            var raw = e?.Value;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (var item in raw.Split('|'))
            {
                var t = item.Trim();
                if (t.Length == 0) continue;
                int arrow = t.IndexOf("<-", StringComparison.Ordinal);
                if (arrow <= 0) continue;

                int target;
                if (!int.TryParse(t.Substring(0, arrow).Trim(), out target)) continue;

                IntPtr tPtr;
                if (!_madeByOrder.TryGetValue(target, out tPtr) || tPtr == IntPtr.Zero)
                {
                    // 不用 WarnOnce —— 它会把这个目标在后续轮次里的成败一起藏掉。
                    // 第一轮链还没造、后面几轮才造好，是正常节奏；只提醒一次会让人误判成"一直没成"。
                    Plugin.Log?.LogInfo($"[连段模组:接线] Link 目标 Order={target} 还不是我们造的段(本轮跳过)");
                    continue;
                }

                var fx = new SkillActivateFixedPoint(tPtr);
                var po = fx?.PreSkillOrder;
                if (po == null) continue;

                int added = 0;
                foreach (var p in t.Substring(arrow + 2).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int pred;
                    if (!int.TryParse(p.Trim(), out pred)) continue;
                    bool has = false;
                    try { for (int i = 0; i < po.Count; i++) if (po[i] == pred) { has = true; break; } } catch { }
                    if (has) continue;
                    try { po.Add(pred); added++; } catch (Exception ex) { Once("link|add", ex); }
                }
                // ★ 不论加没加上都打日志。
                //   `added == 0` 有两种完全不同的含义 ——「前驱本来就在」和「一个都没解析出来」——
                //   原来只在 added>0 时打印，于是"配了 Link 但没生效"看起来和"配了且已生效"一模一样。
                //   这正是本项目反复栽的【静默跳过】坑（见踩坑清单），接线是它的高发区。
                {
                    var sb = new System.Text.StringBuilder();
                    try { for (int i = 0; i < po.Count; i++) sb.Append(po[i]).Append('/'); } catch { }
                    Plugin.Log?.LogInfo($"[连段模组:接线] Link 目标 Order={target}: 本次新增 {added} 个前驱, " +
                                        $"最终 pre=[{sb}]" +
                                        (added == 0 ? "  ← 一个都没加进去(要么本来就有, 要么解析失败)" : ""));
                }
            }
        }
    }

    /// <summary>重写【一条】链。
    ///
    /// 与旧实现的两个根本差别（§3.6.1e E）：
    ///   1. 不再"找平A块并原地替换" —— 改为【移除原生同动作段 + 追加我们的段】。
    ///      旧模型只替换平A槽位，于是佩利诺尔/布鲁诺那些原生段**留在链里**，
    ///      和我们的克隆段条件相同却排在数组更前面 → 我们的段一次都选不中（实测）。
    ///   2. 宿主（继承时序的来源）= 【链的首段】的原生行。旧模型用"被替换的平A行"，
    ///      但新模型里链不再绑定平A槽位。一条链只有一种节奏，首段即基准。
    /// </summary>
    private static void ApplyOneChain(GamePlay.PlayerSkillMgr mgr, ChainSpec spec)
    {
        int g = spec.Group;
        if (g < 0 || g > 12 || spec.Segs.Count == 0) return;

        IntPtr arr = ReadPtr(mgr.Pointer, OFF_CHAINS);
        if (arr == IntPtr.Zero) return;
        int arrLen = ReadI32(arr, 0x18);
        if (g >= arrLen) return;
        IntPtr chainPtr = ReadPtr(arr, 0x20 + g * 8);
        if (chainPtr == IntPtr.Zero) return;

        var list = GetList(chainPtr);
        if (list == null) return;
        int cnt = SafeCount(list);
        if (cnt == 0) return;

        // 幂等：只用 Order>=900 这一个哨兵判据（绝不用内容特征，见旧注释的教训）
        if (HasOurMarker(list, cnt))
        {
            if (_applied.Add(g))
                Plugin.Log?.LogInfo($"[连段模组] 链[{g}] 已注入(Order>=900)，跳过 —— 避免重复拆装重置推进位置");
            return;
        }

        // ---- 宿主 = 链首段对应的原生行 ----
        string firstAct = spec.Segs[0].Action;
        GamePlay.PlayerSkill host = null;
        for (int i = 0; i < cnt; i++)
        {
            var sk = TryGet(list, i);
            if (sk == null) continue;
            if (string.Equals(ActionOf(sk), firstAct, StringComparison.OrdinalIgnoreCase))
            {
                IntPtr pp = ReadPtr(sk.SkillActivate?.Pointer ?? IntPtr.Zero, OFF_WRAP_DATA);
                if (pp != IntPtr.Zero && ReadI32(pp, OFF_ORDER) < 900) { host = sk; break; }
            }
        }
        if (host == null)
        {
            // 首段在链里没有原生行（例如它只存在于别的链）→ 用链里第一个原生段兜底
            for (int i = 0; i < cnt; i++)
            {
                var sk = TryGet(list, i);
                if (sk == null) continue;
                IntPtr pp = ReadPtr(sk.SkillActivate?.Pointer ?? IntPtr.Zero, OFF_WRAP_DATA);
                if (pp != IntPtr.Zero && ReadI32(pp, OFF_ORDER) < 900) { host = sk; break; }
            }
        }
        if (host == null) host = TryGet(list, 0);
        if (host == null) return;

        GamePlay.PlayerSkillChain chainObj = null;
        try { chainObj = new GamePlay.PlayerSkillChain(chainPtr); } catch { }

        // ---- 造我们的段 ----
        var made = new List<GamePlay.PlayerSkill>();
        int orderSeq = 0;
        for (int i = 0; i < spec.Segs.Count; i++)
        {
            if (spec.Segs[i].Remove) continue;          // '-动作名' = 只移除原生那一格
            // 段号起点用 _chainOrderBase（每条链一段独立号段），不是写死的 900 —— 见 ApplyChain 注释
            //
            // ★ 原地模式下【第一段保留原名】。它是顶替原生槽位的那一段，而**入口是"按名选招"**
            //   （实测空中链 `来路=按名选招(B) <- 14("atkAir3")`）—— 改了名游戏就查不到，
            //   直接掉到 `drop3`（这正是"位置对了( index=13 )却还是接不上"的那一格）。
            //   保留原名后：名字解析到动作表里同名的那个**原生逻辑**（动画完全相同），
            //   而链上这一格的**行**仍是我们的（时序/PreInput 可调）—— 两边都不耽误。
            var ps = MakeSkill(host, spec.Segs[i], _chainOrderBase + orderSeq, g, chainObj,
                               orderSeq == 0 ? -1 : _chainOrderBase + orderSeq - 1,
                               IsInPlace(g) && orderSeq == 0);
            orderSeq++;
            if (ps == null)
            {
                WarnOnce("build|" + g + "|" + i, $"链[{g}] 第 {i + 1} 段 \"{spec.Segs[i].Action}\" 造不出来，该链中止");
                return;
            }
            made.Add(ps);
            try { _ours.Add(ps.Pointer); } catch { }
        }

        // ---- 备份整条链（还原用）----
        var backup = new List<GamePlay.PlayerSkill>();
        for (int i = 0; i < cnt; i++)
        {
            var sk = TryGet(list, i);
            if (sk != null) backup.Add(sk);
        }
        _bkOrig[g] = backup;
        _bkStart[g] = 0;

        // ---- 【原地替换】模式：把我们的段插进原生段原来的槽位，不动数组其余部分 ----
        //   为什么只有空中链需要：见 CfgInPlace 的说明（空中按数组顺序推进，挖空中间会断链）。
        if (IsInPlace(g))
        {
            string first = spec.Segs[0].Action;
            int at = -1;
            for (int i = 0; i < SafeCount(list); i++)
            {
                var sk = TryGet(list, i);
                if (sk == null) continue;
                if (OrderOf(sk) >= 900) continue;                 // 我们自己的不算槽位
                if (string.Equals(ActionOf(sk), first, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
            }

            if (at < 0)
            {
                Plugin.Log?.LogWarning($"[连段模组] 链[{g}] 原地模式: 找不到原生 \"{first}\" 的槽位, " +
                                       $"退回「移除+追加」—— 这条链多半接不上");
                for (int i = 0; i < made.Count; i++) list.Add(made[i]);
            }
            else
            {
                SafeRemoveAt(list, at);                            // 挪走原生那一格
                for (int i = 0; i < made.Count; i++)               // 我们的段顶进同一个位置
                    SafeInsertAt(list, at + i, made[i]);
                Plugin.Log?.LogInfo($"[连段模组] 链[{g}] 原地替换: index={at} 处用我们的 {made.Count} 段" +
                                    $"顶替原生 \"{first}\"（数组其余部分一格没动）");
            }

            _applied.Add(g);
            DumpOurSegments(list, "重写后");
            Plugin.Log?.LogInfo($"[连段模组] 链[{g}] 重写完成(原地): 现共 {SafeCount(list)} 段");
            return;
        }

        // ---- 移除原生同动作段（§3.6.1e E：否则它会抢在我们的克隆段前面）----
        int removed = 0;
        if (CfgTakeOver?.Value != false)
        {
            for (int i = SafeCount(list) - 1; i >= 0; i--)
            {
                var sk = TryGet(list, i);
                if (sk == null) continue;
                if (OrderOf(sk) >= 900) continue;                  // 我们自己的不动
                string a = ActionOf(sk);
                if (string.IsNullOrEmpty(a)) continue;
                bool hit = false;
                foreach (var sg in spec.Segs)
                    if (string.Equals(sg.Action, a, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                if (hit) { SafeRemoveAt(list, i); removed++; }
            }
        }

        // ---- 追加我们的段 ----
        for (int i = 0; i < made.Count; i++) list.Add(made[i]);

        _applied.Add(g);
        DumpOurSegments(list, "重写后");
        Plugin.Log?.LogInfo($"[连段模组] 链[{g}] 重写完成: 移除原生同动作段 {removed} 个, 追加 {made.Count} 段, " +
                            $"现共 {SafeCount(list)} 段");
    }

    /// <summary>
    /// 探针: `findNextSkillMatchPreOrderAndInputDir` 的结果。
    ///
    /// 为什么需要: 2026-10-02 实测 `903 → 904` 断掉, 而**一条拒绝日志都没有** ——
    /// 说明它死在候选循环里某个不打日志的过滤器上。
    /// 这个循环有 4 道过滤(见 ACTION_CHAIN_SYSTEM.md §3.1), 之前**一道都没日志**。
    /// 按 (当前段 → 选中段) 去重, 一眼就能看出"从 903 出发到底选中了谁"。
    /// </summary>
    public static void NextSkillPostfix(GamePlay.PlayerSkillChain __instance, GamePlay.PlayerSkill __result)
    {
        _inOurSearch = false;                 // 搜索结束, 收起作用域
        try
        {
            int cur = -1; string curAct = "";
            try { var c = __instance.Cur; if (c != null) { cur = c.Order; curAct = c.Action; } } catch { }
            // ★ 2026-10-03 去掉"只看我们造的段"的过滤。
            //   布鲁诺的原生段 Order 是 1/3/4，全在 900 以下，被这条挡掉了 ——
            //   于是"布鲁诺为什么能穿插"这个最关键的问题，日志里一条证据都没有。
            //   LogEx.Once 按 (当前段, 结果段) 去重，全量打也不会刷屏。
            if (cur < 0 && __result == null) return;

            int res = __result == null ? -1 : OrderOf(__result);
            string resAct = __result == null ? "<null>" : ActionOf(__result);
            // ★ 不能再用 LogEx.Once —— 它有个【全局 key 上限】(KeyCap)，满了之后
            //   **新 key 静默丢弃**。放开过滤后这个探针会为每个 (当前段,结果段) 组合
            //   生成新 key，很快把全局额度占满，真正想看的条目反而全被丢了
            //   （实测：整场战斗 0 条选段日志，而反汇编证明这两个搜索必然被调用）。
            //   这里给它一张独立的、带自己上限的表。
            string nk = cur + "|" + res;
            if (_nextSeen.Count < 400 && _nextSeen.Add(nk))
                Plugin.Log?.LogInfo($"[连段模组:选段] 从 {cur}(\"{curAct}\") 找下一段 -> " +
                                    $"{(res < 0 ? "没找到" : res + "(\"" + resAct + "\")")}");
        }
        catch (Exception e) { Once("NextSkillProbe", e); }
    }

    /// <summary>探针: `CheckSkillCanCast` 的否决。它是候选循环里唯一还没有日志的一道过滤。</summary>
    /// <summary>
    /// 探针: **兜底路径** `findStartingSkillMatchInputDir`。
    ///
    /// 2026-10-02: 主路径探针证明它**从来没以 903 为当前段被调用过** ——
    /// 那"903 → 900"就只能是走了这条兜底路径(退回找起手段), 或者 Cur 被清掉了。
    /// 两者含义完全不同, 必须分清。
    /// </summary>
    public static void StartSkillPostfix(GamePlay.PlayerSkillChain __instance, GamePlay.PlayerSkill __result)
    {
        _inOurSearch = false;                 // ★ 搜索结束必须收起作用域, 否则会漏到后续的 CD 检查(会毁掉原版布鲁诺)
        try
        {
            int cur = -1; string curAct = "";
            try { var c = __instance.Cur; if (c != null) { cur = c.Order; curAct = c.Action; } } catch { }
            int res = __result == null ? -1 : OrderOf(__result);
            if (cur < 900 && res < 900) return;
            LogEx.Once($"combo|start|{cur}|{res}",
                       $"[连段模组:兜底] 起手段查找(当前 {cur}(\"{curAct}\")) -> " +
                       $"{(res < 0 ? "没找到" : res + "(\"" + ActionOf(__result) + "\")")}");
        }
        catch (Exception e) { Once("StartSkillProbe", e); }
    }

    // ==================== 带作用域的 CD 放行 ====================
    //
    // 为什么必须带作用域（血泪，2026-10-02 栽过两次）:
    //   ① `CheckCanChangeToAction(string name)` **只拿到动作名** ——
    //      它分不清"我们造的 903 attackAEX"和"原版的 order 1 attackAEX"。
    //      按【名字在不在 Sequence 里】放行 → 把原版布鲁诺链一起放行 →
    //      候选 attackAEX 永远可用 → 永远第一个命中 → 布鲁诺只剩第 1 段。
    //   ② 全局放行同样会毁掉它: 原版布鲁诺能往后走, **靠的就是**
    //      "attackAEX 用过、在 CD 里、被跳过"这个环节。CD 拒绝是承重结构。
    //
    // 所以判据改成【当前段是不是我们造的】:
    //   搜索开始时看链的 `Cur.Order` —— 只有 >= 900(正走在嫁接连段中间) 才放行。
    //   走在原版链上时 Cur 是原生行(< 900) → 一个字节都不干预。
    private static bool _inOurSearch;

    /// <summary>挂在两个搜索方法上: 判断"这次搜索是否发生在我们造的段之间"。</summary>
    public static void MarkSearchPrefix(GamePlay.PlayerSkillChain __instance)
    {
        try
        {
            var c = __instance.Cur;
            _inOurSearch = c != null && c.Order >= 900;
        }
        catch { _inOurSearch = false; }
    }

    public static bool CdScopedPrefix(string name, ref bool __result)
    {
        try
        {
            if (!_inOurSearch) return true;      // 不在我们的连段里 → 原样
            __result = true;
            LogEx.Once("combo|cdscoped|" + name,
                       $"[连段模组:CD放行] \"{name}\" 的动作CD检查被跳过(当前段是我们造的)");
            return false;
        }
        catch (Exception e) { Once("CdScoped", e); return true; }
    }

    /// <summary>
    /// 探针: **游标自己**的 Reset（`PlayerSkillChainCurSkill.Reset`）。
    ///
    /// 2026-10-02: `PlayerSkillChain.ClearCurSkill/Reset` 一次都没被调用，
    /// 但 `Cur.Skill` 确实被清空了 —— 那只能是这条路。抓住它就知道
    /// "是谁、在什么条件下把链重置成全新状态"（这才是 903 之后接不下去的根因）。
    /// </summary>
    public static void CurSkillResetPrefix(GamePlay.PlayerSkillChainCurSkill __instance, MethodBase __originalMethod)
    {
        try
        {
            string act = ""; int ord = -1;
            try { act = __instance.Action ?? ""; ord = __instance.Order; } catch { }
            // 只报"清掉了一个我们造的段"或"清掉了一个非空段"——空转会刷屏
            if (ord < 0 && string.IsNullOrEmpty(act)) return;
            LogEx.Once($"combo|curreset|{ord}",
                       $"[连段模组:游标重置] PlayerSkillChainCurSkill.Reset —— 被清掉的是 {ord}(\"{act}\")");
        }
        catch (Exception e) { Once("CurResetProbe", e); }
    }

    /// <summary>探针: 链的游标被清空 / 链被重置 —— 这会解释"为什么从 903 出发没人来问"。</summary>
    public static void ClearCurPrefix(GamePlay.PlayerSkillChain __instance, MethodBase __originalMethod)
    {
        try
        {
            int cur = -1;
            try { var c = __instance.Cur; if (c != null) cur = c.Order; } catch { }
            if (cur < 900) return;
            LogEx.Once($"combo|clear|{__originalMethod.Name}|{cur}",
                       $"[连段模组:清游标] {__originalMethod.Name} 被调用 —— 当前段是 {cur}, 游标被清空");
        }
        catch (Exception e) { Once("ClearProbe", e); }
    }

    public static void CanCastPostfix(GamePlay.PlayerSkill curSkill, ref bool __result)
    {
        try
        {
            if (__result || curSkill == null) return;
            int ord = OrderOf(curSkill);
            if (ord < 900) return;                       // 只看我们造的段
            LogEx.Once($"combo|cast|{ord}",
                       $"[连段模组:过滤] \"{ActionOf(curSkill)}\" order={ord} 被 CheckSkillCanCast 否决");
        }
        catch (Exception e) { Once("CanCastProbe", e); }
    }

    private static int OrderOf(GamePlay.PlayerSkill sk)
    {
        try
        {
            IntPtr p = ReadPtr(sk.SkillActivate?.Pointer ?? IntPtr.Zero, OFF_WRAP_DATA);
            return p == IntPtr.Zero ? -1 : ReadI32(p, OFF_ORDER);
        }
        catch { return -1; }
    }

    /// <summary>取【技能行】的 Order。给纹章接管之类"要区分原版 / 我们克隆段"的地方用。
    ///
    /// 判据沿用全项目的同一个哨兵：**Order &gt;= 900 = 我们造的段**。
    /// 它不可能与原生数据碰撞（原生 order 都是个位数/几十），所以比"按动作名猜"稳。
    /// </summary>
    internal static int OrderOfWrap(SkillActivateFixedPointWrap wrap)
    {
        try
        {
            IntPtr p = ReadPtr(wrap?.Pointer ?? IntPtr.Zero, OFF_WRAP_DATA);
            return p == IntPtr.Zero ? -1 : ReadI32(p, OFF_ORDER);
        }
        catch { return -1; }
    }

    /// <summary>
    /// 把链里【我们造的段】逐条打出来: order / Action / preSkillOrder / Input。
    ///
    /// 为什么必须有这个: 排查"903 之后为什么找不到 904"时, 我手上没有任何地方能看到
    /// **重写之后链到底长什么样** —— 只能看见"接受/拒绝"的结果, 看不见接线本身。
    /// 连段的接线全在 `order` 和 `preSkillOrder` 这两个字段上, 不打印它俩等于闭着眼睛调。
    /// </summary>
    private static void DumpOurSegments(Il2CppSystem.Collections.Generic.List<GamePlay.PlayerSkill> list, string tag)
    {
        try
        {
            int cnt = SafeCount(list);
            var sb = new System.Text.StringBuilder();
            sb.Append($"[连段模组:接线] {tag} 共 {cnt} 段:");
            for (int i = 0; i < cnt; i++)
            {
                var sk = TryGet(list, i);
                if (sk == null) continue;
                IntPtr proto = ReadPtr(sk.SkillActivate?.Pointer ?? IntPtr.Zero, OFF_WRAP_DATA);
                if (proto == IntPtr.Zero) continue;
                int order = ReadI32(proto, OFF_ORDER);
                if (order < 900) continue;            // 只看我们造的
                string act = MStr(proto, OFF_ACTION);
                string pre;
                try
                {
                    // ⚠ 不能用 foreach —— Il2CppInterop 生成的 RepeatedField<T>.GetEnumerator()
                    //   返回的 IEnumerator<int> 没实现好, 编译期就报 CS0117。
                    //   走 IList<int> 的索引器(那个是好的, MakeSkill 里 Clear/Add 就用它)。
                    var po = new SkillActivateFixedPoint(proto).PreSkillOrder;
                    var tmp = new List<string>();
                    int pn = 0;
                    try { pn = po?.Count ?? 0; } catch { }
                    for (int k = 0; k < pn; k++)
                    {
                        try { tmp.Add(po[k].ToString()); } catch { tmp.Add("?"); }
                    }
                    pre = "[" + string.Join("/", tmp) + "]";
                }
                catch (Exception e) { pre = "<读失败:" + e.Message + ">"; }
                int input = 0;
                try { input = ReadI32(sk.Pointer, 0x44); } catch { }
                sb.Append($"\n    #{i} order={order} \"{act}\" pre={pre} Input={input}");
            }
            Plugin.Log?.LogInfo(sb.ToString());
        }
        catch (Exception e) { Once("DumpSeg", e); }
    }

    /// <summary>把这一行的触发器字段打成一行字, 用来确认"整行克隆"是否真的带上了原生触发器。</summary>
    private static string FmtTriggers(SkillActivateFixedPoint row)
    {
        try
        {
            var st = row.StartTrigger;
            var et = row.ExitTrigger;
            var rq = row.ReqTriggerId;
            return $"StartTrigger=[{string.Join("/", st)}] ExitTrigger=[{string.Join("/", et)}] " +
                   $"ReqTriggerId=[{string.Join("/", rq)}]";
        }
        catch (Exception e) { return "<触发器读取失败: " + e.Message + ">"; }
    }

    /// <summary>
    /// 造一段连段用的技能。
    ///
    /// ★★★ 克隆源是【目标动作自己的原生行】, 不是平A模板 —— 这是 2026-10-02 的关键修正,
    /// 原因见函数体里的长注释。只覆盖 Group/Input/InputDir/Order/PreSkillOrder 五个"链位置"字段。
    /// </summary>
    private static GamePlay.PlayerSkill MakeSkill(GamePlay.PlayerSkill tmpl, Seg seg, int newOrder, int group,
                                                  GamePlay.PlayerSkillChain chain, int prevOrder,
                                                  bool keepName = false)
    {
        try
        {
            string action = seg.Action;
            var sa = tmpl.SkillActivate;
            if (sa == null) return null;

            IntPtr tmplPtr = ReadPtr(sa.Pointer, OFF_WRAP_DATA);
            if (tmplPtr == IntPtr.Zero) return null;

            // ★★★ 克隆谁 —— 这是整个模组最关键的一处，2026-10-02 修正。
            //
            // 旧做法: 克隆【平A模板那一行】, 只把 Action 换成目标动作。
            //   旧注释还写着"这样 Input/Group/Mps/Trigger/各种开关全部保持平A原样" ——
            //   **那句话就是 bug 本身**。
            //
            // 为什么错: 一行技能不是"播哪个动作", 而是【动作 + 触发器握手 + 自带脚本 + 自带弹幕】的整体。
            //   实测 ES 各行的触发器(dump 自 skillactivatefixedpointwrap):
            //       attack1(平1)    StartTrigger=['Start_1']  ReqTriggerId=[]
            //       attack4(平4)    ExitTrigger =['End_1']    ReqTriggerId=[]
            //       attackAEX(布1)  StartTrigger=['Start_2']  ReqTriggerId=[5607,5626]
            //       attackB  (布2)                            ReqTriggerId=[5607]
            //   `Start_1→End_1` 是【平A连段的窗口状态】, `Start_2→End_2` 是布鲁诺自己的。
            //   旧做法造出来的段: 播着布鲁诺的动作, 却发着平A的 `Start_1`、
            //   而且从不发布鲁诺该发的 `Start_2` ——
            //   **等于把动作和它的状态机劈开了**: 进招时把平A的窗口状态重新点着,
            //   表现就是"剑气/触发器把衔接打乱"。
            //
            // 正解: 克隆【目标动作自己的原生行】, 只覆盖"让它成为本条链的一段"的那几个字段。
            //   触发器 / 脚本 / 弹幕 / 入场券 / 时序 全部保留它自己的。
            IntPtr srcPtr = tmplPtr;
            bool fromNative = false;
            if (_native.TryGetValue(action, out var nativePtr) && nativePtr != IntPtr.Zero)
            {
                srcPtr = nativePtr;
                fromNative = true;
            }

            var proto = new SkillActivateFixedPoint(srcPtr);
            var copy = proto.Clone();
            if (copy == null) return null;

            // ★★★★★ 2026-10-02 第二轮定位 —— 真正的根因：**动作名就是链的认领键**。
            //
            // 上一轮修掉 PrecheckActionCd 之后，903(attackAEX) 第一次被真正选中并启动：
            //     [段闸门] SkillChangePreCall -> True  Action="attackAEX" Order=903 我们造的=True
            //     [推进]  "attackD2" -> "attackAEX"   链[1] Cur指针=0x... 属于我们=True
            // 紧接着：
            //     [段闸门] SkillChangePreCall -> True  我们造的=False  Action="attackB" Order=3
            //     [选中]   psk=0x... 我们造的=False  Action="attackB" Order=3
            //     [输入推进] DoUpdateAndCheckInputSucc -> True  链段数=19 Cur@2("attackB")   ← 全日志第一次 True
            // 注意 `链段数=19` ≠ 链[1] 的 18 —— **返回 True 的是链[2]**。
            //
            // 也就是说：动作名 "attackAEX" 是全局唯一的身份标识，链[2] 里那条原生
            // attackAEX 技能行同样写着这个名字。于是两条链都认领了这同一个动作，
            // 链[2] 先进了输入判定、把这次输入吃掉并推进到自己的 attackB，
            // 链[1] 的游标随即被 Reset —— 表现就是"布鲁诺1的剑气打断了衔接、接不上平3"。
            //
            // 结论：**只要段的 Action 还叫 "attackAEX"，它就永远会被原生布鲁诺链认领。**
            // 改 Action 名是唯一干净的出路。而 GameActionLogic 恰好把
            //     Name(0x20)    = 逻辑身份（链认领键 / CD键 / 查表键）
            //     Animate(0x28) = 真正播放的动画资源
            // 分成了两个字段 —— 所以我们可以"换名不换皮"：
            // 克隆一份 attackAEX 的 GameActionLogic，改成独有名字注入动作表，
            // 动画/判定/剑气/脚本全部原样保留，而链[2] 再也认不出它。
            // （这正是用户自己提出的"创造一个不存在的『平4新』，让它使用布鲁诺1的动作和内容"。）
            string effAction = action;
            if (!keepName && fromNative && NeedClone(action))
                effAction = SynthName(action, newOrder);
            // keepName: 原地模式的第一段 —— 名字必须留住（入口是按名选招），
            //          所以这里**不换名**，链上这一格换的是"行"，不是"名字"。

            // ---- 只覆盖这几个"链位置"字段, 其余全部保留原生 ----
            copy.Action = effAction;
            copy.Order = newOrder;
            // ★★★★★ 【不再写死 Any】—— "串链条"的根因（2026-10-03）
            //   实测：平A 和佩利诺尔是【同一条链】，靠 InputDir 分流：
            //       佩利诺尔 = InputDir 下 | 平A = InputDir 任意 | 上挑 = 上
            //   而我们给每一段都写死 Any ⇒ 任意方向都成立 ⇒ 按住下按攻击时
            //   我们的段把"下"吃掉了，佩利诺尔永远选不中。
            //   改成继承源动作的方向（和 Timeout/ActdurStrict 同一原则）。
            //   逐段覆盖走 WindowOverrides 的第 6 位（见下面）。
            try
            {
                var srcFixed = new SkillActivateFixedPoint(srcPtr);
                copy.InputDir = srcFixed.InputDir;
            }
            catch (Exception e) { Once("InputDir", e); }
            // 段声明里的方向优先（方向是【链位置】属性 —— 同一动作在不同链里方向不同）
            if (seg.Dir >= 0)
            {
                try { copy.InputDir = (SkillInputDirType)seg.Dir; } catch { }
            }
            // ★ Group 必须改成本条链的组号。PlayerSkill.get_SkillType() 读的就是它,
            //   留着原生行的 group(比如布鲁诺的 2) 会让这一段在本链里"身份不对"。
            try { copy.Group = group; } catch { }

            // ★★★★★ 2026-10-02 找到的真正卡点：PrecheckActionCd（SkillActivateFixedPoint 0x94）。
            //
            // 证据（日志里我们自己转储出来的 9 段，含每个字段的原生值）：
            //     [8]  "attack1"    PreCD=0
            //     [9]  "attack2"    PreCD=0
            //     [10] "attackD2"   PreCD=0
            //     [11] "attackAEX"  PreCD=1   ← 布鲁诺1
            //     [12] "attack3"    PreCD=0
            //     [13] "attackB"    PreCD=1   ← 布鲁诺2
            // 整条链上【只有那两个布鲁诺段是 1】，而它们正好就是"选得中、起不来"的两段。
            //
            // 含义：这个字段是"本段除了 CheckCanChangeToAction 之外，还要不要再过一道前置CD"。
            //   平A 全家是 0（裸动作，CD 由外层管）；布鲁诺那两段是 1（它们原生活在链[2]，
            //   由链[2]自己的一套冷却/触发握手接管）。
            //   我们把原生行整行搬进链[1]，就把"你要先满足链[2]的入场条件"这条也一起搬来了 ——
            //   于是 findNextSkillMatchPreOrderAndInputDir 里那道 PrecheckActionCd 恒拒。
            //   这解释了全部症状：CheckSkillCanCast 否决 ×5、903 被选中后游标立刻被 Reset、
            //   以及"剑气会打断衔接"（动作根本没起来，链退回起手段）。
            //
            // 归类上这不是"拍脑袋调数据"：它不是手感数值，是【从别的链上下文继承来的准入条件】，
            //   和 Order / InputDir / Group / PreSkillOrder 一样属于"重新挂载到本链时必须重写"的字段。
            //   我们既然已经决定"让它成为链[1]的一段"，就该让它的准入条件也归链[1]管。
            try { copy.PrecheckActionCd = 0; } catch { }
            try { Marshal.WriteInt32(copy.Pointer, OFF_PRECD, 0); } catch { }   // 兜底：直接写原生字段

            // ★★★★★ 2026-10-02 第三轮定位 —— 第二个"从布鲁诺行继承来的致命值"：Timeout。
            //
            // 反汇编 PlayerSkillChain::DoUpdate（RVA 0x1bb1260，状态==2 分支）：
            //     0x1bb14ec  mov  rbx, [rdi + 0x18]          ; CurSkill.TimeEllaps
            //     0x1bb14ff  call SkillActivateFixedPointWrap::get_Timeout
            //     0x1bb1525  call Fp::op_GreaterThanOrEqual   ; TimeEllaps >= Timeout ?
            //     0x1bb152c  je   <返回>                       ; 没到 -> 不动
            //     ...
            //     0x1bb15a0  call PlayerSkillChainCurSkill::Reset   ; ★ 到了 -> 游标清零
            //
            // 也就是说 Timeout = "这一段的游标能活多久"，到点就 Reset。
            //   平A 各行 Timeout = 0.60~0.80 → 游标活得够久，下一段找得到它。
            //   布鲁诺各行 Timeout = 0        → **TimeEllaps(0) >= 0 恒真，第一帧就 Reset**。
            //
            // 症状完全对得上：`[选段]` 里有 902→903，**从来没有"从 903 出发"**，
            // 因为 903 一进去游标就被清了，下一次输入只能走"找回起手段"→ attack1。
            // （TimeEllaps 字段在 CurSkill +0x18，与 [rdi+0x18] 对上；timeout_ 是 float @0x70。）
            //
            // 处理方式与 PreCD 同类：源行没给这行留接管窗口（=0）时，
            // 就沿用**被它替换掉的那行平A**的窗口 —— 反正这一段现在是链[1]的段。
            try
            {
                // Q32.32 的 long：0.7 秒 = 0x0B3333333
                long cur = copy.Timeout;
                if (cur <= 0L)
                {
                    long host = 0L;
                    try { host = new SkillActivateFixedPoint(tmplPtr).Timeout; } catch { }
                    if (host <= 0L) host = (long)Math.Round(0.7 * 4294967296.0);  // 平A 各行落在 0.60~0.80
                    copy.Timeout = host;
                }

                // ★★★★★ 【后摇衔接窗口随速度放大】—— 2026-10-02 修正
                //
                //   这里才是"后摇能接下招"的那个窗口：窗口区间 = [ActdurStrict, Timeout]
                //   （反汇编：ActdurStrict 之前输入被丢；TimeEllaps >= Timeout 就 Reset 游标）。
                //   把链路按 k 倍速播之后，这个窗口在【现实时间】里被压缩了 k 倍 —— 手感变怪。
                //
                //   ⚠ 上一版我错改成了 Preinputtime。那是【一次按键的有效期】，
                //     放大它会让"按一下攻击"在更长时间里持续算数，于是一路连过去
                //     （实测症状：按一下直接一二段连着出）。那个已经撤掉。
                //
                //   MakeSkill 对每一段都建了 proto 副本（原生平A四段也是盖副本），
                //   所以十段都能改，共享的原生表行一个字节不动。
                float kw = 1f;
                if (CfgPreInputScale?.Value < 0f)          // <0 = 自动跟随变速模块
                {
                    kw = EsActionSpeed.SpeedOf(action);   // 跟随【该动作所属组】的倍率
                }
                else kw = CfgPreInputScale.Value;          // >=0 = 手动指定
                if (kw > 0f && Math.Abs(kw - 1f) > 1e-4f)
                    copy.Timeout = (long)Math.Round(copy.Timeout * (double)kw);

                // ★★★★★ 2026-10-02 第五轮 —— 光改 Timeout 不够，还得改 ActdurStrict。
                //
                // 探针（[输入推进] 的 闸门[Status=.. CD=..]）显示：
                //     attack1   进入时 链冷却 CD=0.300   游标 Timeout=0.700  → 窗口 0.40s  ✓ 能接
                //     attack2   进入时 链冷却 CD=0.350   游标 Timeout=0.600  → 窗口 0.25s  ✓ 能接
                //     ace1_903  进入时 链冷却 CD=0.700   游标 Timeout=0.700  → 窗口 0      ✗ 接不上
                //
                // 反汇编 DoUpdateAndCheckInputSucc（RVA 0x1BB10F0）确认：
                //     [chain+0x54] > 0  → 直接 return false      ← 冷却期间输入全丢
                //     TimeEllaps < ActdurStrict → return false   ← 硬地板
                // 冷却一结束，SetCoolDown 还会顺手 CurSkill.Reset（调用点 0x1bb2837），
                // 游标被清 → 下一次按键只能走兜底 findStartingSkillMatchInputDir → 回 attack1。
                //
                // 可见"能接上"的真正条件是【冷却结束得比游标超时早】。
                // 上一轮只把 Timeout 从 0 改成 0.7（必要：0 会让游标第一帧就被清），
                // 但冷却也跟着变成 0.7，窗口被压成零 —— 所以还是接不上。
                //
                // 处理：时序整体按宿主平A段走。ActdurStrict 现在还是布鲁诺的 0，一并继承。
                // ★ 【桥接段的游标窗口加成】—— 只为"从别的链借来的段"生效。
                //   原因见 Plugin.cs 里 TimeoutBonus 的说明：借来的段冷却与 Timeout 撞在一起、
                //   窗口为零，而冷却值我们暂时定位不到来源。这里绕过：
                //   把 Timeout 拉到冷却之上，游标就不会撞上 "Reset + SetStatus(4) + SetCoolDown" 那条流程。
                //   ⚠ 只影响"等下一次输入能等多久"，不改变动作时长。
                if (effAction != action && CfgTimeWin != null && CfgTimeWin.Value > 0f)
                {
                    long add = (long)Math.Round(CfgTimeWin.Value * 4294967296.0);
                    copy.Timeout = copy.Timeout + add;
                }

                long curA = copy.ActdurStrict;
                if (curA <= 0L)
                {
                    long hostA = 0L;
                    try { hostA = new SkillActivateFixedPoint(tmplPtr).ActdurStrict; } catch { }
                    if (hostA <= 0L) hostA = (long)Math.Round(0.3 * 4294967296.0);  // 平A 各行 0.30~0.35
                    copy.ActdurStrict = hostA;
                }
            }
            catch (Exception e) { Once("Timeout", e); }

            // ★★★★★ 2026-10-02 第四轮 —— 同一类问题的第四个字段：Input（这一行"响应哪个按键"）。
            //
            // 修好 Timeout 之后游标活得住了（`Cur@11("ace1_903")` 连续 900 次调用都在），
            // 但窗口期内输入全被拒：`DoUpdateAndCheckInputSucc -> False`，
            // 而且 `[选段]` 里**再也没出现过"从 903 出发"** —— 连搜索都没进。
            //
            // 对照日志，903 和其它段唯一的差别是这一项：
            //     【技能】"attack1"   按键=Attack/Any
            //     【技能】"ace1_903"  按键=Skill/Any    ← 从 attackAEX 继承来的
            //
            // SkillActivateFixedPoint.input_ 是字符串（@0x40）。平A 各行是 "Attack"，
            // 布鲁诺各行是 "Skill"。链在收输入时按**当前段的 Input** 决定听哪个键 ——
            // 我们把它改成 Skill 之后，玩家按攻击键它当然不理。
            //
            // 处理方式同上：这一行现在是链[1]的段，就该听链[1]的键 —— 取被替换那行的值。
            try
            {
                string hostInput = null;
                try { hostInput = new SkillActivateFixedPoint(tmplPtr).Input; } catch { }
                if (!string.IsNullOrEmpty(hostInput) && copy.Input != hostInput)
                    copy.Input = hostInput;
            }
            catch (Exception e) { Once("Input", e); }
            try { _madeByOrder[newOrder] = copy.Pointer; } catch { }
            if (effAction != action)
            {
                _synthApplied[copy.Pointer] = effAction;
                try { _synthHost[effAction] = MStr(tmplPtr, OFF_ACTION); } catch { }
            }

            // ★ 把"源行 vs 宿主平A行"的全部字段差异摊出来（只打一次）。
            //   这一轮的四个卡点都是逐字段对比能一眼看出来的东西，不该靠撞。
            SkillRowDiff.Compare(action, effAction, srcPtr, tmplPtr);

            if (!fromNative)
                Plugin.Log?.LogWarning($"[连段模组] \"{action}\" 没有原生行, 退回平A模板 —— " +
                                       $"它带的触发器和这个动作不匹配, 衔接可能不正常");

            // ★ 衔接三件套：**克隆源就是该动作的原生行, 所以时序天然就是原生值了**
            //   (布鲁诺 = ActdurStrict 0 + PreInput 0.3; 平A = 0.30 可取消)。
            //   这里只剩"手动覆盖"两条路径。
            //
            // ⚠ 别再退回"只照抄时序"的旧做法 —— 见上面克隆源的说明:
            //   那条路只搬了时序, 把触发器等一起留在了捐赠者身上。
            if (CfgHold?.Value >= 0f)
                // ⚠ SkillActivateFixedPoint.ActdurStrict 是 **long**（Q32.32），不是 Fp
                copy.ActdurStrict = (long)Math.Round(CfgHold.Value * 4294967296.0);
            if (CfgPreInput?.Value >= 0f)
                copy.Preinputtime = (long)Math.Round(CfgPreInput.Value * 4294967296.0);

            // ★★★★★【逐动作窗口覆盖】—— 精调环节。
            //   上面那三段自动值（硬地板/后摇窗口/前置输入）是"能连上"的地基；
            //   这里是"手感对不对"的旋钮，逐动作给绝对值。
            //   值按【现实秒】给，内部乘速度倍率 —— 改速度不会让调好的手感失效。
            try
            {
                var ov = WinOv(action);
                if (ov != null)
                {
                    double S = 4294967296.0;
                    float kk = SpeedK(action);
                    if (ov[0] >= 0) copy.Timeout = (long)Math.Round(ov[0] * kk * S);
                    if (ov[1] >= 0) copy.Preinputtime = (long)Math.Round(ov[1] * kk * S);
                    if (ov[2] >= 0) copy.ActdurStrict = (long)Math.Round(ov[2] * kk * S);
                    // [3]/[4] 是布尔开关(0/1)，不乘速度倍率：
                    //   allowGround  这一行允不允许在地面用
                    //   allowFlying  这一行允不允许在空中用
                    // 佩利诺尔4(atkAirX) 的原生行 allowGround=true —— 所以落地也能放出来。
                    if (ov[3] >= 0) { try { copy.AllowGround = ov[3] > 0.5; } catch { } }
                    if (ov[4] >= 0) { try { copy.AllowFlying = ov[4] > 0.5; } catch { } }
                    // [5] InputDir（方向分流）: 0=任意 1=上 2=下 3=前 4=后 5=无方向
                    if (ov[5] >= 0) { try { copy.InputDir = (SkillInputDirType)(int)ov[5]; } catch { } }
                    Plugin.Log?.LogInfo($"[连段模组:窗口覆盖] \"{action}\" " +
                        $"后摇={(ov[0] < 0 ? "默认" : ov[0] + "s")} " +
                        $"前置输入={(ov[1] < 0 ? "默认" : ov[1] + "s")} " +
                        $"硬地板={(ov[2] < 0 ? "默认" : ov[2] + "s")} " +
                        $"地面={(ov[3] < 0 ? "默认" : (ov[3] > 0.5 ? "允许" : "禁止"))} " +
                        $"空中={(ov[4] < 0 ? "默认" : (ov[4] > 0.5 ? "允许" : "禁止"))}  (×{kk:F2})");
                }
            }
            catch (Exception e) { Once("WinOv|apply", e); }

            LogEx.Once("combo|row|" + action,
                       $"[连段模组:整行] \"{effAction}\" (源={action}) <- {(fromNative ? "原生行(含触发器/脚本/弹幕)" : "平A模板")}  " +
                       $"{FmtTriggers(copy)}  " +
                       $"ActdurStrict={copy.ActdurStrict / 4294967296.0:F3} PreInput={copy.Preinputtime / 4294967296.0:F3} " +
                       $"PreCD={copy.PrecheckActionCd} " +
                       $"Timeout={copy.Timeout / 4294967296.0:F3} Input=\"{copy.Input}\" Dir={(int)copy.InputDir}");

            // ★★★★ 真正决定"能不能接上"的字段：PreSkillOrder（前置技能序号）。
            //
            // 反汇编 PlayerSkillChain.findAndStartSkill_Imp 得到的事实：
            //     call findNextSkillMatchPreOrderAndInputDir   ← 按【前置序号 + 输入方向】找下一段
            //     找不到 -> call findStartingSkillMatchInputDir ← 退回去找"起手段"
            // 也就是说链推进**既不按数组顺序、也不按 Order 顺序**，而是:
            //     拿当前段的 Order，去找哪一段的 PreSkillOrder 里包含它。
            //
            // 我们克隆平A模板时把 PreSkillOrder 一起抄了过来（它指向 attack1 的前置），
            // 于是每一段的"前置"都指向链外的东西 —— 永远找不到下一段，
            // 每次都退化成"重新找起手段" = 永远 attack1。
            //
            // 修法：第 i 段的前置 = 第 i-1 段的 Order。第一段保持模板原样(它得能从站姿起手)。
            // ★ 起手段（尾缀 +）：前驱清空 —— 这样它才能从站姿/任意输入直接起手
            if (seg.Entry)
            {
                try { copy.PreSkillOrder?.Clear(); } catch (Exception e) { Once("EntryClear", e); }
            }
            else if (prevOrder >= 0)
            {
                try
                {
                    var po = copy.PreSkillOrder;
                    if (po != null)
                    {
                        // ★★★★★ 2026-10-03 【不再 Clear】—— 保留源动作原有的前驱，只做【并集】。
                        //
                        // 为什么原来有 Clear：那会儿克隆的是"平A模板那一行"，它自带的 preSkillOrder
                        // 指向模板的前驱（链外的东西），留着链永远走不动，所以必须清掉再写单前驱。
                        // 原注释就是这么写的。
                        //
                        // 但前提变了：现在克隆的是【目标动作自己的原生行】，它的 preSkillOrder 是
                        // **有意义的**。实测（关掉改写、观察原生）：
                        //     从 4("attackD1") 找下一段 -> 6("attackD2")    佩1 → 佩2   (InputDir=下)
                        //     从 4("attackD1") 找下一段 -> 11("attack3")    佩1 → 平3   (InputDir=任意)
                        //     从 6("attackD2") 找下一段 -> 12("attack4")    佩2 → 平4
                        // 同一个起点能到两个不同的段 —— **这就是原生"穿插"的实现**：
                        // attack3 的前驱列表里同时写着 10(平2) 和 4(佩1)。
                        //
                        // Clear() 把它删掉了 ⇒ 我们只能走直线。**不是缺机制，是我们把机制删了。**
                        //
                        // 改成并集：保留原生前驱（原生接线）+ 加上我们链里的前一段（我们的接线）。
                        bool has = false;
                        try
                        {
                            for (int i = 0; i < po.Count; i++)
                                if (po[i] == prevOrder) { has = true; break; }
                        }
                        catch { }
                        if (!has) po.Add(prevOrder);
                    }
                }
                catch (Exception e) { Once("PreOrder", e); }
            }

            var wrap = new SkillActivateFixedPointWrap();
            if (wrap == null || wrap.Pointer == IntPtr.Zero) return null;
            Marshal.WriteIntPtr(wrap.Pointer, OFF_WRAP_DATA, copy.Pointer);

            var ps = new GamePlay.PlayerSkill(wrap, tmpl.AttrOrder, tmpl.Level, (Fp)1f);
            if (ps == null) return null;
            try { ps.Input = tmpl.Input; } catch { }

            // ★★★★★ 2026-10-04 —— 【PlayerSkill.Input(0x44) 为 0 的段，链搜索会直接跳过它】。
            //
            // 反汇编（见 ACTION_CHAIN_SYSTEM.md §3.1 / §3.2）两条查找路径都有这一道：
            //     if (skill[0x44] == 0) continue;
            // 注意这是 **PlayerSkill 对象上的 InputCmd**，和 wrap 数据里那个字符串 Input(@0x40)
            // 是两个字段：字符串那个决定"听哪个键"（上面已经处理过），
            // 这个 InputCmd 决定"这一段算不算一个能被输入选中的段"。
            //
            // 为什么链[1] 一直没事：宿主是 attackD1，InputCmd.Attack = 1。
            // 为什么链[6] DashAttack 会全废：宿主是 **rush** —— 它是纯派生段，
            // 槽位显示"按键=/Any"就是 InputCmd.None = 0，照抄 ⇒ 我们造的每一段都 ==0
            // ⇒ 两条查找路径都跳过它们 ⇒ 冲刺-A 之后按攻击【毫无反应】。
            // （顺带解释了"攻击没法接冲刺"这个手感：rush 自己 Input=0，
            //   所以它永远不可能作为"下一段"被选中 —— 不是游戏规则，就是这个字段。）
            //
            // 修法：宿主是 None 时改用【源动作自己的 InputCmd】—— 那才是"这一格听哪个键"的正解。
            try
            {
                int curIn = 0;
                try { curIn = (int)ps.Input; } catch { }
                if (curIn == 0)
                {
                    GamePlay.InputCmd ic;
                    string srcIn = null;
                    try { srcIn = copy.Input; } catch { }   // wrap 数据里的字符串: "Attack" / "Skill" / ...
                    if (!string.IsNullOrEmpty(srcIn) &&
                        Enum.TryParse(srcIn, true, out ic) && (int)ic != 0)
                        ps.Input = ic;
                    else
                        ps.Input = GamePlay.InputCmd.Attack;  // 兜底：绝不能留 0，留了这段等于不存在
                }
            }
            catch (Exception e) { Once("psInput", e); }

            // ★★★ Owner(0x10) 必须补上。用 `new PlayerSkill(...)` 造出来的对象这个字段是空的，
            //   而它指向"我属于哪条链" —— 游戏推进/回查时很可能就靠它。
            //   原始那些技能是 Init() 时被填上的，我们自己 new 的没人填。
            //   实测症状：链永远停在第一段（每次选中同一个 psk），
            //   因为拿到段之后顺着 Owner 找不到链，就无从"下一段"。
            try
            {
                if (chain != null) ps.Owner = chain;
                else Marshal.WriteIntPtr(ps.Pointer, 0x10, IntPtr.Zero);
            }
            catch (Exception e) { Once("SetOwner", e); }
            try
            {
                IntPtr op = ReadPtr(ps.Pointer, 0x10);
                if (_ownerLogLeft > 0)
                {
                    _ownerLogLeft--;
                    Plugin.Log?.LogInfo($"[连段模组:Owner] 段 \"{action}\" 的 Owner = " +
                                        $"{(op == IntPtr.Zero ? "空!" : "0x" + op.ToInt64().ToString("X"))} " +
                                        $"(链=0x{(chain?.Pointer ?? IntPtr.Zero).ToInt64():X})");
                }
            }
            catch { }

            // ★ 结果校验：三处分别读回，定位"Action 到底写没写进去"。
            //   上一版全变成 attack1 = 克隆体上 Action 没生效，
            //   于是每一段都还指向模板的 attack1，链就在 attack1 上打转。
            if (_verifyLeft > 0)
            {
                _verifyLeft--;
                string viaProp = "?"; try { viaProp = copy.Action ?? "<null>"; } catch (Exception e) { viaProp = "<抛:" + e.Message + ">"; }
                string viaPtr = MStr(copy.Pointer, OFF_ACTION);
                string viaWrap = "?"; try { viaWrap = ps.SkillActivate?.Action ?? "<null>"; } catch (Exception e) { viaWrap = "<抛:" + e.Message + ">"; }
                IntPtr chk = ReadPtr(ReadPtr(ps.Pointer, OFF_SKILLACT), OFF_WRAP_DATA);
                Plugin.Log?.LogInfo($"[连段模组:校验] 想要=\"{effAction}\" (源={action})\n" +
                                    $"      setter后 copy.Action          = \"{viaProp}\"\n" +
                                    $"      直读 copy.Pointer+0x28        = \"{viaPtr}\"   (order={ReadI32(copy.Pointer, OFF_ORDER)})\n" +
                                    $"      经新wrap读 ps.SkillActivate     = \"{viaWrap}\"\n" +
                                    $"      ps.SkillActivate->data 指针    = 0x{chk.ToInt64():X}  (copy=0x{copy.Pointer.ToInt64():X} 一致={chk == copy.Pointer})");
            }
            return ps;
        }
        catch (Exception e) { Once("MakeSkill", e); return null; }
    }

    private static int _cdLogLeft = 60;
    private static readonly HashSet<string> _nextSeen = new HashSet<string>();

    // ---------------- DropSwitches：逐条剔接招窗口 ----------------

    /// <summary>读一条接招窗口的【目标动作名】。
    /// 偏移不是我猜的 —— 来自 ActionStructure 的既有实现：
    ///   ActionSwitchData + 0x30 = NewActionEx(ActionLogicParamString 结构体)
    ///                     + 0x38 = 其中的 ConstValue(string)  ← 接哪一招
    /// </summary>
    private static string SwitchTarget(GamePlay.ActionSwitchData e)
    {
        try
        {
            if (e == null || e.Pointer == IntPtr.Zero) return null;
            IntPtr p = Marshal.ReadIntPtr(e.Pointer, 0x38);
            if (p == IntPtr.Zero) return null;
            return Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(p);
        }
        catch { return null; }
    }

    /// <summary>解析 "源动作:目标1,目标2 | ..." 里某个源动作要剔除的目标集合（支持 * 通配）。</summary>
    private static HashSet<string> DropTargetsFor(string srcAction)
    {
        try
        {
            var raw = CfgDropSwitches?.Value;
            if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrEmpty(srcAction)) return null;
            foreach (var item in raw.Split('|'))
            {
                int c = item.IndexOf(':');
                if (c <= 0) continue;
                if (!string.Equals(item.Substring(0, c).Trim(), srcAction, StringComparison.OrdinalIgnoreCase)) continue;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in item.Substring(c + 1).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var v = t.Trim();
                    if (v.Length > 0) set.Add(v);
                }
                return set.Count > 0 ? set : null;
            }
        }
        catch { }
        return null;
    }

    /// <summary>目标名是否命中剔除集合（支持 * 通配）。</summary>
    private static bool HitDrop(HashSet<string> drop, string target)
    {
        if (drop == null) return false;

        // ★ 空目标 = 「这个时段允许【任意技能切换】进来」的通配许可（模式多为 切技能/仅切Action）。
        //   它没有目标名，所以永远匹配不上普通规则 —— 但它才是"谁都能从这儿插进来"的那个口子。
        //   用 token: empty / - / "" 来剔除它。
        if (string.IsNullOrEmpty(target))
            return drop.Contains("empty") || drop.Contains("-") || drop.Contains("*");
        foreach (var pat in drop)
        {
            if (pat == "*") return true;
            if (pat.IndexOf('*') < 0)
            {
                if (string.Equals(pat, target, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else
            {
                var parts = pat.Split('*');
                int pos = 0; bool ok = true;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].Length == 0) continue;
                    int f = target.IndexOf(parts[i], pos, StringComparison.OrdinalIgnoreCase);
                    if (f < 0) { ok = false; break; }
                    if (i == 0 && !pat.StartsWith("*") && f != 0) { ok = false; break; }
                    pos = f + parts[i].Length;
                }
                if (ok && !pat.EndsWith("*"))
                {
                    var last = parts[parts.Length - 1];
                    if (last.Length > 0 && !target.EndsWith(last, StringComparison.OrdinalIgnoreCase)) ok = false;
                }
                if (ok) return true;
            }
        }
        return false;
    }

    /// <summary>按 DropSwitches 剔除克隆体接招窗口表里的指定条目。
    ///
    /// ⚠ 前提：clone.ActionSwitchs 必须已经是【克隆体自己的表】——
    ///   CloneAction 里已经逐元素复制过（引用型字段整块拷贝会共用 List，直接改会污染原版）。
    /// </summary>
    private static void ClearSwitchesIfNeeded(GamePlay.GameActionLogic clone, string srcAction)
    {
        try
        {
            if (clone == null) return;
            var drop = DropTargetsFor(srcAction);
            if (drop == null) return;

            var sw = clone.ActionSwitchs;
            if (sw == null) return;
            int before = 0; try { before = sw.Count; } catch { }
            if (before == 0) return;

            int removed = 0;
            for (int i = sw.Count - 1; i >= 0; i--)
            {
                GamePlay.ActionSwitchData e = null;
                try { e = sw[i]; } catch { }
                if (e == null) continue;
                if (HitDrop(drop, SwitchTarget(e))) { sw.RemoveAt(i); removed++; }
            }
            if (removed > 0)
                Plugin.Log?.LogInfo($"[连段模组:剔窗口] \"{srcAction}\" 剔除 {removed}/{before} 条接招窗口 " +
                                    $"(剩 {sw.Count}) —— 只动克隆体, 原版不受影响");
            else
                Plugin.Log?.LogInfo($"[连段模组:剔窗口] \"{srcAction}\" 没有条目命中剔除规则 (共 {before} 条)");
        }
        catch (Exception e) { Once("DropSwitches", e); }
    }

    // ---------------- 汇点探针 ----------------
    private static readonly HashSet<string> _implSeen = new HashSet<string>();

    /// <summary>三条起招路径的唯一汇点。打"起了哪一段 + 落在哪条链"。</summary>
    public static void StartImplChainProbe(GamePlay.PlayerSkill psk, GamePlay.PlayerSkillChain chain)
    {
        try
        {
            if (CfgEnabled?.Value != true) return;

            int order = -1; string act = "";
            try { if (psk != null) { order = OrderOf(psk); act = ActionOf(psk); } } catch { }

            int cnt = 0, idx = -1;
            string owner = "";
            try
            {
                var list = chain?.SkillList;
                cnt = SafeCount(list);
                for (int i = 0; i < cnt; i++)
                {
                    var sk = TryGet(list, i);
                    if (sk != null && ReferenceEquals(sk.Pointer, psk?.Pointer)) { idx = i; break; }
                }
                owner = chain == null ? "<null链>" : "0x" + chain.Pointer.ToInt64().ToString("X");
            }
            catch { }

            string key = order + "|" + act + "|" + cnt + "|" + idx;
            if (_implSeen.Count >= 400 || !_implSeen.Add(key)) return;

            Plugin.Log?.LogInfo($"[连段模组:汇点] SkillStartImplement 起了 \"{act}\" Order={order}  " +
                                $"所在链段数={cnt} index={idx} 链={owner}");
        }
        catch (Exception e) { Once("StartImplChainProbe", e); }
    }

    // ---------------- 全量起招探针 ----------------
    private static readonly HashSet<string> _startSeen = new HashSet<string>();
    private static bool _viaChange, _viaJs;

    public static void ViaChangePrefix() { _viaChange = true; }
    public static void ViaJsPrefix() { _viaJs = true; }

    /// <summary>所有起招的唯一汇点。打"目标 + 来路"。</summary>
    public static void StartSkillProbe(GamePlay.PlayerSkillChain __instance, bool __result)
    {
        // 先读后清 —— 标记必须在本次消费掉，否则会污染下一次
        bool viaChange = _viaChange, viaJs = _viaJs;
        _viaChange = false; _viaJs = false;
        try
        {
            if (CfgEnabled?.Value != true || __instance == null) return;

            // 不绑目标技能形参(会 IL Compile Error)；postfix 在调用后跑，读 Cur 即得目标。

            int cnt = 0; try { cnt = SafeCount(__instance.SkillList); } catch { }
            int cur = -1; string curAct = "";
            try { var c = __instance.Cur; if (c != null) { cur = c.Order; curAct = c.Action ?? ""; } } catch { }

            string path = viaChange ? "按名选招(B)" : (viaJs ? "JS桥(C)" : "输入/搜索(A)");
            string key = cur + "|" + curAct + "|" + path + "|" + __result;
            if (_startSeen.Count >= 400 || !_startSeen.Add(key)) return;

            Plugin.Log?.LogInfo($"[连段模组:起招] StartSkill -> {__result}  " +
                                $"链段数={cnt} 起了 Cur={cur}(\"{curAct}\")  来路={path}");
        }
        catch (Exception e) { Once("StartSkillProbe", e); }
    }

    // ---------------- JS 驱动漏斗 ----------------
    private static readonly HashSet<string> _midSeen = new HashSet<string>();
    private static long _searchMain, _searchFall;

    /// <summary>搜索调用计数器（只计数，按 500 次打一条）。</summary>
    public static void SearchCountPrefix(string __0)
    {
        try
        {
            // 两个方法共用一个 prefix，靠参数名区分不了 —— 用调用栈深度无意义，
            // 所以统一计数，再由 MidByOrderPostfix 打总量。够用了。
            _searchMain++;
            if (_searchMain % 500 == 0)
                Plugin.Log?.LogInfo($"[连段模组:计数] 搜索函数累计被调用 {_searchMain} 次");
        }
        catch { }
    }

    /// <summary>JS 驱动连段的漏斗：谁、把链推到哪个 Order。</summary>
    public static void MidByOrderPostfix(GamePlay.PlayerSkillChain __instance, int startOrder, bool __result)
    {
        try
        {
            if (CfgEnabled?.Value != true || __instance == null) return;

            int cnt = 0, idx = -1, curOrder = -1;
            string curAct = "", resOrder = "-", resAct = "";
            try
            {
                var list = __instance.SkillList;
                cnt = SafeCount(list);
                for (int i = 0; i < cnt; i++)
                {
                    var sk = TryGet(list, i);
                    if (sk == null) continue;
                    int o = OrderOf(sk);
                    if (o == startOrder) { resOrder = o.ToString(); resAct = ActionOf(sk); idx = i; }
                }
                var c = __instance.Cur;
                if (c != null) { curOrder = c.Order; curAct = c.Action ?? ""; }
            }
            catch { }

            string key = startOrder + "|" + resOrder + "|" + __result;
            if (_midSeen.Count >= 300 || !_midSeen.Add(key)) return;

            Plugin.Log?.LogInfo($"[连段模组:JS驱动] FindAndStartSkillFromMidByOrder({startOrder}) -> {__result}  " +
                                $"段数={cnt} 目标@{idx}(Order={resOrder} \"{resAct}\")  " +
                                $"当前 Order={curOrder} \"{curAct}\"  搜索累计={_searchMain}");
        }
        catch (Exception e) { Once("MidByOrder", e); }
    }
    private static readonly HashSet<string> _cdSeen = new HashSet<string>();

    /// <summary>冷却真身探针：SetCoolDown 之后读一次 [+0x54]，并带上当前段的动作名。</summary>
    public static void CoolDownPostfix(GamePlay.PlayerSkillChain __instance)
    {
        try
        {
            if (CfgEnabled?.Value != true || __instance == null) return;
            if (_cdLogLeft <= 0) return;
            long v = Marshal.ReadInt64(__instance.Pointer, 0x54);
            string act = "";
            try { var c = __instance.Cur; if (c != null) act = c.Action ?? ""; } catch { }
            if (!_cdSeen.Add($"{act}|{v}")) return;
            _cdLogLeft--;
            Plugin.Log?.LogInfo($"[连段模组:设冷却] SetCoolDown 后 [+0x54]={v / 4294967296.0:F3}  当前段=\"{act}\"");
        }
        catch (Exception e) { Once("SetCoolDown", e); }
    }

    // ==================================================================
    //  合成动作名 + 动作表注入
    //  —— "创造一个不存在的『平X新』，让它使用原动作的动作和内容"
    // ==================================================================

    /// <summary>
    /// 给"原生属于别的链"的动作在本链里造一个独有名字。
    ///
    /// 名字必须**不可能和原生数据碰撞**（本项目已经被"用内容特征当标记"坑过两次，
    /// 见 PROJECT_STATE 的教训），所以用 `ace` 前缀 + 递增序号，不与任何游戏内命名风格重合。
    /// </summary>
    /// <summary>GameActionLogic 的实例大小下界。
    /// export 自 dump.cs：最后一个字段 `m_muteSkillDataGenerated` 在 0x28D，
    /// 即字段区到 0x28E 为止，IL2CPP 按 8 对齐 → 不小于 0x290。
    /// 运行时优先问 il2cpp_class_instance_size，问不到才用它兜底。</summary>
    private const int GAL_MIN_SIZE = 0x290;

    /// <summary>
    /// 复制一个 GameActionLogic。
    ///
    /// **不能用 `src.Clone()`** —— 原版实现是编辑器专用的，运行时直接抛
    /// `Exception: GameActionLogic Clone not avaliable outside editor`（实测日志）。
    ///
    /// 所以自己来：new 一个同类对象，把【字段区】整块搬过去。
    /// GameActionLogic 是纯 IL2CPP 类（不是 UnityEngine.Object），没有托管状态，
    /// 全部数据都在实例内存里，整块搬运是完备的。
    ///
    /// 关于 GC 安全性 —— 这里**故意**不走写屏障，理由：
    ///   被复制的每一个引用型字段（string / List / ActionSwitchData …）所指向的对象，
    ///   在拷贝完成时**都已经被 src 引用着**，而 src 活在 ActionLogicGroup 里、必然可达。
    ///   也就是说这些引用对象本来就是活对象，不存在"漏标"的可能，
    ///   写屏障在这里不承担任何正确性职责。
    ///   拷贝之后我们对 `Name` 的改写走的是 Il2CppInterop 正常 setter，屏障照走。
    /// </summary>
    private static GamePlay.GameActionLogic CloneAction(GamePlay.GameActionLogic src)
    {
        try
        {
            if (src == null || src.Pointer == IntPtr.Zero) return null;

            var dst = new GamePlay.GameActionLogic();
            if (dst == null || dst.Pointer == IntPtr.Zero) return null;

            int size = 0;
            try
            {
                IntPtr klass = Marshal.ReadIntPtr(src.Pointer);   // Il2CppObject.klass @0x0
                if (klass != IntPtr.Zero)
                    size = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_instance_size(klass);
            }
            catch (Exception e) { Once("CloneAction|size", e); }

            if (size < GAL_MIN_SIZE)
            {
                if (size != 0)
                    WarnOnce("CloneAction|size", $"il2cpp_class_instance_size 返回 {size}，小于已知下界 {GAL_MIN_SIZE}，改用下界");
                size = GAL_MIN_SIZE;
            }

            int bytes = size - 0x10;      // 头 0x10 字节 = klass(0x0) + monitor(0x8)，不能动
            if (bytes <= 0) return null;

            var buf = new byte[bytes];
            Marshal.Copy(src.Pointer + 0x10, buf, 0, bytes);
            Marshal.Copy(buf, 0, dst.Pointer + 0x10, bytes);

            // ★ ActionSwitchs 同理必须隔离（否则改克隆体会污染原版）—— 见 IsolateSwitches。
            //   注意这里不知道源动作名，所以只做"给一张自己的表"；
            //   剔除动作在 MakeSkill 里按源动作名做（那里知道 action）。
            try
            {
                var srcSw = dst.ActionSwitchs;
                if (srcSw != null)
                {
                    int sn = 0; try { sn = srcSw.Count; } catch { }
                    var ownSw = new Il2CppSystem.Collections.Generic.List<GamePlay.ActionSwitchData>();
                    for (int i = 0; i < sn; i++)
                    {
                        GamePlay.ActionSwitchData e = null;
                        try { e = srcSw[i]; } catch { }
                        if (e != null) ownSw.Add(e);
                    }
                    dst.ActionSwitchs = ownSw;
                }
            }
            catch (Exception e) { Once("CloneAction|Switchs", e); }

            // ★★★★★ 【必须隔离的一处】TimeScales 是引用型字段，整块拷贝之后
            //   克隆体和源对象【共用同一个 List 对象】(@0xA0 List<ActionTimeScaleRange>)。
            //   变速模块往克隆体注入倍率时，会连原版动作一起改 ——
            //   表现为"我们链上的佩利诺尔污染了原版佩利诺尔"。必须给它一张自己的表。
            try
            {
                dst.TimeScales = new Il2CppSystem.Collections.Generic.List<GamePlay.ActionTimeScaleRange>();
            }
            catch (Exception e) { Once("CloneAction|TimeScales", e); }

            return dst;
        }
        catch (Exception e) { Once("CloneAction", e); return null; }
    }

    private static readonly HashSet<string> _warned = new HashSet<string>();

    /// <summary>只喊一次的警告（Once 的兄弟版，收字符串）。</summary>
    private static void WarnOnce(string tag, string msg)
    {
        try { if (_warned.Add(tag)) Plugin.Log?.LogWarning($"[连段模组:{tag}] {msg}"); } catch { }
    }

    private static string SynthName(string srcAction, int order)
    {
        // 同一段重复走 MakeSkill 时（InitSkills 会跑好几轮）名字必须稳定，否则会造出一堆克隆。
        //
        // ★★★★★ 但"同一段"的判据是【源动作 + 该段自己的 Order】，**不是只看源动作**。
        //
        //   原实现是 `foreach (kv in _synth) if (kv.Value == srcAction) return kv.Key;` ——
        //   只看源动作，于是**同一个动作在一条链里出现两次时，两段共用同一个合成名**。
        //   实测（链转储是 Order:Name 格式）：
        //       902:ace3_902   904:ace3_902      ← 两段名字相同
        //       905:ace5_905   908:ace5_905
        //       903:ace4_903   909:ace4_903
        //   名字不唯一 → 一切"按动作名认领/挂载"的机制都区分不了这两段
        //   （链[2] 抢走 attackAEX 是同一个根因的另一面）。
        //   而"空中 A2/A3/A4 三段同源"正好是这个场景，会把三段一起挂上。
        //   ⚠ 名字只用于认领与我们的登记表，链的推进走 Order + PreSkillOrder，所以唯一化不影响已有连段。
        // ★ 判据是【源动作 + 该段自己的 Order】，不是只看源动作。
        //   （2026-10-03 曾为排查崩溃暂时还原成"只按源动作"，现已确认崩溃真因是注入循环
        //     扫到了 boss 的动作表 —— 已由 InjectPrefix 的 IsPlayerSide 准入判据修掉，
        //     与名字唯一化无关，所以这里恢复。）
        //   不唯一的后果很具体：空中 A2 的三个克隆段共用名字，
        //   `AttachBullets` 按动作名挂载就区分不了 → 三段一起命中同一个纹章（实测）。
        string segKey = srcAction + "|" + order.ToString();
        string cached;
        if (_synthBySeg.TryGetValue(segKey, out cached)) return cached;

        string n = $"ace{++_synthSeq}_{order}";
        _synth[n] = srcAction;
        _synthBySeg[segKey] = n;
        return n;
    }

    /// <summary>
    /// Order → 我们给那一段起的合成动作名。不是我们的段返回 null。
    ///
    /// 给"按 Order 挂载"用（[纹章解放] AttachBullets 的键可以写纯数字）：
    /// 合成名是 `aceN_&lt;order&gt;`，N 是全局递增序号、配置里没法预知，
    /// 而 Order 是确定的 —— 写 Order 就不必"先跑一遍看日志才知道它叫什么"。
    /// </summary>
    internal static string NameOfOrder(int order)
    {
        try
        {
            IntPtr p;
            if (!_madeByOrder.TryGetValue(order, out p) || p == IntPtr.Zero) return null;
            string n;
            return _synthApplied.TryGetValue(p, out n) ? n : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ 冲刺-A 的后续接管
    //   2026-10-04: 「把 布2/平3/布3 追加到链[1]、并拦掉 rush → 平1 那条边」这一版实测**仍然接不上**，
    //   已按用户要求整体回滚（Chain1 恢复 16 段、闸门规则删除）。
    //   留档的结论（避免以后再走一遍）：
    //     · 链[6] DashAttack 的 `链Input = 0` ⇒ 它不参与输入搜索，链[6] 只是"按名注册表"；
    //     · rush 期间按攻击，搜索落在链[1]，Cur 为空 ⇒ 走兜底 ⇒ 命中原生那套起手段；
    //     · 在链[1] 末尾追加起手段 + 用闸门拦掉 907 —— 实测没能改变落点。
    //   真要重做的话，下一步应该先打**兜底路径的候选清单**（当前只记了"最终返回谁"，
    //   没记"沿途被哪道过滤挡掉的"），不要直接上改动。

    /// <summary>
    /// 把 _synth 里登记的所有合成动作真正注入到 ActionLogicGroup 的动作表。
    /// 幂等：按 ActionLogicGroup 原生指针去重。
    /// </summary>
    private static void EnsureInjected(GamePlay.ActionMgr mgr)
    {
        IntPtr gp = IntPtr.Zero;
        try
        {
            if (mgr == null) return;
            gp = ReadPtr(mgr.Pointer, 0x18);          // ActionMgr.m_ActionGroup
            if (gp == IntPtr.Zero) return;
            if (_synth.Count == 0) return;
            if (_foreignGroups.Contains(gp)) return;      // 已知不是目标表
            if (!_injected.TryGetValue(gp, out var have)) { have = new HashSet<string>(); _injected[gp] = have; }
            if (have.Count >= _synth.Count) { bool all = true; foreach (var k in _synth.Keys) if (!have.Contains(k)) { all = false; break; } if (all) return; }

            var group = new GamePlay.ActionLogicGroup(gp);
            var acts = group?.Actions;
            if (acts == null) { WarnOnce("注入", "ActionLogicGroup.Actions 为 null"); return; }

            int cnt = 0;
            try { cnt = acts.Count; } catch { }
            if (cnt == 0) { WarnOnce("注入", "ActionLogicGroup.Actions 为空"); return; }

            // m_cacheAction: Dictionary<string, GameActionLogic> @0x88 —— 查表走的是它
            Il2CppSystem.Collections.Generic.Dictionary<string, GamePlay.GameActionLogic> cache = null;
            try
            {
                IntPtr dp = ReadPtr(gp, 0x88);
                if (dp != IntPtr.Zero)
                    cache = new Il2CppSystem.Collections.Generic.Dictionary<string, GamePlay.GameActionLogic>(dp);
            }
            catch (Exception e) { Once("注入|dict", e); }

            int done = 0;
            int foundAny = 0;
            foreach (var kv in _synth)
            {
                if (have.Contains(kv.Key)) continue;
                try
                {
                    // 1) 找源动作（按 Name 精确匹配）
                    GamePlay.GameActionLogic src = null;
                    for (int i = 0; i < cnt; i++)
                    {
                        GamePlay.GameActionLogic a = null;
                        try { a = acts[i]; } catch { }
                        if (a == null) continue;
                        string nm = null; try { nm = a.Name; } catch { }
                        if (string.Equals(nm, kv.Value, StringComparison.OrdinalIgnoreCase)) { src = a; break; }
                    }
                    if (src == null)
                    {
                        WarnOnce("注入|miss|" + kv.Value, $"动作表里找不到源动作 \"{kv.Value}\"，\"{kv.Key}\" 注入失败（该段仍会用原名）");
                        continue;
                    }
                    foundAny++;

                    // 2) 克隆 —— Name 是身份、Animate 是皮，只换身份
                    var clone = CloneAction(src);
                    if (clone == null) { WarnOnce("注入|clone", $"克隆 \"{kv.Value}\" 失败"); continue; }

                    clone.Name = kv.Key;
                    ClearSwitchesIfNeeded(clone, kv.Value);

                    // ★★★★★ 2026-10-02 第六轮 —— 链冷却的真正来源：动作自己的 CDList。
                    //
                    // GameActionLogic:
                    //     0x120  public List<ActionCountDownData> CDList;   [ActionHidableList("CD",...)]
                    //
                    // 探针实测（[输入推进] 的 闸门[CD=..]）：
                    //     attack1   进入时 CD=0.300
                    //     attack2   进入时 CD=0.350
                    //     ace1_903  进入时 CD=0.700     ← 跟技能行的 ActdurStrict/Timeout 都无关
                    // 改技能行的 ActdurStrict 到 0.300 后 CD 依然 0.700 —— 说明它不来自技能行。
                    //
                    // 我们的克隆是整块搬 attackAEX 的 GameActionLogic，于是把布鲁诺1 自己的 CD
                    // 一起继承了。而 DoUpdateAndCheckInputSucc 里 `[chain+0x54] > 0 → return false`，
                    // 冷却期间输入全丢；冷却一结束 SetCoolDown 又顺手 Reset 游标。
                    // 0.7 的冷却配上 0.7 的 Timeout，可输入窗口正好为零 —— 这就是"接不上平3"。
                    //
                    // 修：这一段既然是替掉某条平A，就沿用那条平A的冷却表（和 Timeout/ActdurStrict 同一原则）。
                    try
                    {
                        if (_synthHost.TryGetValue(kv.Key, out var hostAct) && !string.IsNullOrEmpty(hostAct))
                        {
                            for (int i = 0; i < cnt; i++)
                            {
                                GamePlay.GameActionLogic h = null;
                                try { h = acts[i]; } catch { }
                                if (h == null || ReferenceEquals(h, src)) continue;
                                string hn = null; try { hn = h.Name; } catch { }
                                if (!string.Equals(hn, hostAct, StringComparison.OrdinalIgnoreCase)) continue;
                                try { clone.CDList = h.CDList; } catch (Exception e) { Once("注入|cd", e); }
                                break;
                            }
                        }
                    }
                    catch (Exception e) { Once("注入|cd|outer", e); }
                    // 3) 名字变了要重建内部缓存（原方法签名带默认参数 force=false）
                    try { clone.RebuildCacheConfigAtLoading(true); } catch (Exception e) { Once("注入|rebuild", e); }

                    // 4) 塞进 List 和 Dictionary —— 两个都塞，Item 索引器走缓存、遍历走 List
                    try { acts.Add(clone); } catch (Exception e) { Once("注入|add", e); }
                    if (cache != null)
                    {
                        try { cache[kv.Key] = clone; } catch (Exception e) { Once("注入|dictset", e); }
                    }

                    _injectedRoots.Add(clone);        // ★ root 住，防 GC 收走原生对象
                    have.Add(kv.Key);
                    done++;

                    if (_synthLogLeft-- > 0)
                    {
                        string anim = "?"; try { anim = clone.Animate ?? ""; } catch { }
                        string next = "?"; try { next = clone.NextAction ?? ""; } catch { }
                        Plugin.Log?.LogInfo($"[连段模组:注入] \"{kv.Key}\" = 克隆自 \"{kv.Value}\"  " +
                                            $"Animate=\"{anim}\" NextAction=\"{next}\"  " +
                                            $"CD列表={(clone.CDList == null ? -1 : clone.CDList.Count)} " +
                                            $"表内总数 {cnt} -> {cnt + done}  缓存={(cache != null ? "已写" : "跳过")}");
                    }
                }
                catch (Exception e) { Once("注入|item", e); }
            }

            Plugin.Log?.LogInfo($"[连段模组:注入] ActionLogicGroup 0x{gp.ToInt64():X} 处理完毕: " +
                                $"源动作表 {cnt} 个, 本次注入 {done} 个, 累计 {have.Count}/{_synth.Count}");

            // ★★★ 2026-10-02 翻车点，务必看清：
            //   回滚**只能在本表确实含有源动作时**才做。
            //
            //   游戏里每个角色/模型都有一张自己的 ActionLogicGroup（实测 38 / 82 / 186 个动作的都有），
            //   而 _synthApplied 和我们的段是**全局**的。
            //   第一版没这个判断，于是：某个别的角色的 38 动作表先被 ChangeAction 碰到 →
            //   里面当然找不到 attackAEX → 判"注入失败" → 把**所有**段改回 attackAEX →
            //   等真正的 ES 表（186 个）注入成功时，段的名字早被撸回去了，
            //   表现就是"注入日志明明成功了，段读出来还是 attackAEX"。
            //
            //   所以：本表一个源动作都没有 → 那不是目标表，拉黑、扫都不扫、**绝不回滚**。
            if (foundAny == 0)
            {
                _foreignGroups.Add(gp);
                WarnOnce("注入|foreign", "遇到不含我们源动作的动作表（其它角色的），已跳过");
                return;
            }

            // ★ 兜底：没注入成功的合成名，把我们那些段的名字改回源动作名。
            //   宁可"回到能打平A但接不上布鲁诺"（可用），也不要"整条链完全没反应"（不可用）。
            int rolled = 0;
            foreach (var kv in _synthApplied)
            {
                if (have.Contains(kv.Value)) continue;
                if (!_synth.TryGetValue(kv.Value, out var srcName)) continue;
                try
                {
                    var fix = new SkillActivateFixedPoint(kv.Key);
                    if (fix != null) { fix.Action = srcName; rolled++; }
                }
                catch (Exception e) { Once("注入|rollback", e); }
            }
            if (rolled > 0)
                WarnOnce("注入|rollback", $"有 {rolled} 段因动作表注入失败已改回原名（会退化成「能打平A但接不上布鲁诺」）");
        }
        catch (Exception e) { Once("注入|outer", e); }
    }

    /// <summary>
    /// 挂在 ActionMgr.ChangeAction 的 **prefix**：
    /// 必须在游戏按名字查动作表【之前】把合成动作放进去，否则第一次必然查不到。
    /// 不限"当前动作是不是我们的"—— 第一次 ChangeAction（"born"/"stand"）时就把全表铺好。
    /// </summary>
    public static void InjectPrefix(GamePlay.ActionMgr __instance, string name)
    {
        try
        {
            if (CfgEnabled?.Value != true) return;
            if (_synth.Count == 0) return;

            // ★★★ 准入判据：只对【玩家侧】的动作表工作。见 IsPlayerSide 的注释。
            //   这一条同时修掉三件事：
            //     · 作用域 —— 不再去扫 boss 的动作表
            //     · 悬空指针 —— _injected/_foreignGroups 里只会存在玩家那 1~2 个条目
            //     · 不收敛的重扫 —— 外来组根本不会被访问
            if (!IsPlayerSide(__instance)) { SkipLog(__instance); return; }

            EnsureInjected(__instance);
        }
        catch (Exception e) { Once("Inject", e); }
    }

    /// <summary>
    /// 这个 ActionMgr 是不是"玩家侧"的 —— 注入循环的**唯一准入判据**。
    ///
    /// 为什么必须有它
    /// ──────────────
    /// `InjectPrefix` 挂在 `ActionMgr.ChangeAction` 的 prefix 上，**每个 actor
    /// 每次切动作都会跑**。原实现不看 Owner，对任何 actor 都取 m_ActionGroup(0x18)
    /// 并**整表扫描**（82 个动作名找我们的 16 个）。
    ///
    /// 后果（2026-10-03 夜实测，见 PROJECT_STATE §10）：
    ///   · 训练场只有一个常驻木桩 → 组少且地址稳定 → 一切正常
    ///   · **BossRush 里 boss/小怪不断生成销毁** → 每几秒就有一批新组、新地址
    ///     → `_foreignGroups` 无限膨胀、每个新组都要整表重扫（永远 `累计 0/N`）
    ///   · 最终表现为**原生崩溃**（无托管异常、日志干净截断），
    ///     因为缓存是按**裸指针**索引的，actor 销毁后地址可能被复用
    ///
    /// 三条放行条件，覆盖"影子/分身有没有自己的动作表"两种可能：
    ///   ① `Owner` 就是本地玩家
    ///   ② `Owner.Caster` 是本地玩家      —— 影子/分身沿这条链认祖归宗
    ///   ③ `Owner.RootCaster` 是本地玩家  —— 再套一层召唤物时用这条兜底
    /// 其余（尤其 boss 那种 82 个动作的表）一律不碰。
    ///
    /// ⚠ `IsLocalPlayerActor` 是**严格判等**（`self == actor`），影子自身不满足 ①，
    ///   所以 ②③ 不是可选项，是**必须**的 —— 本项目栽过一次：
    ///   "ES 的招式大量由影子/分身 Actor 打出，Owner 不是 PlayerSelf"。
    /// </summary>
    private static bool IsPlayerSide(GamePlay.ActionMgr mgr)
    {
        try
        {
            if (mgr == null) return false;
            var o = mgr.Owner;
            if (o == null) return false;

            if (DashInvincible.IsLocalPlayerActor(o)) return true;

            try { var c = o.Caster;     if (c != null && DashInvincible.IsLocalPlayerActor(c)) return true; } catch { }
            try { var r = o.RootCaster; if (r != null && DashInvincible.IsLocalPlayerActor(r)) return true; } catch { }

            return false;
        }
        catch (Exception e) { Once("IsPlayerSide", e); return false; }
    }

    /// <summary>
    /// 记录"这个组被跳过了"。**只打地址和 Owner 类型名，绝不读它的动作表** ——
    /// 旧代码正是靠读表才发现"这不是我们的表"，而读表就是风险本身。
    /// 上限 40 条，够看清"被跳过的是些什么东西"，又不会被 boss 潮刷屏。
    /// </summary>
    private static readonly HashSet<IntPtr> _skipLogged = new HashSet<IntPtr>();

    private static void SkipLog(GamePlay.ActionMgr mgr)
    {
        try
        {
            if (_skipLogged.Count >= 40) return;
            IntPtr p = mgr?.Pointer ?? IntPtr.Zero;
            if (p == IntPtr.Zero || !_skipLogged.Add(p)) return;

            string t = "?";
            try { var o = mgr.Owner; t = (o == null) ? "null" : o.GetType().Name; } catch { }

            Plugin.Log?.LogInfo($"[连段模组:注入] 跳过非玩家组 0x{p.ToInt64():X} (Owner={t})" +
                                $" —— 未读它的动作表");
        }
        catch { }
    }

    // ------------------------------------------------------------------ 列表操作（优先托管 API，失败退裸指针）

    private static Il2CppSystem.Collections.Generic.List<GamePlay.PlayerSkill> GetList(IntPtr chainPtr)
    {
        try
        {
            var chain = new GamePlay.PlayerSkillChain(chainPtr);
            return chain?.SkillList;
        }
        catch (Exception e) { Once("GetList", e); return null; }
    }

    private static int SafeCount(Il2CppSystem.Collections.Generic.List<GamePlay.PlayerSkill> l)
    {
        try { return l.Count; } catch { return 0; }
    }

    private static GamePlay.PlayerSkill TryGet(Il2CppSystem.Collections.Generic.List<GamePlay.PlayerSkill> l, int i)
    {
        try { return l[i]; } catch { return null; }
    }

    private static void SafeRemoveAt(Il2CppSystem.Collections.Generic.List<GamePlay.PlayerSkill> l, int i)
    {
        try { l.RemoveAt(i); } catch (Exception e) { Once("RemoveAt", e); }
    }

    /// <summary>空中链(`AttackAir`)的组号。见 PROJECT_STATE 的链清单转储。</summary>
    private const int AIR_GROUP = 4;

    /// <summary>
    /// 起跳系动作名 —— 前缀匹配（`jump` / `jump2` / `jump3` / `jump_down` …）。
    ///
    /// 为什么不用"落地"当判据：`drop*` / `fallmdown*` 在空中也会出现 → 连段打到一半被清，
    /// 空4 直接没了（实测）。**起跳没有这个问题 —— 连段中途不会再跳。**
    /// </summary>
    private static bool IsJumpAction(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        return n.StartsWith("jump", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 上挑系动作（`AttackUp` / `AttackUp2` …）。
    ///
    /// 存在的唯一理由：把"上挑的 jump"从"普通跳的 jump"里分出来 —— 两者的 jump 完全同名，
    /// 只有**跟着的那个动作**不同。见 NotifyPostfix 里【延后一拍】的说明。
    /// </summary>
    private static bool IsUpAttackAction(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        return n.StartsWith("AttackUp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>上一拍见到了 jump*，还没决定要不要重置。见 NotifyPostfix 的【延后一拍】。</summary>
    private static bool _pendingJumpReset;

    /// <summary>
    /// 清掉空中链的游标 —— 复刻原版的"落地重置"。
    /// 两个方法都调：`ClearCurSkill` 摘当前段、`Reset` 归零状态；只调一个可能留下半个状态。
    /// （这两个方法我们本来就挂着钩子 —— `ClearCurPrefix`，当初为了查"谁在清游标"。）
    /// </summary>
    private static void ClearAirCursor(GamePlay.PlayerSkillMgr mgr)
    {
        try
        {
            IntPtr arr = ReadPtr(mgr.Pointer, OFF_CHAINS);
            if (arr == IntPtr.Zero) return;
            int n = ReadI32(arr, 0x18);
            if (AIR_GROUP >= n) return;
            IntPtr chain = ReadPtr(arr, 0x20 + AIR_GROUP * 8);
            if (chain == IntPtr.Zero) return;

            var ch = new GamePlay.PlayerSkillChain(chain);
            try { ch.ClearCurSkill(); } catch { }
            try { ch.Reset(); } catch { }
        }
        catch (Exception e) { Once("ClearAirCursor", e); }
    }

    private static void SafeInsertAt(Il2CppSystem.Collections.Generic.List<GamePlay.PlayerSkill> l, int i,
                                     GamePlay.PlayerSkill v)
    {
        try { l.Insert(i, v); } catch (Exception e) { Once("InsertAt", e); }
    }

    /// <summary>这条链走"原地替换"模式吗。逗号分隔组号，如 "4"。</summary>
    private static bool IsInPlace(int g)
    {
        try
        {
            var raw = CfgInPlace?.Value;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            foreach (var t in raw.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int v;
                if (int.TryParse(t.Trim(), out v) && v == g) return true;
            }
        }
        catch { }
        return false;
    }

    private static void SafeInsert(Il2CppSystem.Collections.Generic.List<GamePlay.PlayerSkill> l, int i, GamePlay.PlayerSkill v)
    {
        try { l.Insert(i, v); } catch (Exception e) { Once("Insert", e); }
    }

    /// <summary>还原：把注入的段全删掉，把原来那几段插回原位。
    /// 关掉 RewriteEnabled 时调用 —— 这是"关掉开关"真正生效的地方。</summary>
    private static void Revert(GamePlay.PlayerSkillMgr mgr)
    {
        if (mgr == null || _bkOrig.Count == 0) return;
        try
        {
            IntPtr arr = ReadPtr(mgr.Pointer, OFF_CHAINS);
            if (arr == IntPtr.Zero) return;
            int arrLen = ReadI32(arr, 0x18);

            foreach (var kv in new List<int>(_bkOrig.Keys))
            {
                int g = kv;
                if (g >= arrLen) continue;
                IntPtr chainPtr = ReadPtr(arr, 0x20 + g * 8);
                if (chainPtr == IntPtr.Zero) continue;
                var list = GetList(chainPtr);
                if (list == null) continue;

                var orig = _bkOrig[g];
                if (orig == null || orig.Count == 0) continue;

                // ★ 不猜、不筛、不判断当前位置 —— 直接把整条链清空，按快照原样重建。
                //   上一版是"删掉 Order>=900 的、再把备份插回去"，任何一处状态假设错了
                //   就会丢段（实测丢了 9 段）。整条重建没有这个风险。
                int had = SafeCount(list);
                while (SafeCount(list) > 0) SafeRemoveAt(list, SafeCount(list) - 1);
                for (int i = 0; i < orig.Count; i++) SafeInsert(list, i, orig[i]);

                Plugin.Log?.LogInfo($"[连段模组] 链[{g}] 已还原: 原 {had} 段 -> 按快照重建 {SafeCount(list)} 段 " +
                                    $"(快照共 {orig.Count} 段)");
            }
        }
        catch (Exception e) { Once("Revert", e); }
        _bkOrig.Clear();
        _bkStart.Clear();
        _ours.Clear();
        _applied.Clear();
    }

    private static readonly Dictionary<int, int> _bkStart = new Dictionary<int, int>();
    private static readonly Dictionary<int, List<GamePlay.PlayerSkill>> _bkOrig
        = new Dictionary<int, List<GamePlay.PlayerSkill>>();

    /// <summary>链里有没有我们注入的段（Order >= 900 的哨兵值）。
    /// 这个判据只看一个不可能与原生数据碰撞的数字，**不看名字/方向/数量** ——
    /// 因为那些正是我们要改的东西，拿它们当判据必然自噬。</summary>
    private static bool HasOurMarker(Il2CppSystem.Collections.Generic.List<GamePlay.PlayerSkill> l, int cnt)
    {
        try
        {
            for (int i = 0; i < cnt; i++)
            {
                var sk = TryGet(l, i);
                if (sk == null) continue;
                IntPtr proto = ReadPtr(sk.SkillActivate?.Pointer ?? IntPtr.Zero, OFF_WRAP_DATA);
                if (proto == IntPtr.Zero) continue;
                if (ReadI32(proto, OFF_ORDER) >= 900) return true;
            }
        }
        catch { }
        return false;
    }

    // ------------------------------------------------------------------ 工具

    private static string ActionOf(GamePlay.PlayerSkill sk)
    {
        try
        {
            IntPtr protoPtr = ReadPtr(sk.SkillActivate?.Pointer ?? IntPtr.Zero, OFF_WRAP_DATA);
            if (protoPtr == IntPtr.Zero) return null;
            IntPtr s = ReadPtr(protoPtr, OFF_ACTION);
            return s == IntPtr.Zero ? null : Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(s);
        }
        catch { return null; }
    }

    private static int DirOf(GamePlay.PlayerSkill sk)
    {
        try
        {
            IntPtr protoPtr = ReadPtr(sk.SkillActivate?.Pointer ?? IntPtr.Zero, OFF_WRAP_DATA);
            return protoPtr == IntPtr.Zero ? -1 : ReadI32(protoPtr, OFF_INPUT_DIR);
        }
        catch { return -1; }
    }

    private static int _verifyLeft = 4;

    private static string MStr(IntPtr p, int off)
    {
        IntPtr s = ReadPtr(p, off);
        if (s == IntPtr.Zero) return "<null>";
        try { return Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(s); } catch { return "<err>"; }
    }

    // ================================================================
    //  段 / 链 声明
    // ================================================================

    private sealed class Seg
    {
        public string Action = "";
        public int Dir = -1;      // -1=继承源动作; 0=任意 1=上 2=下 3=前 4=后 5=无方向
        public bool Entry;        // 尾缀 '+' = 允许作为起手段（前驱清空）
        public bool Remove;       // 前缀 '-' = 只移除原生那一格，不克隆
    }

    private sealed class ChainSpec
    {
        public int Group = 1;
        public readonly List<Seg> Segs = new List<Seg>();
    }

    private static List<ChainSpec> _specs;
    private static string _specsRaw;

    private static readonly Dictionary<string, int> DIRMAP = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        {"任意",0},{"any",0},{"上",1},{"up",1},{"下",2},{"down",2},
        {"前",3},{"front",3},{"后",4},{"back",4},{"无",5},{"none",5}
    };

    /// <summary>解析全部链声明。</summary>
    private static List<ChainSpec> Chains()
    {
        var sb = new System.Text.StringBuilder();
        if (CfgChains != null) foreach (var e in CfgChains) sb.Append(e?.Value).Append((char)1);
        var raw = sb.ToString();
        // ★ 按【值】比较 —— 这里拼出来的是新字符串，用 ReferenceEquals 会永远 miss（栽过一次）
        if (raw == _specsRaw && _specs != null) return _specs;
        _specsRaw = raw;

        var list = new List<ChainSpec>();
        if (CfgChains != null)
        {
            foreach (var e in CfgChains)
            {
                var v = e?.Value;
                if (string.IsNullOrWhiteSpace(v)) continue;

                var spec = new ChainSpec();
                int bar = v.IndexOf('|');
                if (bar > 0)
                {
                    int g;
                    if (int.TryParse(v.Substring(0, bar).Trim(), out g)) spec.Group = g;
                }
                else if (bar == 0)
                {
                    // 没有组号前缀，用默认组
                }
                string body = bar < 0 ? v : v.Substring(bar + 1);

                foreach (var part in body.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = part.Trim();
                    if (t.Length == 0) continue;

                    var seg = new Seg();
                    if (t.EndsWith("+")) { seg.Entry = true; t = t.Substring(0, t.Length - 1).Trim(); }
                    if (t.StartsWith("-")) { seg.Remove = true; t = t.Substring(1).Trim(); }

                    int lb = t.IndexOf('['), rb = t.IndexOf(']');
                    if (lb > 0 && rb > lb)
                    {
                        var d = t.Substring(lb + 1, rb - lb - 1).Trim();
                        int dv;
                        if (DIRMAP.TryGetValue(d, out dv)) seg.Dir = dv;
                        t = (t.Substring(0, lb) + t.Substring(rb + 1)).Trim();
                    }
                    seg.Action = t;
                    if (seg.Action.Length > 0) spec.Segs.Add(seg);
                }
                if (spec.Segs.Count > 0) list.Add(spec);
            }
        }

        _specs = list;
        try
        {
            Plugin.Log?.LogInfo($"  [连段模组] 链声明已解析: {list.Count} 条");
            foreach (var sp in list)
            {
                var sb2 = new System.Text.StringBuilder();
                foreach (var sg in sp.Segs)
                {
                    sb2.Append(sg.Action);
                    if (sg.Remove) sb2.Append('-');
                    if (sg.Entry) sb2.Append('+');
                    if (sg.Dir >= 0) sb2.Append('[').Append(sg.Dir).Append(']');
                    sb2.Append(' ');
                }
                Plugin.Log?.LogInfo($"     组[{sp.Group}] {sp.Segs.Count} 段: {sb2}");
            }
        }
        catch { }
        return list;
    }

    /// <summary>兼容旧配置：没有 ChainN 时，用 Sequence + Group 造一条。</summary>
    private static List<ChainSpec> ChainsOrLegacy()
    {
        var list = Chains();
        if (list.Count > 0) return list;
        var want = ParseSequence();
        if (want.Count == 0) return list;
        var spec = new ChainSpec { Group = CfgGroup?.Value ?? 1 };
        for (int i = 0; i < want.Count; i++) spec.Segs.Add(new Seg { Action = want[i] });
        Plugin.Log?.LogInfo($"[连段模组] 未配置 ChainN，回退到旧 Sequence（组[{spec.Group}]，{want.Count} 段）");
        return new List<ChainSpec> { spec };
    }

    private static List<string> ParseSequence()
    {
        var r = new List<string>();
        var raw = CfgSequence?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return r;
        foreach (var s in raw.Split(new[] { ',', ';', '>', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var v = s.Trim();
            if (v.Length > 0) r.Add(v);
        }
        return r;
    }

    private static IntPtr ReadPtr(IntPtr p, int off)
    {
        if (p == IntPtr.Zero) return IntPtr.Zero;
        IntPtr v = Marshal.ReadIntPtr(p, off);
        long x = v.ToInt64();
        if (x < 0x10000 || x > 0x7FFFFFFFFFFF || (x & 7) != 0) return IntPtr.Zero;
        return v;
    }

    private static int ReadI32(IntPtr p, int off)
    {
        if (p == IntPtr.Zero) return 0;
        try { return Marshal.ReadInt32(p, off); } catch { return 0; }
    }

    private static float Now()
    {
        try { return UnityEngine.Time.realtimeSinceStartup; } catch { return 0f; }
    }

    private static readonly HashSet<string> _errs = new HashSet<string>();
    private static void Once(string where, Exception e)
    {
        if (_errs.Add(where + e.GetType().Name))
            Plugin.Log?.LogWarning($"[连段模组:{where}] 异常(只报一次): {e.Message}");
    }
}
