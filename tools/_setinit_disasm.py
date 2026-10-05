# -*- coding: utf-8 -*-
"""InterpolatorSet::Init —— 判断它把 entries 数组【存引用】还是【克隆】。

这一个事实决定换色管线的做法:
  存引用 -> 改源数组就能影响正在播的特效
  克隆   -> 必须另外找到克隆体(_interpolatorSets[i].interpolators)一起改
"""
import os, sys, io

sys.argv = ["_disasm.py", "zzz_never_matches"]
_t = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _t)
import _disasm as D   # noqa: E402

TARGETS = [
    ("InterpolatorSet::Init", 0x1843ada70),
    ("InterpolatorSet::get_interpolators", 0x579580),
    ("InterpolatorSet::Reset", 0x1843adce0),
    ("InterpolatorSet::.ctor(target,interps,onComplete,autoStart)", 0x43AE070),
]
out = []
for t, r in TARGETS:
    out += D.disasm(r, t)
txt = "\n".join(out)
io.open(os.path.join(_t, "_setinit_out.txt"), "w", encoding="utf-8").write(txt)
print(txt)
