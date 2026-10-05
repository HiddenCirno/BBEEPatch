# 使用手册：存档编辑 + JS 热补丁

两套手段，按需要选：

| 需求 | 手段 | 是否改游戏文件 |
|---|---|---|
| 改数据体分值 / 传承技能索引 | 存档编辑器（Python） | 否，只改存档 |
| 改游戏逻辑（商店、潜能、房间…） | BepInEx JS 补丁 | 否，运行时改 |
| 大改 JS 逻辑 | JS 整文件覆盖 | 否，运行时改 |

---

## 一、存档编辑器 `tools/save_editor.py`

### 前置
存档会被 Steam 云同步。**先断网或确认云同步关闭**，再操作。
脚本首次写入会自动生成 `<存档>.bak`。

### 命令

```bash
cd <游戏目录>/_modding

# 存档整体结构
uv run --with lz4 --with UnityPy python tools/save_editor.py tree 1

# 列出所有数据体
uv run --with lz4 --with UnityPy python tools/save_editor.py list 1

# 某个数据体的全部字段
uv run --with lz4 --with UnityPy python tools/save_editor.py show 1 <uid>

# 某个角色能传承哪些技能
uv run --with lz4 --with UnityPy python tools/save_editor.py skills 103401

# 修改数据体分值
uv run --with lz4 --with UnityPy python tools/save_editor.py set-score 1 <uid> 99999

# 修改数据体传承技能（技能 id 必须在该角色的可传承表里）
uv run --with lz4 --with UnityPy python tools/save_editor.py set-inherit 1 <uid> <技能id> [等级]
```

参数 `1` 是存档槽位。

### 数据体字段含义（STFesActor）

| 字段 | 名称 | 说明 |
|---|---|---|
| 1 | uid | 数据体唯一 id |
| 2 | id | 角色 id |
| 6 | baseAttrs | 基础属性 |
| 8 | blesses | 携带的增益 |
| 14 | activeBlesses | 生效增益 |
| 15 | potentials | 潜能 |
| 16 | score | 分值（决定传承解锁档位） |
| 17 | talents | 天赋 |
| 25 / 30 | inheritBless / inheritBlesses | 传承增益 |
| 26 | skills | 主动技能 |
| 29 | inheritBlessOptions | 传承增益候选 |
| **36** | **inheritSkillIndex** | **传承技能索引** |
| **37** | **inheritSkillLevel** | **传承技能等级** |

### 角色 id 对照

```
100101 拉格纳   100201 琴恩     100301 诺爱儿   100401 雷其儿
100501 桃卡卡   101101 白面     101401 哈札马   101801 白金
102201 芭烈特   102501 九重     102801 RM       102901 琥珀响
103101 黑铁直人 103401 ES       103501 枣麻衣   103701 艾希
103801 细胞人
```
每个角色的可传承技能只有 2 个（用 `skills <角色id>` 查）。
**想传承表里没有的技能 → 走下面的 JS 注入补丁。**

---

## 二、BepInEx JS 热补丁

### 原理

游戏用 PuerTS 加载 JS，加载器是 `Js.RuntimeLoader.ReadFile(string, out string)`。
插件在这之后、V8 编译之前插入，对 JS 源码做文本替换 —— 这就是 **JIT 层修改**，
不碰任何资源包，改错了删掉规则即可。

### 安装状态
已装好：`BepInEx 6.0.0-be.788 (IL2CPP)`，插件已放到 `BepInEx/plugins/BlazblueJsPatch.dll`。

**首次启动游戏会比较慢**（要生成 `BepInEx/interop/`），属正常。

### 目录

```
BepInEx/config/BlazblueJsPatch/
├── patches.txt     规则文件（find/replace）
├── sources/        游戏加载的每个 JS 原样转储（自动生成，用于查看/取用）
└── overrides/      整文件覆盖 —— 放进去就生效，优先级高于规则
```

### 用法 A：规则替换
编辑 `patches.txt`，重启游戏。日志在 `BepInEx/LogOutput.log`，
搜 `[PATCH]` 看命中情况，`[MISS ]` 表示没匹配上（游戏可能更新了）。

格式：
```
[rule]
id       = 随便起个名
module   = 模块路径子串(忽略大小写, 如 battleexplorebuybless)
find     = 要被替换的原文(必须与 JS 里逐字符一致)
replace  = 替换成什么
required = true        # true 时没匹配上会报错, 用于确认补丁生效
enabled  = true
```

### 用法 B：整文件覆盖（推荐做复杂改动）
1. 启动一次游戏，`sources/` 下会出现全部 JS 的干净副本
2. 找到目标文件，复制到 `overrides/` 下**保持同样的相对路径**
3. 随便改，重启游戏生效

### 已配好的补丁（`patches.txt` 里，默认启用）

| id | 效果 |
|---|---|
| `shop-potential-free-display` | 潜能商店单价显示 1 → 0，可买次数 → int32 上限 |
| `shop-potential-no-cost` | 服务端真正扣潜能点的地方去掉扣费 |
| `potential-refresh-unlimited` | 去掉潜能刷新次数上限 |
| `shop-goods-no-balance-check` | 商店货品余额校验恒通过 |
| `shop-goods-free` | 商店货品实际扣费 → 0 |
| `shop-goods-unlimited` | 商店货品永不售罄 |
| `shop-goods-cost-index` | 配套：买多了取模防止越界 |
| `inherit-skill-inject` | **默认关闭**，见下 |

### 传承技能注入（`inherit-skill-inject`）

把任意技能 id 塞进**所有角色**的可传承技能表，UI 显示 / 存档校验 / 实际发技能
全都读同一个表，所以改这一处就全线生效。

1. 打开 `patches.txt`，找到 `inherit-skill-inject`
2. 把 replace 里的 `[1037011,1037012]` 改成你要的技能 id 列表
3. `enabled = false` 改成 `true`
4. 重启游戏

---

## 三点五、数据体传承 / 潜能（实际生效的字段）

**关键结论**：传承界面里看到的「天赋/潜能」来自数据体的 **`inheritBlesses`（field 30）**，
不是 `inheritSkills`（那个是「传承技能」，每个角色只有 2 个，完全另一套东西）。

```
STFesActor {
  8  blesses         数据体自带的潜能
 30  inheritBlesses  传承给别人的潜能   ← 就是它
 17  talents         天赋
 36/37 inheritSkillIndex/Level  传承「技能」的索引和等级
}
STBless { 1:id, 2:quality, 3:active, 4:timestamp, 5:source, 6:子消息(f3/f4=quality) }
```

**品质对照：1=白 2=蓝 3=紫 4=金 5=红**

### 名字 ↔ id 反查

本地化的键格式是 `前缀_<id>`，哈希函数就是 `noah_codec.calculate_hash`。
常用前缀：`TriggerName_`（天赋/潜能名）、`InheritSkill_`（传承技能名）、
`ActorActionName_`、`BlessQuality_`、`PotentialName_`。

```bash
# 已经是「名字 -> 词条」的方向，可用来确认
uv run --with lz4 --with UnityPy python tools/save_editor.py locale-name TriggerName_34161
```

已确认的映射（本次目标）：

| 名字 | key | bless id | 触发 id |
|---|---|---|---|
| 冲刺影子 | `TriggerName_34161` | **3416** | 34161~34164 (q1~q4) |
| 影子反冲 | `TriggerName_34491` | **3449** | 34491~34494 |
| 多重影子 | — | 3454 | — |
| 召唤冰刺 | — | 2225 | — |

### 命令

```bash
# 列出数据体携带/传承的潜能（带中文名和品质）
uv run --with lz4 --with UnityPy python tools/save_editor.py blesses 1 <uid>

# 修改「传承」的潜能品质（不存在则新增）
uv run --with lz4 --with UnityPy python tools/save_editor.py set-ibless 1 <uid> <blessId> <quality>

# 修改数据体「自带」的潜能
uv run --with lz4 --with UnityPy python tools/save_editor.py set-bless  1 <uid> <blessId> <quality>
```

> 改嵌套消息必须**逐层向上重新序列化**（`apply_edit`），否则父层还是旧字节，存档会被写坏。
> 本工具已处理；写盘前会自动做一次 `ser(parse(x)) == x` 的往返校验思维可参考。

---

## 三点六、开局即满潜

**机制**：一局开始时，本局使用的 FesActor **不是数据体本身**，而是新生成的：

```js
// server/model/breeddungeon.js  initializeFesActor()
var n = Actor_1.FesActorUtils.Generate(ModelPlayer.Instance.NextFesUid(), e);  // e = 角色
this.actorInfo.fesActor = n;
// 选中的数据体随后作为 factor 通过 InheritAttrFromFactorActor / inheritBlesses 继承进来
```

而 `FesActorUtils.Generate` 里**本来就遍历了该角色的全部潜能**，只是加了个门槛：

```js
return Xlsx.BaseActorPotentialConf.All
    .filter(t => t.actorId == e.id)
    .forEach(t => { t.init && AddPotential(s, new STPotential({id: t.id})) }),   // ← t.init
    s
```

`t.init` 是配置表里「初始潜能」的开关。**去掉这个条件 → 该角色所有潜能开局到手。**

补丁（`patches.txt`，默认启用）：

```
id      = all-potentials-at-start
module  = Server/Module/Actor
find    = .filter(t=>t.actorId==e.id).forEach(t=>{t.init&&AddPotential(s,new P.STPotential({id:t.id}))})
replace = .filter(t=>t.actorId==e.id).forEach(t=>{AddPotential(s,new P.STPotential({id:t.id}))})
```

只影响 `Generate()`（开新一局），选人界面和训练模式用的是 `ActorUtil.GenFesActorFromSeed`，不受影响。

每个角色约有 15~18 个潜能，所以开局会直接拿到全部。

---

## 三点七、DLC 解锁（配色 / 皮肤）

**结论：纯粹是检测开关，内容本地就有，不需要额外下载。**

全项目只有一条判据：

```js
// server/model/player.js  CheckAvatarSkinRecordWithDLC()
Util.GetSingleton(csharp.SdkManager).IsDlcInstalled(s.steamDlcId)
    ? this.AddResource(..., new STResource({type:s.type, id:s.id, count:1}))   // 解锁
    : (s.type==EResourceType.Avatar && r.push(s.id), s.type==EResourceType.Skin && a.push(s.id))  // 未拥有
```

调用点共 3 处：`server/model/player.js`（配色/皮肤）、`core/util.js`（通用解锁判定）、
`core/actorutil.js`（角色页解锁 `FesActorPageUnlockType.Dlc`）。

**实现方式**：在 C# 侧直接 Hook `SdkManager.IsDlcInstalled(ulong)` 返回恒真
（见 `_modding/plugin/BlazblueJsPatch/Patcher.cs`），一处覆盖全部调用点，
不需要在 `patches.txt` 里配规则。

C# 签名（dump.cs）：
```
// RVA: 0x14FCE90  Slot: 39
public bool IsDlcInstalled(ulong dlcId) { }
```

---

## 三点八、配色/皮肤解锁（按条件类型区分）

**拥有判定的唯一入口**（被 12 个 UI 文件调用）：

```js
// gameplay/avatarutil.js
u.IsSkinUnlocked = function(e) { return null == e || null != DataBinding.DB.Skin.GetData(e) }
```

即：**皮肤 id 在 `ModelPlayer.skinPack.skins` 里就算已拥有。**

解锁方式由 `SkinUnlockConf.unlockType` 决定：

| 值 | 含义 | 说明 |
|---|---|---|
| 1 | `Resource` | **兑换型**（花资源换） |
| 4 | `Dlc` | 已由 C# Hook 处理 |
| 5 | `FightReward` | **bossRush 通关** |
| 10 | `AchievementTaskReward` | 成就 |
| 23 | `DeadCellResource` | 细胞人资源 |
| 7 | `BaseActorUnlock` | 角色解锁 |
| 34 | `CoopModeUnlock` | 联机 |

兑换成本在 `ResourceConvertConf.Get(SkinUnlockConf.convertRelationId).cost[0]`。

### 查看/解锁（存档命令）

```bash
# 按解锁类型统计 + 列出未拥有的
uv run --with lz4 --with UnityPy python tools/save_editor.py skins 1

# 解锁：只放行「兑换型」（bossRush 仍锁着）
uv run --with lz4 --with UnityPy python tools/save_editor.py unlock-skins 1 resource

# 全部解锁 / 只解锁某个 unlockType
uv run --with lz4 --with UnityPy python tools/save_editor.py unlock-skins 1 all
uv run --with lz4 --with UnityPy python tools/save_editor.py unlock-skins 1 10
```

### ⚠️ 为什么必须改存档而不是 hook UI

装备皮肤走的是游戏自己的 REST 层（`RestRequestLocal`，**本地回环，不是真联网**）：

```js
// server/views/avatar.js
class HandleApiSkinEdit {
    post(t) {
        for (...) if (0 == ModelPlayer.Instance.GetSkinCount(a))
            return MakeResponseScrollOrigin(EErrorCode.ClientParamInvalid, `skin ${a} not own`)
        return ModelPlayer.Instance.SetSkins(t.actorId, t.skins, t.isSecondPlayer)
    }
}
// server/model/player.js
GetSkinCount(t) { return 0 <= this.skinPack?.skins?.findIndex(e => e == t) ? 1 : 0 }
```

UI 判定 (`AvatarUtil.IsSkinUnlocked`) 和服务端校验 (`HandleApiSkinEdit`) 读的是**同一份数据**
(`skinPack.skins`)。所以 hook UI 只能骗过界面，一装备就报 `skin xxx not own`。
**正确做法是往存档里真正发放**（`unlock-skins`）。

`patches.txt` 里的 `unlock-exchange-skins` 因此**默认停用**（默认改存档即可，保留备用）。

---

## 三点九、冲刺无敌（方案 B：Hook 无敌等级）

### 帧率结论（重要）

**本作不跑在 Unity 物理帧上。** 战斗是自研 lockstep，逻辑与渲染分离：

```
LockStepManager.LogicUpdate(Fp dt)      ← 战斗逻辑，固定步长
LockStepManager.RenderUpdate(float dt)  ← 渲染，跟帧率无关
```

`LockStepManager.InitFps()` 里写死（反汇编 RVA `0x153E6B0`）：

```asm
mov dword ptr [rbx+0xe8], 0x1e   ; m_driveFps = 30
mov dword ptr [rbx+0xf0], 1      ; m_driveToLogicFactor = 1
mov dword ptr [rbx+0xec], 0x1e   ; m_logicFps = 30   ← 逻辑帧率
```

所以 **逻辑帧率 = 30 fps**：
- 「10 帧」= 10 个逻辑帧 = **0.333 秒**
- **0.2 秒 = 6 个逻辑帧**

`LockStepManager.m_logicFrame`（public uint）是当前逻辑帧号，可以精确计数。

### 机制

冲刺是「动作」，动作数据 `GameActionLogic` 上挂着按**时间区间**生效的判定：

```csharp
public class ActionInvinciple : ActionTimeRangeTrigerer {   // TypeDefIndex 3497
    public TimeRange TimeRange;
    public ActionLogicConditionGroup ConditionGroup;
    public ActionLogicParamInt InvincipalLvlEx;   // 无敌等级
    public override Fp TimeStart { get; set; }
    public override Fp TimeEnd   { get; set; }
}
// GameActionLogic 里: [ActionHidableList("无敌",...)] public List<ActionInvinciple> Invincibility; // 0x208
```

每帧算出当前等级 → `ActionMgrAttr.Invincipal`，命中结算时经 `GetInvincipalLevel()` 取总等级，
与攻击方的 `ActionHitData.InvincipalBreak`（破无敌）比较：攻不破 → `BattleActorBehitType.Invincipal(3)` → 打不中。

数据位置：`actor/logicdata/<角色>.ab`（MonoScript `ActionLogicGroup`），ES = `es.ab`。

### 实现

Hook `GamePlay.PlayerObj.GetInvincipalLevel()`（RVA `0x1BA7560`，Slot 27）+ 基类 `ActorBase` 版本：

- 当前动作名 `ActionMgr.CurrentActionName` 含 `dash` → 抬到 `Level`
- 动作结束后 `TailSeconds` 秒内 → 继续抬

代码：`_modding/plugin/BlazblueJsPatch/DashInvincible.cs`

### ⚠ 事故复盘：第一版把全场受击判定打没了

第一版有三个错误叠在一起，症状是 **ES 打不动敌人、打不碎物体、敌人也打不到 ES**
（后来查明白了：本作命中结算时只要一方无敌就整个跳过，所以一边永久无敌 = 全都打不中）：

| # | 错误 | 后果 |
|---|---|---|
| 1 | `_seenDash` / `_lastDashFrame` 是 **static**，所有 Actor 共用 | 敌人也冲刺 → 任意 Actor 一把全局标志翻成 true |
| 2 | 帧号用 `LockStepManager.Instance?.m_logicFrame`，**恒为 0**（`Instance` 在泛型基类 `SingletonBehaviour<T>` 上，取不到实例） | 尾巴判定 `(0-0) <= 6` **恒真** → 冲刺一次后永久无敌 |
| 3 | 补丁挂在 `ActorBase` 上，**没限定阵营** | 敌人也吃到无敌 |

三条叠加 = 全场永久无敌 = 所有命中不结算。

**现在的三道保险**（改这些代码时务必保留）：

1. 状态按 Actor 分桶（`Dictionary<IntPtr,float>`），不再串台
2. 时间源换 `UnityEngine.Time.time`，且**时钟无效时一律拒绝发放无敌**（fail-closed），
   绝不能让"取不到时间"退化成"时间刚好在窗口内"
3. 默认只对 `BattleBase.Cur.PlayerSelf` 生效；身份判断失败时同样**失败关闭**

> 这条经验对 IL2CPP 逆向通用：**不要依赖某个具体类型/字段一定能解析到**。
> 取不到就 fail-closed，别让默认值恰好落进"生效"分支。

### ⚠ 与「极限闪避」互斥

**冲刺全程无敌 = 命中结算整个被跳过 = 极限闪避的判定帧根本没机会被评估。**
想测/用极限闪避，必须先把 `[冲刺无敌] Enabled` 设成 `false`。

这不是 bug 而是机制：本作在命中结算时只要一方无敌就整个跳过，
而"极限闪避"恰恰是**被攻击打中那一下**才算数的。

---

## 三点十二、极限闪避（完美闪避）

### 术语

游戏里叫**「极限闪避」**，本地化对应：

```
极限闪避时，在周身生成飞刃…                       TriggerDesc_32721/32722/32724/32725
极限闪避成功时，使攻击你的敌人进入破甲状态
每累计触发 N 次极限闪避后，最终伤害提升…
无敌闪避状态下，无视敌人攻击的同时，额外触发极限闪避效果     ← 游戏自带的思路
禁用极限闪避等令动作变慢的效果                       ← MiscSettings.DisableSlowMotion
```

**时缓** = `BattleBase.Cur.BattleTimeControl.SetSlowMotion(duration, startScale, endScale, ignoreSetting)`。

### 「若闪避成功」潜能的闸门

`JsPort.ActorFuncUtils.CheckDodge(PlayerObj, ActorBase, ActorHitResult)`，RVA `0x1665150`。

6 个调用点：1 个 PuerTS 包装 + **5 个 `BuffFuncsJs` 的 lambda**
（`b__38_404/418/550/552/57`）—— 即「若闪避成功 → 生成 XX」那批潜能。

⚠ **不带那些潜能，就根本没人调 `CheckDodge`**，钩子会一次都不触发（实测日志确认）。

### 时缓的调用点找不到入口

`SetSlowMotion` 有 9 个调用点、`ActorFuncUtils.ShowSlow(PlayerObj)` 有 6 个，
**全部是 JS 封装和 BuffFuncsJs 的 lambda，没有一个是核心战斗路径** ——
说明时缓是 **buff 驱动**的，静态调用图到这儿就断了。

所以改用运行时抓：`SlowMotionTrace.cs` 给 `SetSlowMotion` / `ShowSlow` 挂 Postfix，
打印**参数 + 托管调用栈**（自动过滤 Harmony/BepInEx/System 帧）。
触发一次原版极限闪避，日志里 `[时缓]` 就会直接指出是哪一层在放。

### 可调参数

`BepInEx/config/BlazblueJsPatch.cfg`（首次运行生成）：

| 键 | 默认 | 说明 |
|---|---|---|
| `Enabled` | true | 开关 |
| `ActionKeyword` | `dash` | 动作名关键字。含此串即算冲刺动作<br>`es_dash` / `es_dashAir` / `es_dashend` / `es_dashup` / `es_dashweak` / `es_Dash_timeline` 全覆盖 |
| `TailSeconds` | 0.2 | 冲刺结束后仍无敌的时长(秒) |
| `Level` | 999 | 抬到的无敌等级 |
| `PlayerOnly` | true | **别关**。关掉会让敌人也无敌，见上面的事故复盘 |

改完重启游戏生效。日志里搜 `[冲刺无敌]` 能看到进入冲刺动作的记录。

---

## 三点十、IMGUI 兼容层（让 ConfigurationManager 这类插件能跑）

### 问题现象

装了 `BepInEx.ConfigurationManager` 后，插件能加载，但每帧刷屏：

```
System.NotSupportedException: Method unstripping failed
   at ConfigurationManager.Utilities.ImguiUtils.DrawBackground(...) ImguiUtils.cs:line 53
```

第 53 行是 `GUI.DrawTexture(...)`。

### 真正的原因（和我一开始的判断不同）

本作关掉了引擎代码，UnityLinker 做了**方法级 strip**。对比两边的 API 表：

| | `UnityEngine.GUI` | `UnityEngine.GUILayout` |
|---|---|---|
| `dump.cs`（= global-metadata.dat） | 66 个成员，**没有** `DrawTexture`/`DragWindow`/`Window` | 36 个，**没有** `Window`/`TextField`/`Toolbar` |
| `BepInEx/interop/UnityEngine.IMGUIModule.dll` | **240** 个，全都有 | **214** 个，全都有 |

也就是说：**BepInEx 的 interop 程序集由 Cpp2IL 生成，它把引擎 API 表面补全了**；
缺的那些方法体是一根共享桩 —— 整个程序集里 `"Method unstripping failed"` 这个字符串
只出现 **1 次**，异常就是从那儿抛的。

**关键**：这些桩**有真实 IL 方法体**（RVA 非 0），所以 **Harmony 可以直接顶掉**。

> 顺带否定一条路：从 Mono 版 Unity（`I:\TKF` / `I:\TKFCoop` 那种）拉原版
> `UnityEngine.IMGUIModule.dll` 外接引用 —— **运行时没用**。Mono 版里那些方法是
> `extern`/InternalCall，实现要么在 `UnityPlayer.dll` 要么在 Mono 运行时；IL2CPP 下
> 每次托管→原生调用都必须先有元数据里的 `Il2CppMethodDefinition` 才能拿到方法指针。
> 它只能当**编译期符号**，唯一价值是用 ILSpy 反编译出来当"参考实现"移植。

### 实现

`plugin/BlazblueJsPatch/ImguiCompat.cs`。

用 `tools/refs_vs_meta.py` 算出 ConfigurationManager 引用了 129 个 Unity 成员，
**其中只有 13 个是桩**：

| 方法 | 处理 |
|---|---|
| `GUI.DragWindow` / `FocusControl` / `FocusWindow` / `SetNextControlName` / `GUIStyle.set_m_Ptr` | 空实现（我们没有多窗口管理器） |
| `GUI.get_tooltip` | 返回 `""` |
| `GUI.DrawTexture` | 转发给 **`Graphics.DrawTexture`**（CoreModule，`[FreeFunction]`，活着，RVA `0x409EF30`） |
| `GUILayout.Window` | `GUILayout.BeginArea` + `GUI.Box` 背景 + 自己用 `Event.current` 算拖动 |
| `GUILayout.TextField` / `TextArea` | `GUILayoutUtility.GetRect` 占位 + **`GUI.TextField`**（它活着） |
| `GUILayout.MaxWidth` | 反射造 `GUILayoutOption(Type.maxWidth, v)`，失败退回 `GUILayout.Width` |
| `GUI.SelectionGrid` / `GUILayout.SelectionGrid` | 一排 `Button` 凑出来 |
| `GUIStyle.op_Implicit` | 退回 `GUI.skin.label` |

要点：Harmony 的 `__args`（`object[]`）对任意签名都可用，所以**一个 Prefix 能吃同名的一整组重载**，
不用逐个枚举精确签名，只需按返回类型分派（void / string / int / Rect / GUIStyle / GUILayoutOption）。

Prefix 里 **catch 之后一律 `return false`** —— 放行就会回到那根桩，又会抛 `NotSupportedException`。

### 排查工具

```bash
cd <游戏目录>/_modding

# 列出某个 .NET 程序集里的类型和方法（不受 #Strings 后缀合并影响）
uv run --with dnfile python tools/dotnet_types.py <dll> [名字过滤]

# 算出「插件引用了、但元数据里没有」= 必须 shim 的那批方法
uv run --with dnfile python tools/refs_vs_meta.py <插件dll>
```

以后遇到别的插件同样报 `Method unstripping failed`，跑第二个命令就能拿到要补的清单。

### 开关

兼容层在 `Patcher.Apply()` 里无条件安装，日志里看这两行确认：

```
[IMGUI] 已顶掉 N 个引擎桩方法 (覆盖 M 个名字)
```

如果 CM 又出问题，最快的止损是把它移出 `plugins/`（BepInEx 不递归加载子目录）：
`BepInEx/plugins/ConfigurationManager/` 就是为此准备的。

---

## 三点十一、用游戏原版控件做配置面板（JS 层，推荐路线）

IMGUI 那条路（见三点十）能修但一直在跟 strip 缠斗。**用游戏自己的 UI 系统要干净得多**，
而且不用写一行 C# 界面代码。

### 关键发现：游戏自带一个调试面板，只是被藏起来了

`window/gamesettings.js`（游戏设置窗口）里：

```js
this.R.TabDebug.gameObject.SetActive(!1),
this.R.DebugSettingsWidget.gameObject.SetActive(!1),
this.R.TabPvp.gameObject.SetActive(!0),
```

旁边 `TabPvp` 是 `!0` —— 一对比就知道，**原来有个 Debug 标签页，被人为关掉了**。

标签页里是 `widget/debugsettingswidget.js` 的：

```js
const DebugConfigs=[
  {label:"锁血玩家", toggle:!0, click(e){...}, init(e){e.ToggleState=...}},
  {label:"自动战斗", battle:!0, toggle:!0, click(e){...}, init(e){...}},
  {label:"Spine更新频率", battle:!0, click(e){...}, init(e){e.Label.TextAuto("Spine跳帧"+...)}}
  ... 共 17 项
];
```

`DebugComponent` 对每一项 `Instantiate(this.R.Template.gameObject, this.R.GM)`，
生成 **按钮 + TextMeshPro 文字 + 勾选框（CheckMark）**。
这正是"原版控件池"—— 而且是**模板实例化**，不是自己拼的。

**所以「往配置面板加一项」= 往 `DebugConfigs` 数组里插一个对象。**
（纯 JS 文本改动，现有 `overrides/` / `patches.txt` 框架直接支持。）

数值型配置照抄 `"Spine更新频率"` 的范式：Label 显示当前值，点击循环切换。

### 窗口 API

```js
UIUtil.AcquireWindow(WindowClass)       // 创建或取回窗口实例
UIUtil.FindWindow(WindowClass)          // 查已存在的
UIUtil.RecycleWindow(WindowClass)       // 回收
UIUtil.AcquireWindowByName("Name")      // 按名字
UIUtil.RecycleWindowByName("Name")
```

底层是 `UI.UIManagerInstance.AcquireWindow(name)`（C# 侧 `NOAH.UI`，
按名字加载 prefab 再绑回 JS 类）。可复用的现成窗口：`window/` 下 **392 个**、
`widget/` 下 **127 个**。

### 官方 sideloader

`sideloader/` 下 91Act 自己留了一个按名字操作的入口：

```js
exports.SideLoader = {
    OpenWindow(e)  { return UIUtil.AcquireWindowByName(e) },
    CloseWindow(e) { UIUtil.RecycleWindowByName(e) },
    LoadScene(e)   { ... }
}
```

### 组装 CfgManager 的具体做法

| 步 | 做什么 |
|---|---|
| A | 补丁 `window/gamesettings.js`：两处 `SetActive(!1)` → `SetActive(!0)` |
| B | 补丁 `widget/debugsettingswidget.js`：在 `DebugConfigs` 数组里插入我们的项 |
| C | C# 侧 `ClassInjector.RegisterTypeInIl2Cpp<T>()` 注册一个小门面类，把 `ConfigEntry` 暴露成静态属性；JS 用 `CS.BlazblueJsPatch.Panel.Xxx` 读写，与 cfg 文件双向同步 |
| D | 把 JS 补丁规则改成读 C 里的 flag，即可**运行时开关**（比现在的"改文本 + 重启"强） |

### 待实测的点

1. **`TabDebug` 是否挂在 `TabList` 下面** —— `tabMap` 是遍历 `TabList` 子节点建的
   （`SetupWindow` 里的 `TraverseChildren`），如果 `TabDebug` 不在其中，标签页枚举不到它。
   这是唯一需要进游戏确认一次的地方。
2. `DebugConfigs` 的 `battle` 字段用于在 `SceneLogin` 场景下过滤，我们的项不要设 `battle`。
3. 从 JS 调 C# 需要类注册成功，失败时 PuerTS 会抛异常。

---

## 三点十三、特效 / 纹章的着色系统（分析结论 · 已实测修正）

> ⚠ 本节结论经过多轮实测修正。下面这版是**实测过的**，早期"材质染色是主力"的判断是错的。

### 实测结论

**攻击特效的颜色烘在特效 prefab 的粒子系统里，不是靠材质染色。**

证据（直接解析 `effect/prefab/role/es/es_ah_01.ab`）：

```
ParticleSystem.startColor.maxColor = (r=0.438, g=0.622, b=1.000)   ← 该特效的色调
ParticleSystem.ColorModule.enabled = True
    渐变: 白 → 白 → 黑(α=1) → 黑(α=1) → 白(α=0)
```

**结构层次**：

| 层 | 事实 |
|---|---|
| 特效 prefab | **每个角色一套**。ES 有 521 个（`effect/prefab/role/es/`） |
| 配色专属 | **只有 4 个配色**（`esskin_06/10/12/13`，各 ~105 个）做了整套 prefab 副本，其余配色共用 |
| 材质 | prefab 里**没有 Material 对象**，材质是跨 bundle 的**共享通用材质**（`effect/common/material/08_particles/*`、`effect/material/12_role/*`） |
| 颜色 | 烘在 prefab 的 `ParticleSystem` 参数（startColor / ColorModule 渐变）里 |

**推论**：材质是共享的，所以**不可能**靠改材质来分角色/分配色 —— 那会波及所有角色。
颜色差异来自 **per-角色 的 prefab**，少部分配色再叠加整套 prefab 替换。

### 实测排除掉的假设（都挂上了，但命中 0～1 次）

| 挂点 | 命中 | 结论 |
|---|---|---|
| `ActorMaterialOverride.TryGetOverride` | 有 | **只影响角色本体(Spine)**。改它 → 角色变洋红，特效不变 |
| `MaterialColors.Apply` | 有 | 同上，写的是角色材质 |
| `VFXMaterialMatchSkinColor.Restart` | **1** | 几乎不用，不是主路径 |
| `NOAH.VFX.MaterialTinter.Restart` | **0** | 完全没用上 |

### 想改特效颜色，正确的路子

必须在**特效生成时**改它实例上的粒子系统（不能改共享材质）：

- 挂点候选：`VFXEffectHub.DoStart()`（RVA `0x7EA7D0`）/ `Reactivate()`（RVA `0x7EC240`）—— 每个特效实例都会经过
- 改什么：`ParticleSystem.main.startColor` + `ColorOverLifetimeModule` 的渐变
- 「纹章」是同一套 prefab，一并生效

API 可用性已确认：`ParticleSystem.get_main` / `get_colorOverLifetime` / `GameObject.GetComponentsInChildren` 都**未被 strip**。

### 历史踩坑（挂 Harmony 补丁前必看）

1. **命名空间看串** —— `ActorMaterialOverride` 在 `GamePlay`，不是旁边那个 `ACERender`
2. **`AccessTools.Method` 遇重载抛 AmbiguousMatchException** —— 要自己按参数筛选
3. **`out` 参数的 `ParameterType` 是 ByRef**(`MaterialColors&`)，直接比较永远不相等
4. **Harmony 的 `__args` 对 `out` 参数不回写** —— 必须用强类型签名
5. **绝不挂"基类声明、派生类继承"的方法**（如 `ActorVisualBase.SetSkin`）—— 一调用就 `SEHException` 崩溃。
   HarmonyX 会当场警告 `You should only patch implemented methods...`，**看到这条警告必须停下来**

ES 的特效资源实测：`effect/prefab/role/es/` 共 **521** 个 prefab，另有 4 个配色专属子目录：

```
esskin_13  (105)   esskin_12  (105)   esskin_10  (103)   esskin_06  (103)
```

即 `es/es_atkair12_001.ab` 与 `esskin_06/es_atkair12_001.ab` 一一对应 —— **只有 06/10/12/13 这四个配色做了整套替换**，其余靠材质染色。

**没有独立的「纹章」类** —— 纹章就是普通特效 prefab，走同一套材质/着色系统。

### 核心类型

```csharp
// ACERender 命名空间
public struct SkinReplaceInfo {              // TypeDefIndex 3761
    string EffectSubPath;
    bool   EffectPathNeedReplace;            // 是否整条特效资源换掉
    MaterialColors MaterialOverride;         // 材质颜色组
    string SkinName;  int ActorId;
    string EffectCheckTagSelf;  bool EnableSkinEffect;
    const string EffectCheckTagReplaced = "skin_";
    static SkinReplaceInfo ResolveFrom(string skin, int actorId, ActorBase actorBase);
    string EffectSkinReplace(string effName);         // 按配色改写特效名
}

public class ActorMaterialOverride : ScriptableObject {   // TypeDefIndex 3627
    ActorColorConfig[]  _configs;            // 按 (actorId, skin) 查
    BulletColorConfig[] _bulletConfigs;      // 弹幕配色
    Dictionary<ValueTuple<int,string>, MaterialColors> _generalCache;
    // RVA 0x1ABB2E0
    bool TryGetOverride(int id, string skin, out MaterialColors val);
}

public class MaterialColors {                // TypeDefIndex 3623 —— 注意是 class(引用类型)
    string Skin;
    Color  Emission, EmissionX, EmissionY, EmissionZ, EmissionA;
    float  EmissionIntensity, AmbientScale;
    TextureOverride[] TextureOverrides;      // { string PropertyName; Texture2DRef TexRef; }
    // RVA 0x1AC6220
    void Apply(Material mat, object textureRetainer);
}
```

### 调用链

```
ActorVisualSpine.SetSkin(SkinReplaceInfo)      RVA 0x1AC1B70  (Slot 15)
PaletteFrameVisual.SetSkin(SkinReplaceInfo)    RVA 0x1AC7620  (Slot 15)
    -> ActorMaterialOverride.TryGetOverride(actorId, skin, out MaterialColors)
    -> MaterialColors.Apply(material, textureRetainer)
    -> SkeletonGraphicLoader.Init(asset, MaterialColors, ...)   RVA 0x1AE5BE0
```

### 想改颜色的三条挂点

| | 挂点 | 特点 |
|---|---|---|
| **A（推荐）** | `TryGetOverride` Postfix | `MaterialColors` 是**引用类型**且被 `_generalCache` 缓存 —— **改一次全局持久生效**，不用每帧改材质 |
| B | `MaterialColors.Apply` Postfix | 每次应用时改材质，最即时，但调用频繁 |
| C | `ActorVisualSpine.SetSkin` / `PaletteFrameVisual.SetSkin` | 拿得到 `SkinReplaceInfo`，能同时改**资源路径**和颜色，能力最大 |

⚠ `EmissionX/Y/Z/A` 是**四个通道色**（着色器按通道分色）。改的时候要成组改，只改 `Emission` 会出现"只换了一半"的割裂感。

⚠ 资产本身是 `actor/actormaterialoverride.ab`（ScriptableObject，IL2CPP bundle 无 typetree，直接改成本高）—— 运行时改更划算。

### 真正的颜色载体：`NOAH.VFX.MaterialTinter` 的着色插值器

> 这一节是整件事的关键。前面"改粒子 `startColor`"只解决了**一半**问题。

直接解析 prefab 的 `SerializeReference` 数据（`MaterialTinter` → `MaterialInterpolators`
→ `NOAH.VFXInterpolator.MaterialColorInterpolator`）得到的实据：

| prefab | 渲染方式 | `MaterialColorInterpolator` |
|---|---|---|
| `es_ah_01` | ParticleSystem ×5 + MeshRenderer ×3 | `propName='_TintColor'` start=(0,0,0,0) **end=(1.423, 1.874, 4.131, 1.0)** |
| `es_ah_03` | ParticleSystem ×8 + MeshRenderer ×7 | `_TintColor` end=(1.224, 1.506, 2.996) / `_HighlightColor` / `_BrightColor` / `_AmbientColor` |
| `es_ah_02` | **纯 Mesh**（无 ParticleSystem） | `_TintColor` start=(0,0,0,0) end=(0,0,0,0.447) |
| `es_energy_01` | ParticleSystem ×5 | `_TintColor` end=(0.0, 0.319, 0.623) 等 4 条 |
| `es_attackd1_002` / `_005` / `es_attackup_01` / `es_attackhlod_01` | 纯 ParticleSystem | 无颜色插值器 |

**结论**：

1. **特效的颜色烘在插值器的 `startValue` / `endValue` 里**，`propName` 指定写到材质的哪个属性
   （ES 全是 `_TintColor`，部分额外有 `_HighlightColor` / `_BrightColor` / `_AmbientColor`）。
2. 播放时由 `MaterialColorInterpolator` **逐帧写入材质** —— 所以**直接改材质没有意义，会被覆盖**，
   这也解释了早期"挂了 `MaterialColors.Apply` 却完全没用"的现象。
3. `endValue` 的 RGB 可能 **> 1**（HDR），比如 `(1.423, 1.874, 4.131)` —— 保持亮度时要按最大通道缩放。
4. `A` 通道是「出现 → 保持 → 消失」的**包络**（配合 `curve[0]` 使用），改色时**必须保留 A**，
   否则整个特效的节奏就毁了。

**应对**：只改插值器的 RGB（保留 A、按原亮度等比缩放），在 `createVisualEffect` postfix 里
趁特效还没开始播放时改写 `startValue` / `endValue`。不挂 Harmony、纯反射读写实例字段，零崩溃风险。

**这同时修掉了「一部分变了一部分没变」** —— 粒子型特效其实有**两层颜色**
（粒子 `startColor` + 材质 `_TintColor`），早先只改了前者，所以看起来只变了一半。

配置项 `[特效换色] TintProperties`，默认
`_TintColor,_HighlightColor,_BrightColor,_AmbientColor`。

#### 日志截断的教训

`createVisualEffect` 的 trace 原本是**「前 40 条」计数上限**。一场战斗十几秒就打满，
之后放的技能**根本不会被打印** —— 看日志的人会以为"那个特效没走这条路"，
实际只是没轮到打印。现在全部改成**按名字去重**（`_seenCreateFx` / `_seenRecolored` /
`_seenNoPs` / `_seenTints`，各 500 个不同名字上限，去掉 `(Clone)` 归并）。

> 凡是"只记录前 N 条"的探针，都会制造这种假否定。诊断日志一律用**按名字去重**，不要用计数截断。

### 最终落地方案（已实测生效）

三条路线同时挂着，互为补充：

| 路线 | 挂点 | 覆盖范围 |
|---|---|---|
| **运行时实例** | `ActorEffectMgr.createVisualEffect` Postfix → 遍历生成的 GameObject 上所有 `ParticleSystem`，改 `main.startColor` | 绝大多数攻击特效 |
| **JIT 资产** | `AssetBundleProvider.LoadAsset` Postfix → 命中 `Role/Es` 的 prefab 时递归遍历改粒子系统 | 绕过 `ActorEffectMgr` 的那批 |
| **兜底** | `VFXEffectHub.DoStart` / `Reactivate` / `Awake` Postfix | 走 hub 的特殊特效 |

#### 保持亮度 + 手动亮度倍率

`RecolorPs` 里的换色不是直接写目标色，而是**按原颜色亮度等比缩放**：

```csharp
float lum = Math.Max(orig.r, Math.Max(orig.g, orig.b));    // 原色亮度
float tl  = Math.Max(color.r, Math.Max(color.g, color.b)); // 目标色亮度
float k   = (tl > 0.0001f && lum > 0.0001f) ? lum / tl : 1f;
if (!KeepBrightness) k = 1f;
k *= (float)CfgBrightnessScale.Value;                      // 手动倍率
main.startColor = new ParticleSystem.MinMaxGradient(
    new Color(color.r * k, color.g * k, color.b * k, a));
```

**为什么还需要手动倍率**：加色混合（Additive）下真正参与累加的是 R+G+B 总量。
原来的暗蓝约 `(0.44, 0.62, 1.00)`，总量 2.06；洋红 `(1, 0, 1)` 总量 **2.0** —— 即使按最大通道对齐，
观感仍会偏亮、偏"糊"。`BrightnessScale` 就是直接压暗它的旋钮。

> ⚠ `MinMaxGradient` 在 Il2CppInterop 里**只暴露 `color`（只读）+ 两个构造函数**，
> `colorMin` / `colorMax` 是私有字段，反射也拿不到。
> 所以只能保留**读得到的那个 alpha**，原来的颜色渐变结构会被压成单色 —— 这是"发光球"问题的根源之一。
> 早期"直接写满值洋红把渐变洗成纯白热球"就是这个原因，`KeepBrightness` 是对它的补偿。

#### 配置项（`[特效换色]` 段）

| 键 | 默认 | 说明 |
|---|---|---|
| `RecolorEffect` | true | 总开关 |
| `KeepBrightness` | true | 按原颜色亮度等比缩放 |
| **`BrightnessScale`** | **1.0** | **手动亮度倍率，叠加在 KeepBrightness 之上。刺眼就调小（如 0.5），想要更亮就调大** |
| `AssetFilter` | `Role/Es` | JIT 路线匹配的资产路径串 |
| `AssetExclude` | 空 | 排除名单，逗号分隔（如填 `stand` 可跳过待机光效） |
| `EffectNameFilter` | `es` | 按特效名过滤；留空 = 所有角色 |
| `ColorHex` | `FF00FF` | 目标色，支持 `FF00FF` / `#FF00FF` / `"r,g,b"` / `"r,g,b,a"` |

配置改动**运行时热生效**（`RecolorPs` 每次都重新读 `ConfigEntry.Value`），
用 F12 的 ConfigurationManager 拖动即可实时看效果，不用重启。

#### 仍未覆盖（截至最后一次实测）

- ES **SP 技能的翅膀特效**
- **下+技能**释放的剑气

这两个都不经过 `ActorEffectMgr.createVisualEffect`（日志里 30 条 `[特效换色:改过的]` 全是 `es_attackAir_*` / `es_dash_*` 这类基础招式）。
已加 `VFXEffectHub.Awake()` 作为更宽的兜底；若仍不生效，说明它们走的是**独立于 VFXEffectHub 的渲染路径**
（可能是 Spine 挂点上的专用 `VFX*` 组件），需要先按名字在 `effect/prefab/role/es/` 里反查到具体 prefab，再看它挂了什么。

---

## 三、已知限制 / 待验证

- **尚未在游戏内实测**。所有替换字符串都已校验「在原 JS 里唯一」，但需要你启动一次
  游戏，看 `LogOutput.log` 里的 `[PATCH]` 行确认真的命中了。
- `inheritSkill` 里每个角色只有 2 个技能，且是 id 而非名字。
  「影子冲刺」在本地化里**没有独立词条**（只出现在「影刃冲刺」的升级描述中），
  所以暂时无法直接从名字反查 id。先用注入补丁试候选 id，进游戏看名字最快。
- 潜力池刷空时会返回失败（角色已集齐全部潜能），这是正常行为。
- 商店货品免费后，`Hashrate` 不再消耗；潜能点也不再消耗。
