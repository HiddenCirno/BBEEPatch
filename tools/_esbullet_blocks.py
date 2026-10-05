# -*- coding: utf-8 -*-
"""把 esbullet 的 ActionLogicGroup 按【动作块】切开，逐块列出它带的指令/参数。

分段依据（实测）：每个动作块里都会出现一次组名 "esbullet"，紧跟其后就是该动作的名字。
    ...|esbullet|dashSkill2|...   ← 一个块的开头
所以以 \x00esbullet\x00 为界切段，下一根串就是动作名。

这样就能看清：x1/x2（原地蓄力）与 x3/x32/xup/xup2（释放）各自带了什么指令 ——
尤其是 ChangeAction（状态迁移）和 SetActionCD（冷却），
它们才是"生命周期"和"何时变成移动形态"的真正载体。
"""
import io, os, re

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
RAW = os.path.join(BASE, "extracted", "esbullet_mono.raw")
OUT = os.path.join(BASE, "tools", "_esbullet_blocks_out.txt")

raw = open(RAW, "rb").read()

# 1) 找所有 "esbullet" 边界
marks = [m.start() for m in re.finditer(rb"\x00esbullet\x00", raw)]
L = [f"esbullet 动作块标记 {len(marks)} 个, 文件 {len(raw)} 字节"]

INTERESTING = ("CreateBullet", "ChangeAction", "ChangeSkill", "SetActionCD",
               "RemoveBuff", "AddBuff", "action:", "bullet_id:", "buff:",
               "RemoteBuff", "Destroy", "trigger:", "tag:", "cd:")

for idx, start in enumerate(marks):
    end = marks[idx + 1] if idx + 1 < len(marks) else len(raw)
    seg = raw[start:end]
    strs = [s.decode("ascii") for s in re.findall(rb"[\x20-\x7e]{2,}", seg)]
    if not strs:
        continue
    name = strs[1] if len(strs) > 1 else "?"
    L.append(f"\n########## 块 #{idx}  动作名 ≈ \"{name}\"  [{start}:{end}] {len(seg)}B ##########")

    # 只列"像指令/参数"的串，跳过纯特效路径和音效
    hits = []
    for s in strs:
        if any(s.startswith(p) or p in s for p in INTERESTING):
            hits.append(s)
    for h in hits:
        L.append("   " + h[:220])
    if not hits:
        L.append("   (无指令类串)")
    # 顺带把该块用到的特效和判定列出来 —— 这是判断"哪个动作是那个环"的依据
    fx = [s for s in strs if s.startswith("Role/") or s.startswith("Effect/") or s.startswith("Hit/")]
    if fx:
        L.append("   [资源] " + " | ".join(dict.fromkeys(fx)))

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT)
