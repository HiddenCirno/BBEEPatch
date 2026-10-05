#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""按 RVA 反汇编 GameAssembly.dll 中的函数 (capstone)。"""
import struct
import sys

import capstone

GAME = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect"
DLL = GAME + r"\GameAssembly.dll"
IMG = 0x180000000

SECS = [(".text", 4096, 5662656, 1024, 5662720),
        ("il2cpp", 5668864, 65737820, 5663744, 65738240),
        (".rdata", 71409664, 13893636, 71401984, 13894144),
        (".data", 85307392, 7774300, 85296128, 4844544),
        (".pdata", 93085696, 3309876, 90140672, 3310080),
        ("_RDATA", 96399360, 348, 93450752, 512),
        (".reloc", 96403456, 1446488, 93451264, 1446912)]


def rva2off(rva):
    for n, va, vsz, ptr, rsz in SECS:
        if va <= rva < va + max(vsz, rsz):
            return ptr + (rva - va)
    return None


def off2rva(off):
    for n, va, vsz, ptr, rsz in SECS:
        if ptr <= off < ptr + rsz:
            return va + (off - ptr)
    return None


def disasm(rva, count=120, base_img=IMG):
    data = open(DLL, "rb").read()
    off = rva2off(rva)
    if off is None:
        print(f"RVA 0x{rva:x} 不在任何节中")
        return
    code = data[off:off + count * 15]
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    md.detail = True
    va = base_img + rva
    for i, ins in enumerate(md.disasm(code, va)):
        if i >= count:
            break
        ann = ""
        for op in ins.operands:
            if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP:
                tgt = ins.address + ins.size + op.mem.disp
                ann += f"  ; -> 0x{tgt:x}"
                tn = off2rva(tgt - base_img)
                if tn is not None:
                    raw = data[tn:tn + 48]
                    ann += f" [{raw[:40]!r}]"
        print(f"0x{ins.address:x}: {ins.mnemonic:<8} {ins.op_str}{ann}")


if __name__ == "__main__":
    for a in sys.argv[1:]:
        print(f"\n===== RVA 0x{int(a,16):x} =====")
        disasm(int(a, 16), int(sys.argv[0] and 90))
