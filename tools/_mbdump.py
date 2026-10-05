# -*- coding: utf-8 -*-
"""把一个 prefab 里 MonoBehaviour 的【序列化字段树】打出来。

为什么需要: `MaterialTinterProxy` / `MaterialTinter` 内部的插值器是 SerializeReference,
普通读取只能拿到壳。但 UnityPy 的 `read_typetree()` 走的是 bundle 里的类型树,
能把字段名和值(包括它对哪个材质的引用、插值器的属性名/颜色)摊开。

用法:  python _mbdump.py <ab路径> [物体名片段]
"""
import io, json, os, re, struct, sys
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

BASE = os.path.dirname(os.path.abspath(__file__))
AB = os.path.join(BASE, "..", "..", "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")
idx = json.load(io.open(os.path.join(AB, "merge.json"), encoding="utf-8"))
BY = {e["r"].lower(): e for e in idx}


def bundle_bytes(path):
    e = BY.get(path.lower())
    if e is None:
        return None
    with open(os.path.join(AB, e["m"]), "rb") as f:
        f.seek(e["s"])
        d = f.read(256)
        p = d.index(b"\x00", 8) + 1
        p += 4
        p = d.index(b"\x00", p) + 1
        p = d.index(b"\x00", p) + 1
        total = struct.unpack_from(">q", d, p)[0]
        f.seek(e["s"])
        return f.read(total)


def walk(node, indent=0, depth=0):
    pad = "  " * indent
    if depth > 8:
        print(pad + "...")
        return
    if isinstance(node, dict):
        for k, v in node.items():
            if isinstance(v, (dict, list)):
                print("%s%s:" % (pad, k))
                walk(v, indent + 1, depth + 1)
            else:
                print("%s%s = %r" % (pad, k, v))
    elif isinstance(node, list):
        print("%s[%d 项]" % (pad, len(node)))
        for i, v in enumerate(node[:12]):
            print("%s #%d:" % (pad, i))
            walk(v, indent + 1, depth + 1)
    else:
        print(pad + repr(node))


def run(path, want=None):
    data = bundle_bytes(path)
    print("=" * 90)
    print("### %s" % path)
    if data is None:
        print("  读不到")
        return
    import UnityPy
    env = UnityPy.load(data)
    names = {}
    for obj in env.objects:
        if obj.type.name == "GameObject":
            try:
                names[obj.path_id] = obj.read().m_Name
            except Exception:
                pass
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            tt = obj.read_typetree()
        except Exception as e:
            continue
        script = tt.get("m_Script", {})
        cls = ""
        try:
            cls = obj.read().m_Script.read().m_ClassName
        except Exception:
            pass
        if not any(k in cls for k in ("Tinter", "Collector", "GrabPass", "Trail", "Proxy", "Follow")):
            continue
        go = names.get(tt.get("m_GameObject", {}).get("m_PathID"))
        if want and want.lower() not in (str(go) + cls).lower():
            continue
        print("-" * 90)
        print("物体=%s  脚本=%s" % (go, cls))
        walk(tt, 1)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
    else:
        run(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
