# -*- coding: utf-8 -*-
"""1) dump esbullet (10340101) 的完整字段
   2) 列出所有 103401xx (ES 专属弹幕)
   3) 在 js_src / dump.cs / 配置表里找 "esbullet" 的逻辑资源指向
"""
import io, os, struct, subprocess, sys

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _bulletparse import fields, rows  # noqa  复用解析

OUT = os.path.join(BASE, "tools", "_bulletparse2_out.txt")
L = []

L.append("=== esbullet / teacher_esbullet / surroundbullet 全字段 ===")
for _id, name, f45, payload in rows:
    if str(name) in ("esbullet", "teacher_esbullet", "surroundbullet", "herolight", "commonbullet"):
        L.append(f"\n--- {name}  id={_id}  ({len(payload)} bytes)")
        for fn, wt, v in fields(payload):
            if wt == 2:
                try:
                    sv = v.decode("utf-8")
                    if all(32 <= ord(c) < 127 for c in sv) and sv:
                        L.append(f"    f{fn:<3} str  \"{sv}\"")
                        continue
                except UnicodeDecodeError:
                    pass
                L.append(f"    f{fn:<3} len  {len(v)}B  {v[:24].hex()}")
            elif wt == 5:
                L.append(f"    f{fn:<3} f32  {struct.unpack('<f', struct.pack('<I', v))[0]}")
            elif wt == 1:
                L.append(f"    f{fn:<3} f64  {struct.unpack('<d', struct.pack('<Q', v))[0]}")
            else:
                L.append(f"    f{fn:<3} var  {v}")

L.append("\n=== 所有 103401xx (ES 专属) ===")
for _id, name, f45, _ in rows:
    if _id is not None and 10340100 <= int(_id) < 10340200:
        L.append(f"  {_id}  {name}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("\n".join(L[:120]))
