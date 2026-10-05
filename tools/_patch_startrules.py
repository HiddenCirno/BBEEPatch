# -*- coding: utf-8 -*-
"""写入崔斯坦的 StartBullets 规则 + 两个飞行参数。
屏幕角度约定: 0=右 90=上 180=左 -90=下（崔斯坦没有方向性, 所以按屏幕算）。
写了第 5 段(飞行速度) = 自推飞行, 方向精确; 不写 = 只转 dir, 只有 0/180 可靠。
"""
import io

P = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\BepInEx\config\ace.bbee.jspatch.cfg"
lines = io.open(P, encoding="utf-8").read().split("\n")

start = next(i for i, l in enumerate(lines) if l.strip() == "[纹章解放]")
end = next(i for i in range(start + 1, len(lines)) if lines[i].startswith("["))
seg = lines[start:end]

FLY = 18          # 试手感用；改 StartFlySpeed 可全局调

def diag(name, bullet, delay=0.0):
    """斜四向：左上135 右上45 左下-135 右下-45"""
    out = []
    for d in (135, 45, -135, -45):
        out.append("%s:%s:%.2f:%d:%d" % (name, bullet, delay, d, FLY))
    return out

def side(name, bullet, delay=0.0):
    """左右：左180 右0"""
    return ["%s:%s:%.2f:%d:%d" % (name, bullet, delay, d, FLY) for d in (180, 0)]

def updown(name, bullet, delay=0.0):
    """左右 + 左上右上"""
    return ["%s:%s:%.2f:%d:%d" % (name, bullet, delay, d, FLY) for d in (180, 0, 135, 45)]

rules = []
# 踩踏1 + 回边 → 斜四向 A1（布鲁诺1剑气）
rules += diag("fallupd",   "A1")
rules += diag("fallupdd",  "A1")
# 踩踏2 + 回边 → 斜四向 B1（布鲁诺2剑气）
rules += diag("fallup22",  "B1")
rules += diag("fallup2d",  "B1")
rules += diag("fallup2dd", "B1")
# 落地2 → 左右 B1   （落地1(fallend) 与普通落地共用，已删）
rules += side("fallend2", "B1")
# 落地3 / ↓F → 左右 + 左上右上 C1（布鲁诺3剑气）
rules += updown("fallmdownendEX2", "C1")
rules += updown("fallmdownendEX",  "C1")

val = " | ".join(rules)

def setkv(lst, key, val):
    for i, l in enumerate(lst):
        if l.startswith(key + " ="):
            lst[i] = key + " = " + val
            return True
    return False

assert setkv(seg, "StartBullets", val), "StartBullets not found"
if not any(l.startswith("StartFlySpeed =") for l in seg):
    j = seg.index("SkipOurSegments = true")
    seg.insert(j, "StartFlySpeed = " + str(FLY))
    seg.insert(j + 1, "StartFlySeconds = 1.2")

lines[start:end] = seg
io.open(P, "w", encoding="utf-8").write("\n".join(lines))
print("rules:", len(rules))
