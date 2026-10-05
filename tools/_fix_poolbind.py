# -*- coding: utf-8 -*-
"""修 Plugin.cs 里 [弹幕池] 的绑定文案：内层双引号把 C# 字符串截断了。
重写成不含内层双引号的版本（本项目铁律：含中文/转义符的脚本一律写 .py 再跑）。
"""
import io, re

P = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Plugin.cs"
s = io.open(P, encoding="utf-8").read()

start = s.index('            var poolsec = "弹幕池";')
end = s.index('            EsEmblemBurst.CfgStartFlySpeed = Config.Bind(csec, ', start)

NEW = '''            var poolsec = "弹幕池";
            BulletPool.CfgEnabled = Config.Bind(poolsec, "Enabled", true,
                "弹幕池扩容。挂在 BulletMgr.PreCreateBullet 上, 把预创建数量按倍率放大。\\n" +
                "\\n" +
                "依据: 池是【预热】出来的 —— PreCreateBullet(normal, spine, model3d) 按视觉类型\\n" +
                "(普通/Spine/3D) 预先建好一批, GetFromPoolOrCreate 不够时才即时 new。\\n" +
                "所以扩容不用碰任何数据, 改传进去的数量就行。\\n" +
                "\\n" +
                "注意它【不根治】什么: 我们的 mover 持有的是裸引用, 弹幕一旦被回收复用,\\n" +
                "旧引用就指到别人的弹幕上 —— 池子大只是让[回收得太早]发生得更少。\\n" +
                "根治靠按时撒手(PinSeconds / MoveSeconds / StartFlySeconds)或每帧换主人检测。\\n" +
                "扩池的真实收益: 连发/高速连段时不再频繁走即时分配, 减少卡顿。");
            BulletPool.CfgScale = Config.Bind(poolsec, "Scale", 2f,
                "预创建数量的放大倍率。1 = 不放大(只看日志里的原始数量)。\\n" +
                "本作默认池子是按普通节奏的 ES 配的; 我们加了 1+8 环 / 多方向剑气之后明显偏小。");
            BulletPool.CfgLog = Config.Bind(poolsec, "Log", true,
                "打印每次预创建请求的原始数量与放大后的数量(前 20 次)。\\n" +
                "必须打: 不打的话[到底扩了多少]永远说不清, 而[配了没生效]和[本来就是这么大]\\n" +
                "在日志里长得一模一样。");

'''
s = s[:start] + NEW + s[end:]
io.open(P, "w", encoding="utf-8").write(s)
print("ok")
