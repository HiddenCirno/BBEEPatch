# -*- coding: utf-8 -*-
"""最终版「输入 → 招式」表：按键(f8) + 方向(f52) + 姿态(f19)。"""
import io, os, struct
from collections import defaultdict

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
d = open(os.path.join(BASE, "extracted", "skillactivate.ab.bin"), "rb").read()
OUT = os.path.join(BASE, "tools", "_finalmap_out.txt")
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
    post = "空中" if g.get(19) == 1 else "地面"
    nm = g.get(5)
    if not isinstance(nm, str):
        nm = repr(nm)
    idx[(inp, dr, post)].append((g.get(3), g.get(4), nm, g.get(24)))

L = ["ES 输入 → 招式   按键=f8  方向=f52(1=Any 2=Up 3=Down)  姿态=f19(缺省=地面 1=空中)", ""]
order = {"Attack": 0, "Dash": 1, "Jump": 2, "Skill": 3, "Summon": 4, "": 5}
for k in sorted(idx, key=lambda x: (order.get(x[0], 9), x[1], x[2])):
    L.append(f"\n### 按键=\"{k[0] or '(无/派生)'}\"  方向={k[1]}  姿态={k[2]}   ({len(idx[k])} 条)")
    for grp, order_, name, fr in sorted(idx[k], key=lambda x: (x[0] or 0, x[1] or 0)):
        f = fr if isinstance(fr, str) else ""
        L.append(f"    槽{grp}.{order_:<3} {name:<24} {f[:36]}")
io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done, 分组", len(idx))
