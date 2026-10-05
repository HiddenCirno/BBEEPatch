# -*- coding: utf-8 -*-
"""找出 skillactivate 表里的「姿态」(地面/空中) 字段。

方法: 拿成对的地面/空中动作对比同一批 varint 字段，取那个"地面=0/空中=1"的字段。

对照对:
    attackAEX(2.1 地) vs attackA_AirEX(2.5 空)
    attackB  (2.3 地) vs attackB_Air (2.7 空)
    attackC  (2.4 地) vs attackC_Air (2.8 空)
    dashAtk0 (11.1 地) vs dashAirAtk0(11.2 空)
    dashAtk  (5.2 地)  vs dashAirAtk (5.4 空)
    dashAtkG (5.25 地) vs dashAirAtkG(5.23 空)
"""
import io, os, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
d = open(os.path.join(BASE, "extracted", "skillactivate.ab.bin"), "rb").read()
OUT = os.path.join(BASE, "tools", "_posture_out.txt")


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
            v, i = rv(b, i); out.append((fn, v))
        elif wt == 2:
            ln, i = rv(b, i); v = b[i:i + ln]; i += ln
            try:
                s = v.decode('utf-8')
                s = s if all(32 <= ord(c) < 127 for c in s) and s else None
            except UnicodeDecodeError:
                s = None
            out.append((fn, s if s else v))
        elif wt == 5:
            out.append((fn, struct.unpack_from('<f', b, i)[0])); i += 4
        elif wt == 1:
            i += 8; out.append((fn, 0.0))
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


def get(slot, name):
    for r in rows:
        g = {}
        for fn, v in r:
            g.setdefault(fn, v)
        if g.get(2) == 103401 and g.get(3) == slot and g.get(5) == name:
            return g
    return None


PAIRS = [
    ((2, "attackAEX"),       (2, "attackA_AirEX")),   # 注意: slot 相同, 靠名字区分
    ((2, "attackB"),         (2, "attackB_Air")),
    ((2, "attackC"),         (2, "attackC_Air")),
    ((11, "dashAtk0"),       (11, "dashAirAtk0")),
    ((5, "dashAtk"),         (5, "dashAirAtk")),
    ((5, "dashAtkG"),        (5, "dashAirAtkG")),
]

L = ["地面/空中 成对动作的字段差异（找 posture 字段）\n"]
varfields = set()
data = []
for (s1, n1), (s2, n2) in PAIRS:
    a, b = get(s1, n1), get(s2, n2)
    if not a or not b:
        L.append(f"!! 缺 {n1} 或 {n2}")
        continue
    data.append((n1, n2, a, b))
    for k, v in list(a.items()) + list(b.items()):
        if isinstance(v, int):
            varfields.add(k)

hdr = f"{'字段':>6} "
for n1, n2, _, _ in data:
    hdr += f"{n1[:11]:>13}/{n2[:11]:<13}"
L.append(hdr)
for fn in sorted(varfields):
    line = f"f{fn:<5} "
    for n1, n2, a, b in data:
        va, vb = a.get(fn, '-'), b.get(fn, '-')
        mark = " ★" if (va != vb and va in (0, 1) and vb in (0, 1)) else ""
        line += f"{str(va):>13}/{str(vb):<13}"
        if mark:
            line += mark
    L.append(line)

L.append("\n★ = 该字段在地面/空中成对动作间不同, 且取值都是 0/1 —— posture 字段的强候选")
io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done")
