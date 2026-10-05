# ES（103401）原生动作表

## 来源

从 `extracted/skillactivate.ab` 的 TextAsset 静态解析得到（342,244 字节，全表 2438 条，其中 ES 97 条）。
格式是连续 `(uint32 len, protobuf)`。不必进游戏，随时可重新生成：`py tools/_gen_action_table.py`。

### 字段含义（推断 + 已交叉验证）

| 字段 | 含义 |
|---|---|
| f2 | 角色 id（103401 = ES） |
| f3 | **槽位**（见下表） |
| f4 | 槽位内序号 |
| f5 | **内部动作名**（就是 `ActionMgr` 里那个名字） |
| f6 / f7 | 起手 / 收招标记（`Start_N` / `End_N`） |
| f8 | **输入类型**：`Attack` / `Dash` / `Skill` / `Summon` / 空 |
| f9 | 标志位 |
| f10 | 名称 id（varint，如 11234 / 11239 / 11258）—— **在这些前缀里查不到本地化条目，用途待定** |
| f15 | 段数 |
| f24 | 帧数据 JSON，形如 `"all":0.7,"5":0.01` |
| f33 | UI 图标路径（`IconSkill_103401`） |

## 槽位

- **slot=1** — 普攻系（Attack 输入）（13 条）
- **slot=2** — 技能系（Skill 输入）（19 条）
- **slot=3** — ？（1 条，动作名为空）（1 条）
- **slot=4** — 空中 / 落地 / 受身系（16 条）
- **slot=5** — 冲刺系（Dash 输入）（31 条）
- **slot=6** — 突进（rush）（2 条）
- **slot=7** — 跳跃（5 条）
- **slot=8** — 蓄力系（长按）（4 条）
- **slot=11** — 召唤系（Summon 输入）（6 条）

## 全部条目

### slot=1 — 普攻系（Attack 输入）

| idx | 输入 | 动作名 | 段 | 帧数据 |
|---|---|---|---|---|
| 1 | Attack | `AttackUp` |  | "all":0.5,"5":0.01 |
| 2 | Attack | `AttackUp` |  | "all":0.5,"5":0.01 |
| 3 | Attack | `AttackUp2` |  | "all":0.5,"5":0.01 |
| 4 | Attack | `attackD1` |  | "all":0.35,"5":0.01,"7":0.3 |
| 5 | Attack | `attackD1` |  | "all":0.35,"5":0.01,"7":0.3 |
| 6 | Attack | `attackD2` |  | "all":0.35,"5":0.01,"7":0.3 |
| 7 | Attack | `attackD3` |  | "all":0.5,"1":0.3,"5":0.01,"7":0.35 |
| 8 | Attack | `atkAirX` |  | "all":0.28,"5":0.01,"8":0.75 |
| 9 | Attack | `attack1` |  | "all":0.3,"5":0.01,"7":0.3 |
| 10 | Attack | `attack2` |  | "all":0.35,"5":0.01,"7":0.3 |
| 11 | Attack | `attack3` |  | "all":0.35,"5":0.01,"7":0.32 |
| 12 | Attack | `attack4` |  | "all":0.4,"5":0.01,"8":0.75 |
| 13 | Attack | `atkAirX` |  | "all":0.28,"5":0.01,"8":0.75 |

### slot=2 — 技能系（Skill 输入）

| idx | 输入 | 动作名 | 段 | 帧数据 |
|---|---|---|---|---|
| 1 | Skill | `attackAEX` |  | "all":0.5,"1":0.7,"5":0.01 |
| 2 | Skill | `attackA` |  | "all":0.5,"1":0.7,"5":0.01 |
| 3 | Skill | `attackB` |  | "all":0.5,"1":0.8,"5":0.01 |
| 4 | Skill | `attackC` |  | "all":1.25,"4":1.4,"5":0.3,"11":0.3 |
| 5 | Skill | `attackA_AirEX` |  | "all":0.6,"2":0.45,"5":0.01,"7":0.45 |
| 6 | Skill | `attackA_Air` |  | "all":0.6,"2":0.45,"5":0.01,"7":0.45 |
| 7 | Skill | `attackB_Air` |  | "all":0.75,"2":0.55,"5":0.01,"7":0.45 |
| 8 | Skill | `attackC_Air` |  | "all":1,"5":0.01,"7":0.55,"11":1 |
| 9 | Skill | `UltraDashEX` |  | "all":0.55,"2":1 |
| 10 | Skill | `UltraDash` |  | "all":0.55,"2":1 |
| 11 | Skill | `UltraDashAirEX` |  | "all":2 |
| 12 | Skill | `UltraDashAir` |  | "all":2 |
| 13 |  | `UltraDAend` |  | "all":0.5 |
| 14 | Skill | `holdEX` |  | "all":0.7,"5":0.01 |
| 15 | Skill | `hold` |  | "all":0.7,"5":0.01 |
| 16 | Skill | `attackAir2EX` |  | "all":0.3,"5":0.01 |
| 17 | Skill | `attackAir2` |  | "all":0.3,"5":0.01 |
| 18 | Skill | `attackAirEX` |  | "all":0.3,"5":0.01 |
| 19 | Skill | `attackAir` |  | "all":0.3,"5":0.01 |

### slot=3 — ？（1 条，动作名为空）

| idx | 输入 | 动作名 | 段 | 帧数据 |
|---|---|---|---|---|
| 1 |  | `None` |  | None |

### slot=4 — 空中 / 落地 / 受身系

| idx | 输入 | 动作名 | 段 | 帧数据 |
|---|---|---|---|---|
| 1 |  | `fall` |  | "all":0.1,"1":2,"4":2,"5":0.5,"11":0.5 |
| 2 |  | `fallupdd` |  | "all":0.5 |
| 3 |  | `fallupd` |  | "all":0.5 |
| 4 |  | `fallup22` |  | "all":0.2 |
| 5 |  | `fallup2dd` |  | "all":0.5 |
| 6 |  | `fallup2d` |  | "all":0.5 |
| 7 |  | `fallend` |  | "all":0.2,"5":0.05,"11":0.05 |
| 8 |  | `fallend2` |  | "all":0.4,"5":0.1,"11":0.1 |
| 9 |  | `fallm` |  | "all":0.5 |
| 10 |  | `fallmdown` |  | "all":0.5 |
| 11 |  | `fallmdownend` |  | "all":0.6,"5":0.4,"7":0.4,"11":0.25 |
| 12 |  | `atkAir12` |  | "all":0.25 |
| 13 | Attack | `AttackUp2` |  | "all":0.5,"5":0.01 |
| 14 | Attack | `atkAir3` |  | "all":0.25 |
| 15 |  | `fallmdownendEX` |  | "all":0.6,"5":0.4,"7":0.4,"11":0.25 |
| 16 |  | `fallmdownendEX2` |  | "all":0.6,"5":0.4,"7":0.4,"11":0.25 |

### slot=5 — 冲刺系（Dash 输入）

| idx | 输入 | 动作名 | 段 | 帧数据 |
|---|---|---|---|---|
| 1 |  | `dashAtkEX` |  | "all":0.15 |
| 2 |  | `dashAtk` |  | "all":0.15 |
| 3 |  | `dashAirAtkEX` |  | "all":0.15 |
| 4 |  | `dashAirAtk` |  | "all":0.15 |
| 5 | Dash | `attackholdDashEX` |  | all:0.5,"8":0.8,"2":0.2 |
| 5 | Dash | `attackholdDashEX` |  | all:0.5,"8":0.8,"2":0.2 |
| 5 | Dash | `attackholdDashEX` |  | all:0.5,"8":0.8,"2":0.2 |
| 5 | Dash | `attackholdDashEX` |  | all:0.5,"8":0.8,"2":0.2 |
| 6 | Dash | `attackholdDash` |  | all:0.5,"8":0.8,"2":0.2 |
| 6 | Dash | `attackholdDash` |  | all:0.5,"8":0.8,"2":0.2 |
| 6 | Dash | `attackholdDash` |  | all:0.5,"8":0.8,"2":0.2 |
| 6 | Dash | `attackholdDash` |  | all:0.5,"8":0.8,"2":0.2 |
| 7 |  | `attackholdDashEXQ` |  | all:0.5,"8":0.8,"2":0.2 |
| 8 | Dash | `dash` |  | "all":0.1,"1":0.6,"4":0.6,"5":0.6,"8":0.6 |
| 9 | Dash | `dash2` |  | "all":0.1,"1":0.6,"4":0.6,"5":0.6,"8":0.6 |
| 10 | Dash | `dash3` |  | "all":0.1,"1":0.6,"4":0.6,"5":0.6,"8":0.6 |
| 11 |  | `dashend` |  | None |
| 12 |  | `dashAir` |  | "all":0.1,"1":0.45,"2":0.45,"4":0.45,"5":0.4,"11":0.4 |
| 13 |  | `dashAir2` |  | "all":0.1,"1":0.45,"2":0.45,"4":0.45,"5":0.4,"11":0.4 |
| 14 |  | `dashAir3` |  | "all":0.1,"1":0.45,"2":0.45,"4":0.45,"5":0.4,"11":0.4 |
| 15 |  | `attackhold_hit` |  | "all":0.55 |
| 16 |  | `attackhold_hit2` |  | "all":0.45, "2":0.2 |
| 17 |  | `attackhold_hit2Air` |  | "all":0.45,"1":0.35,"2":0.2 |
| 18 |  | `dashAAhit` |  | "all":0.35 |
| 19 |  | `dashAAendEX` |  | "all":0.65,"5":0.2,"11":0.25 |
| 20 |  | `dashAAend` |  | "all":0.65,"5":0.2,"11":0.25 |
| 21 |  | `dashAAendX` |  | "all":0.65,"5":0.2,"11":0.25 |
| 22 |  | `dashAirAtkGEX` |  | "all":0.15 |
| 23 |  | `dashAirAtkG` |  | "all":0.15 |
| 24 |  | `dashAtkGEX` |  | "all":0.15 |
| 25 |  | `dashAtkG` |  | "all":0.15 |

### slot=6 — 突进（rush）

| idx | 输入 | 动作名 | 段 | 帧数据 |
|---|---|---|---|---|
| 1 |  | `rush` |  | "all":0.5,"5":0.45,"8":0.6,"11":0.45 |
| 2 |  | `rushUp` |  | "all":0.5,"5":0.01 |

### slot=7 — 跳跃

| idx | 输入 | 动作名 | 段 | 帧数据 |
|---|---|---|---|---|
| 1 | Jump | `jump_down` |  | "all":0.17 |
| 2 | Jump | `drop_down` |  | "2":0.025,"7":0.2 |
| 3 | Jump | `jump` |  | "all":0.17 |
| 4 |  | `jump2` |  | None |
| 5 |  | `jump3` |  | None |

### slot=8 — 蓄力系（长按）

| idx | 输入 | 动作名 | 段 | 帧数据 |
|---|---|---|---|---|
| 1 | Attack | `attackhold21` |  | "all":0.5,"1":1,"2":0.2,"5":0.01,"7":0.2,"11":0.01 |
| 2 |  | `attackhold22_loop` |  | "all":0.5,"2":0,"5":0,"7":0,"11":0 |
| 3 |  | `attackhold23` |  | "all":0.6,"1":1,"2":0.3,"5":0.01,"7":0.3,"8":1,"11":0.01 |
| 4 | Attack | `attackhold` |  | "all":0.5,"1":1,"2":0.2,"5":0.01,"7":0.2,"8":1.05,"11":0.01 |

### slot=11 — 召唤系（Summon 输入）

| idx | 输入 | 动作名 | 段 | 帧数据 |
|---|---|---|---|---|
| 1 | Summon | `dashAtk0` |  | "all":0.35 |
| 2 | Summon | `dashAirAtk0` |  | "all":0.35 |
| 3 | Summon | `fallmEX0` |  | "all":0.5 |
| 4 |  | `falldownEX0` |  | "all":0.5 |
| 5 |  | `fallmEX0Air` |  | "all":0.5 |
| 6 | Summon | `summon` |  | "11":0.5 |

## 实测交叉验证

用 `ActionJournal`（记录 `ActionMgr.ChangeAction`）在训练场实跑一遍，观察到的动作名与上表**完全吻合**，并补充了表里没有的运行时名：

```
普攻      attack1 → attack2 → attack3 → attack4
下+攻击   attackD1 → attackD2 → attackD3
上挑      AttackUp
空中      atkAirX / atkAir12 / atkAir3
冲刺      dash → dashend → rushUp / dash2 / dashAir / dashAir2
          dashAtk / dashAtk0 / dashAtkG / dashAirAtk / dashAirAtk0 / dashAirAtkG
冲刺攻击  dashAAendEX
蓄力技    attackholdDashEX → attackhold_hit → attackhold_hit2
纹章解放  holdEX
技能      attackA / attackAEX / attackB / attackC
          attackA_AirEX / attackB_Air / attackC_Air
Ultra     UltraDashEX → UDdrop → dropend / UltraDashAirEX → UltraDAend
召唤      summon
下落系    fall / fallup / fallupd / fallup2 / fallup22 / fallup2d / fallup3 /
          fallm / fallmdown / fallmEX0 / fallmdownEX0 / fallmdownendEX / fallmdownendEX2
```

## ★ 结构结论（会影响改动的设计方式）

**`attackA` / `attackB` / `attackC` 是「技能槽」，不是具体的圆桌骑士。**

崔斯坦、布鲁诺、莫德雷德、高文、加拉哈德、佩利诺尔 **共用同一批动作名**，
往 A 槽装谁，`attackA` 就解析成谁的动作组。

也就是说：**潜能不是在替换动作，而是在切换整个输入组的动作映射。**
（这也是为什么 `summon` 是**独立的一种输入类型** —— 召唤/切换走的是单独通道。）

### 对那五条设计的直接影响

| 设计目标 | 能不能靠动作名匹配 |
|---|---|
| ① 冲刺打断冲刺 | ✅ 可以（`dash`/`dash2`/`dashend`，看 `GameActionLogic.Interrupt`） |
| ② 高文 → 崔斯坦一/二/三段纹章 | ❌ **跨技能**，动作名分不出来 |
| ③ 崔斯坦三段 → 布鲁诺三段剑气 | ❌ 跨技能 |
| ④ 布鲁诺剑气 → ±30° 两道 | ⚠ 要先认出"布鲁诺的剑气"是哪个弹幕 |
| ⑤ 莫德雷德上挑 → 移动纹章 / 布鲁诺剑气 | ❌ 跨技能 |

**②③⑤ 必须改从「产物」侧识别**：不认"这是崔斯坦的动作"，而认"它生成了哪个纹章/剑气"
（`bullet_id` + `bullet_action`）。`BulletProbe` 已经能打出这两项。

## 下一步

把三个技能槽分别装上 **崔斯坦 / 布鲁诺 / 莫德雷德** 各按一次，
用 `BulletProbe` 记录「**技能 → 弹幕 id + action**」——
**那张表才是②③⑤ 的真正地基**，有了它这几条就是纯配置。

## 其他

- 全角色原始 dump：`tools/_skillactivate_all.txt`（2438 条）
- 本地化招式名（`ActorActionName_<id>`）：纹章解放=340061、崔斯坦=340081、布鲁诺=340281、
  莫德雷德=340321、高文=340391、加拉哈德=340441、贝德维尔=340501


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


---

# 运行时链路验证表（实测）

用 `ChainTrace` 把「技能 → 动作」按层打出来，本局共 104 条配对、去重 52 种。
**和静态 `skillactivate` 表的槽位完全吻合**（2.1 / 2.3 / 2.4 …），等于双向验证。

| 动作名 | 按键/方向 | 槽位 | 类型 |
|---|---|---|---|
| `AttackUp` | Attack/Up | 1.1 | Attack |
| `attackD1` | Attack/Down | 1.4 | Attack |
| `attackD2` | Attack/Down | 1.6 | Attack |
| `attackD3` | Attack/Down | 1.7 | Attack |
| `atkAirX` | Attack/Down | 1.8 | Attack |
| `attack1` | Attack/Any | 1.9 | Attack |
| `attack2` | Attack/Any | 1.10 | Attack |
| `attack3` | Attack/Any | 1.11 | Attack |
| `attack4` | Attack/Any | 1.12 | Attack |
| `attackAEX` | Skill/Down | 2.1 | Skill1 |
| `attackB` | Skill/Down | 2.3 | Skill1 |
| `attackC` | Skill/Down | 2.4 | Skill1 |
| `attackA_AirEX` | Skill/Down | 2.5 | Skill1 |
| `attackB_Air` | Skill/Down | 2.7 | Skill1 |
| `attackC_Air` | Skill/Down | 2.8 | Skill1 |
| `UltraDashEX` | Skill/Up | 2.9 | Skill1 |
| `UltraDashAirEX` | Skill/Up | 2.11 | Skill1 |
| `UltraDAend` | /Any | 2.13 | Skill1 |
| `fall` | /Any | 4.1 | AttackAir |
| `fallupd` | /Any | 4.3 | AttackAir |
| `fallup2d` | /Any | 4.6 | AttackAir |
| `fallend` | /Any | 4.7 | AttackAir |
| `fallm` | /Any | 4.9 | AttackAir |
| `fallmdown` | /Any | 4.10 | AttackAir |
| `fallmdownend` | /Any | 4.11 | AttackAir |
| `atkAir12` | /Any | 4.12 | AttackAir |
| `atkAir3` | Attack/Any | 4.14 | AttackAir |
| `fallmdownendEX` | /Any | 4.15 | AttackAir |
| `fallmdownendEX2` | /Any | 4.16 | AttackAir |
| `dashAtk` | /Any | 5.2 | Dash |
| `dashAirAtk` | /Any | 5.4 | Dash |
| `attackholdDashEX` | Dash/Down | 5.5 | Dash |
| `dash` | Dash/Any | 5.8 | Dash |
| `dashAir` | /Any | 5.12 | Dash |
| `dashAir2` | /Any | 5.13 | Dash |
| `dashAir3` | /Any | 5.14 | Dash |
| `attackhold_hit` | /Any | 5.15 | Dash |
| `attackhold_hit2` | /Any | 5.16 | Dash |
| `attackhold_hit2Air` | /Any | 5.17 | Dash |
| `dashAAendEX` | /Any | 5.19 | Dash |
| `dashAirAtkG` | /Any | 5.23 | Dash |
| `dashAtkG` | /Any | 5.25 | Dash |
| `rushUp` | /Any | 6.2 | DashAttack |
| `jump` | Jump/Any | 7.3 | Jump |
| `jump2` | /Any | 7.4 | Jump |
| `jump3` | /Any | 7.5 | Jump |
| `attackhold21` | Attack/Any | 8.1 | LongAttack |
| `attackhold23` | /Any | 8.3 | LongAttack |
| `dashAtk0` | Summon/Up | 11.1 | Summon |
| `dashAirAtk0` | Summon/Up | 11.2 | Summon |
| `fallmEX0` | Summon/Down | 11.3 | Summon |
| `summon` | Summon/Any | 11.6 | Summon |

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


---

# ★ 最终版「输入 → 招式」表（三维键位）

**关键发现：输入是三维的** —— `按键` + `方向` + `姿态`。

`ActorVirtualButtonConfig.TouchCriteria` 就是这个结构：
```csharp
public class TouchCriteria {
    public EInputDirection direction;   // 方向
    public EActorPosture    posture;    // 姿态（地面/空中）
}
```

在 `skillactivate` 表里对应：
- `f8`  = 按键（Attack / Dash / Jump / Skill / Summon）
- `f52` = 方向（1=Any 2=Up 3=Down，由运行时实测反推）
- `f19` = **姿态**（缺省=地面，1=空中，由地面/空中成对动作对比得出）

**少了姿态就会把「地面 上+F」和「空中 上+F」混成一格** —— 这就是之前一直理不顺的原因。

## F 键 = Summon 键

`InputCmd.Summon = 5`。所以 F 与潜能强关联：它就是放骑士技能的召唤键。

```
Summon/上 地面 → dashAtk0      槽11.1   ← 高文
Summon/上 空中 → dashAirAtk0   槽11.2   ← 空中高文
Summon/下 空中 → fallmEX0      槽11.3   ← 加拉哈德
Summon/任意 空中 → summon      槽11.6   ← 快速崔斯坦处决 → 纹章 summonburst
```

## 完整表

ES 输入 → 招式   按键=f8  方向=f52(1=Any 2=Up 3=Down)  姿态=f19(缺省=地面 1=空中)


### 按键="Attack"  方向=Any  姿态=地面   (6 条)
    槽1.9   attack1                  "all":0.3,"5":0.01,"7":0.3
    槽1.10  attack2                  "all":0.35,"5":0.01,"7":0.3
    槽1.11  attack3                  "all":0.35,"5":0.01,"7":0.32
    槽1.12  attack4                  "all":0.4,"5":0.01,"8":0.75
    槽8.1   attackhold21             "all":0.5,"1":1,"2":0.2,"5":0.01,"7"
    槽8.4   attackhold               "all":0.5,"1":1,"2":0.2,"5":0.01,"7"

### 按键="Attack"  方向=Any  姿态=空中   (2 条)
    槽1.13  atkAirX                  "all":0.28,"5":0.01,"8":0.75
    槽4.14  atkAir3                  "all":0.25

### 按键="Attack"  方向=Down  姿态=地面   (4 条)
    槽1.4   attackD1                 "all":0.35,"5":0.01,"7":0.3
    槽1.5   attackD1                 "all":0.35,"5":0.01,"7":0.3
    槽1.6   attackD2                 "all":0.35,"5":0.01,"7":0.3
    槽1.7   attackD3                 "all":0.5,"1":0.3,"5":0.01,"7":0.35

### 按键="Attack"  方向=Down  姿态=空中   (1 条)
    槽1.8   atkAirX                  "all":0.28,"5":0.01,"8":0.75

### 按键="Attack"  方向=Up  姿态=地面   (2 条)
    槽1.1   AttackUp                 "all":0.5,"5":0.01
    槽1.2   AttackUp                 "all":0.5,"5":0.01

### 按键="Attack"  方向=Up  姿态=空中   (2 条)
    槽1.3   AttackUp2                "all":0.5,"5":0.01
    槽4.13  AttackUp2                "all":0.5,"5":0.01

### 按键="Dash"  方向=Any  姿态=地面   (3 条)
    槽5.8   dash                     "all":0.1,"1":0.6,"4":0.6,"5":0.6,"8
    槽5.9   dash2                    "all":0.1,"1":0.6,"4":0.6,"5":0.6,"8
    槽5.10  dash3                    "all":0.1,"1":0.6,"4":0.6,"5":0.6,"8

### 按键="Dash"  方向=Down  姿态=地面   (4 条)
    槽5.5   attackholdDashEX         all:0.5,"8":0.8,"2":0.2
    槽5.5   attackholdDashEX         all:0.5,"8":0.8,"2":0.2
    槽5.6   attackholdDash           all:0.5,"8":0.8,"2":0.2
    槽5.6   attackholdDash           all:0.5,"8":0.8,"2":0.2

### 按键="Dash"  方向=Down  姿态=空中   (4 条)
    槽5.5   attackholdDashEX         all:0.5,"8":0.8,"2":0.2
    槽5.5   attackholdDashEX         all:0.5,"8":0.8,"2":0.2
    槽5.6   attackholdDash           all:0.5,"8":0.8,"2":0.2
    槽5.6   attackholdDash           all:0.5,"8":0.8,"2":0.2

### 按键="Jump"  方向=Any  姿态=地面   (2 条)
    槽7.1   jump_down                "all":0.17
    槽7.3   jump                     "all":0.17

### 按键="Jump"  方向=Down  姿态=空中   (1 条)
    槽7.2   drop_down                "2":0.025,"7":0.2

### 按键="Skill"  方向=Any  姿态=地面   (2 条)
    槽2.14  holdEX                   "all":0.7,"5":0.01
    槽2.15  hold                     "all":0.7,"5":0.01

### 按键="Skill"  方向=Any  姿态=空中   (4 条)
    槽2.16  attackAir2EX             "all":0.3,"5":0.01
    槽2.17  attackAir2               "all":0.3,"5":0.01
    槽2.18  attackAirEX              "all":0.3,"5":0.01
    槽2.19  attackAir                "all":0.3,"5":0.01

### 按键="Skill"  方向=Down  姿态=地面   (4 条)
    槽2.1   attackAEX                "all":0.5,"1":0.7,"5":0.01
    槽2.2   attackA                  "all":0.5,"1":0.7,"5":0.01
    槽2.3   attackB                  "all":0.5,"1":0.8,"5":0.01
    槽2.4   attackC                  "all":1.25,"4":1.4,"5":0.3,"11":0.3

### 按键="Skill"  方向=Down  姿态=空中   (4 条)
    槽2.5   attackA_AirEX            "all":0.6,"2":0.45,"5":0.01,"7":0.45
    槽2.6   attackA_Air              "all":0.6,"2":0.45,"5":0.01,"7":0.45
    槽2.7   attackB_Air              "all":0.75,"2":0.55,"5":0.01,"7":0.4
    槽2.8   attackC_Air              "all":1,"5":0.01,"7":0.55,"11":1

### 按键="Skill"  方向=Up  姿态=地面   (2 条)
    槽2.9   UltraDashEX              "all":0.55,"2":1
    槽2.10  UltraDash                "all":0.55,"2":1

### 按键="Skill"  方向=Up  姿态=空中   (2 条)
    槽2.11  UltraDashAirEX           "all":2
    槽2.12  UltraDashAir             "all":2

### 按键="Summon"  方向=Any  姿态=空中   (1 条)
    槽11.6   summon                   "11":0.5

### 按键="Summon"  方向=Down  姿态=空中   (1 条)
    槽11.3   fallmEX0                 "all":0.5

### 按键="Summon"  方向=Up  姿态=地面   (1 条)
    槽11.1   dashAtk0                 "all":0.35

### 按键="Summon"  方向=Up  姿态=空中   (1 条)
    槽11.2   dashAirAtk0              "all":0.35

### 按键="(无/派生)"  方向=?  姿态=空中   (1 条)
    槽4.9   fallm                    "all":0.5

### 按键="(无/派生)"  方向=Any  姿态=地面   (5 条)
    槽3.1   None                     
    槽5.11  dashend                  
    槽6.1   rush                     "all":0.5,"5":0.45,"8":0.6,"11":0.45
    槽8.2   attackhold22_loop        "all":0.5,"2":0,"5":0,"7":0,"11":0
    槽8.3   attackhold23             "all":0.6,"1":1,"2":0.3,"5":0.01,"7"

### 按键="(无/派生)"  方向=Any  姿态=空中   (6 条)
    槽5.12  dashAir                  "all":0.1,"1":0.45,"2":0.45,"4":0.45
    槽5.13  dashAir2                 "all":0.1,"1":0.45,"2":0.45,"4":0.45
    槽5.14  dashAir3                 "all":0.1,"1":0.45,"2":0.45,"4":0.45
    槽6.2   rushUp                   "all":0.5,"5":0.01
    槽7.4   jump2                    
    槽7.5   jump3                    

### 按键="(无/派生)"  方向=Down  姿态=地面   (1 条)
    槽5.16  attackhold_hit2          "all":0.45, "2":0.2

### 按键="(无/派生)"  方向=Down  姿态=空中   (18 条)
    槽4.1   fall                     "all":0.1,"1":2,"4":2,"5":0.5,"11":0
    槽4.2   fallupdd                 "all":0.5
    槽4.3   fallupd                  "all":0.5
    槽4.4   fallup22                 "all":0.2
    槽4.5   fallup2dd                "all":0.5
    槽4.6   fallup2d                 "all":0.5
    槽4.7   fallend                  "all":0.2,"5":0.05,"11":0.05
    槽4.8   fallend2                 "all":0.4,"5":0.1,"11":0.1
    槽4.10  fallmdown                "all":0.5
    槽4.11  fallmdownend             "all":0.6,"5":0.4,"7":0.4,"11":0.25
    槽4.12  atkAir12                 "all":0.25
    槽4.15  fallmdownendEX           "all":0.6,"5":0.4,"7":0.4,"11":0.25
    槽4.16  fallmdownendEX2          "all":0.6,"5":0.4,"7":0.4,"11":0.25
    槽5.7   attackholdDashEXQ        all:0.5,"8":0.8,"2":0.2
    槽5.15  attackhold_hit           "all":0.55
    槽5.17  attackhold_hit2Air       "all":0.45,"1":0.35,"2":0.2
    槽11.4   falldownEX0              "all":0.5
    槽11.5   fallmEX0Air              "all":0.5

### 按键="(无/派生)"  方向=Up  姿态=地面   (5 条)
    槽5.1   dashAtkEX                "all":0.15
    槽5.2   dashAtk                  "all":0.15
    槽5.19  dashAAendEX              "all":0.65,"5":0.2,"11":0.25
    槽5.20  dashAAend                "all":0.65,"5":0.2,"11":0.25
    槽5.21  dashAAendX               "all":0.65,"5":0.2,"11":0.25

### 按键="(无/派生)"  方向=Up  姿态=空中   (8 条)
    槽2.13  UltraDAend               "all":0.5
    槽5.3   dashAirAtkEX             "all":0.15
    槽5.4   dashAirAtk               "all":0.15
    槽5.18  dashAAhit                "all":0.35
    槽5.22  dashAirAtkGEX            "all":0.15
    槽5.23  dashAirAtkG              "all":0.15
    槽5.24  dashAtkGEX               "all":0.15
    槽5.25  dashAtkG                 "all":0.15


---

# ★ 骑士技能 → 输入 最终对照（已实测确认）

用户实测确认的按法 + 日志交叉验证所得：

| 骑士 | 按法 | 按键/方向 | 姿态 | 槽位 | **内部动作名** | 纹章 action |
|---|---|---|---|---|---|---|
| 佩利诺尔（下段平A） | 地面 下+攻击 | `Attack/Down` | 地面 | 1.4 / 1.5 / 1.6 / 1.7 | `attackD1`(×2) → `attackD2` → `attackD3` | `aD12` / `aD22` / `aD32` |
| **崔斯坦** | 空中 下+攻击（快速下落） | `Attack/Down` | **空中** | **1.8** | **`atkAirX`** | **`attackAir2`** |
| **加拉哈德** | 下+冲刺 | `Dash/Down` | 地面 | **5.5** | **`attackholdDashEX`** | `dashSkill` |
| **空中加拉哈德** | 空中 下+冲刺 | `Dash/Down` | 空中 | **5.5** | **`dashendAirAHD`** | — |
| **高文** | 上+F | `Summon/Up` | 地面 | **11.1** | `dashAtk0` | `dashA` / `dash` |
| **空中高文** | 空中 上+F | `Summon/Up` | 空中 | **11.2** | `dashAirAtk0` | `dashAir` / `dashA` |
| **快速崔斯坦处决** | 空中 F | `Summon/Any` | 空中 | **11.6** | `summon` | `summonburst` |
| **莫德雷德** | 上+攻击 | `Attack/Up` | 地面 | 1.1 / 1.2 | `AttackUp` | `aup` / `aup2` |
| **布鲁诺** | 下+技能 | `Skill/Down` | 地面 | **2.1 / 2.3 / 2.4** | `attackAEX` → `attackB` → `attackC` | `A1` / `B1` / `C1` |
| **空中布鲁诺** | 空中 下+技能 | `Skill/Down` | 空中 | **2.5 / 2.7 / 2.8** | `attackA_AirEX` → `attackB_Air` → `attackC_Air` | `A1` / `B1` / `C1` |
| **贝德维尔（SP）** | 上+技能 | `Skill/Up` | 地面 | **2.9** | `UltraDashEX` | `UD` |
| **空中贝德维尔** | 空中 上+技能 | `Skill/Up` | 空中 | **2.11** | `UltraDashAirEX` | `UDA0` / `UDA` / `UDEX` |
| 纹章解放 | 技能（无方向） | `Skill/Any` | 地面 | **2.14** | `holdEX` | `x2` |

## 关键结论

1. **F 键 = `Summon`（召唤键）** —— 所以它与潜能强关联，它就是放骑士技能的键
2. **输入是三维的**：按键 + 方向 + **姿态**（地面/空中）。
   缺了姿态会把「地面 上+F」和「空中 上+F」混成一格 —— 这是之前一直理不顺的根因
3. **崔斯坦和莫德雷德都走 `Attack` 键，靠方向区分**：
   上+攻击 = 莫德雷德（`AttackUp`），空中下+攻击 = 崔斯坦（`atkAirX`）
4. **加拉哈德走 `Dash` 键 + 下**（不是攻击键），空中版动作名不同（`dashendAirAHD`）

## 待补

- **下段平A 的四段**：静态表里 `attackD1` 占两行（1.4 / 1.5），推测四段是
  `D1(1.4)` → `D1(1.5)` → `D2(1.6)` → `D3(1.7)`（前两段同名）。需连按一次验证。
- **崔斯坦的四段**：需要踩到怪物触发弹起，弹起后应转到别的动作名。需实战连弹验证。
