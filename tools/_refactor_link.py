# -*- coding: utf-8 -*-
"""part4: Link 显式接线 —— 让"同一个前驱有多个后继"，即穿插。

配置（[连段模组]，Link1..Link6）：
    Link1 = 901 <- 909 | 900 <- 916
            目标Order <- 前驱Order,前驱Order | 目标Order <- ...

语义：把列出的前驱【追加】到目标的 preSkillOrder 里。
     —— 是"追加"不是"覆盖"，所以不会破坏链内顺序接线。
     —— 目标若带 '+'（起手段）不要给它加前驱，那会让它不再算起手段。

为什么需要它：现在每段的 preSkillOrder = 原生边(基本都指向已删除的段) ∪ 链内上一段，
所以每条线都是【直线】。要让"平A 中段 按 下+攻击 进佩利诺尔"，就必须
让佩线的某一段的 pre 里含有平A中段的 Order。
"""
import io

P = "Modules/Es/EsComboChain.cs"
s = io.open(P, encoding="utf-8").read()

# ---------------------------------------------------------------- 1) 字段
A = "    internal static BepInEx.Configuration.ConfigEntry<bool> CfgTakeOver;"
assert A in s
s = s.replace(A, A + """
    /// <summary>显式接线。Link1..Link6，格式 "目标Order &lt;- 前驱Order,前驱Order | ..."</summary>
    internal static BepInEx.Configuration.ConfigEntry<string>[] CfgLinks;
    internal const int LinkCount = 6;

    /// <summary>Order -> 我们造的那一格的 proto 指针（供 Link 用）。</summary>
    private static readonly Dictionary<int, IntPtr> _madeByOrder = new Dictionary<int, IntPtr>();""", 1)

# ---------------------------------------------------------------- 2) MakeSkill 里登记
B = "            if (effAction != action)\n            {\n                _synthApplied[copy.Pointer] = effAction;"
assert B in s
s = s.replace(B, "            try { _madeByOrder[newOrder] = copy.Pointer; } catch { }\n" + B, 1)

# ---------------------------------------------------------------- 3) 应用 Link
C = "        foreach (var spec in specs) ApplyOneChain(mgr, spec);\n    }"
assert C in s
D = """        foreach (var spec in specs) ApplyOneChain(mgr, spec);

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
                    WarnOnce("link|miss|" + target, $"Link 目标 Order={target} 不是我们造的段，忽略");
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
                if (added > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    try { for (int i = 0; i < po.Count; i++) sb.Append(po[i]).Append('/'); } catch { }
                    Plugin.Log?.LogInfo($"[连段模组:接线] Link: Order={target} += {added} 个前驱 -> pre=[{sb}]");
                }
            }
        }
    }"""
s = s.replace(C, D, 1)

io.open(P, "w", encoding="utf-8").write(s)
print("Link 已实现")
