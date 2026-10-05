# -*- coding: utf-8 -*-
"""把实测得到的「玩家动作 → 纹章 action → 特效」映射追加到 es_action_table.md。"""
import io, os

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
P = os.path.join(BASE, "es_action_table.md")

SEC = """

---

# 实测映射表（游戏内跑出来的）

来源：`ActionJournal`（记录 `ActionMgr.ChangeAction`）与 `BulletProbe`（记录 `BulletMgr.createBulletImp`）
合并时间线得出。跑法：装满圆桌骑士潜能，各招式按一遍。

## 玩家动作 → 纹章 bullet_action

| 玩家动作（输入） | 纹章 action | 说明 |
|---|---|---|
| `attackA` / `attackAEX` / `attackA_AirEX` | **`A1`** | 技能槽 A |
| `attackB` / `attackB_Air` | **`B1`** | 技能槽 B |
| `attackC` / `attackC_Air` | **`C1`** | 技能槽 C |
| `attackD1` / `attackD2` / `attackD3` | `aD12` / `aD22` / `aD32` | 下+攻击，三段 |
| `AttackUp` | `aup` → `aup2` | 上挑 |
| `attackholdDashEX` | `dashSkill` | 蓄力冲刺技 |
| `attackhold_hit` | `dash`, `dashSkill2` | 蓄力命中 |
| `attackhold_hit2` | `AD_hit` | 蓄力命中第二段 |
| `dash` | `dash` | 地面冲刺 |
| `dashAirAtk` / `dashAirAtkG` / `dashAirAtk0` | `dashAir`, `dashA`×3 | 空中冲刺攻击 |
| `rushUp` | `xup2`, `dash2` | |
| `jump2` | `jump` | 跳跃（潜能效果） |
| `atkAirX` | `attackAir2` | |
| `UltraDashEX` | `UD` | Ultra |
| `UltraDashAirEX` | `UDA0` | Ultra |
| `UltraDAend` | `UDA`, `UDEX` | Ultra 收招 |

## 纹章 action → 特效

| action | 特效 |
|---|---|
| `A1` → `A12` | `Role/Es/es_holdatk1_02` |
| `B1` → `B12` | `Role/ha/ha_attack6_02` + `Role/Es/es_holdatk1_02` |
| `C1` → `C12` | `Role/ha/ha_attack6_02` + `Role/Es/es_holdatk3_02` |
| `dashSkill` | `Role/Es/es_attackAir_02` |
| `dashSkill2` | `Role/Es/es_attackAir_02a` |
| `UD` / `UDA` | `Role/Es/es_AH_01`（贝德维尔的翅膀） |
| `UDEX` | `Role/Es/es_AH_03` |
| `x1` / `x2` | `Role/Es/es_holdFull_01`（纹章解放原地环） |
| `x3` / `x32` / `xup` / `xup2` | `Role/Es/es_holdRelease_01/02`（释放环） |

## ★ 关键结论

**技能槽 A/B/C 各有独立的纹章动作 `A1`/`B1`/`C1`。**

这一点很重要 —— 它意味着虽然 `attackA/B/C` 这个**玩家动作名**分不出是哪位骑士
（潜能只是切换了同一槽位的动作映射），但**生成出来的纹章动作是分得开的**：

```
attackA → A1     attackB → B1     attackC → C1
```

所以那几条跨技能的设计（崔斯坦三段放布鲁诺剑气…）**可以按槽位来写规则**，
只要技能装法固定（例如崔斯坦固定装 A 槽、布鲁诺固定装 B 槽）。

**尚未确定**：槽位 A/B/C ↔ 具体哪位圆桌骑士。
需要「装的时候记一笔」，或者给 `PlayerSkill` 加个探针打出技能 id（340081=崔斯坦 / 340281=布鲁诺 …）。

**附带线索**：槽 B/C 的纹章引用了 `Role/ha/ha_attack6_02` —— `ha` 是一个**完整的角色资源**
（302 个特效，带 `haskin_01/06/13` 皮肤），不是 ES 自己的。也就是说这两位骑士的纹章
借用了另一个角色的资源。`ha` 具体是谁待确认。
"""

io.open(P, "a", encoding="utf-8").write(SEC)
print("appended ->", P)
