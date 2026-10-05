# -*- coding: utf-8 -*-
import io, os, sys, struct
sys.argv = ["_disasm.py", "zzz"]
_t = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _t)
import _disasm as D  # noqa

out = []

# ---- jump table for CheckPlayerSkillInputDir (RVA 0x1BDDC10) ----
# lea rdx,[rip-0x1bdddaf] at 0x181bddda8 ; table base VA = 0x180000000
# entry = dword at RVA 0x1BDE16C + i*4 ; target RVA = base + entry  (base=0x180000000)
JT = 0x1BDE16C
o = D.rva2off(JT)
print("jt file offset", hex(o) if o else o)
ents = struct.unpack_from("<9i", D.d, o)
base_rva = 0x0  # base VA 0x180000000 -> rva 0
tgts = []
for i, e in enumerate(ents):
    rva = base_rva + e
    tgts.append((i, rva))
print("jt targets:")
for i, r in tgts:
    out.append(f"[dirType {i}] case target RVA {r:#x}  -> {D.label(0x180000000 + r)}")
print("\n".join(out))

seen = set()
for i, r in tgts:
    if r in seen:
        out.append(f"--- (dirType {i}) shared with previous")
        continue
    seen.add(r)
    out += D.disasm(r, f"CheckPlayerSkillInputDir case dirType={i}")

TARGETS = [
    (0x148B040, "getter_0x148B040 (dump: SkillActivate.get_InputDir?)"),
    (0x5BBA10, "getter_0x5BBA10"),
    (0x148B000, "getter_0x148B000 (ActorId)"),
    (0x1B992A0, "PlayerInput::GetMoveDir"),
    (0x1B99200, "PlayerInput::GetMoveDirSimple"),
    (0x1B990D0, "PlayerInput::GetMoveDirFromPureInputCache"),
    (0x1B9B260, "PlayerInput::getMoveDirFromPureInputCache"),
    (0x1BB2560, "PlayerSkillChain::PredictNextSkillByInputDir"),
    (0x1B9A330, "PlayerInput::.cctor"),
    (0x1B9B550, "PlayerInput::restoreMoveDirFromBlockCache"),
]
for rva, title in TARGETS:
    out += D.disasm(rva, title)

io.open(os.path.join(_t, "_inp_jt.txt"), "w", encoding="utf-8").write("\n".join(out))

CALLS = [
    (0x1BB2560, "PredictNextSkillByInputDir"),
    (0x1BB1AC0, "FindAndStartSkillFromMidByOrder"),
    (0x1BB4000, "predictNextSkill"),
    (0x1B9A110, "RemoveInputCmdBlock"),
]
res = []
for rva, name in CALLS:
    cs = D.callers_of(rva)
    res.append(f"\n### callers of {name} ({rva:#x}) : {len(cs)}")
    for c in cs[:30]:
        best = None
        for r in D.RVA2NAME:
            if r <= c and (best is None or r > best):
                best = r
        who = D.RVA2NAME.get(best, "?")
        res.append(f"   {c:#010x} <- {who} (+{c-best:#x})")
io.open(os.path.join(_t, "_inp_callers2.txt"), "w", encoding="utf-8").write("\n".join(res))
print("done")
