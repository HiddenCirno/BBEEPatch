# -*- coding: utf-8 -*-
"""解 actor/avatarskincolors.ab —— 皮肤颜色表 id -> Color (+blendMode)。

结构(dump.cs):
    AvatarSkinColors : ScriptableObject
        private List<AvatarSkinColors.General> _generalConfigs;   // 序列化字段
    AvatarSkinColors.General { int Id; string descriptions; Color color; SkinBlendMode blendMode; }
Unity 序列化: 4 字节数组长度 + 每条 { int Id; 对齐字符串; float r,g,b,a; int blendMode }
"""
import io, json, os, struct, sys, re

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
GAME = os.path.join(BASE, "..")
AB = os.path.join(GAME, "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")
idx = json.load(io.open(os.path.join(AB, "merge.json"), encoding="utf-8"))
BY = {e["r"].lower(): e for e in idx}
path = sys.argv[1] if len(sys.argv) > 1 else "actor/avatarskincolors.ab"


def bundle_bytes(p):
    e = BY.get(p.lower())
    mp = os.path.join(AB, e["m"])
    with open(mp, "rb") as f:
        f.seek(e["s"]); d = f.read(256)
        q = d.index(b"\x00", 8) + 1
        q += 4
        q = d.index(b"\x00", q) + 1
        q = d.index(b"\x00", q) + 1
        total = struct.unpack_from(">q", d, q)[0]
        f.seek(e["s"]); return f.read(total)


import UnityPy
env = UnityPy.load(bundle_bytes(path))
for obj in env.objects:
    if obj.type.name != "MonoBehaviour":
        continue
    raw = obj.get_raw_data()
    print("MonoBehaviour %d 字节" % len(raw))
    n = struct.unpack_from("<i", raw, 0)[0]
    print("条目数(按序列化首字段) =", n)
    p = 4
    out = []
    for i in range(max(0, min(n, 500))):
        if p + 4 > len(raw):
            break
        Id = struct.unpack_from("<i", raw, p)[0]; p += 4
        ln = struct.unpack_from("<i", raw, p)[0]; p += 4
        desc = raw[p:p+ln].decode("utf-8", "replace"); p += ln
        p = (p + 3) & ~3
        r, g, b, a = struct.unpack_from("<4f", raw, p); p += 16
        bm = struct.unpack_from("<i", raw, p)[0]; p += 4
        out.append((Id, desc, (r, g, b, a), bm))
    print("解出 %d 条:" % len(out))
    for Id, desc, c, bm in out[:40]:
        print("   id=%-4d color=(%.3f, %.3f, %.3f, %.3f) blend=%d  %s" % (Id, c[0], c[1], c[2], c[3], bm, desc))
