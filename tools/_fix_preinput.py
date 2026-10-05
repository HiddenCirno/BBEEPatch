# -*- coding: utf-8 -*-
"""把 Plugin.cs 里被 heredoc 弄坏的 CfgPreInput 配置块重写干净。

⚠ 教训（本项目已栽多次）: 含中文 + 转义符的脚本【一律写成 .py 再跑】。
   走 `python -c "..."` 时，bash 会把 `\\n` 变成 `\n`，python 再把它变成真换行，
   于是 C# 字符串字面量被劈成两行 → CS1010「常量中有换行符」。
   所以生成 C# 源码里的 `\n` 必须在 python 里写成【单反斜杠 + n】，
   并且用 raw 字符串（r'''...'''）避免被再解释一次。
"""
import io

P = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Plugin.cs"
s = io.open(P, encoding="utf-8").read()

start = s.find("AttackChainTweak.CfgPreInput = Config.Bind")
end = s.find("AttackChainTweak.CfgGroup = Config.Bind", start)
assert start > 0 and end > start, "锚点没找到"

BLOCK = r'''AttackChainTweak.CfgPreInput = Config.Bind(ccsec, "PreInputSeconds", 0.3f,
                "★ 每一段的【衔接窗口】(秒) = SkillActivateFixedPoint.Preinputtime。\n" +
                "反汇编确认它用在 PlayerSkillChain.findAndStartSkill_Imp 里 ——\n" +
                "只有落在「动作结束前 N 秒」内的输入才会被采纳。\n" +
                "\n" +
                "为什么需要覆盖: 原生值是按【独立技能】调的。布鲁诺第一段 attackAEX 是\n" +
                "PreInput=0(作为整套技能的起手够用)，塞进混合连段后窗口只剩一个瞬间，\n" +
                "表现就是「等它放完又错过窗口」。\n" +
                "\n" +
                "分工: ActdurStrict = 硬地板(没到就丢弃输入) / PreInputtime = 衔接窗口。\n" +
                "布鲁诺要「不被打断 + 又能接上」= ActdurStrict 0(原生) + PreInput 0.3。\n" +
                "0.3 有原生依据 —— 布鲁诺自己的第 2/3 段(attackB/attackC)用的就是它。\n" +
                "-1 = 照抄原生值(混合连段里会错过窗口)。");
            '''

s = s[:start] + BLOCK + s[end:]
io.open(P, "w", encoding="utf-8").write(s)
print("fixed ok")
