using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BlazblueJsPatch;

/// <summary>
/// 技能行字段对比器 —— 把【源行】和【宿主行(被替换的那条平A)】逐字段摊开，
/// 只打不同的项。
///
/// 为什么需要它：这一轮的卡点已经连着撞了四个，全是同一个病根 ——
/// **一行技能不是"播哪个动作"，它是【动作 + 按键 + 时序 + 触发器 + 脚本 + 弹幕】的整体**，
/// 而布鲁诺那两行的原生值是配合【链[2] 的上下文】存在的。
/// 我们把 attackAEX / attackB 整行搬进链[1]，就顺带把链[2] 的假设也搬了进来：
///
///     Action            = "attackAEX"  → 被链[2] 认领（改名解决）
///     PrecheckActionCd  = 1            → 准入条件过不去
///     Timeout           = 0            → TimeElaps>=0，游标第一帧就被 Reset
///     Input             = "Skill"      → 链不听攻击键
///
/// 逐个撞太贵，所以直接把 41 个字段一次性对完，剩下的差异一眼看完。
///
/// 字段表由 dump.cs:143365 的 SkillActivateFixedPoint **自动生成**，不手抄 ——
/// 注意 dump.cs 里有两个同名类（76850 那个嵌套的 float 版），手抄必错。
/// </summary>
internal static class SkillRowDiff
{
    private enum K { u32, i32, q32, bool_, str, rep, ptr }

    private struct Fld
    {
        public int Off; public K Kind; public string Name;
        public Fld(int off, K kind, string name) { Off = off; Kind = kind; Name = name; }
    }

    private static readonly Fld[] FIELDS =
    {
            new Fld(0x10, K.ptr, "_unknownFields"),
            new Fld(0x18, K.u32, "actorId_"),
            new Fld(0x1C, K.i32, "group_"),
            new Fld(0x20, K.i32, "order_"),
            new Fld(0x28, K.str, "action_"),
            new Fld(0x30, K.rep, "startTrigger_"),
            new Fld(0x38, K.rep, "exitTrigger_"),
            new Fld(0x40, K.str, "input_"),
            new Fld(0x48, K.i32, "inputDir_"),
            new Fld(0x50, K.rep, "reqTriggerId_"),
            new Fld(0x58, K.rep, "mps_"),
            new Fld(0x60, K.rep, "preSkillOrder_"),
            new Fld(0x68, K.bool_, "allowActiveState_"),
            new Fld(0x69, K.bool_, "allowPassiveState_"),
            new Fld(0x6A, K.bool_, "allowGround_"),
            new Fld(0x6B, K.bool_, "allowFlying_"),
            new Fld(0x70, K.q32, "actdurStrict_"),
            new Fld(0x78, K.q32, "timeout_"),
            new Fld(0x80, K.q32, "timeoutAddcd_"),
            new Fld(0x88, K.str, "mutelist_"),
            new Fld(0x90, K.q32, "preinputtime_"),
            new Fld(0x98, K.bool_, "useLongPress_"),
            new Fld(0xA0, K.q32, "longPressStart_"),
            new Fld(0xA8, K.q32, "longPressEnd_"),
            new Fld(0xB0, K.q32, "lastingDuration_"),
            new Fld(0xB8, K.i32, "precheckActionCd_"),
            new Fld(0xC0, K.rep, "icon_"),
            new Fld(0xC8, K.i32, "autoTurn_"),
            new Fld(0xCC, K.bool_, "turnToDrag_"),
            new Fld(0xD0, K.rep, "buffList_"),
            new Fld(0xD8, K.i32, "bulletId_"),
            new Fld(0xE0, K.str, "bulletAction_"),
            new Fld(0xE8, K.str, "startScriptCall_"),
            new Fld(0xF0, K.str, "startScriptCallParams_"),
            new Fld(0xF8, K.str, "preScriptCall_"),
            new Fld(0x100, K.str, "preScriptCallParams_"),
            new Fld(0x108, K.i32, "uiSkip_"),
            new Fld(0x10C, K.i32, "uiSkipMobile_"),
            new Fld(0x110, K.rep, "satisfyInputkey_"),
            new Fld(0x118, K.i32, "actionpointInputTag_"),
            new Fld(0x11C, K.i32, "actionpointInputTag2_"),
    };

    private static readonly HashSet<string> _done = new HashSet<string>();

    /// <summary>对比一次。同名动作只打一次。</summary>
    internal static void Compare(string srcAction, string effAction, IntPtr src, IntPtr host)
    {
        try
        {
            if (src == IntPtr.Zero || host == IntPtr.Zero) return;
            string key = srcAction + "|" + effAction;
            if (src != host && !_done.Add(key)) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[连段模组:字段对比] 源 \"{srcAction}\" vs 宿主平A行  →  造出的段 \"{effAction}\"");
            int diffs = 0;
            foreach (var f in FIELDS)
            {
                string a = Read(f, src), b = Read(f, host);
                bool same = a == b;
                if (src == host) same = true;
                if (!same) diffs++;
                sb.AppendLine($"      {(same ? "  " : "★ ")}{f.Name,-26} 源={a,-34} 宿主={b}");
            }
            sb.AppendLine($"      —— 共 {FIELDS.Length} 个字段，不同 {diffs} 个");
            Plugin.Log?.LogInfo(sb.ToString());
        }
        catch (Exception e) { try { Plugin.Log?.LogWarning("[连段模组:字段对比] " + e.Message); } catch { } }
    }

    private static string Read(Fld f, IntPtr p)
    {
        try
        {
            switch (f.Kind)
            {
                case K.u32:  return ((uint)Marshal.ReadInt32(p, f.Off)).ToString();
                case K.i32:  return Marshal.ReadInt32(p, f.Off).ToString();
                case K.bool_:return (Marshal.ReadByte(p, f.Off) != 0) ? "true" : "false";
                case K.q32:
                {
                    long v = Marshal.ReadInt64(p, f.Off);
                    return (v / 4294967296.0).ToString("F4") + $"  (raw {v})";
                }
                case K.str:
                {
                    IntPtr s = Marshal.ReadIntPtr(p, f.Off);
                    if (s == IntPtr.Zero) return "<null>";
                    string m = Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(s);
                    return m == null ? "<null>" : "\"" + m + "\"";
                }
                case K.rep:
                {
                    IntPtr r = Marshal.ReadIntPtr(p, f.Off);
                    if (r == IntPtr.Zero) return "<null>";
                    // Google.Protobuf RepeatedField<T>: count 在 +0x18（实测布局）
                    int c = Marshal.ReadInt32(r, 0x18);
                    return $"RepeatedField[{c}]";
                }
                case K.ptr:
                {
                    IntPtr q = Marshal.ReadIntPtr(p, f.Off);
                    return q == IntPtr.Zero ? "null" : "0x" + q.ToInt64().ToString("X");
                }
                default: return "?";
            }
        }
        catch (Exception e) { return "<err:" + e.GetType().Name + ">"; }
    }
}
