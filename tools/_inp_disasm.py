# -*- coding: utf-8 -*-
"""Input-system focused disassembly. ASCII-safe print, full output to UTF-8 file."""
import io, os, sys
sys.argv = ["_disasm.py", "zzz_never_matches"]
_t = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _t)
import _disasm as D  # noqa

TARGETS = [
    (0x1BD8780, "PlayerSkillMgr::DoUpdate"),
    (0x1BB1260, "PlayerSkillChain::DoUpdate"),
    (0x1BB10F0, "PlayerSkillChain::DoUpdateAndCheckInputSucc"),
    (0x1BB3320, "PlayerSkillChain::findAndStartSkill_Imp"),
    (0x1BB29F0, "PlayerSkillChain::StartSkill"),
    (0x1BB40D0, "PlayerSkillChain::startSkill"),
    (0x1BB21E0, "PlayerSkillChain::Init"),
    (0x1BB31B0, "PlayerSkillChain::autoGetInputType"),
    (0x1BDDC10, "PlayerSkillUtility::CheckPlayerSkillInputDir"),
    (0x1BE0660, "PlayerSkillUtility::StrictMatchPlayerSkillInputDir"),
    (0x1BDBD30, "PlayerSkillMgr::SkillStartImplement"),
    (0x1BDBB80, "PlayerSkillMgr::SkillChangePreCall"),
    (0x1BB2560, "PlayerSkillChain::PredictNextSkillByInputDir"),
    (0x1BB3700, "PlayerSkillChain::findNextSkillMatchPreOrderAndInputDir"),
    (0x1BB3B20, "PlayerSkillChain::findStartingSkillMatchInputDir"),
    (0x1BB1640, "PlayerSkillChain::FindAStartingSkillForPredictByInputDir"),
    (0x1BB1C70, "PlayerSkillChain::FindByPreSkillOrderByInputDir"),
]

out = []
for rva, title in TARGETS:
    out += D.disasm(rva, title)

io.open(os.path.join(_t, "_inp_out.txt"), "w", encoding="utf-8").write("\n".join(out))
print(f"lines={len(out)} -> _inp_out.txt")
