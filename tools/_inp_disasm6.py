# -*- coding: utf-8 -*-
import io, os, sys
sys.argv = ["_disasm.py", "zzz"]
_t = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _t)
import _disasm as D  # noqa
TARGETS = [
    (0x1C56BF0, "GameInputManager::Update"),
    (0x1C54650, "GameInputManager::ResolveActorAxis"),
    (0x1C54850, "GameInputManager::ResolveActorButton"),
    (0x1C51670, "GameInputManager::NotifyCmdChange"),
    (0x1C519A0, "GameInputManager::NotifyDirChange"),
    (0x275441, "Fp2::Digitize"),
]
out = []
for rva, title in TARGETS:
    out += D.disasm(rva, title)
io.open(os.path.join(_t, "_inp_out6.txt"), "w", encoding="utf-8").write("\n".join(out))
print("done")
