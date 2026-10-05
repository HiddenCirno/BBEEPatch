#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""扫描 GameAssembly.dll 中调用指定 RVA 的所有 call 指令 (E8 rel32)。"""
import bisect
import re
import struct
import sys

import numpy as np

GAME = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect"
DLL = GAME + r"\GameAssembly.dll"
DUMP = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\dump\dump.cs"

SECS = [(".text", 4096, 5662656, 1024, 5662720),
        ("il2cpp", 5668864, 65737820, 5663744, 65738240)]

_rx = re.compile(rb"// RVA: 0x([0-9A-Fa-f]+) Offset: 0x[0-9A-Fa-f]+ VA: 0x([0-9A-Fa-f]+)")
_RA = None


def load_rvas():
    global _RA
    if _RA is None:
        out = []
        with open(DUMP, "rb") as f:
            for line in f:
                m = _rx.search(line)
                if m:
                    out.append((int(m.group(1), 16), line.decode("utf-8", "replace").strip()))
        out.sort()
        _RA = out
    return _RA


def owner(rva):
    ra = load_rvas()
    keys = [x[0] for x in ra]
    i = bisect.bisect_right(keys, rva) - 1
    return (ra[i][1], rva - ra[i][0]) if i >= 0 else (None, 0)


def callers(target_rva):
    data = open(DLL, "rb").read()
    arr = np.frombuffer(data, dtype=np.uint8)
    res = []
    for name, va, vsz, ptr, rsz in SECS:
        end = min(ptr + rsz, len(data))
        seg = arr[ptr:end]
        e8 = np.nonzero(seg == 0xE8)[0]
        e8 = e8[e8 + 5 <= len(seg)]
        if not len(e8):
            continue
        b0 = seg[e8 + 1].astype(np.int64)
        b1 = seg[e8 + 2].astype(np.int64)
        b2 = seg[e8 + 3].astype(np.int64)
        b3 = seg[e8 + 4].astype(np.int64)
        disp = b0 | (b1 << 8) | (b2 << 16) | (b3 << 24)
        disp = (disp ^ 0x80000000) - 0x80000000  # 符号扩展
        rva = va + e8 + 5
        tgt = rva + disp
        for i in np.nonzero(tgt == target_rva)[0]:
            res.append(int(va + e8[i]))
    return res


if __name__ == "__main__":
    for a in sys.argv[1:]:
        t = int(a, 16)
        cs = callers(t)
        print(f"\n### 调用 RVA 0x{t:x} 的位置: {len(cs)} 处")
        for c in cs[:25]:
            nm, off = owner(c)
            print(f"   caller RVA=0x{c:x} (+{off})  {nm}")
