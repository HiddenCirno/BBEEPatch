# -*- coding: utf-8 -*-
"""按 RVA 反汇编 / 找调用点（_disasm.py 只按方法名找，重名方法用不了）。

用法:
    python _rva.py <rva十六进制> [...]        反汇编这些 RVA
    python _rva.py --callers <rva十六进制>    找谁调用了它（列出调用点所在函数）
"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import importlib.util

spec = importlib.util.spec_from_file_location(
    "d", os.path.join(os.path.dirname(os.path.abspath(__file__)), "_disasm.py"))
# _disasm.py 顶层就干活，直接 exec 它拿符号表
src = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "_disasm.py"),
           encoding="utf-8").read()
src = src.split("def main(")[0]
g = {"__name__": "_d"}
g["__file__"] = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_disasm.py")
exec(compile(src, "_disasm.py", "exec"), g)

disasm = g["disasm"]
callers_of = g["callers_of"]
label = g["label"]
IMG = g["IMG"]
RVA2NAME = g["RVA2NAME"]

args = sys.argv[1:]
if not args:
    print(__doc__)
    raise SystemExit(0)

if args[0] == "--callers":
    for a in args[1:]:
        rva = int(a, 16)
        print(f"\n### 谁调用 {a} ({label(IMG + rva)})")
        for c in callers_of(rva)[:40]:
            best = None
            for r in RVA2NAME:
                if r <= c and (best is None or r > best):
                    best = r
            who = RVA2NAME.get(best, "?")
            print(f"    call @ {c:#010x}  <- {who} (+{c - (best or 0):#x})")
else:
    for a in args:
        rva = int(a, 16)
        print("\n".join(disasm(rva, label(IMG + rva))))
