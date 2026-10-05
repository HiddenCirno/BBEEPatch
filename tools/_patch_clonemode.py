# -*- coding: utf-8 -*-
"""把 CfgCloneAll(bool) 换成 CfgCloneMode(三选一)，让"是否改名"这件事可二分。

三个档：
    All          整条链全部克隆（含原生平A四段）—— 设计稿要求的终态
    ExceptHost   只克隆"非平A槽位"的动作（平A四段保留原生名）—— 上一版的行为
    Collision    只在动作名会跟别的链碰撞时才克隆 —— 最早的行为

二分的意义：佩利诺尔在 All 下坏了、在 ExceptHost 下是好的 ——
那根因就在"把 attack1~attack4 也改名了"，而不是"克隆了佩利诺尔段"。
"""
import io

P = "Modules/Es/EsComboChain.cs"
s = io.open(P, encoding="utf-8").read()

s = s.replace(
    "    internal static BepInEx.Configuration.ConfigEntry<bool> CfgCloneAll;",
    "    internal static BepInEx.Configuration.ConfigEntry<string> CfgCloneMode;", 1)

a = s.index("    private static bool NeedClone(string action)")
b = s.index("    /// <summary>我们造的动作名 -> 它的源动作名", a)
new = """    private static string CloneMode => (CfgCloneMode?.Value ?? "All").Trim();

    private static bool NeedClone(string action)
    {
        switch (CloneMode.ToLowerInvariant())
        {
            case "collision":
                // 最早的行为：只在动作名会跟别的链碰撞时才克隆
                return _nativeGroup.TryGetValue(action, out var g) && g != (CfgGroup?.Value ?? 1);
            case "excepthost":
                // 上一版：非平A槽位的动作克隆，原生平A四段保留原名
                return !_hostSlots.Contains(action);
            default:
                return true;   // All: 整条链全部克隆
        }
    }

"""
s = s[:a] + new + s[b:]
io.open(P, "w", encoding="utf-8").write(s)
print("EsComboChain.cs: NeedClone -> 三档模式")

P2 = "Plugin.cs"
t = io.open(P2, encoding="utf-8").read()
i = t.index("EsComboChain.CfgCloneAll = Config.Bind(")
j = t.index('");', i) + 4
NL = chr(92) + 'n'
desc = (" +\n                ".join(
    '"' + l.replace('"', chr(92) + '"') + NL + '"' for l in [
        "★ 连段里哪些动作要克隆成【独有的新动作】。三选一：",
        "",
        "  All         = 整条链全部克隆（默认；设计稿要求的终态）",
        "  ExceptHost  = 只克隆非平A槽位的动作（attack1~attack4 保留原生名）",
        "  Collision   = 只在动作名会跟别的链碰撞时才克隆（最早的行为）",
        "",
        "为什么要克隆：照抄原生行 = 连同它「在别的链里的上下文假设」一起抄进来。",
        "已实测的四个卡点全是这个来源 —— 动作名被原链认领 / PrecheckActionCd=1 /",
        "Timeout=0 让游标第一帧被清 / Input=Skill 不听攻击键。",
        "全部克隆之后，变速/窗口/还原都只作用在我们自己身上，原版动作零影响。",
        "",
        "⚠ All 的代价：原生 attack1 这个名字不再出现在链上。",
        "  若发现原生佩利诺尔等连段被牵连，先用 ExceptHost 做二分定位。",
    ]))
new2 = ('EsComboChain.CfgCloneMode = Config.Bind(ccsec, "CloneMode", "All",\n                '
        + desc + ');\n')
t = t[:i] + new2 + t[j:]
io.open(P2, "w", encoding="utf-8").write(t)
print("Plugin.cs: 绑定换成 CloneMode")
