# -*- coding: utf-8 -*-
"""
反汇编 coreclr.dll 里那两个和崩溃有关的偏移 —— 用来判断"崩的是不是真的 AV, 还是异常分发自己崩了"。

用法: _coreclr_probe.py <coreclr.dll 路径> <rva...>

为什么要看: WinDbg 不在, PDB 也没有。但**指令本身**已经够区分三种情况:
  · `mov [reg+off], reg` / `add` 之类**访问内存**的指令崩在里面  ⇒ 真的是访问冲突(坏指针)
  · 崩在 `call`/`jmp` 上                                       ⇒ 栈/跳转表坏了
  · 崩在 push/pop 或 `sub rsp` 上                              ⇒ 栈爆了(那异常码该是 0xC00000FD)
"""
import sys, struct
from capstone import Cs, CS_ARCH_X86, CS_MODE_64


def file_offset(buf, rva):
    """PE: RVA -> 文件偏移"""
    e_lfanew = struct.unpack_from('<I', buf, 0x3C)[0]
    assert buf[e_lfanew:e_lfanew + 4] == b'PE\0\0'
    nsec = struct.unpack_from('<H', buf, e_lfanew + 6)[0]
    opt = e_lfanew + 24
    sec = opt + struct.unpack_from('<H', buf, e_lfanew + 20)[0]
    for i in range(nsec):
        b = sec + i * 40
        name = buf[b:b + 8].rstrip(b'\0').decode('ascii', 'replace')
        vsize, vaddr, rawsize, rawptr = struct.unpack_from('<IIII', buf, b + 8)
        if vaddr <= rva < vaddr + max(vsize, rawsize):
            return rawptr + (rva - vaddr), name
    return None, None


def main():
    path = sys.argv[1]
    buf = open(path, 'rb').read()
    print('coreclr.dll 大小 %d' % len(buf))
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    for arg in sys.argv[2:]:
        rva = int(arg, 16) if arg.lower().startswith('0x') else int(arg, 16)
        off, sec = file_offset(buf, rva)
        if off is None:
            print('\n0x%X: 不在任何节里' % rva); continue
        start = off - 24
        print('\n=== coreclr.dll+0x%X (节 %s, 文件偏移 0x%X) ===' % (rva, sec, off))
        code = buf[start:off + 40]
        for ins in md.disasm(code, rva - 24):
            mark = '  <<<< 崩溃点' if ins.address == rva else ''
            print('  0x%08X  %-24s %s %s%s' % (ins.address, ins.bytes.hex(), ins.mnemonic, ins.op_str, mark))


if __name__ == '__main__':
    main()
