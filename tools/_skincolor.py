# -*- coding: utf-8 -*-
"""解皮肤配置里的【着色器属性名】(SkinColor.prop) 与 专属特效映射 (SkinEffect.skinToEffect)。

数据结构来自 Gen/pbdef.js:
    SkinColor  { string prop = 1; int32 id = 2; }
    SkinEffect { map<string,string> skinToEffect = 1; }
    AvatarSkinConf { ... repeated SkinColor color; repeated SkinEffect effects; ... }
做法: 从 merge.json 取 (偏移, 清单文件) 切出 UnityFS 块, 用 UnityPy 取 TextAsset 原始字节,
      再按 protobuf 扫, 把 _ 开头的字符串(属性名)与 .ab 结尾的字符串(特效)都捞出来。
"""
import io, json, os, re, struct, sys

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
GAME = os.path.join(BASE, "..")
AB = os.path.join(GAME, "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")
idx = json.load(io.open(os.path.join(AB, "merge.json"), encoding="utf-8"))
BY = {e["r"].lower(): e for e in idx}


def bundle_bytes(path):
    e = BY.get(path.lower())
    if e is None:
        return None
    mp = os.path.join(AB, e["m"])
    with open(mp, "rb") as f:
        f.seek(e["s"]); d = f.read(256)
        p = d.index(b"\x00", 8) + 1
        p += 4
        p = d.index(b"\x00", p) + 1
        p = d.index(b"\x00", p) + 1
        total = struct.unpack_from(">q", d, p)[0]
        f.seek(e["s"]); return f.read(total)


def raw_of(path):
    import UnityPy
    data = bundle_bytes(path)
    if data is None:
        return None
    env = UnityPy.load(data)
    for obj in env.objects:
        if obj.type.name == "TextAsset":
            try:
                t = obj.read()
                r = getattr(t, "m_Script", None)
                if r is None:
                    r = bytes(t.script) if hasattr(t, "script") else None
                if isinstance(r, str):
                    r = r.encode("utf-8", "surrogateescape")
                return r
            except Exception:
                pass
    return None


def strings(b, minlen=2):
    out = []
    i, n = 0, len(b)
    while i < n:
        c = b[i]
        if 0x20 <= c < 0x7f:
            j = i
            while j < n and 0x20 <= b[j] < 0x7f:
                j += 1
            if j - i >= minlen:
                out.append(b[i:j].decode("latin1"))
            i = j
        else:
            i += 1
    return out


for path in sys.argv[1:]:
    r = raw_of(path)
    print("=" * 70)
    print("### %s  (%s bytes)" % (path, "失败" if r is None else len(r)))
    if not r:
        continue
    ss = strings(r)
    props = sorted(set(s for s in ss if s.startswith("_") and len(s) > 2))
    effects = sorted(set(s for s in ss if s.lower().endswith(".ab") or "/" in s and "effect" in s.lower()))
    print("  —— 候选属性名 (%d) ——" % len(props))
    for p in props[:60]:
        print("     ", p)
    if effects:
        print("  —— 候选特效路径 (%d) ——" % len(effects))
        for p in effects[:20]:
            print("     ", p)
