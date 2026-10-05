# -*- coding: utf-8 -*-
"""给「跳跃/冲刺互重置」加冲刺族排除名单。
理由（实测）: 高文的两个槽 dashAtk0 / dashAirAtk0 按键是 Summon/上, 不是冲刺移动,
但名字里有 dash -> 被当成冲刺扣预算 -> 空中连冲后高文按不出来([段数限制] 切换失败)。
与之前 UltraDash 误伤同一类（PROJECT_STATE: 关键字子串误伤必须配排除名单）。
"""
import io

CS = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Modules\Combat\JumpDashCrossReset.cs"
PL = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Plugin.cs"
CFG = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\BepInEx\config\ace.bbee.jspatch.cfg"
EX = "dashAtk0,dashAirAtk0"

# ---------------- 1) 模块代码 ----------------
s = io.open(CS, encoding="utf-8").read()

a = "    internal static BepInEx.Configuration.ConfigEntry<string> CfgDashKw;"
b = ("    internal static BepInEx.Configuration.ConfigEntry<string> CfgDashKw;\n"
     "    /// <summary>冲刺族【排除名单】(逗号分隔)。名字里含这些串的【不算冲刺】。</summary>\n"
     "    internal static BepInEx.Configuration.ConfigEntry<string> CfgDashExclude;")
assert a in s, "CfgDashKw field"
s = s.replace(a, b, 1)

a = "            bool nD = Fam(name, CfgDashKw?.Value ?? \"dash\");"
b = "            bool nD = IsDashFam(name);"
assert a in s, "Precheck nD"
s = s.replace(a, b, 1)

a = "            bool d = Fam(name, CfgDashKw?.Value ?? \"dash\");"
b = "            bool d = IsDashFam(name);"
assert a in s, "postfix d"
s = s.replace(a, b, 1)

a = ("    private static bool Fam(string action, string kw) =>\n"
     "        !string.IsNullOrEmpty(action) && !string.IsNullOrEmpty(kw) &&\n"
     "        action.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;")
b = a + """

    /// <summary>冲刺族判定 —— 含关键字【且不含任何排除串】。
    ///
    /// 为什么必须配排除名单（2026-10-04 实测）：
    ///   高文的两个槽 `dashAtk0`(地面, 槽 11.1) / `dashAirAtk0`(空中, 槽 11.2)
    ///   按键是 **Summon/上**，根本不是冲刺移动 —— 但名字里有 "dash"，
    ///   被关键字匹配当成冲刺扣了预算，于是空中连冲之后高文直接按不出来：
    ///       [段数限制] 切换【失败】 -> dashAtk0
    ///   这和之前 `UltraDash` 那次是同一类坑：**关键字子串误伤**，必须点名排除。
    ///
    /// 判据不是手感数，是按键通道：这两条的 InputCmd 是 Summon，不是 Dash。
    /// </summary>
    private static bool IsDashFam(string action)
    {
        if (!Fam(action, CfgDashKw?.Value ?? "dash")) return false;
        var ex = CfgDashExclude?.Value;
        if (string.IsNullOrWhiteSpace(ex)) return true;
        foreach (var k in ex.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            if (action.IndexOf(k.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return false;
        return true;
    }"""
assert a in s, "Fam"
s = s.replace(a, b, 1)

# 生效日志带上排除名单
a = '                               $"跳跃族=\\"{CfgJumpKw?.Value}\\" 冲刺族=\\"{CfgDashKw?.Value}\\"");'
b = '                               $"跳跃族=\\"{CfgJumpKw?.Value}\\" 冲刺族=\\"{CfgDashKw?.Value}\\" " +\n                               $"冲刺排除=\\"{CfgDashExclude?.Value}\\"");'
assert a in s, "log line"
s = s.replace(a, b, 1)

io.open(CS, "w", encoding="utf-8").write(s)

# ---------------- 2) Plugin.cs 绑定 ----------------
p = io.open(PL, encoding="utf-8").read()
a = ('            JumpDashCrossReset.CfgDashKw = Config.Bind(sec, "DashActionKeyword", "dash",')
b = ('            JumpDashCrossReset.CfgDashExclude = Config.Bind(sec, "DashActionExcludeKeywords", "%s",\n'
     '                "冲刺族【排除名单】(逗号分隔)。名字含这些串的不算冲刺, 不扣冲刺预算、也不回满跳跃预算。\\n" +\n'
     '                "\\n" +\n'
     '                "默认排 dashAtk0 / dashAirAtk0: 它们是【高文】的召唤技(按键 Summon/上), 不是冲刺移动 \\n" +\n'
     '                "—— 但名字里有 dash, 会被关键字当成冲刺扣预算, 空中连冲之后高文就按不出来。\\n" +\n'
     '                "(同 UltraDash 那次: 关键字子串误伤必须配排除名单)");\n\n'
     '            JumpDashCrossReset.CfgDashKw = Config.Bind(sec, "DashActionKeyword", "dash",') % EX
assert a in p, "Plugin bind anchor"
p = p.replace(a, b, 1)
io.open(PL, "w", encoding="utf-8").write(p)

# ---------------- 3) cfg ----------------
lines = io.open(CFG, encoding="utf-8").read().split("\n")
for i, l in enumerate(lines):
    if l.startswith("DashActionKeyword = dash"):
        lines.insert(i + 1, "DashActionExcludeKeywords = " + EX)
        break
else:
    raise SystemExit("DashActionKeyword not in cfg")
io.open(CFG, "w", encoding="utf-8").write("\n".join(lines))

print("ok")
