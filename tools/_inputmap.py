# -*- coding: utf-8 -*-
"""从 skillactivate 表生成「输入 → 招式」反向索引。

每条记录的字段里带 Input(按键) 和 InputDir(方向)，运行时探针已确认这两个值：
    attackD1  输入=Attack/Down      技巧=Attack
    attackAEX 输入=Skill/Down       技巧=Skill1
    dashAir   输入=Dash/Any
    jump      输入=Jump/Any
    dashAtk0  输入=Summon/Up
    fallmEX0  输入=Summon/Down

所以「输入 → 招式」这条链路是【静态可查】的，不用实测。
"""
import io, os, struct
from collections import defaultdict

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
P = os.path.join(BASE, "extracted", "skillactivate.ab.bin")
OUT = os.path.join(BASE, "tools", "_inputmap_out.txt")

d = open(P, "rb").read()


def rv(b, i):
    r = s = 0
    while i < len(b):
        c = b[i]; i += 1
        r |= (c & 0x7F) << s
        if not (c & 0x80):
            break
        s += 7
    return r, i


def fields(b):
    out, i = [], 0
    while i < len(b):
        try:
            k, i = rv(b, i)
        except Exception:
            break
        fn, wt = k >> 3, k & 7
        if wt == 0:
            v, i = rv(b, i); out.append((fn, 'v', v))
        elif wt == 2:
            ln, i = rv(b, i); v = b[i:i + ln]; i += ln
            try:
                s = v.decode('utf-8')
                s = s if all(32 <= ord(c) < 127 for c in s) and s else None
            except UnicodeDecodeError:
                s = None
            out.append((fn, 's', s if s else v))
        elif wt == 5:
            out.append((fn, 'f', struct.unpack_from('<f', b, i)[0])); i += 4
        elif wt == 1:
            out.append((fn, 'f', 0.0)); i += 8
        else:
            break
    return out


rows, i, n = [], 0, len(d)
while i + 4 <= n:
    ln = struct.unpack_from('<I', d, i)[0]
    if ln == 0 or i + 4 + ln > n:
        break
    rows.append(fields(d[i + 4:i + 4 + ln]))
    i += 4 + ln

L = []
# 先看清 ES 记录里到底有哪些字符串字段（找 InputDir）
L.append("=== ES 记录的字符串字段取值分布 ===")
strfields = defaultdict(set)
for r in rows:
    g = {}
    for fn, wt, v in r:
        g.setdefault(fn, v)
    if g.get(2) != 103401:
        continue
    for fn, wt, v in r:
        if wt == 's' and isinstance(v, str) and len(v) < 24:
            strfields[fn].add(v)
for fn in sorted(strfields):
    vals = sorted(strfields[fn])
    L.append(f"  f{fn}: {vals[:14]}")

# 找出 Input 字段（含 Attack/Dash/Skill/Jump/Summon）与 InputDir 字段
INPUT_F, DIR_F = None, None
for fn, vals in strfields.items():
    if {"Skill", "Attack", "Dash"} & set(vals):
        INPUT_F = fn
    if {"Down", "Up", "Any", "None"} & set(vals) and fn != INPUT_F:
        DIR_F = fn
L.append(f"\n判定: Input 字段=f{INPUT_F}  InputDir 字段=f{DIR_F}")

# 生成索引
idx = defaultdict(list)
for r in rows:
    g = {}
    for fn, wt, v in r:
        g.setdefault(fn, v)
    if g.get(2) != 103401:
        continue
    inp = g.get(INPUT_F, '?')
    dr = g.get(DIR_F, '') if DIR_F else ''
    idx[(inp, dr)].append((g.get(3), g.get(4), g.get(5)))

L.append("\n\n########## ES 输入 → 招式 ##########")
for k in sorted(idx, key=lambda x: (str(x[0]), str(x[1]))):
    L.append(f"\n--- 按键={k[0]}  方向={k[1]}  ({len(idx[k])} 条) ---")
    for grp, order, name in sorted(idx[k], key=lambda x: (x[0] or 0, x[1] or 0)):
        L.append(f"    槽{grp}.{order:<3} {name}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT)
