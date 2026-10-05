# -*- coding: utf-8 -*-
"""修正 cfg 里因「BepInEx 不覆盖已存在配置项」而残留的旧默认值。

背景: BepInEx 的规则是 —— 配置项第一次写进 .cfg 之后, 改代码里的默认值不再生效。
上一版默认值 CrestActions=dashSkill / RingAction="" 已被写进用户 cfg,
所以后来改成 x1,x2 / x3 完全没起作用, 表现为"接管逻辑一次都没触发"。
"""
import io, os

P = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..",
                 "BepInEx", "config", "ace.bbee.jspatch.cfg")

FIX = {
    # x2 = 地面纹章解放; ax2 = 空中纹章解放(两条不同的生成路径, 但都汇到 createBulletImp)。
    # 故意不含 jump(那是"跳跃产生纹章"潜能的) 和 dashAir(空中冲刺的)。
    "CrestActions": "x1,x2,ax2",
    # Move 模式下八个环要用【和中间那个一模一样】的动作，所以这里必须清空 ——
    # 留着 x3 会覆盖掉"同动作"的默认，又变回那条普通的发射纹章。
    "RingAction": "",
}

lines = io.open(P, encoding="utf-8").read().split("\n")
sec = None
changed = []
for i, ln in enumerate(lines):
    s = ln.strip()
    if s.startswith("[") and s.endswith("]"):
        sec = s[1:-1]
        continue
    if sec != "纹章解放" or "=" not in s or s.startswith("#"):
        continue
    k, _, v = s.partition("=")
    k = k.strip()
    if k in FIX and v.strip() != FIX[k]:
        changed.append(f"{k}: \"{v.strip()}\" -> \"{FIX[k]}\"")
        lines[i] = f"{k} = {FIX[k]}"

if changed:
    io.open(P, "w", encoding="utf-8").write("\n".join(lines))
    print("已修正:")
    for c in changed:
        print("  ", c)
else:
    print("无需修改")
