# -*- coding: utf-8 -*-
"""踩踏族改成「空2 的纹章(aa3)、朝下(-90°)」，去掉斜四向剑气。
落地2 / 落地3 / ↓F 的 B1 / C1 保持不变。
屏幕角度: 0=右 90=上 180=左 -90=下。写了第 5 段(速度) = 自推飞行(方向精确)。
"""
import io

P = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\BepInEx\config\ace.bbee.jspatch.cfg"
lines = io.open(P, encoding="utf-8").read().split("\n")
start = next(i for i, l in enumerate(lines) if l.strip() == "[纹章解放]")
end = next(i for i in range(start + 1, len(lines)) if lines[i].startswith("["))
seg = lines[start:end]

FLY = 18
DOWN = -90

rules = []
# 踩踏1 / 踩踏2 / 两条回边 → 空2 的纹章(aa3) 朝下
for act in ("fallupd", "fallupdd", "fallup22", "fallup2d", "fallup2dd"):
    rules.append("%s:aa3:0.00:%d:%d" % (act, DOWN, FLY))
# 落地2 → 左右 B1
rules += ["fallend2:B1:0.00:%d:%d" % (d, FLY) for d in (180, 0)]
# 落地3 / ↓F → 左右 + 左上右上 C1
for act in ("fallmdownendEX2", "fallmdownendEX"):
    rules += ["%s:C1:0.00:%d:%d" % (act, d, FLY) for d in (180, 0, 135, 45)]

val = " | ".join(rules)
for i, l in enumerate(seg):
    if l.startswith("StartBullets ="):
        seg[i] = "StartBullets = " + val
        break
else:
    raise SystemExit("StartBullets not found")

lines[start:end] = seg
io.open(P, "w", encoding="utf-8").write("\n".join(lines))
print("rules:", len(rules))
