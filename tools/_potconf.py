# -*- coding: utf-8 -*-
"""解析 baseactorpotentialconf / potentialconf，找骑士潜能 id 挂着什么字段。

骑士潜能 id（来自本地化 ActorActionName_<id>）:
    340061 纹章解放  340081 崔斯坦  340281 布鲁诺  340321 莫德雷德
    340391 高文      340441 加拉哈德 340501 贝德维尔
"""
import io, os, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
EX = os.path.join(BASE, "extracted")
OUT = os.path.join(BASE, "tools", "_potconf_out.txt")

import UnityPy

KNIGHTS = {340061, 340081, 340281, 340321, 340391, 340441, 340501,
           340271, 340191, 340171, 340293, 340294, 340011, 340041}


def rv(b, i):
    r = s = 0
    while i < len(b):
        c = b[i]; i += 1
        r |= (c & 0x7F) << s
        if not (c & 0x80):
            break
        s += 7
    return r, i


def fields(b):
    out, i = [], 0
    while i < len(b):
        try:
            k, i = rv(b, i)
        except Exception:
            break
        fn, wt = k >> 3, k & 7
        if wt == 0:
            v, i = rv(b, i); out.append((fn, 'v', v))
        elif wt == 2:
            ln, i = rv(b, i); v = b[i:i + ln]; i += ln
            try:
                s = v.decode('utf-8')
                s = s if all(32 <= ord(c) < 127 for c in s) and s else None
            except UnicodeDecodeError:
                s = None
            out.append((fn, 's', s if s else v))
        elif wt == 5:
            out.append((fn, 'f', struct.unpack_from('<f', b, i)[0])); i += 4
        elif wt == 1:
            out.append((fn, 'f', 0.0)); i += 8
        else:
            break
    return out


L = []
for fn in ["baseactorpotentialconf.ab", "potentialconf.ab"]:
    p = os.path.join(EX, fn)
    if not os.path.exists(p):
        continue
    env = UnityPy.load(p)
    raw = None
    for obj in env.objects:
        if obj.type.name == "TextAsset":
            try:
                d = obj.read()
                raw = d.m_Script
                if isinstance(raw, str):
                    raw = raw.encode("utf-8", "surrogateescape")
            except Exception:
                raw = obj.get_raw_data()
            break
    if raw is None:
        L.append(f"!! {fn} 没有 TextAsset")
        continue

    L.append(f"\n########## {fn}  {len(raw)} 字节 ##########")
    from collections import Counter
    rows, i, n = [], 0, len(raw)
    while i + 4 <= n:
        ln = struct.unpack_from('<I', raw, i)[0]
        if ln == 0 or i + 4 + ln > n:
            break
        rows.append(fields(raw[i + 4:i + 4 + ln]))
        i += 4 + ln
    L.append(f"条目数 {len(rows)}")

    fc = Counter()
    for r in rows:
        for f2, wt, v in r:
            fc[(f2, wt)] += 1
    L.append("字段分布: " + ", ".join(f"f{k[0]}({k[1]})×{v}" for k, v in sorted(fc.items())))

    # 找骑士 id 出现在哪个字段
    L.append("\n=== 含骑士 id 的条目 ===")
    hit = 0
    for r in rows:
        vals = [v for f2, wt, v in r if wt == 'v']
        if any(v in KNIGHTS for v in vals):
            txt = " | ".join(
                f"f{f2}={v!r}" if wt == 'v' else (f"f{f2}=\"{v}\"" if wt == 's' else f"f{f2}={v:g}")
                for f2, wt, v in r)
            L.append("  " + txt[:260])
            hit += 1
            if hit >= 25:
                break
    if hit == 0:
        L.append("  (无)  前 3 条样本:")
        for r in rows[:3]:
            L.append("  " + " | ".join(f"f{f2}={v!r}" for f2, wt, v in r)[:260])

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT)
