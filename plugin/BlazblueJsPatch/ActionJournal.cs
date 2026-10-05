using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BlazblueJsPatch;

/// <summary>
/// 动作时序记录器 —— 一次跑完，产出一张「招式 ↔ 内部动作名」对照表。
///
/// 为什么要它
/// ──────────
/// 上一轮把 ES 的招式名查出来了（`ActorActionName_340061` = 纹章解放、`340081` = 崔斯坦 …），
/// 但**内部动作名（`holdEX` / `a3` / `aup` / `DAA` 这一堆）跟招式名对不上**。
/// 而后面所有改动（额外纹章、额外剑气、打断冲刺）都要靠内部动作名去匹配。
///
/// 逐个试太慢，所以这里改成：**你按顺序把招式打一遍、每招之间停一下**，
/// 记录器按时间顺序把每次动作切换打出来，并用「空档」自动分段。
/// 分段之后，"这一段的动作名" 就对应 "你刚按的那一招"。
///
/// 顺带把每个动作的完整档案也 dump 出来
/// ────────────────────────────────────
/// `ActionMgr` 上有公开访问器：
///     public ActionLogicGroup ActionGroup { get; }        // 整个动作组
///     public GameActionLogic  CurrentAction { get; set; } // 当前动作的完整数据
/// 而 `GameActionLogic` 带着：
///     RoleName / Animate / TotalDuration / NextAction / DropAction / DeadAction /
///     **Interrupt**（谁能打断它） / ActionSwitchs / CDList / AddEffects / HitDataList
/// 其中 **Interrupt 就是「冲刺打断冲刺」的数据层解法** —— 不用去 patch
/// `CheckCanChangeToAction`，直接看冲刺动作的 Interrupt 里有没有它自己。
///
/// 用法
/// ────
/// 1. `Enabled = true`
/// 2. 进训练场，**一招一招打，每招之间停 1~2 秒**（停够 `GapFrames` 就会打一条分隔线）
/// 3. 打完把日志发我，我按分隔线切段生成对照表
/// </summary>
internal static class ActionJournal
{
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;
    internal static BepInEx.Configuration.ConfigEntry<int> CfgGapFrames;
    internal static BepInEx.Configuration.ConfigEntry<int> CfgCap;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgProfile;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgSkillProbe;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgChain;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgInputProbe;

    // ---- 链路追踪 ----
    // 把「输入 → 技能 → 动作 → 弹幕」这条链的【每一层】都挂上, 用同一个帧计数打出来,
    // 日志就会自然按层缩进排成一条链, 不用再盲猜哪一步对不上。
    //
    // 各层入口:
    //   输入   TouchButtonStyleController.RecordSkill(InputCmd, TouchCriteria, PlayerSkill)
    //          —— 属于 UI 类, 正常游玩不一定触发; 触发不了就靠下面几层反推
    //   门槛   PlayerSkillMgr.GetSkillCastRequires(PlayerSkill, PlayerObj, out…)
    //   技能   PlayerSkillMgr.SkillStartImplement(PlayerSkill, Fp2, PlayerSkillChain)
    //          PlayerSkillMgr.onSkillChangedPostProc(old, new, chain)
    //   动作   ActionMgr.ChangeAction(string)
    //   弹幕   BulletMgr.createBulletImp(...)
    //
    // InputCmd 枚举: Attack=1, Skill=3, Ultra=4, Summon=5, Dash=55, Jump=800
    private static int _chainLines;

    // ---- 技能探针 ----
    // PlayerSkill 上带着我们需要的全部身份信息:
    //     public int SkillId;        // 0x18  ★ 技能 id (340081=崔斯坦 / 340281=布鲁诺 …)
    //     public int ActorId;        // 0x1C
    //     public int SourceTriggerId;// 0x40
    //     public InputCmd Input;     // 0x44  输入指令
    //     public PlayerSkillGroup SkillType { get; }   // 技能类型(可能就是 Slasher/Shooter/Assaulter)
    // 拿到它的正确途径是 PlayerSkillMgr.FindSkillAndChainByActionName(actionName, out chain, out skill)
    // —— 这个方法本身就是「动作名 → 技能对象」的映射, 正是缺的那一环。
    private static readonly HashSet<string> _skillPairs = new HashSet<string>();
    private static MemberInfo _skId, _skActor, _skTrigger, _skInput, _skType, _skActivate;

    private static int _frame;          // 帧计数（由心跳推进）
    private static int _lastEventFrame;
    private static int _seq;
    private static int _lines;
    private static bool _gapOpen;       // 是否已经处于"空档"状态，避免重复打分隔线
    private static readonly HashSet<string> _profiled = new HashSet<string>();

    // ---- 反射缓存 ----
    private static MemberInfo _ownerMember, _curActionMember, _actionGroupMember;
    private static Type _galType;
    private static FieldInfo _fName, _fRole, _fAnimate, _fDuration, _fNext, _fDrop, _fDead,
                            _fInterrupt, _fAddEffects, _fHitData, _fType;

    public static int Apply(Harmony harmony)
    {
        var t = AccessTools.TypeByName("GamePlay.ActionMgr");
        if (t == null) { Plugin.Log?.LogWarning("  [动作记] 找不到 GamePlay.ActionMgr"); return 0; }

        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _ownerMember = (MemberInfo)t.GetProperty("Owner", F) ?? t.GetField("Owner", F);
        _curActionMember = (MemberInfo)t.GetProperty("CurrentAction", F) ?? t.GetField("CurrentAction", F);
        _actionGroupMember = (MemberInfo)t.GetProperty("ActionGroup", F) ?? t.GetField("ActionGroup", F);

        _galType = AccessTools.TypeByName("GamePlay.GameActionLogic") ?? FindGalType();
        if (_galType != null)
        {
            _fName = _galType.GetField("Name", F);
            _fRole = _galType.GetField("RoleName", F);
            _fAnimate = _galType.GetField("Animate", F);
            _fDuration = _galType.GetField("TotalDuration", F);
            _fNext = _galType.GetField("NextAction", F);
            _fDrop = _galType.GetField("DropAction", F);
            _fDead = _galType.GetField("DeadAction", F);
            _fInterrupt = _galType.GetField("Interrupt", F);
            _fAddEffects = _galType.GetField("AddEffects", F);
            _fHitData = _galType.GetField("HitDataList", F);
            _fType = _galType.GetField("ActionType", F);
        }

        int n = 0;
        try
        {
            var m = AccessTools.Method(t, "ChangeAction");
            harmony.Patch(m, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(ActionJournal), nameof(ChangePostfix))));
            Plugin.Log.LogInfo("  [动作记] 已挂钩 ActionMgr.ChangeAction");
            n++;
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [动作记] 挂钩失败: {e.Message}"); }

        // 心跳：推进帧计数 + 检测空档
        var bm = AccessTools.TypeByName("GamePlay.BulletMgr");
        var val = bm == null ? null : AccessTools.Method(bm, "ValidateBullets");
        if (val != null)
        {
            try
            {
                harmony.Patch(val, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(ActionJournal), nameof(Tick))));
                Plugin.Log.LogInfo("  [动作记] 已挂钩 BulletMgr.ValidateBullets (心跳/分段)");
                n++;
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"  [动作记] 挂心跳失败: {e.Message}"); }
        }

        // ---- 技能探针: 一次挂多个候选入口, 谁被调用就用谁 ----
        // 上一版只挂了 FindSkillAndChainByActionName, 结果零命中 —— 它在实际游玩中不被调用。
        // 所以这里改成一网打尽: 把"可能被调到的"几个入口全挂上, 日志里看哪个先动。
        ResolveSkillMembers();
        n += PatchSkillHooks(harmony);
        n += PatchChainHooks(harmony);
        n += PatchInputHooks(harmony);

        Plugin.Log.LogInfo($"  [动作记] 生效配置: Enabled={CfgEnabled?.Value} " +
                           $"GapFrames={CfgGapFrames?.Value} Profile={CfgProfile?.Value} " +
                           $"SkillProbe={CfgSkillProbe?.Value} Cap={CfgCap?.Value}");
        return n;
    }

    // ------------------------------------------------------------------ 空中链记录（崔斯坦验证用）

    // 把一次「空中连段」里的动作按顺序编号记下来，进出各打一条边界。
    // 目的：验证「下落 → 踩踏 → 弹起 ×3 → 落地」这个模型对不对，
    //       以及确认各段到底对应哪个动作名（尤其那两个从没出现过的 fallupdd / fallup2dd）。
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgAirChain;

    private static readonly List<string> _airBuf = new List<string>();
    private static float _airStartT = -1f;
    private static float _airLastT = -1f;
    private static float _airPrevT = -1f;
    private static bool _airActive;

    /// <summary>真实秒表。⚠ 不要用 _frame 当时间轴 ——
    /// `BulletMgr.ValidateBullets`（心跳）的真实调用频率远高于 30Hz 逻辑帧，
    /// 拿帧数除以 30 算出来的一整套崔斯坦连段要 64 秒，明显荒谬。
    /// 凡是"隔了多久"的判断和显示，一律走这里。</summary>
    private static float Now()
    {
        try { return UnityEngine.Time.realtimeSinceStartup; } catch { return 0f; }
    }

    /// <summary>只有【开窗】用这个判据 —— 开窗之后不管什么名字都记。
    /// ⚠ 这里绝不能当过滤器用：崔斯坦的"踩踏"到底叫什么名字我们并不知道,
    ///   如果只记 fall* 名字, 那个关键帧会被静默丢掉, 然后得出"没有踩踏动作"的错误结论。
    ///   (本项目已经在"关键字过滤误伤"上栽过好几次了)</summary>
    private static bool IsAirChain(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        return n.StartsWith("fall") || n.StartsWith("atkAir") || n.StartsWith("attackAir")
            || n.StartsWith("dashAir") || n.StartsWith("AttackUp") || n.StartsWith("aup")
            || n.StartsWith("push_back") || n.StartsWith("AD_hit");
    }

    /// <summary>链尾判据：这个动作出现 = 这一轮空中连段结束了。</summary>
    private static bool IsAirEnd(string n)
    {
        return n.StartsWith("fallmdownend") || n == "fallend" || n == "fallend2"
            || n == "drop3" || n == "stand" || n.StartsWith("idle");
    }

    /// <summary>这一段算"第几段"——纯粹按名字判，用来跟你的描述对照。
    /// 括号里是 skillactivate 静态表里的槽位号(按键=无/派生, 方向=Any, 姿态=空中 的 4.x 段)。</summary>
    private static string AirLabel(string n)
    {
        if (n == "fall") return "下落 4.1";
        if (n == "fallupdd") return "★踩踏1-dd 4.2(没出现过)";
        if (n == "fallupd") return "踩踏1-d 4.3";
        if (n == "fallup") return "弹起1 (无槽)";
        if (n == "fallup22") return "踩踏2-2 4.4";
        if (n == "fallup2dd") return "★踩踏2-dd 4.5(没出现过)";
        if (n == "fallup2d") return "踩踏2-d 4.6";
        if (n == "fallup2") return "弹起2 (无槽)";
        if (n == "fallup3") return "弹起3 (无槽)";
        if (n == "fallend") return "落地 4.7";
        if (n == "fallend2") return "落地2 4.8";
        if (n == "fallm") return "弹完下坠 4.9";
        if (n == "fallmdown") return "下砸 4.10";
        if (n == "fallmdownend") return "砸地 4.11";
        if (n == "atkAir12") return "空中攻击12 4.12";
        if (n == "AttackUp2") return "空中上攻击 4.13";
        if (n == "atkAir3") return "空中攻击3 4.14";
        if (n == "fallmdownendEX") return "砸地EX 4.15";
        if (n == "fallmdownendEX2") return "砸地EX2 4.16";
        if (n.StartsWith("atkAir") || n.StartsWith("attackAir")) return "空中攻击";
        if (n.StartsWith("dashAir")) return "空中冲刺";
        if (n.StartsWith("push_back")) return "(受击后退)";
        return n;
    }

    private static void AirChainFeed(string name)
    {
        if (CfgAirChain?.Value != true) return;
        float t = Now();

        // 还没开窗：只有遇到空中动作才开。开窗后【什么都不过滤】。
        if (!_airActive)
        {
            if (!IsAirChain(name)) return;
            _airActive = true;
            _airBuf.Clear();
            _airStartT = t;
            _airPrevT = t;
            Plugin.Log?.LogInfo("\n[空中链] ══════ 开始 t=" + t.ToString("F2") + " ══════");
        }

        _airBuf.Add(name);
        _airLastT = t;

        // ⚠ 本 SDK 的 DefaultInterpolatedStringHandler 不支持【负】对齐({x,-7}) ——
        //   会报 CS1739 "AppendFormatted 没有名为 alignment 的参数"。左对齐自己算。
        string seq = ("#" + _airBuf.Count).PadLeft(3);
        string tm = ("t=" + t.ToString("F2")).PadRight(10);
        string lbl = AirLabel(name).PadRight(24);
        float since = _airBuf.Count > 1 ? t - _airPrevT : 0f;
        _airPrevT = t;
        Plugin.Log?.LogInfo("[空中链] " + seq + " " + tm + lbl +
                            " (+" + since.ToString("F2") + "s) \"" + name + "\"");

        if (IsAirEnd(name)) AirChainClose("落地动作 \"" + name + "\"");
    }

    private static void AirChainClose(string why)
    {
        if (!_airActive) return;
        float dur = _airLastT - _airStartT;
        // 整条链的原样序列，方便一眼对照"段落模型"
        Plugin.Log?.LogInfo("[空中链] ══════ 结束(" + why + ") 共 " + _airBuf.Count +
                            " 个动作, 用时 " + dur.ToString("F2") + " 秒 ══════");
        Plugin.Log?.LogInfo("[空中链]   序列: " + string.Join(" → ", _airBuf.ToArray()));
        Plugin.Log?.LogInfo("[空中链] ══════════════════════════════════════════════════════\n");
        _airBuf.Clear();
        _airStartT = -1f;
        _airActive = false;
    }

    /// <summary>由心跳调用：太久没有动作切换 = 这一轮连段结束了。
    /// 用真秒表判，不用帧数 —— 心跳频率和逻辑帧不是一回事。</summary>
    private static void AirChainTick()
    {
        if (!_airActive) return;
        float idle = Now() - _airLastT;
        if (idle > 1.5f) AirChainClose("静默 " + idle.ToString("F2") + " 秒");
    }

    // ------------------------------------------------------------------ 原始输入探针

    // 挂 IInput.OnInputCommand(InputCmd cmd, Fp2 dir) —— 实现类是 GamePlay.PlayerInput。
    //
    // 这是【最底层】的输入点: 每一个输入指令都会过这里, **不管后面有没有成功出招**。
    // 所以它能把"没按"和"按了但被吞"彻底分开 —— 这正是之前分不清的地方
    // (某局日志里 Attack 键一次都没出现, 但跳跃出现了 17 次, 无从判断是哪种)。
    private static readonly Dictionary<string, int> _inputCount = new Dictionary<string, int>();

    private static int PatchInputHooks(Harmony harmony)
    {
        int n = 0;
        var post = AccessTools.Method(typeof(ActionJournal), nameof(InputPostfix));
        // ⚠ 目标方法名不是 OnInputCommand —— 那个只在 MonsterInput 上。
        //   PlayerInput 上的入口是 OnInputCmdChange(int playerId, InputCmd cmd, bool pressed, Fp2 dir)。
        //   只挂 PlayerInput：怪物那边调用量极大，对我们没用。
        var t = AccessTools.TypeByName("GamePlay.PlayerInput") ?? AccessTools.TypeByName("PlayerInput");
        if (t == null) { Plugin.Log?.LogWarning("  [输入探针] 找不到 GamePlay.PlayerInput"); return 0; }

        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic |
                               BindingFlags.Static | BindingFlags.Instance;
        foreach (var m in t.GetMethods(F))
        {
            if (m.Name != "OnInputCmdChange") continue;
            try
            {
                harmony.Patch(m, postfix: new HarmonyMethod(post));
                Plugin.Log.LogInfo($"  [输入探针] 已挂钩 {t.FullName}.OnInputCmdChange" +
                                   $"({string.Join(",", Array.ConvertAll(m.GetParameters(), x => x.ParameterType.Name))})");
                n++;
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"  [输入探针] 挂 OnInputCmdChange 失败: {e.Message}"); }
        }
        if (n == 0) Plugin.Log?.LogWarning("  [输入探针] 没找到 PlayerInput.OnInputCmdChange");
        return n;
    }

    /// <summary>
    /// 原始输入回调：只按 cmd 去重计数，前几次全打，之后每 50 次打一次计数。
    ///
    /// ⚠ 参数必须【强类型】，绝不能改用 `object[] __args`。
    ///   `OnInputCommand(InputCmd cmd, Fp2 dir)` 的第二个参数 `Fp2` 是**结构体**，
    ///   Il2CppInterop 装箱时给不出合法指针，对它调 `.ToString()` 会【直接踩空指针把游戏搞崩】。
    ///   （这个坑刚踩过一次，实测崩游戏。）
    ///   所以这里只按名字绑定 `cmd` 这一个参数，方向参数一个字节都不碰。
    /// </summary>
    /// <summary>
    /// 从一个 IL2CPP 对象上【裸读】两个 long（不经过反射装箱）。
    ///
    /// 为什么必须这样：`InputCmdState.CurDir` 是 `Fp2` 结构体，
    /// 用 `FieldInfo.GetValue` 读它会把结构体装箱 —— 而 IL2CPP 的装箱在 Il2CppInterop 下
    /// 会拿到非法指针，**直接崩游戏**（这个坑已经踩过一次）。
    /// 但结构体本身是**平坦内存**：`Fp2 { Fp x; Fp y }`、`Fp { long value }`，
    /// 所以直接按偏移读 long 又安全又快。
    /// 偏移来自 dump.cs：InputCmdState.CurDir @0x18，x.value@0x18 / y.value@0x20。
    /// </summary>
    private static bool RawPair(object il2cppObj, int offX, int offY, out long x, out long y)
    {
        x = y = 0;
        try
        {
            var b = il2cppObj as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
            if (b == null) return false;
            IntPtr p = b.Pointer;
            if (p == IntPtr.Zero) return false;
            x = System.Runtime.InteropServices.Marshal.ReadInt64(p, offX);
            y = System.Runtime.InteropServices.Marshal.ReadInt64(p, offY);
            return true;
        }
        catch { return false; }
    }

    /// <summary>取某个按键的输入状态对象（IInput.GetCmdStatus(InputCmd) -> InputCmdState）。</summary>
    private static MethodInfo _getCmdStatus;
    private static object GetCmdState(GamePlay.PlayerInput pi, GamePlay.InputCmd cmd)
    {
        try
        {
            if (_getCmdStatus == null)
                _getCmdStatus = AccessTools.Method(pi.GetType(), "GetCmdStatus");
            return _getCmdStatus?.Invoke(pi, new object[] { cmd });
        }
        catch { return null; }
    }

    public static void InputPostfix(GamePlay.PlayerInput __instance, GamePlay.InputCmd cmd, bool pressed)
    {
        try
        {
            if (CfgInputProbe?.Value != true) return;
            if (!pressed) return;   // pressed=false 是松手，日志只留"按下去"那一半

            // ⚠ 方向用【强类型参数】拿，不要用 __args 装箱 —— 后者会在 IL2CPP 上踩空指针崩游戏。
            //   强类型参数由 Harmony 直接编组，EsEmblemBurst 里一直这么用，是安全的。
            // 方向不在回调参数里（实测恒为 0），改从【输入状态对象】裸读。
            string d;
            try
            {
                var st = GetCmdState(__instance, cmd);
                d = (st != null && RawPair(st, 0x18, 0x20, out long rx, out long ry))
                    ? $"dir({rx},{ry})"
                    : "(状态读不到)";
            }
            catch (Exception ex) { d = "读方向失败:" + ex.GetType().Name; }

            string key = cmd + d;
            _inputCount.TryGetValue(key, out int c);
            c++;
            _inputCount[key] = c;

            if (c > 3 && c % 50 != 0) return;
            if (_inputLines >= (CfgCap?.Value ?? 4000)) return;
            _inputLines++;

            string indent = new string(' ', 4);
            string fr = _frame.ToString().PadLeft(7);
            Plugin.Log?.LogInfo($"[输入 f={fr}] {indent}cmd={key}   (第 {c} 次)");
        }
        catch { }
    }

    private static int _inputLines;

    // ------------------------------------------------------------------ 链路追踪

    /// <summary>按层缩进打一行链路日志。depth 越小越靠上游。</summary>
    private static void Chain(int depth, string msg)
    {
        if (CfgChain?.Value != true) return;
        int cap = CfgCap?.Value ?? 4000;
        if (_chainLines >= cap * 3) return;
        _chainLines++;
        // 缩进/对齐都先算成变量再插值 —— 直接写 `{new string(...)}` 会被当成对齐说明符，编译不过；
        // `{_frame,7}` 在这套 SDK 下也报 AppendFormatted 不认 alignment，统一避开。
        string indent = new string(' ', depth * 2);
        string fr = _frame.ToString().PadLeft(7);
        Plugin.Log?.LogInfo($"[链路 f={fr}] {indent}{msg}");
    }

    /// <summary>输入指令 + 技能身份，拼成一行摘要。</summary>
    private static string SkillBrief(object skill)
    {
        if (skill == null) return "(skill=null)";
        try
        {
            var sa = ReadMember(skill, _skActivate);
            var sb = new StringBuilder();
            sb.Append($"skill.SkillId={Int(skill, _skId)}");
            if (sa != null)
            {
                sb.Append($" Action=\"{Str(sa, "Action")}\"");
                sb.Append($" 按键={Str(sa, "Input")}/{Str(sa, "InputDir")}");
                sb.Append($" 槽={Int2(sa, "Group")}.{Int2(sa, "Order")}");
            }
            return sb.ToString();
        }
        catch { return "(读技能失败)"; }
    }

    private static int PatchChainHooks(Harmony harmony)
    {
        int n = 0;
        var post = AccessTools.Method(typeof(ActionJournal), nameof(ChainPostfix));

        // (类型名, 方法名) —— 挂不上的记一条日志, 不致命
        var targets = new (string Type, string Method)[]
        {
            ("TouchButtonStyleController", "RecordSkill"),        // 输入记录点(UI 类, 可能不触发)
            ("GamePlay.PlayerSkillMgr", "GetSkillCastRequires"),  // 施放门槛
            ("GamePlay.PlayerSkillMgr", "CheckSkillCanCast"),     // 施放检查
        };

        foreach (var (tn, mn) in targets)
        {
            var t = AccessTools.TypeByName(tn) ?? AccessTools.TypeByName("GamePlay." + tn);
            if (t == null) { Plugin.Log?.LogWarning($"  [链路] 找不到类型 {tn}"); continue; }
            int hit = 0;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.Static | BindingFlags.Instance))
            {
                if (m.Name != mn) continue;
                try
                {
                    harmony.Patch(m, postfix: new HarmonyMethod(post));
                    hit++; n++;
                }
                catch (Exception e) { Plugin.Log?.LogWarning($"  [链路] 挂 {tn}.{mn} 失败: {e.Message}"); }
            }
            if (hit > 0)
                Plugin.Log.LogInfo($"  [链路] 已挂钩 {t.FullName}.{mn} ({hit} 个重载)");
        }
        return n;
    }

    /// <summary>链路层的通用回调：从参数里挑出 PlayerSkill / InputCmd 打出来。</summary>
    public static void ChainPostfix(object[] __args, System.Reflection.MethodBase __originalMethod)
    {
        try
        {
            if (CfgChain?.Value != true || __args == null) return;
            string where = __originalMethod?.Name ?? "?";

            object skill = null;
            var parts = new List<string>();
            foreach (var a in __args)
            {
                if (a == null) continue;
                string t = a.GetType().Name;
                if (t == "PlayerSkill") { skill = a; continue; }
                if (t == "PlayerObj" || t == "PlayerSkillChain" || t == "ParamSet") continue;
                parts.Add($"{t}={a}");
            }
            Chain(1, $"【{where}】 " + string.Join("  ", parts) +
                    (skill != null ? "   " + SkillBrief(skill) : ""));
        }
        catch { }
    }

    // ------------------------------------------------------------------ 技能探针

    /// <summary>
    /// 解析 PlayerSkill 的成员。
    ///
    /// ⚠ 上一版只试了 `GetField("SkillId")`，结果拿不到 —— Il2CppInterop 经常把字段
    ///   生成成【属性】（或加个 `&lt;X&gt;k__BackingField` 的私有字段）。
    ///   所以这里三种都试一遍，并且把最终找到的成员名打出来，免得再猜。
    /// </summary>
    private static void ResolveSkillMembers()
    {
        try
        {
            var sk = AccessTools.TypeByName("GamePlay.PlayerSkill");
            if (sk == null) { Plugin.Log?.LogWarning("  [技能探针] 找不到 GamePlay.PlayerSkill"); return; }
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            _skId = Pick(sk, "SkillId", F);
            _skActor = Pick(sk, "ActorId", F);
            _skTrigger = Pick(sk, "SourceTriggerId", F);
            _skInput = Pick(sk, "Input", F);
            _skType = Pick(sk, "SkillType", F);
            _skActivate = Pick(sk, "SkillActivate", F);

            Plugin.Log.LogInfo($"  [技能探针] PlayerSkill 成员解析: " +
                               $"SkillId={Desc(_skId)} ActorId={Desc(_skActor)} " +
                               $"Input={Desc(_skInput)} SkillType={Desc(_skType)}");
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [技能探针] 成员解析失败: {e.Message}"); }
    }

    private static string Desc(MemberInfo mi) => mi == null ? "✗" : $"✓({mi.MemberType})";

    /// <summary>字段 / 属性 / 编译器生成的后备字段, 三种名字都试。</summary>
    private static MemberInfo Pick(Type t, string name, BindingFlags F)
    {
        var p = t.GetProperty(name, F);
        if (p != null) return p;
        var f = t.GetField(name, F);
        if (f != null) return f;
        return t.GetField($"<{name}>k__BackingField", F)
            ?? (MemberInfo)t.GetProperty($"{name}_k__BackingField", F);
    }

    /// <summary>
    /// 挂一批"技能开始"的候选入口。
    /// 不论哪个被实际调用，都能拿到 PlayerSkill，从而读出 SkillId。
    /// 用 `object[] __args` 而不是具名参数 —— 具名绑定一旦类型/名字对不上就整个 patch 失败，
    /// 而 __args 永远能绑上，之后在回调里自己从参数里挑出 PlayerSkill。
    /// </summary>
    private static int PatchSkillHooks(Harmony harmony)
    {
        var mgr = AccessTools.TypeByName("GamePlay.PlayerSkillMgr");
        if (mgr == null) { Plugin.Log?.LogWarning("  [技能探针] 找不到 GamePlay.PlayerSkillMgr"); return 0; }

        var post = AccessTools.Method(typeof(ActionJournal), nameof(SkillPostfix));
        int n = 0;
        foreach (var name in new[]
        {
            "SkillStartImplement", "OnSkillStart", "onSkillChangedPostProc",
            "ChangeSkillByActionName", "FindSkillAndChainByActionName",
            "GetSkillCastRequires", "IsSkillReady",
        })
        {
            foreach (var m in mgr.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                             BindingFlags.Static | BindingFlags.Instance))
            {
                if (m.Name != name) continue;
                // 至少要有一个 PlayerSkill 参数, 否则拿不到 id
                bool has = false;
                foreach (var p in m.GetParameters())
                    if (p.ParameterType.Name == "PlayerSkill") { has = true; break; }
                if (!has) continue;
                try
                {
                    harmony.Patch(m, postfix: new HarmonyMethod(post));
                    Plugin.Log.LogInfo($"  [技能探针] 已挂钩 PlayerSkillMgr.{name}" +
                                       $"({string.Join(",", Array.ConvertAll(m.GetParameters(), x => x.ParameterType.Name))})");
                    n++;
                }
                catch (Exception e) { Plugin.Log?.LogWarning($"  [技能探针] 挂 {name} 失败: {e.Message}"); }
            }
        }
        if (n == 0) Plugin.Log?.LogWarning("  [技能探针] 一个入口都没挂上");
        return n;
    }

    /// <summary>通用回调: 从参数里挑出 PlayerSkill 并打身份信息。</summary>
    public static void SkillPostfix(object[] __args, System.Reflection.MethodBase __originalMethod)
    {
        try
        {
            if (CfgSkillProbe?.Value != true || __args == null) return;

            object skill = null;
            foreach (var a in __args)
                if (a != null && a.GetType().Name == "PlayerSkill") { skill = a; break; }
            if (skill == null) return;

            string where = __originalMethod?.Name ?? "?";
            DumpSkillOnce(skill);

            // 真正的身份信息在 SkillActivate 里（= skillactivate 表那一行）。
            // PlayerSkill 自己的 SkillId 实测恒为 0，靠不住。
            var sa = ReadMember(skill, _skActivate);

            string action = Str(sa, "Action");
            string input = Str(sa, "Input");
            string reqTrig = ListInt(sa, "ReqTriggerId");
            int group = Int2(sa, "Group");
            int order = Int2(sa, "Order");
            int bulletId = Int2(sa, "BulletId");
            string bulletAct = Str(sa, "BulletAction");
            string inputDir = Str(sa, "InputDir");

            // ⚠ 去重键必须多字段 —— 上一版只用 (入口|SkillId)，而 SkillId 恒为 0，
            //   结果所有技能都塌成同一行，看起来像"探针没工作"。
            // ⚠ 链路模式下【不能去重】。
            //   去重按 (动作名|按键|槽位) 做 —— 而不同技能的同一段位动作名是一样的
            //   (布鲁诺第3段和崔斯坦第3段都是 attackC)，一旦去重，第二个技能就一条都不打了，
            //   正好把"不同技能的段数验证"这个目的废掉。
            //   所以链路模式下每次都打，靠帧号+缩进体现先后。
            if (CfgChain?.Value == true)
            {
                Chain(1, $"【技能】\"{action}\"  按键={input}/{inputDir} 槽={group}.{order}" +
                         $" ActorId={Int(skill, _skActor)} 类型={ReadMember(skill, _skType)}");
                return;
            }

            string key = $"{where}|{action}|{input}|{group}|{order}|{reqTrig}|{bulletId}|{bulletAct}";
            if (!_skillPairs.Add(key)) return;

            var sb = new StringBuilder();
            sb.Append($"[技能探针:{where}] 动作=\"{action}\" 输入={input}/{inputDir} " +
                      $"槽={group}.{order} 需Trigger=[{reqTrig}]");
            if (bulletId != 0 || !string.IsNullOrEmpty(bulletAct))
                sb.Append($"  ★生成弹幕 id={bulletId} action=\"{bulletAct}\"");
            int act = Int(skill, _skActor);
            if (act != 0) sb.Append($" ActorId={act}");
            var st = ReadMember(skill, _skType);
            if (st != null) sb.Append($" 类型={st}");
            sb.Append($"  (已见 {_skillPairs.Count} 种)");
            Plugin.Log?.LogInfo(sb.ToString());
        }
        catch { }
    }

    /// <summary>第一次拿到 PlayerSkill 时, 把它的成员名列一遍 —— 免得再猜字段叫什么。</summary>
    private static bool _skillMembersDumped;
    private static void DumpSkillOnce(object skill)
    {
        if (_skillMembersDumped) return;
        _skillMembersDumped = true;
        try
        {
            var t = skill.GetType();
            var names = new List<string>();
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                names.Add("f:" + f.Name + "(" + f.FieldType.Name + ")");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                names.Add("p:" + p.Name + "(" + p.PropertyType.Name + ")");
            Plugin.Log?.LogInfo($"[技能探针] PlayerSkill 运行时类型 = {t.FullName}\n    " +
                                string.Join("  ", names));
        }
        catch { }
    }

    private static int Int(object o, MemberInfo mi)
    {
        try
        {
            var v = ReadMember(o, mi);
            return v == null ? 0 : Convert.ToInt32(v);
        }
        catch { return 0; }
    }

    /// <summary>按属性名从任意对象上取 int（SkillActivate 上是属性）。</summary>
    private static int Int2(object o, string name)
    {
        if (o == null) return 0;
        try
        {
            var pi = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            var v = pi != null ? pi.GetValue(o)
                  : o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o);
            return v == null ? 0 : Convert.ToInt32(v);
        }
        catch { return 0; }
    }

    private static string Str(object o, string name)
    {
        if (o == null) return "";
        try
        {
            var pi = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            var v = pi != null ? pi.GetValue(o)
                  : o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o);
            return v?.ToString() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>把 IList&lt;int&gt; 之类的集合拼成逗号串（拿不到就返回空）。</summary>
    private static string ListInt(object o, string name)
    {
        if (o == null) return "";
        try
        {
            var pi = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            var v = pi != null ? pi.GetValue(o) : null;
            var en = v as IEnumerable;
            if (en == null) return "";
            var parts = new List<string>();
            foreach (var x in en) { parts.Add(x?.ToString() ?? "?"); if (parts.Count >= 12) break; }
            return string.Join(",", parts);
        }
        catch { return ""; }
    }

    private static Type FindGalType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts;
            try { ts = asm.GetTypes(); } catch { continue; }
            foreach (var x in ts) if (x.Name == "GameActionLogic") return x;
        }
        return null;
    }

    /// <summary>每帧心跳：推进帧计数；静默太久就打一条分隔线（用来切段）。</summary>
    public static void Tick()
    {
        // 帧计数与空中链收尾【独立于 CfgEnabled】—— 空中链是个单独的开关,
        // 关掉总记录不该把空中链一起带走。
        _frame++;
        AirChainTick();

        if (CfgEnabled?.Value != true) return;

        int gap = Math.Max(30, CfgGapFrames?.Value ?? 60);
        if (!_gapOpen && _seq > 0 && _frame - _lastEventFrame >= gap)
        {
            _gapOpen = true;
            Emit($"\n-------- 空档 {_frame - _lastEventFrame} 帧 (约 {( _frame - _lastEventFrame) / 30f:F1}s) " +
                 $"—— 下一段是另一招 --------\n");
        }
    }

    public static void ChangePostfix(object __instance, string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name)) return;
            if (__instance == null) return;

            // 只看本地玩家 —— 敌人的动作会把日志淹掉
            var owner = ReadMember(__instance, _ownerMember);
            if (owner == null || !DashInvincible.IsLocalPlayerActor(owner as GamePlay.ActorBase)) return;

            _lastEventFrame = _frame;
            _gapOpen = false;
            _seq++;

            // 空中链是独立开关, 放在总开关之上 —— 关掉动作总记录也能单独跑它
            AirChainFeed(name);

            if (CfgEnabled?.Value != true) return;

            // 链路模式：动作层走统一缩进，并带上当前动作的档案
            if (CfgChain?.Value == true)
            {
                bool first2 = _profiled.Add(name);
                Chain(2, $"【动作】\"{name}\"" + (first2 && CfgProfile?.Value == true ? Profile(__instance) : ""));
                return;
            }

            bool first = _profiled.Add(name);
            // 注意: 三元表达式整个要包在括号里, 否则 C# 会把 ":" 当格式说明符
            string tag = (CfgProfile?.Value == true && first) ? "新" : "  ";
            Emit($"[动作 #{_seq,4} f={_frame,6} ({tag})] \"{name}\"" +
                 ((first && CfgProfile?.Value == true) ? Profile(__instance) : ""));
        }
        catch { }
    }

    /// <summary>把当前动作的完整档案拼成一行。</summary>
    private static string Profile(object am)
    {
        try
        {
            var gal = ReadMember(am, _curActionMember);
            if (gal == null) return "   (拿不到 CurrentAction)";

            var sb = new StringBuilder();
            sb.Append("   Role=\"").Append(Str(gal, _fRole)).Append("\"");
            sb.Append(" Animate=\"").Append(Str(gal, _fAnimate)).Append("\"");
            sb.Append(" 时长=").Append(FpStr(gal, _fDuration));
            sb.Append(" 类型=").Append(Str(gal, _fType));
            sb.Append(" 下一段=\"").Append(Str(gal, _fNext)).Append("\"");

            var drop = Str(gal, _fDrop);
            if (!string.IsNullOrEmpty(drop)) sb.Append(" 落地=\"").Append(drop).Append("\"");
            var dead = Str(gal, _fDead);
            if (!string.IsNullOrEmpty(dead)) sb.Append(" 死亡=\"").Append(dead).Append("\"");

            int adds = Count(gal, _fAddEffects);
            int hits = Count(gal, _fHitData);
            if (adds > 0) sb.Append(" 附带特效=").Append(adds);
            if (hits > 0) sb.Append(" 判定段=").Append(hits);

            // Interrupt: 谁能打断这个动作 —— "冲刺打断冲刺"就看这里
            var inter = ListStr(gal, _fInterrupt);
            if (inter != null)
                sb.Append(" 可被打断=[").Append(inter).Append("]");

            return sb.ToString();
        }
        catch (Exception e) { return $"   (档案读取失败: {e.Message})"; }
    }

    private static string Str(object o, FieldInfo f)
    {
        try { return f == null ? "?" : (f.GetValue(o) as string ?? "?"); } catch { return "?"; }
    }

    private static string FpStr(object o, FieldInfo f)
    {
        try
        {
            if (f == null) return "?";
            var v = f.GetValue(o);
            if (v == null) return "?";
            var m = v.GetType().GetMethod("ToString", new[] { typeof(string), typeof(IFormatProvider) });
            if (m != null) return (string)m.Invoke(v, new object[] { "0.00", null });
            return v.ToString();
        }
        catch { return "?"; }
    }

    private static int Count(object o, FieldInfo f)
    {
        try
        {
            var v = f?.GetValue(o);
            if (v is ICollection c) return c.Count;
            if (v is IEnumerable e) { int k = 0; foreach (var _ in e) k++; return k; }
        }
        catch { }
        return 0;
    }

    private static string ListStr(object o, FieldInfo f)
    {
        try
        {
            var v = f?.GetValue(o);
            if (v == null) return null;
            var en = v as IEnumerable;
            if (en == null) return null;
            var parts = new List<string>();
            foreach (var x in en) { parts.Add(x?.ToString() ?? "?"); if (parts.Count >= 40) break; }
            return parts.Count == 0 ? null : string.Join(",", parts);
        }
        catch { return null; }
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

    private static void Emit(string s)
    {
        int cap = CfgCap?.Value ?? 4000;
        if (_lines >= cap)
        {
            if (_lines == cap) { _lines++; Plugin.Log?.LogInfo($"[动作记] 已达上限 {cap} 行, 后续不再记录"); }
            return;
        }
        _lines++;
        Plugin.Log?.LogInfo(s);
    }
}
