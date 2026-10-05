# -*- coding: utf-8 -*-
"""给贝德维尔（Ultra）加一个 1.5 倍速组。
新增第 7 组而不是塞进 Group3：Group3 已经是"纹章解放+高文"，混在一起以后没法单独调。
只列 Ultra 专属动作 —— 特别注意【不要把 dropend / stand 加进来】，
那是所有落地共用的收尾（和 fallend 那次一个坑）。
"""
import io

AS = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Modules\Es\EsActionSpeed.cs"
PL = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Plugin.cs"
CFG = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\BepInEx\config\ace.bbee.jspatch.cfg"

ADD = ("UltraDashEX,UltraDash,UltraDashAirEX,UltraDashAir,UltraDAend,UDdrop")

# 1) GroupCount 6 -> 7
s = io.open(AS, encoding="utf-8").read()
a = "    internal const int GroupCount = 6;"
b = "    internal const int GroupCount = 7;"
assert a in s, "GroupCount"
io.open(AS, "w", encoding="utf-8").write(s.replace(a, b, 1))

# 2) 绑定 Group7（挂在 Group6 那段后面）
s = io.open(PL, encoding="utf-8").read()
anchor = 'EsActionSpeed.CfgGroups[5] = Config.Bind(asec, "Group6", '
i = s.index(anchor)
# 找到 Group6 那个 Config.Bind 语句的结束（下一个以 ");" 结尾的行）
j = s.index("\n);", i) + 3
head = s[:j]
tail = s[j:]
new = ('\n            EsActionSpeed.CfgGroups[6] = Config.Bind(asec, "Group7",\n'
       '                "1.5 | %s",\n'
       '                "【倍率 | 动作名单】同上。\\n" +\n'
       '                "Group7 = 贝德维尔(Ultra)。\\n" +\n'
       '                "只列 Ultra 专属动作 —— 千万不要把 dropend / stand 放进来,\\n" +\n'
       '                "那是所有落地共用的收尾动作(和 fallend 那次同一个坑)。");\n' % ADD)
s = head + new + tail
io.open(PL, "w", encoding="utf-8").write(s)

# 3) cfg
lines = io.open(CFG, encoding="utf-8").read().split("\n")
last = max(i for i, l in enumerate(lines) if l.startswith("Group6 = "))
lines.insert(last + 1, "Group7 = 1.5 | " + ADD)
io.open(CFG, "w", encoding="utf-8").write("\n".join(lines))
print("ok")
