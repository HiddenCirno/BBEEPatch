# -*- coding: utf-8 -*-
"""esbullet.ab 里是一个 MonoBehaviour(ScriptableObject)。UnityPy 不认识它的类型,
所以直接看 MonoScript 的类名 + MonoBehaviour 的原始字节。
"""
import io, os, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
EX = os.path.join(BASE, "extracted")
OUT = os.path.join(BASE, "tools", "_esbullet_raw_out.txt")

import UnityPy

L = []
env = UnityPy.load(os.path.join(EX, "esbullet.ab"))
for obj in env.objects:
    if obj.type.name == "MonoScript":
        d = obj.read()
        L.append(f"MonoScript: class={getattr(d,'m_ClassName','?')} ns={getattr(d,'m_Namespace','?')} "
                 f"asm={getattr(d,'m_AssemblyName','?')}")
    if obj.type.name == "MonoBehaviour":
        d = obj.read()
        L.append(f"MonoBehaviour name={getattr(d,'m_Name','?')}")
        raw = obj.get_raw_data()
        L.append(f"raw len={len(raw)}")
        open(os.path.join(EX, "esbullet_mono.raw"), "wb").write(raw)
        # 头 16 字节通常是 m_GameObject PPtr 等
        L.append("head: " + raw[:64].hex())
        # 找可打印字符串
        import re
        strs = re.findall(rb"[\x20-\x7e]{4,}", raw)
        L.append(f"\n=== 可打印串 ({len(strs)}) ===")
        for s in strs[:200]:
            L.append("  " + s.decode("ascii"))

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done")
