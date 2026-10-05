# ES (actorId=103401) 技能链前驱图

数据源: `extracted/skillactivatefixedpoint.ab.bin` (348,579 字节) —— bundle `data/xlsxfixed/skillactivatefixedpointwrap.ab`, 容器 `Assets/AssetBundle/Data/XlsxFixed/SkillActivateFixedPointWrap.bytes`。
该资产是运行时真正加载的表: `PlayerSkillMgr::InitSkills` (RVA 0x1bd9ee0) 反汇编中调用 `Xlsx::get_SkillActivateFixedPoint`。格式为连续 `uint32(len)+protobuf`。
消息字段号取自 `dump/dump.cs` 的 `SkillActivate` (line 76736, TypeDefIndex 921) 与
`SkillActivateFixedPoint` (line 143365, TypeDefIndex 1721) —— 两者字段号完全一致, 本资产是 FixedPoint(long) 版。

## 1. 字段号 → 名称 映射 (dump.cs)

| field# | name | 类型 |
|---|---|---|
| 2 | ActorId | uint |
| 3 | Group | int (PlayerSkillGroup) |
| 4 | Order | int |
| 5 | Action | string |
| 6 | StartTrigger | repeated string |
| 7 | ExitTrigger | repeated string |
| 8 | Input | string (InputCmd 名) |
| 9 | InputDir | SkillInputDirType (enum) |
| 10 | ReqTriggerId | repeated int |
| 11 | Mps | repeated SkillMpSlot |
| **15** | **PreSkillOrder** | **repeated int (LIST!)** |
| 16 | AllowActiveState | bool |
| 17 | AllowPassiveState | bool |
| 18 | AllowGround | bool |
| 19 | AllowFlying | bool |
| 21 | ActdurStrict | float(SkillActivate) / long(SkillActivateFixedPoint) |
| 22 | Timeout | float / long |
| 24 | Mutelist | string |
| 25 | Preinputtime | float / long |
| 26 | UseLongPress | bool |
| 27 | LongPressStart | float / long |
| 28 | LongPressEnd | float / long |
| 51 | ActionpointInputTag | int |
| 52 | ActionpointInputTag2 | int |

枚举 `SkillInputDirType` (dump.cs line 69676): Any=0, Up=1, Down=2, Front=3, Back=4, NoDir=5, Left=6, Right=7, AnyX=8。
枚举 `PlayerSkillGroup` (dump.cs line ~TypeDefIndex 3738): None=0, Attack=1, Skill1=2, Ultra=3, AttackAir=4, Dash=5, DashAttack=6, Jump=7, LongAttack=8, Summon=11, Burst=12。

注: 早先 `tools/_finalmap.py` 把 **f52 (ActionpointInputTag2)** 当成方向列, 那是错的; 方向列是 **f9 (InputDir)**。两者数值不同 (如 attack1: f9 缺省=0=Any, f52=1)。本表一律用 f9。

**字段编码实测**: f21=tagv(x9), f22=tagv(x9), f25=tagv(x19) → f21/f22/f25 是 **varint long(wt0)**, 值为 Fp 定点数 (除以 2^32 得浮点, 例: attackB 的 f25=1288490240 → 0.3)。姊妹资产 `skillactivate.ab.bin` (`Xlsx/SkillActivate.bytes`, 编辑器 float 版) 与它字段号、行数、动作名、前驱完全一致, 仅 f21/22/25 存成 4 字节 float。

ES 共 97 行 (去重前), 91 个不同 (group,order)。

## 2. 完整行表 (按 group, order 排序)

action/input 为内部名; inpDir=D 即输入方向; pre=preSkillOrder 全列表; req=reqTriggerId; AD=ActdurStrict; PI=preinputtime; TO=timeout; LP=useLongPress。

### group=1 (Attack) — 13 行

| order | action | input | inpDir | AD | PI | TO | preSkillOrder | reqTriggerId | useLongPress |
|---|---|---|---|---|---|---|---|---|---|
| 1 | AttackUp | Attack | Up |  |  |  | **(空→入口)** | [5602] | 0 |
| 2 | AttackUp | Attack | Up |  | 0.25 |  | [4,5,6,8,9,10,11,12] | [5602] | 0 |
| 3 | AttackUp2 | Attack | Up |  | 0.25 |  | [4,5,6,7,8,9,10,11,12] | [5603] | 0 |
| 4 | attackD1 | Attack | Down | 0.35 |  | 0.7 | **(空→入口)** | [5600] | 0 |
| 5 | attackD1 | Attack | Down | 0.35 |  | 0.7 | [9] | [5600] | 0 |
| 6 | attackD2 | Attack | Down | 0.35 | 0.3 | 0.75 | [4,5,10] | [5600] | 0 |
| 7 | attackD3 | Attack | Down | 0.3 | 0.3 | 0.65 | [6,11] | [5600] | 0 |
| 8 | atkAirX | Attack | Down |  | 0.2 |  | [7] | [5600] | 0 |
| 9 | attack1 | Attack | Any | 0.3 |  | 0.7 | **(空→入口)** |  | 0 |
| 10 | attack2 | Attack | Any | 0.3 | 0.2 | 0.6 | [9] |  | 0 |
| 11 | attack3 | Attack | Any | 0.35 | 0.2 | 0.8 | [10,4,5] |  | 0 |
| 12 | attack4 | Attack | Any |  | 0.2 |  | [11,6] |  | 0 |
| 13 | atkAirX | Attack | Any |  | 0.2 |  | [7] | [5600] | 0 |

### group=2 (Skill1) — 19 行

| order | action | input | inpDir | AD | PI | TO | preSkillOrder | reqTriggerId | useLongPress |
|---|---|---|---|---|---|---|---|---|---|
| 1 | attackAEX | Skill | Down |  |  |  | **(空→入口)** | [5607,5626] | 0 |
| 2 | attackA | Skill | Down |  |  |  | **(空→入口)** | [5607] | 0 |
| 3 | attackB | Skill | Down |  | 0.3 |  | **(空→入口)** | [5607] | 0 |
| 4 | attackC | Skill | Down |  | 0.3 |  | **(空→入口)** | [5607] | 0 |
| 5 | attackA_AirEX | Skill | Down |  |  |  | **(空→入口)** | [5607,5626] | 0 |
| 6 | attackA_Air | Skill | Down |  |  |  | **(空→入口)** | [5607] | 0 |
| 7 | attackB_Air | Skill | Down |  | 0.3 |  | **(空→入口)** | [5607] | 0 |
| 8 | attackC_Air | Skill | Down |  | 0.3 |  | **(空→入口)** | [5607] | 0 |
| 9 | UltraDashEX | Skill | Up |  |  |  | **(空→入口)** | [5608,5609] | 0 |
| 10 | UltraDash | Skill | Up |  |  |  | **(空→入口)** | [5608] | 0 |
| 11 | UltraDashAirEX | Skill | Up |  |  |  | **(空→入口)** | [5608,5609] | 0 |
| 12 | UltraDashAir | Skill | Up |  |  |  | **(空→入口)** | [5608] | 0 |
| 13 | UltraDAend |  | Any |  |  |  | **(空→入口)** | [5608] | 0 |
| 14 | holdEX | Skill | Any |  |  |  | **(空→入口)** | [5626] | 0 |
| 15 | hold | Skill | Any |  |  |  | **(空→入口)** |  | 0 |
| 16 | attackAir2EX | Skill | Any |  |  |  | **(空→入口)** | [5606,5626] | 0 |
| 17 | attackAir2 | Skill | Any |  |  |  | **(空→入口)** | [5606] | 0 |
| 18 | attackAirEX | Skill | Any |  |  |  | **(空→入口)** | [5626] | 0 |
| 19 | attackAir | Skill | Any |  |  |  | **(空→入口)** |  | 0 |

### group=3 (Ultra) — 1 行

| order | action | input | inpDir | AD | PI | TO | preSkillOrder | reqTriggerId | useLongPress |
|---|---|---|---|---|---|---|---|---|---|
| 1 |  |  | Any |  |  |  | **(空→入口)** | [5608] | 0 |

### group=4 (AttackAir) — 16 行

| order | action | input | inpDir | AD | PI | TO | preSkillOrder | reqTriggerId | useLongPress |
|---|---|---|---|---|---|---|---|---|---|
| 1 | fall |  | Any |  |  |  | **(空→入口)** |  | 0 |
| 2 | fallupdd |  | Any |  |  |  | [2] |  | 0 |
| 3 | fallupd |  | Any |  |  |  | [2] |  | 0 |
| 4 | fallup22 |  | Any |  |  |  | [3,4] | [5613] | 0 |
| 5 | fallup2dd |  | Any |  |  |  | [3,4] |  | 0 |
| 6 | fallup2d |  | Any |  |  |  | [3,4] | [5613] | 0 |
| 7 | fallend |  | Any |  |  |  | **(空→入口)** |  | 0 |
| 8 | fallend2 |  | Any |  |  |  | **(空→入口)** | [5613] | 0 |
| 9 | fallm |  | Any |  |  |  | [6,7] | [5613] | 0 |
| 10 | fallmdown |  | Any |  |  |  | **(空→入口)** | [5613] | 0 |
| 11 | fallmdownend |  | Any |  |  |  | **(空→入口)** | [5613] | 0 |
| 12 | atkAir12 |  | Any |  |  |  | **(空→入口)** |  | 0 |
| 13 | AttackUp2 | Attack | Up |  |  |  | **(空→入口)** | [5603] | 0 |
| 14 | atkAir3 | Attack | Any |  |  |  | [12] |  | 0 |
| 15 | fallmdownendEX |  | Any |  |  |  | **(空→入口)** | [5613,5612,5614,5625] | 0 |
| 16 | fallmdownendEX2 |  | Any |  |  |  | **(空→入口)** | [5613,5612,5614,5625] | 0 |

### group=5 (Dash) — 31 行

| order | action | input | inpDir | AD | PI | TO | preSkillOrder | reqTriggerId | useLongPress |
|---|---|---|---|---|---|---|---|---|---|
| 1 | dashAtkEX |  | Any |  |  |  | **(空→入口)** | [5617,5626] | 0 |
| 2 | dashAtk |  | Any |  |  |  | **(空→入口)** | [5617] | 0 |
| 3 | dashAirAtkEX |  | Any |  |  |  | **(空→入口)** | [5617,5626] | 0 |
| 4 | dashAirAtk |  | Any |  |  |  | **(空→入口)** | [5617] | 0 |
| 5 | attackholdDashEX | Dash | Down |  |  |  | **(空→入口)** | [5621,5626] | 0 |
| 5 | attackholdDashEX | Dash | Down |  |  |  | **(空→入口)** | [5621,5626,5624] | 0 |
| 5 | attackholdDashEX | Dash | Down |  |  |  | **(空→入口)** | [5621,5623,5626] | 0 |
| 5 | attackholdDashEX | Dash | Down |  |  |  | **(空→入口)** | [5621,5623,5626,5624] | 0 |
| 6 | attackholdDash | Dash | Down |  |  |  | **(空→入口)** | [5621] | 0 |
| 6 | attackholdDash | Dash | Down |  |  |  | **(空→入口)** | [5621,5624] | 0 |
| 6 | attackholdDash | Dash | Down |  |  |  | **(空→入口)** | [5621,5623] | 0 |
| 6 | attackholdDash | Dash | Down |  |  |  | **(空→入口)** | [5621,5623,5624] | 0 |
| 7 | attackholdDashEXQ |  | Any |  |  |  | **(空→入口)** | [5622] | 0 |
| 8 | dash | Dash | Any | 0.3 | 0.2 | 0.9 | **(空→入口)** |  | 0 |
| 9 | dash2 | Dash | Any | 0.3 | 0.2 | 0.9 | [8,12] | [1180] | 0 |
| 10 | dash3 | Dash | Any |  | 0.2 |  | [9,13] | [1180] | 0 |
| 11 | dashend |  | Any |  |  |  | **(空→入口)** |  | 0 |
| 12 | dashAir |  | Any |  | 0.2 |  | **(空→入口)** |  | 0 |
| 13 | dashAir2 |  | Any |  | 0.2 |  | **(空→入口)** | [1180] | 0 |
| 14 | dashAir3 |  | Any |  | 0.2 |  | **(空→入口)** | [1180] | 0 |
| 15 | attackhold_hit |  | Any |  |  |  | **(空→入口)** | [5621] | 0 |
| 16 | attackhold_hit2 |  | Any |  |  |  | **(空→入口)** | [5621] | 0 |
| 17 | attackhold_hit2Air |  | Any |  |  |  | **(空→入口)** | [5621] | 0 |
| 18 | dashAAhit |  | Any |  |  |  | **(空→入口)** | [5617] | 0 |
| 19 | dashAAendEX |  | Any |  |  |  | **(空→入口)** | [5617,5618,5619,5620] | 0 |
| 20 | dashAAend |  | Any |  |  |  | **(空→入口)** | [5617] | 0 |
| 21 | dashAAendX |  | Any |  |  |  | **(空→入口)** | [5617] | 0 |
| 22 | dashAirAtkGEX |  | Any |  |  |  | **(空→入口)** | [5617,5620,5626] | 0 |
| 23 | dashAirAtkG |  | Any |  |  |  | **(空→入口)** | [5617,5620] | 0 |
| 24 | dashAtkGEX |  | Any |  |  |  | **(空→入口)** | [5617,5620,5626] | 0 |
| 25 | dashAtkG |  | Any |  |  |  | **(空→入口)** | [5617,5620] | 0 |

### group=6 (DashAttack) — 2 行

| order | action | input | inpDir | AD | PI | TO | preSkillOrder | reqTriggerId | useLongPress |
|---|---|---|---|---|---|---|---|---|---|
| 1 | rush |  | Any |  |  |  | **(空→入口)** |  | 0 |
| 2 | rushUp |  | Any |  |  |  | **(空→入口)** | [5602] | 0 |

### group=7 (Jump) — 5 行

| order | action | input | inpDir | AD | PI | TO | preSkillOrder | reqTriggerId | useLongPress |
|---|---|---|---|---|---|---|---|---|---|
| 1 | jump_down | Jump | Down |  |  |  | **(空→入口)** |  | 0 |
| 2 | drop_down | Jump | Down |  |  |  | **(空→入口)** |  | 0 |
| 3 | jump | Jump | Any |  |  |  | **(空→入口)** |  | 0 |
| 4 | jump2 |  | Any |  |  |  | **(空→入口)** |  | 0 |
| 5 | jump3 |  | Any |  |  |  | **(空→入口)** | [1174] | 0 |

### group=8 (LongAttack) — 4 行

| order | action | input | inpDir | AD | PI | TO | preSkillOrder | reqTriggerId | useLongPress |
|---|---|---|---|---|---|---|---|---|---|
| 1 | attackhold21 | Attack | Any |  |  |  | **(空→入口)** | [5601] | 1 |
| 2 | attackhold22_loop |  | Any |  |  |  | **(空→入口)** |  | 0 |
| 3 | attackhold23 |  | Any |  |  |  | **(空→入口)** |  | 0 |
| 4 | attackhold | Attack | Any |  |  |  | **(空→入口)** |  | 1 |

### group=11 (Summon) — 6 行

| order | action | input | inpDir | AD | PI | TO | preSkillOrder | reqTriggerId | useLongPress |
|---|---|---|---|---|---|---|---|---|---|
| 1 | dashAtk0 | Summon | Up |  |  |  | **(空→入口)** | [5617] | 0 |
| 2 | dashAirAtk0 | Summon | Up |  |  |  | **(空→入口)** | [5617] | 0 |
| 3 | fallmEX0 | Summon | Down |  |  |  | **(空→入口)** | [5613,5612,5614,5625] | 0 |
| 4 | falldownEX0 |  | Any |  |  |  | **(空→入口)** | [5613,5612,5614,5625] | 0 |
| 5 | fallmEX0Air |  | Any |  |  |  | **(空→入口)** | [5613,5612,5614,5625] | 0 |
| 6 | summon | Summon | Any |  |  |  | **(空→入口)** |  | 0 |

## 3. 有向图 (同一 group 内)

语义 (来自 dump.cs `PlayerSkillMgr`): `preSkillOrder` 是**前驱 order 列表**。
`findNextSkillMatchPreOrderAndInputDir(startOrder)` 查找 `startOrder ∈ row.preSkillOrder` 的行 —— 
即「刚做完 order=startOrder 的技能后, 哪些技能可以接上」。
下文 `order X → order Y  (name_X → name_Y)` 表示 Y.preSkillOrder 含 X。

### group=1 (Attack)

前驱列表 (行 ← 其 preSkillOrder):

- `1` **AttackUp** ← (空 = 入口)
- `2` **AttackUp** ← 4:attackD1, 5:attackD1, 6:attackD2, 8:atkAirX, 9:attack1, 10:attack2, 11:attack3, 12:attack4
- `3` **AttackUp2** ← 4:attackD1, 5:attackD1, 6:attackD2, 7:attackD3, 8:atkAirX, 9:attack1, 10:attack2, 11:attack3, 12:attack4
- `4` **attackD1** ← (空 = 入口)
- `5` **attackD1** ← 9:attack1
- `6` **attackD2** ← 4:attackD1, 5:attackD1, 10:attack2
- `7` **attackD3** ← 6:attackD2, 11:attack3
- `8` **atkAirX** ← 7:attackD3
- `9` **attack1** ← (空 = 入口)
- `10` **attack2** ← 9:attack1
- `11` **attack3** ← 4:attackD1, 5:attackD1, 10:attack2
- `12` **attack4** ← 6:attackD2, 11:attack3
- `13` **atkAirX** ← 7:attackD3

后继边 (哪些行的 preSkillOrder 里包含本 order):

- `4:attackD1` → `2:AttackUp`
- `4:attackD1` → `3:AttackUp2`
- `4:attackD1` → `6:attackD2`
- `4:attackD1` → `11:attack3`
- `5:attackD1` → `2:AttackUp`
- `5:attackD1` → `3:AttackUp2`
- `5:attackD1` → `6:attackD2`
- `5:attackD1` → `11:attack3`
- `6:attackD2` → `2:AttackUp`
- `6:attackD2` → `3:AttackUp2`
- `6:attackD2` → `7:attackD3`
- `6:attackD2` → `12:attack4`
- `7:attackD3` → `3:AttackUp2`
- `7:attackD3` → `8:atkAirX`
- `7:attackD3` → `13:atkAirX`
- `8:atkAirX` → `2:AttackUp`
- `8:atkAirX` → `3:AttackUp2`
- `9:attack1` → `2:AttackUp`
- `9:attack1` → `3:AttackUp2`
- `9:attack1` → `5:attackD1`
- `9:attack1` → `10:attack2`
- `10:attack2` → `2:AttackUp`
- `10:attack2` → `3:AttackUp2`
- `10:attack2` → `6:attackD2`
- `10:attack2` → `11:attack3`
- `11:attack3` → `2:AttackUp`
- `11:attack3` → `3:AttackUp2`
- `11:attack3` → `7:attackD3`
- `11:attack3` → `12:attack4`
- `12:attack4` → `2:AttackUp`
- `12:attack4` → `3:AttackUp2`

### group=2 (Skill1)

前驱列表 (行 ← 其 preSkillOrder):

- `1` **attackAEX** ← (空 = 入口)
- `2` **attackA** ← (空 = 入口)
- `3` **attackB** ← (空 = 入口)
- `4` **attackC** ← (空 = 入口)
- `5` **attackA_AirEX** ← (空 = 入口)
- `6` **attackA_Air** ← (空 = 入口)
- `7` **attackB_Air** ← (空 = 入口)
- `8` **attackC_Air** ← (空 = 入口)
- `9` **UltraDashEX** ← (空 = 入口)
- `10` **UltraDash** ← (空 = 入口)
- `11` **UltraDashAirEX** ← (空 = 入口)
- `12` **UltraDashAir** ← (空 = 入口)
- `13` **UltraDAend** ← (空 = 入口)
- `14` **holdEX** ← (空 = 入口)
- `15` **hold** ← (空 = 入口)
- `16` **attackAir2EX** ← (空 = 入口)
- `17` **attackAir2** ← (空 = 入口)
- `18` **attackAirEX** ← (空 = 入口)
- `19` **attackAir** ← (空 = 入口)

后继边 (哪些行的 preSkillOrder 里包含本 order):

- (无任何前驱关系 —— 所有行都是入口/独立输入触发)

### group=3 (Ultra)

前驱列表 (行 ← 其 preSkillOrder):

- `1` **** ← (空 = 入口)

后继边 (哪些行的 preSkillOrder 里包含本 order):

- (无任何前驱关系 —— 所有行都是入口/独立输入触发)

### group=4 (AttackAir)

前驱列表 (行 ← 其 preSkillOrder):

- `1` **fall** ← (空 = 入口)
- `2` **fallupdd** ← 2:fallupdd
- `3` **fallupd** ← 2:fallupdd
- `4` **fallup22** ← 3:fallupd, 4:fallup22
- `5` **fallup2dd** ← 3:fallupd, 4:fallup22
- `6` **fallup2d** ← 3:fallupd, 4:fallup22
- `7` **fallend** ← (空 = 入口)
- `8` **fallend2** ← (空 = 入口)
- `9` **fallm** ← 6:fallup2d, 7:fallend
- `10` **fallmdown** ← (空 = 入口)
- `11` **fallmdownend** ← (空 = 入口)
- `12` **atkAir12** ← (空 = 入口)
- `13` **AttackUp2** ← (空 = 入口)
- `14` **atkAir3** ← 12:atkAir12
- `15` **fallmdownendEX** ← (空 = 入口)
- `16` **fallmdownendEX2** ← (空 = 入口)

后继边 (哪些行的 preSkillOrder 里包含本 order):

- `2:fallupdd` → `2:fallupdd`
- `2:fallupdd` → `3:fallupd`
- `3:fallupd` → `4:fallup22`
- `3:fallupd` → `5:fallup2dd`
- `3:fallupd` → `6:fallup2d`
- `4:fallup22` → `4:fallup22`
- `4:fallup22` → `5:fallup2dd`
- `4:fallup22` → `6:fallup2d`
- `6:fallup2d` → `9:fallm`
- `7:fallend` → `9:fallm`
- `12:atkAir12` → `14:atkAir3`

### group=5 (Dash)

前驱列表 (行 ← 其 preSkillOrder):

- `1` **dashAtkEX** ← (空 = 入口)
- `2` **dashAtk** ← (空 = 入口)
- `3` **dashAirAtkEX** ← (空 = 入口)
- `4` **dashAirAtk** ← (空 = 入口)
- `5` **attackholdDashEX** ← (空 = 入口)
- `6` **attackholdDash** ← (空 = 入口)
- `7` **attackholdDashEXQ** ← (空 = 入口)
- `8` **dash** ← (空 = 入口)
- `9` **dash2** ← 8:dash, 12:dashAir
- `10` **dash3** ← 9:dash2, 13:dashAir2
- `11` **dashend** ← (空 = 入口)
- `12` **dashAir** ← (空 = 入口)
- `13` **dashAir2** ← (空 = 入口)
- `14` **dashAir3** ← (空 = 入口)
- `15` **attackhold_hit** ← (空 = 入口)
- `16` **attackhold_hit2** ← (空 = 入口)
- `17` **attackhold_hit2Air** ← (空 = 入口)
- `18` **dashAAhit** ← (空 = 入口)
- `19` **dashAAendEX** ← (空 = 入口)
- `20` **dashAAend** ← (空 = 入口)
- `21` **dashAAendX** ← (空 = 入口)
- `22` **dashAirAtkGEX** ← (空 = 入口)
- `23` **dashAirAtkG** ← (空 = 入口)
- `24` **dashAtkGEX** ← (空 = 入口)
- `25` **dashAtkG** ← (空 = 入口)

后继边 (哪些行的 preSkillOrder 里包含本 order):

- `8:dash` → `9:dash2`
- `9:dash2` → `10:dash3`
- `12:dashAir` → `9:dash2`
- `13:dashAir2` → `10:dash3`

### group=6 (DashAttack)

前驱列表 (行 ← 其 preSkillOrder):

- `1` **rush** ← (空 = 入口)
- `2` **rushUp** ← (空 = 入口)

后继边 (哪些行的 preSkillOrder 里包含本 order):

- (无任何前驱关系 —— 所有行都是入口/独立输入触发)

### group=7 (Jump)

前驱列表 (行 ← 其 preSkillOrder):

- `1` **jump_down** ← (空 = 入口)
- `2` **drop_down** ← (空 = 入口)
- `3` **jump** ← (空 = 入口)
- `4` **jump2** ← (空 = 入口)
- `5` **jump3** ← (空 = 入口)

后继边 (哪些行的 preSkillOrder 里包含本 order):

- (无任何前驱关系 —— 所有行都是入口/独立输入触发)

### group=8 (LongAttack)

前驱列表 (行 ← 其 preSkillOrder):

- `1` **attackhold21** ← (空 = 入口)
- `2` **attackhold22_loop** ← (空 = 入口)
- `3` **attackhold23** ← (空 = 入口)
- `4` **attackhold** ← (空 = 入口)

后继边 (哪些行的 preSkillOrder 里包含本 order):

- (无任何前驱关系 —— 所有行都是入口/独立输入触发)

### group=11 (Summon)

前驱列表 (行 ← 其 preSkillOrder):

- `1` **dashAtk0** ← (空 = 入口)
- `2` **dashAirAtk0** ← (空 = 入口)
- `3` **fallmEX0** ← (空 = 入口)
- `4` **falldownEX0** ← (空 = 入口)
- `5` **fallmEX0Air** ← (空 = 入口)
- `6` **summon** ← (空 = 入口)

后继边 (哪些行的 preSkillOrder 里包含本 order):

- (无任何前驱关系 —— 所有行都是入口/独立输入触发)

## 4. 前驱为空的「入口技能」

| group | order | action | input | inpDir | reqTriggerId |
|---|---|---|---|---|---|
| 1 | 1 | AttackUp | Attack | Up | 5602 |
| 1 | 4 | attackD1 | Attack | Down | 5600 |
| 1 | 9 | attack1 | Attack | Any |  |
| 2 | 1 | attackAEX | Skill | Down | 5607,5626 |
| 2 | 2 | attackA | Skill | Down | 5607 |
| 2 | 3 | attackB | Skill | Down | 5607 |
| 2 | 4 | attackC | Skill | Down | 5607 |
| 2 | 5 | attackA_AirEX | Skill | Down | 5607,5626 |
| 2 | 6 | attackA_Air | Skill | Down | 5607 |
| 2 | 7 | attackB_Air | Skill | Down | 5607 |
| 2 | 8 | attackC_Air | Skill | Down | 5607 |
| 2 | 9 | UltraDashEX | Skill | Up | 5608,5609 |
| 2 | 10 | UltraDash | Skill | Up | 5608 |
| 2 | 11 | UltraDashAirEX | Skill | Up | 5608,5609 |
| 2 | 12 | UltraDashAir | Skill | Up | 5608 |
| 2 | 13 | UltraDAend |  | Any | 5608 |
| 2 | 14 | holdEX | Skill | Any | 5626 |
| 2 | 15 | hold | Skill | Any |  |
| 2 | 16 | attackAir2EX | Skill | Any | 5606,5626 |
| 2 | 17 | attackAir2 | Skill | Any | 5606 |
| 2 | 18 | attackAirEX | Skill | Any | 5626 |
| 2 | 19 | attackAir | Skill | Any |  |
| 3 | 1 |  |  | Any | 5608 |
| 4 | 1 | fall |  | Any |  |
| 4 | 7 | fallend |  | Any |  |
| 4 | 8 | fallend2 |  | Any | 5613 |
| 4 | 10 | fallmdown |  | Any | 5613 |
| 4 | 11 | fallmdownend |  | Any | 5613 |
| 4 | 12 | atkAir12 |  | Any |  |
| 4 | 13 | AttackUp2 | Attack | Up | 5603 |
| 4 | 15 | fallmdownendEX |  | Any | 5613,5612,5614,5625 |
| 4 | 16 | fallmdownendEX2 |  | Any | 5613,5612,5614,5625 |
| 5 | 1 | dashAtkEX |  | Any | 5617,5626 |
| 5 | 2 | dashAtk |  | Any | 5617 |
| 5 | 3 | dashAirAtkEX |  | Any | 5617,5626 |
| 5 | 4 | dashAirAtk |  | Any | 5617 |
| 5 | 5 | attackholdDashEX | Dash | Down | 5621,5626 |
| 5 | 6 | attackholdDash | Dash | Down | 5621 |
| 5 | 7 | attackholdDashEXQ |  | Any | 5622 |
| 5 | 8 | dash | Dash | Any |  |
| 5 | 11 | dashend |  | Any |  |
| 5 | 12 | dashAir |  | Any |  |
| 5 | 13 | dashAir2 |  | Any | 1180 |
| 5 | 14 | dashAir3 |  | Any | 1180 |
| 5 | 15 | attackhold_hit |  | Any | 5621 |
| 5 | 16 | attackhold_hit2 |  | Any | 5621 |
| 5 | 17 | attackhold_hit2Air |  | Any | 5621 |
| 5 | 18 | dashAAhit |  | Any | 5617 |
| 5 | 19 | dashAAendEX |  | Any | 5617,5618,5619,5620 |
| 5 | 20 | dashAAend |  | Any | 5617 |
| 5 | 21 | dashAAendX |  | Any | 5617 |
| 5 | 22 | dashAirAtkGEX |  | Any | 5617,5620,5626 |
| 5 | 23 | dashAirAtkG |  | Any | 5617,5620 |
| 5 | 24 | dashAtkGEX |  | Any | 5617,5620,5626 |
| 5 | 25 | dashAtkG |  | Any | 5617,5620 |
| 6 | 1 | rush |  | Any |  |
| 6 | 2 | rushUp |  | Any | 5602 |
| 7 | 1 | jump_down | Jump | Down |  |
| 7 | 2 | drop_down | Jump | Down |  |
| 7 | 3 | jump | Jump | Any |  |
| 7 | 4 | jump2 |  | Any |  |
| 7 | 5 | jump3 |  | Any | 1174 |
| 8 | 1 | attackhold21 | Attack | Any | 5601 |
| 8 | 2 | attackhold22_loop |  | Any |  |
| 8 | 3 | attackhold23 |  | Any |  |
| 8 | 4 | attackhold | Attack | Any |  |
| 11 | 1 | dashAtk0 | Summon | Up | 5617 |
| 11 | 2 | dashAirAtk0 | Summon | Up | 5617 |
| 11 | 3 | fallmEX0 | Summon | Down | 5613,5612,5614,5625 |
| 11 | 4 | falldownEX0 |  | Any | 5613,5612,5614,5625 |
| 11 | 5 | fallmEX0Air |  | Any | 5613,5612,5614,5625 |
| 11 | 6 | summon | Summon | Any |  |

## 5. 定向问答

### 5a. group=1: attackD1/D2/D3 (Down) → attack2/3/4 (Any)?

原字节 (f15 / packed ints):

- order 4 `attackD1` input=Attack dir=Down preSkillOrder=[]
- order 5 `attackD1` input=Attack dir=Down preSkillOrder=[9]
- order 6 `attackD2` input=Attack dir=Down preSkillOrder=[4, 5, 10]
- order 7 `attackD3` input=Attack dir=Down preSkillOrder=[6, 11]
- order 10 `attack2` input=Attack dir=Any preSkillOrder=[9]
- order 11 `attack3` input=Attack dir=Any preSkillOrder=[4, 5, 10]
- order 12 `attack4` input=Attack dir=Any preSkillOrder=[6, 11]

判定 (按 `X.preSkillOrder` 含 `Y` ⇒ `Y → X`):

- **attackD1 → attack3 成立**: order 11 `attack3` (Any) 的 preSkillOrder=[10,4,5] 里含 4 和 5 (attackD1)。
- **attackD2 → attack4 成立**: order 12 `attack4` (Any) 的 preSkillOrder=[11,6] 里含 6 (attackD2)。
- attackD1 → attack2 **不成立**: order 10 `attack2` 的 preSkillOrder=[9] 只有 9 (attack1)。
- attackD2 → attack3 **不成立**; attackD3 (order7) 的 preSkillOrder=[6,11], 它接到的是 atkAirX(8/13), 不是平A。
- 反向也存在: order6 `attackD2` (Down) 的 preSkillOrder=[4,5,10] 含 10=attack2 (Any)。

=> 跨方向前驱确实存在, 例如 attack3(Any)←[10(Any),4(Down),5(Down)], attackD2(Down)←[4(Down),5(Down),10(Any)]。

### 5b. group=2 (Skill1): attackAEX/attackA/attackB/attackC 的前驱

- order 1 `attackAEX`: preSkillOrder=[] (空), reqTriggerId=[5607, 5626], dir=Down
- order 2 `attackA`: preSkillOrder=[] (空), reqTriggerId=[5607], dir=Down
- order 3 `attackB`: preSkillOrder=[] (空), reqTriggerId=[5607], dir=Down
- order 4 `attackC`: preSkillOrder=[] (空), reqTriggerId=[5607], dir=Down

=> **group=2 的 19 行 preSkillOrder 全为空** (见第 4 节)。按 `FindByPreSkillOrder` 的语义
(order==0 时走 `FindAStartingSkillForPredict`), 空列表 = 入口技能, 不受任何前驱约束。
因此 attackB 不存在「必须紧跟在 attackAEX 之后」的前驱关系; 四者是并列的入口, 由输入(Down+Skill)与 reqTriggerId 选择 (attackA/B/C 都 req=5607, attackAEX req={5607,5626})。
结论: 就 preSkillOrder 而言, **attackB 可以在 attackAEX 之后、也可以在其他事情之后被触发** —— 它根本没被前驱门控。

## 6. preSkillOrder 多元素 / 跨方向统计

- ES 中 preSkillOrder 非空的行: 19 行 (共 97 行)
- 其中 preSkillOrder 元素 >1 的行: 12 行
- 最长列表: group=1 order=3 `AttackUp2` → [4, 5, 6, 7, 8, 9, 10, 11, 12] (9 个)
- 前驱列表**跨输入方向分支**的行: 6 行

| group | order | action | preSkillOrder | 涉及方向 |
|---|---|---|---|---|
| 1 | 2 | AttackUp | [4,5,6,8,9,10,11,12] | Any,Down |
| 1 | 3 | AttackUp2 | [4,5,6,7,8,9,10,11,12] | Any,Down |
| 1 | 6 | attackD2 | [4,5,10] | Any,Down |
| 1 | 7 | attackD3 | [6,11] | Any,Down |
| 1 | 11 | attack3 | [4,5,10] | Any,Down |
| 1 | 12 | attack4 | [6,11] | Any,Down |

## 7. 数据源交叉核对

- FixedPoint 表 ES 行: 97; float 姊妹表 ES 行: 97
- (group,order,action,preSkillOrder) 逐行一致: True (完全一致)
