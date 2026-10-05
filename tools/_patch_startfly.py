# -*- coding: utf-8 -*-
"""给 Plugin.cs 加 StartFlySpeed / StartFlySeconds 两个绑定，并更新 StartBullets 的说明。
⚠ 必须写成 .py 再跑 —— 内联 heredoc 会毁掉中文（本项目踩过的坑）。"""
import io

P = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Plugin.cs"
s = io.open(P, encoding="utf-8").read()

anchor = '            EsEmblemBurst.CfgStartBullets = Config.Bind(csec, "StartBullets", "",'
assert anchor in s, "anchor missing"

NEW = '''            EsEmblemBurst.CfgStartFlySpeed = Config.Bind(csec, "StartFlySpeed", 18f,
                "★ 自推飞行的默认速度(世界单位/秒)。规则里没写第 5 段时用它。\\n" +
                "为什么必须自推: 弹幕自己的位移逻辑【不看 dir】(实测 30/60/90 无变化、120/150 才翻向 ——\\n" +
                "它只拿 dir 当[朝前/朝后]的粗判据), 所以斜向 45/135 交给它必然退化。\\n" +
                "本作逻辑帧 30fps。世界单位 ≈ 角色身高量级, 先给 18 试手感。");

            EsEmblemBurst.CfgStartFlySeconds = Config.Bind(csec, "StartFlySeconds", 1.2f,
                "自推飞行管多久(秒)。到期就撒手、交还给弹幕自己的逻辑。\\n" +
                "⚠ 必须是有限值: 弹幕是【池化复用】的, 引用失效后继续写 Position 会挪动别的弹幕。");

'''
s = s.replace(anchor, NEW + anchor, 1)

OLD_DESC = '                "★ 动作【起手】时按延迟放出弹幕。语法与 AttachBullets 完全一致：\\n" +'
NEW_DESC = '''                "★ 动作【起手】时按延迟放出弹幕。\\n" +
                "   触发动作:弹幕[:延迟秒[:屏幕角度[:飞行速度]]]   多条用 | 分隔\\n" +
                "   屏幕角度: 0=右 90=上 180=左 -90=下(崔斯坦没有方向性, 所以按屏幕算)\\n" +
                "   写了【飞行速度】= 我们自己推着它飞(精确方向); 不写 = 只转 dir 交给弹幕自己 ——\\n" +
                "   那样【只有 0/180 可靠】, 斜向会退化。\\n" +'''
assert OLD_DESC in s, "desc missing"
s = s.replace(OLD_DESC, NEW_DESC, 1)

io.open(P, "w", encoding="utf-8").write(s)
print("ok")
