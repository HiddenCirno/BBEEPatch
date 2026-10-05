# -*- coding: utf-8 -*-
"""解开 actor/logicdata/esbullet.ab —— 纹章弹幕的状态机。

这就是"彻底接管纹章解放"需要的东西:
   弹幕的行为(原地/移动/结束)由这份逻辑数据驱动,
   里面应该写着它的动作名、生命周期、以及播放哪个特效。
"""
import io, os, json, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
GAME = os.path.join(BASE, "..")
AB = os.path.join(GAME, "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")
OUT = os.path.join(BASE, "tools", "_esbullet_logic_out.txt")
EX = os.path.join(BASE, "extracted")

import UnityPy

idx = json.load(open(os.path.join(AB, "merge.json"), encoding="utf-8"))


def grab(path):
    e = next((x for x in idx if x["r"] == path), None)
    if not e:
        return None
    with open(os.path.join(AB, e["m"]), "rb") as f:
        f.seek(e["s"])
        return f.read(400000)


L = []
env = UnityPy.load(os.path.join(EX, "esbullet.ab"))
for obj in env.objects:
    L.append(f"[{obj.type.name}] pathid={obj.path_id}")
    if obj.type.name == "TextAsset":
        d = obj.read()
        raw = d.m_Script
        if isinstance(raw, str):
            raw = raw.encode("utf-8", "surrogateescape")
        L.append(f"   name={d.m_Name} len={len(raw)}")
        fn = os.path.join(EX, "esbullet_" + (d.m_Name or "x") + ".bin")
        open(fn, "wb").write(raw)
        L.append(f"   -> {fn}")
        L.append("   ---- 前 3000 字节 ----")
        try:
            L.append(raw[:3000].decode("utf-8"))
        except UnicodeDecodeError:
            L.append(repr(raw[:1500]))

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT)
