# -*- coding: utf-8 -*-
"""
每份 dump 一份摘要:
  · 异常码 / 异常地址落在哪个模块
  · 崩溃线程栈上所有 GameAssembly.dll 帧 -> **方法名**(这是唯一能看出"崩在哪条业务链"的线索)
    (低于方法表最小 RVA 的是 il2cpp 运行时助手, 显示为 <il2cpp 运行时>)

用途一目了然: **把历史 dump 排成一列比**。
  同一个崩溃签名 + 同一批方法名 ⇒ 老毛病(和本次改动无关);
  方法名变了 ⇒ 才是本次改动引入的。

用法: _dumpresolve.py [dmp 路径...]   (不给参数 = 扫 CrashDumps 全部)
"""
import os, sys, glob, struct, bisect, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _mindump import load, streams, modules, resolve
from _rvawho import load as load_rva

RULE = '-' * 78


def frames(buf, st, mods, etid):
    rva = st[3][0][1]
    n = struct.unpack_from('<I', buf, rva)[0]
    for i in range(n):
        b = rva + 4 + i * 48
        if struct.unpack_from('<I', buf, b)[0] != etid:
            continue
        start = struct.unpack_from('<Q', buf, b + 24)[0]
        size, srva = struct.unpack_from('<II', buf, b + 32)
        out = []
        for k in range(0, size, 8):
            v = struct.unpack_from('<Q', buf, srva + k)[0]
            r = resolve(mods, v)
            if r.startswith('GameAssembly.dll+'):
                out.append((k, int(r.split('+')[1], 16)))
        return out
    return []


def main():
    args = sys.argv[1:]
    if not args:
        d = os.path.join(os.environ['LOCALAPPDATA'], 'CrashDumps')
        args = sorted(glob.glob(os.path.join(d, 'BlazblueEntropyEffect.exe.*.dmp')),
                      key=os.path.getmtime)
    table = load_rva()
    rvas = [e[0] for e in table]
    for p in args:
        buf = load(p)
        st = streams(buf)
        mods = modules(buf, st)
        if 6 not in st:
            print('%s: 没有异常流' % os.path.basename(p)); continue
        rva = st[6][0][1]
        code = struct.unpack_from('<I', buf, rva + 8)[0]
        addr = struct.unpack_from('<Q', buf, rva + 24)[0]
        etid = struct.unpack_from('<I', buf, rva)[0]
        print(RULE)
        print('%s  (%s)' % (os.path.basename(p), time.strftime('%m-%d %H:%M', time.localtime(os.path.getmtime(p)))))
        print('  异常 0x%08X @ %s' % (code, resolve(mods, addr)))
        for k, ga in frames(buf, st, mods, etid):
            i = bisect.bisect_right(rvas, ga) - 1
            if i < 0:
                name = '<il2cpp 运行时助手>'
            else:
                name = table[i][1][:90]
            print('    [+0x%05X] GameAssembly+0x%-8X %s' % (k, ga, name))


if __name__ == '__main__':
    main()
