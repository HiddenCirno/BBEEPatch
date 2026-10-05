# -*- coding: utf-8 -*-
"""踩踏族的 aa3 也删掉：带判定的弹幕会把怪打飞 → 崔斯坦失位、踩踏链断。
只留落地那三条（落地2 / 落地3 / ↓F）。
"""
import io

P = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\BepInEx\config\ace.bbee.jspatch.cfg"
lines = io.open(P, encoding="utf-8").read().split("\n")
start = next(i for i, l in enumerate(lines) if l.strip() == "[纹章解放]")
end = next(i for i in range(start + 1, len(lines)) if lines[i].startswith("["))
seg = lines[start:end]

FLY = 18
rules = ["fallend2:B1:0.00:%d:%d" % (d, FLY) for d in (180, 0)]
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
