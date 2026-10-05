# -*- coding: utf-8 -*-
"""第二轮定点反汇编: 输入方向匹配 + 输入门 + 预输入缓冲。

★ 重要提醒(工具局限, 读输出时必须记住):
   RVA->名字映射是 setdefault 的, 而 IL2CPP 里【大量微小 getter 共用同一段机器码】。
   所以输出里的 `XxxWrap::get_Yyy` 常常是"同 RVA 的第一个被命名的类", 未必是真的那个方法。
   判断字段归属要看【偏移量】和【调用上下文】, 不要只看标注的名字。
"""
import io, os, re, sys

sys.argv = ["_disasm.py", "zzz_never_matches"]
_t = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, _t)
import _disasm as D   # noqa: E402

DUMP = os.path.join(_t, "..", "dump", "dump.cs")
lines = io.open(DUMP, encoding="utf-8", errors="replace").read().split("\n")

WANT_CLASSES = {"PlayerSkillUtility", "PlayerSkillMgr", "PlayerObj", "PlayerInput"}
WANT_NAMES = {
    "StrictMatchPlayerSkillInputDir", "MatchPlayerSkillInputDir",
    "CheckSkillCanCast", "GetSkillCastRequires", "SkillStartImplement",
    "FindSkillAndChainByActionName", "CheckCanChangeToAction",
    "OnInputCommand", "GetInputDir", "get_InputDir",
}

targets, cur, pending = [], None, None
for l in lines:
    s = l.strip()
    m = re.match(r'^(?:public|internal)\s+(?:sealed\s+|abstract\s+)?(?:class|struct)\s+([\w.`]+)', s)
    if m:
        cur = m.group(1)
    if s.startswith("// RVA:"):
        mm = re.match(r'// RVA: (0x[0-9A-Fa-f]+)', s)
        pending = int(mm.group(1), 16) if mm else None
        continue
    if pending is not None and cur and "(" in s and ";" not in s:
        mm = re.match(r'^(?:public|private|internal|protected|virtual|static|\s)*[\w.<>\[\],\s]+?\s([\w.<>`]+)\s*\(', s)
        if mm and cur in WANT_CLASSES and mm.group(1) in WANT_NAMES:
            targets.append((f"{cur}::{mm.group(1)}", pending, s))
    pending = None

seen = set()
out = []
for name, rva, sig in targets:
    if rva in seen:
        continue
    seen.add(rva)
    out += D.disasm(rva, f"{name}  {sig.strip()}")

txt = "\n".join(out)
io.open(os.path.join(_t, "_chain2_out.txt"), "w", encoding="utf-8").write(txt)
print(f"dumped {len(seen)} methods, {len(out)} lines -> tools/_chain2_out.txt")
for n, r, s in targets:
    print(f"  {r:#x}  {s.strip()[:110]}")
