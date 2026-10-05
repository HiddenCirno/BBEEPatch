# -*- coding: utf-8 -*-
"""IL2CPP 反汇编 + 调用点标注。

不再猜"哪个入口会被调到" —— 直接把方法体拆出来看它调了谁、写了哪个偏移。
dump.cs 里有每个方法的 RVA，GameAssembly.dll 里有机器码，两下一对就能读。

用法:
    python _disasm.py <方法名关键字> [...]        反汇编这些方法（RVA->名字标注 call）
    python _disasm.py --callers <方法名关键字>     反过来找"谁调用了它"
    python _disasm.py --at 0x1aed640              直接反汇编一个 RVA（尾调用 / 匿名 sub）
                                                  可用 --name 指定标题
"""
import struct, os, io, re, sys
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
DLL = os.path.join(BASE, "..", "GameAssembly.dll")
DUMP = os.path.join(BASE, "dump", "dump.cs")
OUT = os.path.join(BASE, "tools", "_disasm_out.txt")
IMG = 0x180000000

# ---------------------------------------------------------------- PE
d = open(DLL, "rb").read()
pe = struct.unpack_from("<I", d, 0x3C)[0]
nsec = struct.unpack_from("<H", d, pe + 6)[0]
optsz = struct.unpack_from("<H", d, pe + 20)[0]
IMGBASE = struct.unpack_from("<Q", d, pe + 24 + 24)[0]
sec = pe + 24 + optsz
SECS = []
for i in range(nsec):
    o = sec + i * 40
    nm = d[o:o + 8].rstrip(b"\0").decode("latin1")
    vsz, va, rsz, raw = struct.unpack_from("<IIII", d, o + 8)
    SECS.append((nm, va, vsz, raw, rsz))


def rva2off(rva):
    for nm, sva, vsz, raw, rsz in SECS:
        if sva <= rva < sva + max(vsz, rsz):
            return raw + (rva - sva)
    return None


# ---------------------------------------------------------------- dump.cs: RVA -> 名字
txt = io.open(DUMP, encoding="utf-8", errors="replace").read().split("\n")
RVA2NAME = {}
NAME2RVA = {}
cur_class = "?"
pending = None
for line in txt:
    s = line.strip()
    if s.startswith("public ") and (" class " in s or " struct " in s or " enum " in s):
        m = re.match(r'public\s+(?:sealed\s+|abstract\s+|static\s+)*\w+\s+([\w.<>`]+)', s)
        if m:
            cur_class = m.group(1)
    if s.startswith("// RVA:"):
        m = re.match(r'// RVA: (0x[0-9A-Fa-f]+)', s)
        pending = int(m.group(1), 16) if m else None
        continue
    if pending is not None:
        m = re.match(r'^(?:public|private|internal|protected|virtual|static|\s)*[\w.<>\[\],\s]+?\s([\w.<>`]+)\s*\(', s)
        if m and '(' in s and ';' not in s:
            name = f"{cur_class}::{m.group(1)}"
            RVA2NAME.setdefault(pending, name)
            NAME2RVA.setdefault(m.group(1), []).append((pending, name))
        pending = None

md = Cs(CS_ARCH_X86, CS_MODE_64)

CALL_OPS = ("call",)


def decode(rva, max_ins=4000, stop_at_ret=True):
    o = rva2off(rva)
    if o is None:
        return []
    code = d[o:o + max_ins * 8]
    out = []
    for ins in md.disasm(code, IMG + rva):
        out.append(ins)
        if ins.mnemonic == "ret":
            break
        if ins.mnemonic == "int3":
            break
        if len(out) > max_ins:
            break
    return out


def label(tgt):
    r = tgt - IMG
    n = RVA2NAME.get(r)
    return n if n else f"sub_{r:X}"


def disasm(rva, title):
    L = [f"\n{'='*78}", f"=== {title}   RVA {rva:#x}", "="*78]
    ins = decode(rva)
    if not ins:
        L.append("  <无法解码>")
        return L
    for i in ins:
        s = f"  {i.address:#012x}  {i.mnemonic:<8} {i.op_str}"
        if i.mnemonic in CALL_OPS and i.op_str.startswith("0x"):
            try:
                tgt = int(i.op_str, 16)
                s += f"      ; -> {label(tgt)}"
            except Exception:
                pass
        L.append(s)
    return L


def callers_of(rva):
    hits = []
    for nm, sva, vsz, raw, rsz in SECS:
        if nm not in (".text", "il2cpp"):
            continue
        blob = d[raw:raw + rsz]
        n = len(blob)
        for i in range(n - 5):
            if blob[i] == 0xE8:
                rel = struct.unpack_from("<i", blob, i + 1)[0]
                if sva + i + 5 + rel == rva:
                    hits.append(sva + i)
    return hits


mode = sys.argv[1] if len(sys.argv) > 1 else ""
keys = [a for a in sys.argv[1:] if not a.startswith("--")]

L = []
if mode == "--at":
    title = "?"
    if "--name" in sys.argv:
        title = sys.argv[sys.argv.index("--name") + 1]
        keys = [k for k in keys if k != title]
    for k in keys:
        rva = int(k, 16) if k.lower().startswith("0x") else int(k)
        t = title if title != "?" else label(IMG + rva)
        L += disasm(rva, t)
elif mode == "--callers":
    for k in keys:
        for rva, name in NAME2RVA.get(k, []):
            L.append(f"\n### {name}  RVA {rva:#x}  调用点 {len(callers_of(rva))} 处")
            for c in callers_of(rva)[:40]:
                # 找命中点所属的最近方法
                best = None
                for r in RVA2NAME:
                    if r <= c and (best is None or r > best):
                        best = r
                who = RVA2NAME.get(best, "?")
                L.append(f"    call @ {c:#010x}  <- {who} (+{c - best:#x})")
else:
    for k in keys:
        cands = NAME2RVA.get(k, [])
        if not cands:
            L.append(f"\n### {k}: 在 dump.cs 里找不到这个方法的 RVA")
            continue
        for rva, name in cands:
            L += disasm(rva, name)

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("\n".join(L[:120]))
print(f"\n... 完整输出: {OUT}")
