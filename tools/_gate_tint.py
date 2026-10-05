# -*- coding: utf-8 -*-
"""修 bug: 插值器路线没有被总开关 RecolorEffect 管住。

实测现场:
    cfg:  RecolorEffect = false
    日志: 改写插值器 _TintColor: (0,0,0,0.447) -> (0.627,0,0,0.447)   ← 仍在改

因为插值器路是三个独立的 Harmony Prefix (MaterialTinter.Play /
CreateInterpolatorSets / MaterialTinterProxy.Restart), 它们只检查了
SkipScreenSpace / TintExclude / 名字过滤 —— 从来没看过 RecolorEffect。
总开关只拦得住 CreateFxPostfix 里的粒子路。

后果: 用户以为关掉了换色, 实际上一半特效还在改 —— 排查问题时会被严重误导
(而且会让人误以为"关了还在改 = 有持久化缓存", 那是错的诊断方向)。
"""
import io, os

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "plugin", "BlazblueJsPatch")
p = os.path.join(BASE, "EffectRecolor.cs")
s = io.open(p, encoding="utf-8").read()

# 1) 在三个消费点的共用入口加总开关
old = """    private static void ConsumeEntryPrefix(object[] __args, object __instance, string where)
    {
        try
        {
            if (__args == null || __args.Length < 1 || __args[0] == null) return;
"""
new = """    private static void ConsumeEntryPrefix(object[] __args, object __instance, string where)
    {
        try
        {
            if (__args == null || __args.Length < 1 || __args[0] == null) return;

            // ★ 总开关。之前漏了这一句 —— 插值器路是独立于 CreateFxPostfix 的三个 Prefix,
            //   没被 RecolorEffect 管住, 于是"关掉换色后特效还在变色"。
            //   排查这类问题时最忌讳开关失灵: 它会把人引向"有持久化缓存"这种错误结论。
            if (CfgRecolorEffect?.Value != true) return;
"""
assert old in s, "找不到 ConsumeEntryPrefix"
s = s.replace(old, new, 1)

# 2) ProxyRestartPrefix 也要管
old2 = """            if (__instance == null || _proxyInterps == null) return;

            // 屏幕空间特效同样要放过 —— 见 ConsumeEntryPrefix 里的说明。"""
new2 = """            if (__instance == null || _proxyInterps == null) return;
            if (CfgRecolorEffect?.Value != true) return;   // 总开关, 同上

            // 屏幕空间特效同样要放过 —— 见 ConsumeEntryPrefix 里的说明。"""
assert old2 in s, "找不到 ProxyRestartPrefix"
s = s.replace(old2, new2, 1)

io.open(p, "w", encoding="utf-8").write(s)
print("EffectRecolor patched")

# 3) 配置项说明补一句: 现在是真·总开关
p = os.path.join(BASE, "Plugin.cs")
s = io.open(p, encoding="utf-8").read()
old3 = '"所以改的是每个特效【实例】上的粒子系统, 共享材质一个字节都不动。");'
new3 = ('"所以改的是每个特效【实例】上的粒子系统, 共享材质一个字节都不动。\\n" +\n'
        '                "【总开关】同时管住粒子路和插值器路(材质着色)。关掉 = 完全不改色。");')
assert old3 in s
s = s.replace(old3, new3, 1)
io.open(p, "w", encoding="utf-8").write(s)
print("Plugin patched")
