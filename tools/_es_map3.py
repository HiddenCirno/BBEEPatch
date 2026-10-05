# -*- coding: utf-8 -*-
"""Byte-level extractor for ES ActionLogicGroup bullet spawns.

Bypasses the length-prefixed string walker (which missed params whose length
prefix was not 4-byte aligned) and instead:
  * locates every action-name marker by its exact serialized bytes
  * locates every CreateBullet* Param string by regex on raw bytes
  * pairs each Param with the nearest preceding CreateBullet* Function name
  * attributes each Param to the enclosing action (nearest preceding marker)
"""
import io, os, re

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
EX = os.path.join(BASE, "extracted")
OUT = os.path.join(BASE, "tools", "_es_map3_out.txt")

FUNCS = [b"CreateBulletIfTriggerChange", b"CreateBulletX", b"CreateBulletU",
         b"CreateBulletm", b"CreateBullet_summon", b"CreateBullet_", b"CreateBullet",
         b"CreateBulletTarget", b"CreateBulletEx"]
FUNCS.sort(key=len, reverse=True)  # longest first so prefixes don't shadow


def markers(raw, group):
    """All offsets of the serialized group-name string, plus the following name."""
    gb = group.encode()
    pat = re.compile(b"\\x%02x\\x00\\x00\\x00" % len(gb) + re.escape(gb) + b"\\x00" * ((-len(gb)) % 4))
    out = []
    for m in pat.finditer(raw):
        n = m.end()
        if n + 4 > len(raw):
            continue
        ln = int.from_bytes(raw[n:n + 4], "little")
        if 1 <= ln <= 40:
            b = raw[n + 4:n + 4 + ln]
            if all(32 <= c < 127 for c in b):
                out.append((m.start(), b.decode()))
    return out


def func_for(raw, off):
    back = raw[max(0, off - 200):off]
    best = (-1, None)
    for f in FUNCS:
        p = back.rfind(f)
        if p > best[0]:
            best = (p, f.decode())
    return best[1]


PARAM = re.compile(rb"(?:trigger\s*:\s*\d+\s*,\s*)?bullet_id\s*:\s*\d+[\x20-\x7e]*")

L = []


def analyse(tag, raw, group):
    L.append(f"\n################ {tag}  ({len(raw)} bytes) ################")
    marks = markers(raw, group)
    L.append(f"  action markers ({group}): {len(marks)}")
    L.append("  action list: " + ", ".join(m[1] for m in marks))

    funcs = sorted({f.decode() for f in FUNCS if f in raw})
    L.append(f"  CreateBullet* function-name strings present: {funcs}")

    # every CreateBullet* script call: locate function strings, param = next PARAM within 200B
    L.append("\n=== CreateBullet* calls (function -> params -> enclosing action) ===")
    seen = set()
    for f in FUNCS:
        start = 0
        while True:
            p = raw.find(f, start)
            if p < 0:
                break
            start = p + 1
            # param string is the closest following PARAM match within 64..220 bytes
            seg = raw[p + len(f): p + len(f) + 240]
            m = PARAM.search(seg)
            if not m:
                continue
            params = m.group(0).decode("ascii", "replace")
            poff = p + len(f) + m.start()
            if (p, poff) in seen:
                continue
            seen.add((p, poff))
            act = "?"
            for mo, name in marks:
                if mo < poff:
                    act = name
                else:
                    break
            L.append(f"  @{p:<8} {f.decode():<26} action={act!r:<20} {params}")

    # params that contain no bullet_id (tag/action-keyword variants)
    L.append("\n=== other CreateBulletIfTriggerChange-style calls without bullet_id ===")
    for f in [b"CreateBulletIfTriggerChange", b"CreateBullet_summon", b"CreateBullet_"]:
        start = 0
        while True:
            p = raw.find(f, start)
            if p < 0:
                break
            start = p + 1
            seg = raw[p + len(f): p + len(f) + 80]
            if PARAM.search(seg):
                continue
            # next printable run
            m = re.search(rb"[\x20-\x7e]{1,60}", seg)
            if not m:
                continue
            params = m.group(0).decode("ascii", "replace")
            poff = p + len(f) + m.start()
            act = "?"
            for mo, name in marks:
                if mo < poff:
                    act = name
                else:
                    break
            L.append(f"  @{p:<8} {f.decode():<26} action={act!r:<20} {params!r}")


raw = open(os.path.join(EX, "es_mono0.raw"), "rb").read()
analyse("es.ab", raw, "es")
raw2 = open(os.path.join(EX, "esbullet_mono.raw"), "rb").read()
analyse("esbullet.ab", raw2, "esbullet")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT, "lines:", len(L))
