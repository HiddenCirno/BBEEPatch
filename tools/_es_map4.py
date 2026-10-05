# -*- coding: utf-8 -*-
"""Diagnostic #4:
  - raw context after every CreateBullet* Function string (to recover tag-only params)
  - raw context around esbullet markers that gave suspicious names ('q','a')
"""
import io, os, re

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
EX = os.path.join(BASE, "extracted")
OUT = os.path.join(BASE, "tools", "_es_map4_out.txt")

L = []


def vis(b):
    return "".join(chr(c) if 32 <= c < 127 else "." for c in b)


for tag, fn in [("es.ab", "es_mono0.raw"), ("esbullet.ab", "esbullet_mono.raw")]:
    raw = open(os.path.join(EX, fn), "rb").read()
    L.append(f"\n################ {tag} ################")
    for f in [b"CreateBulletIfTriggerChange", b"CreateBulletX", b"CreateBulletU",
              b"CreateBulletm", b"CreateBullet_summon", b"CreateBullet_", b"CreateBullet"]:
        start = 0
        while True:
            p = raw.find(f, start)
            if p < 0:
                break
            start = p + 1
            L.append(f"\n@{p}  {f.decode()}\n   after: {vis(raw[p+len(f):p+len(f)+150])}")

    if tag == "esbullet.ab":
        gb = b"esbullet"
        pat = re.compile(b"\\x08\\x00\\x00\\x00" + gb)
        for k, m in enumerate(pat.finditer(raw)):
            n = m.end()
            ln = int.from_bytes(raw[n:n + 4], "little")
            L.append(f"\nmarker#{k} @{m.start()}  nextlen={ln}  nextbytes={vis(raw[n:n+48])}")
            if k >= 12:
                break

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT, "lines:", len(L))
