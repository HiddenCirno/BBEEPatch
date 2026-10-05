# -*- coding: utf-8 -*-
"""1) 打印 GamePlay.ActionLogicGroup 的字段布局
   2) 按 "esbullet" 的出现位置重新切分动作块（跟着的下一根串就是动作名）
"""
import io, os, re

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
OUT = os.path.join(BASE, "tools", "_alg_layout_out.txt")
L = []

s = io.open(os.path.join(BASE, "dump", "dump.cs"), encoding="utf-8", errors="replace").read()


def cls(name, n=4000):
    i = s.find("public class " + name)
    if i < 0:
        return
    seg = s[i:i + n]
    L.append(f"\n===== {name} =====")
    for l in seg.split("\n"):
        t = l.strip()
        if "// 0x" in t or ("(" in t and "// RVA" not in t and ("public" in t or "internal" in t)):
            L.append("  " + t[:150])


cls("ActionLogicGroup")
cls("ActionLogic", 3000)

raw = open(os.path.join(BASE, "extracted", "esbullet_mono.raw"), "rb").read()
occ = [m.start() for m in re.finditer(rb"esbullet", raw)]
L.append(f"\n\n=== esbullet 出现 {len(occ)} 次 ===")

for idx, p in enumerate(occ):
    # 后面紧跟的第一根可打印串
    m = re.compile(rb"[\x20-\x7e]{2,}").search(raw, p + len("esbullet"))
    nm = m.group(0).decode("ascii") if m else "?"
    nxt = occ[idx + 1] if idx + 1 < len(occ) else min(len(raw), p + 4000)
    seg = raw[p:nxt]
    strs = [x.decode("ascii") for x in re.findall(rb"[\x20-\x7e]{2,}", seg)]
    ours = [x for x in strs if any(k in x for k in
            ("action:", "bullet_id:", "buff:", "trigger:", "tag:", "cd:", "ChangeAction", "CreateBullet", "SetActionCD"))]
    fx = [x for x in strs if x.startswith("Role/") or x.startswith("Hit/")]
    L.append(f"\n--- #{idx} @{p}  后续串[0..3]={strs[:4]}")
    for h in ours:
        L.append("     指令 " + h[:200])
    if fx:
        L.append("     资源 " + " | ".join(dict.fromkeys(fx))[:200])

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done")
