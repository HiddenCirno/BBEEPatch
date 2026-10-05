# -*- coding: utf-8 -*-
"""把跟 LookRotation / 朝向有关的源码注释原样导出（控制台是 GBK，必须写文件再读）。"""
import io, os, re

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
s = io.open(os.path.join(BASE, "dump", "dump.cs"), encoding="utf-8", errors="replace").read()
OUT = os.path.join(BASE, "tools", "_lookrot2_out.txt")
L = []

L.append("=== 含 LookRotation 的 Tooltip / 注释 ===")
for m in re.finditer(r'\[Tooltip\("[^"]*LookRotation[^"]*"\)\]', s):
    L.append("  " + m.group(0))

L.append("\n=== 含 LookRotation 的所有字符串 ===")
seen = set()
for m in re.finditer(r'"[^"]{0,260}LookRotation[^"]{0,260}"', s):
    t = m.group(0)
    if t not in seen:
        seen.add(t)
        L.append("  " + t)

# 那个对齐组件是哪个类
i = s.find('public Vector3 worldUp;')
if i > 0:
    j = s.rfind("public class ", 0, i)
    L.append("\n=== 该组件所属类 ===")
    L.append(s[j:j + 400])

L.append("\n=== QuaternionFp ===")
i = s.find("public struct QuaternionFp")
L.append(s[i:i + 1200])

L.append("\n=== ActorVisualBase / ActorRender 相关方法 ===")
for name in ["public class ActorVisualBase", "public class ActorVisual", "public class ActorRender"]:
    i = s.find(name)
    if i < 0:
        continue
    L.append(f"\n--- {name} ---")
    seg = s[i:i + 3000]
    for l in seg.split("\n"):
        t = l.strip()
        if "// 0x" in t or ("(" in t and "// RVA" not in t and ("public" in t or "internal" in t)):
            L.append("  " + t[:150])

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done")
