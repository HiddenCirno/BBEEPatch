# -*- coding: utf-8 -*-
"""速度模块组化：全局单倍率 -> 多组各自独立的倍率 + 动作名单。

配置形态（[动作变速] 段，Group1..Group6，每组一行）：
    Group1 = 2.5 | attack1,attack2,attackD2,attackAEX,...
    Group2 = 1.8 | holdEX

匹配规则：只认名字，从组1往下第一组命中即生效。
⚠ 绝不按"动作内容"匹配 —— 克隆段有独有名字(aceN_xxx)，想只加速自己的段就写 aceN_xxx，
   想加速原版就写原名(attackAEX)，两者互不影响。这是刻意的隔离设计。
"""
import io

NL = chr(92) + 'n'          # C# 源码里的 \n

# ================================================================ EsActionSpeed.cs
P = "Modules/Es/EsActionSpeed.cs"
s = io.open(P, encoding="utf-8").read()

A = ("    internal static BepInEx.Configuration.ConfigEntry<float> CfgSpeed;\n"
     "    internal static BepInEx.Configuration.ConfigEntry<string> CfgActions;")
assert A in s, "缺 CfgSpeed/CfgActions 字段"

B = """    /// <summary>多组配置: 每组一个字符串 "倍率 | 动作1,动作2,..."。</summary>
    internal static BepInEx.Configuration.ConfigEntry<string>[] CfgGroups;
    internal const int GroupCount = 6;

    private sealed class Group { public float K = 1f; public readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    private static List<Group> _groups;
    private static string _groupsRaw;

    /// <summary>解析全部组；任一组的字符串变了就整表重建。</summary>
    private static List<Group> Groups()
    {
        var sb = new System.Text.StringBuilder();
        if (CfgGroups != null) foreach (var e in CfgGroups) sb.Append(e?.Value).Append((char)1);
        var raw = sb.ToString();
        if (ReferenceEquals(raw, _groupsRaw) && _groups != null) return _groups;
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
                        if (t.Length > 0) g.Names.Add(t);
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
            foreach (var g in Groups())
                if (g.Names.Contains(name)) return g.K;
        }
        catch { }
        return 1f;
    }"""
s = s.replace(A, B, 1)

A2 = """        var raw = CfgActions?.Value;
        if (!ReferenceEquals(raw, _namesRaw))
        {
            _namesRaw = raw;
            _names.Clear();
            if (!string.IsNullOrWhiteSpace(raw))
                foreach (var s in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    _names.Add(s.Trim());
        }
        if (_names.Contains(name)) return true;"""
assert A2 in s, "缺 Match 主体"
s = s.replace(A2, """        foreach (var g in Groups())
            if (g.Names.Contains(name)) { _curFactor = g.K; return true; }
        _curFactor = 1f;""", 1)

s = s.replace("Clamp(CfgSpeed?.Value ?? 1.5f)", "Factor()")
s = s.replace("CfgSpeed?.Value", "_curFactor")
s = s.replace('$"杠杆={CfgLever?.Value} 动作=[{_namesRaw}] 挂Model={CfgScaleModel?.Value} " +',
              '$"杠杆={CfgLever?.Value} 组表见上方日志 挂Model={CfgScaleModel?.Value} " +')

io.open(P, "w", encoding="utf-8").write(s)
print("EsActionSpeed.cs 组化完成")

# ================================================================ Plugin.cs
P2 = "Plugin.cs"
t = io.open(P2, encoding="utf-8").read()

i = t.index("EsActionSpeed.CfgSpeed = Config.Bind(")
j = t.index("EsActionSpeed.CfgActions = Config.Bind(")
j = t.index('");', j) + 4          # 含 ");\n

desc_lines = [
    "【倍率 | 动作名单】一行一组。格式:  <倍率> | <动作1,动作2,...>",
    "",
    "  例:  2.5 | attack1,attack2,attackD2,attackAEX,attack3,attackB,attack4,attackD3,atkAirX",
    "       1.8 | holdEX",
    "       1   | (名单留空 = 该组关闭)",
    "",
    "匹配规则: 只认名字, 从组1往下第一组命中即生效。",
    "",
    "⚠ 绝不按【动作内容】匹配 —— 这是故意的隔离设计:",
    "  我们克隆出来的段有独有名字 (aceN_xxx)。想只加速自己的段 → 写 aceN_xxx;",
    "  想加速原版动作 → 写原名(如 attackAEX)。两者互不影响。",
    "",
    "倍率 1 = 不加速。",
    "",
    "《与窗口调节的关系》[连段模组] ChainWindowScale 设为 -1(自动) 时,",
    "后摇窗口会自动跟随【该动作所属组】的倍率 —— 改倍率不用重调窗口。",
]
desc_cs = (" +\n                ".join('"' + l.replace('"', chr(92) + '"') + NL + '"'
                                 for l in desc_lines))
desc_cs = desc_cs.rsplit(" +", 1)[0].rstrip()

defaults = [
    '"2.5 | attack1,attack2,attackD2,attackAEX,attack3,attackB,attack4,attackD3,atkAirX"',
    '"1 | "', '"1 | "', '"1 | "', '"1 | "', '"1 | "',
]

new = "EsActionSpeed.CfgGroups = new BepInEx.Configuration.ConfigEntry<string>[EsActionSpeed.GroupCount];\n"
for gi in range(6):
    new += ('            EsActionSpeed.CfgGroups[%d] = Config.Bind(asec, "Group%d", %s,\n                %s);\n'
            % (gi, gi + 1, defaults[gi], desc_cs))

t = t[:i] + new + t[j:]
io.open(P2, "w", encoding="utf-8").write(t)
print("Plugin.cs 绑定已替换")
