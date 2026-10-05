# -*- coding: utf-8 -*-
"""定点反汇编 MaterialTinter 的消费/缓存路径。

问的问题很具体: 传给 Play / CreateInterpolatorSets 的那个 InterpolatorBase[]
到底是被【存引用】还是被【克隆】—— 这决定了"改源数组"能不能影响到正在播的特效。
"""
import io, os, sys

sys.argv = ["_disasm.py", "zzz_never_matches"]
_t = os.path.join(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _t)
import _disasm as D   # noqa: E402  导入即加载 PE + dump.cs

TARGETS = [
    ("MaterialTinter::Play", 0x7DF9C0),
    ("MaterialTinter::CreateInterpolatorSets", 0x7DF5F0),
    ("MaterialTinter::CheckInterpolatorSetsCacheValid", 0x7DF470),
    ("MaterialTinter::Restart", 0x7DFDB0),
    ("MaterialTinter::RestartInterpolatorSets", 0x7DFD00),
    ("MaterialTinterProxy::Restart", 0x7DF020),
    ("InterpolatorSet::.ctor", 0x43AE070),
]

out = []
for title, rva in TARGETS:
    out += D.disasm(rva, title)

txt = "\n".join(out)
io.open(os.path.join(_t, "_tinter_out.txt"), "w", encoding="utf-8").write(txt)
print(txt)
