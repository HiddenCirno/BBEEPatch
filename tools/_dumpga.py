# -*- coding: utf-8 -*-
"""只看崩溃线程栈里的 GameAssembly.dll 帧 —— 这些是 il2cpp 的原生调用点, 是唯一能定位"哪个补丁"的线索。

把偏移换算成 RVA(减 ImageBase 0x180000000)后, 可以拿去和 dump.cs 里的方法 RVA 对照。
"""
import os, sys, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _mindump import load, streams, modules, resolve

IMAGE_BASE = 0x180000000


def main():
    path = sys.argv[1]
    buf = load(path)
    st = streams(buf)
    mods = modules(buf, st)
    etid = struct.unpack_from('<I', buf, st[6][0][1])[0]
    rva = st[3][0][1]
    n = struct.unpack_from('<I', buf, rva)[0]
    for i in range(n):
        b = rva + 4 + i * 48
        tid = struct.unpack_from('<I', buf, b)[0]
        if tid != etid:
            continue
        start = struct.unpack_from('<Q', buf, b + 24)[0]
        size, srva = struct.unpack_from('<II', buf, b + 32)
        print('崩溃线程 0x%X, 栈 0x%X+0x%X —— GameAssembly.dll 帧(按栈深度):' % (tid, start, size))
        for k in range(0, size, 8):
            v = struct.unpack_from('<Q', buf, srva + k)[0]
            r = resolve(mods, v)
            if r.startswith('GameAssembly'):
                print('    [+0x%05X] %s   RVA 0x%X' % (k, r, v - IMAGE_BASE))
        print()
        print('  —— 同一线程上 coreclr/UnityPlayer 帧 ——')
        for k in range(0, size, 8):
            v = struct.unpack_from('<Q', buf, srva + k)[0]
            r = resolve(mods, v)
            if r.startswith('coreclr') or r.startswith('UnityPlayer'):
                print('    [+0x%05X] %s' % (k, r))


if __name__ == '__main__':
    main()
