# -*- coding: utf-8 -*-
"""把 ActorActionName_<id> 整段扫出来 —— 这是 ES 的招式名表。

已确认: ActorActionName_3400 81 = 崔斯坦 / 340281 布鲁诺 / 340321 莫德雷德 /
        340391 高文 / 340501 贝德维尔
所以 34xxxx 这一段应该就是 ES 的招式名。
"""
import io, os, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
P = os.path.join(BASE, "extracted", "raw", "localization_chs.ab")
OUT = os.path.join(BASE, "tools", "_actnames_out.txt")
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
for pre in ["ActorActionName_", "ActorActionDesc_", "TriggerName_"]:
    L.append(f"\n########## {pre} ##########")
    rows = []
    for i2 in list(range(33000, 62000)) + list(range(330000, 620000)):
        h = chash(pre + str(i2))
        t = table.get(h)
        if t:
            rows.append((i2, t))
    L.append(f"命中 {len(rows)} 条")
    for i2, t in rows:
        L.append(f"  {i2:<8} {t[:110]}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT)
