# -*- coding: utf-8 -*-
"""解析 BulletConfig 表。

格式 = 连续 (uint32 len, protobuf payload)。
payload 已知字段(从头部样本推断):
    f1  varint   -> Id         (如 10010101)
    f2  string   -> LogicRes   ("rgbullet" / "jnbullet" / "nobullet" / "rcbullet")
    f45 string   -> "MidActor" (疑似 sorting layer / 类别)
"""
import io, os, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
P = os.path.join(BASE, "extracted", "bulletconfig.ab_BulletConfig.bin")
OUT = os.path.join(BASE, "tools", "_bulletparse_out.txt")

d = open(P, "rb").read()


def rv(b, i):
    """读 varint -> (值, 新下标)"""
    r = 0; s = 0
    while i < len(b):
        c = b[i]; i += 1
        r |= (c & 0x7F) << s
        if not (c & 0x80):
            break
        s += 7
    return r, i


def fields(b):
    """极简 protobuf 字段扫描, 返回 [(fieldno, wiretype, value)]"""
    out = []
    i = 0
    while i < len(b):
        try:
            k, i = rv(b, i)
        except Exception:
            break
        fn, wt = k >> 3, k & 7
        if wt == 0:
            v, i = rv(b, i); out.append((fn, wt, v))
        elif wt == 2:
            ln, i = rv(b, i)
            v = b[i:i + ln]; i += ln
            out.append((fn, wt, v))
        elif wt == 5:
            v = struct.unpack_from("<I", b, i)[0]; i += 4
            out.append((fn, wt, v))
        elif wt == 1:
            v = struct.unpack_from("<Q", b, i)[0]; i += 8
            out.append((fn, wt, v))
        else:
            break
    return out


rows = []
i = 0
n = len(d)
while i + 4 <= n:
    ln = struct.unpack_from("<I", d, i)[0]
    if ln == 0 or i + 4 + ln > n:
        break
    payload = d[i + 4:i + 4 + ln]
    i += 4 + ln
    fs = fields(payload)
    byno = {}
    for fn, wt, v in fs:
        byno.setdefault(fn, v)
    _id = byno.get(1)
    name = byno.get(2)
    if isinstance(name, bytes):
        try:
            name = name.decode("utf-8")
        except UnicodeDecodeError:
            name = repr(name)
    rows.append((_id, name, byno.get(45), payload))

lines = [f"BulletConfig 条目数: {len(rows)}", ""]
lines.append(f"{'Id':>12}  {'LogicRes':<28} {'f45':<12}")
for _id, name, f45, _ in rows:
    if isinstance(f45, bytes):
        try:
            f45 = f45.decode("utf-8")
        except UnicodeDecodeError:
            f45 = repr(f45)
    lines.append(f"{str(_id):>12}  {str(name):<28} {str(f45):<12}")

# 关注 ES / 纹章 / AH 相关的名字
lines.append("\n=== 名字里带 ah / es / crest / emblem / ring 的 ===")
for _id, name, f45, _ in rows:
    s = (str(name) + " " + str(f45)).lower()
    if any(k in s for k in ("ah", "crest", "emblem", "ring", "es_")):
        lines.append(f"  {_id}  {name}  f45={f45}")

# 也把所有出现的 LogicRes 名字去重列一下, 便于判断命名体系
names = sorted({str(r[1]) for r in rows})
lines.append(f"\n=== LogicRes 去重 ({len(names)}) ===")
lines.append(", ".join(names))

io.open(OUT, "w", encoding="utf-8").write("\n".join(lines))
print("done ->", OUT, len(rows))
