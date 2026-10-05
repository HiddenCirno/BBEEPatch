# -*- coding: utf-8 -*-
"""通用: 从 merge.json 索引导出某个 bundle 并解开其中的 MonoBehaviour 原始字节。

用法: python _grab_logic.py actor/logicdata/es.ab
"""
import io, os, json, re, sys

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
GAME = os.path.join(BASE, "..")
AB = os.path.join(GAME, "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")
EX = os.path.join(BASE, "extracted")

import UnityPy

path = sys.argv[1] if len(sys.argv) > 1 else "actor/logicdata/es.ab"
tag = os.path.basename(path).replace(".ab", "")

idx = json.load(open(os.path.join(AB, "merge.json"), encoding="utf-8"))
e = next((x for x in idx if x["r"].lower() == path.lower()), None)
if not e:
    print("找不到", path)
    sys.exit(1)

def bundle_size(buf):
    """UnityFS 头: magic(8) ver(4) unityVer(str) unityRev(str) size(i64)。
       按真实长度切, 否则会读到下一个 bundle 里把 UnityPy 读崩(实测 segfault)。"""
    i = buf.index(b"\x00", 0) + 1          # "UnityFS\0"
    i += 4                                  # version
    for _ in range(2):                      # unityVersion / unityRevision
        j = buf.index(b"\x00", i) + 1
        i = j
    import struct as _s
    return _s.unpack_from(">q", buf, i)[0] if False else _s.unpack_from("<q", buf, i)[0]


with open(os.path.join(AB, e["m"]), "rb") as f:
    f.seek(e["s"])
    head = f.read(4096)
    sz = bundle_size(head)
    if not (0 < sz < 200 * 1024 * 1024):
        sz = 400000
    f.seek(e["s"])
    data = f.read(sz)

tmp = os.path.join(EX, "_tmp_" + tag + ".ab")
open(tmp, "wb").write(data)
print(f"{path}  {e['m']} @{e['s']}  -> {tmp}")

env = UnityPy.load(tmp)
n = 0
for obj in env.objects:
    if obj.type.name == "MonoScript":
        d = obj.read()
        print(f"  MonoScript class={getattr(d,'m_ClassName','?')} ns={getattr(d,'m_Namespace','?')}")
    if obj.type.name == "MonoBehaviour":
        # 不调 obj.read(): es.ab 的 MonoBehaviour 让 UnityPy 的类型解析段错误,
        # 我们只需要原始字节(里面全是明文字符串), get_raw_data 就够。
        d = type("X", (), {"m_Name": "?"})()
        raw = obj.get_raw_data()
        fn = os.path.join(EX, f"{tag}_mono{n}.raw")
        open(fn, "wb").write(raw)
        print(f"  MonoBehaviour raw={len(raw)} -> {fn}")
        n += 1
        # 找 bullet 生成指令
        for m in re.finditer(rb"bullet_id\s*:\s*\d+", raw):
            i = m.start()
            s = max(0, i - 200); ee = min(len(raw), i + 300)
            t = raw[s:ee].decode("utf-8", "replace")
            t = "".join(c if c.isprintable() else "|" for c in t)
            print(f"\n  ### @{i}\n  {t}\n")

os.remove(tmp)
