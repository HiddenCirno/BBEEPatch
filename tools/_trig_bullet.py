# -*- coding: utf-8 -*-
"""在 playerbattletriggerdetail 表里找出所有生成 esbullet(10340101) 的触发器条目。

找到了就能直接看到:
  - 纹章解放用的是哪个 bullet_action
  - 有没有现成的"朝某方向移动"的 action (dir_x/dir_y 怎么被用)
  - trigger id 59271 之类挂在谁身上
"""
import io, os, json, re, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
EX = os.path.join(BASE, "extracted")
OUT = os.path.join(BASE, "tools", "_trig_bullet_out.txt")

import UnityPy

env = UnityPy.load(os.path.join(EX, "playerbattletriggerdetail.ab"))
blobs = []
for obj in env.objects:
    if obj.type.name == "TextAsset":
        d = obj.read()
        raw = d.m_Script
        if isinstance(raw, str):
            raw = raw.encode("utf-8", "surrogateescape")
        blobs.append((d.m_Name, raw))

L = [f"TextAsset 数: {len(blobs)}"]
for name, raw in blobs:
    L.append(f"  {name}: {len(raw)} bytes")
    fn = os.path.join(EX, "trig_" + str(name) + ".bin")
    open(fn, "wb").write(raw)

    # 找 10340101 的所有出现
    for m in re.finditer(rb"bullet_id\s*:\s*10340101", raw):
        i = m.start()
        s = max(0, i - 120); e = min(len(raw), i + 260)
        txt = raw[s:e].decode("utf-8", "replace")
        txt = "".join(c if c.isprintable() else "|" for c in txt)
        L.append(f"\n--- @{i} ---\n{txt}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("\n".join(L[:200]))
