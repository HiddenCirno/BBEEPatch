# -*- coding: utf-8 -*-
"""确认 DeltaTimeAndScale 的三个访问器到底碰哪个偏移 ——
SetRealTimeAndScale 会不会写 m_DeltaTime(0x10)？

如果 SetRealTimeAndScale 只写 0x0 和 0x8，而原生代码直接读 0x10 的话，
我们在托管侧"看起来改成功了"，原生却一点没变 —— 这正是"改了没反应"的典型成因。
"""
import struct, io, os, sys
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
DLL = os.path.join(BASE, "..", "GameAssembly.dll")
OUT = os.path.join(BASE, "tools", "_dtstruct_out.txt")

TARGETS = [
    ("get_RealDeltaTime",      0x18073F8C0),
    ("get_TimeScale",          0x1805D4390),
    ("get_DeltaTime",          0x1805A57E0),
    ("SetRealTimeAndScale",    0x181BF5430),
]

# ---- PE: 找 .text 段, VA -> 文件偏移 ----
d = open(DLL, "rb").read()
pe = struct.unpack_from("<I", d, 0x3C)[0]
assert d[pe:pe + 4] == b"PE\0\0", "不是 PE"
nsec = struct.unpack_from("<H", d, pe + 6)[0]
optsz = struct.unpack_from("<H", d, pe + 20)[0]
IMGBASE = struct.unpack_from("<Q", d, pe + 24 + 24)[0]   # ⚠ 段头的 VA 是 RVA, 要减 ImageBase
sec = pe + 24 + optsz
secs = []
for i in range(nsec):
    o = sec + i * 40
    name = d[o:o + 8].rstrip(b"\0").decode("latin1")
    vsz, va, rsz, raw = struct.unpack_from("<IIII", d, o + 8)
    secs.append((name, va, vsz, raw, rsz))


def off(va):
    r = va - IMGBASE
    for name, sva, vsz, raw, rsz in secs:
        if sva <= r < sva + max(vsz, rsz):
            return raw + (r - sva)
    return None


md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = False

L = []
for name, va in TARGETS:
    o = off(va)
    L.append(f"\n===== {name} @ {va:#x}  (file 0x{o:x}) =====")
    if o is None:
        L.append("  !! 无法定位")
        continue
    code = d[o:o + 160]
    for ins in md.disasm(code, va):
        L.append(f"  {ins.address:#012x}  {ins.mnemonic:<8} {ins.op_str}")
        if ins.mnemonic == "ret":
            break

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("written", OUT)
