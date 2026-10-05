# -*- coding: utf-8 -*-
"""Which BulletConfig ids appear as binary int32 RoleId inside the ES ALGs?
Reveals data-driven AddRoleData spawns (incl. any non-10340101 ES bullet)."""
import io, os, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
EX = os.path.join(BASE, "extracted")
OUT = os.path.join(BASE, "tools", "_es_roidref_out.txt")


def rv(b, i):
    r = 0; s = 0
    while i < len(b):
        c = b[i]; i += 1; r |= (c & 0x7F) << s
        if not (c & 0x80):
            break
        s += 7
    return r, i


def fields(b):
    out = []; i = 0
    while i < len(b):
        try:
            k, i = rv(b, i)
        except Exception:
            break
        fn, wt = k >> 3, k & 7
        if wt == 0:
            v, i = rv(b, i); out.append((fn, wt, v))
        elif wt == 2:
            ln, i = rv(b, i); out.append((fn, wt, b[i:i + ln])); i += ln
        elif wt == 5:
            out.append((fn, wt, struct.unpack_from("<f", b, i)[0])); i += 4
        elif wt == 1:
            out.append((fn, wt, struct.unpack_from("<d", b, i)[0])); i += 8
        else:
            break
    return out


d = open(os.path.join(EX, "bulletconfig.ab_BulletConfig.bin"), "rb").read()
ids = []
i = 0
while i + 4 <= len(d):
    ln = struct.unpack_from("<I", d, i)[0]
    if ln == 0 or i + 4 + ln > len(d):
        break
    fs = {fn: v for fn, wt, v in fields(d[i + 4:i + 4 + ln])}
    i += 4 + ln
    if 1 in fs:
        ids.append(fs[1])

es = open(os.path.join(EX, "es_mono0.raw"), "rb").read()
esb = open(os.path.join(EX, "esbullet_mono.raw"), "rb").read()

L = [f"BulletConfig ids: {len(ids)}"]
for tag, raw in (("es.ab", es), ("esbullet.ab", esb)):
    L.append(f"\n=== int32 RoleId hits in {tag} ===")
    for v in sorted(set(ids)):
        n = raw.count(struct.pack("<i", v))
        if n:
            L.append(f"  id={v:<10} x{n}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("\n".join(L))
