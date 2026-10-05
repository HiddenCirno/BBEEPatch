# -*- coding: utf-8 -*-
"""用已知锚点反推这些招式名的键名格式。

已知: ActorUltra_103401 = Type:Slasher「贝德维尔」
先用它验证 hash 函数与格式, 再据此找高文/崔斯坦/布鲁诺/莫德雷德的键。
"""
import io, os, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
P = os.path.join(BASE, "extracted", "raw", "localization_chs.ab")
OUT = os.path.join(BASE, "tools", "_knights2_out.txt")
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
    table.setdefault(k, t)
    i += 8 + ln

L = []
L.append(f"本地化条目 {len(table)}")

# ---- 1. 锚点验证 ----
anchors = ["ActorUltra_103401", "ActorUltra_", "TriggerName_50411"]
for a in anchors[:1]:
    h = chash(a)
    L.append(f"\n=== 锚点 {a} -> {h:08X}  表里={'有:' + table[h][:60] if h in table else '没有'}")

# 贝德维尔的 4 个 hash
bedi = [(k, t) for k, t in table.items() if "贝德维尔" in t]
L.append("\n贝德维尔 的 hash:")
for k, t in bedi:
    L.append(f"  {k:08X}")
L.append(f"  chash(ActorUltra_103401) = {chash('ActorUltra_103401'):08X}")

# ---- 2. 用锚点发现的格式, 扫一批前缀+id ----
PREFIXES = ["ActorUltra_", "ActorSkill_", "ActorSkillName_", "ActorActionName_",
            "SkillName_", "Skill_", "ActorAction_", "ActorDesc_", "ActorUltraName_"]
L.append("\n=== 前缀 + id(1000..400000) 命中「高文/崔斯坦/布鲁诺/莫德雷德」 ===")
hit_any = False
for pre in PREFIXES:
    for i2 in range(1000, 400000):
        h = chash(pre + str(i2))
        t = table.get(h)
        if t and any(k in t for k in ("高文", "崔斯坦", "布鲁诺", "莫德雷德", "贝德维尔")):
            L.append(f"  {pre}{i2:<8} {t[:90]}")
            hit_any = True
    io.open(OUT, "w", encoding="utf-8").write("\n".join(L))   # 边跑边写
if not hit_any:
    L.append("  (无命中)")

# ---- 3. 看看 ES 相关的招式/技能描述文本 ----
L.append("\n=== 含「第一段/第二段/第三段」的条目 ===")
for k, t in table.items():
    if any(s in t for s in ("第一段", "第二段", "第三段")) and len(t) < 120:
        L.append(f"  {k:08X}  {t[:100]}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done")
