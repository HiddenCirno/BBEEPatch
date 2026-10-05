# ES 纹章 / 弹幕生成系统 —— 权威地图

> 逆向对象：BlazblueEntropyEffect (Unity 2022.3 IL2CPP)。角色 ES(103401)，DLC 机体。
> 全部结论基于**已抽取的真实字节**，非推断。凡是未能确定的一律标注 **未确定**。
> 生成日期：2026-10-02

## 0. 数据来源（证据文件）

| 来源 | 路径 | 说明 |
|---|---|---|
| ES 角色 ActionLogicGroup 原始字节 | `extracted/es_mono0.raw` | 1,042,540 B（`actor/logicdata/es.ab` 的 MonoBehaviour raw） |
| 纹章弹幕 ActionLogicGroup 原始字节 | `extracted/esbullet_mono.raw` | 168,240 B（`actor/logicdata/esbullet.ab` 的 MonoBehaviour raw） |
| BulletConfig 表 | `extracted/bulletconfig.ab_BulletConfig.bin` | 13,538 B，180 行 |
| dump.cs | `dump/dump.cs` | 类型/偏移/RVA 权威清单 |
| 反汇编 | `tools/_disasm.py` → `_disasm_out.txt` | 运行时调用链 |

本次新写的提取脚本（可复现）：
`tools/_es_final.py`（生成 `_es_final_out.txt`）、`tools/_es_bulletact.py`（`_es_bulletact_out.txt`）、
`tools/_es_roidref.py`（`_es_roidref_out.txt`）、`tools/_es_map.py` / `_es_map3.py` / `_es_map4.py`（诊断）。
（复用既有：`tools/_es_crest_sites.py` / `_esbullet_blocks.py` / `_bulletparse*.py`。）

### 0.1 序列化格式（已实测确认，是本次能精确解析的关键）

ActionLogicGroup 的 MonoBehaviour 里，字符串是 Unity 原生布局：
`[u32 小端长度][ASCII 字节][补 0 到 4 字节对齐]`。
脚本调用 `ActionScriptCall`（`dump.cs:230827`）= 连续两根长度前缀字符串 `{Function, Params}`。
**注意**：既有笔记把 `CreateBulletX / CreateBulletU / CreateBulletm` 当成独立函数名是**误读** ——
`X/U/m` 其实是紧随其后的 `Params` 长度前缀的低字节（例：`@39624` 的字节是
`0c 00 00 00 "CreateBullet" 58 00 00 00 "trigger:56161,..."`，`0x58=88` 即 params 长度）。
本文件用字节级解析修正了这一点。

---

## 1. 任务一：ES 角色 ALG 里**每一处** CreateBullet 生成指令

`es_mono0.raw` 共 175 个动作标记（组名 `es`，`_es_map3_out.txt`）。
含 `bullet_id:` 的生成指令 **26 条**（与 `_es_crest_sites_out.txt` 的 26 一致），另有 2 条
`CreateBullet_summon`（无 bullet_id）。函数名三种：
`CreateBullet`（基础）、`CreateBulletIfTriggerChange`（创建并挂“触发器变化后切换新动作”）、
`CreateBullet_summon`（召唤，参数字符串为空）。

> 触发 id 是**指令自带的 `trigger:` 参数**（条件）。`trigger` 的语义未在本轮反汇编中确认，**未确定**。

| # | 来源动作 | 函数 | bullet_id | bullet_action | bullet_action_new | trigger | pos(x,y) | dir | tag | 视觉/用途 |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | `dashAir3` | CreateBullet | 10340101 | dashAir | – | 56161 | (1,0) | (1,0) | – | 空中冲刺纹章 |
| 2 | `dashAir2` | CreateBullet | 10340101 | dashAir | – | 56161 | (1,0) | (1,0) | – | 同上 |
| 3 | `dashAir` | CreateBullet | 10340101 | dashAir | – | 56161 | (1,0) | (1,0) | – | 同上 |
| 4 | `dash3` | CreateBullet | 10340101 | dash | – | 56161 | (0,-0.5) | (1,0) | – | 地面冲刺纹章 |
| 5 | `dash2` | CreateBullet | 10340101 | dash | – | 56161 | (0,-0.5) | (1,0) | – | 同上 |
| 6 | `dash` | CreateBullet | 10340101 | dash | – | 56161 | (0,-0.5) | (1,0) | – | 同上 |
| 7 | `dashAirAtkold` | CreateBullet | 10340101 | dashAir | – | 56161 | (1,0) | (1,0) | – | 遗留 |
| 8 | `dashAAhitold` | CreateBulletIfTriggerChange | 10340101 | DAA | DAA2 | 56271 | (1.2,1.5) | (1,0) | – | 冲刺攻击派生 |
| 9 | `fall` | CreateBulletIfTriggerChange | 10340101 | attackAir | attackAir2 | 56271 | (0,0) | (1,0) | – | 下落时生成空中纹章 |
| 10 | `fallmdownendEXx` | CreateBullet | 10340101 | fallmdownend | – | 11481 | (0,0) | (1,0) | – | 落地冲击纹章 |
| 11 | `fallmdownendEX` | CreateBullet | 10340101 | fallmdownend | – | 11481 | (0,0) | (1,0) | – | 同上 |
| 12 | `fallmdownendEX2` | CreateBullet | 10340101 | fallmdownend | – | 11481 | (0,0) | (1,0) | – | 同上 |
| 13 | `fallmdownendold` | CreateBullet | **800002** | leibao | – | 11481 | (0,0) | (1,0) | – | 遗留（雷暴弹，非纹章） |
| 14 | `atkAirX` | CreateBulletIfTriggerChange | 10340101 | attackAir | attackAir2 | 56271 | (0,0) | (1,0) | – | 空中收尾纹章 |
| 15 | `attackholdDashEXQ` | CreateBullet | 10340101 | dashSkill | – | 56161 | (0,-0.5) | (1,0) | – | 冲刺+攻击技能纹章 |
| 16 | `attackholdDashEX` | CreateBullet | 10340101 | dashSkill | – | 56161 | (0,-0.5) | (1,0) | – | 同上 |
| 17 | `attackholdDash` | CreateBullet | 10340101 | dashSkill | – | 56161 | (0,-0.5) | (1,0) | – | 同上 |
| 18 | `attackhold_hit2` | CreateBullet | 10340101 | AD_hit | – | – | (-0.2,0.8) | (1,0) | **targpos:1, scale:1** | 蓄力普攻命中纹章 |
| 19 | `attackhold_hit2Air` | CreateBullet | 10340101 | AD_hit | – | – | (-0.2,0.8) | (1,0) | targpos:1, scale:1 | 同上（空） |
| 20 | `attackD2` | CreateBulletIfTriggerChange | 10340101 | a3 | a32 | 56271 | (0.9,0) | (1,0) | – | 下段第2段追加纹章 |
| 21 | `attack3` | CreateBulletIfTriggerChange | 10340101 | a3 | a32 | 56271 | (0.9,0) | (1,0) | – | 平3追加纹章 |
| 22 | `AttackUp` | CreateBulletIfTriggerChange | 10340101 | aup | aup2 | 56271 | (1,1.6) | (1,0) | – | 上挑纹章 |
| 23 | `holdEX` | CreateBulletIfTriggerChange | 10340101 | **x1** | **x2** | 56061 | (0,0) | (1,0) | **es_x** | ★纹章解放（长按） |
| 24 | `hold` | CreateBulletIfTriggerChange | 10340101 | **x1** | **x2** | 56061 | (0,0) | (1,0) | **es_x** | ★纹章解放 |
| 25 | `rush` | CreateBulletIfTriggerChange | 10340101 | **x3** | **x32** | 56271 | (0.5,0) | (1,0) | – | 冲刺突起纹章 |
| 26 | `rushUp` | CreateBulletIfTriggerChange | 10340101 | **xup** | **xup2** | 56271 | (0,0) | (1,0) | **es_rush** | 冲刺上挑纹章 |
| 27 | `summonEX` | CreateBullet_summon | – | – | – | – | – | – | – | 召唤（Params 空） |
| 28 | `summon` | CreateBullet_summon | – | – | – | – | – | – | – | 召唤（Params 空） |

> `CreateBulletIfTriggerChange` 的语义：**创建弹幕并把 `bullet_action` 作为初始动作、
> `bullet_action_new` 作为“trigger 变化后切换到的动作”**（名字直译），`tag` 用于后续按 tag 操作。
> 该函数内部如何注册 trigger 回调**未确定**（未反汇编 JS 包装层）。

### 1.1 数据驱动的生成（AddRoleData，非脚本）

除脚本生成外，ES 的 ALG 还把纹章当作**发射物(AddRoleData)**挂在动作上
（`GameActionLogic.AddRoles`, `dump.cs:233737`，偏移 0x1F8；类 `AddRoleData` `dump.cs:230317`，
字段 `RoleId`(int) @0x48、`StartActionEx`(string list) @0x60、`TagV` @0xC0）。
本次以「二进制 int32 == 某 BulletConfig id」扫 `es_mono0.raw`，**只有 `10340101` 命中，共 72 处**
（`_es_roidref_out.txt`）。把每处紧邻的字符串读出来，得到「哪个动作把纹章弹幕切/设成哪个动作」：

| 来源动作 | 弹幕动作 | 来源动作 | 弹幕动作 |
|---|---|---|---|
| `attack1` | a0, a02 | `attackA` / `attackAEX` | A1 |
| `attack2` | a20, a22 | `attackA_Air` / `attackA_AirEX` | A1 |
| `attack3` | a3, a32 | `attackB_Air` | B1 |
| `attack4` | a4, a42 | `attackC` / `attackC_Air` | C1 |
| `attackD1` | aD1, aD12 | `atkAir12` | aa |
| `attackD2` | aD2, aD22 | `atkAir3` | aa3 |
| `attackD3` | aD3, aD32 | `attackAir` / `attackAirEX` | ax1 |
| `AttackUp` / `AttackUp2` | aup | `attackAir2` / `attackAir2EX` | ax2 |
| `attackhold` | a4, a42 | `jump2` / `jump3` | jump |
| `attackhold23` | aH, aH2, aH3, aHEX, aH2EX, aH3EX | `hold1` | x1 |
| `dashAtk`(+G/EX/GEX) | dashA, dashAtk | `dashAirAtk`(+G/EX/GEX) | dashA, dashAtk |
| `UltraDAend` | UDA, UDEX | `UltraDashEX` / `UltraDash` | UD, UDAEX |
| `UltraDashAirEX` / `UltraDashAir` | UDA0 | | |

（完整 72 条见 `tools/_es_bulletact_out.txt`。）

> **结论**：普通攻击(平A/空中/下段/蓄力)上的“纹章”不是新写的脚本生成，而是
> `AddRoleData(RoleId=10340101, StartAction=<动作名>)` —— 这也解释了本地化里
> “纹章会出现在剑的弧光上”。ES 的弹幕**只有 id=10340101 一个**（见任务三/四）。

---

## 2. 任务二：`esbullet` ActionLogicGroup 状态机

`esbullet_mono.raw` 组名 `esbullet`，共 76 个动作块标记，动作名列表
（`_es_final_out.txt`；`q`/`a` 为一字符动作名，见下方注）：

```
jump, q, fallmdownend, attackAir2old, attackAir2, attackAir, talentBullet,
dashAir3, dashAir2, dashAir, dashSkill2, dash2, dashSkill, dash,
DAA2, DAA, dashBEX, dashB, dashA, dashA0, dashAtk, AD_hit,
C12EX, C1EX, C12, C1, B12EX, B1EX, B12, B1, A12EX, A1EX, A12, A1,
UDA0, UDAA, UDA, UD, UDAEX, UDEX, aup2, aup,
aH3EX, aH3, aH2EX, aH2, aHEX, aH,
aD32, aD3, aD22, aD2, aD12, aD1, a0, a, a02, a20, a2, a22, a3, a32, a4, a42,
aa, aa3, x32old, x32, x3old, x3, xup, xup2, ax2, ax1, x1, x2
```

> 注：`q` 是数据里字面长度 1 的串，`_alg_layout_out.txt` 在第 2 个动作处看到的是 `dead`
> （`dead` 更像是 `DeadAction` 字段而非动作名）。`q` 到底是占位动作名还是解析歧义 **未确定**。

### 2.1 每个动作绑定的 VFX / 命中盒（`_es_map_out.txt`，块内资源串）

| 动作 | VFX (`Role/...`) | Hit | 备注 |
|---|---|---|---|
| jump | Role/Es/es_fall_01a | Hit/hit_105 | sfx_esbullet_jump |
| q | – | Hit/hit_019 | 疑为 dead |
| fallmdownend | Role/Es/es_fallmdownend_02 | Hit/hit_019 | |
| attackAir2old | Role/Es/es_holdFull_01 | Hit/hit_018 | RemoveBuff es_x |
| attackAir2 | Role/Es/es_attackAir_03 | Hit/hit_106 | |
| attackAir | Role/Es/es_attackAir_01 | Hit/hit_018 | |
| talentBullet | Role/Es/es_attackAir_03 | Hit/hit_106 | |
| dashAir3 | Role/Es/es_attackAir_03a | Hit/hit_105 | |
| dashAir2 | Role/Es/es_attackAir_01a | Hit/hit_105 | |
| dashAir | Role/Es/es_attackAir_01 | Hit/hit_105 | |
| dashSkill2 / dash2 | Role/Es/es_attackAir_02a | Hit/hit_105 | |
| dashSkill / dash | Role/Es/es_attackAir_02 | Hit/hit_105 | |
| DAA2 | Role/Es/es_dashAtk1_02 | Hit/hit_106 | |
| DAA | Role/Es/es_dashAtk1_01 | Hit/hit_018 | 内部 CreateBullet_ → DAA2 |
| dashBEX / dashB | Role/Es/es_esbullet_dashA_001, _002 | Hit/hit_105 | |
| dashA | – | Hit/hit_019 | |
| dashA0 | – | – | |
| dashAtk | – | – | cond: ParamV("offsetx"/"offsety") |
| AD_hit | – | Hit/hit_105 | Splash es_AD_hit |
| C12EX / C12 | Role/Es/es_holdatk3_02a | – | |
| C1EX | Role/Es/es_holdatk3_02 | Hit/hit_106 | Splash Esbullet_C |
| C1 | Role/ha/ha_attack6_02, Role/Es/es_holdatk3_02 | Hit/hit_106 | Splash Esbullet_C |
| B12EX / B12 | Role/Es/es_holdatk1_02a | – | |
| B1EX | Role/Es/es_holdatk1_02 | Hit/hit_018 | Splash Esbullet_B |
| B1 | Role/ha/ha_attack6_02, Role/Es/es_holdatk1_02 | Hit/hit_018 | Splash Esbullet_B |
| A12EX / A12 | Role/Es/es_holdatk1_02a | – | |
| A1EX | Role/Es/es_holdatk1_02 | Hit/hit_018 | Splash Esbullet_A |
| A1 | Role/Es/es_holdatk1_02 | Hit/hit_018 | Splash Esbullet_A |
| UDA0 | – | Hit/hit_019 | |
| UDAA | – | Hit/hit_106 | |
| UDA / UD | Role/Es/es_AH_01 | Hit/hit_106 | 贝德维尔 |
| UDAEX / UDEX | Role/Es/es_AH_03 | Hit/hit_106 | 贝德维尔 EX |
| aup2 | Role/Es/es_attackup_02 | Hit/hit_106 | |
| aup | Role/Es/es_attackup_01 | Hit/hit_018 | 内部 CreateBullet_ → aup2 |
| aH3EX / aH3 | Role/Es/es_atkhold_21_006, Role/Es/es_attack3_02 | Hit/hit_106 | |
| aH2EX / aH2 | Role/Es/es_atkhold_21_005, Role/Es/es_attack3_02 | Hit/hit_106 | |
| aHEX / aH | Role/Es/es_atkhold_21_003 | Hit/hit_106 | |
| aD32 | es_attackD1_009 + es_attack3_01/02 | Hit/hit_018 | |
| aD3 | es_attackD1_008 + es_attack3_01 | Hit/hit_018 | |
| aD22 | es_attackD1_006 + es_attack3_01/02 | Hit/hit_018 | |
| aD2 | es_attackD1_005 + es_attack3_01 | Hit/hit_018 | |
| aD12 | es_attackD1_003 | Hit/hit_018 | |
| aD1 | es_attackD1_002 | Hit/hit_105 | |
| a0 | es_attack1_02 | Hit/hit_105 | |
| a | es_attack1_01 | Hit/hit_105 | |
| a02 | es_attack1_03 | Hit/hit_018 | |
| a20 | es_attack2_02 | Hit/hit_105 | |
| a2 | es_attack2_01 | Hit/hit_105 | |
| a22 | es_attack2_03 | Hit/hit_018 | |
| a3 | es_attack3_01 | Hit/hit_018 | |
| a32 | es_attack3_02 | Hit/hit_106 | |
| a4 | es_attack4_02 | Hit/hit_106 | |
| a42 | es_attack4_03 | Hit/hit_106 | |
| aa | es_atkAir12_005 | Hit/hit_106 | |
| aa3 | es_atkAir12_006 + es_attack3_02 | Hit/hit_106 | |
| x32old / x32 | Role/Es/es_holdRelease_02 | Hit/hit_018 | |
| x3old / x3 / xup | Role/Es/es_holdRelease_01 | Hit/hit_105 | |
| xup2 | Role/Es/es_holdRelease_02 | Hit/hit_018 | |
| ax2 / ax1 / x1 | Role/Es/es_holdFull_01 | Hit/hit_018 | RemoveBuff es_x |
| x2 | es_holdFull_01, Role/Hb/hb_s6hn_01, es_attackAir_01 | hit_018/009/002/019 | cond: ActionLogicCondition_Trigger; RemoveBuff es_x |

### 2.2 状态迁移（`ChangeAction` / 内部 `CreateBullet_`，`_es_final_out.txt`）

| 当前动作 | → 目标动作 | 条件 |
|---|---|---|
| `dashB` | dashBEX | 无条件 |
| `C1` | C1EX | trigger:56061 |
| `B1` | B1EX | trigger:56061 |
| `A1` | A1EX | trigger:56061 |
| `aH3` | aH3EX | 无条件 |
| `aH2` | aH2EX | 无条件 |
| `aH` | aHEX | 无条件 |
| `a` | a0 | 无条件 |
| `a2` | a20 | 无条件 |
| `DAA` | DAA2 | 内部 CreateBullet_（trigger:56271） |
| `aup` | aup2 | 内部 CreateBullet_（trigger:56271） |

外部把弹幕导进某状态（来自任务一/1.1）：
`bullet_action_new`：DAA→DAA2、attackAir→attackAir2、a3→a32、aup→aup2、x1→x2、x3→x32、xup→xup2；
`AddRoleData.StartAction`：见 1.1 表（A1/B1/C1/a0/a2/aH… /jump 等）；
`BulletAction*`（见任务三）：dashB、UDAA。

> **未确定**：每个 esbullet 动作的**时长 (TotalDuration / AnimateDuration)**。
> 序列化里这两项是 Fp(Q32.32) 二进制字段，紧随 Name/Animate 之后，但静态块的字段对齐
> 无法仅凭字符串可靠定位（已尝试，值不稳定）。要拿到时长需要：
> ① 反汇编 `GameActionLogic.get_TotalDuration` 定位偏移后按固定布局解析；或
> ② 运行时探针读 `GameActionLogic.TotalDuration(0x40)` / `AnimateDuration(0x30)`。
> 本轮**未取到**，不臆造数值。

---

## 3. 任务三：纹章家族与触发方式

### 3.1 纹章解放（ground/air release）—— **已确认**
- 触发动作：ES 链[2] 的 `holdEX` / `hold`（长按/纹章键）。
- 生成：`CreateBulletIfTriggerChange(bullet_id=10340101, bullet_action="x1", bullet_action_new="x2", tag="es_x", trigger=56061)`。
- 即：**x1 是解放的起始态，x2 是触发器变化后的绽放态**（x2 绑 es_holdFull_01 + Trigger 条件 + RemoveBuff es_x）。
- 相关：`rush`→`x3`(→x32)、`rushUp`→`xup`(→xup2, tag es_rush)、`attackAir2/EX`→`ax2`、`attackAir/EX`→`ax1`、`hold1`→`x1`。
- **结论**：笔记中 “x1/x2/ax2 = 纹章解放” **成立**，并且 x3/x32、xup/xup2、ax1 同族。
  插件里 `[纹章解放] CrestActions=x1,x2,ax2` 的取值有据。

### 3.2 普攻携带的纹章 —— **已确认（数据驱动）**
每一种普攻在 `AddRoleData` 里以 `RoleId=10340101 + StartAction=<动作>` 生成一个纹章弹幕，
动作名即该攻击的纹章形态（见 1.1 表）：`attack1→a0`、`attack2→a20`、`attack3→a3`、
`attack4→a4`、`attackD1→aD1`、`attackD2→aD2`、`attackD3→aD3`、`atkAir12→aa`、`atkAir3→aa3`、
`attackAir→ax1`、`attackAir2→ax2`、`jump2/3→jump`、`attackhold→a4/a42`、`attackhold23→aH/aH2/aH3…`。
另有脚本追加：`attack3/attackD2` → `a3`(→a32)、`AttackUp` → `aup`(→aup2)。

### 3.3 布鲁诺剑气 —— **部分确定**
- 布鲁诺 = ES 的 Shooter 形态（`ActorUltra_103401` 系列，`ActorActionName_340281`，`_knights2_out.txt`）。
- ES 链[2]（技能键）地面三段 = `attackAEX/attackA / attackB / attackC`（`PROJECT_STATE §3.3`）。
- 这四段在 `AddRoleData` 里的纹章动作是 **A1 / B1 / C1**（EX 变体绑 A1；A12/B12/C12 是二段命中）。
  esbullet 中 `A1/B1/C1` 分别绑 `Role/Es/es_holdatk1_02`、`es_holdatk1_02`、`es_holdatk3_02`，命中盒 hit_018/106。
- **bullet id = 10340101（esbullet）**，没有单独的剑气 id。
- ⚠ “剑气(剑波)”与“纹章”在视觉/命名上可能指同一套 A1/B1/C1 弹幕；本地化里
  “斩出剑气。共3段，第3段可破霸体。”与“附带纹章的大剑攻击，共3段。”都很接近，
  二者精确对应关系 **未确定**。

### 3.4 贝德维尔巨大纹章（UDA/UDA0）—— **已确认弹幕 id，非新 id**
- `esbullet` ALG 内确有 `UDA0 / UDAA / UDA / UD / UDAEX / UDEX` 六个动作，
  UDA/UD 绑 `Role/Es/es_AH_01`，UDAEX/UDEX 绑 `Role/Es/es_AH_03`（即贝德维尔的大纹章/翅膀特效）。
- 如何进入：
  - `BulletAction1(Bullet:"esbullet", Action:"UDAA", Tag:"es_ultra")` @ `UltraDAend`（文本形式，`_es_bulletact_out.txt`）。
  - 数据驱动 `RoleId=10340101 + Action`：`UltraDAend→UDA / UDEX`；`UltraDashEX→UD / UDAEX`；
    `UltraDash/Air/EX→UDA0`（二进制 int32 形式）。
- **bullet id = 10340101**（唯一拥有 `esbullet` LogicRes 的 BulletConfig 行）。
- **否定原假设**：任务描述怀疑 “巨大纹章可能是不同 bullet id（不是 10340101）”。
  经①BulletConfig 全表只有 10340101 的 LogicRes 是 `esbullet`；②扫 `es_mono0.raw`/`esbullet_mono.raw`
  中所有 BulletConfig id 的二进制 RoleId，**只有 10340101 命中**（`_es_roidref_out.txt`）
  —— 未发现任何其它 id。故巨大纹章与普通纹章**共用 10340101**，靠 `bullet_action ∈ {UDA,UDA0,UDAA,UDAEX,UDEX}`
  与 `es_AH_01 / es_AH_03` 视觉区分。**没有找到第二个 id**。

### 3.5 其它相关生成
- `dashSkill` 族（`attackholdDash*`）→ bullet_action `dashSkill`：冲刺攻击纹章。
- `AD_hit`（`attackhold_hit2*`，带 targpos/scale）→ 蓄力普攻命中的纹章。
- `fallmdownend`（`fallmdownendEX*`）→ 落地冲击纹章。
- 遗留 `fallmdownendold` → **800002 / thunderbullet / "leibao"**（非纹章，旧数据仍保留）。
- `CreateBullet_summon`（`summon` / `summonEX`，Params 为空）——召唤，具体对象 **未确定**。

---

## 4. 任务四：相关 BulletConfig 行

字段号（`dump.cs:75041` `BulletConfig` / `dump.cs:139410` `BulletConfigFixedPoint`）：
`1=Id 2=LogicRes 3=StartAction 5=Width(f32) 6=Height(f32) 9=Scale 10=Skin 12=Resistance
13=CasterHpRatio 14=FixedHp 15=ForceOneDamage 20=ExtParams 41=SortingLayer 42=SortingOrder`。
表按 `(id, startAction)` 建键（`XlsxLoader_BulletConfig.Get(id, startAction)`，`dump.cs:173194`）。

| Id | LogicRes | StartAction | Skin | 归属 |
|---|---|---|---|---|
| **10340101** | esbullet | *(空)* | *(空)* | **ES 纹章（唯一一行）** |
| 100034 | teacher_esbullet | *(空)* | *(空)* | ES 的教学/训练副本 |
| 800002 | thunderbullet | *(空)* / "totem_behit" | – | 雷弹（`fallmdownendold` 用 "leibao"） |
| 800000 | commonbullet | *(空)* | – | 通用 |
| 800001 | firebullet | *(空)*/"dilei_fall"/"dilei_fall_s" | – | 通用火弹 |
| 800008 | surroundbullet | *(空)* | – | 通用 |
| 1001 | herolight | *(空)* | – | 通用 |

- `103401xx` 段（ES 专属）全表**只有 10340101 一行**；`teacher_esbullet` 是 100034。
- `esbullet` 行的完整字段（`_bulletparse2_out.txt`）：`f1=10340101, f2="esbullet",
  f5=0.1, f6=0.1, f23/24/25=1(PerformanceSkip*), f26=RenderConf(空), f41="MidActor"`；
  **f3(StartAction) 与 f10(Skin) 缺失 → 均为空**。
  由于空 StartAction，运行时 `Get(10340101, "x1")` 会 miss，再由 `createBulletImp`
  的**回落分支** `Get(id, <默认串>, ErrType)` 命中该唯一行（反汇编见任务五）。

**确认属于 ES 的 id**：`10340101`（纹章弹幕本体）、`100034`（teacher_esbullet，训练副本）。
其余 id 均为通用/其它角色，ES 的 ALG 里**未出现**（二进制 RoleId 扫描，`_es_roidref_out.txt`）。

---

## 5. 任务五：运行时调用链

### 5.1 签名（`dump.cs:244706-244795`）
```
// dump.cs:244749  RVA 0x1B548F0  VA 0x181B548F0
private BulletObj BulletMgr.createBulletImp(
    ActorBase caster, int idx, Fp2 pos, Fp2 dir, Fp damageScale,
    string startAction, SkillActivateFixedPointWrap skillActivate,
    ParamSet paramSet, string[] initAddTags)

// dump.cs:244746  公开入口
public BulletObj BulletMgr.CreateBulletByParams(
    ActorBase caster, int idx, Fp2 pos, Fp2 dir, Fp damageScale,
    string startAction, SkillActivateFixedPointWrap skillActivate, ParamSet paramSet)

// dump.cs:244743  数据驱动入口（AddRoleData）
public BulletObj BulletMgr.CreateBulletFromConf(ActorBase caster, AddRoleData addData)
```
- `idx` = bullet_id（BulletConfig 的 Id）。
- `startAction` = 脚本里的 `bullet_action`（如 "x1"/"dash"），也是决定用哪一行 BulletConfig 的键之一。
- `initAddTags` = 初始 tag（对应脚本 `tag`，如 "es_x"）。

### 5.2 createBulletImp 反汇编要点（`_disasm_out.txt`，RVA 0x1B548F0）
```
0x1b54bcc  call Xlsx::get_BulletConfigFixedPoint
0x1b54bef  call Xlsx.XlsxLoader_BulletConfigFixedPoint::Get   ; Get(idx, startAction)
0x1b54bfa  jne  -> ok
0x1b54c22  call ...::Get(...)                                  ; 回落 Get(idx, <默认串>, ErrType)
0x1b54c4b  call ActionLogicGroup::TryGet                       ; 用行的 LogicRes 取 ALG
0x1b54c88  call BulletMgr::GetFromPoolOrCreate                 ; 池化
0x1b557a4  call BulletObj::Init                                ; (caster, ALG, settings, conf, tags)
0x1b557c7  call BulletObj::StartBullet                         ; (skill, startAction, dir)
```
### 5.3 bullet_action 如何变成弹幕的起始动作
- `BulletObj::StartBullet(SkillActivateFixedPointWrap skill, string action, Fp2 startDir)`
  （`dump.cs:244821`，RVA 0x1B60540）内：
  ```
  0x1b609b6  call ActionMgr::ChangeAction     ; action 参数即 bullet_action
  ```
- 即：**脚本 params.bullet_action → `CreateBulletByParams(..., startAction=bullet_action, ...)`
  → `createBulletImp` → `BulletObj.StartBullet(..., action=startAction, ...)` → `ActionMgr.ChangeAction(action)`
  → 弹幕播放 esbullet ALG 中同名动作。**
- ES 的 `esbullet` 行 StartAction 为空、表又按 (id,startAction) 建键，因此 `x1` 这类
  并不存在专门行的 action，靠 `createBulletImp` 的回落查找仍能拿到该行配置，
  而**实际播放的动作由传入的 startAction 决定**，不依赖表里的 StartAction。

### 5.4 脚本层
`CreateBullet*` 是 ActionLogicGroup 的 `ActionScriptCall{Function, Params}`（`dump.cs:230827`，
位于 `ActionScriptCallWithTickRange` 内，`dump.cs:230842`）。Params 由 JS 包装层解析为
`<>f__AnonymousType34<bullet_id, bullet_action, targpos, bullet_target, pos_x, pos_y, dir_x, dir_y,
trigger, bullet_action_new, damage, scale, limit, limitKey, tag>`（`dump.cs:4555`）后调用
`BulletMgr.CreateBulletByParams` —— 与签名逐项吻合。

---

## 6. 未能解决 / 未确定清单（明确列出，勿当作已解）

1. **esbullet 每个动作的时长** —— 未取到（Fp 字段静态对齐不可靠；需反汇编 getter 或运行时探针）。
2. **`trigger` 参数（56161/56271/56061/11481）的条件语义** —— 只知道是触发 id，未反汇编其判定。
3. **动作 `q`** —— 字面长度 1 的串，疑为 `dead`；未确定。
4. **`CreateBullet_summon` 的召唤对象** —— Params 为空，未确定。
5. **“布鲁诺剑气”与 A1/B1/C1 的精确对应** —— 弹幕 id/动作已定，但“剑气 vs 纹章”的命名归属未确定。
6. **巨大纹章是否另有独立 id** —— 已用两种独立证据否定（LogicRes 唯一、二进制 RoleId 唯一），
   **未发现**第二个 id；若游戏内确有独立巨大纹章资源，其逻辑仍在 esbullet(10340101) 的 UDA 族里。
7. **AddRoleData 的完整字段**（标签、位置等）未逐字段解析，仅确认 `RoleId + StartAction` 的对应。
