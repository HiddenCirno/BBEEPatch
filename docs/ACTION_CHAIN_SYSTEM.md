# 动作 / 连段 / 招式衔接 系统结构

> 全部结论来自**反汇编**与 `dump.cs` 字段偏移，不是推测。
> 每条都标了 RVA 或偏移，便于复核。最后更新 2026-10-02。
>
> ⚠ 工具局限：`_disasm.py` 的 RVA→名字映射是 `setdefault` 的，而 IL2CPP 里
> **大量微小 getter 共用同一段机器码**，所以输出里的 `XxxWrap::get_Yyy` 常常是
> "同 RVA 里第一个被命名的类"，未必是真身。
> **判断字段归属要看偏移量和调用上下文，不要只看标注的名字。**
> 本文里凡是标注"同 RVA 复用"的地方都是这么处理过的。

---

## 1. 数据结构（dump.cs 原文偏移）

```
PlayerSkillMgr
  0x10 Owner/PlayerObj ...
  (技能组见 §2)

PlayerSkillChain                     ← 一条链 = 一个技能组
  0x10 Owner        (PlayerSkillMgr)
  0x18 SkillList    (List<PlayerSkill>)     ★ 链的本体
  0x20 Status       (ChainStatus 枚举)
  0x24 SkillType
  0x28 Cur          (PlayerSkillChainCurSkill)   ★ 每链独立的"当前段"游标
  0x30 LastSkill    (PlayerSkillChainCurSkill)
  0x38 NextSkillPredict
  0x40 CoolDownRemain / 0x48 CoolDownTotal
  0x50 MuteBy      0x54 MuteRemain  0x5C LastMuteMax
  0x64 CastTimes
  0x68 m_Input      (InputCmd)              ★ 这条链认哪个键

PlayerSkillChainCurSkill
  0x10 Skill      (PlayerSkill)
  0x18 TimeEllaps (Fp)              ★ 这一段已经播了多久

PlayerSkill
  0x10 Owner            (PlayerSkillChain)
  0x18 SkillId   0x1C ActorId
  0x20 SkillActivate    (SkillActivateFixedPointWrap)   → data = 那一行 protobuf
  0x28 AttrOrder 0x2C Level
  0x38 Attr             (SkillAttrFixedPointWrap)
  0x40 SourceTriggerId
  0x44 Input            (InputCmd)
  属性 IsStartingSkill → **不是存储字段，是算出来的**（见 §3.3）
```

**数据来源（★ 之前找错了表，已修正）**：运行时用的是
`data/xlsxfixed/skillactivatefixedpointwrap.ab`（container `XlsxFixed/SkillActivateFixedPointWrap.bytes`，
TextAsset `SkillActivateFixedPointWrap`，348,579 字节），
由 `PlayerSkillMgr::InitSkills`（RVA 0x1bd9ee0）调用 `Xlsx::get_SkillActivateFixedPoint` 读入 —— 已反汇编确认。
⚠ 之前解析的 `data/xlsx/skillactivate.ab` 是**编辑器用的浮点变体**（同一个 sv 的兄弟资产），
两者 2438 行 / ES 97 行、`(group,order,action,preSkillOrder)` 完全一致，**只有 FixedPoint 的编码不同**。
另外：`_finalmap.py` 把 **f52（ActionpointInputTag2）当成了方向** —— **真正的方向字段是 f9**。

**protobuf 行 `SkillActivateFixedPoint` 的字段号**
（`SkillActivate` TypeDefIndex 921 与 `SkillActivateFixedPoint` TypeDefIndex 1721 编号一致）：
`2=ActorId 3=Group 4=Order 5=Action 6=StartTrigger 7=ExitTrigger 8=Input 9=InputDir
10=ReqTriggerId 11=Mps 15=PreSkillOrder(repeated int, packed) 16..19=Allow* 21=ActdurStrict
22=Timeout 24=Mutelist 25=Preinputtime 26=UseLongPress 27=LongPressStart 28=LongPressEnd`

**衔接相关的字段**：
`actorId / group / order / action / input / inputDir / reqTriggerId / mps /
preSkillOrder / actdurStrict / timeout / preinputtime / useLongPress / longPressStart / longPressEnd`

其中 **`preSkillOrder` 是 `RepeatedField<int>`，是一个【列表】** —— 这是整套系统里最关键的一个事实。

---

## 2. 每帧的推进流程

```
PlayerSkillChain.DoUpdateAndCheckInputSucc(dt)          RVA 0x1bb10f0
  ├ if (Status == 4 || Status == 5) return false        ; 链处于冷却/禁用
  ├ if (MuteRemain > 0)             return false        ; 静音期(受击硬直等)不收输入
  ├ if (Cur.Skill != null && Cur.TimeEllaps < ActdurStrict) return false
  │                                       ★★ 硬地板: 没播满就【丢弃输入】, 连缓存都不做
  └ return findAndStartSkill_Imp(-1)                    RVA 0x1bb3320
```

`findAndStartSkill_Imp(startOrder)` 的**固定顺序**（按调用先后）：

1. **`findNextSkillMatchPreOrderAndInputDir(startOrder)`** ← 主路径
2. 找不到 → **`findStartingSkillMatchInputDir(startOrder)`** ← 兜底：退回"起手段"
3. 若命中段带 `useLongPress` → 校验长按时长落在 `[longPressStart, longPressEnd]`
4. **`preinputtime` 窗口判定**
5. `ActionMgr.CheckCanChangeToAction(SuccessAction)` — 动作层还允许不允许切
6. `PlayerSkillMgr.SkillChangePreCall(...)` — （我们的连段模组挂的"段闸门"就是这里）
7. `PlayerSkillChain.startSkill(skNew, inputDir)`

---

## 3. 核心：下一段到底怎么选出来

### 3.1 主路径 —— `preSkillOrder` 是**列表**，匹配是"包含"

两个关键取值器（都被 `_disasm.py` 标成了别的类的名字，**已按偏移量验明正身**）：

```
wrap = PlayerSkill[0x20]                    ; SkillActivateFixedPointWrap
order 取值器  RVA 0x5bb720:  rax = wrap[0x10](=data);  return data[0x20](int32)  → order
preList 取值器 RVA 0x148c140: rax = wrap[0x10](=data);  return data[0x60](引用)   → preSkillOrder
```
对得上已知的 protobuf 行布局（`order` @0x20、`preSkillOrder` @0x60），可以定案。

`findNextSkillMatchPreOrderAndInputDir`  RVA 0x1bb3700 的实际流程：

```
Cur      = this[0x28]
curOrder = Cur.Skill[0x20].data[0x20]                 ; 即当前段的 order

for each skill in this.SkillList[0x18]:
    if (skill == null) continue
    list = skill[0x20].data[0x60]                      ; preSkillOrder 列表
    if (list == null) continue
    idx = indexOfNoGC(list, curOrder)                  ★ RVA 0x1bb3f50, 线性查找
    if (idx < 0) continue                              ; ← 这个候选的前置列表里没有当前段
    if (candidate.data[0x20] < startOrder) continue     ; 候选自己的 order 也要够大
    if (!PlayerSkillUtility.CheckSkillCanCast(skill, player, true)) continue
    if (skill[0x44] == 0) continue                     ; Input 不能为 0
    if (PrecheckActionCd == 1 && SuccessAction 非空)
        if (!ActionMgr.CheckCanChangeToAction(SuccessAction)) continue
    if (!PlayerSkillUtility.CheckPlayerSkillInputDir(player, triggerId)) continue
    return skill                                       ★ 命中
```

**判定式一句话：候选段的 `preSkillOrder` 列表里，必须包含【当前段】的 order。**

`indexOfNoGC(IList<int> src, int find)` 只是个不分配内存的线性查找（RVA 0x1bb3f50），
它的第一个参数类型是 **`IList<int>`** —— 直接证明 `preSkillOrder` 是列表而不是标量。

### 3.2 兜底 —— "起手段"

`findStartingSkillMatchInputDir`  RVA 0x1bb3b20：
遍历 `SkillList` → `PlayerSkill.get_IsStartingSkill` → `CheckSkillCanCast` →
`PrecheckActionCd`/`CheckCanChangeToAction` → `skill[0x44] != 0` →
`CheckPlayerSkillInputDir` → 命中。

### 3.3 `IsStartingSkill` 的定义（反汇编 RVA 0x1bb5480）

```
list = this[0x20].data.<preSkillOrder>
return (list == null) || (list.Count <= 0)
```

**即：`preSkillOrder` 为空的段 = 起手段。**

这条对改模组非常重要：
我们克隆出来的第 0 段必须满足"前置列表为空"才能作为起手被 `findStartingSkillMatchInputDir` 找到；
后续段则必须把 `preSkillOrder` 设成**前一段的 order**（这正是之前反汇编 `PreSkillOrder` 修好的那个 bug）。

---

## 4. 输入方向（`SkillInputDirType`）—— 完整 9 个取值

`PlayerSkillUtility.StrictMatchPlayerSkillInputDir(player, inputDir=(x,y), dirType)`  RVA 0x1be0660
是一个**跳转表 switch**（表 @RVA 0x1be09f0，9 项）。逐 case 反汇编结果：

| 值 | 名字 | 判定 | 证据 |
|---|---|---|---|
| 0 | `Any` | `true` | `mov al,1` |
| 1 | `Up` | `y > 0` | `comiss y, 0; seta` |
| 2 | `Down` | `y < 0` | `comiss 0, y; seta` |
| 3 | `Front` | `facing * x > 0` | `ActorDir::get_Dir`→Fp→float `* x` |
| 4 | `Back` | `facing * x < 0` | 同上，比较方向相反 |
| 5 | `NoDir` | `x == 0 && y == 0` | 精确与零向量比较 |
| 6 | `Left` | `x < 0` | `comiss 0, x; seta` |
| 7 | `Right` | `x > 0` | `comiss x, 0; seta` |
| 8 | `AnyX` | `x != 0` | 相等则 false，否则 true |

⚠ **本项目旧笔记里只记了 0~5，实际枚举有 9 个**（`Left=6 / Right=7 / AnyX=8` 是之前漏掉的）。
旧笔记里 `Front=3 / Back=4` 的语义也确认了：**是"输入方向与角色朝向同向/反向"**，
不是绝对方向 —— 所以它对镜像/换边是自动正确的。

另外注意：**这里的比较是严格 `> 0`，没有死区**。死区必然是在上游生成
`inputDir` 那个 Vector2 时施加的（见 `input_system_analysis.md`）。

### 4.1 链搜索实际用的是**另一个**方向判定（已反汇编定案）

⚠ 我早前写的"由 `triggerId` 推出方向"是**错的** —— 那是被错误符号名误导。
真实签名是：

```
PlayerSkillUtility.CheckPlayerSkillInputDir(ActorBase actor, SkillInputDirType dirType)
    RVA 0x1bddc10
调用方传的第二个参数 = SkillActivateFixedPointWrap.get_InputDir()  (getter RVA 0x148b040)
                                              ↑ 即【这个技能自己声明的 inputDir】
内部: 读 actor.Input → IInput.GetMoveDirSimple() 得到当前移动方向
      → 按 dirType 走 9 路跳转表(RVA 0x1bde16c):
         Up    dir.Y > T          Down  dir.Y < -T
         Front dir.X*facing > T   Back  dir.X*facing < -T
         NoDir dir.X==g && dir.Y==g      Left/Right/AnyX 看 dir.X
      其中 T = static(+0x114) + static(+0x12C)      ← ★ 有死区阈值
```

**两个方向判定器的区别（别混用）：**

| | `CheckPlayerSkillInputDir(actor, dirType)` | `StrictMatchPlayerSkillInputDir(player, inputDir, dirType)` |
|---|---|---|
| 用于 | **技能链搜索**（真正生效的那个） | 预测/UI 路径 |
| 方向来源 | **实时移动方向** `GetMoveDirSimple()` | 调用方传入的 `Vector2` |
| 死区 | **有**（阈值 `T`） | **无**（严格 `> 0`） |

`GetMoveDirSimple` → `GetMoveDir`（RVA 0x1b992a0）= 取 **`Move`（InputCmd = 0xF）命令的 `CurDir`**。
死区是在**更上游**施加的：`GameInputManager.ResolveActorAxis`（RVA 0x1c54650）里调
`MathUtility.Digitize(ref dir, xAxisDeadZone[+0xC8], yAxisDeadZone[+0xCC])`。

### 4.2 ★★ 预输入不是队列，是"按键时间戳 + 有效期"

**`PlayerSkillChain` / `PlayerSkillMgr` 里没有任何待处理输入队列**（字段表里没有这种东西，
反汇编也找不到重放逻辑）。缓冲是**隐式的**，靠每个按键命令各自的一份持久状态：

```
PlayerInput
  0x28 m_cmdState : List<InputCmdState>    ← 每个 InputCmd 一份, 线性查找(getInputCmd_raw RVA 0x1b9b150)
  0x30 m_histList : List<InputCmdHistory>  ← ⚠ 只被 JS 包装层读, 不参与技能判定
  0x38 m_maxHist

InputCmdState
  0x14 Pressing (bool)
  0x18 CurDir (Fp2)
  0x28 LastStartPressingDir
  0x38 LastPressTimeStamp   (Fp)   ★ 按下时刻
  0x40 LastUnpressTimeStamp (Fp)
  0x58 PressInputSatisfied  (bool) ★ 一旦置位, 本技能不再接受这个按键
```

- **按下**时（`OnInputCmdChange` 按下分支 RVA 0x1b99bb0）：`LastPressTimeStamp = owner.Time`，并清 `PressInputSatisfied`。
- **松手**时：只写 `LastUnpressTimeStamp`，**`LastPressTimeStamp` 保留**。
- 每帧重新轮询 `IInput.GetCmdStatus(cmd)`，判定 `now − LastPressTimeStamp ≤ 窗口`。

**所以"预输入"的真相是：按一下，这次按键在 `Preinputtime` 秒内一直算数，松不松手都一样。**
它不是"动作结束前 N 秒才收输入"，而是"**你按下的那一刻起，N 秒内有效**"。

### 4.3 每帧读输入的路径

```
PlayerObj.UpdateLogic (0x1badc30)
  → PlayerSkillMgr.DoUpdate (0x1bd8780)
     → 每条链 PlayerSkillChain.DoUpdateAndCheckInputSucc
        → IInput.GetCmdStatus(chain.m_Input[0x68])

chain.m_Input 由 autoGetInputType (RVA 0x1bb31b0) 算出 =
  该链 SkillList 里【第一个 Input != 0 的技能】的 Input
  ⇒ ★ 改连段时，新造的段必须带上正确的 Input，否则整条链认错键
    （现有实现 `ps.Input = tmpl.Input` 是对的；换模板段时要留意）
```

原始输入在 `GameInputManager.ResolveActorAxis`(0x1c54650) → `ResolveActorButton`(0x1c54850)
→ `NotifyCmdChange`/`NotifyDirChange` 抛事件 → `PlayerInput.Init`(0x1b996b0) 订阅。

---

## 5. 为什么"隔了别的招还能接着接"—— 两个现象的机制

### 5.1 佩利诺尔 → 平A 的 2/3/4 段 —— ★ 成立（实测数值）

平A 和佩利诺尔**在同一条链里**（`group=1` Attack），只是 `inputDir` 不同
（平A=`Any`，佩利诺尔=`Down`）。抽取出的 `group=1` 实际数值（见 `skillchain_graph.md`）：

| order | action | inputDir | preSkillOrder | 含义 |
|---|---|---|---|---|
| 4, 5 | `attackD1` | Down | 空 | 佩利诺尔·起手（两条重复行） |
| 6 | `attackD2` | Down | [4] | 佩利诺尔 2 |
| 7 | `attackD3` | Down | [6, 11] | 佩利诺尔 3 |
| 8 | `atkAirX` | Down | [7] | 佩利诺尔 4（空中收尾） |
| 9 | `attack1` | Any | 空 | 平1（起手） |
| 10 | `attack2` | Any | **[9]** | 平2 |
| 11 | `attack3` | Any | **[10, 4, 5]** | 平3 ← 前置里带佩利诺尔起手 |
| 12 | `attack4` | Any | **[11, 6]** | 平4 ← 前置里带佩利诺尔 2 |
| 13 | `atkAirX` | Any | [7] | 空中收尾 |

**逐条对上你的描述：**
- `attack3`(平3) 的前置 = `[10, 4, 5]` → **从佩利诺尔起手(order 4/5)按平A键 → 直接进平3** ✓
- `attack4`(平4) 的前置 = `[11, 6]` → **从佩利诺尔2(order 6)按平A键 → 直接进平4** ✓
- `attack2`(平2) 的前置只有 `[9]`（= 平1）→ **佩利诺尔接不到平2**（这一条与直觉不符，但数据如此）

也就是说：**"佩利诺尔能接平A的 3/4 段"是表里写死的边，不是特殊逻辑** ——
实现方式就是**一个段的 `preSkillOrder` 列表里可以同时列多个不同方向分支的前置**。
全表里有 **12 行**的前置列表长度 > 1，最长的是 `group=1 order 3 AttackUp2`，
列表 = `[4,5,6,7,8,9,10,11,12]`（9 项），**一个上+攻击可以从链上几乎任何一段接出来**。

### 5.2 布鲁诺1 → 打了平A → 还能接布鲁诺2/3 —— ★ 技能表里**没有**这个信息

布鲁诺在 **`group=2`（Skill1，技能键）**，平A 在 **`group=1`（Attack，普攻键）**。

**已确证（字段层面）**：`PlayerSkillChain` 是**按技能组各一条**的对象（`m_SkChains[group]`），
每条链各有自己的 `Cur`（0x28）、`LastSkill`（0x30）、冷却、`MuteRemain`、`CastTimes`、`m_Input`（0x68）。
`Cur` 是链的实例字段，**跨链不共享** —— 所以打平A 不会动链[2]的游标。这一点成立。

**但是**：抽取出来的表显示（`skillchain_graph.md`），
**`group=2` 的全部 19 行 `preSkillOrder` 都是空的** —— 布鲁诺的
`attackAEX / attackA / attackB / attackC` **全是"起手段"**。

按 §3.1 的判定式，"下一段"要求候选段的 `preSkillOrder` 包含当前段的 order；
group=2 全是空列表 ⇒ **技能链层面对布鲁诺 A/B/C 之间的顺序【没有任何约束】，
也给不出 A→B→C 的推进**。而且 `DoUpdateAndCheckInputSucc` 传进来的 `startOrder` 恒为 `-1`
（`findAndStartSkill_Imp` 原样透传，没有 `cur.Order+1` 这种推导 —— 已反汇编确认），
所以 `order >= startOrder` 这个下界在正常路径上是**失效的**。
⇒ 按技能键从站姿出发，兜底路径 `findStartingSkillMatchInputDir` 会稳定返回
**列表里第一个**满足条件的起手段，也就是 `attackAEX`（order 1）—— 每次都一样。

**那么 A→B→C 是谁驱动的？** 反汇编 `PlayerSkillChain::FindAndStartSkillFromMidByOrder`
（RVA 0x1bb1ac0，公开方法）的**调用点**只有两个，且都不是 C# 的每帧循环：

```
GamePlay_PlayerSkillChain_Wrap::M_FindAndStartSkillByOrder      ← PuerTS 的 JS 包装层
ActorJs_hz_HangAnimController.AnmAndTime::<initSpecFuncMap>b__12_4  ← 动画时间轴上的 spec func
```

**即：按指定 order 从链中间启动技能，是【JS 层 / 动画时间轴】驱动的，不是技能链自己推的。**
本项目的插件本来就有 JS 注入通道（`JsPatchManager` / `RuntimeLoader.ReadFile`），
所以这条线是**可读、可改**的 —— 但要先把那段 JS 挖出来。

**结论（最终版，已由运行时日志证实，见 §5.6）**：
- "跨组不重置进度"**成立**，机制是每链独立 `Cur`；
- "布鲁诺 1→2→3 靠 `preSkillOrder` 串起来"**是错的**（表里 group 2 全空）；
- **真正驱动它的是【动作层】的 `ActionMgr.CheckCanChangeToAction`** ——
  技能链把 group 2 的候选按数组顺序逐个拿去问"当前动作能不能接到你"，
  第一个被放行的胜出。所以顺序来自**动作的接招表**，与连段历史无关，
  这也正是"插一段平A 之后照样能接布鲁诺 2/3"的原因。

> 这一条正好印证了本项目的教训：**没有数据支撑的机制解释，写得再顺也可能是错的。**

### 5.3 JS 层在哪里 —— ★ 它已经被转译成 C# 了

找 `FindAndStartSkillFromMidByOrder` 的调用点时看到 `ActorJs_hz_HangAnimController...`，
一度以为要挖 JS bundle。**不用**：这些类在 `JsPort` 命名空间下，
是**构建期把 JS 转译成 C#** 的结果，**类名/方法名/字符串全部保留**：

```
JsPort.ActorJs_hz          ↔ ES 玩家角色的 JS 逻辑
JsPort.ActorJs_hzbullet    ↔ esbullet(纹章) 的 JS 逻辑
JsPort.ActorJs_head_ray    ↔ 激光怪
每个类:  static Dictionary<string, ActorFuncsJs.ActorFuncJs> s_SpecFuncMap;
         private static void initSpecFuncMap()   ← 在这里把"名字 → lambda"注册进去
```
所以在 `dump/dump.cs` 里搜 `ActorJs_` 就能直接读游戏逻辑，**比挖 JS 省事得多**。
（这也解释了为什么在 `js_src/` 里 grep 这些名字一无所获 —— 它们不在 JS 里了。）

### 5.4 ★ ES 角色逻辑的完整"脚本函数"词汇表（从 `es.ab` 抽的）

ActionLogicGroup 里一次脚本调用 = 相邻两个字符串 `{Function, Params}`。
把 ES 角色 ALG（`extracted/es_mono0.raw`）全扫一遍，函数名只有这些：

| 次数 | 函数 | 作用 |
|---|---|---|
| **795** | `ChangeSkill` | ★ 改技能。`action:"xxx"`，可带 `trigger` / `CheckOrder:1` / `effect` / `buff` |
| 75 | `SetActionCD` | 给动作设 CD |
| 49 | `SkillSetCD` | **按链设 CD**：`chain:2, cd:0.8, trigger:56081` |
| 23 | `ChangeAction` | 直接换动作（绕过技能层） |
| 16 | `CreateBullet` | 生成弹幕 |
| 13 | `BulletClearTarget` | |
| 10 | `CreateBulletIfTriggerChange` | |
| 10 | `DeleteBulletByTag` | |
| 8 | **`SkillChainReset`** | **重置某条链**：`chain:2, cd:0` |
| 4 | `BulletAction` | |

`ChangeSkill` 的参数形态（实测）：
```
ChangeSkill(action:"attackA", trigger:56071)                       ← 来自 hold / holdEX / dashend
ChangeSkill(action:"attackA_Air", trigger:56251, CheckOrder:1)      ← 来自 AttackUp / AttackUp2
ChangeSkill(action:"fallupd", trigger:56251, trigger2:56131, effect:..., effpos_x:.., effpos_y:..)
```

**★ 一个直接可用的发现：`hold` / `holdEX` / `dashend` 会 `ChangeSkill(action:"attackA", trigger:56071)`**
—— 也就是**纹章解放可以接出布鲁诺1**。这是脚本层写死的边，不是技能表里的。

### 5.5 布鲁诺地面 1→2→3：脚本层被**排除**，指向动作层

穷举验证（都是在 `es_mono0.raw` 原始字节上数出来的）：

```
action:"attackB"   出现 0 次
action:"attackC"   出现 0 次
action:"attackA"   出现 3 次   (全部来自 hold / holdEX / dashend)
attackB / attackC  共 43 次 —— 全部是 attackB_Air / attackC_Air（空中版）
```

**⇒ 地面布鲁诺的 A→B→C 没有任何脚本调用在推。** 脚本层这条路可以划掉了。

同时，在动作数据里发现**动作名列表**这种结构（同一段里连续出现）：
```
@359572  attackA_AirEX | attackA_Air | attackAEX | attackA | ...
```
出现在多处（367728 / 375176 / 377792 / 383952 / …），
形态就是**「某动作可被哪些动作接替」的名字表** —— 这正是 `GameActionLogic` 的
`ActionSwitchs`(接招窗口, 0x110) / `Interrupt`(0x118) 那一类字段。

**因此当前最有力的假设是：地面布鲁诺 A→B→C 在【动作层】用接招窗口串起来，
技能层只负责「按技能键 → attackAEX」。** 这与 §5.2 的观察吻合：
技能表里 A/B/C 全是起手段、没有顺序信息。

### 5.6 ★★★ 已由运行时日志证实：闸门就是 `ActionMgr.CheckCanChangeToAction`

2026-10-02 训练场实测（日志原文，`ActionLimitTrace` 只在**被拒**时打印）：

```
当前动作 = attackAEX:
   [段数限制] 拒绝切换 -> attackAEX
   [段数限制] 拒绝切换 -> attackA
   [连段模组:段闸门] SkillChangePreCall -> True   Action="attackB" Order=3    ← 选中
   [连段模组:按Order找] startOrder=-1 -> True   Cur@0("attackAEX") → Cur@2

当前动作 = attackB:
   [段数限制] 拒绝切换 -> attackAEX
   [段数限制] 拒绝切换 -> attackA
   [段数限制] 拒绝切换 -> attackB
   [连段模组:段闸门] SkillChangePreCall -> True   Action="attackC" Order=4    ← 选中
   [连段模组:按Order找] startOrder=-1 -> True   Cur@2("attackB") → Cur@3
```

`Cur` 的下标是 **0 → 2 → 3**（跳过了下标 1 的 `attackA`）—— 与 §5.2 的推断完全一致。

**机制定案：**

```
findStartingSkillMatchInputDir(startOrder = -1)
  按 SkillList 的【数组顺序】逐个试候选:
    attackAEX(=index0) → attackA(=index1) → attackB(=index2) → attackC(=index3) → …
  每个候选过一遍过滤, 其中【决定性的一道】是
    ActionMgr.CheckCanChangeToAction(候选的 SuccessAction)   ← 动作层的"我现在能接到谁"
  第一个被动作层放行的候选胜出。
```

- 当前动作是 `attackAEX` 时：它自己 ✗、`attackA` ✗、**`attackB` ✓** → 所以 Cur 从 0 跳到 2。
- 当前动作是 `attackB` 时：`attackAEX` ✗、`attackA` ✗、`attackB` ✗、**`attackC` ✓**。

**这就是"为什么插一段平A 之后还能接着出布鲁诺"的答案：**
"下一个布鲁诺是哪一段"**完全不看连段历史**，只由**"当前动作能接到哪些动作"**这张表决定。
中间打平A 只是把"当前动作"换成了平A动作；再按技能键时，动作层从那个动作出发放行哪个布鲁诺，就出哪个。
`preSkillOrder` 在 group 2 里全是空的，本来就给不出顺序 —— 顺序来自动作层。

**副作用/待办**：`attackA`（槽 2.2）在这条路径上**从没被选中过**（两次都被动作层拒了）。
要么它有别的触发条件，要么它在当前潜能配置下确实不可达 —— 需要再测。

> 原本打算用 `[动作结构]` 转储（`NextAction` / `ActionSwitchs` / `Interrupt`）
> 来看这张表，结果**运行时日志直接给出了答案**，不用静态推断。
> `[动作结构]` 仍然有用 —— 它能给出**接招窗口的时间区间**（什么时刻允许接谁），
> 那对"调手感"是需要的，但对"机制是什么"已经不需要了。

---

## 6. 前摇 / 后摇 / 输入窗口 —— 三个字段的分工

| 字段 | 语义 | 消费点（反汇编） |
|---|---|---|
| `actdurStrict` | **硬地板**：`Cur.TimeEllaps < 它` → 那一帧直接 `return false`，不进推移逻辑 | `DoUpdateAndCheckInputSucc` |
| `preinputtime` | ★ **按键有效期**：`now − 按下时刻 ≤ 它` 才接受。**不是**"动作尾部窗口" | `findAndStartSkill_Imp`（详见 §7） |
| `timeout` | 超时：`CurSkill.IsTimeOut()`，超时后当前段作废 | `PlayerSkillChainCurSkill.IsTimeOut` |

⚠ 旧笔记里"`0` 不是一按就断"仍成立，但**表述要改**（两个 `0` 都反直觉）：
- `actdurStrict = 0` → **没有硬地板**（那一帧就会进推移逻辑）
- `preinputtime = 0` → **回落到游戏内置的默认窗口**，不是"没有窗口"

两者都不是"一按就断"，也都不是"窗口只剩一瞬"。

**攻击段 vs 后摇** 的原生分界不在这些字段上，而在 `GameActionLogic.HitDataList`
（攻击判定时间段列表）—— 那是动作层的，与技能链层正交。

---

## 7. ★★★ `preinputtime` 到底是什么（已定案，且**推翻了旧结论**）

`findAndStartSkill_Imp`  RVA 0x1bb3320，非长按分支：

```
0x181bb3569  cmp  byte ptr [r14+0x58], 0      ; InputCmdState.PressInputSatisfied
0x181bb356e  jne  → 拒绝                       ; 按过一次就不再接受
0x181bb3583  call get_Preinputtime
0x181bb35b2  Preinputtime <= 0 ?
0x181bb35e1     → 改用【静态默认窗口】(static_fields+0x68)
0x181bb3615  r9  = [r14+0x38]                  ; LastPressTimeStamp（按下时刻）
0x181bb3628  age = IInput.GetTimeEllaps(LastPressTimeStamp)
0x181bb364e  if (age > window) → 拒绝
```

**判定式：`接受 ⟺ (now − 按下时刻) ≤ Preinputtime`。**

⚠ **旧笔记写的"只有落在『动作结束前 N 秒』内的输入才被采纳"是错的。**
它不是"动作尾部的窗口"，而是**按键本身的有效期**，从**按下的瞬间**起算。

**三个可直接用的推论：**

1. **"按快接不上"的根因**：这个判定在 `ActdurStrict` 地板**之后**才跑。
   你按得太早 → 地板开启时 `now − 按下时刻` 已经超过 `Preinputtime` → 按键**已过期** → 丢弃。
   能"早到多早"= `地板开启时刻 − Preinputtime`。
2. **想改善手感是调大 `Preinputtime`**（按键活得更久），而不是动 `ActdurStrict`。
   之前把它当"尾部窗口"来理解，怎么调都对不上，就是因为模型本身错了。
3. **`Preinputtime = 0` ≠ "没有窗口"** —— 它会**回落到游戏内置的默认窗口**。
   `ActdurStrict = 0` 同理 ≠ "一按就断"，而是"没有硬地板"。
   **两个 `0` 的真实含义都和直觉相反。** 之前照抄原生 `0` 值调手感调不出来，根因在此。
4. **松手不影响**：`LastPressTimeStamp` 在按下时写、松手**不清**。
   按一下松手，这次按键仍然在 `Preinputtime` 秒内保持有效。

**给连段模组的直接建议**：`PreInputSeconds` 这个配置项现在的语义清楚了 ——
它是"按键有效期（秒）"。要"早按也能接上"就调大；要"必须卡在尾巴上按"就调小。
`-1`（不修改、沿用原生）在布鲁诺那种原生 `0`（= 默认窗口）的情况下，
用户看到的实际窗口是游戏内置默认值，不是"一瞬间"。

---

## 8. 对连段模组的直接影响（可执行结论）

1. **克隆段的 `preSkillOrder` 必须是"上一段的 order"** —— 已修，机制已确证。
2. **第 0 段必须让 `preSkillOrder` 为空**，否则它不再是合法起手段，
   一旦链被超时/清空，按普攻键就回不到序列开头。
   ⚠ 现有实现是"照抄平A模板的列表"，而 attack1 恰好是起手段（空列表）所以侥幸正确；
   如果哪天换了模板段，这条会静默失效。
3. **`actdurStrict` 与 `preinputtime` 要分开调**：
   前者决定"这段最短必须播多久"，后者决定"离结尾还有多远才收输入"。
   之前"照抄原生值 → 窗口只剩一瞬"的现象，根因在后者。
4. **链的进度不会因为你打了别的组的招而丢失** —— 所以混合连段不需要额外的状态管理，
   只要 `preSkillOrder` 图连对了就行。
