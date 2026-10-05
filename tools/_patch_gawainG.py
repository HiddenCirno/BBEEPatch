# -*- coding: utf-8 -*-
"""高文 = dashAtkG / dashAirAtkG（"G"=Gawain），不是 dashAtk0。
实测: [连段模组:起招] 起了 Cur=25("dashAtkG")  ← 槽 11.1 的 dashAtk0 只是"技能行名"，
      实际动作被骑士映射换成 G 版（同"技能槽不是具体骑士，装谁由潜能决定"那条原则）。
所以: 变速 / 起手剑气 / 排除名单 三处都补上 G 版（0 版保留，无害）。
"""
import io

CFG = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\BepInEx\config\ace.bbee.jspatch.cfg"
CS = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Plugin.cs"

GROUP3 = "1.5 | holdEX,!holdEX,dashAtk0,dashAirAtk0,dashAtkG,dashAirAtkG"
EXCL = "dashAtk0,dashAirAtk0,dashAtkG,dashAirAtkG"
RULES = ("fallend2:B1:0.00:180:18 | fallend2:B1:0.00:0:18 | "
         "fallmdownendEX2:B1:0.00:180:18 | fallmdownendEX2:B1:0.00:0:18 | "
         "fallmdownendEX2:B1:0.00:135:18 | fallmdownendEX2:B1:0.00:45:18 | "
         "fallmdownendEX:B1:0.00:180:18 | fallmdownendEX:B1:0.00:0:18 | "
         "fallmdownendEX:B1:0.00:135:18 | fallmdownendEX:B1:0.00:45:18 | "
         "dashAtk0:C1:0:0 | dashAirAtk0:C1:0:0 | "
         "dashAtkG:C1:0:0 | dashAirAtkG:C1:0:0")

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
    elif l.startswith("DashActionExcludeKeywords = "):
        lines[i] = "DashActionExcludeKeywords = " + EXCL
io.open(CFG, "w", encoding="utf-8").write("\n".join(lines))

# ---- 代码默认值 ----
s = io.open(CS, encoding="utf-8").read()
for a, b in [
    ('"Group3", "1.5 | holdEX,!holdEX,dashAtk0,dashAirAtk0"',
     '"Group3", "%s"' % GROUP3),
    ('"DashActionExcludeKeywords", "dashAtk0,dashAirAtk0"',
     '"DashActionExcludeKeywords", "%s"' % EXCL),
    ('"dashAtk0:C1:0:0 | dashAirAtk0:C1:0:0"', '"%s"' % RULES),
]:
    assert a in s, a[:60]
    s = s.replace(a, b, 1)
io.open(CS, "w", encoding="utf-8").write(s)
print("ok")
