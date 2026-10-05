# -*- coding: utf-8 -*-
"""part3: 配置绑定 Chain1..Chain6 + TakeOverNative。"""
import io

NL = chr(92) + 'n'

P = "Plugin.cs"
s = io.open(P, encoding="utf-8").read()

A = "            EsComboChain.CfgRewrite = Config.Bind("
assert A in s, "锚点缺失"

desc = (" +\n                ".join('"' + l.replace('"', chr(92) + '"') + NL + '"' for l in [
    "★ 链声明。一条链一行，Chain1..Chain6。",
    "",
    "  格式:  <目标链号> | <段1>, <段2>, ...",
    "  段语法:  动作名[方向][+]",
    "      [方向]  任意/上/下/前/后/无   缺省 = 继承源动作自身的方向",
    "      +       尾缀，表示这一段允许作为【起手段】（前驱清空，能从站姿直接起手）",
    "",
    "  例:  Chain1 = 1 | attackD1[下]+, attack3[下], attackAEX[下], attackD2[下]",
    "",
    "【方向为什么写在链上】方向是「链位置」属性 —— 同一个 attack3 在平A链里要 任意、",
    "在佩利诺尔链里要 下。实测：方向明确的段优先于 任意。",
    "",
    "【每条链只有一种节奏】宿主(继承后摇/预输入/硬地板的来源) = 该链【首段】的原生行。",
    "所以首段要选一个有代表性的动作。",
    "",
    "【Segment 旧配置】没配 ChainN 时会回退到旧的 Sequence + Group，只改一条链。",
]))

new = ('EsComboChain.CfgChains = new BepInEx.Configuration.ConfigEntry<string>[EsComboChain.ChainCount];\n'
       '            for (int ci = 0; ci < EsComboChain.ChainCount; ci++)\n'
       '                EsComboChain.CfgChains[ci] = Config.Bind(ccsec, "Chain" + (ci + 1), "",\n                    '
       + desc + ');\n'
       '            EsComboChain.CfgTakeOver = Config.Bind(ccsec, "TakeOverNative", true,\n'
       + '                "' + "★ Sequence 里出现的动作，移除链里【原生同动作段】。" + NL + '" +\n'
       + '                "' + NL + '" +\n'
       + '                "' + "为什么必须移除：起手段靠 InputDir 竞争，同动作的原生段只要还在链里，" + NL + '" +\n'
       + '                "' + "就会排在数组更前面把输入抢走 —— 实测「新佩利诺尔链」里我们自己的段一次都没被选中，" + NL + '" +\n'
       + '                "' + "打的全是原生佩段。" + NL + '" +\n'
       + '                "' + NL + '" +\n'
       + '                "' + "关掉 = 新旧并存（只用于对比观察）。" + '");\n'
       )
s = s.replace(A, new + A, 1)
io.open(P, "w", encoding="utf-8").write(s)
print("part3 完成")
