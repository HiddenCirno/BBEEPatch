# -*- coding: utf-8 -*-
"""去掉剑气规则里的"自推飞行"（第 5 段速度 18）。
依据（用户实测纠正）：**布鲁诺剑气是万向的** —— 它自己能飞任意方向。
之前那条"只认朝前/朝后、30/60/90 无变化"的实测是**纹章/环**的结论，套错对象了。
连带后果（都是好事）：
  · 不再需要 mover ⇒ 不捏任何引用 ⇒ 对象池复用那个坑自动消失
  · 高文"倒着飞"是自推造成的，去掉即可；dir 用局部 0 = 正前方（与原生生成完全一致）
  · 高文也不再需要朝向闸门：局部 0 就是"朝哪边就往哪边" ✓
"""
import io, re

CFG = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\BepInEx\config\ace.bbee.jspatch.cfg"
PL = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Plugin.cs"

RULES = ("fallend2:B1:0.00:180 | fallend2:B1:0.00:0 | "
         "fallmdownendEX2:B1:0.00:180 | fallmdownendEX2:B1:0.00:0 | "
         "fallmdownendEX2:B1:0.00:135 | fallmdownendEX2:B1:0.00:45 | "
         "fallmdownendEX:B1:0.00:180 | fallmdownendEX:B1:0.00:0 | "
         "fallmdownendEX:B1:0.00:135 | fallmdownendEX:B1:0.00:45 | "
         "dashAAendEX:C1:0:0")

lines = io.open(CFG, encoding="utf-8").read().split("\n")
st = next(i for i, l in enumerate(lines) if l.strip() == "[纹章解放]")
en = next(i for i in range(st + 1, len(lines)) if lines[i].startswith("["))
for i in range(st, en):
    if lines[i].startswith("StartBullets ="):
        lines[i] = "StartBullets = " + RULES
        break
else:
    raise SystemExit("StartBullets not found")
io.open(CFG, "w", encoding="utf-8").write("\n".join(lines))

p = io.open(PL, encoding="utf-8").read()
p2, n = re.subn(r'(EsEmblemBurst\.CfgStartBullets = Config\.Bind\(csec, "StartBullets",\s*\n\s*")[^"]*(",)',
                lambda m: m.group(1) + RULES + m.group(2), p, count=1)
assert n == 1, "default not found"
io.open(PL, "w", encoding="utf-8").write(p2)
print("ok")
