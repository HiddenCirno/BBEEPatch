# -*- coding: utf-8 -*-
"""把 ClearSwitches(整表清空) 升级成 DropSwitches(逐条剔 + 通配)。

配置（[连段模组]）：
    DropSwitches = attackC:drop_down,drop*,jump* | attackB:xxx
                   源动作名 : 要剔除的窗口目标(逗号分隔，支持 * 通配)  多项用 | 隔开
    DropSwitches = attackC:*        通配 * = 整表清空

读目标名的偏移来自 ActionStructure 的既有实现（不是我猜的）：
    ActionSwitchData + 0x30 = NewActionEx(ActionLogicParamString 结构体)
                      + 0x38 = 其中的 ConstValue(string)  ← 接哪一招
"""
import io

P = "Modules/Es/EsComboChain.cs"
s = io.open(P, encoding="utf-8").read()

# 1) 读目标名：偏移改 0x38
a = """        try { var v = e.NewActionEx; if (!string.IsNullOrEmpty(v)) return v; } catch { }
        try
        {
            IntPtr p = Marshal.ReadIntPtr(e.Pointer, 0x30);"""
assert a in s
s = s.replace(a, """        // 偏移来自 ActionStructure 的既有实现: NewActionEx 结构体在 0x30, ConstValue 在 +0x38
        try
        {
            IntPtr p = Marshal.ReadIntPtr(e.Pointer, 0x38);""", 1)

# 2) DropTargetsFor 支持通配 + 通配集合
b = '''                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in item.Substring(c + 1).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var v = t.Trim();
                    if (v.Length > 0) set.Add(v);
                }
                return set;'''
assert b in s
b2 = '''                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in item.Substring(c + 1).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var v = t.Trim();
                    if (v.Length > 0) set.Add(v);
                }
                return set;'''
# (解析本身不用改，通配在匹配时处理)

# 3) 匹配函数（通配）
c = "    /// <summary>清空克隆体的接招窗口表。"
assert c in s
c2 = '''    /// <summary>目标名是否命中剔除集合（支持 * 通配）。</summary>
    private static bool HitDrop(HashSet<string> drop, string target)
    {
        if (drop == null || string.IsNullOrEmpty(target)) return false;
        foreach (var pat in drop)
        {
            if (pat == "*") return true;
            if (pat.IndexOf('*') < 0)
            {
                if (string.Equals(pat, target, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else
            {
                // 简单通配：按 * 切成若干段，要求按顺序出现
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
                if (ok && !pat.EndsWith("*") && parts.Length > 0 && parts[parts.Length - 1].Length > 0
                    && !target.EndsWith(parts[parts.Length - 1], StringComparison.OrdinalIgnoreCase)) ok = false;
                if (ok) return true;
            }
        }
        return false;
    }

    /// <summary>清空克隆体的接招窗口表。'''
s = s.replace(c, c2, 1)

# 4) ClearSwitchesIfNeeded -> 逐条剔
d = '''            if (clone == null || !ShouldClearSwitches(srcAction)) return;
            int before = 0;
            try { before = clone.ActionSwitchs?.Count ?? 0; } catch { }
            clone.ActionSwitchs = new Il2CppSystem.Collections.Generic.List<GamePlay.ActionSwitchData>();
            Plugin.Log?.LogInfo($"[连段模组:剔窗口] \\"{srcAction}\\" 的克隆体接招窗口已清空 (原 {before} 条) —— 原版不受影响");'''
assert d in s
d2 = '''            if (clone == null) return;
            var drop = DropTargetsFor(srcAction);
            if (drop == null || drop.Count == 0) return;

            var sw = clone.ActionSwitchs;
            if (sw == null) return;
            int before = 0; try { before = sw.Count; } catch { }

            int removed = 0;
            for (int i = sw.Count - 1; i >= 0; i--)
            {
                GamePlay.ActionSwitchData e = null;
                try { e = sw[i]; } catch { }
                if (e == null) continue;
                if (HitDrop(drop, SwitchTarget(e))) { sw.RemoveAt(i); removed++; }
            }
            if (removed > 0)
                Plugin.Log?.LogInfo($"[连段模组:剔窗口] \\"{srcAction}\\" 剔除 {removed}/{before} 条接招窗口 " +
                                    $"(剩 {sw.Count}) —— 只动克隆体, 原版不受影响");'''
s = s.replace(d, d2, 1)

# 5) ShouldClearSwitches -> 不再需要（改由 DropTargetsFor 判空）
e = '''    /// <summary>这个源动作的克隆体要不要清空接招窗口表。</summary>
    private static bool ShouldClearSwitches(string srcAction)
    {
        try
        {
            var raw = CfgDropSwitches?.Value;
            if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrEmpty(srcAction)) return false;
            foreach (var t in raw.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(t.Trim(), srcAction, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

'''
assert e in s
s = s.replace(e, "", 1)

io.open(P, "w", encoding="utf-8").write(s)
print("DropSwitches 升级完成")
