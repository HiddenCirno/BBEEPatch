# -*- coding: utf-8 -*-
"""
最小 minidump 解析器 —— 只为回答一个问题：**崩在哪。**

Unity IL2CPP + CoreCLR 的组合下, 崩溃汇报通常是"游戏炸了"四个字, 什么也没有。
BepInEx 的日志会在崩溃前**戛然而止**(硬崩, 托管 try/catch 抓不到),
所以只能去 %LOCALAPPDATA%\\CrashDumps 拿 .dmp 自己解。

只实现需要的四个流:
  6  ExceptionStream  —— 异常码 + 异常地址
  3  ThreadListStream —— 所有线程的栈 + 上下文
  4  ModuleListStream —— 基址/大小/名字 (用来把地址解析成 "模块+偏移")
  9  MemoryListStream —— 栈内存 (用来手工回溯 —— 有些 dump 不给你 ThreadContext 能用的栈)

输出:
  · 异常码 / 异常地址 -> 模块+偏移
  · 崩溃线程的寄存器 (Rip/Rsp/Rbp 等)
  · 从 Rsp 起扫栈, 把落在已知模块里的值全部打印出来 —— 这就是回溯
     (不做真正的 unwind, 因为 IL2CPP/CoreCLR 的展开信息我们没有;
      扫栈虽然会掺进数据, 但**顺序 + 成组出现**足够看出一条调用链)

⚠ JIT 出来的托管代码在**匿名内存**里, 不在 coreclr.dll 中 ⇒ 会显示成 <未知模块>。
  这种情况下看**它的返回地址**: 返回地址一定落在 GameAssembly.dll 或 coreclr.dll 里。
"""
import struct, sys, os

MDMP = 0x504D444D


def load(path):
    with open(path, 'rb') as f:
        return f.read()


def streams(buf):
    sig, ver, n, rva = struct.unpack_from('<IIII', buf, 0)
    assert sig == MDMP, '不是 minidump: %08X' % sig
    out = {}
    for i in range(n):
        t, size, srva = struct.unpack_from('<III', buf, rva + i * 12)
        out.setdefault(t, []).append((size, srva))
    return out


def read_string(buf, rva):
    n = struct.unpack_from('<I', buf, rva)[0]
    return buf[rva + 4:rva + 4 + n].decode('utf-16-le', 'replace')


def modules(buf, st):
    if 4 not in st:
        return []
    _, rva = st[4][0]
    n = struct.unpack_from('<I', buf, rva)[0]
    mods = []
    for i in range(n):
        base = rva + 4 + i * 108          # MINIDUMP_MODULE = 108 字节
        b, size = struct.unpack_from('<QI', buf, base)
        name_rva = struct.unpack_from('<I', buf, base + 20)[0]
        try:
            name = read_string(buf, name_rva)
        except Exception:
            name = '?'
        mods.append((b, b + size, os.path.basename(name)))
    return mods


def resolve(mods, addr):
    for lo, hi, name in mods:
        if lo <= addr < hi:
            return '%s+0x%X' % (name, addr - lo)
    return '<未知模块 0x%X>' % addr


def regions(buf, st):
    """
    内存区表 —— 两种编码都得认：
      9  MemoryListStream   : 数量(u32) + {Start u64, DataSize u32, Rva u32} × N
      5  Memory64ListStream : 数量(u64) + BaseRva u64 + {Start u64, Size u64} × N
                              —— 数据从 BaseRva 起**顺序**排布(不像 9 每段带自己的 Rva)
    ⚠ 只认 9 会让"栈明明在 dump 里却扫不出来" —— 28MB 的 dump 一般走的是 5。
    """
    out = []
    for _, rva in st.get(9, []):
        n = struct.unpack_from('<I', buf, rva)[0]
        for i in range(n):
            ent = rva + 4 + i * 16
            start, dsize, drva = struct.unpack_from('<QII', buf, ent)
            out.append((start, dsize, drva))
    for _, rva in st.get(5, []):
        n, base_rva = struct.unpack_from('<QQ', buf, rva)
        cur = base_rva
        for i in range(n):
            start, size = struct.unpack_from('<QQ', buf, rva + 16 + i * 16)
            out.append((start, size, cur))
            cur += size
    return out


def walk_stack(buf, st, mods, sp, stack=None, limit=400):
    """
    从 sp 起把栈上每个 8 字节值都试一遍 —— 落在模块里的就算一个候选返回地址。

    ⚠ 优先用 ThreadListStream 里那条线程自带的 Stack 描述符(见调用方),
       MemoryList 的两种编码在本项目这台机器上**都对不上**(见 _dumpinfo.py 的 size 反推),
       别把时间再花在猜它上面 —— 线程描述符是权威且唯一的。
    """
    cands = []
    if stack and stack[1] > 0:
        cands.append(stack)
    try:
        cands += regions(buf, st)
    except Exception as e:
        print('  (内存区表解析失败, 只扫线程栈: %s)' % e)
    for start, dsize, drva in cands:
        if not (start <= sp < start + dsize):
            continue
        off = sp - start
        print('  --- 栈回溯 (从 Rsp 起扫, 起点 %s, 区 0x%X+0x%X) ---'
              % (resolve(mods, sp), start, dsize))
        for k in range(off, min(dsize, off + limit * 8), 8):
            v = struct.unpack_from('<Q', buf, drva + k)[0]
            if v == 0:
                continue
            r = resolve(mods, v)
            if not r.startswith('<未知'):
                print('    [sp+0x%03X] 0x%X  %s' % (k - off, v, r))
        return
    print('  (没有任何内存区覆盖 Rsp —— 试着传 --stack 或看 ThreadList 的 Stack 描述符)')


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else None
    if not path:
        print('用法: _mindump.py <dmp>'); return
    buf = load(path)
    st = streams(buf)
    mods = modules(buf, st)
    print('模块数: %d' % len(mods))

    exc_addr = None
    if 6 in st:
        _, rva = st[6][0]
        tid = struct.unpack_from('<I', buf, rva)[0]
        code, flags = struct.unpack_from('<II', buf, rva + 8)
        exc_addr = struct.unpack_from('<Q', buf, rva + 24)[0]
        nparam = struct.unpack_from('<I', buf, rva + 32)[0]
        params = [struct.unpack_from('<Q', buf, rva + 40 + i * 8)[0] for i in range(min(nparam, 15))]
        print('\n=== 异常 ===')
        print('  线程      : 0x%X' % tid)
        print('  异常码    : 0x%08X' % code)
        print('  标志      : 0x%08X' % flags)
        print('  异常地址  : 0x%X  -> %s' % (exc_addr, resolve(mods, exc_addr)))
        print('  参数      : %s' % ' '.join('0x%X' % p for p in params))
        # 0xC0000005 = 访问冲突: [0]=读/写(0读8写), [1]=被访问的地址
        if code == 0xC0000005 and len(params) >= 2:
            print('  ⇒ 访问冲突: %s 地址 0x%X %s' %
                  ('写' if params[0] else '读', params[1], resolve(mods, params[1])))

    # 崩溃线程的上下文
    threads = []
    if 3 in st:
        _, rva = st[3][0]
        n = struct.unpack_from('<I', buf, rva)[0]
        for i in range(n):
            base = rva + 4 + i * 48        # MINIDUMP_THREAD = 48 字节
            tid = struct.unpack_from('<I', buf, base)[0]
            # MINIDUMP_THREAD: Tid4 Suspend4 PriorityClass4 Priority4 Teb8(@16)
            #                  Stack{Start8@24, DataSize4@32, Rva4@36}
            #                  ThreadContext{DataSize4@40, Rva4@44}
            stk_start = struct.unpack_from('<Q', buf, base + 24)[0]
            stk_size, stk_rva = struct.unpack_from('<II', buf, base + 32)
            ctx_size, ctx_rva = struct.unpack_from('<II', buf, base + 40)
            threads.append((tid, stk_start, stk_size, stk_rva, ctx_size, ctx_rva))

    target = None
    if 6 in st:
        etid = struct.unpack_from('<I', buf, st[6][0][1])[0]
        for t in threads:
            if t[0] == etid:
                target = t
    if target is None and threads:
        target = threads[0]
    if target is None:
        print('没有线程列表'); return

    tid, stack_start, stk_size, stk_rva, ctx_size, ctx_rva = target
    print('\n=== 崩溃线程 0x%X 的寄存器 ===' % tid)
    if ctx_size >= 0x100 + 8:
        regs = {}
        for name, off in (('Rax', 0x78), ('Rcx', 0x80), ('Rdx', 0x88), ('Rbx', 0x90),
                          ('Rsp', 0x98), ('Rbp', 0xA0), ('Rsi', 0xA8), ('Rdi', 0xB0),
                          ('R8', 0xB8), ('R9', 0xC0), ('R10', 0xC8), ('R11', 0xD0),
                          ('R12', 0xD8), ('R13', 0xE0), ('R14', 0xE8), ('R15', 0xF0),
                          ('Rip', 0xF8)):
            regs[name] = struct.unpack_from('<Q', buf, ctx_rva + off)[0]
        print('  ' + '  '.join('%s=0x%X' % (k, v) for k, v in regs.items() if k not in ('Rip',)))
        print('  Rip=0x%X -> %s' % (regs['Rip'], resolve(mods, regs['Rip'])))
        if exc_addr is not None and regs['Rip'] != exc_addr:
            print('  ⚠ Rip != 异常地址 —— 异常地址才是真凶: %s' % resolve(mods, exc_addr))
        print('  Rsp=0x%X -> %s' % (regs['Rsp'], resolve(mods, regs['Rsp'])))
        sp = regs['Rsp']
    else:
        print('  (没有可用上下文)')
        sp = 0

    print()
    print('  线程栈描述符: 0x%X + 0x%X (rva 0x%X)' % (stack_start, stk_size, stk_rva))
    walk_stack(buf, st, mods, sp, stack=(stack_start, stk_size, stk_rva))


if __name__ == '__main__':
    main()
