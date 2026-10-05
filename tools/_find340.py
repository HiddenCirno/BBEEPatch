# -*- coding: utf-8 -*-
"""全库搜骑士 id 的 varint 编码，看它到底出现在哪张表 / 哪个资源里。"""
import io, os, re

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
EX = os.path.join(BASE, "extracted")
OUT = os.path.join(BASE, "tools", "_find340_out.txt")
GAME = os.path.join(BASE, "..")
ABDIR = os.path.join(GAME, "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")

IDS = [340011, 340061, 340081, 340281, 340321, 340391, 340441, 340501]


def varint(n):
    out = bytearray()
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            out.append(b | 0x80)
        else:
            out.append(b)
            break
    return bytes(out)


L = []
pat = {i: varint(i) for i in IDS}
L.append("id -> varint 字节: " + ", ".join(f"{i}={pat[i].hex()}" for i in IDS))

# 1) extracted 下的文件（含解出来的 TextAsset .bin）
L.append("\n=== extracted/ 下的文件 ===")
for fn in sorted(os.listdir(EX)):
    p = os.path.join(EX, fn)
    if not os.path.isfile(p) or os.path.getsize(p) > 8_000_000:
        continue
    try:
        d = open(p, "rb").read()
    except Exception:
        continue
    hits = [i for i, b in pat.items() if b in d]
    if hits:
        L.append(f"  {fn}: {hits}")

# 2) .m 容器里按 merge.json 索引，抽查 role/es 与 actor/logicdata 下的 bundle
L.append("\n=== .m 容器抽查 (只查 es / logicdata 相关) ===")
import json
try:
    idx = json.load(open(os.path.join(ABDIR, "merge.json"), encoding="utf-8"))
except Exception as e:
    idx = []
    L.append(f"  merge.json 读不到: {e}")

checked = 0
for e in idx:
    r = e["r"].lower()
    if not (r.startswith("actor/logicdata/") or "/es/" in r or r.startswith("actor/logicdata/es")):
        continue
    try:
        with open(os.path.join(ABDIR, e["m"]), "rb") as f:
            f.seek(e["s"])
            d = f.read(3_000_000)
    except Exception:
        continue
    checked += 1
    hits = [i for i, b in pat.items() if b in d]
    if hits:
        L.append(f"  {e['r']}: {hits}")
    if checked > 400:
        break
L.append(f"  抽查了 {checked} 个 bundle")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT)
