# -*- coding: utf-8 -*-
"""解析 skillactivate 表 —— 技能 id → 相关动作名。

格式: 连续 (uint32 len, protobuf)。
样本第一条: 10 e8 07 | 18 05 | 20 01 | 2a 07 "dashAir" | 32 07 "Start_5" | 3a 05 "End_5" | 42 04 "Dash"
推测: f2=id, f3/f4=序号, f5..=各种动作名(起手/结束/触发动作)
"""
import io, os, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
P = os.path.join(BASE, "extracted", "skillactivate.ab.bin")
OUT = os.path.join(BASE, "tools", "_skillact_parse_out.txt")

d = open(P, "rb").read()


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
            ln, i = rv(b, i)
            v = b[i:i + ln]; i += ln
            try:
                s = v.decode('utf-8')
                ok = all(32 <= ord(c) < 127 for c in s) and s
            except UnicodeDecodeError:
                ok = False; s = None
            out.append((fn, 's', s if ok else v))
        elif wt == 5:
            out.append((fn, 'f32', struct.unpack_from('<f', b, i)[0])); i += 4
        elif wt == 1:
            out.append((fn, 'f64', struct.unpack_from('<d', b, i)[0])); i += 8
        else:
            break
    return out


rows = []
i, n = 0, len(d)
while i + 4 <= n:
    ln = struct.unpack_from('<I', d, i)[0]
    if ln == 0 or i + 4 + ln > n:
        break
    payload = d[i + 4:i + 4 + ln]
    i += 4 + ln
    rows.append(fields(payload))

L = [f"条目数 {len(rows)}", f"文件 {len(d)} 字节"]

# 字段号分布
from collections import Counter
fc = Counter()
for r in rows:
    for fn, wt, v in r:
        fc[(fn, wt)] += 1
L.append("\n字段号分布: " + ", ".join(f"f{fn}({wt})×{c}" for (fn, wt), c in sorted(fc.items())))

L.append("\n=== 前 6 条完整 ===")
for r in rows[:6]:
    L.append("  " + " | ".join(f"f{fn}={v!r}" for fn, wt, v in r))

# 找含关键动作名的条目
KEYS = ["holdEX", "AttackUp", "attackA", "attackB", "attackC", "summon",
        "fallup", "atkAir12", "UltraDashEX", "dashAtk"]
for kw in KEYS:
    L.append(f"\n=== 含 \"{kw}\" 的条目 ===")
    hit = 0
    for r in rows:
        if any(isinstance(v, str) and v == kw for fn, wt, v in r):
            L.append("  " + " | ".join(f"f{fn}={v!r}" for fn, wt, v in r))
            hit += 1
            if hit >= 12:
                break
    if not hit:
        L.append("  (无精确命中)")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT)
