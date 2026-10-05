# -*- coding: utf-8 -*-
"""给 Patcher.cs 里各模块装上"总开关"。

分四块:
  Recolor -> EffectRecolor
  Combat  -> DashInvincible / PerfectDodge / JumpDashCrossReset / SkillCostTweak / ActionLimitTrace / SlowMotionTrace
  Extra   -> CrestTakeover / AhWing / ActionProbe / BulletProbe
（JsPatches 在 Plugin.Load 里处理, 不在这里）
"""
import io, os, re

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "plugin", "BlazblueJsPatch")
p = os.path.join(BASE, "Patcher.cs")
s = io.open(p, encoding="utf-8").read()

# 1) 加三个只读属性
anchor = "    public static int Apply(Harmony harmony)\n    {"
props = """    /// <summary>模块级总开关 —— 关掉就是【根本不挂载】, 不是"挂上不干活"。</summary>
    private static bool Combat => Plugin.CfgMountCombat?.Value != false;
    private static bool Extra => Plugin.CfgMountExtra?.Value != false;
    private static bool Recolor => Plugin.CfgMountRecolor?.Value != false;

"""
assert anchor in s
s = s.replace(anchor, props + anchor, 1)

# 2) 各模块套上开关
SUBS = [
    ("PerfectDodge.Apply(harmony)", "Combat"),
    ("EffectRecolor.Apply(harmony)", "Recolor"),
    ("JumpDashCrossReset.Apply(harmony)", "Combat"),
    ("ActionLimitTrace.Apply(harmony)", "Combat"),
    ("SlowMotionTrace.Apply(harmony)", "Combat"),
    ("CrestTakeover.Apply(harmony)", "Extra"),
    ("AhWing.Apply(harmony)", "Extra"),
    ("ActionProbe.Apply(harmony)", "Extra"),
    ("BulletProbe.Apply(harmony)", "Extra"),
    ("SkillCostTweak.Apply(harmony)", "Combat"),
]

for call, flag in SUBS:
    # 只替换 `try { n += X }` 形式
    old = f"try {{ n += {call}; }}"
    new = f"try {{ if ({flag}) n += {call}; }}"
    if old not in s:
        print(f"  !! 找不到 {old}")
        continue
    s = s.replace(old, new, 1)

io.open(p, "w", encoding="utf-8").write(s)

# 统计
n = len(re.findall(r"if \((?:Combat|Extra|Recolor)\) n \+= ", s))
print(f"已套上开关的模块调用: {n}")
for call, flag in SUBS:
    print(f"  {'OK ' if f'try {{ if ({flag}) n += {call}; }}' in s else 'MISS'} {flag:8} {call}")
