using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using GamePlay;

namespace BlazblueJsPatch;

/// <summary>
/// 跳跃 ⟷ 冲刺 计数互相重置。
///
/// 需求
/// ────
/// 跳跃后可以重新三段冲，冲刺后可以重新三段跳。
///
/// 机制（日志实证）
/// ────────────────
/// 原版空中机动是"只能往上走、不能回头"：用过 dashAir 之后不能再切回 dashAir，
/// 只能 dashAir2 / dashAir3；跳跃同理 jump → jump2 → jump3。
/// 判断点有两处（**两道门都要过**）：
///   ActionMgr.CheckCanChangeToAction(name)  —— 查询, 每帧被轮询
///   ActionMgr.changeActionPrecheck(name)    —— 实际切换前的检查
/// 只挂第一处会出现"放行了 61 次但动作没变"。
///
/// 做法
/// ────
/// 不再用"放行一步"的土办法（那样只会永远卡在第一档），改成自己记一套预算：
///   · 冲刺消耗冲刺预算；跳跃消耗跳跃预算
///   · 跳跃成功 -> 冲刺预算回满；冲刺成功 -> 跳跃预算回满
///   · 落地/其它动作 -> 两边都回满
///   · 预算 > 0 时才在两处强制放行
///
/// ⚠ 冲刺的收尾动作（dashEnd / dashendAirAHD）不计入消耗 —— 它只是冲刺的尾巴，
///   当成一次新冲刺会让预算掉得莫名其妙。
///
/// 安全边界
/// ────────
///   · 只对本地玩家生效
///   · 只在【当前动作本身就是跳跃/冲刺族】时才干预
///   · 反射句柄启动时解析一次并缓存（别学第一版放热路径上，会把日志刷爆）
///   · 全程 try/catch，出错一律放行走原逻辑
/// </summary>
internal static class JumpDashCrossReset
{
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgUnlimited;
    internal static BepInEx.Configuration.ConfigEntry<int> CfgDashBudget;
    internal static BepInEx.Configuration.ConfigEntry<int> CfgJumpBudget;
    internal static BepInEx.Configuration.ConfigEntry<string> CfgJumpKw;
    internal static BepInEx.Configuration.ConfigEntry<string> CfgDashKw;
    /// <summary>冲刺族【排除名单】(逗号分隔)。名字里含这些串的【不算冲刺】。</summary>
    internal static BepInEx.Configuration.ConfigEntry<string> CfgDashExclude;
    internal static BepInEx.Configuration.ConfigEntry<string> CfgGroundKw;
    /// <summary>地面冲刺可以打断地面冲刺（快速冲刺）。见 ChainPrefix 的注释。</summary>
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgGroundDashCancel;
    /// <summary>快速冲刺的专项诊断（在日志里认 `[快速冲刺:诊断]`）。查完可关。</summary>
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgDebug;

    private static PropertyInfo _curProp;
    private static MemberInfo _ownerMember;
    private static bool _reflectOk = true;

    private static int _dashLeft = -1, _jumpLeft = -1;   // -1 = 尚未初始化
    /// <summary>
    /// 是否处在"空中机动周期"里。
    /// 关键: 空中攻击(attack2 之类)【不结束】周期, 否则一跳一冲之后插个普攻, 链条就断了 ——
    /// 这正是上一版卡住的原因。只有落地动作才结束周期。
    /// </summary>
    private static bool _airCycle;
    private static int _logged, _traceN, _groundLogged;

    private static readonly string[] DefaultGround =
        { "stand", "run", "squat", "runBrake", "wakeup", "born", "push_back" };

    private static int MaxDash => Math.Max(0, CfgDashBudget?.Value ?? 3);
    private static int MaxJump => Math.Max(0, CfgJumpBudget?.Value ?? 3);
    private static bool Unlimited => CfgUnlimited?.Value ?? false;

    public static int Apply(Harmony harmony)
    {
        var t = AccessTools.TypeByName("GamePlay.ActionMgr");
        if (t == null) { Plugin.Log?.LogWarning("  [跳跃/冲刺互重置] 找不到 GamePlay.ActionMgr"); return 0; }

        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _curProp = t.GetProperty("CurrentActionName", F);
        _ownerMember = (MemberInfo)t.GetProperty("Owner", F) ?? t.GetField("Owner", F);
        if (_curProp == null || _ownerMember == null)
        {
            _reflectOk = false;
            Plugin.Log?.LogWarning($"  [跳跃/冲刺互重置] 反射解析不全 (CurrentActionName={_curProp != null}, Owner={_ownerMember != null}), 不干预");
            return 0;
        }

        int n = 0;
        n += Hook(harmony, t, "CheckCanChangeToAction", nameof(Precheck), false);
        n += Hook(harmony, t, "changeActionPrecheck", nameof(Precheck), false);
        n += Hook(harmony, t, "ChangeAction", nameof(ChangePostfix), true);

        // 地面冲刺打断地面冲刺: 清掉"冲刺锁链"的那道沉默。
        // 挂的是 PlayerSkillChain.DoUpdateAndCheckInputSucc —— 它开头就是
        // `MuteRemain(+0x54) > 0 -> return false`, 我们在这个 prefix 里先把沉默清掉,
        // 同一次调用里那道门就不再拦人（同一帧生效，不像 postfix 要等下一帧）。
        var ct = AccessTools.TypeByName("GamePlay.PlayerSkillChain");
        if (ct != null)
        {
            n += Hook(harmony, ct, "DoUpdateAndCheckInputSucc", nameof(ChainPrefix), false);
            // 诊断用: 沉默清掉之后，游戏到底有没有去"起手下一段冲刺"？卡在哪一步？
            n += Hook(harmony, ct, "findStartingSkillMatchInputDir", nameof(StartingSearchPostfix), true);
            n += Hook(harmony, ct, "StartSkill", nameof(StartSkillPostfix), true);
        }
        else Plugin.Log?.LogWarning("  [跳跃/冲刺互重置] 找不到 GamePlay.PlayerSkillChain (快速冲刺不可用)");

        if (n > 0)
            Plugin.Log.LogInfo($"  [跳跃/冲刺互重置] 冲刺预算={MaxDash} 跳跃预算={MaxJump}; " +
                               $"跳跃族=\"{CfgJumpKw?.Value}\" 冲刺族=\"{CfgDashKw?.Value}\" " +
                               $"冲刺排除=\"{CfgDashExclude?.Value}\"");
        return n;
    }

    private static int Hook(Harmony harmony, Type t, string method, string patch, bool postfix)
    {
        var m = AccessTools.Method(t, method);
        if (m == null) { Plugin.Log?.LogWarning($"  [跳跃/冲刺互重置] 在 {t.Name} 上找不到 {method}"); return 0; }
        try
        {
            var mi = typeof(JumpDashCrossReset).GetMethod(patch, BindingFlags.Static | BindingFlags.Public);
            harmony.Patch(m, prefix: postfix ? null : new HarmonyMethod(mi),
                             postfix: postfix ? new HarmonyMethod(mi) : null);
            Plugin.Log.LogInfo($"  [跳跃/冲刺互重置] 已挂钩 {t.Name}.{method} ({(postfix ? "postfix" : "prefix")})");
            return 1;
        }
        catch (Exception e)
        {
            Plugin.Log?.LogWarning($"  [跳跃/冲刺互重置] 挂钩 {method} 失败: {e.Message}");
            return 0;
        }
    }

    // ------------------------------------------------------------------ 两道门上的同一个判定

    public static bool Precheck(object __instance, string name, ref bool __result)
    {
        try
        {
            if (CfgEnabled?.Value != true) return true;
            if (!_reflectOk || string.IsNullOrEmpty(name)) return true;

            bool nJ = Fam(name, CfgJumpKw?.Value ?? "jump");
            bool nD = IsDashFam(name);
            if (!nJ && !nD) return true;                 // 只关心跳跃/冲刺族的目标动作

            if (!IsLocalPlayer(__instance)) return true;
            if (_dashLeft < 0) ResetBoth();              // 初始

            // ---- 地面冲刺打断地面冲刺（快速冲刺）----
            // 反汇编已证: CheckCanChangeToAction 整个函数体就是
            //     ActorCountDown.GetCountDown(name) <= 0
            // 只读、无副作用 —— 所以在这里直接放行不会漏掉任何别的事。
            // 主修复在 ChainPrefix(清沉默), 这里是第二道保险:
            // 万一冲刺动作在 ActorCountDown 上还挂着计时, 也一并放行。
            // (另带 `_dashLeft > 0` 作保险: 地面冲刺不再扣预算, 所以地面上它恒 > 0,
            //  不影响快速冲刺; 但万一当前动作是个名字里不带 air 的空中动作
            //  (如 dashAAendEX), 空中额度用光时就仍然不许乱放。)
            string curNow = Current(__instance);
            if (CfgGroundDashCancel?.Value == true && nD && _dashLeft > 0 &&
                IsDashFam(curNow) && !IsAirName(curNow))
            {
                __result = true;
                _groundLogged++;
                if (_groundLogged <= 10 || _groundLogged % 60 == 0)
                    Plugin.Log?.LogInfo($"[快速冲刺] 放行 {curNow} -> {name} " +
                                        $"(地面冲刺自打断, 第 {_groundLogged} 次)");
                return false;
            }

            // 注意: 这里【不再】要求"当前动作属于跳跃/冲刺族"。
            // 空中攻击(attack2 等)不属于任何一族, 旧写法会因为这一条整个失效, 链条被普攻打断。
            // 改成看"是否还在空中机动周期里" —— 周期只被落地动作结束。
            if (!_airCycle && !Unlimited) return true;

            bool allow = Unlimited ||
                         (nD && _dashLeft > 0) ||
                         (nJ && _jumpLeft > 0);
            if (!allow) return true;                     // 预算耗尽 -> 交回原逻辑

            __result = true;
            if (_logged++ < 60)
                Plugin.Log?.LogInfo($"[跳跃/冲刺互重置] 放行 {Current(__instance)} -> {name}  " +
                                    $"(冲刺余 {_dashLeft}, 跳跃余 {_jumpLeft})");
            return false;
        }
        catch (Exception e)
        {
            if (_traceN++ < 5) Plugin.Log?.LogError($"[跳跃/冲刺互重置] 异常(放行原逻辑): {e.Message}");
            return true;
        }
    }

    /// <summary>
    /// 地面冲刺 → 冲刺（快速冲刺）★ 核心修复
    ///
    /// 症状
    /// ────
    /// 地面上冲刺时再按冲刺键毫无反应；空中冲刺却能被普攻打断。
    /// 说明"接招表"本身允许取消，拦路的是别的东西。
    ///
    /// 反汇编定位（RVA 0x1BB10F0 PlayerSkillChain::DoUpdateAndCheckInputSucc）
    /// ─────────────────────────────────────────────────────────────────
    ///     if (Status(+0x20) == 4 || == 5)      return false;
    ///     if (MuteRemain(+0x54) > 0)           return false;   ← ★ 就是这道门
    ///     if (TimeEllaps < ActdurStrict)       return false;   ← 硬地板（**保留，不碰**）
    /// 再看 RVA 0x1BB28C0 SetMute:
    ///     [+0x50] = muteBy(int)   [+0x54] = [+0x5C] = muteTime(Fp)
    ///     —— 字段表: +0x50 MuteBy / +0x54 MuteRemain / +0x5C LastMuteMax
    /// 实测日志印证: 地面冲刺一起手，本链的沉默就是 0.600s
    ///     [连段模组:输入推进] ... Cur@7("dash") 闸门[Status=2 CD=0.600]
    /// 而冲刺动作本身只有 0.25s —— 冲刺结束后还被自己锁 0.35s, 再按冲刺自然没反应。
    /// 也就是说: 原版的"冲刺冷却"不是冷却，是【技能给自己挂的沉默】。
    ///
    /// 做法
    /// ────
    /// 地面冲刺【动作进行中】时, 把本链的沉默清零。只动这一条链:
    /// 条件是"本链当前段就是地面冲刺动作", 所以玩家的其它链(高文链等)的沉默不受影响。
    ///
    /// ⚠ 不碰 ActdurStrict: 硬地板是原生"这一招至少打多久"的护栏,
    ///   留着它, 快速冲刺就不会退化成瞬移连冲(那是"拍脑袋调数据", 不做)。
    /// ⚠ 空中冲刺(dashAir)不在范围内 —— 它走预算那套, 行为不变。
    /// </summary>
    public static void ChainPrefix(GamePlay.PlayerSkillChain __instance)
    {
        try
        {
            if (CfgEnabled?.Value != true) return;
            if (CfgGroundDashCancel?.Value != true) return;
            if (__instance == null) return;

            IntPtr cp = __instance.Pointer;
            if (cp == IntPtr.Zero) return;

            // 热路径: 先读。没沉默就立刻走人（绝大多数帧都是这条）
            long mute = Marshal.ReadInt64(cp, OFF_MUTE_REMAIN);
            if (mute <= 0) return;

            var pl = OwnerPlayerOf(cp);
            if (pl == null || !DashInvincible.IsLocalPlayerActor(pl)) return;

            // ⚠ 必须先初始化: `_dashLeft` 初值 -1 = "还没算过", 而它的初始化原本只在
            //   Precheck/ChangePostfix 里发生 —— 若玩家【只在地面上冲刺】(从不跳、也不触发
            //   Precheck 的预算分支), 这里就会一直读到 -1, 下面的 `<= 0` 保险会把整个功能静默关掉。
            if (_dashLeft < 0) ResetBoth();

            // 本链当前段是哪个动作 —— 只有"地面冲刺"才清, 空中冲刺/普攻都不清
            // (`_dashLeft` 同上作保险; 地面冲刺不扣预算, 所以地面上恒成立)
            string act = CurSkillActionOf(cp);
            if (!IsDashFam(act) || IsAirName(act) || _dashLeft <= 0) return;

            Marshal.WriteInt64(cp, OFF_MUTE_REMAIN, 0);   // MuteRemain = Fp 0
            Marshal.WriteInt32(cp, OFF_MUTE_BY, 0);       // MuteBy = 0

            _clearLogged++;
            // ⚠ 不再限"前 10 次" —— 上一版就是被这个上限弄成假阴性:
            //   整场 100 多次冲刺只留下前 10 行，后面 2/3 的测试期完全瞎。
            //   改成按【动作名】配对去重: 新动作名永远打得出来。
            LogPair($"clear|{act}",
                    $"[快速冲刺] 清掉地面冲刺链的沉默 ({mute / 4294967296.0:F3}s, 动作=\"{act}\")");
        }
        catch (Exception e)
        {
            if (_traceN++ < 5) Plugin.Log?.LogError($"[快速冲刺] ChainPrefix 异常: {e.Message}");
        }
    }

    private static int _clearLogged;

    // ==================================================================
    //  诊断: 沉默清掉之后，游戏到底走到哪一步？(只在"玩家正处地面冲刺"时打印)
    // ==================================================================

    /// <summary>
    /// ★ 快速冲刺的真正实现: 【接管起手搜索】。
    ///
    /// 为什么必须接管（2026-10-04 实测 + 反汇编）:
    ///   `findStartingSkillMatchInputDir`(0x1BB3B20) 只有一个调用方
    ///   —— `findAndStartSkill_Imp`(0x1BB3320) 的第 ② 步（起手段搜索），
    ///   所以动它的返回值**不会**污染 UI 的"下一招"预测（那条走 FindAStartingSkillForPredict）。
    ///   而实测日志证明: 地面冲刺动作进行中按冲刺键，这个搜索**返回 null**
    ///       [快速冲刺:诊断] 起手搜索(当前=dash) 返回 <null>
    ///   ⇒ 游戏自己找不到"能立刻起来的下一段"，按键就这么被吞掉了。
    ///   （把 dash 三段都看了一遍: PreSkillOrder 全空 = 都是起手段，
    ///     所以"下一段"搜索 findNextSkillMatchPreOrderAndInputDir 也永远返回 null。两条路都是 null。）
    ///
    /// 做法: 当游戏递回【空】或【当前这一段自己】（等于白按）时，我们替它挑下一段递回去。
    ///   挑法完全照抄原生三段设计的顺序: 同一组里、动作名同族（dash / dash2 / dash3）、
    ///   有 Input 的下一段；走到头就绕回第 1 段 ⇒ dash → dash2 → dash3 → dash → …
    ///   只递【技能对象】，起招还是走游戏自己的 StartSkill/ChangeAction ⇒ 链的记账不会乱。
    /// </summary>
    public static void StartingSearchPostfix(GamePlay.PlayerSkillChain __instance, ref GamePlay.PlayerSkill __result)
    {
        try
        {
            if (__instance == null) return;
            IntPtr cp = __instance.Pointer;
            if (cp == IntPtr.Zero || !GroundDashChain(cp, out string cur)) return;

            string got = "<null>";
            try { if (__result != null) got = ActionOfSkillPtr(__result.Pointer) ?? "<无动作名>"; } catch { }

            if (CfgDebug?.Value == true)
                LogPair($"search|{cur}|{got}", $"[快速冲刺:诊断] 起手搜索(当前={cur}) 返回 {got}");

            // 游戏递回"空"或"就是当前这一段"(后者等于按了白按) -> 我们接管
            bool useless = __result == null || IsSameStageName(cur, got);
            if (!useless) return;
            if (CfgGroundDashCancel?.Value != true) return;

            var next = PickNextStage(__instance, cur);
            if (next == null) return;

            __result = next;
            LogPair($"take|{cur}|{ActionOfSkillPtr(next.Pointer)}",
                    $"[快速冲刺] 接管起手搜索: 当前={cur} -> 递 \"{ActionOfSkillPtr(next.Pointer)}\"");
        }
        catch (Exception e) { DiagErr("StartingSearch", e); }
    }

    /// <summary>同一族的"段"名字判定: 去掉尾部数字后相等。
    /// dash/dash2/dash3 同族 ✓；dashAtk / dashAAendEX / attackholdDashEX 不是 ✓（避免递错招）。</summary>
    private static bool IsSameStageName(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return string.Equals(StripDigits(a), StripDigits(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string StripDigits(string s)
    {
        int i = s.Length;
        while (i > 0 && char.IsDigit(s[i - 1])) i--;
        return s.Substring(0, i);
    }

    /// <summary>
    /// 挑下一段冲刺。
    ///
    /// ⚠ 第一版是【自己走 SkillList 按名字+Input+IsStartingSkill 过滤】的，
    ///   结果 240 次机会一次都没递出去，而且**没有任何日志** —— 正是本项目最致命的
    ///   "静默 return"。现在改成走游戏自己的 `PlayerSkillChain.FindByActionName`
    ///   （它就是"在本链里按动作名找技能行"），每一步都打日志，不许再静默。
    ///
    /// 段名规则: dash→dash2，dash2→dash3，dash3→(dash4 找不到)→绕回 dash。
    /// </summary>
    private static GamePlay.PlayerSkill PickNextStage(GamePlay.PlayerSkillChain chain, string curAction)
    {
        string baseName = StripDigits(curAction);
        string want = NextStageName(curAction);

        if (want != null)
        {
            var sk = FindSkill(chain, want);
            if (sk != null)
            {
                LogPair($"pick|hit|{curAction}|{want}",
                        $"[快速冲刺:诊断] 下一段 \"{want}\" 在本链找到 (Input={ReadI32(sk.Pointer, OFF_SKILL_INPUT)})");
                return sk;
            }
            LogPair($"pick|miss|{curAction}|{want}",
                    $"[快速冲刺:诊断] 下一段 \"{want}\" 在本链【没找到】");
        }
        else LogPair($"pick|noname|{curAction}", $"[快速冲刺:诊断] 段名规则算不出下一个(当前={curAction})");

        // 走到头了就绕回第 1 段（dash3 -> dash）
        if (!string.IsNullOrEmpty(baseName) && !string.Equals(baseName, curAction, StringComparison.Ordinal))
        {
            var sk = FindSkill(chain, baseName);
            if (sk != null)
            {
                LogPair($"pick|wrap|{curAction}|{baseName}",
                        $"[快速冲刺:诊断] 到头了, 绕回第 1 段 \"{baseName}\"");
                return sk;
            }
            LogPair($"pick|wrapmiss|{curAction}|{baseName}",
                    $"[快速冲刺:诊断] 绕回第 1 段 \"{baseName}\" 也没找到");
        }
        return null;
    }

    private static GamePlay.PlayerSkill FindSkill(GamePlay.PlayerSkillChain chain, string action)
    {
        try { return chain.FindByActionName(action); }
        catch (Exception e)
        {
            LogPair($"pick|ex|{action}", $"[快速冲刺:诊断] FindByActionName(\"{action}\") 异常: {e.Message}");
            return null;
        }
    }

    /// <summary>dash → dash2 ; dash2 → dash3 ; dash3 → dash4(不存在, 由调用方绕回)</summary>
    private static string NextStageName(string cur)
    {
        if (string.IsNullOrEmpty(cur)) return null;
        string b = StripDigits(cur);
        string digits = cur.Substring(b.Length);
        if (digits.Length == 0) return cur + "2";
        if (!int.TryParse(digits, out int n) || n < 0 || n > 90) return null;
        return b + (n + 1).ToString();
    }

    /// <summary>StartSkill 的尝试与结果 —— 找到了却没起成，答案就在这里。</summary>
    public static void StartSkillPostfix(GamePlay.PlayerSkillChain __instance, GamePlay.PlayerSkill skNew, bool __result)
    {
        try
        {
            if (CfgDebug?.Value != true || __instance == null) return;
            IntPtr cp = __instance.Pointer;
            if (cp == IntPtr.Zero || !GroundDashChain(cp, out string cur)) return;

            string want = "<null>";
            try { if (skNew != null) want = ActionOfSkillPtr(skNew.Pointer) ?? "<无动作名>"; } catch { }
            LogPair($"start|{cur}|{want}|{__result}",
                    $"[快速冲刺:诊断] StartSkill(当前={cur}) 请求 {want} -> {__result}");
        }
        catch (Exception e) { DiagErr("StartSkill", e); }
    }

    /// <summary>本链是否正处于"地面冲刺"（当前段是 dash/dash2/dash3 这类，且本机玩家）。</summary>
    private static bool GroundDashChain(IntPtr chainPtr, out string cur)
    {
        cur = null;
        IntPtr mgr = ReadPtr(chainPtr, OFF_CHAIN_OWNER);
        if (mgr == IntPtr.Zero) return false;
        IntPtr pl = ReadPtr(mgr, OFF_MGR_OWNER);
        if (pl == IntPtr.Zero || !IsLocalFast(pl)) return false;
        cur = CurSkillActionOf(chainPtr);
        return IsDashFam(cur) && !IsAirName(cur);
    }

    private static string ActionOfSkillPtr(IntPtr sk)
    {
        IntPtr wrap = ReadPtr(sk, OFF_SKILL_ACTIVATE);
        if (wrap == IntPtr.Zero) return null;
        IntPtr data = ReadPtr(wrap, OFF_WRAP_DATA);
        if (data == IntPtr.Zero) return null;
        IntPtr s = ReadPtr(data, OFF_DATA_ACTION);
        if (s == IntPtr.Zero) return null;
        try { return Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(s); }
        catch { return null; }
    }

    // 本机玩家的指针，每 5 秒校对一次 —— 别在每个调用里走 BattleBase(那是几万次/秒的热路径)
    private static IntPtr _localPtr;
    private static float _localNext;

    private static float Now()
    {
        try { return UnityEngine.Time.realtimeSinceStartup; } catch { return 0f; }
    }

    private static bool IsLocalFast(IntPtr actorPtr)
    {
        float now = Now();
        if (_localPtr == IntPtr.Zero || now > _localNext)
        {
            _localNext = now + 5f;
            try { var self = BattleBase.Cur?.PlayerSelf; if (self != null) _localPtr = self.Pointer; }
            catch { }
        }
        return _localPtr != IntPtr.Zero && actorPtr == _localPtr;
    }

    /// <summary>按"组合"去重: 新组合永远打得出来(不设总条数上限 —— 限条数=制造假阴性, 本项目栽过 5 次)。</summary>
    private static readonly Dictionary<string, int> _pairs = new Dictionary<string, int>();

    private static void LogPair(string key, string msg)
    {
        _pairs.TryGetValue(key, out int c);
        _pairs[key] = ++c;
        if (c != 1 && c % 120 != 0) return;
        Plugin.Log?.LogInfo($"{msg}   (第 {c} 次)");
    }

    private static int _diagErr;
    private static void DiagErr(string where, Exception e)
    {
        if (_diagErr++ < 5) Plugin.Log?.LogError($"[快速冲刺:诊断] {where} 异常: {e.Message}");
    }

    private const int OFF_MUTE_BY = 0x50;       // PlayerSkillChain.MuteBy     (int)
    private const int OFF_MUTE_REMAIN = 0x54;   // PlayerSkillChain.MuteRemain (Fp)
    private const int OFF_CHAIN_OWNER = 0x10;   // PlayerSkillChain.Owner = PlayerSkillMgr
    private const int OFF_MGR_OWNER = 0x10;     // PlayerSkillMgr.Owner   = PlayerObj
    private const int OFF_CHAIN_CUR = 0x28;     // PlayerSkillChain.Cur
    private const int OFF_CUR_SKILL = 0x10;     // PlayerSkillChainCurSkill.Skill
    private const int OFF_SKILL_ACTIVATE = 0x20;// PlayerSkill.SkillActivate
    private const int OFF_WRAP_DATA = 0x10;     // SkillActivateFixedPointWrap.data
    private const int OFF_DATA_ACTION = 0x28;   // SkillActivateFixedPoint.action_
    private const int OFF_SKILL_INPUT = 0x44;   // PlayerSkill.Input (InputCmd)

    private static int ReadI32(IntPtr p, int off)
    {
        if (p == IntPtr.Zero) return 0;
        try { return Marshal.ReadInt32(p, off); } catch { return 0; }
    }

    private static GamePlay.PlayerObj OwnerPlayerOf(IntPtr chainPtr)
    {
        IntPtr mgr = ReadPtr(chainPtr, OFF_CHAIN_OWNER);
        if (mgr == IntPtr.Zero) return null;
        IntPtr pl = ReadPtr(mgr, OFF_MGR_OWNER);
        if (pl == IntPtr.Zero) return null;
        try { return new GamePlay.PlayerObj(pl); } catch { return null; }
    }

    /// <summary>本链【当前段】的动作名。三层指针: Cur → CurSkill.Skill → Skill.SkillActivate → .data → action_</summary>
    private static string CurSkillActionOf(IntPtr chainPtr)
    {
        IntPtr cur = ReadPtr(chainPtr, OFF_CHAIN_CUR);
        if (cur == IntPtr.Zero) return null;
        IntPtr sk = ReadPtr(cur, OFF_CUR_SKILL);
        if (sk == IntPtr.Zero) return null;
        return ActionOfSkillPtr(sk);
    }

    private static IntPtr ReadPtr(IntPtr p, int off)
    {
        if (p == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            IntPtr v = Marshal.ReadIntPtr(p, off);
            long x = v.ToInt64();
            if (x < 0x10000 || x > 0x7FFFFFFFFFFF || (x & 7) != 0) return IntPtr.Zero;
            return v;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>切换成功后才扣预算 —— 被拒的切换不该扣。</summary>
    public static void ChangePostfix(object __instance, string name, ref bool __result)
    {
        try
        {
            if (CfgEnabled?.Value != true || !__result || string.IsNullOrEmpty(name)) return;
            if (!IsLocalPlayer(__instance)) return;

            // 冲刺的收尾动作不算一次冲刺
            if (name.IndexOf("end", StringComparison.OrdinalIgnoreCase) >= 0) return;

            // 落地/地面动作 -> 结束空中周期, 两边预算回满
            if (IsGround(name))
            {
                if (_airCycle) Plugin.Log?.LogInfo($"[跳跃/冲刺互重置] 落地 {name} -> 周期结束, 预算回满");
                _airCycle = false;
                ResetBoth();
                return;
            }

            bool j = Fam(name, CfgJumpKw?.Value ?? "jump");
            bool d = IsDashFam(name);

            if (d)
            {
                _airCycle = true;
                // 预算管的是【空中机动】(三段冲/三段跳), 地面冲刺不吃它 ——
                // 否则地面上连冲三下就把空中冲刺额度花光了, 而且第 4 下按不出来。
                bool air = IsAirName(name);
                if (air) _dashLeft--;
                _jumpLeft = MaxJump;
                Plugin.Log?.LogInfo($"[跳跃/冲刺互重置] {(air ? "空中" : "地面")}冲刺 {name} -> " +
                                    $"跳跃预算回满({MaxJump}), 冲刺余 {_dashLeft}");
            }
            else if (j) { _airCycle = true; _jumpLeft--; _dashLeft = MaxDash; Plugin.Log?.LogInfo($"[跳跃/冲刺互重置] 跳跃 {name} -> 冲刺预算回满({MaxDash}), 跳跃余 {_jumpLeft}"); }
            // 其它动作(空中攻击等) -> 【什么都不做】: 周期延续, 预算保持
        }
        catch (Exception e)
        {
            if (_traceN++ < 5) Plugin.Log?.LogError($"[跳跃/冲刺互重置] Change 异常: {e.Message}");
        }
    }

    private static void ResetBoth()
    {
        _dashLeft = MaxDash;
        _jumpLeft = MaxJump;
    }

    // ------------------------------------------------------------------

    /// <summary>是不是落地/地面动作。用来结束"空中机动周期"。</summary>
    private static bool IsGround(string action)
    {
        if (string.IsNullOrEmpty(action)) return false;
        var custom = CfgGroundKw?.Value;
        var list = string.IsNullOrWhiteSpace(custom)
            ? DefaultGround
            : custom.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var k in list)
            if (action.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static bool Fam(string action, string kw) =>
        !string.IsNullOrEmpty(action) && !string.IsNullOrEmpty(kw) &&
        action.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>冲刺族判定 —— 含关键字【且不含任何排除串】。
    ///
    /// 为什么必须配排除名单（2026-10-04 实测）：
    ///   高文的两个槽 `dashAtk0`(地面, 槽 11.1) / `dashAirAtk0`(空中, 槽 11.2)
    ///   按键是 **Summon/上**，根本不是冲刺移动 —— 但名字里有 "dash"，
    ///   被关键字匹配当成冲刺扣了预算，于是空中连冲之后高文直接按不出来：
    ///       [段数限制] 切换【失败】 -> dashAtk0
    ///   这和之前 `UltraDash` 那次是同一类坑：**关键字子串误伤**，必须点名排除。
    ///
    /// 判据不是手感数，是按键通道：这两条的 InputCmd 是 Summon，不是 Dash。
    /// </summary>
    private static bool IsDashFam(string action)
    {
        if (!Fam(action, CfgDashKw?.Value ?? "dash")) return false;
        var ex = CfgDashExclude?.Value;
        if (string.IsNullOrWhiteSpace(ex)) return true;
        foreach (var k in ex.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            if (action.IndexOf(k.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return false;
        return true;
    }

    /// <summary>是不是【空中】那一档。实测: 地面 = dash / 空中 = dashAir（各段同理带 Air）。
    /// 只用于把"地面快速冲刺"和"空中三段冲"分开 —— 前者不受预算约束，后者照旧。</summary>
    private static bool IsAirName(string action) =>
        !string.IsNullOrEmpty(action) && action.IndexOf("air", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsLocalPlayer(object actionMgr)
    {
        var owner = ReadOwner(actionMgr) as ActorBase;
        return owner != null && DashInvincible.IsLocalPlayerActor(owner);
    }

    private static object ReadOwner(object actionMgr)
    {
        try
        {
            switch (_ownerMember)
            {
                case PropertyInfo p: return p.GetValue(actionMgr);
                case FieldInfo f: return f.GetValue(actionMgr);
            }
        }
        catch { }
        return null;
    }

    private static string Current(object actionMgr)
    {
        try { return _curProp?.GetValue(actionMgr) as string; }
        catch { return null; }
    }
}
