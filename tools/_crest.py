# -*- coding: utf-8 -*-
"""找出「纹章解放」在数据里的真实身份。

本地化表格式 = 连续 (uint32 keyHash, uint32 len, utf8)，键 = 前缀_<id>，
hash 用游戏的 CalculateHash:
    h=0x01234567; for c in bytes: h = ((h ^ c) * 0x89ABCDEF) & 0xFFFFFFFF
    return (h * 0x89ABCDEF) & 0xFFFFFFFF

反过来做: 先把本地化表读成 hash->text，
再用「前缀 + id 区间」暴力算 hash 去查表，命中率高的前缀就是对的。
"""
import io, os, struct, sys

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
P = os.path.join(BASE, "extracted", "raw", "localization_chs.ab")
OUT = os.path.join(BASE, "tools", "_crest_out.txt")

M = 0x89ABCDEF

def chash(s):
    h = 0x01234567
    for c in s.encode("utf-8"):
        h = ((h ^ c) * M) & 0xFFFFFFFF
    return (h * M) & 0xFFFFFFFF

d = open(P, "rb").read()
table = {}
i = 0
n = len(d)
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

out = []
out.append(f"本地化条目: {len(table)}")

# 1) 含「纹章」的文本
hits = [(k, t) for k, t in table.items() if "纹章" in t]
out.append(f"\n=== 文本含「纹章」的条目 ({len(hits)}) ===")
for k, t in hits[:80]:
    out.append(f"  {k:08X}  {t[:70]}")

# 2) 反查键名: 前缀 + id 区间
PREFIXES = ["TriggerName_", "PotentialName_", "ActorActionName_", "InheritSkill_",
            "BlessQuality_", "SkillName_", "ActorSkill_", "BuffName_", "ActorUltra_"]
for pre in PREFIXES:
    got = []
    for i2 in range(1000, 200000):
        h = chash(pre + str(i2))
        if h in table:
            got.append((i2, table[h]))
    out.append(f"\n=== {pre}  命中 {len(got)} 条 (抽样) ===")
    for i2, t in got[:25]:
        out.append(f"  {i2:>7}  {t[:70]}")
    # 只列出含纹章的
    crest = [(i2, t) for i2, t in got if "纹章" in t]
    if crest:
        out.append(f"  --- 其中含「纹章」---")
        for i2, t in crest[:40]:
            out.append(f"  {i2:>7}  {t[:70]}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(out))
print("done ->", OUT)
