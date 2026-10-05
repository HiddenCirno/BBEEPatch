# -*- coding: utf-8 -*-
"""MakeSkill 模块化：多链 + 段语法（动作[方向][+]）+ 接管原生同动作段。

配置形态（[连段模组]，Chain1..Chain6）：
    Chain1 = 1 | attackD1[下]+, attack3[下], attackAEX[下], attackD2[下],
                 attack4[下], attackB[下], attackD3[下], atkAirX[任意]
    Chain2 = 1 | attack1[任意]+, attack2[任意], attack3[任意], attack4[任意]
    TakeOverNative = true

核心决策（来自 §3.6.1e 的 A~G）：
  1. 宿主(继承时序的来源) = 【链的首段】的原生行 —— 一条链只有一种节奏，首段即基准。
     旧模型的宿主是"被替换的那条平A行"，新模型里链不再绑定平A槽位。
  2. 不再"找平A块并原地替换"，改为【移除原生同动作段 + 追加我们的段】。
     这是 §3.6.1e E 的坑：同动作的原生段留在链里会抢在克隆段前面。
  3. 方向/起手由段语法决定（方向是【链位置】属性，不是动作属性）。
  4. 兼容：没有配置 ChainN 时，回退到旧的 Sequence + Group。
"""
import io

P = "Modules/Es/EsComboChain.cs"
s = io.open(P, encoding="utf-8").read()

# ================================================================ 1) 字段
A = "    internal static BepInEx.Configuration.ConfigEntry<string> CfgSequence;"
assert A in s
s = s.replace(A, A + """
    /// <summary>多链声明。Chain1..Chain6，格式 "组号 | 动作[方向][+], ..."</summary>
    internal static BepInEx.Configuration.ConfigEntry<string>[] CfgChains;
    internal const int ChainCount = 6;
    /// <summary>Sequence 里出现的动作，是否移除原生同动作段（见 §3.6.1e E）。</summary>
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgTakeOver;""", 1)

# ================================================================ 2) 段/链 数据结构 + 解析
B = "    private static List<string> ParseSequence()"
assert B in s
NEW = '''    // ================================================================
    //  段 / 链 声明
    // ================================================================

    private sealed class Seg
    {
        public string Action = "";
        public int Dir = -1;      // -1=继承源动作; 0=任意 1=上 2=下 3=前 4=后 5=无方向
        public bool Entry;        // 尾缀 '+' = 允许作为起手段（前驱清空）
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

                foreach (var part in body.Split(new[] { ',', ';', '\\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = part.Trim();
                    if (t.Length == 0) continue;

                    var seg = new Seg();
                    if (t.EndsWith("+")) { seg.Entry = true; t = t.Substring(0, t.Length - 1).Trim(); }

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

    private static List<string> ParseSequence()'''
s = s.replace(B, NEW, 1)

io.open(P, "w", encoding="utf-8").write(s)
print("part1: 段/链 数据结构与解析 已加入")
