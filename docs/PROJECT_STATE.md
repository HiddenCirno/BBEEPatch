# 苍翼：混沌效应 Mod 项目 — 现状梳理

> 用途：跨对话交接。**接手任何工作前先读这份**，不用重新推导。
> 最后更新：2026-10-02（架构重整第一轮：模块表 + 换色管线 + ES 拆分 + 面板去 CM 依赖）

---

## 0. 一句话现状

| 线 | 状态 |
|---|---|
| 特效换色 | ✅ 完成可用（已抽成独立管线 `Pipelines/Recolor/`） |
| 纹章解放接管（1+8 环） | ✅ 可用（已从连段模组里**拆出来**，见 §2.2） |
| 贝德维尔翅膀消失 | ✅ 已修（dash 关键字误伤） |
| 技能无耗 / 冲刺无敌 / 完美闪避 | ✅ 可用 |
| 配置面板 | ✅ 可用，**已不再依赖 ConfigurationManager**（自己挂 OnGUI 宿主） |
| **动作变速（平A加速）** | ⚠️ 机制打通，**手感未调好**（后摇/取消窗口被一起加速） |
| **连段模组（重排普攻）** | ⚠️ 机制打通（能按序列播放），**衔接时序未调好**（布鲁诺被打断 / 按快接不上） |

**两条未决线都不是"没做出来"，而是"做出来了但手感不对"。** 这是架构重整要解决的核心。

---

## 1.5 架构（2026-10-02 重整）

```
plugin/BlazblueJsPatch/
├─ Plugin.cs               启动引导：绑配置 → 装 UI 宿主 → 交给 ModuleHost
├─ Patcher.cs              只做"引导补丁"(JS 加载器转发 / DLC 解锁)，其余全交模块表
├─ Core/
│   ├─ ModModule.cs        模块描述(顺序/开关/结果) —— 「挂载」变成一个数据结构
│   ├─ ModuleHost.cs       挂载 + 逐模块异常隔离 + 启动时打一张【模块挂载表】
│   ├─ ModuleTable.cs      ★ 整个插件的挂载清单。加减管线只改这里
│   ├─ Cfg.cs              配置解析(列表/颜色/关键字+排除名单)
│   ├─ LogEx.cs            去重日志(新 key 永远打得出, 老 key 每 400 次提醒)
│   └─ Reflect.cs          IL2CPP 反射共用件(TryCast / IEnumerable / 成员查找)
├─ Ui/
│   ├─ UiHost.cs           ★ 自建 MonoBehaviour 当 OnGUI 宿主(不再寄生 CM)
│   ├─ ConfigPanel.cs      面板主体(段序 / 过滤 / 滚动 / 拖拽 / 保存)
│   ├─ UiTheme.cs          皮肤(贴图必须 hideFlags, 否则会被 GC 回收成"看不见")
│   └─ UiWidgets.cs        控件(含修好的文本输入框)
├─ Pipelines/Recolor/      ★ 特效换色 = 一条独立管线
│   ├─ RecolorConfig.cs    管线全部配置 + 目标色 + 策略版本号(零分配)
│   ├─ TintPolicy.cs       唯一的准入判据入口(屏幕空间/排除名单/名字过滤)
│   ├─ TintBrush.cs        真正上色的地方 + 【原值表】
│   └─ RecolorPipeline.cs  四个载体 + 追染
├─ Modules/Combat/         DashInvincible / PerfectDodge / JumpDashCrossReset / SkillCostTweak
├─ Modules/Es/             ★ ES 机体性能：EsActionSpeed / EsComboChain / EsEmblemBurst
└─ (其余为只读转储与诊断模块)
```

**三条设计约束（都是踩坑换来的）：**
1. **总开关语义 = 根本不挂载**，不是"挂上不干活"。配置项关闭只是提前 return，
   万一哪条写路径漏了检查，排查就会被误导。
2. **一条管线只有一个总开关入口**。换色的 `RecolorEffect` 曾经只拦住粒子路、
   插值器路照样染 —— 于是"关掉换色特效还在变色"，把人引向"有持久化缓存"这种错误结论。
3. **每个"没挂 / 跳过"都必须说明原因**。静默跳过 = 日志看起来像"这个模块没问题"。

---

## 1. 环境与部署

```
游戏   I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect
工程   <游戏>/_modding/plugin/BlazblueJsPatch/
产物   <游戏>/BepInEx/plugins/BlazblueJsPatch.dll
配置   <游戏>/BepInEx/config/ace.bbee.jspatch.cfg
日志   <游戏>/BepInEx/LogOutput.log
```

- Unity 2022.3.62f2 / IL2CPP / metadata v31 / 未加密 / ImageBase `0x180000000`
- BepInEx 6.0.0-be.788 + Il2CppInterop 1.5.3 + HarmonyX
- 构建：`dotnet build -c Release` → 手动 `cp` 到 `BepInEx/plugins/`
- 应急停用：把 dll 改名 `.disabled`

---

## 2. 模块清单（权威定义在 `Core/ModuleTable.cs`）

| 模块 | 文件 | 总开关 | 关键 hook | 状态 |
|---|---|---|---|---|
| `DashInvincible` | Modules/Combat/ | MountCombat | 动作名含 `dash` → 抬无敌等级（**必须配排除名单**） | ✅ |
| `PerfectDodge` | Modules/Combat/ | MountCombat | 冲刺全程触发极限闪避 | ✅ |
| `JumpDashCrossReset` | Modules/Combat/ | MountCombat | 跳跃/冲刺互重置 | ✅ |
| `SkillCostTweak` | Modules/Combat/ | MountCombat | 技能不耗 MP | ✅ |
| **`RecolorPipeline`** | Pipelines/Recolor/ | MountRecolor | 粒子路 / 插值器路 / Prefab 路 / 角色路 + 追染 | ✅ |
| **`EsActionSpeed`** | Modules/Es/ | **MountEsMech** | `ActionMgr.Update` / `TimeScales` 注入 | ⚠️ 手感 |
| **`EsComboChain`** | Modules/Es/ | **MountEsMech** | `PlayerSkillMgr` 链重写 | ⚠️ 衔接 |
| **`EsEmblemBurst`** | Modules/Es/ | **MountEsMech** | `BulletMgr.createBulletImp` | ✅ |
| `ActionStructure` | 根目录 | MountExtra | 动作结构转储 | 诊断 |
| `ActionJournal` | 根目录 | MountExtra | `ActionMgr.ChangeAction` 等 | 诊断 |
| `SkillChainDump` | 根目录 | MountExtra | 技能链转储 | 诊断 |
| `ActionProbe` | 根目录 | MountExtra | 打印见过的动作名 | 诊断 |
| `BulletProbe` | 根目录 | MountExtra | `createBulletImp` 打弹幕 | 诊断 |
| `AhWing` | 根目录 | MountExtra | 贝德维尔渲染器普查 | 诊断 |
| `ActionLimitTrace` | 根目录 | MountCombat | 诊断用 | 临时 |
| `SlowMotionTrace` | 根目录 | MountCombat | 诊断用 | 临时 |
| `ImguiCompat` | 根目录 | MountImgui | 顶掉被 strip 的 GUI 方法 | ✅ |
| `InputBlocker` | 根目录 | MountInput | 面板打开时屏蔽输入 | ✅ |

**总开关（cfg `[总开关]`）**：`MountJsPatches` / `MountRecolor` / `MountCombat` /
**`MountEsMech`（新增）** / `MountExtra` / `MountImgui` / `MountInput`

配置段（中文）：`总开关` / `冲刺无敌` / `特效换色` / `动作记录` / `动作结构` /
`连段模组` / `动作变速` / `纹章解放` / `贝德维尔` / `IMGUI`

### 2.1 面板：为什么不再需要按 F1
上一版面板的 OnGUI 入口挂在 **ConfigurationManager 的 OnGUI Postfix** 上，
所以必须先按 F1 把那个"本来就点不动"的原版 CM 窗口叫出来，我们的面板才会被画。
这是纯粹的架构依赖错误（自己的 UI 寄生在别人的 UI 上）。
现在 `UiHost` 用 `BasePlugin.AddComponent<T>()` 注入一个自己的 MonoBehaviour 当宿主，
CM 那条降级成兜底 —— 而且兜底判据是 **`UiHost.Alive`（Unity 真的调过 OnGUI）**，
不是 `Active`（挂载没抛异常）：只信后者的话，挂上但 Unity 不派发事件时会把兜底一起关掉，面板静默死掉。

### 2.2 纹章解放 vs 连段：曾经串在一起
`EsComboChain.Sequence` 的默认值里曾经有 `holdEX`（移动纹章解放）。
后果是**两件事互相绑架**：想调纹章得先改连段序列，而改连段又会改变纹章的触发时机。
现已从序列里移除（代码默认值 + cfg 实况**都改了**，因为 BepInEx 不覆盖已写入的项）。
纹章走它自己的路径（正常按纹章键 → `bullet_action` ∈ `x1/x2/ax2`），
`EsEmblemBurst` 单独一个模块、单独一段配置、单独一个总开关。

### 2.3 特效换色：从"一个文件里的四坨"变成一条管线
`EffectRecolor.cs`（1434 行）拆成 `Pipelines/Recolor/` 四个文件。同时修了两个真 bug：
- **字符串配置的输入框一个像素都不画** —— 见 §5 第 21 条。
- **插值器不跟配置及时变色 + 换色有累积亮度漂移** —— 见 §4.3。

### 2.4 字符串输入框为什么画不出来（定死了，别再猜）
`ImguiCompat.DoTextField` 里调的是 `GUI.TextField(rect, text, maxLength, style)` —— **四参重载**。
但本作 metadata 里 `GUI.TextField` **只有三参那一个**（`dump.cs` 全类只有
`TextField(Rect, string, GUIStyle)` 一条）。四参版本只存在于 interop 程序集，
是一根"一调就抛 `Method unstripping failed`"的空桩；异常被 `StringPrefix` 的 catch 吞掉后
返回 `null` → 输入框什么都不画。而 bool/int/float 走 Button + Label，全是活着的原生方法，
所以症状精确地表现为【只有字符型参数坏】。
修法：`UiWidgets` 自己调确定存活的三参 `GUI.TextField`，不再把关键控件压在模拟层上；
`ImguiCompat.DoTextField` 也一并改成三参（maxLength 只能丢掉，游戏没实现带长度限制的重载）。

---

## 3. 核心知识库（已确认的事实，勿重新推导）

### 3.1 动作系统（`GamePlay` 命名空间）

```
ActorBase            (非 MonoBehaviour! 无 transform/GetComponentsInChildren)
  0x30 ActorModel
  0x78 ActionMgr

ActionMgr
  0x50 CurrentAction (GameActionLogic)
  0x68 ActionRunner  (ActionLogicRunner)
  0x80 Time          (Fp, Q32.32)  ★ 动作时钟
  0x118 ?

ActionLogicRunner
  ActionStart(logic) / ActionUpdate(logic, t0, t1) / ActionTimeEnd(logic) / ActionExit(logic,time)
  ★ ActionUpdate 按【时间区间】派发逻辑指令(判定窗口/特效/事件)

GameActionLogic
  0x20 Name        0x28 Animate      0x30 AnimateDuration
  0x40 TotalDuration (= 动画时长，四段普攻二者相等)
  0x48 EnableAnimationTimeMap   0xC8 AutoEndActionWhenMotionEnds(=False)
  0xA0 TimeScales   ★ 原生"这个动作在第几秒到第几秒以什么倍率播"
  0x110 ActionSwitchs (接招窗口)   0x118 Interrupt   0x120 CDList
  0x190 HitDataList ★ 攻击判定段(攻击 vs 后摇 的原生分界)

DeltaTimeAndScale (struct, 24 字节)
  0x0 m_RealDeltaTime  0x8 m_TimeScale  0x10 m_DeltaTime   (全 Fp)
  SetRealTimeAndScale(realDt, scale) → 三个字段【全写】(已反汇编确认)
```

### 3.2 技能 / 连段系统 ★ 本轮最大收获

```
PlayerSkillMgr
  0x10 Owner(PlayerObj)   0x30 m_SkChains (PlayerSkillChain[])   下标 = PlayerSkillGroup
  静态 s_skConfForActorCached : List<SkillActivateFixedPointWrap>

PlayerSkillChain
  0x18 SkillList (List<PlayerSkill>)   ★ 连段模组本体
  0x24 SkillType    0x28 Cur(PlayerSkillChainCurSkill)   0x30 LastSkill
  0x64 CastTimes    0x68 m_Input
  方法: DoUpdate / DoUpdateAndCheckInputSucc / findAndStartSkill_Imp /
        FindAndStartSkillFromMidByOrder / findNextSkillMatchPreOrderAndInputDir /
        findStartingSkillMatchInputDir / StartSkill / startSkill

PlayerSkillChainCurSkill { 0x10 Skill, 0x18 TimeEllaps }

PlayerSkill (0x18 SkillId / 0x1C ActorId / 0x20 SkillActivate(Wrapper) /
             0x28 AttrOrder / 0x44 Input)

SkillActivateFixedPoint (protobuf, 全链共享的表行)
  0x18 actorId  0x1C group  0x20 order  0x28 action  0x40 input  0x48 inputDir
  0x50 reqTriggerId  0x58 mps  0x60 preSkillOrder ★  0x70 actdurStrict ★★
  0x78 timeout  0x90 preinputtime ★★  0x98 useLongPress / 0xA0 longPressStart / 0xA8 longPressEnd
SkillActivateFixedPointWrap { 0x10 data }
```

**★★★ 2026-10-02 运行时定案：`findStartingSkillMatchInputDir` 的候选闸门是【动作层】。**

技能链按 `SkillList` 的**数组顺序**逐个试，其中决定性的一道过滤是
`ActionMgr.CheckCanChangeToAction(候选的动作名)` —— **"我现在能接到谁"由动作的接招表说了算**。
实测（训练场，`[段数限制] 拒绝切换` 只在被拒时打印）：

```
当前动作=attackAEX:  attackAEX ✗  attackA ✗  attackB ✓   ← Cur 0→2(跳过 attackA)
当前动作=attackB:    attackAEX ✗  attackA ✗  attackB ✗  attackC ✓
```

**这解释了"为什么插一段平A 之后还能接布鲁诺2/3"**：下一段是谁**不看连段历史**，
只看"当前动作能接到谁"。`preSkillOrder` 在 group 2（Skill1）里全是空的，本来就给不出顺序。
详见 `ACTION_CHAIN_SYSTEM.md §5.6`。

**连段推进的完整机制（反汇编 `DoUpdateAndCheckInputSucc` 得到）：**

```
if (Status==4||5) return false
if (MuteRemain>0) return false
Cur = this.Cur
if (Cur.Skill != null && Cur.TimeEllaps < Cur.Skill.SkillActivate.ActdurStrict) return false  ← 硬地板
...输入检查...
return findAndStartSkill_Imp(-1)
      → findNextSkillMatchPreOrderAndInputDir   ★ 按 PreSkillOrder + InputDir 找下一段
      → 找不到就 findStartingSkillMatchInputDir  ★ 退回找"起手段"
```

**三个时序字段的语义（反汇编定死，勿再猜）：**

| 字段 | 消费者 | 语义 |
|---|---|---|
| `ActdurStrict` | `CurSkill::IsFixDurComplete` / `DoUpdateAndCheckInputSucc` / `startSkill` | **硬地板**：`TimeEllaps < 它` → 那一帧**根本不进推移逻辑**（`DoUpdateAndCheckInputSucc` 直接 return false） |
| `Preinputtime` | **`findAndStartSkill_Imp`** | ★★ **按键有效期**：判定式 `(now - 按下时刻) <= Preinputtime`。**是从"你按下的那一刻"起算，不是从动作结尾倒推**（见下） |
| `Timeout` | `CurSkill::IsTimeOut` / `DoUpdate` | 超时判定 |

★★★ **`Preinputtime` 的语义曾经被记错，2026-10-02 反汇编定案**：
```
findAndStartSkill_Imp  RVA 0x1bb3320
  0x181bb3569  cmp byte ptr [r14+0x58], 0      ; InputCmdState.PressInputSatisfied
  0x181bb356e  jne  → 拒绝                      ; 按过一次就不再接受(同一次按键不重复触发)
  0x181bb3583  call get_Preinputtime
  0x181bb35b2  Preinputtime <= 0 ?
  0x181bb35e1     → 用【静态默认窗口】(static_fields+0x68)   ← ★ 0 不是"没有窗口"
  0x181bb3615  r9 = [r14+0x38]                  ; InputCmdState.LastPressTimeStamp
  0x181bb3628  call IInput.GetTimeEllaps(LastPressTimeStamp)   ; age = now - 按下时刻
  0x181bb364e  op_GreaterThan(age, window) → 拒绝
```
**即：`接受 ⟺ (当前时间 − 按键按下的时间戳) ≤ Preinputtime`。**
`LastPressTimeStamp` 在**按下**时写入（`OnInputCmdChange` 按下分支），
**松手不清除它**（松手只写 `LastUnpressTimeStamp`）——
所以按一下松手，这次按键仍然在 `Preinputtime` 秒内保持"有效"。

**推论（这才是手感的根因）：**
- 判定发生在 `ActdurStrict` 地板之后 —— 按键太早（早于地板开启超过 `Preinputtime` 秒），
  到地板开启时**已经过期**，于是被丢弃。这就是"按快反而接不上"。
- 想"早按也能接上"要**调大** `Preinputtime`（按键活得更久），而不是改 `ActdurStrict`。
- 早到多早还能接上 = `地板开启时刻 − Preinputtime`。

⚠ 旧反例记录仍然成立但表述要改：`ActdurStrict=0` **不是**"一按就断"，它表示**没有硬地板**（那一帧就会进推移逻辑）。
而 `Preinputtime=0` **不是**"窗口只剩一瞬"，它表示**回落到游戏内置的默认窗口**。
两个 `0` 的真实含义都和直觉相反 —— 之前照抄原生 `0` 值调不出手感，根因就在这里。

### 3.3 ES(103401) 的链结构（实测转储）

**链[1] Attack = 一条 13 段的长链**，靠 `InputDir` 区分输入方向：

```
槽     动作         InputDir   ActdurStrict  PreInput  Timeout
1.1~1.3  AttackUp*   Up         0             0/0.25    0
1.4      attackD1    Down       0.35          0         0.70   佩利诺尔1
1.5      attackD1    Down       0.35          0         0.70
1.6      attackD2    Down       0.35          0.30      0.75   佩利诺尔2
1.7      attackD3    Down       0.30          0.30      0.65   佩利诺尔3
1.8      atkAirX     Down       0             0.20      0      ★佩利诺尔4(空中收尾)
1.9      attack1     Any        0.30          0         0.70   平1
1.10     attack2     Any        0.30          0.20      0.60   平2
1.11     attack3     Any        0.35          0.20      0.80   平3
1.12     attack4     Any        0             0.20      0      平4(收尾,不可取消)
1.13     atkAirX     Any        0             0.20      0
```

**链[2] Skill1 = 19 段**（技能键）：
```
2.1 attackAEX / 2.2 attackA / 2.3 attackB / 2.4 attackC      ← 地面布鲁诺(全 ActdurStrict=0, PreInput 0/0/0.3/0.3)
2.5~2.8  attackA_AirEX / attackA_Air / attackB_Air / attackC_Air
2.9~2.13 UltraDashEX / UltraDash / UltraDashAirEX / UltraDashAir / UltraDAend
2.14 holdEX ★纹章解放  2.15 hold
2.16~2.19 attackAir2EX / attackAir2 / attackAirEX / attackAir
```

`SkillInputDirType`：`Any=0 Up=1 Down=2 Front=3 Back=4 NoDir=5`
`PlayerSkillGroup`：`None0 Attack1 Skill1_2 Ultra3 AttackAir4 Dash5 DashAttack6 Jump7 LongAttack8 Summon11 Burst12`

### 3.4 弹幕 / 纹章 ★ 2026-10-02 大幅修正（详见 `bullet_emblem_map.md`）

- 纹章 = `BulletObj : ActorBase`，`Id=10340101`，`LogicRes="esbullet"`
- 生成汇点：`BulletMgr.createBulletImp(...)`
  签名 `createBulletImp(caster, idx, pos, dir, damageScale, startAction, skillActivate, paramSet, initAddTags)`
  → `Xlsx.BulletConfigFixedPoint.Get(idx, startAction)` → `ActionLogicGroup.TryGet(LogicRes)`
  → `BulletObj.Init` → `StartBullet(skill, action=startAction, dir)` → `ActionMgr::ChangeAction(action)`
  **即 `bullet_action` 直接就是弹幕的起始动作名。**
- `BulletConfig` 180 条，按 **(id, startAction)** 作键。
  **★ 全表只有一条 `LogicRes="esbullet"`（10340101）**（另有一条 `teacher_esbullet` 是训练场变体）。
  **贝德维尔的"巨大纹章"不是另一个 bullet id** —— 就是同一个 10340101，
  被驱动进 `UDA0/UDA/UD/UDAA/UDAEX/UDEX` 这些**动作状态**而已。
  （验证：`bulletconfig.ab_BulletConfig.bin` 里 `esbullet` 字面量只出现 1 次）
- **★ ActionLogicGroup 的序列化格式（解析它的前提）**：
  字符串是 `[u32 小端长度][ASCII][补 0 到 4 字节]`；一次脚本调用 =
  **两个连续字符串** `{Function, Params}`（`ActionScriptCall`，dump.cs:230827）。
  ⚠ 旧笔记里的 `CreateBulletX / CreateBulletU / CreateBulletm` **不是函数名** ——
  那个 `X/U/m` 其实是后面 Params 长度前缀的低字节。ES 真正用的是
  `CreateBullet` / `CreateBulletIfTriggerChange` / `CreateBullet_summon`（esbullet 里是 `CreateBullet_`）。
- **纹章的来源有两条路，不止 CreateBullet：**
  1. `es.ab` 里 26 条 `CreateBullet*` 指令（冲刺族→`dash/dashAir/dashSkill`、下劈→`fallmdownend`、
     平A追加→`a3`、上挑→`aup`、跳跃/下落→`attackAir`、蓄力→`x1`、rush→`x3`、rushUp→`xup`…）
  2. **★ 72 条数据驱动的 `AddRoleData(RoleId=10340101, StartAction=…)`** ——
     **普通攻击携带的纹章全部走这条路**，不是走 CreateBullet：
     `attack1→a0`、`attackA/B/C→A1/B1/C1`（**布鲁诺剑气 = 这三个**）、`attackAir→ax1`、
     `attackAir2→ax2`、`jump2/3→jump`、`UltraDash*→UDA/UDA0/UD/UDEX/UDAEX`。
  拉全量清单：`tools/_es_bulletact_out.txt`。
- 纹章解放 = `holdEX`/`hold` → `x1 → x2`（tag `es_x`）—— **确认**；
  `x3/x32`（rush）、`xup/xup2`（rushUp）、`ax1/ax2`（空中）同族。
- `esbullet` 状态机：76 个动作块，每块绑自己的 VFX（`Role/Es/…`）与 Hit id；
  转移如 `A1→A1EX`、`B1→B1EX`、`C1→C1EX`、`a→a0`、`DAA→DAA2`、`aup→aup2`。
- 未确定（勿编）：各动作时长（Fp 字段，静态对齐不可靠）、`trigger` id 的语义
  （56161/56271/56061/11481）、无 tag 调用的触发条件。

### 3.5 其他

- 特效换色：粒子 `startColor` 烘在 prefab 实例 + 材质 `_TintColor` 走 `MaterialColorInterpolator`
- 本地化反查：`ActorActionName_<id>` / `ActorActionDesc_<id>`，hash 用 `CalculateHash`
- ES 招式 id：340061=纹章解放 340081=崔斯坦 340281=布鲁诺 340321=莫德雷德 340391=高文 340501=贝德维尔
- 崔斯坦空中链：`fall → fallupd(踩踏) → fallup2 → fallup22 → fallup2d → fallup3 → fallm → fallmdown → fallmdownendEX2`

---

### 3.6 ⚠ 本节的结论已被实测推翻 —— 见 3.6.1，勿再引用本节作为依据

（保留原文以便追溯推理过程；**当前正确状态以 3.6.1 为准**。）

**原文（已被推翻）**：主张"连段推进是 JS 驱动的，`preSkillOrder` 搜索只是输入驱动的支线"。

**推翻它的实测（2026-10-03 晚，构建标记 16:02:22，一场地面+空中实战）**：

```
FindAndStartSkillFromMidByOrder（"JS 漏斗"）        被调用 0 次   ← 原生连段根本不走它
findNextSkillMatchPreOrderAndInputDir + 兜底搜索    被调用 105,000 次  ← 是热路径，不是支线
findAndStartSkill_Imp（真正起招）                   被调用 41 次
[选段] 探针的有效输出                                4 条，全部是 900~904（我们自己的链）
```

**推论**：
- "JS 能调它" ≠ "主路径是它"。**从调用点反推特例为通例**，这是本次错误的形态。
- 搜索是每帧、每条链都在跑的热路径（多半来自 `DoUpdate → predictNextSkill` 的预判），
  十万次里绝大多数是空转（`cur<0 且 result=null`）——**以前日志里选段行很少是真的没什么可报**，
  不是日志通道丢数据（这一轮计数器与探针产出对上了，探针是健康的）。
- **原生布鲁诺/佩利诺尔的推进既不走搜索、也不走 JS 漏斗 —— 是第三条路，尚未找到。**
  这是当前最关键的未解问题。

### 3.6.1 连段推进：目前确认与未确认（2026-10-03 晚）

**已确认**：
- `findAndStartSkill_Imp(int startOrder = -1)` 是起招入口；`startOrder >= 0` 时**跳过**两条搜索
  直接按 Order 起招（反汇编：两条搜索的 call 在其函数体前段，条件分支之后）。
- 两条搜索只在**输入驱动**路径上产出有效结果；我们自己的链（900→901→…）就是这么跑通的。
- ES 的 `s_SpecFuncMap` 里确实注册了一组 `ActorJs_hz.<>c.<b__12_N>`，
  其中 `b__12_4`（RVA 0x17343A0）逻辑为：
  `if (BulletMgr.GetBulletsByCasterAndActionGroupAndTag(...).Count == 0) chain.FindAndStartSkillFromMidByOrder(HeroConfig.Quality + 1);`
  ——**存在**，但实战中未被触发（0 次）。

**未确认（下一步）**：
- 原生连段推进到底走哪条路。候选：
  (a) 某个 `b__12_N` 直接改链游标 / 调其它 `PlayerSkillChain` 方法（不是 FindAndStartSkillFromMidByOrder）
  (b) `findAndStartSkill_Imp(startOrder>=0)` 被谁以显式 Order 调用（`[按Order找]` 41 次里非搜索的那些）
  (c) `PlayerSkillChainCurSkill.StartSkill` / `SetStatus` 侧的推进
- **建议**：先给 `findAndStartSkill_Imp` 的 postfix 补上**入参 startOrder 与调用来源**，
  把 41 次调用分成"走了搜索的 4 次"和"没走搜索的 37 次"，后者就是答案所在。

### 3.6.1b ★★★★★ 起招的完整调用图（2026-10-03 晚，**实测确认**，以此为准）

**唯一汇点：`PlayerSkillMgr::SkillStartImplement(PlayerSkill psk, Fp2 lastInputDir, PlayerSkillChain chain)`**
（dump.cs:249511，RVA 0x1BDBD30，返回 void）

```
路径 A 输入驱动
    DoUpdateAndCheckInputSucc → findAndStartSkill_Imp(-1)
        → findNextSkillMatchPreOrderAndInputDir / findStartingSkillMatchInputDir
        → SkillChangePreCall → PlayerSkillChain::startSkill ─┐
                                                              │
路径 B 按动作名                                                ├→ SkillStartImplement
    ChangeSkillByActionName(动作名)                            │
        → PlayerSkillChain::StartSkill → SkillChangePreCall ──┤
                                                              │
路径 C JS 桥（地面布鲁诺走的就是这条）                          │
    M_SkillChangePreCall / M_SkillStartImplement ─────────────┘
```

**验证过程（怎么确认的，供追溯）**：
- 挂 `PlayerSkillChain::StartSkill` → 只捕到路径 B，且**全是空中动作**（`atkAir12/atkAir3/attackA_AirEX`）
- 挂 `SkillChangePreCall` → **地面布鲁诺三段都在**（attackAEX/attackB/attackC）
- 而 `StartSkill(+0x233)` 和 `findAndStartSkill_Imp(+0x392)` 两处探针**都没出地面布鲁诺**
  → 剩下的调用者只有 `M_SkillChangePreCall`（JsPort 桥）→ **路径 C 成立**
- 挂 `SkillStartImplement`（汇点）→ **什么都看得见**：我们的 900~906、佩利诺尔、地面布鲁诺 1/3/4、空中、dashAir

**⚠ 两条被推翻的中间结论（同一形态，记下来别再犯）**：
1. "连段推进是 JS 驱动，`preSkillOrder` 搜索只是支线" → 错。JS 漏斗
   `FindAndStartSkillFromMidByOrder` **实测 0 次调用**；两条搜索**实测 105,000 次**（是热路径）。
2. "`PlayerSkillChain::StartSkill` 是起招的唯一汇点" → 错。它和 `findAndStartSkill_Imp` 是
   **并列**的两条路，另有 JS 桥完全绕开两者。

**共同教训**：**不要从「调用者列表」推断架构**。调用者是静态的，只能说明"可能"，
不能说明"实际走哪条"。今天两个错结论都是这么来的，纠正全靠实测（计数器 / 探针产出）。
**能挂汇点就挂汇点，别逐个堵入口。**

### 3.6.1c ★★★ 两条链完全隔离（实测），以及"穿插"的正解

汇点探针按 `chain` 分组的实测输出：

```
"attackAEX" Order=1   链段数=19  链=0x…C900   ┐ 地面布鲁诺全程在【同一条链】里
"attackB"   Order=3   链段数=19  链=0x…C900   ├ 从不碰我们的链
"attackC"   Order=4   链段数=19  链=0x…C900   ┘
"attack1"   Order=900 链段数=18  链=0x…CA80   ┐ 我们的平A链
"attack2"   Order=901 链段数=18  链=0x…CA80   ┘
```

**结论：跨链跳不存在。** 布鲁诺的推进不会跳到我们的链上，我们的段也进不去它那条链。

**所以"穿插"的正解不是"给原生链补接线"、也不是"改 JS 那张表"，而是：
把要用的段全部复制进【我们自己的一条链】—— 用已经跑通的克隆+接线机制。**

用户的设计稿
```
佩1 - 佩2 - 平1 - 平2 - 平3 - 佩3 - 佩4 - 平4 - 平5 - 平6
```
就只是**一条链里的一个线性序列**，不需要图、不需要跨链、不需要碰 JS。

**剩下唯一的技术问题**：同一段后面接两个方向不同的分支（佩2 之后：无方向→平1，下+攻击→佩3）。
现在我们的段**全部 `InputDir = Any`**，分不开。要做的：
1. `Sequence` 语法里给每段带方向（如 `attackD2[Down]`），MakeSkill 写进 `InputDir`
2. **做一次"Any vs 具体方向谁优先"的实验**（原生按 下+攻击 进佩利诺尔而非平A，
   说明方向明确的赢或数组序在前 —— 但这个规则必须实测确认，否则分支优先级是碰运气）

### 3.6.1d ★★★★★ 接的是「格」不是「状态机」；`PreSkillOrder` 本来就是多前驱（2026-10-03 晚，实测）

#### (1) 两层结构，别混

| 层次 | 是什么 | 装什么 |
|---|---|---|
| **技能行（段/"格"）** | 接线用的一格 | `Order`（编号）、`preSkillOrder`（**前驱列表**）、`InputDir`（方向）、Timeout/ActdurStrict/Preinputtime |
| **动作（`GameActionLogic`）** | 那一格播什么 | **真正的状态机**：动画/判定/弹丸/内嵌脚本/`ActionSwitchs`/`NextAction` |

**连接发生在"格"与"格"之间，按 `Order` 号匹配**（不是按名字）。
所以一个**原生格**能连到一个**我们的克隆格** —— 名字完全无关，只要编号对得上。

**我们克隆的是"一整格"**（段数据 + 它包着的动作状态机），
⇒ **克隆体天然继承原版的入边**。这就是"佩2 能接平4"白送的原因 —— 边是原版 `attack4` 自带的。

#### (2) `PreSkillOrder` 是多前驱 —— 原生靠它实现穿插

实测（关掉改写、观察原生）：
```
从 4("attackD1") 找下一段 -> 6("attackD2")     佩1 → 佩2  (InputDir=下)
从 4("attackD1") 找下一段 -> 11("attack3")     佩1 → 平3  (InputDir=任意)
从 6("attackD2") 找下一段 -> 12("attack4")     佩2 → 平4  (InputDir=任意)
从 9("attack1") → 10 → 11 → 12                 平1→平2→平3→平4
```
**同一起点能到两个不同的段** ⇒ `attack3` 的前驱列表里同时写着 `10`(平2) 和 `4`(佩1)。

**⚠ 我们的 `MakeSkill` 里写着 `po.Clear(); po.Add(prevOrder);`** ——
把原版填好的多前驱**删成了单前驱**。原注释说"模板的 preSkillOrder 指向链外，留着走不动"
——**那是当年克隆"平A模板行"时的必要操作，现在克隆的是目标动作自己的原生行，前提已变**。
**不是缺机制，是我们把机制删了。** 已改成【并集】（保留原生边 + 追加我们的顺序边）。

#### (3) 方向优先于 Any（实测，决定性）

同一格（原生佩2，Order 6）出发，两条不同结果：
```
下 + 攻击  →  7("attackD3")     (InputDir=下)
只按攻击   →  906("ace7_906")   (InputDir=任意，我们的克隆)
```
**方向明确的赢。** 设计分支时可以放心混放。

#### (4) ⚠ 当前缺陷：入边不可控（**下一步要修的**）

现在 `preSkillOrder = 原生边 ∪ 我们的顺序边`，**原生边一条不落地全保留** ——
"你想不想要，你说了不算"。链一复杂就会莫名其妙多出旧关系。

**规划中的接线配置**（尚未实现）：
```
Chain1 = 1 | attack1,attack2,...
Link   = S7 <- S2, attackD2        # 第7段可从第2段、以及【原生 attackD2】来
```
词汇：`S3` = 我们链里第 3 段；`attackD2` = 原生那一格。
默认策略三档：`ChainOnly`（建议默认，纯直线零意外）/ `Native`（保留原版边，穿插白送但不可控）/ `Explicit`（只按 Link）。

#### (5) 方法论（**今天最贵的一条**）

**不要从「调用者列表」推断架构。** 调用者是静态的，只能说明"可能被调用"，
不能说明"实际走哪条"。今天三次给"X 是主路径/唯一汇点"的结论，三次被实测推翻：
- §3.6 "JS 是主路径" → 那个 JS 漏斗实测 **0 次**调用
- "`PlayerSkillChain::StartSkill` 是唯一汇点" → 它和 `findAndStartSkill_Imp` **并列**，另有 JS 桥绕过两者
- "原生段不会被我们的段影响" → 错，并集之后原生边会接进我们的克隆

**能挂汇点就挂汇点，别逐个堵入口。** 唯一可靠的汇点是
**`PlayerSkillMgr::SkillStartImplement(psk, lastInputDir, chain)`** —— 三条路全到这里，实测什么都看得见。

### 3.6.1e ★★★★★ 【动作移植】全部经验固化 —— 重写 MakeSkill 前必读（2026-10-03 收尾）

#### A. 移植的单位是「一整格」，不是一个动作

**一格 = 技能行(段) + 它包着的 `GameActionLogic`（真正的状态机）**，整套一起搬。

| 层 | 字段 | 属性类别 |
|---|---|---|
| 段 | `Order` / `preSkillOrder` / `InputDir` | **链位置** |
| 段 | `Timeout` / `ActdurStrict` / `Preinputtime` | **动作时序** |
| 段 | `Input` / `PrecheckActionCd` / `AllowGround` / `AllowFlying` | 准入 |
| 段 | `StartTrigger`/`ExitTrigger`/`ReqTriggerId`/脚本/`BulletId` | **动作表现（必须保留原样）** |
| 动作 | `Animate` / 判定 / 弹丸 / 内嵌脚本 / `ActionSwitchs` / `NextAction` | 状态机 |

#### B. 必须重写的字段（**全部来自"原生行是给别的链用的"这个前提**）

| 字段 | 原生值 | 为什么要改 | 改成 |
|---|---|---|---|
| `Action` | 原动作名 | 会被原链认领（两条链同时指向同一动作） | **独有名** `aceN_xxx` |
| `PrecheckActionCd` | 1 | 准入条件过不去，选得中起不来 | 0 |
| `Timeout` | 0 | `TimeEllaps>=0` 恒真 → 游标第一帧被 Reset | 宿主值 × 窗口系数 |
| `Input` | `"Skill"` | 链不听攻击键 | 宿主（`"Attack"`） |
| `ActdurStrict` | 0 | 硬地板；0 = 无 | 宿主值 |
| `Order` | 原生 | 会被原链的 Order 空间撞上 | 我们分配（900+） |
| `preSkillOrder` | 原生前驱 | 见 D —— **不要清空，要并集/可控** | 见 D |
| `InputDir` | 动作自带 | **它是链位置属性**，见 C | 按链声明 |
| `TimeScales` | **共享同一个 List** | 变速注入会串到原版动作 | **克隆体必须有自己的空表** |
| `CDList` | 原动作的 | 链冷却串味 | 宿主值 |

#### C. 方向（`InputDir`）是「链位置」属性，不是动作属性

同一个 `attack3`：在平A链里要 `任意`，在佩利诺尔链里要 `下`。
**实测：方向明确的段优先于 `Any`**（同一起点，下+攻击→佩段，单按攻击→Any段）。

#### D. `preSkillOrder` 是多前驱 —— 原生的穿插就靠它

`attack3` 的前驱 = `{平2, 佩1}`；`attack4` = `{平3, 佩2}`。
**我们原来写的 `po.Clear(); po.Add(prev)` 把它删成了单前驱** ⇒ 链只能是直线。
现改为并集。**但并集带来的问题是入边不可控** —— 链一复杂就会莫名其妙多出旧关系，
所以模块化必须给**接线控制权**。

#### E. 起手（入口）与「接管」—— 今天最后踩的坑

**起手段 = `preSkillOrder` 为空的段**，靠 `InputDir` 竞争。
**同动作的原生段只要还在链里，就会抢在我们的克隆段前面**（数组序靠前）。
实测：新佩利诺尔链里我们的 900 一次都没被选中，打的全是原生佩段。

⇒ **`Sequence` 里出现的动作，原生那一格必须移除。**
（否则"重做佩利诺尔"实际上是"新佩链和老佩链并存打架"）
⚠ 只改目标链（链[1]）；`attackAEX`/`attackB` 在链[2]，**原版布鲁诺不受影响**。

#### F. 观测纪律

唯一可靠的汇点是 **`PlayerSkillMgr::SkillStartImplement(psk, lastInputDir, chain)`**。
别逐个堵入口（`StartSkill` / `findAndStartSkill_Imp` / `ChangeSkillByActionName` / JS 桥
是**并列**的四条路，堵不完）。详见 §5.1.1 与 §3.6.1d(5)。

#### G. 模块化目标形态（待实现）

```
[连段模组]
# 一条链一行: <目标链号> | <段序列>
#   段语法: 动作名[方向][+]      方向=上/下/前/后/无/任意(缺省=继承源动作)
#                              尾部 + = 允许作为起手段
Chain1 = 1 | attackD1[下]+, attack3[下], attackAEX[下], attackD2[下], \
             attack4[下], attackB[下], attackD3[下], atkAirX[任意]

# 接线（可选）。缺省 = 只能从链内上一段来
Link = S7 <- S2, attackD2

# 是否移除原生同动作段（见 E）
TakeOverNative = true
```
**配置按语义分区**：链位置属性（顺序/方向/接线/起手）在连段模组；
动作自身属性（窗口/前摇/倍率）在变速模块。

### 3.6.2 原文保留（供追溯）

**这是本日最重要的发现，它换掉了整条设计路线的前提。**

反汇编 + 调用点统计（一场地面+空中布鲁诺实战）：

```
PlayerSkillChain::findAndStartSkill_Imp（入口）              被调用 41 次
  ├ findNextSkillMatchPreOrderAndInputDir（我们一直在研究的搜索）  1 次（且返回"没找到"）
  └ findStartingSkillMatchInputDir（兜底）                        2 次

PlayerSkillChain::FindAndStartSkillFromMidByOrder（JS 直接指定 Order 起招）
  ← GamePlay_PlayerSkillChain_Wrap::M_FindAndStartSkillFromMidByOrder   (JsPort 桥 JS→C#)
  ← ActorJs_hz_HangAnimController.AnmAndTime::<initSpecFuncMap>b__12_4  ★ ES 角色 JS
```

**结论：正常游戏里，链怎么走是【角色 JS 算好了直接下达"起 Order=N 那一段"】，
不是靠"找出 preSkillOrder 包含当前段的候选"。**

`PreSkillOrder` 那套搜索是**输入驱动的那条支线** —— 真实存在（我们自己的链就是靠它跑通的），
但**不是原生连段的主干**。

| 现象 | 解释 |
|---|---|
| 布鲁诺能在平A中间穿插 | **JS 让它穿插的** |
| 我们造的段之间能连（900→901→…） | 走输入驱动支线（唯一接上的路） |
| 我们的段接不进佩利诺尔/布鲁诺 | JS 不认识我们的段，不下指令；输入路径又够不到原生"起手段" |
| `[兜底] 起手段查找 → 900` | 输入路径失败后唯一的退路 |

⚠ **过程教训**：§6.1 早就写着"找游戏逻辑先看 `JsPort`，搜 `ActorJs_`"，
我们记了却没照做，之后好几轮都在一个 `ActorJs_hz_HangAnimController` 完全没参与的
机制上做推理（`PreSkillOrder` 图、多前驱接线）。**"谁在调链推进"这个问题本该最先问。**

**下一步入口**：读懂 `ActorJs_hz_HangAnimController.AnmAndTime` 的 `b__12_4`，回答三问：
1. 什么条件下调 `FindAndStartSkillFromMidByOrder`（按动作名？动画进度？输入？）
2. 它传的 Order 来自哪张表 → **那张表就是要改写的东西**
3. 它认不认 `aceN_*` → 若不认，正是"穿插失败"的直接原因，也是我们要补的表

### 3.7 连段模组现状（2026-10-03）

**已跑通**：新平A链（十段 900~908）全部克隆、命名、注入动作表、接线、可加速、窗口可调。

| 配置 | 作用 |
|---|---|
| `Sequence` | 段序列（源动作名） |
| `CloneMode` | `All`(整链克隆，终态/默认) / `ExceptHost`(平A四段留原名) / `Collision`(只在名字碰撞时克隆) |
| `WindowOverrides` | 逐动作窗口：`动作名:后摇,前置输入,硬地板,允许地面,允许空中`，值按**现实秒**给、内部乘速度倍率 |
| `ChainWindowScale` | 后摇窗口随速度放大的系数（-1=自动跟随组倍率） |
| `TimeoutBonus` | 补"借来的段冷却与 Timeout 撞车"的结构性缺口 |
| `[动作变速] Group1..6` | 每组 `倍率 \| 动作名列表`；**只认名字，绝不按内容匹配**（隔离） |

**方法（可复用于任何动作移植）**：
1. 克隆目标动作的 `GameActionLogic`（`Clone()` 运行时不可用，走整块字段拷贝）+ **给 `TimeScales` 一张自己的表**（否则变速串到原版）
2. 换独有名字注入 `ActionLogicGroup` 的 `_actions`(0x78)+`m_cacheAction`(0x88)，`AddComponent` 前必须 root 住防 GC
3. 技能行照抄原生（保住触发器/脚本/弹幕/剑气），只覆盖"链上下文"字段：
   `Action` / `Input` / `PrecheckActionCd`(0xB8) / `Timeout`(0x78) / `ActdurStrict`(0x70)
4. 时序与冷却继承宿主（被替换的那条平A行）
5. 窗口三件套逐动作可调

**已知未解**：`FindAndStartSkillFromMidByOrder` 那条 JS 主干（见 3.6）。

### 3.8 ★★★ 双线穿插设计（2026-10-03 收尾，**已在游戏里实测通过**）

⚠ 上面 3.7 里的段号（900~908）已过时，以本节为准。

**两条线都在组 1 的同一个 `SkillList` 里**（我们追加的 16 段），靠 `InputDir` 区分：

| 线 | 站桩起手 | 段序（名字 → Order） | 方向 |
|---|---|---|---|
| **普攻线** | `攻击` | 平1(907) 平2(908) 佩2(909) 布1(910) 平3(911) 布2(912) 平4(913) 佩3(914) 佩4(915) | 全 `任意` |
| **下段线** | `下+攻击` | 佩1(900) 平3(901) 布2(902) 佩2(903) 布2(904) 平2(905) 纹章解放(906) | 全 `下`（纹章解放 `任意`） |

#### ★★ 用户记法：普攻线 `2467`，下段线 `35`

- 普攻线第 **2/4/6** 段（平2 / 布1 / 布2）按 `下+攻击` → 拐进下段线的 **佩2(903)**
- 普攻线第 **7** 段（平4）按 `下+攻击` → 拐进下段线的 **布2(904)**
- 下段线第 **3/5** 段（两个布2）按 `攻击` → 拐回普攻线的 **平4(913) / 佩3(914)**

**这两个数字完整编码了全部 6 条 Link 边**，对应配置：

```
Link1 = 903 <- 908,910,912
Link2 = 904 <- 913
Link3 = 913 <- 902
Link4 = 914 <- 904
```

#### 三条铁律（都踩过）

1. **绝不 Link 到起手段**（900 / 907）。起手段的定义是 `PreSkillOrder` **为空**，靠 fallback 按数组顺序选中；一旦给它加前驱就不再是起手段 → **站桩按键再也进不去**。
2. **方向自动分流，不需要额外开关**。`Link` 只加前驱边；走哪条由**目标段的 `InputDir`** 决定，且「具体方向 > 任意」。两线一全 `下`、一全 `任意`，正好天然对立。
3. **接线前必须先跑环检测**。同时存在 `913←904` 和 `904←913` 这类回边就会成环，可以无限循环。判据：**落点若在回边起点的下游**（914/915 在 913 下游）则天然无环。

#### 新增工具 `tools/_bulletscan.py`

把 `[弹幕探针]` 与当时的 `【技能】/【动作】` 按时间轴对齐，输出「**动作 → 它发射的弹幕 action**」表。做"给某动作附别动作的弹幕"（`[纹章解放] AttachBullets = 触发弹幕:追加弹幕:延迟秒`）时必备，**不用再试错**。

已确认的对照表：

| 段 | 源动作 | 弹幕 | | 段 | 源动作 | 弹幕 |
|---|---|---|---|---|---|---|
| 平1 | `attack1` | `a0`,`a02` | | 佩1 | `attackD1` | `aD12` |
| 平2 | `attack2` | `a20`,`a22` | | 佩2 | `attackD2` | `aD22` |
| 平3 | `attack3` | `a32` | | 佩3 | `attackD3` | `aD32` |
| 平4 | `attack4` | `a42` | | 佩4 | `atkAirX` | `attackAir2` |
| 布2 | `attackB` | `B1` | | 纹章解放 | `holdEX` | `x2` |

#### 已知未解

`atkAirX` **克隆注入失败**（`[注入|miss|atkAirX] 动作表里找不到源动作`，疑似空中变体按需加载）。原版动作仍会经过 `BulletMgr` 汇点，所以不影响它被当"附弹幕的来源"；但**若要把佩4 作为链上的一段，必须先修这个**。

### 3.9 ★★★ 地面冲刺为什么"按了没反应" —— 是【链上的沉默】，不是接招表（2026-10-04，反汇编确认）

**症状**：地面上冲刺时再按冲刺键毫无反应；但空中冲刺可以被普攻打断 ⇒ 接招表本身允许取消，
拦路的另有其人。

**反汇编定位**（这是根因，不是推测）：

| RVA | 方法 | 关键代码 |
|---|---|---|
| `0x1BB10F0` | `PlayerSkillChain::DoUpdateAndCheckInputSucc` | `Status(+0x20)==4\|\|5 → false`；**`MuteRemain(+0x54) > 0 → false`**；`TimeEllaps < ActdurStrict → false` |
| `0x1BB28C0` | `PlayerSkillChain::SetMute(int muteBy, Fp muteTime)` | `[+0x50]=muteBy`；**`[+0x54]=[+0x5C]=muteTime`** |
| `0x1BB0F60` | `PlayerSkillChain::ClearMute` | 把 `+0x54` 等清 0 |
| `0x1AE8740` | `ActionMgr::CheckCanChangeToAction` | 整个函数体 = `ActorCountDown.GetCountDown(name) <= 0`（只读、无副作用） |
| `0x1BB3B20` | `PlayerSkillChain::findStartingSkillMatchInputDir` | 起手搜索：`Quality` → `IsStartingSkill` → `CheckSkillCanCast` → `PrecheckActionCd==1` 时再 `CheckCanChangeToAction(SuccessAction)` → `Input!=0` → `CheckPlayerSkillInputDir` |

`PlayerSkillChain` 字段表：`+0x10 Owner(PlayerSkillMgr)` / `+0x20 Status` / `+0x28 Cur` / `+0x40 CoolDownRemain` /
**`+0x50 MuteBy` / `+0x54 MuteRemain` / `+0x5C LastMuteMax`** / `+0x68 m_Input`。
`PlayerSkillMgr.Owner(+0x10)` = `PlayerObj`；`Cur(+0x28) → Skill(+0x10) → SkillActivate(+0x20) → data(+0x10) → action_(+0x28)`。

**实测印证**：地面冲刺一起手本链就 `闸门[Status=2 CD=0.600]`（那个"CD"就是 `MuteRemain`），
而冲刺动作本身只有 **0.25s** ⇒ 结束后还被自己锁 0.35s。
**旁证（很硬）**：`JumpDashCrossResetUnlimited` 当时已经是 `true`，`CheckCanChangeToAction` 那道门早就无条件放行了，
可还是按不出来 ⇒ 说明门在**更前面** —— 就是 `DoUpdateAndCheckInputSucc` 开头这道沉默，
它一旦返回 false，起手搜索**根本不会被调用**。

**做法（`JumpDashCrossReset.ChainPrefix`）**：在 `DoUpdateAndCheckInputSucc` 的 **prefix** 里，
若「本链当前段 = 地面冲刺动作」且是本机玩家，就清掉本链 `MuteRemain/MuteBy`。
- 挂在 prefix 而不是 postfix ⇒ **同一帧**就生效（postfix 要等下一帧）
- 只清"当前段就是地面冲刺"的那条链 ⇒ 玩家的其它链（高文链等）的沉默不受影响
- **不碰 `ActdurStrict`**：那是原生硬地板，留着它快速冲刺才不会退化成瞬移连冲
- 空中冲刺（`dashAir`）不在范围 ⇒ 依旧走三段冲预算；地面冲刺不再消耗空中冲刺预算
- 开关：`[冲刺无敌] GroundDashCancelDash`（默认 true）

⚠ **自查踩到的坑（写下来免得重犯）**：保险条件用了 `_dashLeft <= 0`，
而 `_dashLeft` 的初值是 **-1 = "还没算过"**，它的初始化原本只发生在 `Precheck`/`ChangePostfix` 里
—— 玩家若只在地面上冲刺（不跳、也不走预算分支），这里就恒读到 -1，
**整个功能会被这条"保险"静默关掉**（正是本项目反复栽的"静默 return"）。已在 `ChainPrefix` 里补 `ResetBoth()`。

**方法论**：`tools/_disasm.py` 新增 `--at <RVA> [--name X]`，可直接反汇编尾调用到的匿名 sub
（`CheckCanChangeToAction` 就是把活干在 `0x1A...640` 的）。

### 3.10 跳跃无敌（2026-10-04）

`Modules/Combat/DashInvincible.cs` 从"冲刺族专属"扩成**两族共用一套状态**：
`moving = IsDashName(name) || (JumpInvincible && IsJumpName(name))`，尾巴/等级/按 Actor 分桶全部复用。
- 用户要的"前0.2秒-全程-后0.2秒"在本模块里的形状就是：**动作进行中一直给无敌（开头自然在内）+ 结束后 TailSeconds 尾巴**
- 实测动作名干净：`jump` / `jump2` / `jump3`（默认无须排除名单，但 `JumpExcludeKeywords` 键留着备用）
- ⚠ `IsDashingNow` **故意只认冲刺**：唯一调用方是完美闪避，跳跃无敌 ≠ 跳跃期间算极限闪避（两者按用户要求互斥）
- 开关：`[冲刺无敌] JumpInvincible` / `JumpActionKeyword` / `JumpExcludeKeywords`

### 3.9b ★★★★ 技能表可以【离线静态读】—— 别再靠运行时探针猜数值（2026-10-04）

`extracted/skillactivate.ab.bin` 是 **Google.Protobuf 生成类**的资产，而 `dump.cs` 里
`SkillActivateFixedPoint` 带 `public const int XxxFieldNumber = N` ⇒ **字段号 ↔ 字段名是白给的**：

| f# | 名字 | f# | 名字 | f# | 名字 |
|---|---|---|---|---|---|
| 2 | ActorId | 15 | PreSkillOrder | 25 | Preinputtime |
| 3 | Group | 16/17 | AllowActive/PassiveState | 26 | UseLongPress |
| 4 | Order | 18/19 | **AllowGround / AllowFlying** | 27/28 | LongPressStart/End |
| 5 | Action | 21 | **ActdurStrict** | 31 | LastingDuration |
| 6/7 | Start/ExitTrigger | 22 | **Timeout** | 32 | PrecheckActionCd |
| 8 | Input | 23 | TimeoutAddcd | 37 | BulletId |
| 9 | InputDir | **24** | **Mutelist** | 51 | ActionpointInputTag |

工具：`tools/_dashdata.py`（Fp = Q32.32，除 2^32）。

**★ `Mutelist` 就是"冲刺冷却"的真身**（此前一直当成引擎硬编码）：
格式 `"all":0.1,"1":0.6,"4":0.6,"5":0.6,"8":0.6` —— **按【技能组号】记该组被沉默多久**。
ES 的 `dash`(组5 序8)/`dash2`(序9)/`dash3`(序10) 都对 组1/4/5/8 沉默 **0.600s**
⇒ 正好是实测的 0.600s 沉默；`dashend`(序11) 的 Mutelist 为空 ⇒ 沉默不是它给的。

同表还纠正了两个猜测（**看数据 > 猜**）：
- `dash` 的 `ActdurStrict = 0`、`Timeout = 0` —— **硬地板根本不是拦路的东西**。
  （原打算改 ActdurStrict，读了表才知道改了也没用）
- `dash`/`dash2`/`dash3` 的 `PreSkillOrder` 全为空 ⇒ 三段都是**起手段**，
  所以链推进走的是 `findStartingSkillMatchInputDir`（起手搜索），不是"下一段"搜索。
- `dash` 的 `AllowGround=1 / AllowFlying=None`（地面专属）✓、`dashAir*` 反过来 ✓。

`findAndStartSkill_Imp`（0x1BB3320）的真实顺序（反汇编）：
```
sk = findNextSkillMatchPreOrderAndInputDir(startOrder)      // ① 按 PreSkillOrder
if (sk == null) sk = findStartingSkillMatchInputDir(startOrder)  // ② 起手段搜索
if (sk == null) return false
if (inputMap[sk.Input] == 0) return false                   // ③ 输入表里查得到才算
```

**结论（2026-10-04 三次排查，探针实测）**：
```
[快速冲刺:诊断] 起手搜索(当前=dash) 返回 <null>
```
⇒ **冲刺动作进行中，游戏自己的起手搜索返回 null**（找不到能立刻起的段），
所以按键被吞 —— 与"沉默"无关（沉默早已清掉：`清掉…0.58s → CD 0.000`；整场 `切换【失败】` 0 次）。
而且 `dash/dash2/dash3` 的 `PreSkillOrder` 全空 ⇒ "下一段"搜索 `findNextSkillMatchPreOrderAndInputDir`
同样恒 null。**两条路都是 null ⇒ 原生就是不让"冲刺打断冲刺"。**

**修法：接管起手搜索**（`JumpDashCrossReset.StartingSearchPostfix`，postfix + `ref __result`）。
判据是反汇编确认的：`findStartingSkillMatchInputDir`(0x1BB3B20) **只有一个调用方**
= `findAndStartSkill_Imp`(0x1BB3320) 的第 ② 步 ⇒ 改它的返回值**不会**污染 UI 的"下一招"预测。
- 只在【游戏递回空 或 递回当前这一段自己】时接管，且只在"本链当前段 = 地面冲刺"时
- 挑下一段完全照抄原生三段设计：同组、**动作名同族**（`dash`/`dash2`/`dash3`，去尾数字比较）、
  有 `Input`、`IsStartingSkill`；走到头绕回第 1 段 ⇒ `dash → dash2 → dash3 → dash` 循环
  （同族判据是必需的：否则会递到 `attackholdDashEX`/`dashAAendEX` 这些名字里也带 dash 的别的招）
- 只递【技能对象】，起招仍走游戏自己的 `StartSkill`/`ChangeAction` ⇒ 链的记账不乱
- 开关同 `GroundDashCancelDash`；日志 `[快速冲刺] 接管起手搜索: 当前=dash -> 递 "dash2"`

## 4. 两条未决线的精确卡点

### 4.1 动作变速（`AttackSpeed.cs`）

**已打通**：
- `Lever=Inject`：往 `GameActionLogic.TimeScales` 注入 `ActionTimeScaleRange` →
  游戏自己的 `GetTimeScaleByTime` 会用它 → **动作时钟/动画/位移/事件全部同步加速**（正道）
- `Lever=Time`：直接写 `ActionMgr.Time`(0x80) + 手动补派发 `ActionRunner.ActionUpdate(t0,t1)`
  → 能变速、纹章也修好了，但**动画不跟**、属于 hack
- 反汇编确认：`SetRealTimeAndScale` 三字段全写；`get_DeltaTime` 是裸字段读

**卡点**：
- `Lever=Dt` 无效 → **Harmony 对值类型形参的 `ref` 写回传不到原生**（重要结论）
- 全程加速会把**取消窗口**一起压缩 → 搓招手感变
- `SplitByHitData`（用 `HitDataList` 终点切"攻击段/后摇"）+ `AccelRatio` 已实现但**手感没调好**

### 4.2 连段模组（`AttackChainTweak.cs`）

**已打通**：
- 链载体定位：`m_SkChains[1].SkillList`
- 克隆 `SkillActivateFixedPoint` → 只改 `Action`（保持平A语义）→ 插入 10 段
- **`PreSkillOrder` = 上一段的 `Order`** ← 这是链能推进的关键（反汇编发现的）
- 幂等标记：`Order>=900` 哨兵值（**不要用动作名当标记**，见踩坑）
- 回滚：整条链快照 + 重建
- 实测已能按配置序列播放 10 段

**卡点**：
- 布鲁诺段被后面的段打断（`ActdurStrict` 用平A值 0.30 → 允许取消）
- 照抄原生值（`ActdurStrict=0`, `PreInput=0`）→ 又不被打断但**窗口只剩一瞬**，接不上平3
- 当前折中：`ActdurStrict=0`(原生) + `PreInputSeconds=0.3`(覆盖) → **仍不理想**
- **按快时剑气放不出来** ← ★ **根因已定位（2026-10-02）**：
  `Preinputtime` 被当成了"动作尾部的窗口"来理解，实际是**按键有效期，从按下的瞬间起算**。
  判定在 `ActdurStrict` 地板**之后**才跑，所以按太早 → 键盘时间戳过期 → 丢弃。
  能早到多早 = `地板开启时刻 − Preinputtime`。
  **改善手感要调大 `Preinputtime`，不是动 `ActdurStrict`。**
  详见 §3.2 的判定式与 `ACTION_CHAIN_SYSTEM.md §7`。
  另外 `PreInputSeconds=0` 会**回落到游戏内置默认窗口**（不是"没有窗口"），
  这点也让之前"照抄原生 0 值"的调法失效。

### 4.3b ★★★ 换色的"漏染"排查（2026-10-04，用户点名四类：敌人受击+受击纹章 / 布鲁诺剑气拖影 / 冲刺拖影 / 多段跳脚下粒子）

**载体现状（`TintBrush`）**：只刷两种 —— ① `ParticleSystem.main.startColor` ② `MaterialTinter` 的颜色插值器。

**离线看 prefab**（新工具 `tools/_fxinspect.py`：merge.json 给 (偏移, 清单文件)，切出 UnityFS 块交给 UnityPy）：

| prefab | 里面的载体 | startColor 模式 |
|---|---|---|
| `es_dash_02`（冲刺拖影） | hub + `tuowei01`/`lizi01`/`Line`(带 MaterialTinter) | **全 0 = Color** |
| `es_rushup_01`（起跳/多段跳脚下） | hub + `02`/`03`/`11` 粒子 | 全 0 |
| `hit_009`（受击） | hub(`L0`/`L1`) + `star01`/`glow01`/`sharp001` + MaterialTinterProxy | 全 0 |
| `es_esbullet_dasha_001`（剑气） | hub + `es`/`line01`/`tuowei01`/`sj01` + 2 个 MaterialTinter | 全 0 |

⇒ **颜色全都在 ParticleSystem.startColor 里，且都是纯色模式** —— 载体本身不是问题，问题是**有没有被访问到**。

**修的两处过滤（有效）**：受击特效的实例名就叫 `hit_009`（没有 `es` 前缀），被
`EffectNameFilter="es"` 整族跳过；prefab 路径 `AssetFilter="Role/Es"` 也盖不到 `Effect/Prefab/Hit/<皮肤>/`。
⇒ 两个都改成**逗号分隔多关键字**：`EffectNameFilter = es,hit_`、`AssetFilter = Role/Es,Prefab/Hit`。
⚠ 用 `hit_` **带下划线**：`hit` 会误伤任何含它的名字（最典型是 **white**）。
另外把出生口从 `createVisualEffect`+`Reactivate` 扩到 hub 自己的
`DoStart`/`EnableVisualElements`（受击/纹章/剑气是别的系统拉的 hub，原来根本不经过那条漏斗）。

**★ 事故：`new MinMaxGradient(color)` 会把渐变换成纯色 ⇒ 屏幕上一个实心圆**（用户实测：冲刺纹章、纹章解放）。

**★★ 别猜 interop 暴露了什么 —— 照着程序集摊开看**（方法：`tools/refl/` 那个 30 行控制台程序，
`Assembly.LoadFrom(BepInEx/interop/*.dll)` + `AssemblyResolve` 把 `BepInEx/core` 也加进搜索路径，
然后把 `GetFields/GetProperties/GetConstructors` 全打出来）。
第一次我用"猜名字 + 反射 GetFields"结果 **0 命中**，直接把整条粒子路弄挂（日志：
`MinMaxGradient 字段: mode=False color=False min=False max=False`，表现是"特效染色全挂、只剩插值器"）。
摊开之后的**实际形态**（`UnityEngine.ParticleSystem.MinMaxGradient`）：

| 成员 | 形态 | 可写? |
|---|---|---|
| `m_Mode` (`ParticleSystemGradientMode`) | **属性** | ✔ get+set |
| `m_ColorMin` / `m_ColorMax` (`Color`) | **属性** | ✔ get+set |
| `color` (`Color`) | 属性 | ✘ 只读（老代码卡在这） |
| `m_GradientMin` / `m_GradientMax` (`Gradient`) | 属性 | ✔ get+set |
| `.ctor(Color)` / `.ctor(Color,Color)` | 构造 | — |

`Gradient` 也可写：`colorKeys`（`Il2CppStructArray<GradientColorKey>`，get+set）、`alphaKeys`、`mode`、`m_Ptr`；
`GradientColorKey` 是值类型、公开字段 `color`/`time`。
枚举 `ParticleSystemGradientMode`：**Color, Gradient, TwoColors, TwoGradients, RandomColor**。

⇒ 正确写法（**不需要反射、不需要装箱**）：
- `Color`（0）→ 改 **`m_ColorMax`**（★ Unity 把唯一那个颜色存在 max 里，离线 dump prefab 也印证：
  state=0 时 `maxColor` 才是真颜色，`minColor` 是没用的白）—— 保留它的 alpha，淡出包络才在
- `TwoColors`（2）/ `RandomColor`（4）→ 改 `m_ColorMin` + `m_ColorMax`
- `Gradient`（1）/ `TwoGradients`（3）→ 逐 `colorKeys[i].color` 改 RGB（原值表按 `Gradient.m_Ptr` 记一次）
- 最后 `main.startColor = g` 写回

⚠ 老代码的两处错都在这一条上：① 用 `color`（只读、且不是真颜色）取原色 ② 用 `ctor(Color)` 整个换掉结构。
两件叠加 = 渐变丢失 + alpha 变 1 = **实心圆**。

**★ 同样被否掉的：材质/拖影路**（写 `renderer.material` 的 `_TintColor`）。
用户原话"拖影不能这么写"：对加色混合的特效，材质那层的颜色**不是最终颜色**，
写进去会变成实心圆并误伤别的特效 ⇒ 已改成开关 `TintRenderers`，**默认 false**。

**★ 冲刺拖影的正主 = 公共 prefab `Effect/Prefab/Common/silhouette_601`**（用户提供的线索："拖影是玩家形象，
冲刺时玩家身上有一层蓝色叠加"。离线解开证实）：

```
[GameObject] silhouette_601  + VFXEffectHub
[GameObject] Trail           + ActorTrailProxy      ← 复制玩家形象的拖影
[GameObject] Other           + MaterialTinterProxy ×2  ← 蓝色叠加层的颜色来源
```

名字里没有 `es` ⇒ 一直被 `EffectNameFilter` 挡掉（日志原话
`战斗特效 "Common.silhouette_601" 不含名字过滤 "es", 跳过 (Owner=null, 非本地玩家)`）。
⇒ 两个 filter 加 `silhouette`。⚠ 它是**公共**的（全游戏只有这一个 silhouette 资产）⇒ 敌方拖影也会一起变红。

**★ 普查兜底（`RecolorPipeline.CensusOnce`，挂在 `VFXEffectBase.Update`）**：
`es_rushup_*`（多段跳的环）和 `es_esbullet_dasha_*`（剑气拖影）实测**一次都没被任何出生钩子看到**
（既没进 `createVisualEffect`，也没触发 hub 的 `DoStart`/`EnableVisualElements`/`Reactivate`）。
所以不再追出生口：`VFXEffectBase.Update` 是每个活着显示的特效都会走的 ——
**首次见到（按 GameObject 指针去重）就染一遍**，与"谁生的"彻底无关。
判据仍走 `TintPolicy.AllowInterpolator`（屏幕空间/排除名单/名字过滤一条都不放宽）；池化复用由 `Reactivate` 那条负责。
日志 `[特效换色:普查] 首次见到 "X" (N 处) [载体 …]` 顺带就是场上特效的**完整清单**。

**★ 三处补齐（2026-10-04 第二轮）**：
1. **两份名字过滤曾经不一致**（老问题"判据没写在一处"的又一例）：
   `EffectNameFilter`（战斗路）已经是 `es,hit_,silhouette`，而 `TintNameFilter`（插值器/普查/追染路）
   还是老的 `"es_"` ⇒ `silhouette_601` **过了战斗路那道门（日志 `LoadAsset … 命中=True`）、
   却在这里被挡掉**，表现成"命中=True 但画面上没变"。修：`TintNameFilter` 留空即沿用 `EffectNameFilter`，
   且按逗号拆多关键字（原来只当单个子串比）。默认值改成 `es,hit_,silhouette`。
2. **prefab 级染不动 silhouette**：它的 `MaterialTinterProxy` 在 prefab 里数组是**空的**（运行时才填）
   ⇒ `已改 Prefab … (0 处)`。所以必须**实例级**去染（普查/追染/出生口），这也是普查存在的意义。
3. **子弹路**（剑气拖影）：`es_esbullet_dasha_*` 从来不经过 `AssetBundleProvider.LoadAsset`
   （48 条 asset 行里一条都没有）、也不触发 hub 的任何出生口 ⇒ prefab 引用烘在 `BulletConfig` 里、
   由子弹系统直接实例化。修：挂子弹唯一漏斗 `BulletMgr.createBulletImp`（postfix，`__result` 是 `BulletObj`，
   `ActorBase.gameObject` 是公开属性）⇒ 新建子弹就染它的 GameObject，复用同一套去重与名字判据。
4. hub 出生口再补 `Awake`/`Start` —— **只要 hub 被实例化，Unity 一定会调 Awake**，
   比 `DoStart`/`EnableVisualElements`/`Reactivate` 更难被绕过（多段跳的环就属于一直没露面的那类）。

**★★ 引擎自己的换色机制（2026-10-04 拆解完成，来源：JS 转储 + pbdef + xlsx 配置）**

JS 转储在 `BepInEx/config/BlazblueJsPatch/sources/`（700 个已加载模块，**排查 JS 侧逻辑先来这里找**，别去挖 bundle）。
`Gen/pbdef.js` 里的 proto 定义说清了全部：

```proto
SkinColor  { string prop = 1; int32 id = 2; }        // 皮肤色 = 着色器属性名 + 颜色id
SkinEffect { map<string,string> skinToEffect = 1; }  // ★ 皮肤 → 专属特效 prefab
AvatarSkinConf { id heroId painting position mainTexPath extraTexPath
                 transfer[]  color[]:SkinColor  effects[]:SkinEffect }
```

⇒ 引擎换色只有两件事：**① 按皮肤换一整套特效 prefab（这就是"专属特效"）② 按 `prop` 名往材质套颜色**。
`GamePlay/AvatarUtil.js` 的 `Init()` 把 `AvatarSkinConf.All` 编成 `prop → position` 的查表（`SkinPositionLut` / `AvatarSkins`）；
运行时用的是固定点镜像（`AvatarSkinConfFixedPoint` / `SkinEffectFixedPoint` / `SkinEffectFixedPoint.SkinToEffectEntryFixedPoint`），
加载器是 `Xlsx.XlsxLoader_AvatarSkinConf : XlsxLoader<AvatarSkinConf>`。

**★ 游戏用的属性名（`data/xlsx/avatarskinconf.ab` 解出来，不是猜的）**：
`_Emission` `_EmissionX` `_EmissionY` `_EmissionZ` `_EmissionA` `_Skin1` `_Skin2` `_Skin3` `_Skin4`

**★ 我们自己的 `propmiss` 日志抓到的（本管线在用、原先不在白名单）**：
`_SubTexTintColor`(**最多, 29 次**) / `_DecoTexTintColor` / `_DissolveColor` / `_HighlightColor` / `_BrightColor` / `_AmbientColor`
⚠ `_RemapColorFrom` / `_RemapColorTo` 是一对"把 A 重映射成 B"的参数，**两个都改 = 抹平成纯色**（同"实心圆"那类事故）⇒ 故意不进白名单。

**JIT 方案（下一步，用户提的"用同样的方式改"）**：
既然运行时是 `AvatarSkinConfFixedPoint` 那份配置 + 颜色表 `actor/avatarskincolors.ab`（`SkinColor.id` → 颜色），
就可以**改内存里的配置/颜色表**，让游戏自己把它认得的所有东西染成我们的色 —— 不必再逐个载体追。
待办：① 解出颜色表（`avatarskincolors.ab` 格式还没解出来）② 确定"谁在读配置并把颜色套下去"那个汇点。

**★★ 材质路（2026-10-04，按用户要求"基于游戏自己的路线重做"的第一步）**

缺口是这么发现的：`LoadAssetPostfix` 开头是

```csharp
var go = Reflect.Cast<GameObject>(__result);
if (go == null) return;      // ← 材质资产在这里被直接丢掉
```

而特效的颜色**大量烘在共享材质上**（`Effect/Common`、`Effect/Material` 那批 bundle）——
实例侧的 tinter 是空数组，所以日志出现"tinter 改写了 3~8 处、画面纹丝不动"（剑气拖尾就是这一类；
载体报告 `渲染器=4~7 拖影=0` 说明那根拖尾是 MeshRenderer 的带子）。

修：`LoadAssetPostfix` 现在**也收 `Material`**，且只认路径/名字含 `Effect` 的（UI/场景材质不碰）；
按 `TintProperties` 写 `Tint(原值)` —— **和插值器每帧写的是同一个函数**，所以两者不打架，
没插值器的材质（烘死的）就靠这一步。原值按 (材质指针, 属性名) 只记一次 ⇒ 可逆、不累积。
与"实心圆"事故的区别：那是**凭空塞一个不透明亮色**；这里是**照原值算**（保留原 alpha、沿用原量级）。

日志：`[特效换色] 已改材质 "X" (N 条属性) ref="Effect/Material/…"`。
⚠ 材质是**共享**的 ⇒ 改了就是全局生效（符合"整体换主题"的取向），但也可能牵到复用它别的东西 —— 靠上面这行核对。

**诊断（下次排查直接看这些行）**：
`已改战斗特效 "X" (N 处) [载体 粒子=3 渲染器=5(拖影=1) Tinter=1  Tinter属性=_TintColor/_HighlightColor]`
—— 载体清单 + Tinter 里真实属性名。清单空 = 没东西可染；属性名不在 `TintProperties` = 改配置即可。

### 4.3c ★★★ 尾焰为什么染不上（2026-10-04，用自建「特效解剖」查出来的，**实据**）

`esbullet`（剑气那一颗）里有 4 个材质，**只有一个是火焰着色器**：

| 材质 | 着色器 | 用在 |
|---|---|---|
| **`turbulence_008_k2`** | **`NOAH/Effect/Variant/Flame`** | **`feng02`** ← 尾焰 |
| `particles_006_a` | `NOAH/Effect/Variant/Common` | `lizi02` |
| `pattern_656_a` | `NOAH/Effect/Variant/Common` | `jianqi01`（剑气本体） |
| `glow_002_n` | `NOAH/Effect/Variant/Common` | `glow01` |

尾焰材质上的颜色（**运行时实读**）：
```
_InnerFlameColor  : Color=(0.823, 1.200, 2.770, 0.427)   内焰(蓝)
_OutterFlameColor : Color=(0.770, 1.307, 4.595, 0.141)   外焰(蓝, alpha 很低)
_SubTexTintColor  : Color=(2.814, 1.517, 1.235, 1)       ← 在白名单里, 但不是 Flame 的主色
_FlameDetail      : Texture=turbulence_008_4r            噪声图, 无色
```
⇒ **两个主色都不在 `TintProperties` 里**，而且这颗材质**没有插值器**
（同一次日志 `Tinter属性=<无插值器>`）⇒ 粒子路/插值器路都够不着。
**这就是尾焰一直是蓝的的全部原因。**

**修法（窄口径火焰路，`TintBrush.TintFlameMaterials`，开关 `[特效换色] TintFlame` 默认开）**：
只对 `sharedMaterial.shader.name` 含 `"Flame"` 的渲染器写**实例**材质（`renderer.material`，
Unity 自动复制 ⇒ 不污染共享资产），并跳过同节点真在驱动这些属性的 tinter。
**为什么不用宽口径 `TintRenderers`**：那条会对每个特效渲染器都动手 —— 2026-10-04
"角色被染色 / 实心圆"就是它引进来的。这里用**只读**的 `sharedMaterial.shader.name` 先筛，
非火焰渲染器连实例材质都不会建。

**副产品：`_TintColor` 的值印证了材质确实被写过** —— `pattern_656_a._TintColor=(2.996,0.999,1.248)`
= 3:1:1.25，正是目标色 `C04050` 的比例（`Tint()` 保亮度换算的结果）。

⚠ **别再用宽口径材质路去试**：那种做法没有 shader 级别的边界，等于把整棵特效树的材质都改一遍。

### 4.3d ★★★ 冲刺"叠加层"的来龙去脉（2026-10-04，离线 diff + 字段树，**全部实据**）

工具：`tools/_prefdiff.py`（把两个 prefab 的结构签名拉平成可比较的行，只打差异）、
`tools/_mbdump.py`（把 MonoBehaviour 的**序列化字段树**摊开 —— `SerializeReference` 的插值器
都能读到字段名和值，这是拿到下面那三个插值器的关键）。

**皮肤特效套装实况**（全量扫的）：

| 套装 | 文件数 | 内容与原色不同的 prefab |
|---|---|---|
| `esskin_06` | **103**/105（缺 `es_energy_03`、`es_energy_03a`） | **75/105** |
| `esskin_10` | **103**/105（同上） | **83/105** |
| `esskin_12` | 105/105 | 1/105 |
| `esskin_13` | 105/105 | 0/105 |

⇒ **"有专属特效的换色皮肤丢了叠加层"= esskin_06 / esskin_10**；12/13 是完整拷贝，结构与原色一致。

**叠加层本体 = `es_dash_01` 里那个 `Other`（挂 MaterialTinterProxy）**：

```
原色        : hub + guangzhu01(粒子+MaterialTinter+MaterialCollector) + Refrac(GrabPass) + ★Other(MaterialTinterProxy)
esskin_06/10: hub + guangzhu01 + Refrac                                              ← Other 整个不存在
esskin_12/13: 与原色完全一致
```
`Other` 上三条插值器（duration 全 0.3s）就是"叠加层淡入"：
```
MaterialColorInterpolator  (0, 0.776, 1.0, a=0) → (0, 0.145, 1.0, a=1)      蓝 + alpha 0→1
MaterialFloatInterpolator   1.0 → 1.0                                      (占位)
MaterialColorInterpolator  (0,0,0, a=0)         → (1.72, 1.72, 1.72, a=1)   白亮 + alpha 0→1
```
同一个 prefab 里 `guangzhu01` 的粒子色也被皮肤烘过：原色 `(0.420,0.554,1.000)` 蓝 →
06 版 `(1.000,0.602,0.599)` 粉。

**结论：健康版本的皮肤不是"把叠加层调透明"，而是那套资产里根本没做这个物体。**

**修法（用户要的是【关掉】，不是补上）：`Modules/Common/EffectSuppress.cs`「特效裁剪」**
规则写成 `特效名片段:子物体名`，挂在 `VFXEffectHub` 的 `Awake`(前缀+后置)/`DoStart`/
`Reactivate`/`EnableVisualElements` + `ActorEffectMgr.createVisualEffect` 上，
匹配到的子物体 `SetActive(false)`（`SuppressMode=Destroy` 则直接销毁）。
默认名单只有一条 **`es_dash_01:Other`**。

⚠ **绝不能按"名字叫 Other 就关"**：全表 504 个特效 prefab 里有 **85 个**含
`MaterialTinterProxy@Other`（`buff_*` / `dead_*` / `portal_*` / `superarmor_*` / `flash_*` …），
一刀切会把它们全废掉。这也是"按名字一刀切"这个老坑的又一例。
⚠ 想连冲刺残影一起关的话，那是公共资产 `silhouette_601`（所有皮肤共用，含
`MaterialTinterProxy@Other ×2 + ActorTrailProxy@Trail`），加一条 `silhouette_601:Other`。

### 4.3e ★★ 冲刺那道"蓝光"= 角色冲刺残影族（`Role/Avatar/*`），**被我们自己的排除名单挡着**

用户问"冲刺会有一个蓝光特效，它是哪来的"。离线定位（`tools/_fxscan.py`，按
`VFXEffectExtension.ScreenSpace` 和粒子颜色全量扫）：

- 屏幕空间（全屏）特效全表只有 **14 个**，与冲刺无关 —— 所以**不是**"被 SkipScreenSpace 跳过的全屏层"。
- 正主是这一族（都在 `effect/prefab/role/avatar/`）：
  | 资产 | 结构 | 颜色 |
  |---|---|---|
  | `avatar_dashshadow_ap_01/02` | hub + `First`/`Last`(TransformMotor/EffectBoneFollower 绑骨骼) + **`Trail`(LineRenderer + MaterialTinter + MaterialCollector + TrailFollow)** | 材质 `_HighlightColor=(1.882,2.659,5.992)` / `_BrightColor=(1.950,2.469,4.541)` / `_AmbientColor=(1.831,2.366,4.541)`（b≫r,g 且 >1 = **HDR 发光蓝**） |
  | `avatar_trailloop_ap_01` | hub + `lizi01` 粒子 | startColor TwoColors：蓝 `(0.090,0.296,1.0)` / 青绿 `(0.271,1.0,0.830)` |

**为什么它保持蓝**：`AssetExclude` 里有 **`Avatar`** —— 那是当初"角色被染色"事故的修法。
整族被 `TintPolicy.Excluded` 跳过 ⇒ 它是画面里唯一没被染的东西。
⇒ 日志里能直接验证：`[特效换色] 按排除名单跳过 "Role.Avatar.Avatar_DashShadow_AP_01" (含 "Avatar")`。

**注意 `Common` 也在同一个排除名单里** ⇒ `silhouette_601`（冲刺残影/蓝色叠加）同样被整族跳过。

**修法（2026-10-04，用户选"染了看看"）：新增【放行名单】`TintAllowList`**（`TintPolicy.Allowed`），
默认 `avatar_dashshadow,avatar_trailloop`。命中放行名单的名字**越过** `AssetExclude` 与名字过滤，
但**不越过**屏幕空间判据与用户自己的 `TintExclude`（那两道是安全阀）。
插在三处：`Excluded()` 开头、`AllowInterpolator` 名字过滤前、`AllowBattleEffect` 名字过滤前
（后者必须——冲刺光影的 Owner 不是玩家，靠它才能过门）。
`AssetExclude` 里的 `Avatar` 是**整族**关键字，放行名单的意义就是"只为这一道光解禁，不用放宽整族"。

### 4.3 特效换色管线（已解决，记录两个反汇编事实）

**事实 1**：`MaterialTinter.Play(entries, proxy)` 的尾部**就是**调
`CreateInterpolatorSets(entries, mats, mask)` —— 每次播放都会重建集合，不是只在创建时建一次。

**事实 2**：`InterpolatorSet::.ctor(Object target, InterpolatorBase[] inInterpolators, ...)`
把数组**按引用**存进 `+0x40`，一个字节都没克隆：

```
0x01843ae0be  mov qword ptr [r14 + 0x40], rdi     ; rdi = inInterpolators 参数
```

⇒ 集合和源数组指向**同一批 MaterialColorInterpolator 对象**，
⇒ **就地改写源数组的 `startValue`/`endValue`，对正在播的特效同样生效**。
（旧注释里"它会把源数组克隆成 _interpolatorSets"是**错的** —— 被克隆的是 set 对象，不是插值器数组。）

**据此修的两件事：**
1. **原值表**（`TintBrush._origin`，按插值器**原生指针**做 key）。
   旧版是破坏性改写，第二次换色时把"已经染过的颜色"当"原色"再染一遍 ——
   亮度换算 `k = 原亮度/目标亮度` 有累积效应，`BrightnessScale ≠ 1` 时每换一次色整体亮度翻倍
   （症状：越换越刺眼）。现在每个插值器原值只记一次，永远从原值算，换色可逆可重复。
2. **追染**（`RecolorPipeline.VfxUpdatePostfix`，挂在 `VFXEffectBase.Update`）。
   策略版本号一变，就在之后约 30 帧里把所有在播的特效重染一遍 ≈ **1 秒内跟色**，
   而不是"要等下一次播放"。
   ⚠ 不用"活对象表"是因为那等于持有原生对象的托管引用，而特效是**池化复用**的，
   那样会把已回池的对象钉住不放。Update 路线平时只做一次整数比较（**零分配**，见 `RecolorConfig.Revision`）。

---

## 5. 踩坑清单（**血泪，勿重犯**）

### 5.1 判断类错误（最致命，反复犯）

> **过滤条件 / 判据 / 标记，必须自问：它会不会把目标本身干掉？**

1. **关键字子串匹配误伤**：`dash` 把贝德维尔的 `UltraDashEX/UDA` 全卷进来 → Ultra 全程无敌 → 翅膀消失。**修：加排除名单**
2. **用内容特征当"我改过的"标记**：我给自己插的段改名后，判据按"动作名以 attack 开头"去认，结果**认出了自己插的段** → 反复拆装 → 链无限膨胀 → 位置归零。**修：用不可能与原生碰撞的哨兵值 `Order>=900`**
3. **探针限条数 = 制造假阴性**（**栽了 5 次**）：
   - 只记前 N 条 → 预算被噪声吃光（`rush→AttackUp2` 爆发、`ChangeAction("")` 空串、`stand→squat` 每帧重试）
   - **修：按"配对去重"（如 `当前动作→目标动作`），新配对永远打得出来，老配对每 N 次提醒**
   - **静默 return 同样致命**：探针自己 `if (xxx) return;` 不打印 → 整轮日志空白 → 又得猜。**每个 return 都要说明原因**
4. **Harmony postfix 形参必须与目标方法签名严格一致**，否则 `IL Compile Error`，探针**从来没挂上**，却被我当"零命中 = 不在路径上"（浪费数轮）
5. **单帧采样当结论**：曾断言"3 个 MeshRenderer 被禁用"，实际全部启用
6. **存活判据误杀**：`IsDead` 每帧检查 → 池化对象刚取出时标志位是残留值 → 全被丢弃
7. **caster 过滤假阴性**：要求"本地玩家" → 影子/分身打出的全被滤掉

#### 5.1.1 ★★★ 【观测工具悄悄失效】—— 2026-10-03 一天连栽五次，全是同一个形态

**形态**：工具/机制看起来在工作，实际没有；而我把它的输出（尤其是"没有输出"）
当成了事实，拿去推理。

| # | 事故 | 我读成了 |
|---|---|---|
| 1 | `SetChainCD` 探针形参类型写错（`Fp` 写成 `long`）→ Harmony 生成非法 IL，**每次调用都抛** | "这个函数全游戏零调用" → 据此排除了整条 JS 线索 |
| 2 | 盲替换 `Clamp(CfgSpeed?.Value ?? 1.5f)` → 命中 `Factor()` **定义处自身** → 自递归 → StackOverflow（不可捕获，日志全空白） | "无原因闪退" |
| 3 | 组表缓存用 `ReferenceEquals` 比一个**每次新拼出来的**字符串 | "缓存没问题" → 实际每帧重解析 + 刷屏 |
| 4 | 为了"看得更多"去掉探针过滤 → `LogEx.Once` **全局 key 上限**被占满 → 新 key 静默丢弃 | "选段机制没被调用" |
| 5 | 整个 `PreSkillOrder` 推理链 | 前提本身错了（真正的主干是 JS 驱动，见 3.6） |

**规矩（写进流程，别再犯）**：
- **部署后必须能自证版本** —— 已加 `[构建标记] 程序集写入时间`，任何日志先看这一行。
  今天多次出现"这份日志是哪版跑的"说不清，全靠时间戳猜，浪费了轮次。
- **改探针 = 改工具**。探针改动（尤其放开/收紧过滤、改形参）之后，先问一句
  "它还能正常工作吗"，必要时给一个**计数器**做交叉验证（调用方动了 N 次，被调用方计数应为 N）。
- **全局去重器有上限**（`LogEx.Once` 的 `KeyCap`）。高频探针要用**自己的**去重表，别蹭全局。
- **零命中 ≠ 不在路径上**（已有条目）。**先怀疑探针，再怀疑世界。**
- **盲替换之后立刻 grep 被替换的符号**，确认没有命中"定义处自身"（自递归/自赋值编译期合法，只在运行时炸）。
- 结论要有**两条独立证据**才写进知识库；单靠一条推理链很容易像 #5 那样整条作废。

### 5.2 平台/工具类错误

8. **BepInEx 不覆盖已写入 `.cfg` 的配置项**（**栽了 3 次**）。改默认值**必须同时改 cfg 文件**。
   - 症状：改了代码默认值，用户那边毫无变化
   - 本次实例：`ManualAdvance` 默认改 false 但 cfg 仍是 true → 双重推进 → 隔一段跳一段
9. **`python -c "..."` 内联脚本会毁掉中文和转义符**（bash 把 `\\n` 变成 `\n`，python 再变成真换行 → C# `CS1010 常量中有换行符`）。
   **含中文/转义符的脚本一律用 Write 写成 .py 再跑。**
10. **Harmony 按形参名绑定**：`ActorModel.UpdateModel` 的形参叫 `delta` 不叫 `dt`，写 `dt` 直接挂失败
11. **Il2CppInterop 只生成公开字段**，私有字段反射拿不到（`m_SkChains` 恒 null）→ 走裸指针
12. **Il2CppInterop 多态返回是声明的基类包装** → 必须 `TryCast<T>()`
13. **`Il2CppReferenceArray<T>` 没实现非泛型 `IList`**，`as IList` 恒 null
14. **`object[] __args` + struct 参数（Fp2）会装箱 → 无效指针 → 原生崩溃，C# try/catch 抓不到**
15. **插值字符串不支持负对齐** `{x,-7}` → `CS1739`
16. **反汇编时段头的 VA 是 RVA**，要减 ImageBase
17. **`SkillActivateFixedPoint.ActdurStrict` 是 `long`**（Q32.32 原始值），同名类 `SkillActivate` 的才是 `float`
21. **★★ "名字在 metadata 里"不等于"那个重载在"。重载对不上号 = 空桩 = 一调就抛，
    而且异常很容易被 catch 吞掉，症状是"某个控件就是不画"，日志里啥也没有。**
    interop 程序集有**全 API 表面**，游戏 metadata 只有被 UnityLinker 留下的一部分；
    `dump.cs` 就是权威清单。**加任何新控件前先 grep 确认重载，别信 IDE 补全。**
    已中过的两枪（都是本项目实修）：
    | 写法 | metadata 里实际有 | 后果 |
    |---|---|---|
    | `GUI.TextField(rect, text, maxLength, style)` | **只有** `TextField(Rect, string, GUIStyle)` | 字符型配置的输入框**一个像素都不画** |
    | `GUI.Box(rect, text, style)` | **只有** `Box(Rect, string)` / `Box(Rect, GUIContent, GUIStyle)` | 画色卡/底板时不画 |
    | `GUILayout.Label(new GUIContent(...), style, opts)` | **只有** `Label(string, opts)` / `Label(string, style, opts)` | 整行不画（甚至整个面板不画） |
    验证：
    ```
    grep -nE "^\s+public static (void|bool|string|Rect) (Box|Label|TextField)\(" dump/dump.cs
    ```
    替代写法：`GUI.Label(rect, "", style)` 在样式只带 `normal.background` 时**就是**画背景，
    可以用它替掉 `GUI.Box(rect, "", style)`（`Label(Rect,string,GUIStyle)` 是确认存在的）。
22. **"我持有它的托管引用所以它不会被回收" —— 对 IL2CPP 池化对象是反效果。**
    换色追染最初想维护一张"活 MaterialTinter 表"，但弹幕/特效是池化复用的，
    持有引用会把已回池的对象钉在池外（泄漏，且症状诡异）。
    改用「搭在每帧都会跑的原生方法上 + 零分配的策略版本号比较」。
24. **★★ 不要在"已验证正常"的构建里夹带未验证的改动。**
    2026-10-02 的启动崩溃就是这么来的：一次部署的说明是"只改了配置文案"，
    实际还夹带了 `AcceptableValueList`（枚举选择器）和 `ReadFile` 的 `__0` 形参改名。
    崩了之后**说不清是哪一处**，只能逐轮回滚试 —— 两轮才排掉。
    **规矩：一次部署只放一件事；夹带的改动必须在同一条消息里点名。**

25. **★★★ 挂钩 `RuntimeLoader.GetAssetBundleFile` = 游戏一启动就硬崩。**
    ```
    症状: coreclr.dll 访问违例(托管侧, try/catch 抓不到)
          日志断在 [exist] puerts/init.mjs -> True 之后, [load ] 那行永远不出现
    原因: .mjs 文件在 AssetBundle 里, 加载器走的是 FileExists → GetAssetBundleFile,
          **根本不经过 ReadFile** —— 所以这个钩子一挂上, 第一次取 JS 就是最后一次。
    处理: MethodCandidates 只留 "ReadFile"。不要加回 GetAssetBundleFile。
          代价: 走 bundle 的 JS 文件不会被改写(与历史行为一致)。
    ```
    **它踩坑的方式很典型**：这个钩子原本因为形参名写错（`filepath` vs `requirePath`）
    而 **IL Compile Error 静默挂不上** —— 无害了很多轮。我去"修"它（先试按位置的 `__0`，
    再试正确的 `requirePath`），**两次都让游戏启动即崩**，因为崩的原因是
    **"挂了这个钩子"本身**，跟形参怎么命名无关。**把无害的失败"修"成了致命崩溃。**
    ⚠ 未查明：为什么单单这个方法挂不得（`ReadFile` 同样是 `(string, out string)` 却没事）。

26. **★ 排查启动崩溃：真正管用的手段是"对比两份日志的 hook 清单"，不是读代码。**
    这轮我走了三段弯路，值得记下来：
    - ❌ 把"时间上最后改的代码"当元凶（`ReadFile` 形参改成 `__0`）—— 改回去崩溃点纹丝不动。
    - ❌ 听信"最可疑的功能改动"去回滚（枚举选择器）—— 也没用，白跑一轮。
    - ✅ **能进游戏的日志 vs 崩溃的日志，逐行 diff 挂钩结果** —— 一眼就看出
      "崩溃版多挂了 `GetAssetBundleFile` 这一条"。**这是最终定位的唯一有效手段。**
    配套两条：
    - **先问清"上一次正常是什么状态"**（是进了游戏还是只看了面板、哪一版）。
    - **用日志确认代码路径有没有执行**：崩溃日志里一条 `[IMGUI:evt]` 都没有
      ⇒ CM 从没画过 ⇒ `DoTextField` / `GUI.Box→Label` 那两处改动**根本没跑**，直接排除。
    - 启动早期崩溃时，先加"根本不挂载"级别的开关（`MountRecolor` /
      `[IMGUI] SelfOnGuiHost`），比继续读代码快。

27. **写脚本处理含中文/反引号的源码时，不要用 heredoc 内联 python**（第 9 条的复发）：
    本轮又犯了一次 —— 用 `python - <<EOF` 改 `Patcher.cs` 的注释，中文和反引号被 bash 吞掉，
    断言失败、静默没改到。**改用编辑器工具做精确字符串替换。**

23. **配置改了必须同时改 cfg 文件**（第 8 条的推论，但这次是"删配置项里的值"）：
    把 `holdEX` 从 `Sequence` 默认值里去掉时，cfg 里的实况也得一起去掉，否则它一直生效。

28. **★★★ 第三方 BepInEx 插件"装了没反应"——BepInEx 6 会【静默丢弃】版本不匹配的插件，一行日志都不打。**
    （2026-10-04，装 UnityExplorer 时栽的。**这次我先猜错了两轮**：先怀疑"子目录不会被扫"，
    把 DLL 平铺到 `plugins/` 根 —— 白折腾，文件其实一直被扫到。）

    **判定链（`BepInEx.Core/Bootstrap/BaseChainloader.cs`）**：
    ```csharp
    HasBepinPlugins(ass)  // ① 必须引用 BepInEx 的程序集 ② 必须有 BepInPlugin 类型引用
    ToPluginInfo(type)    // ③ try { type.IsSubtypeOf(typeof(TPlugin)) }
                          //    catch (AssemblyResolutionException) { return null; }   ← ④ 静默丢弃
    ```
    ④ 是关键：插件类的**基类类型解析不出来**（程序集名字对不上），异常被吞，**零日志**。

    **怎么一眼看穿 —— 缓存文件是明文的**：
    `BepInEx/cache/chainloader_typeloader.dat`
    ```
    ...plugins\BlazblueJsPatch.dll <hash> 01 00 00 00 16BlazblueJsPatch.Plugin...
    ...plugins\sinai-dev-UnityExplorer\UnityExplorer.BIE.IL2CPP.CoreCLR.dll <hash> 00 00 00 00
                                                                            ↑ 0 个插件类型 = 被静默丢弃
    ```
    每个 dll 后面那个 int 就是**解析出的插件类型数**。正常插件 ≥1，被丢弃的是 0。

    **本次根因**：be.788 把链加载器程序集从 `BepInEx.IL2CPP` 改名为 `BepInEx.Unity.IL2CPP`；
    UnityExplorer 4.9.0 继承的是老名字 ⇒ 解析失败。**另外版本号方向也是坑**：
    .NET 绑定要求"请求版本 ≤ 实际版本"，而它请求 `BepInEx.Unity.IL2CPP 6.0.0.538`
    而实际是 `6.0.0.0` ⇒ 即使名字对了也绑不上。

    **修法（工具 `_modding/tools/bie_refix/`，Mono.Cecil，BepInEx/core 里就有）**：
    ```
    dotnet run -- dump   <插件.dll>                      # 看引用清单 + 插件类基类 + 属性
    dotnet run -- fix    <插件.dll> <输出.dll> <core目录>  # 改程序集名 + 按 core 真实版本对齐版本号
    dotnet run -- verify <改后.dll> <新基类所在.dll>       # 校验真覆盖的成员在新基类里都在
    ```
    ⚠ 判"真覆盖"要用 `IsVirtual && !IsNewSlot`：插件类自己声明的虚方法（`IsNewSlot`）
    不是覆盖，拿它比对基类会得假阳性（我第一版就这么误报了 7 条）。

    **教训**：以后再遇"插件没反应"，先读那个缓存文件，再看程序集引用名和版本方向 ——
    **不要去改目录结构、也不要去猜路径**。

    **★★ 后续（同日）：改完之后 UnityExplorer 能加载了，但它的 UI 永远起不来 —— 别再试了。**
    日志：
    ```
    [Message:UnityExplorer] Loading [UnityExplorer 4.9.0] / UniverseLib 1.5.1 initializing...
    [Warning:UniverseLib] Exception parsing Unity version, falling back to old AssetBundle load method...
    [Error:Il2CppInterop] Exception in IL2CPP-to-Managed trampoline:
      TypeInitializationException: Il2CppStructArray`1..cctor
       → Il2CppClassPointerStore`1..cctor: InvalidOperationException: Sequence contains more than one matching element
         at UniverseLib.AssetBundle.LoadFromMemory(Byte[] binary, UInt32 crc)
    ```
    根因（`Il2CppClassPointerStore<T>` 的静态构造，Il2CppInterop 源码）：
    ```csharp
    if (targetType.IsPrimitive || targetType == typeof(string))
        RuntimeHelpers.RunClassConstructor(AppDomain.CurrentDomain.GetAssemblies()
            .Single(it => it.GetName().Name == "Il2Cppmscorlib")   // ← 内存里有两份同名程序集 ⇒ 抛
            .GetType("Il2Cpp" + targetType.FullName).TypeHandle);
    ```
    ⇒ **任何 `byte[] → Il2CppStructArray<byte>` 的路径都会踩**（磁盘上只有一个
    `BepInEx/interop/Il2Cppmscorlib.dll`，所以是内存级重复）。UniverseLib 的 UI 包
    必须走 `AssetBundle.LoadFromMemory` ⇒ **UE 在本作里没救**（除非上游修 interop）。
    ⚠ 不要再花时间调 UE 的配置/版本/键位。

    **★★★ 后果比"UI 起不来"严重得多：它会【毒死整个 Il2CppInterop】（2026-10-04 夜，用户报"连段全失效"）**
    症状：连段模组挂了 24 处钩子、配置也对（`Chain1` 16 段都在），但日志里全是
    ```
    [连段模组:整行] "ace1_900" ... <触发器读取失败: The type initializer for
       'Il2CppInterop.Runtime.Il2CppClassPointerStore`1' threw an exception.>
    [连段模组:注入|miss|attackD1] 动作表里找不到源动作 "attackD1"，"ace1_900" 注入失败
    ```
    **机制（C# 语义，不是猜）**：静态构造函数一旦抛异常，**该类型在本进程内永久不可用**。
    时序（同一份日志）：`行 1056 UnityExplorer initializing` → `行 1069 UniverseLib 退回
    AssetBundle.LoadFromMemory(byte[],uint)` → **`行 1071 Il2CppClassPointerStore\`1 静态构造抛异常`**
    → 之后**行 2680 起**我们连段模组里任何碰这些 interop 类型的代码全部连带阵亡。
    ⇒ **装上 UnityExplorer = 其它所有 mod 的 Il2CppInterop 代码随机坏掉**，而且症状看起来
    跟 UE 毫无关系（"连段怎么失效了"），极难往它身上想。
    **处理：已把两个 dll 移出 `BepInEx/plugins/`（备份在 `_modding/tools/_dl/`）——
    UE 在这个游戏里没有任何留下来的理由。**
    ⚠ 教训：**装任何会触碰 il2cpp interop 的第三方插件之前，先想清楚"它出错时会连累谁"**。

    **替代方案（已落地）：`Modules/Common/FxAnatomy.cs`「特效解剖」**
    metadata 里 `Shader.GetPropertyCount/GetPropertyName/GetPropertyNameId/GetPropertyType`
    是**公开实例方法**（dump.cs 已核对）⇒ 运行时可枚举材质的**每一个**着色器属性及其值
    （`Material.GetColor/GetFloat/GetVector/GetInt/GetTexture`），比 UE 的检视器还全。
    挂在 `BulletMgr.createBulletImp` 上，**延迟 0.3s / 1.0s 各采一次**
    （刚创建那一瞬子物体还没挂上：日志 `粒子=0 渲染器=2` 就是那一瞬），
    倒出：层级 + 每个渲染器的材质 + 全部属性值 + 粒子 `startColor`。
    配置 `[诊断] FxAnatomy` / `FxAnatomyKeyword`（默认 `bullet`），关掉=**根本不挂钩子**。

### 5.3 方法论

18. **"不知道该 hook 哪个"时，不要枚举候选挂探针** —— 直接反汇编：
    `dump.cs` 有 RVA、`GameAssembly.dll` 有机器码，`tools/_disasm.py` 能一次看到调用图和字段偏移。
    **本轮 `PreSkillOrder` 就是反汇编一次命中的**；而在此之前我挂了六七个探针全零命中。
19. **改默认值 = 同时改 cfg**（见 8）
20. **先只读转储，再动手改**；动手前先看清真实结构

---

## 6. 工具（`_modding/tools/`）

| 脚本 | 用途 |
|---|---|
| `_disasm.py` | ★ **IL2CPP 反汇编 + call 目标符号标注**；`--callers <方法名>` 反向找调用点 |
| `_dtstruct.py` | 确认 `DeltaTimeAndScale` 三个访问器碰哪个偏移 |
| `_finalmap.py` | skillactivate 静态表 → 「输入 → 招式」表 |
| `_append_chain.py` | 运行时链路日志 → 动作↔技能对照表 |
| `_fix_preinput.py` | 修 Plugin.cs 被 heredoc 弄坏的字符串块 |
| `_timexref.py` | 扫 `call ActionMgr::set_Time`（0 命中，已被内联） |
| `_tinter_disasm.py` | ★ 定点反汇编 `MaterialTinter.Play / CreateInterpolatorSets / ...`（查"存引用还是克隆"） |
| `_setinit_disasm.py` | ★ 定点反汇编 `InterpolatorSet::.ctor / get_interpolators`（确证按引用存数组） |
| **`_alg_calls.py`** | ★ **抽 ActionLogicGroup 的全部脚本调用**：`python _alg_calls.py <raw> [关键字]` |
| **`_changeskill_sites.py`** | ★ 把 `ChangeSkill` 调用**归属到所在动作块** |
| `_chain_disasm.py` / `_chain2_disasm.py` | 定点反汇编 `PlayerSkillChain` / `PlayerSkillUtility` 全部方法 |
| `_inputdir_cases.py` | ★ 解码 `StrictMatchPlayerSkillInputDir` 的**跳转表**，逐 case 出判定 |
| `_predecessor_graph.py` | 从 skillactivatefixedpointwrap 生成 `preSkillOrder` 前置图 |
| **`bie_refix/`** | ★★ **第三方 BepInEx 插件装不上的修复器**（Cecil）：`dump` 看引用 / `fix` 改名+版本对齐 / `verify` 校验。见 §5.2-28 |

> `_disasm.py` 的"定点版"：后者按名字关键字扫全文，重名方法多时会漏；
> 定点版直接按 RVA 反汇编，问一个具体问题就给一个确定答案。
> 模板：`sys.argv=["_disasm.py","zzz"]` → `import _disasm` → `_disasm.disasm(rva, 标题)`。
>
> ⚠⚠ **`_disasm.py` 的 RVA→名字标注会骗人。** 映射是 `setdefault` 的，而 IL2CPP 里
> **大量微小 getter 共用同一段机器码**，于是某个 getter 会被标成"同 RVA 里第一个被命名的类"。
> **判断字段归属要看偏移量和调用上下文，不要只看标注的名字。**
> 本项目的 `order` / `preSkillOrder` 两个取值器就是这么验明的（读 `wrap[0x10]` 再读 `data[0x20]` / `data[0x60]`）。

### 6.1 ★★ 找游戏逻辑时先看 `JsPort`，不要去挖 JS

**游戏的"JS 逻辑"在构建期已经转译成 C# 了。** `dump/dump.cs` 里搜 `ActorJs_`：

```
JsPort.ActorJs_hz        = ES 玩家角色逻辑        （★ 795 个 ChangeSkill 调用就在这里）
JsPort.ActorJs_hzbullet  = 纹章(esbullet) 逻辑
JsPort.ActorJs_head_ray  = 激光怪
每个类:  static Dictionary<string, ActorFuncsJs.ActorFuncJs> s_SpecFuncMap;
         private static void initSpecFuncMap()   ← "名字 → lambda" 的注册处
```

**类名 / 方法名 / 字符串全部保留**，直接可读。
在 `js_src/`（14MB 真 JS）里 grep 这些名字**一无所获**，就是因为逻辑已经不在 JS 里了。

`_modding/dump/dump.cs` = Il2CppDumper 全量输出（**查偏移/签名先看它**）

---

## 7. 当前配置状态（cfg 实况）

```
[总开关]      Mount* 全 true；MountEsMech 由 BepInEx 下次启动时自动补上(默认 true)
[动作变速]    Enabled=false, Speed=1, Lever=Inject, SplitByHitData=true, AccelRatio=0.45
[连段模组]    RewriteEnabled=true, ManualAdvance=false, HoldSeconds=-1, PreInputSeconds=0.3, Group=1
              Sequence=attack1,attack2,attackD2,attackAEX,attack3,attackB,attack4,attackD3,atkAirX
              (holdEX 已移出 —— 纹章归 [纹章解放] 段)
[纹章解放]    Enabled=true, BulletId=10340101, CrestActions=x1,x2,ax2, RingCount=8, SpreadMode=Move
[特效换色]    RecolorEffect=true, ColorHex=FF00FF, FollowLive=true(新增)
[冲刺无敌]    Enabled=true, ActionExcludeKeywords=Ultra,UD,UDA
```

> ⚠ 新增的 `FollowLive` 与 `MountEsMech` 是**新键**，BepInEx 会按默认值写进 cfg；
>   而改过默认值的**老键**（如 `Sequence`）必须手动改 cfg —— 见 §5 第 8 / 23 条。

---

## 8. 用户长期约束（勿违反）

- **不修改游戏房间**
- 冲刺无敌保持关闭语义（与完美闪避互斥）—— 已加排除名单，不再干扰 Ultra
- 角色本体重新着色保持关闭（"闪眼"）
- **优先运行时 hook，不做磁盘资源编辑**
- **不要自己拍脑袋调数据** —— 优先读原生值/反汇编，而不是填手感数

---

## 9. 待办

### 已完成（2026-10-02 架构重整第一轮）
- ✅ 模块表（`Core/ModuleTable.cs`）+ 逐模块异常隔离 + 启动打「模块挂载表」
- ✅ 面板自建 OnGUI 宿主，**去掉了对 ConfigurationManager 的依赖**（不再需要先按 F1）
- ✅ 修好字符串配置输入框（四参 `GUI.TextField` 空桩，见 §5 第 21 条）
- ✅ 换色抽成独立管线 `Pipelines/Recolor/`，并修「不跟配置变色」+「换色亮度累积漂移」
- ✅ 纹章解放从连段模组里拆出来：独立的模块 / 配置段 / 总开关 `MountEsMech`
- ✅ ES 机体模块归到 `Modules/Es/`，与只读诊断分开

### 未决
1. **变速线**：全链路加速 + "只加速攻击段、保留后摇窗口"（`HitDataList` 切分已就位）
2. **连段线**：衔接时序（`ActdurStrict` / `Preinputtime` / `Timeout` 三者组合）调通；
   "按快剑气放不出"未定位
3. 诊断/临时模块（ActionProbe / ActionJournal / AhWing / BulletProbe / *Trace）
   考虑再加一个「调试模式总开关」把它们收进去（现已在 MountExtra 下，但没细分）
4. 纹章接管 vs "原版移动纹章解放"的取舍（同一 `holdEX` 动作，可能无法区分连段触发与手动触发）
5. 配置项仍由 `Plugin.cs` 集中绑定（~450 行）。下一步可考虑把每个模块的 Bind 挪到模块自己身上，
   让 `Plugin.cs` 只剩启动引导；但**段名/键名不能动**，否则用户调好的值全部作废

## 10. 崩溃排查：训练场不崩 / BossRush 崩（2026-10-03 夜，**进行中**）

**现象**：插件加装后训练场长时间游玩不崩，**BossRush 反复崩**；
日志**干净截断、没有任何托管异常** → 原生崩溃（访问违例），BepInEx 抓不到、写不出来。

### ★ 判据：差别在【场景里 actor 的数量与生命周期】

| | 训练场 | BossRush |
|---|---|---|
| 敌人 | 1 个木桩，常驻 | 大量 boss/小怪，**不断生成销毁** |
| ActionLogicGroup | 少且地址稳定 | 多且**频繁变** |

我们代码里**唯一每帧遍历"所有角色动作表"**的就是注入循环，日志尾部反复刷的就是它：

```
[连段模组:注入] ActionLogicGroup 0x... 处理完毕: 源动作表 82 个, 本次注入 0 个, 累计 0/16
```

注意 `累计 0/N` —— 它对**外来组**（boss 的动作表）永远标记不成"已完成"，
于是**每帧重新整表扫一遍**。两个后果：

1. 性能：entity 一多，每帧扫描量线性上涨
2. ★ **悬空指针**：`_injected` / `_foreignGroups` 是按**裸原生指针**（`IntPtr`）索引的，
   而这些组会随 boss 死亡被销毁；**指针被回收后可能被新对象复用** →
   缓存命中一个已经不属于那个对象的地址 → 访问违例 → 无日志的原生崩溃（正是本症状）

训练场只有一个常驻木桩，地址从不失效，所以不崩 —— 这与观察完全吻合。

### 定性实验

```
[连段模组] RewriteEnabled = false
```
→ `ApplyChain` 直接返回 → `_synth` 为空 → 注入循环首行 `return`，**一次扫描都不做**。
代价：连段全停（地面两线 / 穿插 / 空中）。一行配置，可随时改回。

### 修法方向（定性后）

1. 用**组对象**而非裸指针做键，或加生命周期校验（对象被销毁则剔除缓存）
2. 或干脆**只对目标组注入，永不扫描外来组**

### 已排除

`Chain2` 空中链（清空）、`SynthName` 唯一化（已还原）、`BulletLab` 面板（已关）。
**未单独排除**：弹幕探针新加的 `Fp2 pos/dir` 形参（弱嫌疑）。

### ⚠ 方法教训（这次真栽了）

19:12~19:31 之间我**连着四处改动一起上线**，出问题后只能在盲区里二分，白烧了好几轮。
**下次一次只动一个变量。** 这条比任何一次具体 bug 都值钱。

## 11. 存档分析（2026-10-04，**已完全解开**）

**文件位置**：`%USERPROFILE%\AppData\LocalLow\91Act\BlazBlueEntropyEffect\Steam\<steamid>\Save\1`
（`PlayerData` 是加密的 `NOAH` 容器，**别碰**；`Save\1` 是明文可解的。`Backup\1` 同格式；
`ColdBackup\1\<unix时间戳>` 是历史快照。）

**格式（实测，不是猜）**：`LZ4 frame` 包着的 `protobuf`。
```
04 22 4D 18   LZ4 帧魔数(0x184D2204)
68            FLG: version=01, blockIndep=1, contentSize=1
40            BD : blockMaxSize=64KB
A8 DB 00 00 00 00 00 00   contentSize(56232)
54            HC
8D 9D 00 00   第一个 block 长度
```
**顶层结构**：`#2 重复{ #1 模型名(str), #2 负载(protobuf) }` —— 每个系统一个模型
（`ModelPlayer` / `ModelPlayerNewFesActorPack` / `ModelDeadCell` …）。

**★ 数据体（FesActor）在哪**：
```
ModelPlayerNewFesActorPack 的负载
  └ #1(一条, 里面是) 重复 #1 = map entry { #1 uid, #2 STFesActor }
STFesActor（★ score 是 double(fixed64)，用 varint 读会得 0 —— 踩过）:
  #1 uid   #2 id(角色)   #16 score(double)
  #15 potentials : STPotential[]   {#1 id, #3 active, #5 order}        ← 潜能
  #17 talents    : STTalent[]      {#1 id,#2 quality,#3 inheritNum,#4 fromActorId} ← 传承
  #26 skills     : STActiveSkill[] {#1 id,#2 level,#3 fromActorId}      ← 策略
  #8  blesses    : STBless[]                                            ← 祝福
  #36/#37 inheritSkillIndex/Level
```

**工具**（都在 `_modding/tools/`）：
| 脚本 | 用途 |
|---|---|
| `_savedump.py` | 通用：LZ4 解包 + 无名 protobuf 树 |
| `_pbdef.py` | ★ 从 `Gen/pbdef.js` 反推 **proto 字段表**（`find`/`msg`/`msgs`）—— 存档里所有字段名都靠它 |
| `_savedb.py` | 把数据体列表拉出来（uid/角色/分数/潜能/传承/策略/祝福） |
| `_potdb.py` | 读潜能配置 `data/xlsx/baseactorpotentialconf.ab`（`<u32 长度><protobuf>` 分帧） |

⚠ `_pbdef.py` 两个坑（都踩过）：① 消息类型字段用 `.fork()` 编码、**类型名写在前面**
（`$root.STAttr.encode(e.baseAttrs[r],t.uint32(50).fork())`），不是 `(e.x)`；
② 只取 **encode 体**（到 `,X.encodeDelimited=` 为止），把 decode 体算进来字段号会全歪。

**★ 潜能是"按 rank 固定"的**（2026-10-04 实测）：同一 rank 上，四个数据体的潜能 id **完全相同**
（差异 0 个）。所以"某份数据体多出几个潜能"只是**它多抽了几档**，不是随机池。
**做"差异对比"时必须先按 rank 对齐**，否则会把"抽得多"当成"不一样"。

**本次用它解出的结论（★ 先错后对，两步都要记住）**：

- ❌ **第一版按"单个潜能 id / rank"比，得"rank 9（5608）"是判别式 —— 被用户一条反例直接证伪**：
  uid 1（1010 分）**有**特效，但它根本没有 rank 9。教训：**rank 只是槽位**，
  "某份多出几个潜能"只说明它多抽了几档，不是差异。
- ✅ **第二版按"潜能家族"比（`icon` + `preLabels/升级链` 归并）** —— 用户提示"每个槽内的潜能
  还有分支升级"，这正是关键：5608/5609 是同一潜能的**两级**（icon 都是 5608、5609 的
  `preLabels` 指向 5608），5621 是根、**5622/5623/5624 是它的分支**。
  归并成家族后，判别式唯一：**家族 5621** —— 1268(uid11) / 1158(uid9) / **1010(uid1)** 三份
  "有特效"的**全都有**，998(uid6) / 1073(uid12) 两份"没特效"的**都没有**，5/5 全对。
  而且 uid 9 只点了根节点 5621（没点升级分支）也有特效 ⇒ **家族出现即可，分支不影响**。

**5621 = 加拉哈德**，这条映射是交叉验证过的：
`BaseActorPotentialConf.relationId(#18)` 里，全表恰好 **5 个**潜能各挂一位骑士 ——
5602=莫德雷德(340321)、5607=**布鲁诺**(340281)、5608=贝德维尔(340501)、5617=高文(340391)、
**5621=加拉哈德(340441)**。旁证两条：① 5607=布鲁诺 正是我们一直在染的"布1/布2 剑气"；
② 5608/动作 340501 的 `isUltraSkill` 都是 1，两边一致。
（骑士名来自早先那次本地化工作：`ActorActionName_340xxx`。）

**可验证的预测**（其余 10 份 ES 数据体是否含家族 5621）：
预测**有**特效：uid 32(2778) / 18(2163) / 25(1698) / 24(1683) / 17(1563) / 22(1548) / 8(1458) / 16(1273)；
预测**无**特效：uid **13(1208)** / 5(483)。← uid 13 是最有判别力的那个（分数不低但没这个家族）。
**实测：uid 13（1208）确实不出该特效 ⇒ 判据 6/6 成立。**（用户自己在游戏里验证的）

**★★ 谜底（用户当场认出来的）**：那道"蓝光"= **加拉哈德的「↓+冲刺」技能**，
消耗 **50 MP**（与 `ActorActionConf` 里 340441 的 `skillCostOrigin=50` ✓ 对上），
**正常情况下只在命中怪物时才触发** —— 所以"空放"（没打中）时残留下来的正是它那层特效，
看起来就像"冲刺时莫名其妙冒出一道蓝光，还有 CD"。
⚠ 因此它**不是**冲刺动作自带的层 ⇒ 之前那份"关掉 `es_dash_01:Other`"的名单对它无效，
要处理必须针对这个技能自己的特效资产。
**下次定位它用哪套资产最快的办法：打一次 ↓冲刺，看那一刻日志里新出现的 `[特效换色]` 行。**

**★★★ 日志里查实了（2026-10-04 夜，一次 ↓冲刺的完整证据链）**：
```
[动作结构:变速机制] GetTimeScaleByTime("anyingchongci", t=0.000)          ← 暗影冲刺这个动作
[特效换色] 战斗特效 "darkbullet" 不含名字过滤 "es,hit_,silhouette", 跳过 (Owner=null, 非本地玩家)
[弹幕探针] idx=800010 startAction="anyingchongci" caster=PlayerObj conf[LogicRes="darkbullet"]
```
⇒ **那道蓝光 = 暗影冲刺（`anyingchongci`）发射的子弹 `darkbullet`**，
而它**一直没被染色，是因为名字里没有 `es`/`hit_`/`silhouette` 任何一个关键字，被名字过滤整族跳过**
（和当初"受击特效 `hit_009`"、"冲刺拖影 `silhouette_601`"是同一类漏网 —— 白名单制度的固有代价）。
**修法：把 `darkbullet` 加进 `EffectNameFilter` / `TintNameFilter`（或放行名单 `TintAllowList`），一行配置，不用改代码。**
⚠ 同类线索以后先看这一行：`[特效换色] ... 不含名字过滤 ... 跳过`。

**用户补充的设计事实**：这个特效是**"没命中"时才留下**的 —— 命中了反而没有这层（用户的观察）。

⚠ `BaseActorPotentialConf` 的字段名（从 encode 体读的，别再猜）：`#3 label` `#4 preLabels`
`#15 orderId` `#18 relationId`（都是 packed repeated uint32），`#19 iconId`。

**没解决的**：本地化表 `data/localization/localization_chs.ab` 是
`<u32 键哈希><u32 长度><utf8 文本>`，键被哈希过；拿"纹章解放/登录"当已知对试了
crc32 / fnv1 / fnv1a / djb2 / sdbm / unity-StringToHash **都没破**。
要拿技能名就走**运行时**：`Localization.LocalizationManager.GetLocale("ActorActionName_<id>")`。

### 4.3f ★★ ↓冲刺残影（加拉哈德）—— 结论：**不要自己染，引擎本来就会跟皮肤变**

用户实测（2026-10-04 夜）：**用正版换色皮肤时，这道残影是跟着皮肤的专属特效变的** ——
也就是说引擎对它有自己的一条路（皮肤那套 `esskin_XX` 资产 / 皮肤配置驱动），我们自己去染
是**重复劳动**，而且必然踩坑。

**这一轮试过并已回滚的做法（`ActorTrail` 残影路）**，留个记录免得重蹈：
- 现象：`es_dodge_01/02` 的资产里只有 `hub + Other(ActorTrailProxy)`，
  运行日志始终是 `载体 粒子=0 渲染器=0 Tinter=0` + `命中但一处都没改写 (nops)`
  （残影的渲染器/材质是**运行时**生成的，粒子路/插值器路看不见）。
- 失败点一：`ActorTrail` 挂在**角色渲染器的副本**上（节点名就叫 `Renderer`），
  父链里没有 `VFXEffectHub` ⇒ `TintPolicy.EffectName` 退化成节点名 ⇒ 名单永远匹配不上。
- 失败点二（改对识别之后的）：`ActorTrailProxy`（挂在特效自己身上，有 `m_trail` @0x70 指回 trail）
  确实能认出 `es_dodge_02` ✓，但**按整个 trail 节点做 `GetComponentsInChildren<Renderer>` 会把
  角色模型副本的渲染器全染掉** ⇒ 用户实测"干扰了角色、冲刺、高文"。
  真正的靶子应该是 `ActorTrail._tuples`（`List<(MeshRenderer,MeshFilter)>` @0x50，它自己生成的拖尾网格）。
- **结论：这条路不做了**（用户拍板"实在不行就像布鲁诺那样拆它的特效组合"）。
  代码已整段删除（`HookTrails/TrailPostfix/ProxyPostfix/TintBrush.TintTrailRenderers` +
  `TrailTint/TrailNameFilter` 两个配置项 + cfg 键）。

⚠ 教训：**动手染某个东西之前，先用正版皮肤确认"它本来会不会跟着皮肤变"** ——
会变就说明引擎有现成的路，硬染一定是在错误的层面上打架。

**★ 拆解结果（2026-10-04 深夜，离线可复现）—— 蓝色的准确位置**

```
effect/prefab/role/es/es_dodge_01.ab （5494 字节）
  层级:  es_dodge_01 [VFXEffectHub + VFXEffectExtension]
           └ Other [★ActorTrailProxy]      ← 整份资产只有这一个组件
  ActorTrailProxy 字段: m_lifeTime=0.30  m_trailDeltaTime=0.07
    MaterialInterpolators[2]:
      #0 MaterialColorInterpolator  propName = "_AddColor"     (0,0,0,a=0) → (0, 0.380, 1.0, a=0)   0.25s
      #1 MaterialFloatInterpolator  propName = "_AlphaScale"   0.0 → 1.0                          0.25s
```
⇒ 那道蓝白电弧 = **`_AddColor` = (0, 0.380, 1.0)** 的**加色**（b=1，g=0.38 ⇒ 蓝白）✓

**为什么我们一直没染到它（两条同时成立，缺一不可）**：
1. **载体不认识**：插值器路（`TintBrush.TintTintersOn`）只遍历 `MaterialTinter` / `MaterialTinterProxy`，
   **`ActorTrailProxy` 不在名单里** —— 它同样是 `VFXEffectBase` + `MaterialInterpolators`，形态一模一样。
2. **属性名不在白名单**：`_AddColor` 不在 `TintProperties`（`_TintColor` 在，但 `es_dodge_01/02` 用的不是它）。

**这一族（`ActorTrailProxy` + 它的 propName）全表只有 6 个**（扫 `role/es/*` + 几个 common）：
```
es_dodge_01 / es_dodge_02   Other   _AddColor, _AlphaScale
quickstanddefault(_01)      Other   _AddColor, _AlphaScale
silhouette_601 (common)     Trail   _AlphaScale, _TintColor, _AddColor
dodge_01 (common)           Other   _AlphaScale, _TintColor
```
**4 套皮肤的同名副本结构零差异** ⇒ 引擎换色不是靠这个 prefab（是材料层的调色板在起作用）。
> ❌❌ **上面这句是错的，2026-10-04 深夜推翻。** 原因是我信了自己的 `_prefdiff.py`：
> 它对 MonoBehaviour **只记类名、不记字段值**，两套"值完全不同"的 prefab 会被报成"结构完全一致"。
> 按**值**重比（新工具 `tools/_trailcmp.py`）真相是：**皮肤就是靠这个 prefab 换色的** ——
> | 版本 | `_AddColor` endValue | |
> |---|---|---|
> | 原色 | **(0.000, 0.380, 1.000)** | 蓝 |
> | esskin_06 | (0.311, 0.028, 0.039) | 红 |
> | esskin_10 | (0.064, 0.310, 0.027) | 绿 |
> | esskin_12 | (0.455, 0.509, 0.329) | 米黄 |
> | esskin_13 | (0.410, 0.000, 1.000) | 紫 |
> 用户原话"皮肤下它的蓝色会变成别的颜色" = 这张表 ✓（**用户的观察是对的，我的工具骗了我**）。
> ⚠ **教训：比较两份资产时，只比"有哪些组件"是不够的 —— 插值器/颜色这类数值才是内容。**
> 凡是涉及"某套资产是否不同"的判断，一律用 `_trailcmp.py` 这种**按值**比的办法。

**★ 观感对上了**："外蓝内白、非常亮" = `_AddColor`（**加色**混合，故亮）叠在角色视觉副本（白/肤色底子）上；
`_AlphaScale` 0→1 负责淡入。(0, 0.38, 1.0) 的 b 最高 ⇒ 外圈蓝。

**★ 修法（2026-10-04 深夜已落）**：只差**白名单一行** —— `SweepOne` 找到
`MaterialColorInterpolator` 后会卡在 `PropMatches(prop, TintProperties)`，而 `_AddColor` 不在名单里
⇒ 一条都不写（日志里那个 `nops` 就是这儿）。
`TintProperties` 加 `_AddColor`（代码默认 + cfg 同步改）即可 —— **通用扫描本来就覆盖 `ActorTrailProxy`**，
不需要加载体名单，也不需要碰材质/渲染器。

**最小改法（如果还要做）**：把 `ActorTrailProxy` 加进插值器路的载体名单 + `_AddColor` 进白名单 ——
**只改插值器对象，不碰渲染器/材质**（上一次伤到角色，正是因为我按渲染器/材质走了一遍）。
⚠ 唯一风险：若 trail 用的是**角色那份共享材质**，写 `_AddColor` 会连带染到角色。
实现时要加"只写 trail 自己的实例材质"的防护，并在日志里把每次写入点名。

---

## 12. ★★ 崩溃终于能查了：dump 解析 + 残影路的崩溃根因（2026-10-04 深夜）

### 12.1 为什么要解 dump
"游戏崩了"在 IL2CPP + CoreCLR 下**什么都留不下**：`LogOutput.log` 在崩溃前**戛然而止**
（硬崩，托管 try/catch 抓不到），BepInEx 一行错误都不写，看起来像"日志被截断"。
**真正的现场在 `%LOCALAPPDATA%\CrashDumps\BlazblueEntropyEffect.exe.<pid>.dmp`。**
（`LocalLow/91Act/.../backtrace/` 里只有 crashpad 的 settings.dat，没有 dump。）

### 12.2 工具（都在 `_modding/tools/`，纯 Python + capstone，无调试器可用时唯一的办法）
| 工具 | 作用 |
|---|---|
| `_dumpinfo.py <dmp>` | 打印流目录（排查"扫不到栈"时先看这个） |
| `_mindump.py <dmp>` | 异常码/异常地址/寄存器 + 扫栈解析成"模块+偏移" |
| `_dumpthreads.py <dmp>` | 所有线程的栈（看"谁在跑"） |
| `_dumpscan.py` | 一批 dump 只列 时间+异常码+异常地址（**判"是不是老毛病"**） |
| `_dumpresolve.py [dmp...]` | **把崩溃线程栈上的 GameAssembly 帧翻译成方法名** ← 最有用的一个 |
| `_rvawho.py 0x<rva>...` | RVA → 方法名（用 dump.cs 的 RVA 注释表） |
| `_coreclr_probe.py <coreclr.dll> <rva>` | 反汇编 coreclr 的崩溃偏移（判断"是不是真 AV"） |

**踩过的解析坑（别再花时间）**：
- `MINIDUMP_THREAD` 里 `Stack` 在 **+24/+32/+36**、`ThreadContext` 在 **+40/+44**（我第一版写错成 +20/+28 ⇒ 寄存器全读不到）。
- **MemoryList 的两种编码在这台机器上都对不上**（`size` 反推不出来）⇒ 别猜，直接用**线程自带的 Stack 描述符**。
- `dump.cs` 的 RVA 有两种行：`// RVA: 0x…`（方法，要的就是它）和 `|-RVA: 0x…`（泛型实例化，夹在 `/* GenericInstMethod */` 里）。
  只认前者；`// RVA: -1` 要排除。低于方法表最小 RVA 的地址 = **il2cpp 运行时助手区**，解析不出来是正常的。
- 帧上的地址是**方法内偏移**（返回地址在 call 之后）⇒ 必须找 "≤ 该地址的最大方法起点"，精确匹配会全军覆没。

### 12.3 结论一：所有 crash 的签名**完全相同**
| 时间 | 异常 | 位置 | 崩溃时在跑的链 |
|---|---|---|---|
| 10-02 12:57 | 0xC0000005 | JIT 区 | 游戏自己的 Jint/JS 引擎 |
| 10-02 20:25×2 / 20:29 / 20:34 | 0xC0000005 | **coreclr+0x1D1FDD** | Jint `InvokeJSFunction` / Task 调度 |
| 10-03 09:56 / 09:57 | 0xC00000FD | — | **栈溢出**（另一种崩法） |
| 10-03 19:48 | 0xC0000005 | **coreclr+0x1D1FDD** | — |
| 10-04 11:56 | 0xC0000005 | **coreclr+0x1D1FDD** | `createBulletImp`→`StartBullet`→`ChangeDir2`（**我们补过的弹幕路**） |
| 10-04 23:03 | 0xC0000005 | **coreclr+0x1D1FDD** | `ActorTrail.Update`→**`ActorTrail.CreateTrail`**（**我们补过的残影路**） |

**同一个 coreclr 偏移 ⇒ 不是"某条指令的数据访问崩了"，而是 CLR 的统一死法。**
把两处偏移反汇编出来（`coreclr+0x1D1FDD` = `mov rdx, rax`，`+0x25C713` = `lea rax, [rsp+0x40]`）
—— **这两条指令物理上不可能触发访问冲突** ⇒ 那是**异常分发/failfast 路径**上的地址，
即 **`0xC0000005` 是"托管异常逃进原生代码"后 CLR 死掉的表现**，不是真正的坏指针 AV。
⚠ .NET Core 下**原生代码里抛的 SEH 异常无法转成可 catch 的托管异常** ⇒
`try/catch` 对它**无效** —— 这就是本项目反复出现的"try/catch 抓不到"的真身。

### 12.4 结论二：这一次崩在**残影路**（有铁证）
反汇编 `ActorTrail.Update`：
```
0x1807c59da   call   0x1807c4af0      ; -> ActorTrail::CreateTrail   ← 我们补的就是它
0x1807c59df   add    rsp, 0x30        ← dump 里栈上那一帧正好停在这
```
崩溃线程栈上的 GameAssembly 帧里有一个 `0x7C59DF` = **`CreateTrail` 调用点的下一句**
⇒ 崩在 `ActorTrail.CreateTrail` 内、也就是**我们 `TrailPostfix` 的调用链里**。

**根因（已修）**：上一版 `HookTrails` 把命中的 `ActorTrailProxy` 指针**永久存进 `_wantProxies`**，
而 `CreateTrail` **每帧都调**，每次都拿这些指针去 `Marshal.ReadIntPtr(p + 0x70)`。
残影特效是**池化/用完即毁**的 ⇒ 指针很快指向**已释放内存**；
一旦命中，还会 `new ActorTrailProxy(p)` 并往它的插值器数组里**写** ⇒ 写进已释放内存。
（时序相关 ⇒ 所以"有时候崩有时候不崩"。）

**修法：跨帧不留任何裸指针。**
- 识别改用 `ActorTrail._visual`(0x58) → `ActorVisualBase.resName`(0x48) —— **当场按名字认**，
  不再需要"先认 proxy 再回找 trail"。
- proxy 的插值器改写移到 `ProxyPostfix` **当场做完**（对象此刻必然活着）。
- 探针改成**同步**（就在 trail 刚建好那一刻读渲染器材质），
  原来的"隔 0.45s 再采"正是拿已销毁对象去 `GetComponentsInChildren` ⇒ 必崩。

### 12.5 铁律（新增，勿违反）
1. **绝不跨帧持有 il2cpp 裸指针**。池化对象（弹幕/特效/残影）随时会被回收。
   要跨帧认对象，就**跨帧按"内容"重新认一遍**（名字/资源名），不要留指针。
2. **绝不对可能已销毁的对象调 Unity API**（`==null`/`GetComponentsInChildren`/`material`/`name` 都算）。
   要读就**在创建它的那一 tick 内读完**。
3. 崩了先跑 `_dumpscan.py` 判"是不是老毛病"，再跑 `_dumpresolve.py` 定位方法 —— **不要靠猜**。

### 12.6 残影/电弧的实测事实（2026-10-04 深夜，同步探针实测）

- **识别**：`ActorVisualBase.resName` 实测是 `"es"`（**前缀**，不是特效名）、`ActorTrail.m_tag` 是空串
  ⇒ 这两个都不能用来认 trail。**能用的是 proxy 上的名字**（`Role.Es.es_dodge_01`）+
  `ActorTrailProxy.m_trail`(0x70) —— 实测在 proxy 的 `Play/Restart` 那一刻 **m_trail 已经就位**
  ⇒ 同一个 tick 内直接拿到 trail，**不需要跨帧指针**（这正是 §12.4 修掉的崩溃根因）。
- **电弧的渲染器不是角色材质**：`es_dodge_01` 的 trail 渲染器是
  `Line`(streak_024_b) / `lizi01`(streak_626_a) / `tuowei01`(pattern_027_b) / `glow01`(glow_002_c) /
  `ring02`(streak_400_d) / `guangzhu01|02`(streak_603_b)，**外加**一支 `Renderer`(SpritePalette —— 角色那份)。
  ⇒ **改电弧 = 改 `es_dodge_01` 自己的那几个材质，跟"角色本体着色"是两码事**（后者才是用户的禁区）。
- **颜色值**：这些材质上 `_AddColor` = **(0,0,0,0)（死属性）**；有值的是 `_TintColor`，且是 **HDR 白**
  （`11.984`=4× / `5.992`=2× / `2.996`=1×，`1,1,1` = 不变）。
  ⇒ 蓝白色**来自贴图本身**（streak/glow 流线贴图），`_TintColor` 只是亮度倍率。
  ⚠ 但这是"特效刚出生那一刻"的值 —— **只采一次会得出错误结论**（本项目栽过），
  已改成同一特效采 6 次（`ProbePerName`）。
- **下一步判据**：`MaterialColorInterpolator.target` 是 **public 属性**
  ⇒ 能直接问插值器"你驱动的是哪块材质"，把"改了没反应"变成可测量事实。

### 12.7 ★★ 多态数组：**碰字段前必须先验类型**（2026-10-04 23:17 崩的一次）

`ActorTrail._materialIpp` 的声明类型是 **`InterpolatorBase[]`** —— 里面**不是只有颜色插值器**。
实测第 2 条按 `MaterialColorInterpolator` 硬解释出来是：
`prop="" start=(0,1,0,0) end=(3056289000000,0,3036726000000,0)`（**指针位被当 float 读**的味道）。
按错误类型去读"Material 指针"⇒ 拿到垃圾地址 ⇒ 一解引用就是**不可 catch 的原生崩溃**
（dump 帧：`VFXEffectHub.EnableVisualElements` → 我们的探针）。

**规矩（两条，都是硬性的）**：
1. 碰任何字段之前先验类型：
   `new Il2CppObjectBase(ptr).TryCast<T>()`，null 就**跳过并打印真实类名**，绝不硬解释。
2. 判断真实类型用新加的 `Reflect.KlassName(ptr)`
   （`IL2CPP.il2cpp_object_get_class` + `il2cpp_class_get_name`）——
   比一个个 TryCast 猜快得多，一眼就能看出"这条到底是什么"。

⚠ `TintBrush.TintRawInterpolatorArray`（**一直在用的写路径**）原来也没验类型：
之前不崩只是因为 `propName` 不在白名单就 `continue`（空串天然不在白名单）——
**那是运气，不是安全**。现在两个地方都补上了 TryCast 校验。

### 12.8 ★★★ 全量拆解：原色 vs 换色皮肤的特效（2026-10-04 深夜，离线权威结论）

**工具链（这次才真正打通，之前两版都有盲点）**
- `_abload.py` —— 共享加载器。**关键**：一个容器文件（`m_XXX.m`）里是**几十个 bundle 首尾相接**
  （实测 `m_878.m` = 64 个切片铺满 8.44 MB）；只切一个切片 ⇒ 交叉引用全解不开、
  材质一律读成"(读不到)"，很容易误判成"这特效没有材质"。
- `_cabindex.py` → `_cabindex.json` —— **54431 个 cab 名 → 容器** 的索引。
  引用形状是 `archive:/CAB-<hex>/CAB-<hex>`，必须靠这张表把依赖 bundle 也载进来。
- `_fxfull.py` —— **全量**对比：把每个对象的全部内容摊开（材质属性/贴图哈希/Mesh/MonoBehaviour 全字段）。
  `_prefdiff.py`（MonoBehaviour 只记类名）和 `_trailcmp.py`（只比插值器数值）都**看不见**
  贴图/材质属性/关键字的差异 —— 之前"离线说没差异，线上却明显变色"就是这么来的。

**es_dodge_01 的全量对比结论**
1. prefab 里**只有 3 个 MonoBehaviour**：`VFXEffectHub` / `VFXEffectExtension` / `Other.ActorTrailProxy`。
   **一个材质、一个渲染器都没有**（材质全在别的 bundle 里，运行时才挂上来）。
2. 原色 vs 四套皮肤，**唯一的内容差异**在 `ActorTrailProxy.references`（SerializeReference）里的两条插值器：
   | 字段 | 原色 | esskin_06 |
   |---|---|---|
   | `MaterialColorInterpolator.propName` | `_AddColor` | `_AddColor`（同一个属性） |
   | `…endValue` | **(0, 0.380, 1.0, 0)** 蓝 | **(0,0,0,0)**（等于关掉） |
   | `MaterialFloatInterpolator.propName` | `_AlphaScale` | `_AlphaScale` |
   | `…duration` | 0.25 | **0.13** |
   ⇒ **皮肤不是靠换材质/贴图，而是靠"把 `_AddColor` 这层加法色关掉"**。
3. 这两条插值器**就是运行时 trail 的 `_materialIpp`**：
   运行时探针读到 `_materialIpp` 恰好 2 条 = `MaterialColorInterpolator(_AddColor)` + `MaterialFloatInterpolator(_AlphaScale)`
   ⇒ **对上了**，说明我们改的对象确实是它。
4. ⇒ **原色那道蓝电弧 = `_AddColor`(0,0.38,1) 这层加法色**（皮肤把它设成 0 就没有这层）。

**于是"改了没反应"只剩一个解释（下一步就查它）**：
`MaterialColorInterpolator` 里有个 `MaterialColorReferenceCounter`
（内嵌类，字段 `referenceValue` / `finalInterpolationEndValue` / `counter`，挂在
**静态** `ReferenceCounterMap` 上，键 = `(propID, propName)`）
—— 多个插值器写同一属性时，游戏用**引用计数**把它们的值合成一个"最终值"再落盘到材质。
若落盘用的是这个快照值，那么**只改插值器对象的 endValue 就没用**（快照在更早/别处算好了）。
★ 这同时也正是用户一直问的 **`叠色算法`** —— 游戏自己就是用 reference-counter 做"多个插值器叠在同一属性上"的合成。

### 12.9 ★★★ 官方换色配方（离线拆解换色皮肤得到，2026-10-04 深夜，**权威**）

皮肤**只带 prefab、不带任何材质/贴图**（440 个 esskin 资产全是 `effect/prefab/...`）
⇒ 换色配方**只能**写在 prefab 数据里 = `MaterialTinter`/`MaterialTinterProxy` 的插值器。
用 `_skinprobe.py <特效名>`（原色 vs esskin_06/10/12/13 逐条对照）读出来的**两套机制**：

| 机制 | 字段 | 原色 | 皮肤怎么做 |
|---|---|---|---|
| **Remap（重映射）** | 关键字 `_ENABLEREMAP_ON` + `_RemapColorFrom` / `_RemapColorTo` / `_RemapLerp` | `es_dash_01`: From=(0,0.145,1) **蓝**、To=(1.72,1.72,1.72) **中性白**、Lerp=1 | **只改 `From` 的色相**：esskin_12 `From`=(0.349,0.377,0.251) 米黄、esskin_13 `From`=(0.106,0,0.717) 紫；`To` 留中性白(只缩亮度, esskin_12→0.86) |
| **副贴图着色** | `_SubTexTintColor` | `es_attackhlod_02` 里**没有**这条 | 皮肤**新增**一条插值器：esskin_06=(1.227,0.735,0.826) 粉红、esskin_10=(0.929,1.227,0.739) 绿（HDR，>1 保亮度） |

**观感原理**：`From` 是要被重映射掉的色相、`To` 是重映射到的色（中性白=白芯）
⇒ 「白芯 + 彩色边」。皮肤换 `From` = 「白芯 + 皮肤色边」= **保留层次、只换色相**。

**结论（写进代码/配置的口径）**
- ✅ 白名单加 `_RemapColorFrom` 与 `_SubTexTintColor`（**只加 `From`，绝不加 `To`** ——
  `To` 是白芯，改了就抹平层次，正是以前那个"实心圆"事故）。
- ❌ `_AddColor` 从白名单**删掉**：离线 + 线上双向证实它管的是**角色身上那层加法色(描边)**，
  不是电弧。改它只会看到"角色描边变色"（2026-10-04 实测，用户："怎么变成角色描边了"）。
- 以前记的"`_RemapColorFrom`/`_RemapColorTo` 是一对、两个都改会抹平 ⇒ 故意不进白名单"
  —— **只对了一半**：不是不能碰，是**只能碰 `From`**。

**方法论**："我们没找对采样/改错属性"这类问题，正确答案在**官方自己怎么做的**里。
`_skinprobe.py` 这种"原色 vs 皮肤副本逐字段对照"比继续在线挂探针猜快得多。

### 12.10 ★★★ "怎么改都没反应"的最终原因：换色目标被我们自己的裁剪规则关掉了

**离线一次就说清了**（`_recolorscan.py`：扫所有特效 prefab，找带 `_RemapColorFrom` 的 = 官方预留的换色点）：
全 ES 只有 **5 个**特效带官方换色配方 ——
`es_dash_01`(From=(0,0.145,1) 蓝) / `es_holdfull_01` / `es_holdfull_02` /
`es_attackhlod_hit_01` / `es_attackhlod_hit_04`。**冲刺那道蓝光就是 `es_dash_01`。**

**而那道蓝光的换色配方挂在哪个子物体上**（离线读 prefab 的 GameObject 路径）：
```
MaterialTinterProxy   路径=es_dash_01/Other         插值器=['_RemapColorFrom','_RemapLerp','_RemapColorTo']
MaterialTinter        路径=es_dash_01/guangzhu01    插值器=['_MainTex_ST']
```
⇒ **`es_dash_01/Other` 正是用户先前要求"关掉那个叠加层"的对象**（`SuppressList = es_dash_01:Other`）。
关掉它 = **把换色目标本身关掉**。线上表现完全对上：
```
[特效裁剪] "es_dash_01(Clone)" 里关掉子物体 "Other" x1
hub 激活 "es_dash_01" … Tinter属性=<非颜色>
[特效换色:tint] 消费点 MaterialTinter.Play 触发 … 改写 0 条插值器
```
所以无论我们把白名单怎么改、Prefix 还是 Postfix，**都碰不到那条插值器** —— 它所在的对象被 SetActive(false) 了。

**处置（2026-10-04 深夜）**：`SuppressList` 清空（cfg + 代码默认同步改）。
用户当初"关掉它"的诉求，现在有更好的解：**把它染成目标色**（那层本来就是为换色设计的）。

**顺带两条教训**
- **"乱染"会伤及无辜**：白名单里有 `_TintColor`/`_SubTexTintColor` 时，`silhouette_601`、
  `Common.dodge_01_black`（冲刺影子）会被一起染红 —— 用户："怎么冲刺影子变了"。
  ⇒ 换色要**按"官方换色点"清单走**（带 `_RemapColorFrom` 的 5 个），不要全局摊开。
- **自问一句**：这个"改不动"的东西，是不是已经被我们自己的另一条规则关掉了？
  （本次就是 —— 裁剪规则和换色规则打起来了，日志里两条线索都在，但没人把它们连起来。）

### 12.11 ★★ 动作 → 特效 对照表（2026-10-05 实测，**别再靠推**）

把 `ActorBase.ActionMgr.CurrentActionName` 绑到特效日志上量出来的
（`[特效换色:seen] createVisualEffect "…" owner=… 当前动作="…"`）：

| 动作名 | 拉起的特效 | 说明 |
|---|---|---|
| `dashAir` | `es_dash_01` | **空中冲刺** —— 我们一晚上盯错的就是它 |
| **`dashSkill`** | **`es_attackAir_02`** | **消耗 MP 的那个冲刺（↓+冲刺）** ← 真正的目标 |
| `dashSkill2` | `es_attackAir_02a` | 同上的第二段 |
| `attackholdDashEX` | `es_attackhlod_02` + `es_dodge_01/02` | 加拉哈德冲刺（伴随残影 trail） |

### 12.12 目标特效的官方换色配方（逐特效不同！别套用别处的）

| 特效 | 官方配方 | 依据 |
|---|---|---|
| `es_dash_01`（空中冲刺） | `_RemapColorFrom`（+`_RemapColorTo` 中性白 / `_ENABLEREMAP_ON`） | 皮肤改 `From` 的色相（esskin_12 米黄 / esskin_13 紫） |
| **`es_attackAir_02`（MP 冲刺）** | **`_SubTexTintColor`** | **原色里一条颜色插值器都没有**；皮肤**新增**一条：esskin_06=(1.227,0.735,0.826) 粉红 / esskin_10=(1.144,…) |
| `es_attackhlod_02`（加拉哈德冲刺） | `_SubTexTintColor` | 同上，皮肤新增 |
| `es_attackAir_02a` | 皮肤未改 | — |
| `es_holdfull_01/02`、`es_attackhlod_hit_01/04` | `_RemapColorFrom` | 全 ES 只有这 5 个带 remap（`_recolorscan.py`） |

★ **两种配方对应两类做法**：
- 特效**已有**颜色插值器 ⇒ 走**插值器路**改它即可（`es_dash_01` 这类）。
- 特效**没有**颜色插值器（原色直接烘在材质里）⇒ 插值器路**天生够不着**，
  只能照官方做法**直写实例材质**的属性（`_SubTexTintColor`）——
  这就是新加的【副贴图着色路】（`TintSubTex`，点名 `es_attackAir_02`，三道名字判据 + 幂等）。

### 12.13 ★★ 冲刺解剖（`Modules/Common/DashAnatomy.cs`）——"把现场摊开"

用户原话："通过类似特效解剖的方式，把所有的冲刺动作产生的对象递归展开，这样我们就能直观的知道到底有什么了"。
存在的原因：这个项目反复在 **动作→对象→目标** 的映射上栽跟头（把 `_AddColor` 当电弧 → 把影子当电弧 →
把 `es_dash_01` 当 MP 冲刺的特效），**每一次都是靠推的，没人把现场摊开看过**；UE 又用不了。

**做成了什么**：一个动作按下，把它拉起的全部对象**递归展开**倒进日志：
- 层级树（深度到顶会**明说**还有几个子物体没展开，不静默截断）
- 每个节点的组件（按已知类型逐个 TryCast —— `GetComponents(typeof(Component))` 的 GetType() 恒为 "Component"）
- 每个渲染器：真实类名 / 材质名 / shader / **着色属性当前值**（关注名单一个不落 + 其余"非恒等色"的）
- 每个 `MaterialTinter`/`MaterialTinterProxy`/`ActorTrailProxy`：**插值器逐条**（真实类名 + propName + start/end）
- `ParticleSystem.startColor`、`VFXEffectExtension.ScreenSpace` 等

**两个出生口都挂**（缺一不可）：`ActorEffectMgr.createVisualEffect`（特效）+
`BulletMgr.createBulletImp`（弹幕）—— 实测 **MP 冲刺 `dashSkill` 只有弹幕、一只特效都不拉**。

**三条纪律**（都是血换的）：按"动作|对象名"去重（不按实例，防批量生成打爆）、每帧最多剖一个、
出生后 0.4s/0.9s 各剖一次（出生那一瞬子物体还没挂上、材质是占位图）。
配置：`DashAnatomy` / `DashAnatomyActions`(默认 `dash,esbullet`) / `DashAnatomyDepth`(5) / `DashAnatomyMax`(10)。

配套工具：`_dashmap.py`（从日志聚出**动作→特效/弹幕**表，不用重跑游戏）。

### 12.14 ★★★ 全部 ES 换色特效拆解结果（`_skindiff_all.py`，4 套皮肤 × ~95 个特效）——**官方换色配方的全貌**

工具：`_skindiff_all.py`（原色 vs 每个 `esskin_XX/<特效>` 逐个比：新增/删除插值器、值变化、对象增减、材质引用）
输出存档：`tools/_skindiff_out.txt`。规模：**esskin_06 改 94 个 / 10 改 96 / 12 改 96 / 13 改 73**。

**两种机制，覆盖全部换色（依特效而定）**：
| 情形 | 皮肤做法 | 例 |
|---|---|---|
| 特效**已有**颜色插值器（蓝在里面） | **改值**成皮肤的淡色 | `es_ah_01/glow01` `_TintColor` 蓝 `(0,0.327,2.0)`→淡粉 `(2.0,0,0.241)`；`es_dash_01/Other` 直接**删掉** `_RemapColorFrom/_RemapLerp/_RemapColorTo` 三条 |
| 特效**没有**颜色插值器 | **新增 `MaterialTinter` + `_SubTexTintColor` 插值器**（挂在点名物体上） | `es_attackAir_02`：`sword / sword(3)(4)(6) / 44`；`es_dash_02`：`tuowei01`；`es_dashatk1_01/02`：`02` |

**皮肤色（很淡，都是"白芯+浅色边"）**：
`esskin_06 = (1.227,0.735,0.826)` 浅粉白 / `esskin_10 = (0.739,1.227,0.745)` 浅绿白 + `(1.206,1.206,1.206)` 米白 …
⇒ **皮肤从不做"饱和色相乘"**，只在白上叠一层很淡的色。这正是用户描述的观感。

**对我们在做的 MP 冲刺（`dashSkill`→弹幕 `esbullet`→视觉 `es_attackAir_02`）**：
- 皮肤给这只补 tinter 的物体 = `sword / sword(3)(4)(6) / 44` ✓
- 冲刺解剖实测这些物体用的是 `pattern_026_a_wb` / `pattern_025_a` / `pattern_023_a`，**我们的写入对象与皮肤完全一致** ✓
- ⇒ 机制没错；"看不出变化"极可能是**目标色太淡**（用户试的 `F0C0F0`/`CCFFFF` 都接近白）。
- 若要复刻皮肤的观感，淡色公式应是 **`lerp(白, 目标色, ~0.25) × 亮度`**，而不是饱和色直乘（蓝贴图 × 强红 = 变暗变黑）。

### 12.15 冲刺换色：两个真凶（2026-10-05）

**① 写错了对象（不是被覆盖）**
日志实证：我们写的品红**确实落地并且留住了** ——
`"44" _TintColor=(2.263,0,2.828,1)`、`"sword" _TintColor=(2.397,0,2.996,1)`（冲刺解剖复核），
可画面依旧蓝 ⇒ **`pattern_025_a` / `pattern_026_a` 不是那道蓝光的可见载体**。
真正像"闪光+向外扩散圆弧"的是同一份解剖里的：
`Refrac`（shader `NOAH/Effect/Variant/Refract`，折射环）/ `guangzhu01`（`streak_604_a` 光束）/ `glass`，
而它们**我们一个都没写到**。

**② 为什么没写到：它们在【弹幕自己的层级】下**
`esbullet → ActorAnim → Motor → Renderer → …→ Refrac/guangzhu01`
—— 这棵子树**没有 VFXEffectHub**，而我们的写入只挂在"hub 激活"（走特效自己的子树）
⇒ **弹幕子树从来没被走过**。修法：`BulletPostfix`（子弹出生口）也调一次 `MaybeTintSubTex`。

**③ 顺手修掉两个自己造的坑**
- **黄字刷屏 + 卡顿的真凶**：冲刺解剖里**每个节点**都调 `AccessTools.TypeByName` —— 那 API
  每次都重扫全部程序集，找不到还打 Warning。日志实证 `GetTypesFromAssembly …CoreModule` ×995、
  `Could not find type named NOAH.VFX.VFXEffectExtension` ×130 ⇒ 改**一次性解析 + 缓存（null 也缓存）**。
- `InstantiateProbe.InWindow` 每次调用 `Cfg.List()`（每次都 Split 新数组）⇒ GC 风暴 ⇒ 用户实测"卡卡卡卡"。
  **热路径上任何会分配的东西都必须缓存**，包括看似人畜无害的 `Cfg.List()`。

### 12.16 参考日志存档（2026-10-05 12:43）

`_modding/logs/LogOutput-1005-1243-dash+dashSkill-原色.log`（3.97 MB / 30599 行）
—— **只包含一个地面冲刺 + 一个 MP 冲刺（dashSkill），原色皮肤**，是最干净的一份现场。
配套 `_modding/logs/LogOutput-1005-1243-关键片段.md` 把四类关键行抽出来了（不用在 4 MB 里 grep）：
冲刺解剖（对象递归展开）/ 实例化探针（对象出生与播放）/ 副贴图着色写入 / 特效与弹幕出生。

⚠ 这份日志里的 `HarmonyX` 黄字（3893 条）是**当时还没修的**两个热路径反射坑造成的
（`AccessTools.TypeByName` 每节点一次 + `Cfg.List()` 每调用一次），**不是现场本身的特征** ——
看这份存档时请忽略那两类黄字（见 §12.15）。

### 12.17 ★★★ 那道"闪蓝光 + 向外扩散圆弧"的真身（2026-10-05，从 12:43 那份存档里读出来的）

```
【所属】 attackholdDashEX ▸ 特效 "Role.Es.es_dash_01"
    "Refrac"      ParticleSystemRenderer  shared="normal_002_a"   shader="NOAH/Effect/Variant/Refract"  _TintColor=(1,1,1,1)
    "guangzhu01"  ParticleSystemRenderer  shared="streak_604_a"  shader="NOAH/Effect/Variant/Common"   _TintColor=(5.992,5.992,5.992,1)
```
另有同族的 `Line / lizi01 / tuowei01 / ring01`（`streak_024_b`/`streak_626_a`/`pattern_027_b`/`pattern_025_a`）。

**⇒ 那道折射环（向外扩散的圆弧）在 `es_dash_01` 里，而我们的点名名单写的是 `es_dash_02` —— 漏了 `_01`！**
整整一轮"值写进去了、画面就是不变"，根因就是这个（写到了另一只特效上）。
名单现已覆盖整个冲刺族：`es_dash_01/02, es_attackAir_01/01a/02/02a/03a, es_dodge_01/02, esbullet`。

**教训（与 §12.4/§12.14 同一类）**：一晚上在"目标是谁"上连环栽跟头（`_AddColor`→角色描边、
影子→电弧、`es_dash_01`→MP 冲刺、`es_dash_02`≠`es_dash_01`）。
**只要"目标是哪个对象"这件事靠人手填名单，就一定会填错** —— 正确做法是
**让现场自己给出名单**（`冲刺解剖` 的递归展开 + `实例化探针` 的出生/播放记录），
用户要的"备份这次日志"这一步正是把这份名单**固化**下来（存档见 §12.16）。

## §13 ★★ 找到那道蓝白光弧的**颜色开关**了：`es_stand_01 / Other01 / _DecoTexTintColor`（2026-10-05）

### 对象身份（用户实测+离线双重确认）
| 对象 | 是什么 |
|---|---|
| `…/es_stand_01/lod1/ring02` | **电弧光环本体**（用户实测：SetActive 藏掉它，光环就没了） |
| `…/es_stand_01/lod1/guangzhu01` / `guangzhu02` | ES 身上的**光带**特效（不是那道弧） |
| `…/Motor/Renderer`（次序第一个） | 被临时替换了材质的人物渲染器 |

### ★★ 颜色开关 = `Other01` 的 `MaterialTinterProxy._DecoTexTintColor`
离线铁证（`tools/_tinterdump.py es_stand_01`，原色 + 4 套皮肤**全部插值器**逐条）：

```
原色      Other01 / MaterialTinterProxy  _DecoTexTintColor
            (0,0,0,0) => (0.580, 0.966, 2.996, 1.000)   ← 强蓝
            (0,0,0,0) => (0.049, 0.099, 0.358, 1.000)   ← 强蓝
esskin_06 → (0.491, 0.243, 0.260)   粉白
esskin_10 → (0.269, 0.490, 0.243)   绿白
esskin_12 → (0.391, 0.396, 0.331)   米白
esskin_13 → (0.830, 0.521, 2.567)   蓝（13 本来就是蓝套）
```

⇒ **`es_stand_01` 里官方皮肤唯一改过颜色的东西就是它。** 而 `MaterialTinterProxy` 是
"代理"型着色器 —— 它把颜色施加到**子孙渲染器的材质**上，所以「ring02 是电弧但改 ring02 没用」。

### ⚠⚠ 两个把我带偏过的坑（都记下来）
1. **`_skindiff_all.py` 只列差异** ⇒ 某个对象在四套皮肤里都没被改，它就**一次都不出现在输出里**。
   我把"没出现"读成了"官方没动它/它不重要"。**要看一个对象的全貌必须摊开全部插值器**
   （所以才写了 `tools/_tinterdump.py`）。
2. **张冠李戴**：`guangzhu01` 的 `_TintColor=(5.647,6.525,11.984)` 属于 **`es_fall_04`** 那一份；
   `es_stand_01` 里的 `guangzhu01` **只有 `_MainTex_ST`，一个颜色插值器都没有**。
   ⇒ 同名对象在不同 prefab 里可以完全是两回事，**必须按 prefab 逐份看**。

### `ring02` 自己的 tinter 是**中性白**
`ring02 / MaterialTinter / _TintColor = (4.237, 4.237, 4.237)` —— 完全等灰，四套皮肤一模一样。
它只是个**乘数**，不是颜色开关。（这也说明"看到数值就以为它是答案"是不行的：
R=G=B 的插值器天然不是颜色来源。）

### 管线的盲点（已修）
原来只按**特效 hub 的名字**放行（`TintSubTexEffects`），于是清单之外的特效下面的
对象**永远够不着** —— 哪怕它才是真正的颜色开关。新增两个配置键：

```
TintObjects     = Other01        # 按【子物体名】下刀, 命中就改写它自己的着色插值器
TintObjectHosts = es_stand_01    # 只在这些宿主特效的子树里找
```

走已验过的 `TintBrush.TintTintersOn`（它本来就覆盖 `MaterialTinter` / `MaterialTinterProxy` /
`ActorTrailProxy` 三种载体），只在 **hub 激活 / 弹幕出生**两个事件点遍历子树，
**不做每帧遍历**（每帧遍历子树 = 白烧 CPU，本项目在 `AccessTools.TypeByName` 上栽过同类坑）。

### ★★ 现场检查器（F10）与"SetActive 是破坏性动作"
`LiveInspector`：把捕获到的 GO 列出来，在**冻结的世界**里开关/挪位置来认特效。
**关键设计**：列表里**不持有任何 Unity 对象引用**，只存 `实例ID+路径`，操作时现场重找
（池化对象跨帧持引用 = 不可 catch 的原生崩溃）。

⚠⚠ **SetActive 会惊动 RecycleRoot 对象池**：它触发 `OnDisable`，池子把对象判为空闲并
别处复用 ⇒ 用户实测"隐藏整个角色再显示"后 **光环消失不回来 + 角色渲染损坏**。
**认特效只能用 `Renderer.enabled`**（只影响绘制，不触发 OnEnable/OnDisable）。
⇒ 面板里 SetActive 已降级为"按住 Shift 才生效"，并加了"还原全部改动"。

### 时停（用户要的 UE 行为）
`SnapshotProbe`：F5 慢放循环(1/0.1/0.02/0) → F6 时停 → F7 捕获(名字标在屏幕上)。
⚠ 恢复判定必须挂 IMGUI（渲染循环驱动，不受 timeScale 影响）；挂在游戏逻辑帧上 =
冻住后再也收不到按键（栽过一次）。

### ⚠⚠ 排除名单把新规矩挡死了（2026-10-05，自己的门挡自己的路）
日志实证：`[特效换色] 按排除名单跳过 "es_stand_01" (含 "es_stand_01")`，
而 `特效换色:子物体` **出现 0 次** —— 我新加的"按子物体名下刀"那条路挂在
`AllowBattleEffect`（排除名单）**之后**，所以**一次都没被调用过**。
`es_stand_01` 在 `AssetExclude` 里（当初为了不让角色站姿被整体染色而加的），
而那道电弧恰恰就在 `es_stand_01` 下。

**修法**：把 `MaybeTintObjects` 提到排除名单**之前**。
放宽的依据是它自己有两道**精确**闸门（宿主名必须命中 `TintObjectHosts`，
子对象名必须命中 `TintObjects`），比"整棵子树染色"窄得多。

★ **教训（通用）**：加一条新的过滤/放行规矩时，**必须先确认它挂在哪道旧门之后**。
粗粒度的排除名单会把后面所有精确规矩一起吃掉 —— 表现是"新代码像没写一样"，
而不是报错。本项目在"过滤把目标自己干掉"上已经是第三次栽了。

### 相关对象身份（用户实测）
| 路径 | 是什么 |
|---|---|
| `.../es_stand_01/lod1/ring02` | 电弧光环本体（材质 `streak_400_d`，shader `NOAH/Effect/Variant/Common`）|
| `.../es_stand_01/lod1/guangzhu01` / `guangzhu02` | ES 身上的光带 |
| `.../es_stand_01/lod1/glow01` | 角色身上的辉光 |
| `.../Motor/Renderer`（次序第一个）| 玩家渲染器，材质是 `Hidden/InternalErrorShader (Instance)`（预制里本来就是空槽，靠 MaterialCollector 运行时填）|

⚠ 我们**只观察** `MaterialCollector`（`DashAnatomy.Parts` 里那一行），没有改过它。

## §14 ✅ 电弧光环换色：成了（2026-10-05，一天）

**能用的做法**：`TintObjects = ring02,glow01,guangzhu01,guangzhu02`（直写它们的渲染器实例材质）。

| 对象 | 结果 |
|---|---|
| `ring02` | ✅ **变了** —— 它就是那道电弧光环的本体 |
| `glow01` / `guangzhu01` / `guangzhu02` | ✅ 同路数（lod0/lod1 下的粒子渲染器，材质有 `_TintColor`）|
| `Other01`（官方配方）| ❌ **没效果** |

### ★★★ 本轮最值钱的一课：**「官方改了 X」≠「在 X 上照抄就有效」**
离线看官方皮肤，`es_stand_01` 里唯一被改的是
`Other01 / MaterialTinterProxy / _DecoTexTintColor`（原色强蓝 → 粉/绿/米白），
于是理所当然照着抄 —— **完全没反应**。

原因：**Proxy 是在 `Play` 时把值推/拷进目标材质的**，我们「就地改写它的插值器」推不动；
而普通 `MaterialTinter` 的 `InterpolatorSet` 是**按引用**存数组（`mov [r14+0x40], rdi`），
所以能就地改。**两种 Tinter 的传播语义不一样。**

⇒ 真正可靠的是「**直写渲染器实例材质上的属性**」——那条路一次就通。

### ⚠ 另外两条踩到的坑
1. **BepInEx 存盘时会用代码里的描述重新生成整个 cfg**，手写的 cfg 注释**会被冲掉**。
   ⇒ 结论要写进**代码描述**（Plugin.cs）或本文档，cfg 注释不是持久的地方。
2. **页面里手打的配置值会带打字错误**（用户打了 `ring02s`）——而我们的比对是**精确**的，
   于是静默不命中。⇒ 值不生效时，先去 cfg 里**逐字核对**那个字符串，别急着怀疑机制。

### 对象身份总表（用户实测，2026-10-05）
| 路径 | 是什么 |
|---|---|
| `…/es_stand_01/lod1/ring02` | **电弧光环**（材质 `streak_400_d`，shader `NOAH/Effect/Variant/Common`）|
| `…/es_stand_01/lod1/glow01` | 角色身上的辉光 |
| `…/es_stand_01/lod1/guangzhu01` / `guangzhu02` | ES 身上的光带 |
| `…/Motor/Renderer`（次序第一个）| 玩家渲染器（材质是 `Hidden/InternalErrorShader`，预制里本就是空槽）|

`es_stand_01` 运行时结构：根下只有 `lod0` / `lod1` / `audio`；
`lod0` 与 `lod1` 各有 5 个子物体 = `Other01` + `glow01` + `guangzhu01` + `guangzhu02` + `ring02`。

## §15 叠色（Tint）口径定案：用旧的 peak + 上限 8（2026-10-05 用户拍板）

`TintBrightMode = peak`、`TintMaxScale = 8` ⇒ **与旧的 `k = max(原)/max(目标)` + `k<=8` 完全等价**。
用户原话：「换成旧的吧，大不了我不用高饱和色了」。

### 为什么会有这个取舍（结论：无解，只能选）
「保亮度」保的是什么，取决于目标色的**饱和程度**：

| 目标色 | max/Luma | 三种口径 |
|---|---|---|
| `A0F0C0` 淡绿白 | 1.09 | 几乎一样 ⇒ 怎么都对 |
| `CD00F0` 饱和洋红 | **3.94** | peak=偏暗 / luma=峰值×3.9过曝刺眼 / capped=介于 |
| 官方皮肤 esskin_06/10/12 | **1.00~1.45** | —— 全是淡色！ |

★ 关键事实：**官方皮肤的换色目标色全是淡色（max/Luma 1.0~1.45）**。
在加色混合 + HDR + bloom 的管线里，高饱和目标色必然在"偏暗"和"过曝刺眼"之间二选一，
美术就是靠"只用淡色"绕开的。⇒ 换色时**避开高饱和色**是和官方一致的做法。

### ★ 色差的真正来源（用户问："为什么纹章解放颜色不一"）
日志实测（`[特效换色:副贴图]` 行）——纹章那一族的材质 `_TintColor` **全是 R=G=B 的中性值**，
但**量级差 6 倍**：`glow_002_c`=11.984、`nature_005_a`=5.992、`other_009_a`=4、
`pattern_026_a`=2.996、`pattern_023_a`=2.828、`pattern_024_a`=2。

⇒ 这些**不是颜色，是亮度乘数**。中性值经 Tint 后必然变成 `目标色 × 原值`，
**色相精确等于目标色**，彼此差的只是亮度 ⇒ 亮的过曝成白心、暗的显色 ⇒ "有的绿有的偏白"。
**这个亮度分层是原始素材里就有的，不是染出来的。**

而旧版看着"整齐"，是因为 `k<=8` 把最亮的（需要 11.984）**压到 8** ⇒ 抹平了分层。
⇒ 换句话说：**旧的"整齐"是假象，代价是最亮的特效被压暗。**

⚠ 另有一处**真 bug**（已修，与口径选择无关，保留）：
`TintSubTexMaterials` 原来从 `m.GetColor(prop)`（**当前值**）算，
而日志证明**游戏自己在逐帧回写这个属性**（同一个 `pattern_026_a` 两次访问，
`当前` 一次是已染值、一次是原值）⇒ 采样时刻不同 ⇒ 同一实例结果不同。
已改成 `MatOrigin`（记一次原值，之后永远从原值算）⇒ 确定性。

### 手动验证公式等价（保留给将来）
`peak` 模式下 `Luma()` 根本不参与 ⇒ 公式 = 旧版逐字等价；
唯一区别：**夹断时会写一行日志**（旧版静默截断）。
