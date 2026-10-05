# -*- coding: utf-8 -*-
"""ES「输入 → 招式」表。Input=f8，InputDir=f52（1=Any 2=Up 3=Down，由运行时实测反推）。"""
import io, os, struct
from collections import defaultdict

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
d = open(os.path.join(BASE, "extracted", "skillactivate.ab.bin"), "rb").read()
OUT = os.path.join(BASE, "tools", "_inputmap2_out.txt")
DIRN = {0: "None", 1: "Any", 2: "Up", 3: "Down"}


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

idx = defaultdict(list)
for r in rows:
    g = {}
    for fn, v in r:
        g.setdefault(fn, v)
    if g.get(2) != 103401:
        continue
    inp = g.get(8, "")
    if not isinstance(inp, str):
        inp = ""
    dr = DIRN.get(g.get(52), "?")
    idx[(inp, dr)].append((g.get(3), g.get(4), g.get(5), g.get(24)))

L = ["ES(103401) 输入 → 招式    Input=f8, InputDir=f52", ""]
for k in sorted(idx, key=lambda x: (x[0], x[1])):
    L.append(f"### 按键=\"{k[0] or '(无/派生)'}\"  方向={k[1]}   ({len(idx[k])} 条)")
    for grp, order, name, fr in sorted(idx[k], key=lambda x: (x[0] or 0, x[1] or 0)):
        name = name if isinstance(name, str) else repr(name)
        f = fr if isinstance(fr, str) else ""
        L.append(f"    槽{grp}.{order:<3} {name:<22} {f[:38]}")
    L.append("")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT)
print(f"分组数 {len(idx)}")
