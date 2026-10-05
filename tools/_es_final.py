# -*- coding: utf-8 -*-
"""Final consolidated extractor for the ES emblem/bullet spawn system.

Serialization confirmed: [u32 len][ascii bytes][zero pad to 4].  A script call is
ActionScriptCall{Function, Params} -> two consecutive length-prefixed strings.

Produces:
  A. es.ab  : every bullet-spawn script call (function + params + enclosing action)
  B. es.ab  : every BulletAction* call (change action of an existing bullet)
  C. esbullet.ab : action list, per-action VFX/hit/cond, and ChangeAction transitions
  D. BulletConfig rows for the ids involved (Id, LogicRes, StartAction, Skin)
"""
import io, os, re, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
EX = os.path.join(BASE, "extracted")
OUT = os.path.join(BASE, "tools", "_es_final_out.txt")

L = []


def read_str(raw, off):
    if off + 4 > len(raw):
        return None, off
    ln = int.from_bytes(raw[off:off + 4], "little")
    if not (0 <= ln <= 4096) or off + 4 + ln > len(raw):
        return None, off
    b = raw[off + 4:off + 4 + ln]
    if any(c < 9 or (13 < c < 32) or c > 126 for c in b):
        return None, off
    nxt = off + 4 + ln
    nxt += (-(ln + 4)) % 4 if False else 0
    return b.decode("ascii", "replace"), off + 4 + ln


def call_at(raw, p, fn_bytes):
    """Given p = offset of function text, return Params string (length-prefixed)."""
    end = p + len(fn_bytes)
    end += (-len(fn_bytes)) % 4          # string pad to 4
    s, _ = read_str(raw, end)
    return s


def markers(raw, group):
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


def enclosing(marks, off):
    act = "?"
    for mo, nm in marks:
        if mo < off:
            act = nm
        else:
            break
    return act


def section_calls(title, raw, group, funcs):
    marks = markers(raw, group)
    L.append(f"\n\n############ {title} ############")
    L.append(f"  markers={len(marks)}")
    L.append(f"  actions: {', '.join(m[1] for m in marks)}")
    # search occurrences of each function name as a serialized string
    rows = []
    for fb in funcs:
        needle = struct.pack("<I", len(fb)) + fb
        start = 0
        while True:
            p = raw.find(needle, start)
            if p < 0:
                break
            start = p + 1
            ftoff = p + 4
            params = call_at(raw, ftoff, fb)
            act = enclosing(marks, p)
            rows.append((p, fb.decode(), act, params))
    rows.sort()
    for p, fn, act, params in rows:
        L.append(f"  @{p:<9} [{act:<20}] {fn:<26} {params}")

    # transitions (ChangeAction/ChangeSkill) per action, for esbullet
    if group == "esbullet":
        L.append("\n  --- ChangeAction / ChangeSkill transitions ---")
        for p, fn, act, params in rows:
            if fn in ("ChangeAction", "ChangeSkill"):
                L.append(f"    [{act:<14}] {fn}({params})")
    return rows


es = open(os.path.join(EX, "es_mono0.raw"), "rb").read()
esb = open(os.path.join(EX, "esbullet_mono.raw"), "rb").read()

section_calls("A. es.ab bullet spawns", es, "es",
              [b"CreateBulletIfTriggerChange", b"CreateBullet_summon", b"CreateBullet_", b"CreateBullet"])
section_calls("B. es.ab bullet action changes (BulletAction*)", es, "es",
              [b"BulletAction1", b"BulletAction2", b"BulletAction", b"BulletActionChange",
               b"ChangeBulletAction", b"SetBulletAction"])
section_calls("C. esbullet.ab calls", esb, "esbullet",
              [b"CreateBullet_", b"CreateBullet", b"ChangeAction", b"ChangeSkill", b"SetActionCD",
               b"RemoveBuff_es", b"DeleteBulletByTag", b"ScanBulletAndAddBuff", b"Splash", b"AddBuff"])

# ---- D. BulletConfig rows ----
d = open(os.path.join(EX, "bulletconfig.ab_BulletConfig.bin"), "rb").read()


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


rows = []
i = 0
while i + 4 <= len(d):
    ln = struct.unpack_from("<I", d, i)[0]
    if ln == 0 or i + 4 + ln > len(d):
        break
    fs = fields(d[i + 4:i + 4 + ln]); i += 4 + ln
    by = {}
    for fn, wt, v in fs:
        by.setdefault(fn, v)
    rows.append(by)

L.append("\n\n############ D. BulletConfig rows (Id, LogicRes, StartAction, Skin) ############")
L.append(f"  total rows: {len(rows)}")
want = ("esbullet", "surroundbullet", "commonbullet", "herolight", "firebullet", "thunderbullet")
L.append("\n  -- rows whose LogicRes is an ES/emblem-relevant resource --")
for by in rows:
    res = by.get(2)
    res = res.decode("utf-8", "replace") if isinstance(res, bytes) else res
    if res in want or (isinstance(res, str) and res.startswith("es")):
        sa = by.get(3); sk = by.get(10)
        sa = sa.decode("utf-8", "replace") if isinstance(sa, bytes) else sa
        sk = sk.decode("utf-8", "replace") if isinstance(sk, bytes) else sk
        L.append(f"    id={by.get(1)}  LogicRes={res!r}  StartAction={sa!r}  Skin={sk!r}")
L.append("\n  -- every row with id in 10340100..10340200 --")
for by in rows:
    _id = by.get(1)
    if isinstance(_id, int) and 10340100 <= _id < 10340200:
        res = by.get(2); sa = by.get(3); sk = by.get(10)
        res = res.decode("utf-8", "replace") if isinstance(res, bytes) else res
        sa = sa.decode("utf-8", "replace") if isinstance(sa, bytes) else sa
        sk = sk.decode("utf-8", "replace") if isinstance(sk, bytes) else sk
        L.append(f"    id={_id}  LogicRes={res!r}  StartAction={sa!r}  Skin={sk!r}")
L.append("\n  -- rows whose StartAction mentions a known esbullet action --")
knownactions = {"x1", "x2", "x3", "x32", "xup", "xup2", "ax1", "ax2", "dash", "dash2", "dashAir",
                "dashAir2", "dashAir3", "dashSkill", "dashSkill2", "DAA", "DAA2", "attackAir",
                "attackAir2", "fallmdownend", "aup", "aup2", "a3", "a32", "AD_hit", "UDA", "UDA0"}
for by in rows:
    sa = by.get(3)
    sa = sa.decode("utf-8", "replace") if isinstance(sa, bytes) else sa
    if sa in knownactions:
        res = by.get(2); sk = by.get(10)
        res = res.decode("utf-8", "replace") if isinstance(res, bytes) else res
        sk = sk.decode("utf-8", "replace") if isinstance(sk, bytes) else sk
        L.append(f"    id={by.get(1)}  LogicRes={res!r}  StartAction={sa!r}  Skin={sk!r}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT, "lines:", len(L))
