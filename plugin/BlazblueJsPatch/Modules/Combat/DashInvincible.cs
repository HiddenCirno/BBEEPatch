using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using GamePlay;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 冲刺无敌 / 跳跃无敌 —— 方案 B（运行时 Hook）
///
/// 覆盖范围
/// ────────
///   · 冲刺族（ActionKeyword，默认 "dash"），排除名单 Ultra,UD,UDA
///   · 跳跃族（JumpActionKeyword，默认 "jump"）—— 用户要求"和冲刺同款的全程无敌"。
///     所谓"前0.2秒-全程-后0.2秒"在本模块里的实现就是:
///     动作进行中【一直】给等级（开头那 0.2 秒自然也在内）+ 动作结束【之后】0.2 秒尾巴。
/// 两族共用一套状态（_lastDash / 尾巴 / Level），因为形状完全一样。
///
/// 原理
/// ────
/// 冲刺是「动作」(Action)。动作数据类 GameActionLogic 上挂着
///     List&lt;ActionInvinciple&gt; Invincibility
/// 每帧算出当前无敌等级 -> ActionMgrAttr.Invincipal，命中结算时经 GetInvincipalLevel()
/// 取总等级，与攻击方的 ActionHitData.InvincipalBreak(破无敌) 比较：攻不破 → 打不中。
///
/// ⚠ 历史事故（务必保持下面三道保险）
/// ──────────────────────────────────
/// 第一版有三个错误，叠在一起造成了「全场所有受击判定消失」
/// （ES 打不动敌人、打不碎物体、敌人也打不到 ES）:
///
///   1. 状态是 static 的 —— `_seenDash` / `_lastDashFrame` 被【所有 Actor 共用】。
///      敌人也会冲刺，所以任意一个 Actor 冲刺过，全局标志就翻了。
///   2. 帧号取不到 —— `LockStepManager.Instance?.m_logicFrame` 恒为 0
///      （generic 基类 SingletonBehaviour&lt;T&gt; 的静态 Instance 取不到实例）。
///      于是尾巴判定 `(0 - 0) &lt;= 6` 【恒为真】→ 冲刺一次后永久无敌。
///   3. 没限定阵营 —— 补丁挂在 ActorBase 上，敌人也跟着吃。
///
///   结果：全场无敌 → 命中结算整体被跳过 → 什么都打不中。
///
/// 所以现在:
///   · 状态按 Actor 分桶（不再串台）
///   · 时间源用 UnityEngine.Time.time，且【时钟无效时一律拒绝给无敌】(fail-closed)
///   · 默认只对本地玩家生效
///
/// 帧率
/// ────
/// 本作不跑在 Unity 物理帧上，战斗是自研 lockstep: LockStepManager.InitFps() 写死 30fps
/// （m_driveFps/m_logicFps = 0x1E），逻辑与渲染分离。
/// 但尾段时间本来就是「按秒」的需求，直接用 Time.time 更稳，也不依赖那些取不到的字段。
/// </summary>
internal static class DashInvincible
{
    // 这些是 ConfigEntry —— 配合 BepInEx.ConfigurationManager 可实时改
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;
    internal static BepInEx.Configuration.ConfigEntry<string> CfgKeyword;
    internal static BepInEx.Configuration.ConfigEntry<double> CfgTail;
    internal static BepInEx.Configuration.ConfigEntry<int> CfgLevel;
    internal static BepInEx.Configuration.ConfigEntry<string> CfgExclude;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgPlayerOnly;

    // ---- 跳跃族（2026-10-04 新增: 跳跃要享受和冲刺同款的全程无敌）----
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgJumpEnabled;
    internal static BepInEx.Configuration.ConfigEntry<string> CfgJumpKeyword;
    internal static BepInEx.Configuration.ConfigEntry<string> CfgJumpExclude;

    private static bool Enabled => CfgEnabled?.Value ?? true;
    private static string Keyword => CfgKeyword?.Value ?? "dash";
    private static float TailSeconds => (float)(CfgTail?.Value ?? 0.2);
    private static int Level => CfgLevel?.Value ?? 999;
    private static bool JumpEnabled => CfgJumpEnabled?.Value ?? true;
    private static string JumpKeyword => CfgJumpKeyword?.Value ?? "jump";

    /// <summary>
    /// 动作名是否算「冲刺」。
    ///
    /// ⚠ 排除名单是必需的, 不是可选优化:
    ///   贝德维尔(Ultra)的动作名全都带 Dash —— UltraDashEX / UltraDashAirEX,
    ///   连它产生的纹章动作都叫 UDA(Ultra Dash Attack)。
    ///   只用 Keyword 子串匹配的话, **整个 Ultra 演出期间玩家都是无敌的**。
    ///   而本作"命中结算时只要一方无敌就整个跳过" ——
    ///   Ultra 二段的命中被吃掉, 二段就不成立。
    ///   (实测症状: 贝德维尔的翅膀纹章消失, 因为它是二段命中时沿轨迹释放的。)
    /// </summary>
    private static bool IsDashName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        var ex = CfgExclude?.Value;
        if (!string.IsNullOrWhiteSpace(ex))
            foreach (var k in ex.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (name.IndexOf(k.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return false;

        return name.IndexOf(Keyword, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// 动作名是否算「跳跃」。和冲刺【共用同一套状态与尾巴】:
    /// 玩家按需求要的是"前 0.2 秒 - 全程 - 后 0.2 秒"都无敌,
    /// 而本模块的形状本来就是"处于该族动作 = 全程无敌 + 结束后 0.2s 尾巴",
    /// 所以跳跃只要接进同一个 moving 判定即可, 不需要另起一套状态。
    ///
    /// 实测动作名: jump / jump2 / jump3(三段跳各一档), 干净, 默认无须排除名单。
    /// </summary>
    private static bool IsJumpName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        var ex = CfgJumpExclude?.Value;
        if (!string.IsNullOrWhiteSpace(ex))
            foreach (var k in ex.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (name.IndexOf(k.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return false;

        return name.IndexOf(JumpKeyword, StringComparison.OrdinalIgnoreCase) >= 0;
    }
    private static bool PlayerOnly => CfgPlayerOnly?.Value ?? true;

    /// <summary>给完美闪避用的: 抬到哪个等级来吸收这一击。</summary>
    internal static int LevelValue => Level;

    // 每个 Actor 各自记录「最近一次处于冲刺/跳跃动作」的时刻（两族共用）。
    // 绝不能用 static 单个变量 —— 那会让所有 Actor 互相污染。
    private static readonly Dictionary<IntPtr, float> _lastDash = new Dictionary<IntPtr, float>();

    internal static BepInEx.Configuration.ConfigEntry<bool> CfgTraceLevel;

    private static bool _timeBroken, _playerLookupFailed;
    private static int _enterLogged;
    private static int _lvTrace;

    private static string SafeActionName(ActorBase a)
    {
        try { return a.ActionMgr?.CurrentActionName ?? "-"; }
        catch { return "?"; }
    }

    public static int Apply(Harmony harmony)
    {
        int n = 0;

        // PlayerObj 是 override, ActorBase 是 virtual 基类 —— 两个都挂
        foreach (var tn in new[] { "GamePlay.PlayerObj", "GamePlay.ActorBase" })
        {
            var t = AccessTools.TypeByName(tn);
            if (t == null) { Plugin.Log.LogWarning($"  [冲刺无敌] 找不到类型 {tn}"); continue; }
            var m = AccessTools.Method(t, "GetInvincipalLevel");
            if (m == null) { Plugin.Log.LogWarning($"  [冲刺无敌] 找不到 {tn}.GetInvincipalLevel"); continue; }
            try
            {
                harmony.Patch(m, postfix: new HarmonyMethod(
                    typeof(DashInvincible).GetMethod(nameof(Postfix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)));
                Plugin.Log.LogInfo($"  [冲刺无敌] 已挂钩 {tn}.GetInvincipalLevel");
                n++;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"  [冲刺无敌] 挂钩 {tn} 失败: {e.Message}"); }
        }

        if (n > 0)
            Plugin.Log.LogInfo($"  [冲刺无敌] 动作名含 \"{Keyword}\" 时 -> 等级 {Level}; " +
                               $"结束后 {TailSeconds}s 仍生效; {(PlayerOnly ? "仅本地玩家" : "所有角色")}; " +
                               $"跳跃族 {(JumpEnabled ? $"\"{JumpKeyword}\" 同款全程无敌" : "已关闭")}");
        return n;
    }

    public static void Postfix(object __instance, ref int __result)
    {
        try
        {
            // ---- 观察模式: 不改行为, 只记录每帧/每次命中时的无敌等级 ----
            // 用来搞清楚「闪避档」到底是不是某个特定等级值, 以及冲刺动作的等级曲线。
            // GetInvincipalLevel 是命中结算时被调的, 所以这些采样点就是"被攻击的那一刻"。
            if (CfgTraceLevel?.Value == true && _lvTrace < 150)
            {
                var lvActor = __instance as ActorBase;
                if (lvActor != null)
                {
                    _lvTrace++;
                    Plugin.Log?.LogInfo($"[无敌等级] {lvActor.GetType().Name} action={SafeActionName(lvActor)} lvl={__result}");
                }
            }

            // 注意: 完美闪避【不在这里】触发。
            // GetInvincipalLevel 是每帧都在调的(不是只在中招时), 挂这里会导致
            // "闪一下就时缓一下"。已改挂 PlayerObj.OnBattleDamageBeHitCalcStage,
            // 那个回调只在真的被打中时才调。见 PerfectDodge.cs。

            if (!Enabled) return;

            var actor = __instance as ActorBase;
            if (actor == null) return;

            if (PlayerOnly && !IsLocalPlayer(actor)) return;

            var am = actor.ActionMgr;
            if (am == null) return;

            string name;
            try { name = am.CurrentActionName; }
            catch { return; }

            bool dashing = IsDashName(name);
            bool jumping = JumpEnabled && IsJumpName(name);
            bool moving = dashing || jumping;      // 冲刺族 ∪ 跳跃族 —— 同一套全程+尾巴

            float now = Now();
            var key = KeyOf(actor);

            if (moving)
            {
                // 时钟不可信时不写状态 —— 否则会退化成“永久无敌”
                if (now > 0f) _lastDash[key] = now;

                if (_enterLogged < 20)
                {
                    _enterLogged++;
                    Plugin.Log?.LogInfo($"[冲刺无敌] {(dashing ? "冲刺" : "跳跃")}动作: {name} (t={now:F2})");
                }
            }

            bool active = moving;

            // 尾巴: 三道保险 —— 时钟有效 / 有记录 / 时间没有倒流
            // （冲刺族和跳跃族共用这个时间戳: 冲刺 0.2s 内起跳, 无敌是连着的, 符合"全程"的直觉）
            if (!active && now > 0f && _lastDash.TryGetValue(key, out var t))
                active = now >= t && (now - t) <= TailSeconds;

            if (active && __result < Level)
                __result = Level;

            if (_lastDash.Count > 64) Prune(now);
        }
        catch (Exception e)
        {
            Plugin.Log?.LogError($"[冲刺无敌] Postfix 异常: {e}");
        }
    }

    /// <summary>时钟。取不到就返回 0 —— 调用方据此【拒绝】发放无敌, 而不是当成"时间刚好"。</summary>
    private static float Now()
    {
        try
        {
            var t = Time.time;
            if (t > 0f) return t;
            if (!_timeBroken) { _timeBroken = true; Plugin.Log?.LogWarning("[冲刺无敌] Time.time 无效, 冲刺尾段无敌已停用(冲刺全程仍生效)"); }
            return 0f;
        }
        catch (Exception e)
        {
            if (!_timeBroken) { _timeBroken = true; Plugin.Log?.LogWarning($"[冲刺无敌] 读取 Time.time 失败, 尾段无敌停用: {e.Message}"); }
            return 0f;
        }
    }

    private static IntPtr KeyOf(ActorBase a)
    {
        try { return a.Pointer; }
        catch { return (IntPtr)RuntimeHelpers.GetHashCode(a); }
    }

    // ------------------------------------------------------------------ 给完美闪避补丁用

    /// <summary>这个 Actor 是不是本地玩家。取不到身份时返回 false（失败关闭）。</summary>
    internal static bool IsLocalPlayerActor(ActorBase actor)
    {
        if (actor == null) return false;
        return IsLocalPlayer(actor);
    }

    /// <summary>是不是正处在「冲刺动作」中。直接读动作名 —— 不依赖任何每帧被轮询的 getter。
    ///
    /// ⚠ 故意【只认冲刺，不认跳跃】: 唯一调用方是完美闪避(PerfectDodge)。
    ///   跳跃无敌是加给"挨打时免伤"的, 不等于"跳跃期间被击中算极限闪避"
    ///   —— 后者会和完美闪避的语义搅在一起(用户明确要求两者互斥)。要改请另开开关。</summary>
    internal static bool IsDashingNow(ActorBase actor)
    {
        try
        {
            var am = actor?.ActionMgr;
            if (am == null) return false;
            var n = am.CurrentActionName;
            return IsDashName(n);
        }
        catch { return false; }
    }

    /// <summary>只在本地玩家身上生效。取不到身份时【失败关闭】—— 宁可不生效, 也不能全军无敌。</summary>
    private static bool IsLocalPlayer(ActorBase actor)
    {
        try
        {
            var battle = BattleBase.Cur;
            if (battle == null) return false;

            var self = battle.PlayerSelf;
            if (self == null) return false;

            return ReferenceEquals(self, actor) || self.Pointer == actor.Pointer;
        }
        catch (Exception e)
        {
            if (!_playerLookupFailed)
            {
                _playerLookupFailed = true;
                Plugin.Log?.LogWarning($"[冲刺无敌] 判断本地玩家失败, 已停用(可把 PlayerOnly 设为 false 绕过): {e.Message}");
            }
            return false;
        }
    }

    private static void Prune(float now)
    {
        List<IntPtr> dead = null;
        foreach (var kv in _lastDash)
            if (now - kv.Value > 30f) (dead ??= new List<IntPtr>()).Add(kv.Key);
        if (dead != null)
            foreach (var k in dead) _lastDash.Remove(k);
    }
}
