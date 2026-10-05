# -*- coding: utf-8 -*-
import io, os, sys
sys.argv = ["_disasm.py", "zzz"]
_t = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _t)
import _disasm as D  # noqa

TARGETS = [
    (0x1BB21E0, "PlayerSkillChain::Init"),
    (0x1BB31B0, "PlayerSkillChain::autoGetInputType"),
    (0x1B99BB0, "PlayerInput::OnInputCmdChange_pressedpath"),
    (0x1B98D08, "PlayerInput::GetCmdStatus_tail"),
    (0x1B988D0, "PlayerInput::GetCmdStatusFromSpecialReplaceByDestKey"),
    (0x1BDDC10, "PlayerSkillUtility::CheckPlayerSkillInputDir"),
    (0x1BE0660, "PlayerSkillUtility::StrictMatchPlayerSkillInputDir"),
    (0x1BB1640, "PlayerSkillChain::FindAStartingSkillForPredictByInputDir"),
    (0x1BB1C70, "PlayerSkillChain::FindByPreSkillOrderByInputDir"),
    (0x1BB3700, "PlayerSkillChain::findNextSkillMatchPreOrderAndInputDir"),
    (0x1BB3B20, "PlayerSkillChain::findStartingSkillMatchInputDir"),
    (0x1B9A280, "PlayerInput::SetPressInputSatisfied"),
    (0x1B984A0, "PlayerInput::DoUpdate"),
]
out = []
for rva, title in TARGETS:
    out += D.disasm(rva, title)
io.open(os.path.join(_t, "_inp_out3.txt"), "w", encoding="utf-8").write("\n".join(out))

# callers
CALLS = [
    (0x1BD8780, "PlayerSkillMgr::DoUpdate"),
    (0x1B99950, "PlayerInput::OnInputCmdChange"),
    (0x1B984A0, "PlayerInput::DoUpdate"),
    (0x1BDDC10, "PlayerSkillUtility::CheckPlayerSkillInputDir"),
    (0x1B9A280, "PlayerInput::SetPressInputSatisfied"),
    (0x1B98C30, "PlayerInput::GetCmdStatus"),
    (0x1B985D0, "PlayerInput::GetCmdHistory"),
    (0x1B98EE0, "PlayerInput::GetLastestCmdHistory"),
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
io.open(os.path.join(_t, "_inp_callers.txt"), "w", encoding="utf-8").write("\n".join(res))
print("done")
