# -*- coding: utf-8 -*-
"""Extract data-driven bullet-action entries in es.ab: each is a binary int32
RoleId (10340101) followed (within a short window) by two length-prefixed strings
= the target bullet Action and Tag.  These are the BulletAction* / 're-action an
existing emblem bullet' entries (distinct from the script CreateBullet calls)."""
import io, os, re, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
EX = os.path.join(BASE, "extracted")
OUT = os.path.join(BASE, "tools", "_es_bulletact_out.txt")
L = []


def read_str(raw, off):
    if off + 4 > len(raw):
        return None, off
    ln = int.from_bytes(raw[off:off + 4], "little")
    if not (1 <= ln <= 64) or off + 4 + ln > len(raw):
        return None, off
    b = raw[off + 4:off + 4 + ln]
    if not all(32 <= c < 127 for c in b):
        return None, off
    return b.decode("ascii", "replace"), off + 4 + ln


def markers(raw, group):
    gb = group.encode()
    pat = re.compile(b"\\x%02x\\x00\\x00\\x00" % len(gb) + re.escape(gb) + b"\\x00" * ((-len(gb)) % 4))
    out = []
    for m in pat.finditer(raw):
        n = m.end()
        ln = int.from_bytes(raw[n:n + 4], "little") if n + 4 <= len(raw) else 0
        if 1 <= ln <= 40:
            b = raw[n + 4:n + 4 + ln]
            if all(32 <= c < 127 for c in b):
                out.append((m.start(), b.decode()))
    return out


raw = open(os.path.join(EX, "es_mono0.raw"), "rb").read()
marks = markers(raw, "es")


def enclosing(off):
    act = "?"
    for mo, nm in marks:
        if mo < off:
            act = nm
        else:
            break
    return act


target = struct.pack("<i", 10340101)
L.append("=== data-driven (id=10340101, Action, Tag) entries in es.ab ===")
start = 0
rows = []
while True:
    i = raw.find(target, start)
    if i < 0:
        break
    start = i + 1
    # scan forward up to 80 bytes for two consecutive strings
    a = None
    for off in range(i + 4, i + 90, 1):
        s, nx = read_str(raw, off)
        if s:
            s2, _ = read_str(raw, nx + ((4 - nx % 4) % 4))
            if s2 is None:
                s2, _ = read_str(raw, nx)
            rows.append((i, enclosing(i), s, s2))
            break
for i, act, a, t in rows:
    L.append(f"  @{i:<9} [{act:<18}] Action={a!r}  Tag={t!r}")
L.append(f"\n  total: {len(rows)}")

# also the textual BulletAction form
L.append("\n=== textual Bullet:* script calls ===")
for m in re.finditer(rb"Bullet:\"[^\"]*\"[^\x00]*", raw):
    i = m.start()
    L.append(f"  @{i} [{enclosing(i)}] {m.group(0).decode('ascii','replace')}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT, "lines:", len(L))
