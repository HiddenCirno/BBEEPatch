# -*- coding: utf-8 -*-
"""
修 Plugin.cs 里 TintBrightMode 那段配置说明。

这次踩的坑（同一类第 N 次，记下来）：
  · 用 bash heredoc 跑 python，源码里的 `\\n` 被吃掉一层 → C# 字符串里出现真换行 → CS1010。
  · 改成 `NL=chr(92)+'n'` 之后又在 join 时把它当**真换行**用 → 整块被压成一行。
  ⇒ 规矩：**真换行用 chr(10)，C# 的转义序列用 chr(92)+'n'**，两者绝不混用。
  ⇒ 而且含转义符的脚本一律 Write 成 .py 再跑，不要用 heredoc。
"""
import io

P = r'I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Plugin.cs'
BS = chr(92)          # 反斜杠
LF = chr(10)          # 真换行
ESC = BS + 'n'        # C# 字符串里的换行转义（两个字符）
Q = '"'
IND = ' ' * 16        # 续行缩进

s = io.open(P, encoding='utf-8', newline='').read().replace('\r\n', '\n')

a = s.index('            RecolorConfig.BrightMode = Config.Bind(esec, "TintBrightMode"')
b = s.index('            RecolorConfig.MaxScale = Config.Bind(esec, "TintMaxScale"')


def lit(text, tail=' +'):
    """一行 C# 字符串字面量。"""
    return IND + Q + text + ESC + Q + tail


block = LF.join([
    '            RecolorConfig.BrightMode = Config.Bind(esec, "TintBrightMode", "capped",',
    lit('★【保亮度口径】capped(默认) / luma / peak —— 「保亮度」到底保什么, 是个无解的取舍:'),
    lit('  · 淡色目标(如 A0F0C0): max ≈ Luma ⇒ 三种几乎一样 ⇒ 怎么都对'),
    lit('  · 饱和目标(如 CD00F0): max 远大于 Luma(3.9:1) ⇒ 三种差很远:'),
    lit('      - peak ⇒ 不过曝, 但整体偏暗'),
    lit('      - luma ⇒ 亮度一致, 但峰值通道被推到 ~3.9 倍 ⇒ bloom 下刺眼'),
    lit('      - capped(默认) = min(luma, peak): **保感知亮度, 但绝不让结果峰值超过原色峰值**'),
    lit('        ⇒ 淡色目标下等于 luma; 饱和目标下自动退回 peak。'),
    lit('        物理含义: 绝不把光效的核心烧得比原来更烫(bloom 阈值就是这么判的)。', ');'),
    '',
])

s = s[:a] + block + s[b:]

# 自检：任何一行里"未闭合的引号"都应该没有；且不该出现真的换行藏在字符串里
bad = []
for i, line in enumerate(s.split('\n'), 1):
    t = line.rstrip()
    if t.count(Q) % 2 == 1:
        bad.append((i, t[:100]))

io.open(P, 'w', encoding='utf-8', newline='').write(s.replace('\n', '\r\n'))
print('修复完成；可疑行数 =', len(bad))
for i, t in bad[:10]:
    print('   ', i, t)
print('BrightMode 行数 =', s.count('BrightMode = Config.Bind'))
