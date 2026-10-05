# -*- coding: utf-8 -*-
"""1) 崔斯坦落地：布鲁诺3(C1) → 布鲁诺2(B1)
   2) 高文(dashAtk0)：1.5 倍加速 + 同向释放布鲁诺3剑气
屏幕角度: 0=右 90=上 180=左 -90=下。写了第 5 段(速度) = 自推飞行。
高文那条【不写速度】= 只转 dir(偏转 0 = 局部正前方 = 同向)，交给弹幕自己飞。
"""
import io

P = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\BepInEx\config\ace.bbee.jspatch.cfg"
lines = io.open(P, encoding="utf-8").read().split("\n")
start = next(i for i, l in enumerate(lines) if l.strip() == "[纹章解放]")
end = next(i for i in range(start + 1, len(lines)) if lines[i].startswith("["))
seg = lines[start:end]

# ---- 崔斯坦落地：C1 -> B1 ----
FLY = 18
rules = ["fallend2:B1:0.00:%d:%d" % (d, FLY) for d in (180, 0)]
for act in ("fallmdownendEX2", "fallmdownendEX"):
    rules += ["%s:B1:0.00:%d:%d" % (act, d, FLY) for d in (180, 0, 135, 45)]
# ---- 高文：同向(局部 0°)布鲁诺3剑气，不自推 ----
rules.append("dashAtk0:C1:0:0")

val = " | ".join(rules)
for i, l in enumerate(seg):
    if l.startswith("StartBullets ="):
        seg[i] = "StartBullets = " + val
        break
else:
    raise SystemExit("StartBullets not found")

lines[start:end] = seg

# ---- 高文 1.5 倍: 并进 Group3（本来就是 1.5，同速率，不必新开组）----
for i, l in enumerate(lines):
    if l.startswith("Group3 = 1.5 | holdEX,!holdEX"):
        lines[i] = "Group3 = 1.5 | holdEX,!holdEX,dashAtk0"
        break
else:
    raise SystemExit("Group3 not found")

io.open(P, "w", encoding="utf-8").write("\n".join(lines))
print("rules:", len(rules))
