using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;

namespace BlazblueJsPatch;

/// <summary>
/// 动作结构转储：把 `GameActionLogic` 里"这个动作怎么播完、怎么接下一招"的那几个字段
/// 原样打出来。
///
/// 为什么要它
/// ──────────
/// 平A加速之后会出现"纹章丢、接不上"，根因是**动作时钟被我们推快了，而动画没有**。
/// 要判断这个动作到底是"按时长收招"还是"动画播完才收招"，以及"接续下一招"到底
/// 由哪个字段/哪个时间窗决定，光看 dump.cs 猜不出来 —— 必须把真实数值打出来。
///
/// 字段布局（全部来自 Il2CppDumper 的 dump.cs，走裸指针读，不依赖反射 ——
/// 上一版反射 `GetField("TotalDuration")` 直接返回 null，改用指针后一次就通了）
///
/// GameActionLogic:
///   0x18  RoleName(string)      0x20  Name(string)        0x28  Animate(string)
///   0x30  AnimateDuration(Fp)   0x38  ActionType(int)     0x40  TotalDuration(Fp)
///   0x48  EnableAnimationTimeMap(bool)                    0x50  AnimationTimeMap(Curve)
///   0x68  NextAction(string)    0x70  DropAction(string)  0x98  DeadAction(string)
///   0xC8  AutoEndActionWhenMotionEnds(bool)   ★ 动画播完即收招
///   0x110 ActionSwitchs(List<ActionSwitchData>)  ★ 接招窗口
///   0x118 Interrupt(List<string>)                ★ 谁能打断它
///   0x120 CDList(List<ActionCountDownData>)
///
/// ActionSwitchData（每个元素 = 一条"在这个时间段内可以接某招"的规则）:
///   0x10 TimeStart(Fp)  0x18 TimeEnd(Fp)      ← 基类 ActionTimeRangeTrigerer
///   0x20 TimeCheck(ActionLogicTimeCheckRangeConfig*) → +0x10 StartType / +0x14 Start / +0x1C End
///   0x30 NewActionEx(ActionLogicParamString 结构体)  → +0x38 ConstValue(string) ★ 接哪一招
///   0x50 NewActionMode(int)  0x54 SkillCheck(int)  0x58 SkillGroup(int)
///
/// IL2CPP 容器读法（没有托管包装时）:
///   List&lt;T&gt;: 对象头 0x10 字节 → _items(T[]) @0x10, _size(int) @0x18
///   数组:      对象头 0x10 + bounds(8) + max_length(8) → 元素从 @0x20 开始, 每个 8 字节
/// </summary>
internal static class ActionStructure
{
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;
    internal static BepInEx.Configuration.ConfigEntry<string> CfgActions;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgOnce;

    // ---- GameActionLogic ----
    private const int OFF_ROLE = 0x18, OFF_NAME = 0x20, OFF_ANIM = 0x28;
    private const int OFF_ANIMDUR = 0x30, OFF_ACTTYPE = 0x38, OFF_TOTALDUR = 0x40;
    private const int OFF_TIMEMAP = 0x48;
    private const int OFF_NEXT = 0x68, OFF_DROP = 0x70, OFF_DEAD = 0x98;
    private const int OFF_AUTOEND = 0xC8;
    private const int OFF_SWITCHS = 0x110, OFF_INTERRUPT = 0x118, OFF_CDLIST = 0x120;
    private const int OFF_TIMESCALES = 0xA0;    // List<ActionTimeScaleRange> ★ 动作自带的变速区间
    private const int OFF_HITDATA = 0x190;      // List<ActionHitData> ★ 攻击判定段

    // ---- ActionTimeScaleRange ----
    private const int TS_START = 0x10, TS_END = 0x18;
    private const int TS_RANGE = 0x20;          // TimeRange{Start 0x20, Finish 0x28}
    private const int TS_SCALE = 0x30, TS_TAG = 0x38;

    // ---- ActionSwitchData ----
    private const int SW_TIMESTART = 0x10, SW_TIMEEND = 0x18;
    private const int SW_TIMECHECK = 0x20;
    private const int SW_NEWACTION = 0x38;      // NewActionEx.ConstValue (结构体在 0x30, string 在 +0x8)
    private const int SW_MODE = 0x50, SW_SKILLCHECK = 0x54, SW_SKILLGROUP = 0x58;

    // ---- List<T> ----
    private const int LIST_ITEMS = 0x10, LIST_SIZE = 0x18;
    private const int ARRAY_DATA = 0x20, PTR = 8;
    private const double FP_ONE = 4294967296.0;

    private static readonly HashSet<string> _done = new HashSet<string>();

    public static int Apply(Harmony harmony)
    {
        int n = 0;
        try
        {
            var t = AccessTools.TypeByName("GamePlay.ActionMgr");
            var m = t == null ? null : AccessTools.Method(t, "ChangeAction", new[] { typeof(string) });
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(ActionStructure), nameof(ChangePostfix))));
                Plugin.Log.LogInfo("  [动作结构] 已挂钩 ActionMgr.ChangeAction");
                n++;
            }
            else Plugin.Log?.LogWarning("  [动作结构] 找不到 ActionMgr.ChangeAction");

            // ★ 决定性一问：游戏自己有没有在用「动作变速区间」这套机制？
            //   如果 GetTimeScaleByTime 每帧都被调用，那往 TimeScales 里塞一条记录
            //   就是**通用且不破坏表现**的加速方案 —— 而且是游戏原生代码作用在 dt 上，
            //   动作时钟/动画/位移/事件派发全部同步。
            var gal = AccessTools.TypeByName("GamePlay.GameActionLogic");
            var fp = AccessTools.TypeByName("Unity.Mathematics.FixedPoint.Fp");
            var ts = fp == null ? null : AccessTools.Method(gal, "GetTimeScaleByTime", new[] { fp });
            if (ts != null)
            {
                harmony.Patch(ts, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(ActionStructure), nameof(GetTimeScalePostfix))));
                Plugin.Log.LogInfo("  [动作结构] 已挂钩 GameActionLogic.GetTimeScaleByTime (原生变速机制探针)");
                n++;
            }
            else Plugin.Log?.LogWarning("  [动作结构] 找不到 GameActionLogic.GetTimeScaleByTime");
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [动作结构] 挂钩失败: {e.Message}"); }

        Plugin.Log.LogInfo($"  [动作结构] 生效配置: Enabled={CfgEnabled?.Value} 动作=[{CfgActions?.Value}] 只第一次={CfgOnce?.Value}");
        return n;
    }

    public static void ChangePostfix(GamePlay.ActionMgr __instance, string name)
    {
        try
        {
            if (CfgEnabled?.Value != true) return;
            if (__instance == null || string.IsNullOrEmpty(name)) return;
            if (!DashInvincible.IsLocalPlayerActor(__instance.Owner)) return;
            if (!Want(name)) return;
            if (CfgOnce?.Value == true && !_done.Add(name)) return;

            var act = __instance.CurrentAction;
            if (act == null) return;
            Dump(act);
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"[动作结构] 转储异常: {e.Message}"); }
    }

    /// <summary>GetTimeScaleByTime 探针。⚠ 按【动作名】配对去重，不设总条数上限 ——
    /// 本项目已经四次栽在"限制打印条数"上：预算被前面的噪声吃光，
    /// 真正要看的时刻反而一片空白，制造出假阴性。</summary>
    public static void GetTimeScalePostfix(GamePlay.GameActionLogic __instance, Unity.Mathematics.FixedPoint.Fp time, ref GamePlay.ActionTimeScaleRange __result)
    {
        try
        {
            if (CfgEnabled?.Value != true || __instance == null) return;
            string name = SafeName(__instance);
            string key = (name ?? "?") + "|" + (__result != null);
            _tsPairs.TryGetValue(key, out int c);
            _tsPairs[key] = ++c;
            if (c != 1 && c % 500 != 0) return;

            string got = __result == null ? "null" : $"TimeScale={Fp(__result.Pointer, TS_SCALE):F3}";
            Plugin.Log?.LogInfo($"[动作结构:变速机制] GetTimeScaleByTime(\"{name}\", t={Fp2(time):F3}) -> {got}  (第 {c} 次)");
        }
        catch { }
    }

    private static readonly Dictionary<string, int> _tsPairs = new Dictionary<string, int>();

    private static string SafeName(GamePlay.GameActionLogic a)
    {
        try { return Str(a.Pointer, OFF_NAME); } catch { return "?"; }
    }

    private static float Fp2(Unity.Mathematics.FixedPoint.Fp v)
    {
        try { return (float)v; } catch { return 0f; }
    }

    private static bool Want(string name)
    {
        var raw = CfgActions?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return true;    // 留空 = 全部
        foreach (var s in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(s.Trim(), name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ------------------------------------------------------------------ 转储

    private static void Dump(GamePlay.GameActionLogic a)
    {
        IntPtr p = a.Pointer;
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine("╔══════════════════════════════════════════════════════════════════");
        sb.AppendLine($"║ 动作结构 \"{Str(p, OFF_NAME)}\"   (类型={ActTypeName(I32(p, OFF_ACTTYPE))})");
        sb.AppendLine("╠══════════════════════════════════════════════════════════════════");
        sb.AppendLine($"║ 动画名      : \"{Str(p, OFF_ANIM)}\"");
        sb.AppendLine($"║ 动画时长    : {Fp(p, OFF_ANIMDUR):F3}s");
        sb.AppendLine($"║ 总时长      : {Fp(p, OFF_TOTALDUR):F3}s");
        sb.AppendLine($"║ 动画播完即收招(AutoEndActionWhenMotionEnds) = {Bool(p, OFF_AUTOEND)}   ★");
        sb.AppendLine($"║ 动画时间映射(EnableAnimationTimeMap)        = {Bool(p, OFF_TIMEMAP)}");
        sb.AppendLine($"║ 默认下一招 NextAction = \"{Str(p, OFF_NEXT)}\"");
        sb.AppendLine($"║ 下落招     DropAction = \"{Str(p, OFF_DROP)}\"");
        sb.AppendLine($"║ 死亡招     DeadAction = \"{Str(p, OFF_DEAD)}\"");

        DumpHitData(sb, p);
        DumpStringList(sb, p, OFF_INTERRUPT, "可打断它的动作 Interrupt");
        DumpSwitchList(sb, p, OFF_SWITCHS);
        DumpTimeScales(sb, p);

        sb.AppendLine("╚══════════════════════════════════════════════════════════════════");
        Plugin.Log?.LogInfo(sb.ToString());
    }

    private static void DumpStringList(StringBuilder sb, IntPtr p, int off, string title)
    {
        int n = ListCount(p, off);
        sb.AppendLine($"║ {title} ({n} 项)");
        if (n == 0) { sb.AppendLine("║   (空)"); return; }
        var parts = new List<string>();
        for (int i = 0; i < n && i < 40; i++)
        {
            IntPtr e = ListElem(p, off, i);
            if (e != IntPtr.Zero) parts.Add("\"" + Managed(e) + "\"");
        }
        sb.AppendLine("║   " + string.Join(", ", parts.ToArray()));
    }

    /// <summary>接招窗口：这个动作在第 [Start, End] 秒之间，允许接哪一招。</summary>
    private static void DumpSwitchList(StringBuilder sb, IntPtr p, int off)
    {
        int n = ListCount(p, off);
        sb.AppendLine($"║");
        sb.AppendLine($"║ ── 接招窗口 ActionSwitchs ({n} 条) ──  \"在这个动作的第几秒到第几秒之间，可以接哪一招\"");
        if (n == 0) { sb.AppendLine("║   (空 —— 说明这个动作不能取消接招，只能等它自己播完)"); return; }

        for (int i = 0; i < n && i < 30; i++)
        {
            IntPtr s = ListElem(p, off, i);
            if (s == IntPtr.Zero) continue;

            float t0 = Fp(s, SW_TIMESTART), t1 = Fp(s, SW_TIMEEND);
            IntPtr tc = PtrAt(s, SW_TIMECHECK);
            float c0 = tc == IntPtr.Zero ? -1f : Fp(tc, 0x14);
            float c1 = tc == IntPtr.Zero ? -1f : Fp(tc, 0x1C);
            int tcType = tc == IntPtr.Zero ? -1 : I32(tc, 0x10);

            string target = Str(s, SW_NEWACTION);
            int mode = I32(s, SW_MODE);
            int skCheck = I32(s, SW_SKILLCHECK);
            int skGroup = I32(s, SW_SKILLGROUP);

            sb.AppendLine($"║  [{i}] 接 \"{target}\"");
            sb.AppendLine($"║       窗口(原生)  {t0:F3}s ~ {t1:F3}s   |  TimeCheck {c0:F3}~{c1:F3} (类型={TimeTypeName(tcType)})");
            sb.AppendLine($"║       模式={ModeName(mode)}  技能放行={SkillCheckName(skCheck)}  技能组={SkillGroupName(skGroup)}");
        }
        if (n > 30) sb.AppendLine($"║  ...(还有 {n - 30} 条)");
    }

    /// <summary>攻击判定段。**这是"攻击 vs 后摇"的原生分界** ——
    /// 最后一段判定结束的时刻，就是攻击结束、后摇开始的地方。
    /// 变速只加速到那里，后面留原速，搓招手感就不会变。</summary>
    private static void DumpHitData(StringBuilder sb, IntPtr p)
    {
        int n = ListCount(p, OFF_HITDATA);
        sb.AppendLine("║");
        sb.AppendLine($"║ ── 攻击判定段 HitDataList ({n} 段) ──  \"这个动作在第几秒到第几秒之间有攻击判定\"");
        if (n == 0) { sb.AppendLine("║   (空 —— 不是攻击动作，或者判定挂在别处)"); return; }

        float total = Fp(p, OFF_TOTALDUR);
        float max = -1f;
        for (int i = 0; i < n && i < 20; i++)
        {
            IntPtr h = ListElem(p, OFF_HITDATA, i);
            if (h == IntPtr.Zero) continue;
            float te = Fp(h, 0x18);              // 基类 TimeEnd
            IntPtr tr = PtrAt(h, 0x20);          // TickRange
            float ts2 = tr == IntPtr.Zero ? -1f : Fp(tr, 0x10);
            float te2 = tr == IntPtr.Zero ? -1f : Fp(tr, 0x18);
            if (te > max) max = te;
            if (te2 > max) max = te2;
            sb.AppendLine($"║  [{i}] 判定 {te:F3}s 段" +
                          (tr == IntPtr.Zero ? "" : $"   TickRange {ts2:F3}~{te2:F3}"));
        }
        sb.AppendLine($"║  → 最后判定结束 = {max:F3}s / 总时长 {total:F3}s" +
                      $"   ⇒ 后摇 {max:F3} ~ {total:F3}s ({(total - max) * 1000:F0}ms)");
    }

    /// <summary>动作自带的变速区间。这是**游戏原生的"这个动作在这里播快一点"机制** ——
    /// 如果我们能往这里塞一条 (0 ~ 总时长, 倍率) 的记录, 那么加速就是由游戏自己的代码
    /// 作用在 dt 上的: 动作时钟、动画、位移、事件派发**全部同步**，不需要我们去推 Time，
    /// 也就不会出现"事件被跳过 / 招没完成"。这才是有可能通用、且不破坏表现的那条路。</summary>
    private static void DumpTimeScales(StringBuilder sb, IntPtr p)
    {
        int n = ListCount(p, OFF_TIMESCALES);
        sb.AppendLine("║");
        sb.AppendLine($"║ ── 原生变速区间 TimeScales ({n} 条) ──  \"这个动作在第几秒到第几秒之间，以什么倍率播放\"");
        if (n == 0) { sb.AppendLine("║   (空 —— 这个动作没有原生变速，但也说明机制存在、只是没配)"); return; }

        for (int i = 0; i < n && i < 20; i++)
        {
            IntPtr e = ListElem(p, OFF_TIMESCALES, i);
            if (e == IntPtr.Zero) continue;
            sb.AppendLine($"║  [{i}] TimeScale={Fp(e, TS_SCALE):F3}   " +
                          $"TimeStart/End {Fp(e, TS_START):F3}~{Fp(e, TS_END):F3}   " +
                          $"Range {Fp(e, TS_RANGE):F3}~{Fp(e, TS_RANGE + 8):F3}   Tag=\"{Str(e, TS_TAG)}\"");
        }
    }

    // ------------------------------------------------------------------ 裸读工具

    private static string Str(IntPtr p, int off)
    {
        try
        {
            IntPtr sp = PtrAt(p, off);
            return sp == IntPtr.Zero ? "" : Managed(sp);
        }
        catch { return "<err>"; }
    }

    private static string Managed(IntPtr strPtr)
    {
        try { return Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(strPtr); }
        catch { return "<str err>"; }
    }

    private static float Fp(IntPtr p, int off)
    {
        if (p == IntPtr.Zero) return 0f;
        return (float)(Marshal.ReadInt64(p, off) / FP_ONE);
    }

    private static int I32(IntPtr p, int off)
    {
        if (p == IntPtr.Zero) return 0;
        return Marshal.ReadInt32(p, off);
    }

    private static bool Bool(IntPtr p, int off)
    {
        if (p == IntPtr.Zero) return false;
        return Marshal.ReadByte(p, off) != 0;
    }

    private static IntPtr PtrAt(IntPtr p, int off)
    {
        if (p == IntPtr.Zero) return IntPtr.Zero;
        IntPtr v = Marshal.ReadIntPtr(p, off);
        return IsBadPtr(v) ? IntPtr.Zero : v;
    }

    /// <summary>List&lt;T&gt; 元素个数。_size 在 0x18。</summary>
    private static int ListCount(IntPtr p, int off)
    {
        IntPtr lst = PtrAt(p, off);
        if (lst == IntPtr.Zero) return 0;
        int n = Marshal.ReadInt32(lst, LIST_SIZE);
        return (n < 0 || n > 4096) ? 0 : n;
    }

    /// <summary>List&lt;T&gt; 第 i 个元素（引用类型 = 指针）。</summary>
    private static IntPtr ListElem(IntPtr p, int off, int i)
    {
        IntPtr lst = PtrAt(p, off);
        if (lst == IntPtr.Zero) return IntPtr.Zero;
        IntPtr arr = PtrAt(lst, LIST_ITEMS);
        if (arr == IntPtr.Zero) return IntPtr.Zero;
        IntPtr v = Marshal.ReadIntPtr(arr, ARRAY_DATA + i * PTR);
        return IsBadPtr(v) ? IntPtr.Zero : v;
    }

    /// <summary>明显不像合法指针的直接拒掉，免得裸读崩游戏。Il2CppInterop 的托管对象
    /// 在 64 位下地址都很大且 16 字节对齐（对象头含 klass 指针）。</summary>
    private static bool IsBadPtr(IntPtr v)
    {
        long x = v.ToInt64();
        if (x == 0) return true;
        if (x < 0x10000 || x > 0x7FFFFFFFFFFF) return true;
        if ((x & 7) != 0) return true;
        return false;
    }

    // ------------------------------------------------------------------ 枚举名

    private static string ActTypeName(int v)
    {
        switch (v)
        {
            case 1: return "Attack";
            case 2: return "Standby";
            case 3: return "Walk";
            case 4: return "Run";
            case 6: return "Ultimate";
            case 9: return "Drop";
            default: return v.ToString();
        }
    }

    private static string TimeTypeName(int v)
    {
        switch (v) { case 1: return "ByTime"; case 3: return "Other"; default: return "v" + v; }
    }

    private static string ModeName(int v)
    {
        switch (v) { case 0: return "仅切Action"; case 1: return "切技能(ChangeSkill)"; default: return "v" + v; }
    }

    private static string SkillCheckName(int v)
    {
        if (v == 0) return "None";
        var parts = new List<string>();
        if ((v & 1) != 0) parts.Add("GroundState");
        if ((v & 2) != 0) parts.Add("CD");
        if ((v & 4) != 0) parts.Add("MP");
        if ((v & 8) != 0) parts.Add("ActiveState");
        return string.Join("|", parts.ToArray());
    }

    private static string SkillGroupName(int v)
    {
        switch (v)
        {
            case 0: return "None(通配)";
            case 1: return "Attack";
            case 2: return "Skill1";
            case 3: return "Ultra";
            case 4: return "AttackAir";
            case 5: return "Dash";
            case 6: return "DashAttack";
            case 7: return "Jump";
            case 8: return "LongAttack";
            case 11: return "Summon";
            case 100: return "All";
            default: return "v" + v;
        }
    }
}
