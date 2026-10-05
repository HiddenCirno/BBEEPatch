# -*- coding: utf-8 -*-
"""InputDir 不再写死 Any，改为继承源动作的方向 —— 这是"串链条"的根因修复。

实测（链[1] 原生布局，同一条链靠 InputDir 分流）：
    Order 4-7   attackD1/D1/D2/D3   InputDir=2 (下)  ← 佩利诺尔
    Order 8     atkAirX             InputDir=2 (下)
    Order 9-12  UltraDash*          InputDir=1 (上)
    Order 13    atkAirX             InputDir=0 (任意)
    Order 14    holdEX              InputDir=0        ← 纹章解放，本来就是链[1] 的邻居
    （平A 那几段原本 InputDir=0）

而 MakeSkill 里原本是：
    copy.InputDir = (SkillInputDirType)0;    // ★ 每一段都写死 Any
`Any` 的语义是"任意方向都算"，所以按住下按攻击时我们的段照样成立，
把"下"吃掉了 —— 佩利诺尔那些 InputDir=下 的段永远选不中。**这就是"串链条"。**

改法：继承源动作的 InputDir（和 Timeout/ActdurStrict 同一原则），
并留一个 WindowOverrides 式的逐段覆盖入口（第 6 位）。
"""
import io

P = "Modules/Es/EsComboChain.cs"
s = io.open(P, encoding="utf-8").read()

A = """            // 不按方向也能接下去
            try { copy.InputDir = (SkillInputDirType)0; } catch { }"""
assert A in s, "缺 InputDir 写死处"
B = """            // ★★★★★ 【不再写死 Any】—— "串链条"的根因（2026-10-03）
            //   实测：平A 和佩利诺尔是【同一条链】，靠 InputDir 分流：
            //       佩利诺尔 = InputDir 下 | 平A = InputDir 任意 | 上挑 = 上
            //   而我们给每一段都写死 Any ⇒ 任意方向都成立 ⇒ 按住下按攻击时
            //   我们的段把"下"吃掉了，佩利诺尔永远选不中。
            //   改成继承源动作的方向（和 Timeout/ActdurStrict 同一原则）。
            //   逐段覆盖走 WindowOverrides 的第 6 位（见下面）。
            try
            {
                var srcFixed = new SkillActivateFixedPoint(srcPtr);
                copy.InputDir = srcFixed.InputDir;
            }
            catch (Exception e) { Once("InputDir", e); }"""
s = s.replace(A, B, 1)

# WindowOverrides 支持第 6 位 = InputDir
a2 = "var arr = new double[] { -1, -1, -1, -1, -1 };"
assert a2 in s, "缺 arr 分配"
s = s.replace(a2, "var arr = new double[] { -1, -1, -1, -1, -1, -1 };", 1)

a3 = "for (int i = 0; i < 5 && i < vals.Length; i++)"
assert a3 in s, "缺解析循环"
s = s.replace(a3, "for (int i = 0; i < 6 && i < vals.Length; i++)", 1)

a4 = """                    if (ov[4] >= 0) { try { copy.AllowFlying = ov[4] > 0.5; } catch { } }"""
assert a4 in s, "缺 AllowFlying 应用"
B4 = a4 + """
                    // [5] InputDir（方向分流）: 0=任意 1=上 2=下 3=前 4=后 5=无方向
                    if (ov[5] >= 0) { try { copy.InputDir = (SkillInputDirType)(int)ov[5]; } catch { } }"""
s = s.replace(a4, B4, 1)

io.open(P, "w", encoding="utf-8").write(s)
print("InputDir 继承 + 覆盖入口 已加入")
