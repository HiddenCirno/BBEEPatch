# -*- coding: utf-8 -*-
"""把「输入 → 招式」表追加到 es_action_table.md。"""
import io, os

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
P = os.path.join(BASE, "es_action_table.md")

SEC = """

---

# 输入 → 招式（静态可查，不用实测）

来源：`skillactivate` 表的 `f8`(按键) + `f52`(方向)。
`f52` 的取值映射是用运行时探针实测的 6 个值反推出来的：**1=Any 2=Up 3=Down**
（attackD1=Down→3、dashAir=Any→1、dashAtk0=Up→2、attackAEX=Down→3、jump=Any→1、fallmEX0=Down→3）。

生成脚本：`tools/_inputmap2.py` → `tools/_inputmap2_out.txt`

| 按键 | 方向 | 招式 | 说明 |
|---|---|---|---|
| **Skill** | **Down** | `attackAEX` / `attackA` / `attackB` / `attackC` | **技能键+下：第 1/2/3 段** |
| Skill | Down | `attackA_AirEX` / `attackA_Air` / `attackB_Air` / `attackC_Air` | 空中版 |
| **Skill** | **Up** | `UltraDashEX` / `UltraDash` / `UltraDashAirEX` / `UltraDashAir` | **Ultra = 技能键+上** |
| Skill | Any | `holdEX` / `hold` / `attackAir` / `attackAir2`(+EX) | 纹章解放 / 空中技能 |
| Attack | Any | `attack1`~`attack4`、`atkAirX`、`atkAir3`、`attackhold`、`attackhold21` | 普攻四段 |
| Attack | Down | `attackD1` / `attackD2` / `attackD3` | 下段普攻 |
| Attack | Up | `AttackUp` / `AttackUp2` | 上挑 |
| Dash | Any | `dashend` / `dashAir` / `dashAir2` / `dashAir3` / `rush` / `rushUp` | |
| Dash | Up | `dashAtk(EX)` / `dashAirAtk(EX)` / `dashAAend(EX)` / `dashAAhit` / `dashAtkG` | 冲刺攻击 |
| Dash | Down | `attackholdDashEXQ` / `attackhold_hit` / `attackhold_hit2` | 蓄力冲刺 |
| Jump | Any | `jump2` / `jump3` | 二段/三段跳 |
| **Summon** | **Up** | `dashAtk0` / `dashAirAtk0` | 召唤通道 |
| Summon | Down | `fallmEX0` | |
| Summon | Any | `summon` | |
| (无/派生) | Down | `fall` / `fallup*` / `fallm*` / `atkAir12` 等 | 落地/受身状态机 |

## ★ 对设计目标的直接意义

「输入 → 招式 → 纹章」这条链现在是**贯通**的：

```
技能键+下 第1段 → attackA → 纹章 A1 → 特效 es_holdatk1_*
技能键+下 第2段 → attackB → 纹章 B1 → 特效 ha_attack6_02 + es_holdatk1_02
技能键+下 第3段 → attackC → 纹章 C1 → 特效 es_holdatk3_*
```

**第 3 段永远 = `attackC` / `C1`，与装的是哪位骑士无关**（段位由输入决定）。
所以「第三段剑气」这类需求可以**按段位写规则**，不必先认出骑士是谁。

（待最终确认：换成崔斯坦按同样的技能键+下三下，第三段的纹章是否仍是 `C1`。若确认，②③⑤三条设计即可开写。）
"""

io.open(P, "a", encoding="utf-8").write(SEC)
print("appended ->", P)
