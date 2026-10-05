# -*- coding: utf-8 -*-
"""补上 Group7 的绑定（上一版锚点找错：各组的说明文案是共用的，不能靠它定位）。"""
import io

PL = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Plugin.cs"
CFG = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\BepInEx\config\ace.bbee.jspatch.cfg"
ADD = "UltraDashEX,UltraDash,UltraDashAirEX,UltraDashAir,UltraDAend,UDdrop"

lines = io.open(PL, encoding="utf-8").read().split("\n")
i = next(k for k, l in enumerate(lines) if "CfgGroups[5] = Config.Bind" in l)
# 从这个语句往后找它的结束行（说明文案的最后一行 + ");"）
j = next(k for k in range(i + 1, len(lines)) if lines[k].rstrip().endswith('");'))
NEW = [
    '            EsActionSpeed.CfgGroups[6] = Config.Bind(asec, "Group7", "1.5 | %s",' % ADD,
    '                "【倍率 | 动作名单】同上。Group7 = 贝德维尔(Ultra)。\\n" +',
    '                "只列 Ultra 专属动作 —— 千万不要把 dropend / stand 放进来,\\n" +',
    '                "那是所有落地共用的收尾动作(和 fallend 那次同一个坑)。");',
]
lines[j + 1:j + 1] = NEW
io.open(PL, "w", encoding="utf-8").write("\n".join(lines))

cl = io.open(CFG, encoding="utf-8").read().split("\n")
last = max(k for k, l in enumerate(cl) if l.startswith("Group6 = "))
if not any(l.startswith("Group7 = ") for l in cl):
    cl.insert(last + 1, "Group7 = 1.5 | " + ADD)
io.open(CFG, "w", encoding="utf-8").write("\n".join(cl))
print("ok")
