# -*- coding: utf-8 -*-
"""DropSwitches：克隆动作时，剔除指定的【接招窗口】条目。

背景（2026-10-03 实测）：
    ace6_905 (布3, 源 attackC) 的 ActionSwitchs 有 29 条，开头就是
        [0] 接 "drop_down"  TimeCheck 0.500~2.000  模式=仅切Action
        [1] 接 "drop_down"  TimeCheck 0.950~2.000
        [2] 接 ""           TimeCheck 0.500~2.000  模式=切技能
        [3]/[4] 接 jump2/jump
    因为原生空中布鲁诺里 attackC 就是【收尾段】，它本来就该允许接下落/跳跃。
    我们把它搬进链的中段，这条"收尾权限"就成了累赘 —— 按 下 会被下落系抢走。

⚠ 最大的坑：ActionSwitchs 是【引用型字段】，CloneAction 是整块内存拷贝，
   所以克隆体和原版【共用同一个 List】。直接删条目会把原版一起删掉。
   必须先给克隆体一张自己的表（逐元素复制），再在上面剔 —— 和 TimeScales 同一处理。

配置（[连段模组]）：
    DropSwitches = attackC:drop_down,drop_down2 | attackB:xxx
                   源动作名 : 要剔除的窗口目标动作名(逗号分隔)   多项用 | 隔开
"""
import io

P = "Modules/Es/EsComboChain.cs"
s = io.open(P, encoding="utf-8").read()

# ---------------------------------------------------------------- 1) 配置字段
A = "    internal static BepInEx.Configuration.ConfigEntry<string> CfgWindowOverrides;"
assert A in s
s = s.replace(A, A + "\n    /// <summary>克隆时剔除的接招窗口: \"源动作:目标动作,目标动作 | ...\"</summary>\n"
                    "    internal static BepInEx.Configuration.ConfigEntry<string> CfgDropSwitches;", 1)

# ---------------------------------------------------------------- 2) 工具：自己的表 + 剔除
B = "    // ---------------- 汇点探针 ----------------"
assert B in s
C = '''    // ---------------- DropSwitches ----------------

    /// <summary>给克隆体一张【自己的】ActionSwitchs 表，并按 DropSwitches 剔除指定目标。</summary>
    private static void IsolateSwitches(GamePlay.GameActionLogic clone, string srcAction)
    {
        if (clone == null) return;
        try
        {
            var src = clone.ActionSwitchs;                    // 此刻还和原版共用
            if (src == null) return;

            int n = 0;
            try { n = src.Count; } catch { }
            if (n == 0) return;

            var own = new Il2CppSystem.Collections.Generic.List<GamePlay.ActionSwitchData>();
            for (int i = 0; i < n; i++)
            {
                GamePlay.ActionSwitchData e = null;
                try { e = src[i]; } catch { }
                if (e != null) own.Add(e);
            }
            clone.ActionSwitchs = own;                        // ★ 从这一刻起原版不再受影响

            var drop = DropTargetsFor(srcAction);
            if (drop == null || drop.Count == 0) return;

            int removed = 0;
            for (int i = own.Count - 1; i >= 0; i--)
            {
                GamePlay.ActionSwitchData e = null;
                try { e = own[i]; } catch { }
                if (e == null) continue;
                string tgt = SwitchTarget(e);
                if (tgt == null) continue;
                if (drop.Contains(tgt)) { own.RemoveAt(i); removed++; }
            }
            if (removed > 0)
                Plugin.Log?.LogInfo($"[连段模组:剔窗口] \\"{srcAction}\\" 的克隆体剔除了 {removed} 条接招窗口 " +
                                    $"(剩 {own.Count} 条) —— 原版不受影响");
        }
        catch (Exception e) { Once("IsolateSwitches", e); }
    }

    /// <summary>读一条接招窗口的目标动作名。优先托管属性，失败退裸指针 @0x30。</summary>
    private static string SwitchTarget(GamePlay.ActionSwitchData e)
    {
        try { var v = e.NewActionEx; if (!string.IsNullOrEmpty(v)) return v; } catch { }
        try
        {
            IntPtr p = Marshal.ReadIntPtr(e.Pointer, 0x30);
            if (p != IntPtr.Zero) return Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(p);
        }
        catch { }
        return null;
    }

    /// <summary>解析 "源动作:目标1,目标2 | ..." 里某个源动作要剔除的目标集合。</summary>
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
                return set;
            }
        }
        catch { }
        return null;
    }

    // ---------------- 汇点探针 ----------------'''
s = s.replace(B, C, 1)

# ---------------------------------------------------------------- 3) CloneAction 之后调用
D = """            // ★★★★★ 【必须隔离的一处】TimeScales 是引用型字段，整块拷贝之后"""
assert D in s
D2 = '''            // ★ ActionSwitchs 同理必须隔离（否则改克隆体会污染原版）—— 见 IsolateSwitches。
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

''' + D
s = s.replace(D, D2, 1)

# ---------------------------------------------------------------- 4) MakeSkill 里按源动作名剔除
E = "            if (effAction != action)\n            {\n                _synthApplied[copy.Pointer] = effAction;"
assert E in s
E2 = '''            // ★ 按源动作名剔除接招窗口（DropSwitches）—— 此时 copy 已经是自己的表（见 CloneAction）
            try
            {
                var drop = DropTargetsFor(action);
                if (drop != null && drop.Count > 0 && copy.ActionSwitchs != null)
                {
                    var sw = copy.ActionSwitchs;
                    int removed = 0;
                    for (int i = sw.Count - 1; i >= 0; i--)
                    {
                        GamePlay.ActionSwitchData e = null;
                        try { e = sw[i]; } catch { }
                        if (e == null) continue;
                        string tgt = SwitchTarget(e);
                        if (tgt != null && drop.Contains(tgt)) { sw.RemoveAt(i); removed++; }
                    }
                    if (removed > 0)
                        Plugin.Log?.LogInfo($"[连段模组:剔窗口] \\"{action}\\" 剔除 {removed} 条接招窗口 (剩 {sw.Count} 条)");
                }
            }
            catch (Exception e) { Once("DropSwitches", e); }

''' + E
s = s.replace(E, E2, 1)

io.open(P, "w", encoding="utf-8").write(s)
print("DropSwitches 已实现")
