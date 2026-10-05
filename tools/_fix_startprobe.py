# -*- coding: utf-8 -*-
"""把 StartSkillProbe 的签名收窄到"已知能绑定"的形参。

绑定 GamePlay.PlayerSkill psk 会 IL Compile Error（形参名/类型没先确认就写死了，
和 SetChainCD 那次同型的错误）。postfix 在调用【之后】执行，
此时 __instance.Cur 已经是刚起的那一段 —— 目标信息照样拿得到，不需要绑定形参。
"""
import io

P = "Modules/Es/EsComboChain.cs"
s = io.open(P, encoding="utf-8").read()
n = 0

def rep(a, b, tag):
    global s, n
    assert a in s, "锚点缺失: " + tag
    s = s.replace(a, b, 1)
    n += 1
    print("改好:", tag)

rep(
    "    public static void StartSkillProbe(GamePlay.PlayerSkillChain __instance,\n"
    "                                       GamePlay.PlayerSkill psk, bool __result)",
    "    public static void StartSkillProbe(GamePlay.PlayerSkillChain __instance, bool __result)",
    "签名去掉 psk")

rep(
    '            int order = -1; string act = "";\n'
    '            try { if (psk != null) { order = OrderOf(psk); act = ActionOf(psk); } } catch { }\n',
    '            // 不绑目标技能形参(会 IL Compile Error)；postfix 在调用后跑，读 Cur 即得目标。\n',
    "去掉读 psk")

rep(
    'string key = order + "|" + act + "|" + path + "|" + __result;',
    'string key = cur + "|" + curAct + "|" + path + "|" + __result;',
    "key 改用 Cur")

Q = chr(92) + '"'
old_log = ('$"[连段模组:起招] StartSkill(' + Q + '{act}' + Q + ' Order={order}) -> {__result}  " +\n'
           '                                $"链段数={cnt} Cur={cur}(' + Q + '{curAct}' + Q + ')  来路={path}");')
new_log = ('$"[连段模组:起招] StartSkill -> {__result}  " +\n'
           '                                $"链段数={cnt} 起了 Cur={cur}(' + Q + '{curAct}' + Q + ')  来路={path}");')
rep(old_log, new_log, "日志行")

io.open(P, "w", encoding="utf-8").write(s)
print("共改动", n, "处")
