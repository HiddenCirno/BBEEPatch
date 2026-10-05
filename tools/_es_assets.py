# -*- coding: utf-8 -*-
"""1) 列出 ES 的全部特效 prefab 路径 (找"翅膀纹章"和 es_AH_*)
   2) 把 actor/logicdata/esbullet.ab 抽出来看 (弹幕状态机)
"""
import io, os, json, sys

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
GAME = os.path.join(BASE, "..")
AB = os.path.join(GAME, "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")
MERGE = os.path.join(AB, "merge.json")
OUT = os.path.join(BASE, "tools", "_es_assets_out.txt")

idx = json.load(open(MERGE, encoding="utf-8"))
L = [f"总条目 {len(idx)}"]

es = [e for e in idx if "/es" in e["r"].lower() and "effect/prefab" in e["r"].lower()]
L.append(f"\n=== ES 特效 prefab ({len(es)}) ===")
for e in sorted(es, key=lambda x: x["r"]):
    L.append(f"  {e['r']}")

# 翅膀 / 纹章 关键字
L.append("\n=== 关键字过滤 (wing/ah/crest/burst/ultra/ring) ===")
for e in es:
    n = e["r"].lower()
    if any(k in n for k in ("wing", "ah_", "_ah", "crest", "burst", "ultra", "ring", "circ", "emblem")):
        L.append(f"  {e['r']}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))


def extract(path, outfile):
    e = next((x for x in idx if x["r"] == path), None)
    if not e:
        return f"!! 找不到 {path}"
    off = e["s"]
    mp = os.path.join(AB, e["m"])
    with open(mp, "rb") as f:
        f.seek(off)
        head = f.read(64)
    fsize = os.path.getsize(mp)
    with open(mp, "rb") as f:
        f.seek(off)
        data = f.read()  # 到文件尾; 下面按 UnityFS 自己切
    return data, head, fsize


r = extract("actor/logicdata/esbullet.ab", None)
if isinstance(r, str):
    print(r)
else:
    data, head, fsize = r
    print("head:", head[:40])
    print("剩余字节:", len(data))
    open(os.path.join(BASE, "extracted", "esbullet.ab"), "wb").write(data[:200000])
    print("已写 extracted/esbullet.ab (截断 200KB)")
print("done ->", OUT)
