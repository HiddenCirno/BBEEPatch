# -*- coding: utf-8 -*-
import io, os, sys
sys.argv = ["_disasm.py", "zzz"]
_t = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _t)
import _disasm as D  # noqa

TARGETS = [
    (0x1B99471, "PlayerInput::GetMoveDir part2"),
    (0x1B995B6, "PlayerInput::GetMoveDir forced"),
    (0x1B995CF, "PlayerInput::GetMoveDir tailB"),
    (0x1B99C70, "PlayerInput::OnInputDirChange"),
    (0x1B9AD30, "PlayerInput::delayedTeacherSkillInputEventProc"),
    (0x1B9AB30, "PlayerInput::delayedTeacherSkillInputEventProc_SimulateKeyClick"),
    (0x1B9B5E0, "PlayerInput::updateTeacherSkillInputEventProc"),
    (0x1B64B80, "InputCmdState::Clear"),
]
out = []
for rva, title in TARGETS:
    out += D.disasm(rva, title)
io.open(os.path.join(_t, "_inp_out4.txt"), "w", encoding="utf-8").write("\n".join(out))
print("done")
