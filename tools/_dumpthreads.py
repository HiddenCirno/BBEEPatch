# -*- coding: utf-8 -*-
"""
把 dump 里**所有线程**的栈都扫一遍, 只打印落在模块里的地址。

为什么要全扫:
  崩溃线程上"没有任何 GameAssembly.dll 的帧"是一个极强的线索 ——
    · 有 GameAssembly 帧 ⇒ 崩在 il2cpp 调过来的某个补丁里(我们的 C#);
    · 没有                    ⇒ 崩在 BepInEx/CLR 自己的调用链(UnityPlayer 直接进来)里。
  而且"谁在跑"这件事, 另一条线程的栈往往比崩溃现场更能说明问题。
"""
import os, sys, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _mindump import load, streams, modules, resolve


def threads(buf, st):
    rva = st[3][0][1]
    n = struct.unpack_from('<I', buf, rva)[0]
    out = []
    for i in range(n):
        b = rva + 4 + i * 48
        tid = struct.unpack_from('<I', buf, b)[0]
        start = struct.unpack_from('<Q', buf, b + 24)[0]
        size, srva = struct.unpack_from('<II', buf, b + 32)
        out.append((tid, start, size, srva))
    return out


def main():
    path = sys.argv[1]
    only = sys.argv[2].lower() if len(sys.argv) > 2 else None
    buf = load(path)
    st = streams(buf)
    mods = modules(buf, st)
    etid = struct.unpack_from('<I', buf, st[6][0][1])[0] if 6 in st else -1

    for tid, start, size, srva in threads(buf, st):
        if only and only not in ('all',) and only not in ('%x' % tid):
            continue
        if size == 0 or srva == 0 or srva + size > len(buf):
            print('线程 0x%-6X <无栈数据>' % tid); continue
        hits = []
        for k in range(0, size, 8):
            v = struct.unpack_from('<Q', buf, srva + k)[0]
            if v == 0:
                continue
            r = resolve(mods, v)
            if not r.startswith('<未知'):
                hits.append((k, v, r))
        # 兴趣点: 有没有 GameAssembly / 有没有 Unity 主线程标志
        gasm = [h for h in hits if h[2].startswith('GameAssembly')]
        print('\n线程 0x%-6X%s  栈 0x%X+0x%X  模块命中 %d  GameAssembly 命中 %d'
              % (tid, '  <<< 崩溃线程' if tid == etid else '', start, size, len(hits), len(gasm)))
        for k, v, r in hits[:24]:
            print('    [+0x%05X] %s' % (k, r))


if __name__ == '__main__':
    main()
