# -*- coding: utf-8 -*-
"""查「高文 / 崔斯坦 / 布鲁诺 / 莫德雷德」在游戏内部的身份。

它们是明天那批改动的主角，必须先确定:
  · 是潜能名(PotentialName_/TriggerName_) 还是招式名(ActorActionName_) 还是技能名
  · 对应的 id 是多少
  · 由此反推内部动作名 / 特效前缀

方法: 本地化表 = 连续 (uint32 keyHash, uint32 len, utf8)，键 = 前缀_<id>，
hash 用游戏的 CalculateHash。先扫文本，再用「前缀 + id 区间」反查键名。
"""
import io, os, struct, re

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
P = os.path.join(BASE, "extracted", "raw", "localization_chs.ab")
OUT = os.path.join(BASE, "tools", "_knights_out.txt")

M = 0x89ABCDEF


def chash(s):
    h = 0x01234567
    for c in s.encode("utf-8"):
        h = ((h ^ c) * M) & 0xFFFFFFFF
    return (h * M) & 0xFFFFFFFF


d = open(P, "rb").read()
table = {}
i, n = 0, len(d)
while i + 8 <= n:
    k, ln = struct.unpack_from("<II", d, i)
    if ln > 4000 or i + 8 + ln > n:
        i += 1
        continue
    try:
        t = d[i + 8:i + 8 + ln].decode("utf-8")
    except UnicodeDecodeError:
        i += 1
        continue
    if k not in table:
        table[k] = t
    i += 8 + ln

L = [f"本地化条目 {len(table)}"]

# 1) 直接找这四个词
for kw in ["高文", "崔斯坦", "布鲁诺", "莫德雷德", "贝德维尔", "剑气", "上挑"]:
    hits = [(k, t) for k, t in table.items() if kw in t]
    L.append(f"\n=== 含「{kw}」 ({len(hits)}) ===")
    for k, t in hits[:14]:
        L.append(f"  {k:08X}  {t[:90]}")

# 2) 反查键名: 前缀 + id 区间, 找这四个词
PREFIXES = ["ActorActionName_", "ActorActionDesc_", "TriggerName_", "PotentialName_",
            "SkillName_", "ActorSkill_", "BuffName_", "ActorUltra_", "BlessName_"]
found = {}
for pre in PREFIXES:
    for i2 in range(1000, 300000):
        h = chash(pre + str(i2))
        t = table.get(h)
        if t and any(k in t for k in ("高文", "崔斯坦", "布鲁诺", "莫德雷德")):
            found.setdefault(pre, []).append((i2, t))

for pre, lst in found.items():
    L.append(f"\n=== {pre} 命中 ({len(lst)}) ===")
    for i2, t in lst[:40]:
        L.append(f"  {i2:>7}  {t[:96]}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done")
