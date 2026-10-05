# -*- coding: utf-8 -*-
"""
批量扫 %LOCALAPPDATA%\\CrashDumps 里这个游戏的所有 dump, 只打印**异常码 + 异常地址**。

为什么需要: 一次崩溃到底是什么引起的, 光看最新那份 dump 是**证明不了**的 ——
  如果历史 dump 崩在**同一个偏移**上, 那这个崩法就是老毛病(和本次改动无关);
  只有"这次的偏移是新的", 才能把责任落到本次改动上。
"""
import os, sys, struct, glob
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _mindump import load, streams, modules, resolve

d = os.path.join(os.environ['LOCALAPPDATA'], 'CrashDumps')
files = sorted(glob.glob(os.path.join(d, 'BlazblueEntropyEffect.exe.*.dmp')),
               key=os.path.getmtime)
for p in files:
    try:
        buf = load(p)
        st = streams(buf)
        if 6 not in st:
            print('%-34s 没有异常流' % os.path.basename(p)); continue
        rva = st[6][0][1]
        code = struct.unpack_from('<I', buf, rva + 8)[0]
        addr = struct.unpack_from('<Q', buf, rva + 24)[0]
        mods = modules(buf, st)
        import time
        ts = time.strftime('%m-%d %H:%M', time.localtime(os.path.getmtime(p)))
        print('%-16s %-20s 0x%08X  %s' % (ts, os.path.basename(p)[24:29], code, resolve(mods, addr)))
    except Exception as e:
        print('%-34s 解析失败: %s' % (os.path.basename(p), e))
