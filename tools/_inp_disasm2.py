# -*- coding: utf-8 -*-
import io, os, sys
sys.argv = ["_disasm.py", "zzz"]
_t = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _t)
import _disasm as D  # noqa

TARGETS = [
    (0x1B98C30, "PlayerInput::GetCmdStatus"),
    (0x1B9B150, "PlayerInput::getInputCmd_raw"),
    (0x1B985D0, "PlayerInput::GetCmdHistory"),
    (0x1B98EE0, "PlayerInput::GetLastestCmdHistory"),
    (0x1B99950, "PlayerInput::OnInputCmdChange"),
    (0x1B9A7E0, "PlayerInput::addCmdHistory"),
    (0x1B995E0, "PlayerInput::GetTimeEllaps"),
    (0x1B984A0, "PlayerInput::DoUpdate"),
    (0x1B98790, "PlayerInput::GetCmdStatusBySkillSlotStatus"),
    (0x1B98080, "PlayerInput::ClearSafe"),
    (0x1B97F10, "PlayerInput::ClearOnlyKeyPresss"),
    (0x1B988D0, "PlayerInput::GetCmdStatusFromSpecialReplaceByDestKey"),
    (0x1B64B80, "InputCmdState::Clear"),
    (0x1B64C70, "InputCmdState::CopyFrom"),
    (0x1B99C70, "PlayerInput::OnInputDirChange"),
]
out = []
for rva, title in TARGETS:
    out += D.disasm(rva, title)
io.open(os.path.join(_t, "_inp_out2.txt"), "w", encoding="utf-8").write("\n".join(out))
print(f"lines={len(out)}")
