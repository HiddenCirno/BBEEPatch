# -*- coding: utf-8 -*-
"""反推 InputDir 字段号。

运行时探针已确认（这几个是实测值）：
    attackD1  输入=Attack/Down   attackAEX 输入=Skill/Down
    dashAir   输入=Dash/Any      jump      输入=Jump/Any
    dashAtk0  输入=Summon/Up     fallmEX0  输入=Summon/Down
    fall      输入=(空)/Any
拿这些行对比各 varint 字段的取值，找出「第 1 行 Down / 第 2 行 Down / 第 3 行 Any …」的那个字段。
"""
import io, os, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
P = os.path.join(BASE, "extracted", "skillactivate.ab.bin")
OUT = os.path.join(BASE, "tools", "_inputdir_out.txt")
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

TARGETS = ["attackD1", "attackAEX", "dashAir", "jump", "dashAtk0", "fallmEX0", "fall", "attack1"]

L = []
for t in TARGETS:
    for r in rows:
        g = {}
        for fn, wt, v in r:
            g.setdefault(fn, v)
        if g.get(2) != 103401 or g.get(5) != t:
            continue
        L.append(f"\n--- \"{t}\"  ---")
        L.append("   " + " | ".join(
            (f"f{fn}=v{v}" if wt == 'v' else (f"f{fn}=\"{v}\"" if wt == 's' else f"f{fn}={v:g}"))
            for fn, wt, v in r))
        break

# 对每个 varint 字段，看它在上面这些行里的取值
L.append("\n\n=== 各 varint 字段的取值对照 ===")
keys = {}
for t in TARGETS:
    for r in rows:
        g = {}
        for fn, wt, v in r:
            g.setdefault(fn, v)
        if g.get(2) == 103401 and g.get(5) == t:
            keys[t] = g
            break
varfields = set()
for g in keys.values():
    for k, v in g.items():
        if isinstance(v, int):
            varfields.add(k)
L.append(f"{'字段':>6} " + " ".join(f"{t:>10}" for t in TARGETS))
for fn in sorted(varfields):
    vals = [str(keys.get(t, {}).get(fn, '-')) for t in TARGETS]
    L.append(f"f{fn:<5} " + " ".join(f"{v:>10}" for v in vals))

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done")
