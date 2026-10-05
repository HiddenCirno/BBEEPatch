# -*- coding: utf-8 -*-
"""高文有两个槽: 地面 dashAtk0(11.1) / 空中 dashAirAtk0(11.2)。两个都要管。
同步 Group3 与 StartBullets（cfg + 代码默认值两处都改，防"全部重置"抹掉）。
"""
import io

CFG = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\BepInEx\config\ace.bbee.jspatch.cfg"
CS = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Plugin.cs"

RULES = ("fallend2:B1:0.00:180:18 | fallend2:B1:0.00:0:18 | "
         "fallmdownendEX2:B1:0.00:180:18 | fallmdownendEX2:B1:0.00:0:18 | "
         "fallmdownendEX2:B1:0.00:135:18 | fallmdownendEX2:B1:0.00:45:18 | "
         "fallmdownendEX:B1:0.00:180:18 | fallmdownendEX:B1:0.00:0:18 | "
         "fallmdownendEX:B1:0.00:135:18 | fallmdownendEX:B1:0.00:45:18 | "
         "dashAtk0:C1:0:0 | dashAirAtk0:C1:0:0")
GROUP3 = "1.5 | holdEX,!holdEX,dashAtk0,dashAirAtk0"

# ---- cfg ----
lines = io.open(CFG, encoding="utf-8").read().split("\n")
start = next(i for i, l in enumerate(lines) if l.strip() == "[纹章解放]")
end = next(i for i in range(start + 1, len(lines)) if lines[i].startswith("["))
for i in range(start, end):
    if lines[i].startswith("StartBullets ="):
        lines[i] = "StartBullets = " + RULES
        break
for i, l in enumerate(lines):
    if l.startswith("Group3 = "):
        lines[i] = "Group3 = " + GROUP3
        break
io.open(CFG, "w", encoding="utf-8").write("\n".join(lines))

# ---- 代码默认值 ----
s = io.open(CS, encoding="utf-8").read()
a = 'EsActionSpeed.CfgGroups[2] = Config.Bind(asec, "Group3", "1.5 | holdEX,!holdEX,dashAtk0",'
b = 'EsActionSpeed.CfgGroups[2] = Config.Bind(asec, "Group3", "%s",' % GROUP3
assert a in s, "Group3 default"
s = s.replace(a, b, 1)
assert RULES in s or True
import re
s2 = re.sub(r'(EsEmblemBurst\.CfgStartBullets = Config\.Bind\(csec, "StartBullets",\s*\n\s*")[^"]*(",)',
            lambda m: m.group(1) + RULES + m.group(2), s, count=1)
assert s2 != s, "StartBullets default"
io.open(CS, "w", encoding="utf-8").write(s2)
print("ok")
