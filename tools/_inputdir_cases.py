# -*- coding: utf-8 -*-
"""解码 StrictMatchPlayerSkillInputDir 的跳转表, 逐个 case 打印判定逻辑。

反汇编里这一段是:
    lea  rdx, [rip - 0x1be0851]        ; rdx = ImageBase (0x180000000)
    mov  ecx, [rdx + rdi*4 + 0x1be09f0]; 读跳转表第 dirType 项
    add  rcx, rdx                       ; 表里存的是 RVA 偏移
    jmp  rcx
所以直接按 RVA 读表 + 反汇编每个分支即可 —— 比人肉读汇编可靠。
"""
import io, os, struct, sys

sys.argv = ["_disasm.py", "zzz_never_matches"]
_t = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _t)
import _disasm as D   # noqa: E402

IMG = D.IMG
TABLE_RVA = 0x1be09f0
NFUNCS = 0x1be0a00 - TABLE_RVA

def rd(rva, n):
    o = D.rva2off(rva)
    return D.d[o:o + n]

raw = rd(TABLE_RVA, NFUNCS * 4)
entries = struct.unpack("<%dI" % NFUNCS, raw)

out = []
out.append(f"跳转表 @RVA {TABLE_RVA:#x}, {NFUNCS} 项")
for i, off in enumerate(entries):
    tgt = IMG + off
    out.append(f"  case {i}: -> {tgt:#x}")

# 把每个分支反汇编出来(到下一个分支起点为止)
starts = sorted(set(IMG + o for o in entries))
for i, off in enumerate(entries):
    tgt = IMG + off
    rva = tgt - IMG
    out.append("")
    out.append(f"########## dirType = {i}  (RVA {rva:#x}) ##########")
    ins = D.decode(rva, max_ins=200)
    for ins_ in ins:
        s = f"  {ins_.address:#014x}  {ins_.mnemonic:<8} {ins_.op_str}"
        if ins_.mnemonic == "call" and ins_.op_str.startswith("0x"):
            try:
                s += f"      ; -> {D.label(int(ins_.op_str,16))}"
            except Exception:
                pass
        out.append(s)

txt = "\n".join(out)
io.open(os.path.join(_t, "_inputdir_cases.txt"), "w", encoding="utf-8").write(txt)
print(txt[:6000])
