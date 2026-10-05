using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Unity.Mathematics.FixedPoint;

namespace BlazblueJsPatch;

/// <summary>
/// 纹章解放接管：原地留一个环，同时向 N 个方向再放出等寿命的环。
///
/// 纹章到底是什么（查清楚了，不是猜的）
/// ────────────────────────────────────
/// 之前一直在**特效层**找那个"大环形纹章"，方向从头就是错的。它是**弹幕**：
///
/// · `BulletConfig` 表里 `<actorId>NN` 是各角色的专属弹幕，
///   ES 的是 **`Id=10340101, LogicRes="esbullet"`** —— 全表唯一一条 ES 专属。
///   (表在 `data/...` → `bulletconfig.ab` / TextAsset "BulletConfig"，180 条)
///
/// · `actor/logicdata/esbullet.ab` 里是一个 `GamePlay.ActionLogicGroup`(ScriptableObject)，
///   就是这条弹幕的**动作状态机**，动作名包括：
///       dash / dash2 / dashAir~3 / dashAtk / dashB / dashBEX / dashSkill / dashSkill2
///       attackAir / attackAir2 / talentBullet / DAA / DAA2 / a3 / aup / x1 / x3 / xup
///       AD_hit / Splash / Esbullet_B / Esbullet_C / jump / dead / push_back / behit_*
///   每个动作自带特效路径(`Role/Es/es_attackAir_02` 等)、判定(`Hit/hit_018`)、音效、帧数据。
///   → 所以"纹章" = BulletObj + ActionLogicGroup，**不是** VFXEffectHub 粒子特效。
///
/// · ES 的角色动作组 (`actor/logicdata/es.ab`，1MB 的 ActionLogicGroup) 里有 **26 条**
///   CreateBullet 指令，参数模板长这样（实测原文，注意 trigger 在前）：
///
///       CreateBullet → trigger:56161,bullet_id:10340101,bullet_action:"dashSkill",
///                      pos_x:0,pos_y:-0.5,dir_x:1,dir_y:0
///       变体: CreateBulletX / CreateBulletIfTriggerChange / CreateBulletU / CreateBulletm
///       可选后缀: ,bullet_action_new:"dashSkill2"  ,tag:"es_x"  ,scale:1
///
///   对应 C# 侧的 `<>f__AnonymousType34<bullet_id, bullet_action, targpos, bullet_target,
///   pos_x, pos_y, **dir_x, dir_y**, trigger, bullet_action_new, damage, scale, limit,
///   limitKey, tag>` —— **dir_x/dir_y 就是方向旋钮**，而且 26 条全部硬编码成 (1,0)。
///
/// · 升级分支的写法是 `bullet_action_new`：先按 bullet_action 生成，满足条件时
///   `ChangeAction` 到 _new。所以 `dashSkill` → `dashSkill2` 就是
///   「升级纹章解放后向面朝方向移动」那条分支的形态。这也是本模块把
///   "中间那个环"和"飞出去的环"拆成两个可配动作的原因。
///
/// 为什么能这么做
/// ──────────────
/// `CreateBulletByParams(caster, idx, pos, **dir**, damageScale, startAction, skillActivate, paramSet)`
/// 的 dir 是**局部空间**方向(`ActorBase.TransformDirToGlobal` 会按朝向变换)，
/// 所以只要把 dir 绕原点转 45°×k 再生成，就是"以角色朝向为基准的八个方向"。
/// 生命周期不用管 —— 用的是同一条 bullet 配置 + 同一个 action，天然等同。
///
/// 一个必须注意的坑
/// ────────────────
/// **不能在自己的 Postfix 里直接再调 CreateBulletByParams。**
/// 该方法是在 `BulletMgr` 遍历 `BulletList` 的调用栈里被调到的，
/// 当场往 List 里追加会破坏正在进行的枚举（InvalidOperationException 或漏处理）。
/// 所以这里只**入队**，统一在 `ValidateBullets`(帧末扫描) 的 Postfix 里补生成。
/// </summary>
internal static class EsEmblemBurst
{
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;
    internal static BepInEx.Configuration.ConfigEntry<string> CfgActions;
    internal static BepInEx.Configuration.ConfigEntry<int> CfgBulletId;
    internal static BepInEx.Configuration.ConfigEntry<int> CfgRingCount;
    internal static BepInEx.Configuration.ConfigEntry<float> CfgStartAngle;
    internal static BepInEx.Configuration.ConfigEntry<string> CfgRingAction;
    /// <summary>追加弹幕: "触发弹幕action:要追加的弹幕action | ..."，如 "aD12:A1"。</summary>
    internal static BepInEx.Configuration.ConfigEntry<string> CfgAttach;
    /// <summary>追加弹幕的待生成队列（帧末统一补生成，理由同 _queue）。</summary>
    private static readonly List<Req> _attach = new List<Req>();
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgDebug;

    /// <summary>只接管"原版"纹章：从我们克隆出来的连段段（技能行 Order&gt;=900）打出的纹章跳过。
    /// 见 CreatePostfix 里的说明（下段线末尾那个会移动的纹章解放要靠它保住）。</summary>
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgSkipOurSegments;

    private static int _skipLogged;

    /// <summary>★ 动作起手触发：「某动作起手时按延迟放出弹幕」。
    /// 语法与 AttachBullets 一致（`触发动作:弹幕:延迟[:局部偏转角[:真实方向角]]`，`|` 分隔多条），
    /// 区别只在**触发点**：AttachBullets 挂在"弹幕生成"，这里挂在"动作起手"。
    /// 为什么必须另开一个：崔斯坦的踩踏/落地那批动作自己不生成弹幕，AttachBullets 无从搭车。见 Apply 里的说明。
    /// 追加出来的弹幕统一走 `_attach` 队列（延迟/`_fixes` 那一套完全复用）。</summary>
    internal static BepInEx.Configuration.ConfigEntry<string> CfgStartBullets;

    /// <summary>自推飞行的默认速度（世界单位/秒）—— 规则里没写第 5 段时用它。</summary>
    internal static BepInEx.Configuration.ConfigEntry<float> CfgStartFlySpeed;
    /// <summary>自推飞行管多久（秒）。到期就撒手交还给弹幕自己（有限值，防池化误伤）。</summary>
    internal static BepInEx.Configuration.ConfigEntry<float> CfgStartFlySeconds;

    private static readonly Dictionary<string, float> _startSeen = new Dictionary<string, float>();
    private static readonly Dictionary<string, int> _startLogged = new Dictionary<string, int>();
    /// <summary>起手触发的诊断去重表（按 "动作|原因"）。上限 60 条，防刷屏。</summary>
    private static readonly HashSet<string> _startDiagSeen = new HashSet<string>();

    // ---- 计数式诊断（不再限条数：限条数会制造假阴性，本项目栽过 5 次）----
    /// <summary>StartBullets 里所有【规则键名】（冒号前那一段）的集合，按配置原文缓存。
    ///
    /// 为什么必须按"键名"而不是"整串里出现过"：弹幕自己的动作(A1/B1/C1/x2/aup…)
    /// 同样走 ActionMgr.ChangeAction，而它们作为【值】出现在配置里 ——
    /// 用 IndexOf 预筛会把它们全放进来：钩子每次白跑，计数也被污染
    /// （实测那 96 次"caster 不是本地玩家"其实是弹幕，不是影子）。</summary>
    private static HashSet<string> _startKeys;
    private static string _startKeysRaw;

    private static bool IsStartKey(string name)
    {
        try
        {
            var raw = CfgStartBullets?.Value ?? "";
            if (_startKeys == null || !string.Equals(raw, _startKeysRaw, StringComparison.Ordinal))
            {
                _startKeysRaw = raw;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in raw.Split('|'))
                {
                    int c = item.IndexOf(':');
                    if (c > 0) set.Add(item.Substring(0, c).Trim());
                }
                _startKeys = set;
            }
            return _startKeys.Contains(name);
        }
        catch { return false; }
    }

    /// <summary>各跳过原因的累计次数。永远只增不减，汇总时打印。</summary>
    private static readonly Dictionary<string, int> _skipReasons = new Dictionary<string, int>();
    /// <summary>配置里点名的动作"到达钩子"的次数（含成功与失败）。</summary>
    private static int _hookHits;
    /// <summary>我们真正入队放出的弹幕数。</summary>
    private static int _spawnQueued;
    /// <summary>汇总间隔计时。</summary>
    private static float _sumAcc;
    private static int _sumLogged;
    /// <summary>被朝向闸门跳过的计数（按弹幕名去重，只打前几次）。</summary>
    private static readonly Dictionary<string, int> _faceSkipLogged = new Dictionary<string, int>();
    internal static BepInEx.Configuration.ConfigEntry<string> CfgUpright;
    internal static BepInEx.Configuration.ConfigEntry<int> CfgUprightFrames;
    internal static BepInEx.Configuration.ConfigEntry<string> CfgSpread;
    internal static BepInEx.Configuration.ConfigEntry<float> CfgMoveSeconds;
    internal static BepInEx.Configuration.ConfigEntry<float> CfgSpeed;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgPinCenter;
    internal static BepInEx.Configuration.ConfigEntry<float> CfgPinSeconds;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgCheckDead;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgPlayerOnly;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgHoldAfterMove;
    internal static BepInEx.Configuration.ConfigEntry<float> CfgHoldSeconds;

    private struct Req
    {
        public GamePlay.BulletMgr Mgr;
        public GamePlay.ActorBase Caster;
        public int Idx;
        public Fp2 Pos;
        public Fp DamageScale;
        public string Action;
        public SkillActivateFixedPointWrap Skill;
        public GamePlay.ParamSet Params;
        /// <summary>游戏原生生成的那个"正中间"纹章 —— 它的朝向就是标准答案。</summary>
        public GamePlay.BulletObj Center;
        /// <summary>原生纹章出生时的 dir，原样复用可以保证视觉一模一样。</summary>
        public Fp2 Dir;
        /// <summary>true = 我们自己推位移；false = 旋转 dir 交给动作自己飞。</summary>
        public bool MoveMode;
        /// <summary>追加弹幕的剩余延迟(秒)。>0 时留在队列里每帧递减。</summary>
        public float Delay;

        /// <summary>追加弹幕相对触发弹幕方向的【局部空间】偏转角(度)。0 = 完全同向。</summary>
        public float AngleDeg;

        /// <summary>
        /// 追加弹幕的【真实飞行方向角】（度，相对角色朝向：0=前 90=上 -90=下）。
        ///
        /// 为什么需要它、以及它和 <see cref="AngleDeg"/> 的分工：
        ///   `AngleDeg` 只转 `dir` 参数 —— 而**弹幕自己的位移逻辑根本不看 `dir`**
        ///   （实测 30/60/90 无变化、120/150 才翻向 → 它只拿 `dir` 当"朝前/朝后"的粗判据；
        ///    纹章解放那边当年的结论一模一样: "Dir 模式会让环躺下")。
        ///   所以要精确控制方向，只能**我们自己推位移** —— 走 `_movers` + `MoveTick`
        ///   那套（帧内绝对定位，`位置 = 基准点 + 方向 × 速度 × 已推时长`，动作自己怎么推都白搭）。
        ///   这正是纹章解放能出八个方向的原因。
        ///
        /// <see cref="HasMoveDeg"/> = false 时本字段无意义（= 完全走弹幕自己的逻辑，与改动前一致）。
        /// </summary>
        public float MoveDeg;

        /// <summary>配置里给了第 5 段（真实方向角）才为 true。</summary>
        public bool HasMoveDeg;

        /// <summary>
        /// ★ 自推【飞行】方向角（度，**屏幕空间**：0=右 90=上 180=左 -90=下）。
        ///
        /// 为什么 StartBullets 需要它：`AngleDeg` 只转弹幕的 `dir`，而弹幕自己的位移逻辑
        /// 根本不看 dir（实测 30/60/90 无变化、120/150 才翻向 ⇒ 只认"朝前/朝后"）。
        /// 所以"斜四向飞出去的剑气"这条路**必须自己推位移** ——
        /// 和纹章解放出八个方向是同一个引擎（`_movers` + `MoveTick` 的绝对定位）。
        ///
        /// ⚠ 与 <see cref="MoveDeg"/> 的区别：`MoveDeg` 对**追加弹幕**只做视觉旋转
        ///   （追加出来的是"纹章"，原地不动），不会产生位移；`FlyDeg` 才是真的让它飞。
        /// </summary>
        public float FlyDeg;

        /// <summary>自推飞行的速度（世界单位/秒）。&lt;=0 时用 `StartFlySpeed` 配置值。</summary>
        public float FlySpeed;

        /// <summary>配置里给了飞行角才算自推飞行（见 `StartFor` 的解析）。</summary>
        public bool HasFly;

        /// <summary>飞行方向的参考系：false=屏幕/世界角(崔斯坦用)，true=角色朝向(高文用)。
        /// 为什么必须能选：高文有方向性、崔斯坦没有。而弹幕自己的位移逻辑不可靠，
        /// 只能我们自己算全局方向 —— 这两种参考系算出来的全局方向是两回事。</summary>
        public bool FlyLocal;

        /// <summary>朝向闸门：0=不限，-1=只在朝左时放，+1=只在朝右时放。
        /// 用途：高文落地按 ES 朝向放左/右剑气（方向和崔斯坦的左右一致，所以参考系仍是屏幕）。</summary>
        public int FaceReq;
    }

    /// <summary>一个由我们推着往外飞的环（或钉在原地不动的原版环）。</summary>
    private struct Mover
    {
        public GamePlay.BulletObj Ring;
        public Vector2 Dir;        // 全局单位方向（Pin=true 时无意义）
        public float TimeLeft;     // 还要管多久(秒)
        /// <summary>true = 钉住不动：每帧把位置【赋值】回原位，而不是叠加位移。</summary>
        public bool Pin;
        /// <summary>基准点 —— 懒捕获，因为入队那一刻弹幕可能还没摆好。</summary>
        public bool Captured;
        public Vector2 At;
        /// <summary>已经推了多久（秒）。绝对定位要用它算总位移。</summary>
        public float Elapsed;
        /// <summary>这一条自己的速度（世界单位/秒）。&lt;=0 = 用全局配置 CfgSpeed（八个环就是走这条）。</summary>
        public float Speed;

        /// <summary>我们上一次亲手写进去的位置（复用检测用）。</summary>
        public Vector2 Last;
        /// <summary>是否已经写过至少一次。</summary>
        public bool Wrote;
    }

    private static readonly List<Mover> _movers = new List<Mover>();

    /// <summary>存活探针：生成后过一小段回看这条弹幕还在不在。
    ///
    /// 为什么需要它：实测"连按崔斯坦会丢剑气"，而日志显示 76 发全部【生成成功】、
    /// 零次返回 null ⇒ 丢失发生在生成之后。两种可能必须分开：
    ///   ① 生成成功但随后被清掉（游戏动作自带 BulletClearTarget / DeleteBulletByTag，
    ///      踩踏链动作极密，我们附的剑气继承了实参模板的 tag ⇒ 刚生成就被清）
    ///   ② 生成了但没显示（位置/缩放/可见性）
    /// 这一行日志直接把 ① 和 ② 分开。
    /// </summary>
    private static readonly List<Surv> _surv = new List<Surv>();

    private struct Surv
    {
        public GamePlay.BulletObj B;
        public string Act;
        public float Left;      // 还要等多久才回看
    }

    /// <summary>由帧末冲洗调用：到点回看每条弹幕是否还活着。</summary>
    private static void SurvTick(float dt)
    {
        if (_surv.Count == 0) return;
        for (int i = _surv.Count - 1; i >= 0; i--)
        {
            var s = _surv[i];
            s.Left -= dt;
            if (s.Left > 0f) { _surv[i] = s; continue; }
            _surv.RemoveAt(i);
            try
            {
                bool dead = s.B == null || IsDead(s.B);
                Plugin.Log?.LogInfo($"[纹章接管:存活] \"{s.Act}\" 0.35s 后: {(dead ? "★已消失/已死（被清掉）" : "仍在")}");
            }
            catch (Exception e) { Plugin.Log?.LogError($"[纹章接管:存活] 异常: {e.Message}"); }
        }
    }

    /// <summary>一个待扶正的环，以及它的参照物。</summary>
    private struct Fix
    {
        public GamePlay.BulletObj Center;
        public GamePlay.BulletObj Ring;
        public int FramesLeft;

        /// <summary>
        /// true = **不抄 Center 的朝向**，而是把这个弹幕的 Renderer 绕 Z 轴【额外】转 Deg 度。
        /// 给 `AttachBullets` 的第 5 段用（想让追加的纹章换朝向）。
        ///
        /// 为什么是 Renderer 的 Z 而不是 `dir`：
        ///   本作是 3D 渲染的 2D 格斗，弹幕网格朝向从 `dir` 推 —— 一转 `dir` 它就**转出平面
        ///   变成"躺着"的**（实测 30/60/90 看不出变化、120/150 才翻向，正是翻出/翻回屏幕）。
        ///   所以平面内的旋转必须动 Renderer，这也是 `UprightTick` 当初的结论。
        /// </summary>
        public bool UseAngle;
        /// <summary>登记那一刻 Renderer 原本的 Z 角 —— 我们是在它【基础上】加角度，不是覆盖。</summary>
        public float BaseZ;
        public bool Captured;
        /// <summary>额外偏转的度数（屏幕平面内，逆时针为正）。</summary>
        public float Deg;
        /// <summary>
        /// 登记时这条弹幕的动作名。**这是"换主人检测"的判据** ——
        /// 弹幕是池化复用的，指针会被下一条弹幕接管；一旦动作名变了就说明
        /// 我们手上这条已经不是我么当初那条了，必须立刻收手并复位，
        /// 否则就会去转一条无辜的弹幕（"其它纹章/剑气错位"就是这么来的）。
        /// </summary>
        public string WantAct;
    }

    private static readonly List<Req> _queue = new List<Req>();
    private static readonly List<Fix> _fixes = new List<Fix>();
    private static bool _reentrant;   // 防止自己生成的弹幕再次触发 Postfix

    // ---------------- 弹幕方向探针（挂 BulletObj.ChangeDir2）----------------

    internal static BepInEx.Configuration.ConfigEntry<bool> CfgTraceDir;
    internal static BepInEx.Configuration.ConfigEntry<string> CfgTraceDirActions;

    private static readonly List<string> _dirWatch = new List<string>();
    private static readonly HashSet<string> _dirNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static string _dirWatchRaw;
    private static int _dirLogged;
    private static int _dirEntered;
    private static int _dirErrs;

    /// <summary>视觉偏转的诊断计数（证明写没写）。见 UprightTick 的 UseAngle 分支。</summary>
    private static int _angLogged;

    private static bool WatchDir(string act)
    {
        try
        {
            var raw = CfgTraceDirActions?.Value ?? "";
            if (!string.Equals(raw, _dirWatchRaw, StringComparison.Ordinal))
            {
                _dirWatchRaw = raw;
                _dirWatch.Clear();
                foreach (var t in raw.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var v = t.Trim();
                    if (v.Length > 0) _dirWatch.Add(v);
                }
            }
            foreach (var w in _dirWatch)
                if (string.Equals(w, act, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 弹幕方向的【唯一写入点】探针 —— 打的是调用之后 `GetDir2()` 的**实际值**，
    /// 也就是这一帧游戏最终认定的方向。
    ///
    /// ⚠ 故意**不去重**：连续多行相同值恰恰是最重要的判据 ——
    ///   它说明 `UpdateLogic` 每帧都在重算方向（这正是"掰 Renderer 会失效"的根因）。
    ///   上限 60 行，够看清模式又不刷屏。
    /// </summary>
    /// <summary>
    /// 我们追加出来、并且配了方向的弹幕 —— key = BulletObj(=ActorBase) 原生指针。
    /// 用指针而不是动作名是因为这里只有 ActorDir，靠钩子反推 owner 后做成员判定最快。
    /// </summary>
    private sealed class DirT { public float Deg; public int Until; }
    private static readonly Dictionary<IntPtr, DirT> _dirTargets = new Dictionary<IntPtr, DirT>();
    private static bool _inDirFix;   // 防止我们调 ChangeDir2 又触发自己 → 无限递归

    /// <summary>
    /// ⚠ 曾经想用 `dirPtr - 0x60` 反推 owner —— **错了**。
    /// `ActorDir` 是 **class 不是 struct**，`ActorBase` 在 0x60 存的是**指向它的指针**，
    /// 对象本体在别处的堆上，所以偏移减法推不出 ActorBase。
    /// 正解：从 `BulletObj` 直接取 `b.ActorDir`，**拿 ActorDir 自己的指针当键** —— 不需要任何偏移。
    /// </summary>

    /// <summary>
    /// 弹幕方向的【真正汇点】postfix。
    ///
    /// 怎么改方向：**不写裸指针**，而是拿转过的角度**再调一次 `ChangeDir2`**
    /// —— 它本来就会同时写 `m_Dir`(0x10) 和 `m_DirUp`(0x20)，再调一次就保持两个字段一致。
    /// （`ref Fp2` 那条路走不通: 本项目的已知坑 —— Harmony 对值类型形参的 ref 写回传不到原生代码。）
    ///
    /// 为什么这个挂点是对的：它拦的是"游戏自己算方向"的那一刻 ——
    /// `BulletObj.UpdateLogic` 每帧都会走到这里，所以
    /// **不存在"被下一帧覆盖"，也不碰 Transform，所以没有池化残留**。
    /// </summary>
    public static void ActorDirChangePostfix(GamePlay.ActorDir __instance, Fp2 dir)
    {
        if (_inDirFix) return;
        try
        {
            // ★ 先证明"这个 postfix 到底有没有被调用"。
            //   上一版外面包了个大 catch{}, 于是"没被调用"和"一进来就抛异常"看起来一模一样 ——
            //   这正是本项目踩坑清单里那条【静默 return 同样致命】。先各打 5 条。
            if (_dirEntered < 5)
            {
                _dirEntered++;
                Plugin.Log?.LogInfo($"[弹幕方向] postfix 进入 (第 {_dirEntered} 次) instance=0x{(__instance == null ? 0L : __instance.Pointer.ToInt64()):X}");
            }

            bool trace = CfgTraceDir?.Value == true;
            if (_dirTargets.Count == 0 && !trace) return;

            // 键就是 ActorDir 自己的指针 —— 登记时用的也是它，两边天然对得上
            IntPtr key = __instance.Pointer;
            if (key == IntPtr.Zero) return;

            // ---- 探针：回答"我们的弹幕有没有经过这里" ----
            //   ★ 判据换成【指针命中】：ActorDir 拿不到 owner、也就拿不到动作名，
            //     但我们登记时用的就是 ActorDir 指针，所以"命中 = 这是我们那条弹幕" 是精确的。
            if (trace && _dirLogged < 60)
            {
                bool ours = _dirTargets.ContainsKey(key);
                if (ours || _dirNames.Add(key.ToString("X")))
                {
                    string d = "?";
                    try { d = dir.ToString(); } catch { }
                    _dirLogged++;
                    Plugin.Log?.LogInfo($"[弹幕方向] dir={d}  {(ours ? "★这是我们登记的弹幕" : "别人的")}" +
                                        $"   #{_dirLogged}");
                }
            }

            // ---- 转向：只有我们自己追加、且配了方向的弹幕才动 ----
            DirT t;
            if (!_dirTargets.TryGetValue(key, out t)) return;
            // ⚠ 过期即摘掉：弹幕是【池化复用】的，指针会被下一条弹幕重用，
            //   不摘掉就会去转一条无辜的弹幕。用注册时的帧号 + 存活帧数兜住。
            int now = 0; try { now = Time.frameCount; } catch { }
            if (now > t.Until) { _dirTargets.Remove(key); return; }
            float deg = t.Deg;
            if (deg == 0f) return;

            float x = (float)dir.x, y = (float)dir.y;
            Rot2(ref x, ref y, deg * Math.PI / 180.0);
            _inDirFix = true;
            try { __instance.ChangeDir2(new Fp2((Fp)x, (Fp)y)); }
            finally { _inDirFix = false; }
        }
        catch (Exception e)
        {
            if (_dirErrs++ < 5)
                Plugin.Log?.LogWarning($"[弹幕方向] postfix 异常: {LogEx.Unwrap(e)}");
        }
    }

    /// <summary>
    /// F9 弹幕实验台用：**最近一次真实弹幕**的完整实参。
    ///
    /// 面板放弹幕时需要 skillActivate / paramSet / pos / dir —— 这些只有
    /// createBulletImp 的实参里才有，光知道 idx 是放不出来的。
    /// 所以在这里留一份"模板"，面板换掉 idx / action 即可复用。
    ///
    /// ⚠ <c>Center</c> 故意留 null：那是 BulletObj，**池化对象**，
    ///   长期持有引用会把它钉住（本项目的已知坑）。而 CreateBulletByParams 也不需要它。
    /// </summary>
    /// ⚠ Req 是 **struct**，所以这里必须用可空 —— 直接 `= null` / `!= null` 编译不过（CS0019）。
    private static Req? _lastLive;
    internal static bool HasTemplate => _lastLive.HasValue && _lastLive.Value.Mgr != null;

    public static int Apply(Harmony harmony)
    {
        var t = AccessTools.TypeByName("GamePlay.BulletMgr");
        if (t == null) { Plugin.Log?.LogWarning("  [纹章接管] 找不到 GamePlay.BulletMgr"); return 0; }

        ResolveVisualMembers();
        if (!IsOff(CfgUpright?.Value) && _visualTransformMember == null)
            Plugin.Log?.LogWarning("  [纹章接管] 拿不到 ActorModel.VisualTransform —— 扶正会无效(环会躺着飞)。已降级为不扶正。");

        int n = 0;
        // ⚠ 挂在【私有汇点 createBulletImp】上, 不是 CreateBulletByParams。
        //   实测: 地面纹章解放走 CreateBulletByParams(能抓到), 而空中释放【一个都没抓到】——
        //   说明还有别的调用方直接进 createBulletImp(数据驱动的触发器指令 CreateBullet 很可能就是)。
        //   createBulletImp 是所有生成路径的公共汇点, 挂它才不漏。
        //   两者只能挂一个, 否则同一发弹幕会被入队两次, 变成放出 16 个环。
        try
        {
            var create = AccessTools.Method(t, "createBulletImp") ?? AccessTools.Method(t, "CreateBulletByParams");
            harmony.Patch(create, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(EsEmblemBurst), nameof(CreatePostfix))));
            Plugin.Log.LogInfo($"  [纹章接管] 已挂钩 BulletMgr.{create.Name} (公共汇点, 只入队)");
            n++;
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [纹章接管] 挂 createBulletImp 失败: {e.Message}"); }

        try
        {
            var val = AccessTools.Method(t, "ValidateBullets");
            harmony.Patch(val, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(EsEmblemBurst), nameof(ValidatePostfix))));

            // ★★ 弹幕方向探针：挂 BulletObj.ChangeDir2。
            //   反汇编结论（2026-10-04）：
            //     ActorDir 用一个【二元组】决定朝向 —— m_Dir(0x10) + m_DirUp(0x20)，
            //     而 ChangeDir2 一次写这两个字段（normalize(dir) 和它的垂直方向）。
            //     调用者共 29 个，其中 **BulletObj.UpdateLogic 每帧都在调** ——
            //     这就是"掰 Renderer 会不生效"的原因（朝向每帧被重算），
            //     也是"拦它是正解"的原因（这里是方向的【唯一写入点】）。
            //   ActorGetDirectionType 的枚举标签（游戏自己的文案）：
            //     StartDir = "(bullet)按发射速度方向" / MovementDir = "(bullet)按速度方向"
            //     —— 弹幕是"跟着速度转"的，所以转 dir 会翻出平面。
            //   ⚠ 实测: `BulletObj.ChangeDir2` **零流量** —— JS 绕过它直连 ActorDir
            //     （ActorDir::ChangeDir2 的 5 个调用者里 4 个是 ActorJs_zz_*）。
            //     所以要挂的是【ActorDir.ChangeDir2】这个真正的汇点。
            var adT = AccessTools.TypeByName("GamePlay.ActorDir");
            var cd2 = adT != null ? AccessTools.Method(adT, "ChangeDir2") : null;
            if (cd2 != null)
            {
                harmony.Patch(cd2, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsEmblemBurst), nameof(ActorDirChangePostfix))));
                Plugin.Log.LogInfo("  [纹章接管] 已挂钩 ActorDir.ChangeDir2 (弹幕方向汇点)");
            }
            else Plugin.Log?.LogWarning("  [纹章接管] 找不到 ActorDir.ChangeDir2");
            Plugin.Log.LogInfo("  [纹章接管] 已挂钩 BulletMgr.ValidateBullets (帧末补生成)");
            n++;
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [纹章接管] 挂 ValidateBullets 失败: {e.Message}"); }

        // ---- ★ 动作起手触发（StartBullets）----
        // 为什么需要它：`AttachBullets` 的触发点是【某个弹幕生成的那一刻】（挂在 createBulletImp 上），
        // 而崔斯坦的踩踏/落地那批动作**自己根本不生成弹幕**（fallupd/fallup22/fallup2d/fallend/fallend2
        // 都不在"含 CreateBullet 的 28 个动作"名单里）⇒ 那条链上没有任何弹幕生成事件可以搭车，
        // 用 AttachBullets 写它们会**一条都不触发**（而且是静默的）。
        // 所以另开一个触发入口：**动作起手时**按延迟放弹幕。
        try
        {
            var am = AccessTools.TypeByName("GamePlay.ActionMgr");
            var ca = am == null ? null : AccessTools.Method(am, "ChangeAction", new[] { typeof(string) });
            if (ca != null)
            {
                harmony.Patch(ca, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsEmblemBurst), nameof(ActionStartPostfix))));
                Plugin.Log.LogInfo("  [纹章接管] 已挂钩 ActionMgr.ChangeAction (动作起手触发 StartBullets)");
                n++;
            }
            else Plugin.Log?.LogWarning("  [纹章接管] 找不到 ActionMgr.ChangeAction(string) —— StartBullets 不会生效");
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [纹章接管] 挂 StartBullets 失败: {e.Message}"); }

        // ---- 弹幕池扩容（和"高速 ES"同一类诉求，跟着这个模块一起挂）----
        try { n += BulletPool.Apply(harmony); }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [弹幕池] 挂载失败: {e.Message}"); }

        // 把【实际生效值】打出来。
        // ⚠ BepInEx 的规则: 配置项一旦写进 .cfg, 改代码里的默认值就不再覆盖它。
        //   所以"我明明改了默认值却没生效"是常态而不是异常 —— 这条日志就是为了让那种情况一眼可见。
        Plugin.Log.LogInfo($"  [纹章接管] 生效配置: Enabled={CfgEnabled?.Value} " +
                           $"BulletId={CfgBulletId?.Value} CrestActions=\"{CfgActions?.Value}\" " +
                           $"RingCount={CfgRingCount?.Value} RingAction=\"{CfgRingAction?.Value}\" " +
                           $"SpreadMode={CfgSpread?.Value} MoveSeconds={CfgMoveSeconds?.Value} " +
                           $"MoveSpeed={CfgSpeed?.Value} PinCenter={CfgPinCenter?.Value} " +
                           $"PinSeconds={CfgPinSeconds?.Value} StartAngle={CfgStartAngle?.Value}");
        return n;
    }

    /// <summary>起手触发没发生的【原因】—— 配置里点名了却没过闸时一定要打出来。
    /// 为什么必须有它：这些 return 全是静默的，而"被某道闸挡住"和"压根没调用到"
    /// 在日志里长得一模一样（本项目为此栽过 5 次，见 PROJECT_STATE 的踩坑清单）。</summary>
    /// <summary>记一次"跳过"并计数。第 1 次 + 每 10 次各打一条 —— 永远打得出来，不会哑。</summary>
    private static void Skip(string name, string why)
    {
        try
        {
            int c;
            _skipReasons.TryGetValue(why, out c);
            _skipReasons[why] = ++c;
            if (c == 1 || c % 10 == 0)
                Plugin.Log?.LogInfo($"[纹章接管:起手] 跳过({why}) 第 {c} 次 —— 最近一个是 \"{name}\"");
        }
        catch { }
    }

    /// <summary>每 10 秒汇总一次：命中/放出/各原因跳过。判断"有没有丢"就看这张表。</summary>
    private static void Summarize(float dt)
    {
        try
        {
            if (_hookHits == 0 && _skipReasons.Count == 0 && _spawnQueued == 0) return;
            _sumAcc += dt;
            if (_sumAcc < 10f) return;
            _sumAcc = 0f;
            if (++_sumLogged > 200) return;          // 只防日志爆炸，正常战斗用不到

            var sb = new System.Text.StringBuilder();
            sb.Append($"[纹章接管:起手] 10s 汇总: 钩子命中 {_hookHits} 次, 入队放出 {_spawnQueued} 发");
            if (_skipReasons.Count > 0)
            {
                sb.Append(", 跳过: ");
                foreach (var kv in _skipReasons) sb.Append(kv.Key).Append('×').Append(kv.Value).Append("  ");
            }
            else sb.Append(", 跳过: 无");
            Plugin.Log?.LogInfo(sb.ToString());
        }
        catch { }
    }

    private static void StartDiag(string name, string why)
    {
        try
        {
            if (_startDiagSeen.Count >= 60 || !_startDiagSeen.Add(name + "|" + why)) return;
            Plugin.Log?.LogInfo($"[纹章接管:起手] \"{name}\" 这次没触发: {why}");
        }
        catch { }
    }

    // ------------------------------------------------------------------ 动作起手触发

    /// <summary>
    /// 「某动作【起手】时按延迟放出弹幕」—— `StartBullets` 的解析。
    /// 取第 skip 条命中项（调用方循环取全部，和 AttachFor 同一套路）。
    /// 语法：`触发动作:弹幕[:延迟秒[:局部偏转角[:真实方向角]]]`，多条用 `|` 分隔。
    /// </summary>
    private static bool StartFor(string action, int skip, out string extra, out float delay,
                                 out float angle, out float moveDeg, out bool hasMoveDeg,
                                 out float flySpeed, out bool hasFly, out bool flyLocal, out int faceReq)
    {
        extra = null; delay = 0f; angle = 0f; moveDeg = 0f; hasMoveDeg = false;
        flySpeed = 0f; hasFly = false; flyLocal = false; faceReq = 0;
        var raw = CfgStartBullets?.Value;
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrEmpty(action)) return false;

        int seen = 0;
        foreach (var item in raw.Split('|'))
        {
            int c = item.IndexOf(':');
            if (c <= 0) continue;
            if (!string.Equals(item.Substring(0, c).Trim(), action, StringComparison.OrdinalIgnoreCase)) continue;
            if (seen++ < skip) continue;

            var parts = item.Substring(c + 1).Split(':');
            if (parts.Length == 0) return false;
            extra = parts[0].Trim();
            if (extra.Length == 0) return false;
            if (parts.Length > 1)
                float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out delay);
            if (parts.Length > 2)
                float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out angle);
            // ⚠ StartBullets 的 `angle` 是【屏幕角度】，**不是** AttachBullets 那个 MoveDeg。
            //   这里刻意【不】设 hasMoveDeg：那是"只转视觉、不产生位移"的老机制，
            //   和自推飞行同时生效会双份处理同一条弹幕（一个转视觉、一个推位置）→ 表现混乱。
            //   所以 StartBullets 只走两条路：写了飞行速度 = 自推飞行；没写 = 只转 dir。
            moveDeg = 0f; hasMoveDeg = false;

            // ★★ 字段槽位（2026-10-04 修正 off-by-one）：
            //      触发:弹幕:延迟:角度:速度[:参考系]
            //      parts[0]=弹幕 parts[1]=延迟 parts[2]=角度 parts[3]=速度 parts[4]=参考系
            //   原来多留了一个 AttachBullets 才用的"视觉偏转"槽，于是 `…:0:0:18:L`
            //   的 18 被吃进废槽、L 落进"速度"槽 -> 解析失败 -> hasFly=false
            //   ⇒ **自推飞行从来没生效过**（崔斯坦那 10 条也一样），弹幕全走自己的逻辑。
            if (parts.Length > 3 && parts[3].Trim().Length > 0)
            {
                float sp;
                if (float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out sp) && sp > 0f)
                {
                    hasFly = true; flySpeed = sp;
                }
                // 解析不出来也没关系 —— 那一段可能是"朝左/朝右/L"这类关键词，下面循环会认它
            }
            // ★ 参考系 / 朝向条件 —— 按【关键词】识别，**不管它在第几段**。
            //
            // 为什么改成扫关键词（2026-10-04 踩到）：
            //   原来写死 parts[4]，于是 `dashAAendEX:C1:0:0:朝右` 这种【不写速度】的规则里
            //   "朝右" 落在 parts[3]（速度槽）→ float 解析失败 → 当没写；
            //   而 parts[4] 压根不存在 ⇒ faceReq=0 ⇒ 门控形同虚设，左右各出一发。
            //   语法本来就允许省略中间段，所以这里扫全部剩余段来认关键词。
            for (int pi = 3; pi < parts.Length; pi++)
            {
                var fr = parts[pi].Trim();
                if (fr.Length == 0) continue;

                if (fr.Equals("L", StringComparison.OrdinalIgnoreCase) ||
                    fr.Equals("local", StringComparison.OrdinalIgnoreCase) ||
                    fr.Equals("局部", StringComparison.Ordinal) ||
                    fr.Equals("朝向", StringComparison.Ordinal))
                {
                    flyLocal = true;                       // 以角色前方为 0
                }
                else if (fr.Equals("朝左", StringComparison.Ordinal) ||
                         fr.Equals("left", StringComparison.OrdinalIgnoreCase) ||
                         fr.Equals("lft", StringComparison.OrdinalIgnoreCase))
                {
                    faceReq = -1;                          // 只在 ES 朝左时放
                }
                else if (fr.Equals("朝右", StringComparison.Ordinal) ||
                         fr.Equals("right", StringComparison.OrdinalIgnoreCase) ||
                         fr.Equals("rgt", StringComparison.OrdinalIgnoreCase))
                {
                    faceReq = +1;                          // 只在 ES 朝右时放
                }
                // S/屏幕 是默认值，不需要处理；认不出来的段忽略
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// `ActionMgr.ChangeAction` 的 postfix —— 动作起手的触发点。
    ///
    /// 为什么挂这里、而不是继续用 `AttachBullets`（挂 createBulletImp）
    /// ────────────────────────────────────────────────────────────
    /// `AttachBullets` 的语义是"**某个弹幕生成的那一刻**，顺带再放一个"。
    /// 而崔斯坦的踩踏/落地那批动作**自己不生成弹幕**：
    ///   `fallupd` / `fallup22` / `fallup2d` / `fallend` / `fallend2` 全都不在
    ///   "含 CreateBullet 的 28 个动作"名单里（`_es_map_out.txt`）；
    ///   `fallmdownendEX/EX2` 那条是**条件生成**（trigger 11481，实测常常不发）。
    /// ⇒ 那条链上没有任何"弹幕生成事件"可以搭车，用 AttachBullets 写它们会一条都不触发（而且是静默的）。
    ///
    /// 位置/方向取【当前角色】的，不取模板的：
    ///   `pos = caster.PositionFp`（实测原生弹幕的 pos 就是角色位置，日志里 `pos` 与 `casterPos` 完全相等）；
    ///   `dir = (1,0)` 是**局部空间**的前方（局部→全局由 `ActorBase.TransformDirToGlobal` 内部完成），
    ///   所以偏转角放在第 3 段（`AngleDeg`）就是"以角色朝向为基准转"。
    /// </summary>
    public static void ActionStartPostfix(GamePlay.ActionMgr __instance, string name, bool __result)
    {
        try
        {
            if (string.IsNullOrEmpty(name)) return;
            // 只诊断"配置里点名了"的动作 —— 其余动作这条路每帧都在过，不能打日志
            bool listed = IsStartKey(name);          // 只有规则键名才算；弹幕名只是规则里的值
            if (listed) { _hookHits++; if (_hookHits <= 20) Plugin.Log?.LogInfo($"[纹章接管:起手] 钩子命中 \"{name}\" (result={__result})"); }
            if (!__result || __instance == null) return;
            if (listed) Skip(name, "钩子通过(ChangeAction 成功)");
            if (CfgEnabled?.Value != true) { if (listed) Skip(name, "纹章接管 Enabled=false"); return; }

            var raw = CfgStartBullets?.Value;
            if (string.IsNullOrWhiteSpace(raw)) return;
            // 没有实参模板就没法生成（与 F9 面板同一限制：先在游戏里过一次真弹幕）
            if (!_lastLive.HasValue || _lastLive.Value.Mgr == null)
            { if (listed) Skip(name, "还没有弹幕实参模板"); return; }

            GamePlay.ActorBase caster = null;
            try { caster = __instance.Owner; } catch { }
            // 只对本地玩家生效：影子/分身也跑同一套动作，不加这一层会一次动作放好几批
            if (caster == null || !DashInvincible.IsLocalPlayerActor(caster))
            { if (listed) Skip(name, "caster 不是本地玩家"); return; }

            // 去重：一次切换可能被 ChangeAction 调用多次，不去重会一次动作放好几批
            float now = 0f;
            try { now = Time.realtimeSinceStartup; } catch { }
            // ★ 时间戳必须等【真的入队了】才写 —— 第一版写在检查之前，于是
            //   ChangeAction 第一次返回 false、游戏下一帧重试成功时，那次成功
            //   正好落在 0.1s 窗口里被吃掉 ⇒ 表现成"这一下没剑气，等一下又好了"。
            if (_startSeen.TryGetValue(name, out float last) && now - last < 0.10f)
            { Skip(name, "0.1s 内重复(去重窗口)"); return; }
            bool queuedAny = false;

            var tpl = _lastLive.Value;
            Fp2 pos = tpl.Pos, dir = tpl.Dir;
            try { pos = caster.PositionFp; } catch { }
            try { dir = new Fp2((Fp)1f, (Fp)0f); } catch { }

            for (int ai = 0; ai < 8; ai++)
            {
                if (!StartFor(name, ai, out var extra, out var delay, out var angle,
                              out var moveDeg, out var hasMoveDeg, out var flySpeed, out var hasFly, out var flyLocal, out var faceReq)) break;
                foreach (var one in extra.Split('+'))
                {
                    var act = one.Trim();
                    if (act.Length == 0) continue;
                    _attach.Add(new Req
                    {
                        Mgr = tpl.Mgr, Caster = caster, Idx = CfgBulletId?.Value ?? 10340101,
                        Pos = pos, Dir = dir, DamageScale = tpl.DamageScale, Action = act,
                        Skill = tpl.Skill, Params = tpl.Params,
                        Delay = delay, AngleDeg = angle, MoveDeg = moveDeg, HasMoveDeg = hasMoveDeg,
                        FlyDeg = angle, FlySpeed = flySpeed, HasFly = hasFly, FlyLocal = flyLocal,
                        FaceReq = faceReq,
                    });
                    _spawnQueued++;
                    queuedAny = true;
                    _startLogged.TryGetValue(name, out int lg);
                    if (lg < 3)
                    {
                        _startLogged[name] = lg + 1;
                        Plugin.Log?.LogInfo($"[纹章接管:起手] \"{name}\" → 追加 \"{act}\"" +
                                            (delay > 0f ? $" 延迟 {delay:F2}s" : "") +
                                            (angle != 0f ? $" 局部偏转 {angle:F0}°" : "") +
                                            (hasMoveDeg ? $" 真实方向 {moveDeg:F0}°(视觉偏转)" : "") +
                                            (hasFly ? $" 自推飞行 {angle:F0}° @ {flySpeed:F0}u/s" : ""));
                    }
                }
            }
            if (queuedAny) _startSeen[name] = now;    // ★ 只有真放出东西才记时间
        }
        catch (Exception e) { Plugin.Log?.LogError($"[纹章接管:起手] 异常: {e.Message}"); }
    }

    /// <summary>查"这个弹幕要不要额外追加一个"。返回要追加的弹幕 action，没有则 null。</summary>
    /// <summary>查"这个弹幕要不要额外追加一个"。返回 追加action 与 延迟秒；没有则 false。
    ///
    /// ★ 键名同时匹配【触发弹幕的 startAction】和【发射那一瞬角色正在播的动作名】。
    ///
    /// 为什么需要后者：一条链上多个克隆段往往**发射同一个弹幕**（空中 A2 的 A2/A3/A4 就是），
    /// 只按弹幕名挂就必然一起命中，没法只挂给其中一段。
    /// 而克隆动作自带独有名（aceN_xxx），拿它当判据既精确又不用改配置语法 ——
    /// 这和"动作名就是链的认领键"是同一条原则，本项目已经栽过一次（attackAEX 被链[2]抢）。
    ///
    /// 配置写法： 触发键:追加动作[:延迟秒[:角度度]]    多项用 | 隔开
    ///     aD12:A1:0.2             按弹幕名（佩1 的纹章生成时附布1剑气，延迟 0.2s）
    ///     1001:aH2EX:0:15         按【段号】挂（第 1001 段发射时附蓄力1纹章，偏转 15°）
    ///
    /// ★ 角度是【局部空间】的偏转：弹幕的 `dir` 本来就是局部空间（局部→全局由
    ///   `ActorBase.TransformDirToGlobal` 在内部完成），所以在这里转 θ 就是
    ///   "以角色朝向为基准转 θ"，**转身时方向跟着一起转**，不需要自己算全局角。
    ///   正数 = 逆时针（屏幕坐标系下即"往身后偏"）。改完存盘即时生效（每次都现读配置）。
    /// </summary>
    private static bool AttachFor(string startAction, string viaAction, int skip,
                                  out string extra, out float delay, out float angle,
                                  out float moveDeg, out bool hasMoveDeg)
    {
        extra = null; delay = 0f; angle = 0f; moveDeg = 0f; hasMoveDeg = false;
        // skip = 跳过前 N 个命中项。调用方用它循环取【全部】命中项 ——
        // 原来只认第一个，于是"同一个动作配两条附加"永远只生效一条（静默那种）。
        // 布鲁诺三连（每招三道剑气）正是靠这个：同一个动作名下挂多条不同延迟。
        int seen = 0;
        var raw = CfgAttach?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (string.IsNullOrEmpty(startAction) && string.IsNullOrEmpty(viaAction)) return false;
        foreach (var item in raw.Split('|'))
        {
            int c = item.IndexOf(':');
            if (c <= 0) continue;
            var key = item.Substring(0, c).Trim();

            // 纯数字键 = 按【该段的 Order】挂载，运行时解析成我们给那一段起的合成动作名。
            // 合成名是 aceN_<order>，N 是全局递增序号、配置里没法预知；Order 是确定的。
            // 写 Order 就不必"先跑一遍看日志才知道它叫什么"。
            int keyOrder;
            if (int.TryParse(key, out keyOrder))
            {
                var resolved = EsComboChain.NameOfOrder(keyOrder);
                if (string.IsNullOrEmpty(resolved)) continue;   // 那一段还不是我们的（或还没造出来）
                key = resolved;
            }

            bool hit = (!string.IsNullOrEmpty(startAction) &&
                        string.Equals(key, startAction, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrEmpty(viaAction) &&
                        string.Equals(key, viaAction, StringComparison.OrdinalIgnoreCase));
            if (!hit) continue;
            if (seen++ < skip) continue;      // 跳过前面已经取过的命中项

            var rest = item.Substring(c + 1).Trim();
            if (rest.Length == 0) return false;

            // rest = 追加动作[:延迟秒[:dir偏转角[:真实方向角]]]
            var parts = rest.Split(':');
            extra = parts[0].Trim();
            if (extra.Length == 0) return false;
            if (parts.Length > 1 && parts[1].Trim().Length > 0)
                float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out delay);
            if (parts.Length > 2 && parts[2].Trim().Length > 0)
                float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out angle);
            // 第 5 段【有没有写】本身就是开关 —— 写了才由我们推位移。
            if (parts.Length > 3 && parts[3].Trim().Length > 0)
            {
                hasMoveDeg = float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture, out moveDeg);
            }
            return true;
        }
        return false;
    }

    private static readonly HashSet<string> _natLogged = new HashSet<string>();
    private static bool LogEx_Once(string k) { try { return _natLogged.Count < 60 && _natLogged.Add(k); } catch { return false; } }

    private static string[] ActionList()
    {
        var s = CfgActions?.Value;
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        var parts = s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
        return parts;
    }

    private static bool IsCrest(string action)
    {
        if (string.IsNullOrEmpty(action)) return false;
        foreach (var a in ActionList())
            if (string.Equals(a, action, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static void CreatePostfix(GamePlay.BulletMgr __instance, GamePlay.ActorBase caster, int idx,
                                     Fp2 pos, Fp2 dir, Fp damageScale, string startAction,
                                     SkillActivateFixedPointWrap skillActivate, GamePlay.ParamSet paramSet,
                                     GamePlay.BulletObj __result)
    {
        try
        {
            // ★ F9 弹幕实验台：留一份"最近一次真实弹幕"的实参模板。
            //   _reentrant 时跳过 —— 那是我们自己放出来的，不能拿自己当模板
            //   （否则面板会越放越偏，最终把参数污染成我们上次的产物）。
            if (!_reentrant && __result != null)
            {
                try
                {
                    _lastLive = new Req
                    {
                        Mgr = __instance, Caster = caster, Idx = idx, Pos = pos, Dir = dir,
                        DamageScale = damageScale, Action = startAction,
                        Skill = skillActivate, Params = paramSet,
                        Center = null,          // ← 见字段注释：池化对象不能钉
                    };
                }
                catch { }
            }

            // ★★★ 技能修改·技术验证：把某个弹幕的生成【追加】成另一个弹幕。
            //   例: aD12(佩1 的纹章) 生成时, 同时放一个 A1(布1 的剑气)。
            //
            // ⚠ 必须放在 CfgEnabled 检查【之前】—— 追加弹幕有自己的开关
            //   (AttachBullets 非空即生效)，不该被"纹章接管"的 Enabled 挡住。
            //   （第一版就搭在后面，结果 Enabled=False 时永远走不到 —— 实测踩到）
            // ⚠ 只入队 —— 当场调 CreateBulletByParams 会破坏 BulletList 的枚举（见文件顶部注释）。
            try
            {
                if (!_reentrant && __result != null)
                {
                    // 发射这一瞬角色正在播的动作名 —— "按动作挂载"的判据（见 AttachFor 注释）。
                    // 用 ActorMgr.CurrentActionName 而不是 caster.Action：后者在克隆段上
                    // 未必是链的认领键那个名字。当前动作名才是。
                    string viaAction = null;
                    try { viaAction = caster?.ActionMgr?.CurrentActionName; } catch { }

                    // 循环取【全部】命中项 —— 同一动作名下可以挂多条（不同延迟/角度）。
                    // 上限 8 条，防配置写错时无限循环。
                    for (int ai = 0; ai < 8; ai++)
                    {
                    string extra; float delay; float angle; float moveDeg; bool hasMoveDeg;
                    if (!AttachFor(startAction, viaAction, ai, out extra, out delay, out angle,
                                   out moveDeg, out hasMoveDeg)) break;
                    {
                        // 两种并列方式：
                        //   · 同一个条目里用 `+`：`1002:attackAir2+aup:0` —— 共用这一条的延迟/角度
                        //   · 写成多条同键条目：`attackAEX:A1:0.10 | attackAEX:A1:0.28`
                        //     —— 各自带延迟/角度（布鲁诺三连就是靠这个：一招三道、依次出）
                        foreach (var one in extra.Split('+'))
                        {
                            var act = one.Trim();
                            if (act.Length == 0) continue;
                            _attach.Add(new Req
                            {
                                Mgr = __instance, Caster = caster, Idx = idx, Pos = pos,
                                DamageScale = damageScale, Action = act,
                                Skill = skillActivate, Params = paramSet, Center = __result,
                                Dir = dir, MoveMode = true, Delay = delay, AngleDeg = angle,
                                MoveDeg = moveDeg, HasMoveDeg = hasMoveDeg,
                            });
                        }
                    }
                    }
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"[纹章接管:追加] 入队失败: {e.Message}"); }

            // 对照：原生 A1/B1/C1(布鲁诺剑气) 生成时的实参 —— 用来和我们追加的那次比对
            try
            {
                if (startAction != null && startAction.Length <= 3 &&
                    (startAction[0] == 'A' || startAction[0] == 'B' || startAction[0] == 'C') &&
                    LogEx_Once("native|" + startAction))
                {
                    string np = "?", nd = "?";
                    try { np = pos.ToString(); } catch { }
                    try { nd = dir.ToString(); } catch { }
                    float nds = 0f; try { nds = (float)damageScale; } catch { }
                    Plugin.Log?.LogInfo($"[纹章接管:对照] 原生 \"{startAction}\" idx={idx} " +
                                        $"pos={np} dir={nd} dmgScale={nds:F3} " +
                                        $"skill={(skillActivate == null ? "null" : "有")} params={(paramSet == null ? "null" : "有")}");
                }
            }
            catch { }

            if (CfgEnabled?.Value != true) return;
            if (_reentrant) return;
            if (__result == null) return;
            if (idx != (CfgBulletId?.Value ?? 10340101)) return;
            if (!IsCrest(startAction)) return;

            // caster 过滤【默认关】。
            // 原因: `idx == 10340101` 本身就是 ES 专属弹幕(全表唯一一条, 敌人用的是别的 id),
            // 所以这一层过滤是多余的 —— 而它会误杀真正的目标:
            // 实测 ES 的招式大量由【影子/分身 Actor】打出, Owner 不是 PlayerSelf,
            // 沿用"只认本地玩家"会把空中那批发出的纹章整批滤掉。
            // 真出现"敌人 ES 也甩环"的问题时再把这个开关打开。
            if (CfgPlayerOnly?.Value == true && caster != null &&
                !DashInvincible.IsLocalPlayerActor(caster)) return;

            // ★★★ 2026-10-04 【只接管"原版"纹章，放我们克隆段的那一发走】
            //
            // 需求原话：原版纹章解放要"钉在原地 + 八方向静止环"，
            // 而**我们克隆段**打出的纹章解放（下段线末尾那个 Order=906）必须保持原样"会移动"。
            // 这就是 PROJECT_STATE 待办里那条"纹章接管 vs 原版移动纹章解放"的取舍 ——
            // 之前以为区分不了，其实**能区分**，而且有一个很干净的判据：
            //
            //   弹幕生成的实参里就带着 SkillActivateFixedPointWrap（= 触发它的那一行技能），
            //   读它的 Order 即可 —— **Order >= 900 就是"我们造的段"**，
            //   这是全项目一直在用的哨兵值（原生 order 都是个位数/几十，不可能碰撞）。
            //
            // 为什么不用动作名去区分：`caster.ActionMgr.CurrentActionName` 在克隆段上
            //   报的到底是合成名(ace7_906) 还是源名(holdEX) 还没验证过，
            //   押它会翻车；Order 是行上的确定字段，不依赖那个。
            int rowOrder = EsComboChain.OrderOfWrap(skillActivate);
            if (CfgSkipOurSegments?.Value != false && rowOrder >= 900)
            {
                if (CfgDebug?.Value == true && _skipLogged < 5)
                {
                    _skipLogged++;
                    Plugin.Log?.LogInfo($"[纹章接管] 跳过克隆段的纹章 (Order={rowOrder}, action=\"{startAction}\")" +
                                        $" —— 这一段保持原生行为（会移动的那个）");
                }
                return;
            }

            _queue.Add(new Req
            {
                Mgr = __instance, Caster = caster, Idx = idx, Pos = pos,
                DamageScale = damageScale, Action = startAction,
                Skill = skillActivate, Params = paramSet, Center = __result,
                Dir = dir,
                MoveMode = (CfgSpread?.Value ?? "Move").Trim()
                               .Equals("Move", StringComparison.OrdinalIgnoreCase),
            });

            // 原版那个环也要管住。
            // 实测：纹章解放升级之后，游戏自己生成的那个环是【会移动】的（向面朝方向飞），
            // 于是光放八个环不够 —— 中间还多一个跟着跑。这里把它钉死在出生点。
            if (CfgPinCenter?.Value != false)
            {
                float ps = CfgPinSeconds?.Value ?? 2.5f;
                _movers.Add(new Mover
                {
                    Ring = __result,
                    Pin = true,
                    Dir = Vector2.zero,
                    TimeLeft = ps > 0f ? ps : 3600f,   // <=0 = 整个生命周期(有池化风险, 见文档)
                });
            }

            if (CfgDebug?.Value == true)
            {
                // IsDead 的实际读数 —— 池化弹幕刚创建时它到底是 true 还是 false,
                // 是判断"存活判据能不能用"的唯一依据, 值得每次打出来。
                string dead = _isDeadMember == null ? "N/A" : (IsDead(__result) ? "true" : "false");
                Plugin.Log?.LogInfo($"[纹章接管] 捕获纹章 action=\"{startAction}\" idx={idx} " +
                                    $"Order={EsComboChain.OrderOfWrap(skillActivate)} " +
                                    $"pos=({pos.x},{pos.y}) caster={(caster == null ? "null" : caster.GetType().Name)}" +
                                    $" IsDead={dead}" +
                                    (CfgPinCenter?.Value != false ? "  [原版环已钉住]" : ""));
            }
        }
        catch (Exception e) { Plugin.Log?.LogError($"[纹章接管] CreatePostfix 异常: {e.Message}"); }
    }

    /// <summary>帧末冲洗：此时 BulletMgr 已经遍历完 BulletList，追加是安全的。</summary>
    public static void ValidatePostfix(Fp dt)
    {
        float dtf = 0.033f;
        try { dtf = (float)dt; } catch { }
        if (dtf <= 0f || dtf > 0.5f) dtf = 0.0333f;   // 本作逻辑帧固定 30fps

        MoveTick(dtf);
        UprightTick();
        SurvTick(dtf);
        Summarize(dtf);

        // ---- 追加弹幕（技能修改验证）: 每个只放 1 个，方向沿用触发它的那一下 ----
        if (_attach.Count > 0)
        {
            _reentrant = true;
            try
            {
                // 从后往前：带延迟的项递减后留在队列里，到点才生成
                for (int ai = _attach.Count - 1; ai >= 0; ai--)
                {
                    var r = _attach[ai];
                    if (r.Delay > 0f) { r.Delay -= dtf; _attach[ai] = r; continue; }
                    _attach.RemoveAt(ai);
                    if (r.Mgr == null) continue;
                    try
                    {
                        // ★★ 朝向闸门【必须在生成之前】——
                        //   第一版插在 CreateBulletByParams 之后，于是两发都已经生成了，
                        //   continue 只挡掉了"推位移"，表现成"朝左朝右各出一发"（左右剑气）。
                        //   规则没写 faceReq=0(不限)；跳过时打日志（每弹幕前 3 次），
                        //   免得又把"正确挡掉"和"没触发"混在一起。
                        if (r.FaceReq != 0)
                        {
                            bool left = FacingLeft(r.Caster);
                            if ((r.FaceReq < 0 && !left) || (r.FaceReq > 0 && left))
                            {
                                Skip(r.Action, $"朝向闸门要求{(r.FaceReq < 0 ? "朝左" : "朝右")}");
                                continue;
                            }
                        }
                        var b = r.Mgr.CreateBulletByParams(r.Caster, r.Idx, r.Pos, RotateLocalDir(r.Dir, r.AngleDeg),
                                                           r.DamageScale, r.Action, r.Skill, r.Params);

                        // ★ 配了第 5 段（真实方向角）就【我们自己推位移】。
                        //   为什么不能只靠 dir：弹幕自己的位移逻辑不看 dir（它只拿 dir 当"朝前/朝后"），
                        //   所以想朝下飞就必须走 MoveTick 那套绝对定位 —— 和纹章解放出八个方向是同一个引擎。
                        if (r.HasMoveDeg && b != null)
                        {
                            // 走 `_fixes`（视觉旋转），**不是** `_movers`（推位移）。
                            // 追加出来的东西是【纹章】不是投射体 —— 它原地不动，只换朝向。
                            // 补 UprightFixFrames 帧是因为动作状态机接下来几帧可能把朝向写回去
                            // （与 UprightTick 的环用的是同一条理由，30fps 下 10 帧 ≈ 0.33s）。
                            // ★ 走【Renderer 视觉偏转】—— 实测这是唯一真能改到它的路。
                            //   （ActorDir 那条: 钩子拦到了、指针也对上了，但纹章**不认**它 ——
                            //     `m_Dir/m_DirUp` 是"角色朝向帧"，纹章是原地不动的，视觉不读它。
                            //     而且转 ActorDir 有副作用: GetRotateDegree 的调用者里有
                            //     `ActorAttackBoxManager::getBoxPosition`，可能连带转掉攻击判定框。
                            //     所以那条路只留探针，不再用来转向。）
                            //
                            //   污染用**两道防线**解决，这样保持时间才敢拉长：
                            //     ① 换主人检测（动作名一变立刻复位收手）—— 见 UprightTick
                            //     ② 到期复位
                            _fixes.Add(new Fix
                            {
                                Ring = b,
                                UseAngle = true,
                                Captured = false,
                                Deg = r.MoveDeg,
                                WantAct = r.Action,
                                FramesLeft = 150,      // ≈5s @30fps，盖住纹章整个可见期
                            });
                            Plugin.Log?.LogInfo($"[纹章接管:追加] \"{r.Action}\" 视觉偏转 {r.MoveDeg}°" +
                                                $"（Renderer, 保持 150 帧 + 换主人检测）");
                        }
                        // ★ 自推【飞行】：StartBullets 配了屏幕角度就走这条。
                        //   和纹章解放出八个方向是同一个引擎（`_movers` + `MoveTick` 的绝对定位），
                        //   区别只是这里不钉死、按自己的速度一直飞。
                        //   为什么非要自己推：弹幕自己的位移逻辑不看 dir（只认"朝前/朝后"），
                        //   斜向 45/135 交给它必然退化成朝前/朝后 —— 这正是"角度只有左右两个方向"的根子。
                        Vector2 flyDir = Vector2.zero;
                        if (r.HasFly && b != null)
                        {
                            // 参考系：L = 以角色前方为 0（GlobalDirAt 就是干这个的，纹章解放八方向用的同一个 helper）
                            //         S/空 = 屏幕/世界角（崔斯坦用，因为它没有方向性）
                            // ⚠ 必须取反 —— 实测（2026-10-04，日志三条证据）：
                            //   ① 原生那发 C1 与我们那发 pos/dir 完全相同，原生飞得对、我们飞反了；
                            //   ② GlobalDirAt 把"角色前方(1,0)"换算成全局，得到的和角色实际朝向差 180°
                            //      （日志配对: casterDir=+1 ↔ fwd=(+1,0)，而 +1 那一侧是我们推错的那侧）；
                            //   ③ 八个环是【对称】的，这个 180° 错误在纹章解放上永远看不出来，
                            //      只有"必须朝前"的剑气才暴露。
                            //   所以这里取反。不动 GlobalDirAt 本身（环那边靠它、且改动无收益）。
                            if (r.FlyLocal) flyDir = -GlobalDirAt(r.Caster, r.FlyDeg);
                            else
                            {
                                float rad = (float)(r.FlyDeg * Math.PI / 180.0);
                                flyDir = new Vector2((float)Math.Cos(rad), (float)Math.Sin(rad));
                            }
                            _movers.Add(new Mover
                            {
                                Ring = b,
                                Pin = false,
                                Dir = flyDir,
                                Speed = r.FlySpeed,
                                TimeLeft = CfgStartFlySeconds?.Value ?? 1.2f,
                                Captured = false,
                            });
                        }

                        if (r.HasFly)
                        {
                            string face = "?";
                            try { face = $"casterDir={r.Caster?.Dir} fwd={r.Caster?.TransformDirToGlobal(new Fp2((Fp)1f, (Fp)0f))}"; } catch { }
                            Plugin.Log?.LogInfo($"[纹章接管:追加] 自推飞行 dir=({flyDir.x:F2},{flyDir.y:F2}) " +
                                                $"参考系={(r.FlyLocal ? "L 角色朝向" : "S 屏幕")} {face}");
                        }
                        if (b != null && _surv.Count < 60)
                            _surv.Add(new Surv { B = b, Act = r.Action, Left = 0.35f });
                        // ★ 指针 + mover 数：查"同一批 4 发是不是拿到了同一个池对象"
                        //   （池按需分配，若 4 发里有两发是同一个对象，视觉上就少一发 ⇒ 看起来像丢失）
                        string ptr = "?";
                        try { if (b != null) ptr = "0x" + b.Pointer.ToInt64().ToString("X"); } catch { }
                        string fp = "?"; try { fp = r.Pos.ToString(); } catch { }
                        string fd = "?"; try { fd = r.Dir.ToString(); } catch { }
                        float ds = 0f; try { ds = (float)r.DamageScale; } catch { }
                        Plugin.Log?.LogInfo($"[纹章接管:追加] 额外放出 \"{r.Action}\" " +
                                            $"(触发 idx={r.Idx}) {(b != null ? "成功" : "返回null")} obj={ptr} movers={_movers.Count}  " +
                                            $"pos={fp} dir={fd} dmgScale={ds:F3} " +
                                            $"skill={(r.Skill == null ? "null" : "有")} params={(r.Params == null ? "null" : "有")}");
                    }
                    catch (Exception e) { Plugin.Log?.LogWarning($"[纹章接管:追加] 放出失败: {e.Message}"); }
                }
            }
            finally { _reentrant = false; }
        }

        // ---- F9 弹幕实验台：手动试放 ----
        // 独立于上面那个块(不等 _attach)，且必须在这里 —— 帧末 BulletList 已遍历完。
        // 参数走 _lastLive 模板：面板只知道 idx/名称，skillActivate/paramSet/pos/dir
        // 得从最近一次真实弹幕借。
        var labShots = BulletLab.DrainPending();
        if (labShots.Length > 0)
        {
            if (!_lastLive.HasValue || _lastLive.Value.Mgr == null)
            {
                Plugin.Log?.LogWarning("[弹幕实验台] 还没捕获过任何真实弹幕，缺少实参模板" +
                                       "（先在游戏里放一次真弹幕，再试）");
            }
            else
            {
                var tpl = _lastLive.Value;
                _reentrant = true;
                try
                {
                    foreach (var s in labShots)
                    {
                        string act = string.IsNullOrEmpty(s.Name) ? tpl.Action : s.Name;
                        try
                        {
                            // `s.Deg` = F9 面板上的"方向偏转"滑条（局部空间，0 = 原样）
                            var b = tpl.Mgr.CreateBulletByParams(
                                        tpl.Caster, s.Id, tpl.Pos, RotateLocalDir(tpl.Dir, s.Deg),
                                        tpl.DamageScale, act, tpl.Skill, tpl.Params);
                            Plugin.Log?.LogInfo($"[弹幕实验台] 试放 idx={s.Id} action=\"{act}\" " +
                                                $"偏转 {s.Deg:F0}° " +
                                                $"-> {(b != null ? "成功" : "返回null(可能是idx/名称对不上)")}");
                        }
                        catch (Exception e)
                        {
                            Plugin.Log?.LogWarning($"[弹幕实验台] 试放 idx={s.Id} action=\"{act}\" 抛出: {e.Message}");
                        }
                    }
                }
                finally { _reentrant = false; }
            }
        }

        if (_queue.Count == 0) return;
        var batch = _queue.ToArray();
        _queue.Clear();

        _reentrant = true;
        try
        {
            foreach (var r in batch)
            {
                if (r.Mgr == null) continue;
                int n = Math.Max(0, Math.Min(CfgRingCount?.Value ?? 8, 32));
                float startDeg = CfgStartAngle?.Value ?? 0f;
                string ringAction = CfgRingAction?.Value;
                if (string.IsNullOrWhiteSpace(ringAction)) ringAction = r.Action;

                // 八个方向的【全局】单位向量。dir 是局部空间的，而位移要我们自己在世界坐标里推，
                // 所以先用 caster.TransformDirToGlobal 把"前方"换算到全局，再在全局平面里平分旋转。
                var dirs = BuildGlobalDirs(r.Caster, n, startDeg, r.MoveMode);

                int ok = 0;
                for (int k = 0; k < n; k++)
                {
                    float deg = startDeg + 360f * k / n;
                    double rad = deg * Math.PI / 180.0;

                    // Move 模式: dir 原样不动 —— 视觉与原生纹章一字不差，不会翻出平面。
                    // Dir  模式: 旋转 dir，交给动作自己的位移逻辑去飞(实测会让环躺下)。
                    Fp2 d = r.MoveMode
                        ? r.Dir
                        : new Fp2((Fp)(float)Math.Cos(rad), (Fp)(float)Math.Sin(rad));

                    try
                    {
                        var b = r.Mgr.CreateBulletByParams(r.Caster, r.Idx, r.Pos, d,
                                                           r.DamageScale, ringAction, r.Skill, r.Params);
                        if (b != null)
                        {
                            ok++;
                            if (r.MoveMode)
                                _movers.Add(new Mover
                                {
                                    Ring = b,
                                    Dir = dirs[k],
                                    TimeLeft = Math.Max(0.05f, CfgMoveSeconds?.Value ?? 1.2f),
                                });
                            else if (r.Center != null && !IsOff(CfgUpright?.Value))
                                _fixes.Add(new Fix
                                {
                                    Center = r.Center, Ring = b,
                                    FramesLeft = Math.Max(1, CfgUprightFrames?.Value ?? 6),
                                });
                        }
                    }
                    catch (Exception e)
                    {
                        Plugin.Log?.LogWarning($"[纹章接管] 第 {k} 个环生成失败: {e.Message}");
                    }
                }
                if (CfgDebug?.Value == true)
                    Plugin.Log?.LogInfo($"[纹章接管] 已按 \"{ringAction}\" 放出 {ok}/{n} 个环 " +
                                        $"(模式={(r.MoveMode ? "Move 自推位移" : "Dir 旋转方向")}, 起始角 {startDeg}°)" +
                                        // 这个数就是"环到底有没有在飞"的直接依据:
                                        // 它一旦归零, 说明 mover 被丢了, 环就会退回由动作自己飞。
                                        $" [活动 mover={_movers.Count}]");
            }
        }
        catch (Exception e) { Plugin.Log?.LogError($"[纹章接管] ValidatePostfix 异常: {e.Message}"); }
        finally { _reentrant = false; }
    }

    /// <summary>把一个 2D 向量按角度旋转（全局平面内）。</summary>
    private static void Rot2(ref float x, ref float y, double rad)
    {
        double c = Math.Cos(rad), s = Math.Sin(rad);
        float nx = (float)(x * c - y * s);
        float ny = (float)(x * s + y * c);
        x = nx; y = ny;
    }

    /// <summary>
    /// 把【局部空间】的 dir 偏转 deg 度 —— 给 `[纹章解放] AttachBullets` 的第四段用。
    ///
    /// 为什么在局部空间里转就够了：弹幕的 `dir` 本来就是局部空间
    /// （局部→全局由 `ActorBase.TransformDirToGlobal` 在内部完成），
    /// 所以在这里转 θ 就等于「以角色朝向为基准转 θ」——
    /// **角色转身时方向跟着一起转**，不需要（也不应该）自己去算全局角。
    ///
    /// ⚠ 别拿 `BuildGlobalDirs` 那套来改这里：那是"我们自己推位移"时用的全局方向，
    ///   两套坐标系不同，混用会让角色朝左时偏到反方向。
    /// </summary>
    private static Fp2 RotateLocalDir(Fp2 d, float deg)
    {
        if (deg == 0f) return d;
        try
        {
            float x = (float)d.x, y = (float)d.y;
            Rot2(ref x, ref y, deg * Math.PI / 180.0);
            return new Fp2((Fp)x, (Fp)y);
        }
        catch { return d; }
    }

    /// <summary>
    /// 角度 → 【全局】单位方向（0 = 角色前方, 90 = 上, -90 = 下）。
    ///
    /// 为什么不能直接用 (cos, sin)：这是给 `MoveTick` 用的**世界坐标**方向，
    /// 必须先知道"角色前方在世界里指哪"，再在它基础上转 ——
    /// 与 `BuildGlobalDirs` 同一套坐标系（那边正是靠这个才做到"角色朝左时八个方向不整体错位"）。
    ///
    /// ⚠ 和 `RotateLocalDir` 是**两个坐标系**，别混用：
    ///   · `RotateLocalDir` —— 转局部空间的 `dir`（喂给弹幕的，但弹幕多半不看）
    ///   · `GlobalDirAt`    —— 算全局方向（喂给 `MoveTick` 的，这个才真的决定往哪飞）
    /// </summary>
    /// <summary>角色此刻是否朝左（世界 -x 方向）。
    ///
    /// 判据用 `-GlobalDirAt(caster,0)` —— 也就是**取反之后**的那个"真正的前方"。
    /// 为什么带这个取反：实测（原生那发 C1 与我们那发 pos/dir 完全相同、原生飞得对而我们飞反了）
    /// 说明 GlobalDirAt 给的全局方向与角色实际朝向差 180°，见 FlyLocal 处的三条证据。
    /// </summary>
    private static bool FacingLeft(GamePlay.ActorBase caster)
    {
        try
        {
            var f = -GlobalDirAt(caster, 0f);
            // ⚠ 判断式取反过一次（2026-10-04 实测）：第一版写成 f.x < 0 时，
            //   朝左按高文实际出了右剑气 —— 说明"朝左 <=> f.x < 0"是反的。
            //   与 FlyLocal 那处取反同源（GlobalDirAt 的 180° 偏差），但这里不能再套一层推理，
            //   只能以实测定：**f.x > 0 才是朝左**。
            return f.x > 0f;
        }
        catch { return false; }
    }

    private static Vector2 GlobalDirAt(GamePlay.ActorBase caster, float deg)
    {
        float fx = 1f, fy = 0f;
        try
        {
            if (caster != null)
            {
                var g = caster.TransformDirToGlobal(new Fp2((Fp)1f, (Fp)0f));
                float gx = (float)g.x, gy = (float)g.y;
                float len = (float)Math.Sqrt(gx * gx + gy * gy);
                if (len > 1e-6f) { fx = gx / len; fy = gy / len; }
            }
        }
        catch { }
        Rot2(ref fx, ref fy, deg * Math.PI / 180.0);
        return new Vector2(fx, fy);
    }

    /// <summary>
    /// 算出八个方向的【全局】单位向量。
    ///
    /// 为什么不直接用 (cos, sin)：弹幕的 dir 是**局部空间**的（`ActorBase.TransformDirToGlobal`
    /// 会按角色朝向再变换一次）。我们自己推位移时是在世界坐标里动，所以必须先把
    /// "角色前方"换算到全局，再在全局平面里平分旋转 —— 否则角色朝左时八个方向会整体错位。
    /// </summary>
    private static Vector2[] BuildGlobalDirs(GamePlay.ActorBase caster, int n, float startDeg, bool moveMode)
    {
        var res = new Vector2[n];
        float fx = 1f, fy = 0f;
        try
        {
            if (caster != null)
            {
                var g = caster.TransformDirToGlobal(new Fp2((Fp)1f, (Fp)0f));
                float gx = (float)g.x, gy = (float)g.y;
                float len = (float)Math.Sqrt(gx * gx + gy * gy);
                if (len > 1e-6f) { fx = gx / len; fy = gy / len; }
            }
        }
        catch { }

        for (int k = 0; k < n; k++)
        {
            float x = fx, y = fy;
            Rot2(ref x, ref y, (startDeg + 360f * k / n) * Math.PI / 180.0);
            res[k] = new Vector2(x, y);
        }
        return res;
    }

    private static bool IsOff(string s) =>
        string.IsNullOrWhiteSpace(s) || s.Trim().Equals("Off", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Move 模式的核心：每帧把八个环沿各自的全局方向推出去。
    ///
    /// 为什么不靠动作自己飞
    /// ────────────────────
    /// 靠动作飞只有两个办法，实测都翻车：
    ///   1) 旋转 dir —— 视觉朝向也由 dir 推出来，环会翻出平面变成"躺着的"；
    ///   2) 换个会飞的 action(x3) —— 那是另一条弹幕，表现成了"普通的发射纹章"，不是那个大环。
    ///
    /// 而位移是 `ActorBase.Position` 上的一个可写属性，跟视觉朝向完全解耦。
    /// 所以让八个环用**和中间那个一模一样的 action 与 dir** 生成（视觉一字不差、
    /// 生命周期天然等同），位移我们自己推 —— 两边的诉求就都满足了。
    ///
    /// 推力只持续 `MoveSeconds` 秒就撒手，之后环恢复由动作自己管，
    /// 于是"飞出去"和"原地的绽放/追加攻击"两段都有。
    /// </summary>
    private static void MoveTick(float dt)
    {
        if (_movers.Count == 0) return;
        float cfgSpeed = CfgSpeed?.Value ?? 6f;

        for (int i = _movers.Count - 1; i >= 0; i--)
        {
            var m = _movers[i];
            try
            {
                if (m.Ring == null || m.TimeLeft <= 0f) { _movers.RemoveAt(i); continue; }

                // ⚠ 弹幕是【池化复用】的（BulletMgr.GetFromPoolOrCreate / RecycleBullet）。
                //   我们持有的引用在弹幕死掉之后可能已经指向池里被复用的另一条弹幕，
                //   继续写 Position 就会把无辜的弹幕瞬移走。
                //
                //   但这条检查【默认关】—— 实测打开之后八个环全变成朝面朝方向飞，
                //   说明池里拿出来的弹幕刚创建时 IsDead 还带着上一世的残留值，
                //   结果所有 mover 在第一个 tick 就被误杀了。
                //   现在的防误伤手段改为：限制 TimeLeft（见 PinSeconds/MoveSeconds 的说明）。
                if (CfgCheckDead?.Value == true && IsDead(m.Ring)) { _movers.RemoveAt(i); continue; }

                var p = m.Ring.Position;
                if (!m.Captured) { m.At = p; m.Captured = true; }

                // ★★ 复用检测（2026-10-04 实测依据）：
                //   我们每帧写的是【绝对位置】，而游戏自己的位移每帧只有零点几个单位。
                //   如果读到的位置离【我们上一次写的】差了 2.5 个单位以上，
                //   那基本不可能是同一条弹幕 —— 是池把它回收后又发给了别人。
                //   此时必须立刻撒手，否则会把【新生成】的那条拽到旧轨迹上 ⇒ 看着就是"剑气丢了"。
                //   证据：22 次生成只拿到 14 个不同对象（有一个被复用 5 次），
                //   而当时同时有 4~6 个 mover 还活着。
                if (m.Wrote && !m.Pin)
                {
                    Vector2 d = new Vector2(p.x - m.Last.x, p.y - m.Last.y);
                    if (d.sqrMagnitude > 2.5f * 2.5f)
                    {
                        Plugin.Log?.LogInfo($"[纹章接管:mover] 对象已被池复用（位置跳了 {Math.Sqrt(d.sqrMagnitude):F1}）" +
                                            $" —— 撒手，不再推它 [剩 {_movers.Count - 1}]");
                        _movers.RemoveAt(i);
                        continue;
                    }
                }

                if (m.Pin)
                {
                    // 钉住：每帧把位置【赋值】回原位。
                    // 注意必须是赋值而不是"加上零位移" —— 游戏自己会推它，加上零等于没拦。
                    p.x = m.At.x; p.y = m.At.y;
                }
                else
                {
                    // ⚠ 必须是【绝对定位】，不能是"每帧叠加一点位移"。
                    //
                    // 原因：环用的 action(x2) 自己就会朝面朝方向移动 —— 这正是中间那个
                    // 原版环会跑、需要钉住的原因。如果这里只做叠加，动作自身的位移照样
                    // 生效，我们那点向外推力就被盖过去，表现成"八个环还是朝面朝方向移动"。
                    //
                    // 改成"位置 = 基准点 + 方向 × 速度 × 已经推了多久"，动作想怎么推都白搭。
                    m.Elapsed += dt;
                    float speed = m.Speed > 0f ? m.Speed : cfgSpeed;
                    p.x = m.At.x + m.Dir.x * speed * m.Elapsed;
                    p.y = m.At.y + m.Dir.y * speed * m.Elapsed;
                }
                m.Ring.Position = p;
                m.Last = p; m.Wrote = true;

                m.TimeLeft -= dt;
                if (m.TimeLeft <= 0f)
                {
                    // 推到极限了。默认【就地钉住】而不是撒手 ——
                    // 撒手之后动作(x2 本身会朝面朝方向移动)立刻接管，环就会继续往前飘，
                    // 表现为"到了极限还是往面朝方向走"。
                    if (m.Pin || CfgHoldAfterMove?.Value == false)
                    {
                        _movers.RemoveAt(i);
                    }
                    else
                    {
                        m.Pin = true;
                        m.At = new Vector2(p.x, p.y);   // 停在到达的地方
                        m.Captured = true;
                        float hs = CfgHoldSeconds?.Value ?? 3f;
                        m.TimeLeft = hs > 0f ? hs : 3600f;
                        _movers[i] = m;
                    }
                }
                else _movers[i] = m;
            }
            catch { _movers.RemoveAt(i); }
        }
    }

    /// <summary>
    /// 扶正：把每个环的渲染器朝向照抄「正中间那个原生纹章」。
    ///
    /// 为什么需要
    /// ──────────
    /// 本作是**3D 渲染的 2D 格斗**，弹幕的网格朝向也是从 `dir` 推出来的。
    /// 而游戏自己那 26 条 CreateBullet 指令**清一色 `dir_x:1, dir_y:0`** ——
    /// 也就是说这条环的设计里从来没走过斜的或朝上的方向。
    /// 我们一旋转 dir，环就跟着转出平面，变成"躺着的"。
    ///
    /// 中间那个环是游戏原生生成的，朝向必然正确，所以直接拿它当标准答案：
    /// 两边是同一个 prefab，`GetComponentsInChildren<Renderer>` 的顺序一致，
    /// 按序号一一对应抄世界旋转即可。**只动渲染器的旋转，不动弹幕本体**，
    /// 所以飞行方向不受影响。
    ///
    /// 为什么要连续补若干帧：动作状态机可能在接下来的若干帧里重新写朝向，
    /// 补一次会被覆盖掉。默认补 6 帧（本作逻辑帧 30fps）。
    /// </summary>
    private static void UprightTick()
    {
        if (_fixes.Count == 0) return;
        bool zOnly = (CfgUpright?.Value ?? "Visual").Trim().Equals("VisualZ", StringComparison.OrdinalIgnoreCase);

        for (int i = _fixes.Count - 1; i >= 0; i--)
        {
            var f = _fixes[i];
            try
            {
                if (f.Ring == null) { _fixes.RemoveAt(i); continue; }

                // ---- 模式一：按角度偏转 Renderer（AttachBullets 第 5 段）----
                //   注意是【基础 Z + Deg】，不是直接赋值 —— 用户明确要求"额外加参数、不覆盖现有旋转"。
                //   基础值在登记后的第一帧捕获（那一刻动作刚把朝向摆好）。
                if (f.UseAngle)
                {
                    var rta = VisualOf(f.Ring);
                    if (rta == null) { _fixes.RemoveAt(i); continue; }

                    // ★★★ 换主人检测（这是"不污染别人"的关键）。
                    //   弹幕池化复用 → 同一个 BulletObj 指针下一条弹幕会换动作名。
                    //   动作名一变就说明手里这条已经不是我么那条了：**先复位再收手**。
                    //   有了它，保持时间才敢拉长（否则拉长就是拿别人的纹章去转）。
                    string actNow = null;
                    try { actNow = f.Ring.ActionMgr?.CurrentActionName; } catch { }
                    if (!string.IsNullOrEmpty(f.WantAct) && !string.IsNullOrEmpty(actNow) &&
                        !string.Equals(actNow, f.WantAct, StringComparison.OrdinalIgnoreCase))
                    {
                        try { rta.localEulerAngles = new Vector3(rta.localEulerAngles.x, rta.localEulerAngles.y, f.BaseZ); } catch { }
                        _fixes.RemoveAt(i);
                        continue;
                    }
                    var re = rta.localEulerAngles;
                    if (!f.Captured) { f.BaseZ = re.z; f.Captured = true; }
                    rta.localEulerAngles = new Vector3(re.x, re.y, f.BaseZ + f.Deg);

                    // 诊断：证明"到底写没写、写进去多少、写的是哪条"。
                    // 没有它的话，"没生效"和"根本没执行"看起来一模一样（本项目的头号坑）。
                    if (_angLogged < 10)
                    {
                        _angLogged++;
                        float after = 0f;
                        try { after = rta.localEulerAngles.z; } catch { }
                        Plugin.Log?.LogInfo($"[视觉偏转] \"{f.WantAct}\" baseZ={f.BaseZ:F1} + {f.Deg:F1} " +
                                            $"= {(f.BaseZ + f.Deg):F1}  (写后读回 {after:F1}, " +
                                            $"act={actNow ?? "?"}, 剩 {f.FramesLeft} 帧)");
                    }

                    f.FramesLeft--;
                    if (f.FramesLeft <= 0)
                    {
                        // ★★★ 必须【把角度还回去】。
                        //   弹幕是**池化复用**的（BulletMgr.RecycleBullet / GetFromPoolOrCreate）：
                        //   我们留在 Renderer 上的偏转，会跟着这条弹幕被分给下一个使用者，
                        //   表现成"别的纹章 / 剑气莫名其妙错位"——而且越是在弹幕生灭快的场合
                        //   （空中高速飞行）越容易撞上。
                        //   用户早就报过这个 bug（"空中高速飞行释放魔改纹章解放后投射体全错位"），
                        //   根因就是这里 —— UprightTick 的八个环也写 Renderer，同样没复位。
                        try { rta.localEulerAngles = new Vector3(re.x, re.y, f.BaseZ); } catch { }
                        _fixes.RemoveAt(i);
                    }
                    else _fixes[i] = f;
                    continue;
                }

                if (f.Center == null) { _fixes.RemoveAt(i); continue; }

                var ct = VisualOf(f.Center);
                var rt = VisualOf(f.Ring);
                if (ct == null || rt == null) { _fixes.RemoveAt(i); continue; }

                if (zOnly)
                {
                    // 用户提的「只改 Z 轴」：把 X/Y 的倾角借中心的，Z 的自旋保留自己的。
                    // 这样环还朝各自的飞行方向转，但不会翻出平面。
                    var ce = ct.localEulerAngles;
                    var re = rt.localEulerAngles;
                    rt.localEulerAngles = new Vector3(ce.x, ce.y, re.z);
                }
                else
                {
                    // 整体照抄中心那个原生纹章的视觉朝向 —— 它必然是对的。
                    rt.rotation = ct.rotation;
                }

                f.FramesLeft--;
                if (f.FramesLeft <= 0) _fixes.RemoveAt(i);
                else _fixes[i] = f;
            }
            catch { _fixes.RemoveAt(i); }
        }
    }

    /// <summary>
    /// 拿到一个 Actor 的视觉根节点。
    ///
    /// ⚠ `GamePlay.ActorBase` 【不是 MonoBehaviour】—— 它没有任何 Unity 那边的东西，
    ///   所以 `bullet.transform` / `GetComponentsInChildren` 全都不存在(编译期就会报错)。
    ///   作用角色是**战斗逻辑实体**，视觉挂在它的成员上：
    ///       ActorBase.ActorModel       (0x30) -> ActorModel.VisualTransform  (0x28)  ← 用这个
    ///       ActorBase.SmoothObject     (0x28) -> VisualObject.m_transform    (0x10)
    ///   游戏自己就是往 VisualTransform 上写朝向的，所以扳它最对症。
    ///   走反射而不是强类型，免得 interop 里类型解析出岔子。
    /// </summary>
    private static Transform VisualOf(GamePlay.BulletObj b)
    {
        try
        {
            var model = ReadMember(b, _actorModelMember);
            if (model == null) return null;
            return ReadMember(model, _visualTransformMember) as Transform;
        }
        catch { return null; }
    }

    private static MemberInfo _actorModelMember;
    private static MemberInfo _visualTransformMember;
    private static MemberInfo _isDeadMember;
    private static bool _noDeadCheck;

    /// <summary>
    /// 弹幕是否已死。拿不到这个属性时返回 false（乐观放行）——
    /// 宁可多推一帧，也不要因为读不到就把整个功能停掉。
    /// </summary>
    private static bool IsDead(GamePlay.BulletObj b)
    {
        if (_noDeadCheck || _isDeadMember == null) return false;
        try
        {
            var v = ReadMember(b, _isDeadMember);
            return v is bool bb && bb;
        }
        catch { return false; }
    }

    private static void ResolveVisualMembers()
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var ab = AccessTools.TypeByName("GamePlay.ActorBase");
        if (ab != null)
            _actorModelMember = (MemberInfo)ab.GetField("ActorModel", F) ?? ab.GetProperty("ActorModel", F);

        var am = AccessTools.TypeByName("GamePlay.ActorModel");
        if (am != null)
            _visualTransformMember = (MemberInfo)am.GetField("VisualTransform", F)
                                     ?? am.GetProperty("VisualTransform", F);

        if (ab != null)
        {
            _isDeadMember = (MemberInfo)ab.GetProperty("IsDead", F) ?? ab.GetField("IsDead", F);
            if (_isDeadMember == null)
            {
                _noDeadCheck = true;
                Plugin.Log?.LogWarning("  [纹章接管] 拿不到 ActorBase.IsDead —— 弹幕池化复用时" +
                                       "可能误伤别的弹幕。请把 PinSeconds/MoveSeconds 调小。");
            }
        }
    }

    private static object ReadMember(object obj, MemberInfo mi)
    {
        if (obj == null || mi == null) return null;
        try
        {
            switch (mi)
            {
                case PropertyInfo p: return p.GetValue(obj);
                case FieldInfo f: return f.GetValue(obj);
            }
        }
        catch { }
        return null;
    }
}
