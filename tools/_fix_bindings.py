# -*- coding: utf-8 -*-
"""
修 Plugin.cs 里被 heredoc 毁掉的两段 config 绑定说明。

坑的经过: 用 bash heredoc 跑 python，源码里的 `\\n` 被吃掉一层变成 `\n`，
于是 C# 字符串里出现了**真换行** ⇒ CS1010「常量中有换行符」。
（本项目栽过第二次了 —— 规矩是：含中文/转义符的脚本一律 Write 成 .py 再跑。）

本脚本的做法: **绝不写字面反斜杠**，一律用 BS = chr(92) 拼出来，这样任何转义层都吃不掉它。
定位也用锚点切片，不做整段字面量匹配（CRLF/引号会把匹配搞崩）。
"""
import io

P = r'I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Plugin.cs'
BS = chr(92)          # 反斜杠本身
Q = '"'
NL = BS + 'n'         # C# 字符串里的换行转义（两个字符）


def lit(indent, text, tail):
    """拼一行 C# 字符串字面量：indent + "text\\n" + tail"""
    return indent + Q + text + NL + Q + tail


s = io.open(P, encoding='utf-8', newline='').read().replace('\r\n', '\n')

# ---- ① CfgEnabled / CfgKey 两段（被 heredoc 毁掉的那段）----------------------
a = s.index('            SnapshotProbe.CfgEnabled = Config.Bind')
b = s.index('            SnapshotProbe.CfgKey = Config.Bind')
c = s.index('            SnapshotProbe.CfgFreeze = Config.Bind')

I4 = '                '
I3 = '            '

enabled = '\n'.join([
    I3 + 'SnapshotProbe.CfgEnabled = Config.Bind(isec, "Snapshot", true,',
    lit(I4, '★【时停 + 现场捕获】= UnityExplorer 那一套, 自己做出来:', ' +'),
    lit(I4, '  SnapshotTimeKey 慢放循环 -> SnapshotKey 时停 -> SnapshotPickKey 捕获(名字标在屏幕上)。', ' +'),
    lit(I4, '  为什么必须自己做: UE 的 UI 在本作修不动(UniverseLib 要 Il2CppStructArray<byte>,', ' +'),
    I4 + Q + '  而 Il2Cppmscorlib 在内存里有两份 -> Il2CppInterop 的 Single() 抛异常)。");',
])

key = '\n'.join([
    I3 + 'SnapshotProbe.CfgKey = Config.Bind(isec, "SnapshotKey", "F6",',
    lit(I4, '★ 时停开关(UnityEngine.KeyCode 名)。按一下 Time.timeScale=0, 再按恢复。', ' +'),
    I4 + Q + '  ⚠ 这个键走 IMGUI 事件, 所以**冻结时照样收得到**(走 Input.GetKey* 会卡死, 栽过)。");',
    '',
])

s = s[:a] + enabled + '\n' + key + s[c:]
io.open(P, 'w', encoding='utf-8', newline='').write(s.replace('\n', '\r\n'))

# ---- 自检: 任何 C# 字符串字面量里都不该出现真换行 ------------------------------
bad = []
for i, line in enumerate(s.split('\n'), 1):
    t = line.rstrip()
    if t.count(Q) % 2 == 1:          # 引号奇数个 = 字符串没闭合 = 里面混进了真换行
        bad.append((i, t[:90]))
print('修复完成')
print('可疑行数 =', len(bad))
for i, t in bad[:10]:
    print('  ', i, t)
