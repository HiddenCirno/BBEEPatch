# -*- coding: utf-8 -*-
"""给冲刺无敌加"排除名单"。

问题: DashInvincible 用 ActionKeyword="dash" 子串匹配,
而贝德维尔(Ultra)的动作名恰好全都带 Dash —— UltraDashEX / UltraDashAirEX / UDA(Ultra Dash Attack)。
后果: 整个 Ultra 演出期间玩家都无敌; 而本作"命中结算时只要一方无敌就整个跳过",
      Ultra 二段的命中被吃掉 —— 而翅膀正是在二段命中时沿轨迹释放的。
"""
import io, os

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "plugin", "BlazblueJsPatch")

# ---- 1. DashInvincible.cs ----
p = os.path.join(BASE, "DashInvincible.cs")
s = io.open(p, encoding="utf-8").read()

if "IsDashName" not in s:
    # 加字段
    s = s.replace(
        "    internal static BepInEx.Configuration.ConfigEntry<int> CfgLevel;",
        "    internal static BepInEx.Configuration.ConfigEntry<int> CfgLevel;\n"
        "    internal static BepInEx.Configuration.ConfigEntry<string> CfgExclude;",
        1)

    # 加判定函数（挂在 Keyword 属性后面）
    anchor = '    private static int Level => CfgLevel?.Value ?? 999;'
    assert anchor in s, "找不到 Level 属性"
    fn = anchor + """

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
    }"""
    s = s.replace(anchor, fn, 1)

    # 替换两处判定
    old1 = ("            bool dashing = !string.IsNullOrEmpty(name) &&\n"
            "                           name.IndexOf(Keyword, StringComparison.OrdinalIgnoreCase) >= 0;")
    assert old1 in s, "找不到第一处判定"
    s = s.replace(old1, "            bool dashing = IsDashName(name);", 1)

io.open(p, "w", encoding="utf-8").write(s)
print("DashInvincible: 已加 IsDashName")

# 第二处 (行 214 附近) 单独看上下文再替换
s = io.open(p, encoding="utf-8").read()
import re
m = re.search(r"([ \t]*)(\w+(?:\.\w+)*)\s*=\s*[^;]*?n\.IndexOf\(Keyword, StringComparison\.OrdinalIgnoreCase\) >= 0;", s)
print("第二处:", repr(m.group(0)) if m else "未找到")

# ---- 2. Plugin.cs 绑定 ----
p2 = os.path.join(BASE, "Plugin.cs")
s2 = io.open(p2, encoding="utf-8").read()
if "ActionExcludeKeywords" not in s2:
    a = '            DashInvincible.CfgLevel = Config.Bind(sec, "Level", 999,'
    assert a in s2
    s2 = s2.replace(a,
        '            DashInvincible.CfgExclude = Config.Bind(sec, "ActionExcludeKeywords", "Ultra,UD,UDA",\n'
        '                "动作名含这些串的【不算冲刺】, 逗号分隔。\\\\n" +\n'
        '                "必须留这个名单: 贝德维尔(Ultra)的动作全带 Dash(UltraDashEX / UltraDashAirEX / UDA),\\\\n" +\n'
        '                "只用 dash 子串匹配会让整个 Ultra 演出期间玩家无敌 ——\\\\n" +\n'
        '                "而本作命中结算时只要一方无敌就整个跳过, Ultra 二段的命中会被吃掉。\\\\n" +\n'
        '                "实测症状: 贝德维尔的翅膀纹章消失(它正是二段命中时沿轨迹释放的)。");\n' + a, 1)
    io.open(p2, "w", encoding="utf-8").write(s2)
    print("Plugin: 已绑定 ActionExcludeKeywords")
