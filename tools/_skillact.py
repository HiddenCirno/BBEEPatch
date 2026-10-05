# -*- coding: utf-8 -*-
"""解 skillactivate / skillattr / fesactorskilllevelconf，看能不能静态拿到「技能 → 动作名」。"""
import io, os, re, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
EX = os.path.join(BASE, "extracted")
OUT = os.path.join(BASE, "tools", "_skillact_out.txt")

import UnityPy

NAMES = [b"holdEX", b"attackA", b"attackB", b"attackC", b"AttackUp", b"dashAtk",
         b"summon", b"fallup", b"atkAir12", b"UltraDashEX", b"attack1", b"dash"]

L = []
for fn in ["skillactivate.ab", "skillattr.ab", "fesactorskilllevelconf.ab"]:
    p = os.path.join(EX, fn)
    if not os.path.exists(p):
        L.append(f"!! 缺 {fn}"); continue
    L.append(f"\n================ {fn} ================")
    try:
        env = UnityPy.load(p)
    except Exception as e:
        L.append(f"  载入失败 {e}"); continue
    for obj in env.objects:
        L.append(f"  [{obj.type.name}] pathid={obj.path_id}")
        if obj.type.name == "TextAsset":
            try:
                d = obj.read()
                raw = d.m_Script
                if isinstance(raw, str):
                    raw = raw.encode("utf-8", "surrogateescape")
            except Exception:
                raw = obj.get_raw_data()
            L.append(f"     {len(raw)} 字节  head={raw[:48]!r}")
            out = os.path.join(EX, fn + "_" + str(getattr(d, 'm_Name', 'x') if 'd' in dir() else 'x') + ".bin")
            open(os.path.join(EX, fn + ".bin"), "wb").write(raw)
            for kw in NAMES:
                n = len(re.findall(re.escape(kw), raw))
                if n:
                    L.append(f"     命中 {kw.decode()}: {n}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done")
