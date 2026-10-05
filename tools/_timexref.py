# -*- coding: utf-8 -*-
"""谁在推进 ActionMgr.Time？

背景：实测
    Lever=Dt   (改 ActionMgr.Update 的 dt)  -> 动作时长**不变**
    Lever=Time (直接写 ActionMgr.Time)      -> 动作时长变成 1/3，有效
    Lever=None (只加速动画)                 -> 动作时长**不变**
三组数据合起来说明：ActionMgr.Time 确实是动作时钟，但推进它的既不是
ActionMgr.Update 的 dt，也不是动画 —— 是**第三个地方**。

那就别猜了，直接扫全二进制找 `call ActionMgr::set_Time` (E8 rel32)，
把调用者的 RVA 列出来，再回到 dump.cs 反查是哪个函数。
"""
import struct, os, io, re
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
GAME = os.path.join(BASE, "..")
DLL = os.path.join(GAME, "GameAssembly.dll")
DUMP = os.path.join(BASE, "dump", "dump.cs")
OUT = os.path.join(BASE, "tools", "_timexref_out.txt")

TARGET = 0x59F3B0          # ActionMgr::set_Time(RVA)
IMG = 0x180000000

d = open(DLL, "rb").read()
pe = struct.unpack_from("<I", d, 0x3C)[0]
nsec = struct.unpack_from("<H", d, pe + 6)[0]
optsz = struct.unpack_from("<H", d, pe + 20)[0]
sec = pe + 24 + optsz
secs = []
for i in range(nsec):
    o = sec + i * 40
    nm = d[o:o + 8].rstrip(b"\0").decode("latin1")
    vsz, va, rsz, raw = struct.unpack_from("<IIII", d, o + 8)
    secs.append((nm, va, vsz, raw, rsz))


def code_sections():
    """只扫代码段。il2cpp 段装的是 AOT 编译出来的方法体。"""
    for nm, va, vsz, raw, rsz in secs:
        if nm in (".text", "il2cpp"):
            yield d[raw:raw + rsz], va


callers = []
for blob, base_va in code_sections():
    n = len(blob)
    i = 0
    while i < n - 5:
        if blob[i] == 0xE8:
            rel = struct.unpack_from("<i", blob, i + 1)[0]
            # call 指令的下一条指令地址 = base_va + i + 5
            tgt = base_va + i + 5 + rel
            if tgt == TARGET:
                callers.append(base_va + i)
        i += 1

# ---- 回到 dump.cs 反查：这个 RVA 属于哪个方法 ----
txt = io.open(DUMP, encoding="utf-8", errors="replace").read()
# 建 RVA -> "类::方法" 表
ent = {}
cur_class = "?"
for line in txt.split("\n"):
    if line.startswith("public ") and (" class " in line or " struct " in line):
        m = re.match(r'public\s+(?:sealed\s+|abstract\s+|static\s+)*\w+\s+(\w+)', line)
        if m:
            cur_class = m.group(1)
    m = re.match(r'\s*// RVA: (0x[0-9A-Fa-f]+)', line)
    if m:
        rva = int(m.group(1), 16)
        ent.setdefault(rva, cur_class)

L = [f"扫描目标 ActionMgr::set_Time @ RVA {TARGET:#x}", f"找到 {len(callers)} 处 call", ""]
for c in callers:
    owner = ent.get(c)
    if owner is None:
        # 往上找最近的 RVA 标注
        best = None
        for r in ent:
            if r <= c and (best is None or r > best):
                best = r
        owner = f"{ent[best]} (+{c - best:#x})" if best is not None else "?"
    L.append(f"  call @ RVA {c:#010x}   <- {owner}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("\n".join(L[:60]))
print("...")
print("written", OUT)
