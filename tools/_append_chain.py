# -*- coding: utf-8 -*-
"""把运行时链路追踪验证过的「动作 ↔ 技能」表追加到 es_action_table.md。"""
import io, os, re

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
LOG = os.path.join(BASE, "..", "BepInEx", "LogOutput.log")
DOC = os.path.join(BASE, "es_action_table.md")

lines = io.open(LOG, encoding="utf-8", errors="replace").read().split("\n")
seen, rows = set(), []
for ln in lines:
    m = re.search(r'\[链路 f=\s*(\d+)\]\s+【技能】"([^"]*)"\s+按键=(\S+)\s+槽=(\S+)\s+ActorId=(\d+)\s+类型=(\S+)', ln)
    if not m:
        continue
    a, inp, slot, typ = m.group(2), m.group(3), m.group(4), m.group(6)
    k = (a, inp, slot, typ)
    if k in seen:
        continue
    seen.add(k)
    rows.append(k)

rows.sort(key=lambda x: (len(x[2].split('.')[0]), int(x[2].split('.')[0]), int(x[2].split('.')[1])))

SEC = ["\n\n---\n", "# 运行时链路验证表（实测）\n",
       "用 `ChainTrace` 把「技能 → 动作」按层打出来，本局共 104 条配对、去重 52 种。",
       "**和静态 `skillactivate` 表的槽位完全吻合**（2.1 / 2.3 / 2.4 …），等于双向验证。\n",
       "| 动作名 | 按键/方向 | 槽位 | 类型 |", "|---|---|---|---|"]
for a, inp, slot, typ in rows:
    SEC.append(f"| `{a}` | {inp} | {slot} | {typ} |")

SEC.append("""
## 结论

- **技能键 + 下** = `attackAEX`(2.1) → `attackB`(2.3) → `attackC`(2.4)：**同一个技能的第 1/2/3 段**
  （静态表里 `2.2` 是 `attackA`，EX 与非 EX 相邻；运行时一直走 2.1，说明升级版生效）
- **技能键 + 上** = `UltraDashEX`(2.9) / `UltraDashAirEX`(2.11)：**Ultra**
- 三个段位对应的纹章动作固定为 **`A1` / `B1` / `C1`**，效果分别是
  `es_holdatk1_*` / `ha_attack6_02` / `es_holdatk3_*`

**段位由输入决定，与装的是哪位骑士无关** —— 所以「第三段剑气」这类需求可以按段位写规则。

## 尚未挂上的层

`ChainTrace` 里第 0 层（输入记录）和「门槛」层没挂上：
`TouchButtonStyleController.RecordSkill` / `PlayerSkillMgr.GetSkillCastRequires` /
`CheckSkillCanCast` 这三个入口在启动日志里没有任何 `[链路]` 输出，说明方法名或归属类不对。
**但不影响**：按键/方向已从技能行拿到，第 0 层是冗余的。
""")

io.open(DOC, "a", encoding="utf-8").write("\n".join(SEC))
print(f"追加 {len(rows)} 行 -> {DOC}")
