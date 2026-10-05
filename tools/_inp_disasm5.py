# -*- coding: utf-8 -*-
import io, os, sys
sys.argv = ["_disasm.py", "zzz"]
_t = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _t)
import _disasm as D  # noqa
TARGETS = [
    (0x1B996B0, "PlayerInput::Init"),
    (0x153B130, "CommandManager::ResolveInput"),
    (0x153AEC0, "CommandManager::NotifyPressChange"),
    (0x153AE50, "CommandManager::NotifyDirChange"),
    (0x1BADC30, "PlayerObj::UpdateLogic"),
]
out = []
for rva, title in TARGETS:
    out += D.disasm(rva, title)
io.open(os.path.join(_t, "_inp_out5.txt"), "w", encoding="utf-8").write("\n".join(out))
CALLS = [(0x153B130, "CommandManager::ResolveInput"),
         (0x1B996B0, "PlayerInput::Init")]
res = []
for rva, name in CALLS:
    cs = D.callers_of(rva)
    res.append(f"### callers of {name} ({rva:#x}) : {len(cs)}")
    for c in cs[:20]:
        best = None
        for r in D.RVA2NAME:
            if r <= c and (best is None or r > best):
                best = r
        res.append(f"   {c:#010x} <- {D.RVA2NAME.get(best,'?')} (+{c-best:#x})")
io.open(os.path.join(_t, "_inp_callers3.txt"), "w", encoding="utf-8").write("\n".join(res))
print("done")
