# -*- coding: utf-8 -*-
"""Diagnostic #2: map every CreateBullet/bullet_id offset in es.ab to the nearest
preceding action-name marker; also hunt UDA / x1 / x3 / a3 / AD_hit spawn sites."""
import io, os, re, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
EX = os.path.join(BASE, "extracted")
OUT = os.path.join(BASE, "tools", "_es_map2_out.txt")


def strings(raw):
    out = []
    n = len(raw)
    i = 0
    while i + 4 <= n:
        ln = struct.unpack_from("<I", raw, i)[0]
        if 1 <= ln <= 96 and i + 4 + ln <= n:
            b = raw[i + 4:i + 4 + ln]
            if all(32 <= c < 127 for c in b):
                end = i + 4 + ln
                pad = (-ln) % 4
                if end + pad <= n and (pad == 0 or raw[end:end + pad] == b"\x00" * pad):
                    out.append((i, end + pad, b.decode("ascii")))
                    i = end + pad
                    continue
        i += 4
    return out


L = []
raw = open(os.path.join(EX, "es_mono0.raw"), "rb").read()
ss = strings(raw)
# index by offset
offs = [x[0] for x in ss]


def prev_strings(off, k=6):
    import bisect
    i = bisect.bisect_left(offs, off) - 1
    return ss[max(0, i - k + 1):i + 1]


def enclosing_action(off):
    import bisect
    i = bisect.bisect_left(offs, off) - 1
    # walk back to nearest exact "es" marker; next string is action name
    j = i
    while j > 0 and ss[j][2] != "es":
        j -= 1
    if ss[j][2] == "es" and j + 1 < len(ss):
        return ss[j + 1][2], ss[i][2] if i < len(ss) else "?"
    return "?", ss[i][2] if i < len(ss) else "?"


L.append("=== CreateBullet_ / CreateBulletIfTriggerChange occurrences by enclosing action ===")
for j, (o, e, s) in enumerate(ss):
    if s.startswith("CreateBullet"):
        params = ss[j + 1][2] if j + 1 < len(ss) else ""
        act, cur = enclosing_action(o)
        L.append(f"  @{o:<8} action={act!r:<22} func={s}  params={params!r}")

L.append("\n=== all strings containing UDA / Ultra (es.ab) ===")
for o, e, s in ss:
    if "UDA" in s or "Ultra" in s:
        act, _ = enclosing_action(o)
        L.append(f"  @{o:<8} [{act}] {s}")

L.append("\n=== all strings containing x1/x2/x3/xup/ax2/AD_hit/a3/aup (params) ===")
for o, e, s in ss:
    if re.search(r"bullet_action\s*:\s*\"?(x1|x2|x3|xup|ax2|AD_hit|a3|aup)\b", s):
        act, _ = enclosing_action(o)
        L.append(f"  @{o:<8} [{act}] {s}")

L.append("\n=== raw context around x1 spawn (offset 534510) ===")
o = 534510
sub = raw[o - 1200:o + 400]
txt = "".join(chr(c) if 32 <= c < 127 else "|" for c in sub)
L.append(txt)

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT, "lines:", len(L))
