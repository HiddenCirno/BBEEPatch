#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""在 GameAssembly.dll 中查找对指定 VA 的 rip-relative 引用, 并映射回方法名。"""
import os
import re
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
BASE = os.path.normpath(os.path.join(HERE, ".."))
GAME = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect"
DLL = os.path.join(GAME, "GameAssembly.dll")
DUMP = os.path.join(BASE, "dump", "dump.cs")


def parse_pe(path):
    d = open(path, "rb").read()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    assert d[pe:pe + 4] == b"PE\0\0", "not PE"
    magic = struct.unpack_from("<H", d, pe + 0x18)[0]
    machine, nsec = struct.unpack_from("<HH", d, pe + 4)
    optsz = struct.unpack_from("<H", d, pe + 0x14)[0]
    if magic == 0x20B:
        imgbase = struct.unpack_from("<Q", d, pe + 0x18 + 0x18)[0]
    else:
        imgbase = struct.unpack_from("<I", d, pe + 0x18 + 0x1C)[0]
    secs = []
    so = pe + 0x18 + optsz
    for i in range(nsec):
        o = so + i * 40
        name = d[o:o + 8].rstrip(b"\0").decode(errors="replace")
        vsz, va, rsz, ptr = struct.unpack_from("<IIII", d, o + 8)
        secs.append((name, va, vsz, ptr, rsz))
    return d, imgbase, secs, magic


def scan_target(data, imgbase, secs, target_va):
    """返回所有 disp32 使 (next_insn_va + disp) == target_va 的文件偏移。"""
    hits = []
    arr = np.frombuffer(data, dtype=np.uint8)
    for name, va, vsz, ptr, rsz in secs:
        if name.lower() not in (".text", "il2cpp"):
            continue
        end = min(ptr + rsz, len(data) - 4)
        seg = arr[ptr:end]
        disp = seg.view("<i4") if seg.flags["C_CONTIGUOUS"] else None
        if disp is None:
            continue
        # disp[i] 位于文件偏移 ptr+4i, 指令结束于 ptr+4i+4
        idx = np.arange(len(disp), dtype=np.int64)
        rva_end = va + 4 * idx + 4
        tgt = imgbase + rva_end + disp.astype(np.int64)
        m = np.nonzero(tgt == target_va)[0]
        for i in m:
            hits.append(ptr + int(i) * 4)
    return hits


_RA = None


def load_rvas():
    global _RA
    if _RA is not None:
        return _RA
    rx = re.compile(rb"// RVA: 0x([0-9A-Fa-f]+) Offset: 0x[0-9A-Fa-f]+ VA: 0x([0-9A-Fa-f]+)")
    out = []
    with open(DUMP, "rb") as f:
        for line in f:
            m = rx.search(line)
            if m:
                out.append((int(m.group(1), 16), line.decode("utf-8", "replace").strip()))
    out.sort()
    _RA = out
    return out


def method_for(rva):
    import bisect
    ra = load_rvas()
    keys = [x[0] for x in ra]
    i = bisect.bisect_right(keys, rva) - 1
    if i < 0:
        return None, 0
    return ra[i][1], rva - ra[i][0]


if __name__ == "__main__":
    data, imgbase, secs, magic = parse_pe(DLL)
    print(f"PE magic=0x{magic:x} ImageBase=0x{imgbase:x}")
    for s in secs:
        print("   ", s)
    for arg in sys.argv[1:]:
        tva = int(arg, 16)
        for base_cand in (tva, imgbase + tva, tva - imgbase if tva > imgbase else tva):
            hits = scan_target(data, imgbase, secs, base_cand)
            if hits:
                print(f"\n### target=0x{base_cand:x} -> {len(hits)} 处引用")
                for h in hits[:40]:
                    # 反推 RVA
                    rva = None
                    for name, va, vsz, ptr, rsz in secs:
                        if ptr <= h < ptr + rsz:
                            rva = h - ptr + va
                    name, off = method_for(rva) if rva else (None, 0)
                    print(f"   file=0x{h:x} RVA=0x{rva:x} (+{off}) {name}")
                break
