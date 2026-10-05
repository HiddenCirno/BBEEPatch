# -*- coding: utf-8 -*-
"""从 bulletconfig.ab 里取出弹幕配置表, 找出 ES 的纹章 / 贝德维尔相关条目。

目的: 探针要在游戏里跑一轮才有数据, 而配置表现在就能读 —— 先静态查一遍,
      如果纹章的 Skin 是 es_AH_* 之类, 就能跟日志里的特效名对上, 提前锁定 idx。
"""
import io, os, sys, json

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
OUT = os.path.join(BASE, "tools", "_bullets_out.txt")
OUTDIR = os.path.join(BASE, "extracted")

import UnityPy

res = []
for name in ["bulletconfig.ab", "es_logic.ab", "es_ah_01.ab"]:
    p = os.path.join(OUTDIR, name)
    if not os.path.exists(p):
        res.append(f"!! 缺 {name}")
        continue
    env = UnityPy.load(p)
    res.append(f"\n================ {name} ================")
    for obj in env.objects:
        res.append(f"  [{obj.type.name}] pathid={obj.path_id}")
        if obj.type.name == "TextAsset":
            d = obj.read()
            raw = d.m_Script
            if isinstance(raw, str):
                raw = raw.encode("utf-8", "surrogateescape")
            fn = os.path.join(OUTDIR, name + "_" + (d.m_Name or "unnamed") + ".bin")
            os.makedirs(os.path.dirname(fn), exist_ok=True)
            open(fn, "wb").write(raw)
            res.append(f"     -> dump {len(raw)} bytes -> {fn}")
            head = raw[:200]
            res.append("     head: " + repr(head))

io.open(OUT, "w", encoding="utf-8").write("\n".join(res))
print("done")
