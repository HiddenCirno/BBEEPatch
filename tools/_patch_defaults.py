# -*- coding: utf-8 -*-
"""把 cfg 里的实况同步进代码默认值 —— 防「全部重置」把调好的值抹掉（本项目栽过两次）。
要同步的: Group3（加 dashAtk0）、StartBullets（崔斯坦/高文那 11 条规则）。
"""
import io

P = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Plugin.cs"
s = io.open(P, encoding="utf-8").read()

a = 'EsActionSpeed.CfgGroups[2] = Config.Bind(asec, "Group3", "1.5 | holdEX,!holdEX",'
b = 'EsActionSpeed.CfgGroups[2] = Config.Bind(asec, "Group3", "1.5 | holdEX,!holdEX,dashAtk0",'
assert a in s, "Group3 default"
s = s.replace(a, b, 1)

RULES = ("fallend2:B1:0.00:180:18 | fallend2:B1:0.00:0:18 | "
         "fallmdownendEX2:B1:0.00:180:18 | fallmdownendEX2:B1:0.00:0:18 | "
         "fallmdownendEX2:B1:0.00:135:18 | fallmdownendEX2:B1:0.00:45:18 | "
         "fallmdownendEX:B1:0.00:180:18 | fallmdownendEX:B1:0.00:0:18 | "
         "fallmdownendEX:B1:0.00:135:18 | fallmdownendEX:B1:0.00:45:18 | "
         "dashAtk0:C1:0:0")

a2 = 'EsEmblemBurst.CfgStartBullets = Config.Bind(csec, "StartBullets", "",'
b2 = 'EsEmblemBurst.CfgStartBullets = Config.Bind(csec, "StartBullets",\n                "%s",' % RULES
assert a2 in s, "StartBullets default"
s = s.replace(a2, b2, 1)

io.open(P, "w", encoding="utf-8").write(s)
print("ok")
