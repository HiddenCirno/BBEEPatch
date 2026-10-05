# -*- coding: utf-8 -*-
"""定点反汇编 PlayerSkillChain 的连段推进算法。

问的是这套系统的核心问题:
  「下一段是谁」到底怎么定 —— 是按数组顺序? 按 order? 还是按 preSkillOrder 列表?
  preSkillOrder 是【标量还是列表】? 多个前置能不能并存?
  输入方向(InputDir)在哪一步参与筛选?
  输入缓冲(预输入)存在哪、活多久?

不做猜测: 直接把方法体拆出来, 看它读了哪些字段偏移、调了谁。
"""
import io, os, re, sys

sys.argv = ["_disasm.py", "zzz_never_matches"]
_t = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _t)
import _disasm as D   # noqa: E402

DUMP = os.path.join(_t, "..", "dump", "dump.cs")
lines = io.open(DUMP, encoding="utf-8", errors="replace").read().split("\n")

# ---- 从 dump.cs 抓 PlayerSkillChain 每个方法的 RVA
targets = []
cur = None
pending = None
for l in lines:
    s = l.strip()
    if re.match(r'^public class PlayerSkillChain\b', s) or re.match(r'^public class PlayerSkillChainCurSkill\b', s):
        cur = s.split()[2]
    if s.startswith("// RVA:"):
        m = re.match(r'// RVA: (0x[0-9A-Fa-f]+)', s)
        pending = int(m.group(1), 16) if m else None
        continue
    if pending is not None and cur and "(" in s and ";" not in s:
        m = re.match(r'^(?:public|private|internal|protected|virtual|static|\s)*[\w.<>\[\],\s]+?\s([\w.<>`]+)\s*\(', s)
        if m:
            targets.append((f"{cur}::{m.group(1)}", pending, s))
    pending = None

WANT = [
    "DoUpdate", "DoUpdateAndCheckInputSucc", "findAndStartSkill_Imp",
    "FindAndStartSkillFromMidByOrder", "startSkill", "StartSkill",
    "findNextSkillMatchPreOrderAndInputDir", "findStartingSkillMatchInputDir",
    "FindByPreSkillOrder", "FindByPreSkillOrderByInputDir", "FindByActionName",
    "PredictNextSkillByInputDir", "FindAStartingSkillForPredictByInputDir",
    "FindAStartingSkillForPredict", "predictNextSkill", "Reset", "ClearCurSkill",
    "indexOfNoGC",
]

out = []
for name, rva, sig in targets:
    short = name.split("::")[1].split("(")[0]
    if short not in WANT:
        continue
    out.append(f"\n### {name}   {sig.strip()}")
    out += D.disasm(rva, f"{name}  {sig.strip()}")

txt = "\n".join(out)
io.open(os.path.join(_t, "_chain_out.txt"), "w", encoding="utf-8").write(txt)
print(f"methods found: {len(targets)}; dumped {len(out)} lines -> tools/_chain_out.txt")
for n, r, s in targets:
    print(f"  {r:#x}  {s.strip()[:100]}")
