# -*- coding: utf-8 -*-
"""给连段模组加【逐动作窗口覆盖】。

三个字段（语义都经反汇编确认）：
    ActdurStrict  硬地板   —— 前摇没走完输入被丢
    Timeout       后摇衔接窗口上限 —— 到点 CurSkill.Reset
    Preinputtime  前置输入有效期 —— 从按下那一刻起算多久内算数

配置形态（单个 BepInEx 字符串键，用 | 分隔多项，逗号分字段）：
    WindowOverrides = attack2:0.55,0.18 | attackD2:0.7,-,0.25
字段顺序 后摇窗口秒, 前置输入秒, 硬地板秒；写 - 表示该项不覆盖。
值一律按【现实秒】给，内部乘该动作的速度倍率 —— 这样改速度不会让调好的手感失效。
"""
import io, sys

P = "Modules/Es/EsComboChain.cs"
s = io.open(P, encoding="utf-8").read()

# ---------------------------------------------------------------- 1) 字段 + 解析
ANCHOR_F = "    internal static BepInEx.Configuration.ConfigEntry<float> CfgPreInputScale;"
assert ANCHOR_F in s, "缺 CfgPreInputScale 字段"
s = s.replace(ANCHOR_F, ANCHOR_F + """

    /// <summary>逐动作窗口覆盖。格式见 Plugin.cs 的说明。</summary>
    internal static BepInEx.Configuration.ConfigEntry<string> CfgWindowOverrides;

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
                        var arr = new double[] { -1, -1, -1 };
                        for (int i = 0; i < 3 && i < vals.Length; i++)
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
            if (cfg < 0f) return EsActionSpeed.IsSpeeding(action) ? (EsActionSpeed.CfgSpeed?.Value ?? 1f) : 1f;
            return cfg > 0f ? cfg : 1f;
        }
        catch { return 1f; }
    }""", 1)

# ---------------------------------------------------------------- 2) 应用覆盖
ANCHOR_L = '            LogEx.Once("combo|row|" + action,'
assert ANCHOR_L in s, "缺 整行 日志锚点"
s = s.replace(ANCHOR_L, """            // ★★★★★【逐动作窗口覆盖】—— 精调环节。
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
                    Plugin.Log?.LogInfo($"[连段模组:窗口覆盖] \\"{action}\\" " +
                        $"后摇={(ov[0] < 0 ? "默认" : ov[0] + "s")} " +
                        $"前置输入={(ov[1] < 0 ? "默认" : ov[1] + "s")} " +
                        $"硬地板={(ov[2] < 0 ? "默认" : ov[2] + "s")}  (×{kk:F2})");
                }
            }
            catch (Exception e) { Once("WinOv|apply", e); }

""" + ANCHOR_L, 1)

io.open(P, "w", encoding="utf-8").write(s)
print("EsComboChain.cs 已打补丁")

# ---------------------------------------------------------------- 3) 配置项
P2 = "Plugin.cs"
t = io.open(P2, encoding="utf-8").read()
A = '            EsComboChain.CfgTimeWin = Config.Bind(ccsec, "TimeoutBonus", 0.5f,'
assert A in t, "缺 TimeoutBonus 绑定"
B = '''            EsComboChain.CfgWindowOverrides = Config.Bind(ccsec, "WindowOverrides", "",
                "\\u2605 \\u3010\\u9010\\u52a8\\u4f5c\\u7a97\\u53e3\\u8986\\u76d6\\u3011\\u2014\\u2014 \\u7cbe\\u8c03\\u624b\\u611f\\u7528\\u3002\\n" +
                "\\n" +
                "\\u4e09\\u4e2a\\u5b57\\u6bb5\\uff08\\u8bed\\u4e49\\u5747\\u7ecf\\u53cd\\u6c47\\u7f16\\u786e\\u8ba4\\uff09\\uff0c\\u987a\\u5e8f\\u56fa\\u5b9a\\uff1a\\n" +
                "    \\u540e\\u6447\\u7a97\\u53e3(\\u79d2)   = Timeout        \\u4e0a\\u9650\\uff0c\\u5230\\u70b9 CurSkill.Reset\\n" +
                "    \\u524d\\u7f6e\\u8f93\\u5165(\\u79d2)   = Preinputtime  \\u4ece\\u6309\\u4e0b\\u90a3\\u4e00\\u523b\\u8d77\\u7b97\\u591a\\u4e45\\u5185\\u7b97\\u6570\\n" +
                "    \\u786c\\u5730\\u677f(\\u79d2)   = ActdurStrict  \\u6ca1\\u8d70\\u5b8c\\u524d\\u8f93\\u5165\\u88ab\\u4e22\\n" +
                "\\n" +
                "\\u683c\\u5f0f\\uff1a\\u52a8\\u4f5c\\u540d:\\u540e\\u6447,\\u524d\\u7f6e,\\u786c\\u5730\\u677f    \\u591a\\u9879\\u7528 | \\u9694\\u5f00\\uff0c\\u5199 - \\u8868\\u793a\\u8be5\\u9879\\u4e0d\\u8986\\u76d6\\n" +
                "\\u4f8b\\uff1a  attack2:0.55,0.18 | attackD2:0.7,-,0.25 | attackAEX:0.9,0.3\\n" +
                "\\n" +
                "\\u52a8\\u4f5c\\u540d\\u7528\\u3010Sequence \\u91cc\\u5199\\u7684\\u540d\\u5b57\\u3011\\uff08\\u6e90\\u52a8\\u4f5c\\u540d\\uff09\\uff0c\\u4e0d\\u662f\\u6211\\u4eec\\u6539\\u540d\\u540e\\u7684 aceN_xxx\\u3002\\n" +
                "\\n" +
                "\\u503c\\u6309\\u3010\\u73b0\\u5b9e\\u79d2\\u3011\\u7ed9\\uff0c\\u5185\\u90e8\\u4f1a\\u4e58\\u8be5\\u52a8\\u4f5c\\u7684\\u901f\\u5ea6\\u500d\\u7387 \\u2014\\u2014 \\u6240\\u4ee5\\u4f60\\u8c03\\u597d\\u7684\\u624b\\u611f\\uff0c\\u6539\\u52a0\\u901f\\u500d\\u7387\\u4e5f\\u4e0d\\u4f1a\\u5931\\u6548\\u3002\\n" +
                "\\u7a7a = \\u5168\\u90e8\\u7528\\u81ea\\u52a8\\u503c\\uff08\\u5c31\\u662f\\u4e0a\\u9762 ChainWindowScale / TimeoutBonus / PreInputSeconds \\u7b97\\u51fa\\u6765\\u7684\\uff09\\u3002\\n" +
                "\\n" +
                "\\u6bcf\\u4e2a\\u52a8\\u4f5c\\u6700\\u7ec8\\u751f\\u6548\\u7684\\u4e09\\u4e2a\\u503c\\u4f1a\\u6253\\u5728 [\\u6574\\u884c] \\u65e5\\u5fd7\\u91cc\\uff0c\\u8c03\\u5b8c\\u5bf9\\u4e00\\u4e0b\\u5c31\\u77e5\\u9053\\u751f\\u6ca1\\u751f\\u6548\\u3002");
''' + A
t = t.replace(A, B, 1)
io.open(P2, "w", encoding="utf-8").write(t)
print("Plugin.cs 已加 WindowOverrides")
