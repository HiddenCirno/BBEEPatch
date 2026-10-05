# -*- coding: utf-8 -*-
"""在所有配置表里找「动作名」和「潜能/技能 id」同时出现的那张表。

思路: 把 extracted/ 下每个 .ab 解出 TextAsset，搜一组已知动作名
（holdEX / attackA / AttackUp / fallup …）。
哪张表里同时出现「动作名」和「340xxx 这类潜能 id」，它就是我们要的映射表。
"""
import io, os, re

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
EX = os.path.join(BASE, "extracted")
OUT = os.path.join(BASE, "tools", "_findmap_out.txt")

import UnityPy

ACTIONS = [b"holdEX", b"attackA", b"attackB", b"attackC", b"AttackUp", b"fallup",
           b"atkAir12", b"dashAtk", b"UltraDash", b"attackD1", b"summon", b"dashAir2"]

L = []
files = sorted(f for f in os.listdir(EX) if f.endswith(".ab"))
L.append(f"待查表 {len(files)} 个")

for fn in files:
    p = os.path.join(EX, fn)
    try:
        env = UnityPy.load(p)
    except Exception as e:
        L.append(f"\n--- {fn}: 载入失败 {e}")
        continue

    found = []
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        try:
            try:
                d = obj.read()
                raw = d.m_Script
                if isinstance(raw, str):
                    raw = raw.encode("utf-8", "surrogateescape")
            except Exception:
                raw = obj.get_raw_data()
        except Exception:
            continue

        hits = {a.decode(): len(re.findall(re.escape(a), raw)) for a in ACTIONS}
        hits = {k: v for k, v in hits.items() if v}
        if not hits:
            continue

        # 同时找 340xxx / 103401 这类 id
        ids = sorted(set(int(m) for m in re.findall(rb"(?<!\d)(34\d{4}|103401|1[01]\d{4})(?!\d)", raw)))
        found.append((len(raw), hits, ids[:12], raw[:24]))

    if found:
        L.append(f"\n########## {fn} ##########")
        for sz, hits, ids, head in found:
            L.append(f"   TextAsset {sz} 字节  head={head!r}")
            L.append(f"     动作名命中: " + ", ".join(f"{k}×{v}" for k, v in hits.items()))
            L.append(f"     疑似 id: {ids}")
    else:
        L.append(f"  {fn}: 无动作名命中")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT)
