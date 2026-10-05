# -*- coding: utf-8 -*-
"""给 csproj 加上 Unity.Mathematics.FixedPoint 引用 (纹章接管构造 Fp2 要用)。"""
import io, os

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "plugin", "BlazblueJsPatch")
p = os.path.join(BASE, "BlazblueJsPatch.csproj")
s = io.open(p, encoding="utf-8").read()

if "Unity.Mathematics.FixedPoint" in s:
    print("已存在, 跳过")
else:
    old = '    <Reference Include="VFX.Interpolator">'
    BS = chr(92)
    new = (
        '    <!-- 纹章接管要用 Fp / Fp2 (定点数向量) 构造旋转后的方向 -->\n'
        '    <Reference Include="Unity.Mathematics.FixedPoint">\n'
        '      <HintPath>$(GameDir)' + BS + 'BepInEx' + BS + 'interop' + BS + 'Unity.Mathematics.FixedPoint.dll</HintPath>\n'
        '      <Private>false</Private>\n'
        '    </Reference>\n'
        '    <Reference Include="VFX.Interpolator">'
    )
    assert old in s, "找不到 VFX.Interpolator 锚点"
    io.open(p, "w", encoding="utf-8").write(s.replace(old, new, 1))
    print("csproj 已加引用")
