using System;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using GamePlay;

namespace BlazblueJsPatch;

/// <summary>
/// 冲刺全程触发「极限闪避」（= 完美闪避）
///
/// 机制
/// ────
/// `JsPort.ActorFuncUtils.CheckDodge(PlayerObj player, ActorBase actor, ActorHitResult hitres)`
/// （RVA 0x1665150）是「这一下算不算闪避成功」的**唯一闸门**。
/// 它的 6 个调用点:
///   · 1 个 PuerTS 包装 JsPort_ActorFuncUtils_Wrap.F_CheckDodge（暴露给 JS）
///   · 5 个 BuffFuncsJs 的 lambda，即「若闪避成功 → 生成XX」那一批潜能
///     （b__38_404 / 418 / 550 / 552 / 57）
///
/// 本地化里对应的词条：
///   极限闪避时，在周身生成飞刃…            TriggerDesc_32721/32722/32724/32725
///   极限闪避成功时，使攻击你的敌人进入破甲状态
///   每累计触发 N 次极限闪避后，最终伤害提升…
///   无敌闪避状态下，无视敌人攻击的同时，额外触发极限闪避效果   ← 游戏自带的那条思路
///
/// 所以「让冲刺全程都能触发完美闪避」= 在冲刺期间让 CheckDodge 返回 true。
///
/// 实现
/// ────
/// Postfix，**只把 false 抬成 true，从不反向覆盖**。
/// 只有「本地玩家 && 正在冲刺」才会抬 —— 冲刺状态只跟踪本地玩家，敌人不在其中。
/// 调用点本身是「命中结算时」触发的，不是每帧，所以不会刷屏。
///
/// 类型解析按运行时扫描，不硬编命名空间 —— IL2CPP 下类型名/命名空间可能被改写。
/// </summary>
internal static class PerfectDodge
{
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;

    private static bool Enabled => CfgEnabled?.Value ?? true;

    private static int _logged;
    private static int _trace;

    // ================================================================
    //  C 方案: 冲刺全程 = 完美闪避
    // ================================================================
    //
    //  机制依据(日志实证):
    //    · 原生完美闪避触发时 GetInvincipalLevel 返回 0 —— 它不走无敌那条路
    //    · 完美闪避的载体是动作里的 ActionBehitNotifyData
    //      { TimeRange, ConditionGroup, HitResCalcStage, ScriptCall }
    //      即「在动作第 X~Y 帧内被击中 → 在命中结算的某阶段调用脚本」,
    //      那个脚本就是做 OnDodge() + ShowSlow(player) 的 actor func。
    //    · 我们的全程无敌之所以把它弄没: 命中变成了 BattleActorBehitType.Invincipal,
    //      在到达 behit-notify 之前就被吸收, 脚本根本没机会跑。
    //
    //  所以 C 的做法: 在【真的被打中那一刻】自己把原生那一套补出来 ——
    //    吸收这一击(否则"闪避"还会掉血) + 播 ShowSlow(时缓)。
    //
    //  挂点选型(踩了两次坑才定下来):
    //    ❌ GetInvincipalLevel.Postfix —— 实测【每帧】都在调, 不是只在中招时,
    //       结果变成"闪一下就时缓一下"
    //    ❌ JsPort.ActorFuncUtils.CheckDodge —— 只被 5 个「若闪避成功」潜能调用,
    //       原生完美闪避发生两次它一次都没被调
    //    ✅ PlayerObj.OnBattleDamageBeHitCalcStage —— 只在真的被打中时调

    internal static BepInEx.Configuration.ConfigEntry<bool> CfgTrigger;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgAbsorb;
    internal static BepInEx.Configuration.ConfigEntry<double> CfgCooldown;

    private static System.Reflection.MethodInfo _showSlow;
    private static float _lastShow;
    private static int _fired, _fireLogged;

    /// <summary>Apply 时解析一次 ShowSlow 的句柄。</summary>
    private static void ResolveShowSlow()
    {
        var t = ResolveTypeByName("JsPort.ActorFuncUtils") ?? ResolveTypeByName("ActorFuncUtils");
        if (t == null) { Plugin.Log?.LogWarning("  [完美闪避] 找不到 ActorFuncUtils, ShowSlow 无法触发"); return; }

        _showSlow = AccessTools.Method(t, "ShowSlow");
        if (_showSlow == null) Plugin.Log?.LogWarning("  [完美闪避] 找不到 ActorFuncUtils.ShowSlow");
        else Plugin.Log?.LogInfo("  [完美闪避] ShowSlow 句柄已就绪 (冲刺被击中时自动触发)");
    }

    /// <summary>挂载点: GamePlay.PlayerObj.OnBattleDamageBeHitCalcStage —— 只在真的被打中时才调。</summary>
    public static int ApplyBeHitHook(Harmony harmony)
    {
        var t = AccessTools.TypeByName("GamePlay.PlayerObj");
        if (t == null) { Plugin.Log?.LogWarning("  [完美闪避] 找不到 GamePlay.PlayerObj"); return 0; }

        var m = AccessTools.Method(t, "OnBattleDamageBeHitCalcStage");
        if (m == null) { Plugin.Log?.LogWarning("  [完美闪避] 找不到 PlayerObj.OnBattleDamageBeHitCalcStage"); return 0; }

        try
        {
            harmony.Patch(m, prefix: new HarmonyMethod(
                typeof(PerfectDodge).GetMethod(nameof(BeHitPrefix), BindingFlags.Static | BindingFlags.Public)));
            Plugin.Log.LogInfo("  [完美闪避] 已挂钩 PlayerObj.OnBattleDamageBeHitCalcStage (冲刺中被击中 -> 免伤 + 时缓)");
            return 1;
        }
        catch (Exception e)
        {
            Plugin.Log?.LogWarning($"  [完美闪避] 挂钩失败: {e.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 冲刺期间被击中 = 完美闪避。
    ///
    /// 为什么是这个挂点(踩过两次坑):
    ///   · ❌ GetInvincipalLevel —— 每帧都在调, 结果变成"闪一下就时缓一下"
    ///   · ❌ CheckDodge        —— 只被 5 个「若闪避成功」潜能调用, 原生闪避根本不经它
    ///   · ✅ OnBattleDamageBeHitCalcStage —— 只在真的被打中时调, 且带 calcStage
    ///
    /// 返回值语义(反汇编 0x1BAB0F0 得到): 累加自 ActionMgr.OnBattleDamageHitCalcStage,
    /// true = "这一击已被脚本处理"。原生完美闪避就是这么免伤的。
    /// 所以我们在 PreCalc(结算前) 阶段返回 true 并跳过原逻辑 = 这一击不结算。
    ///
    /// 保险: 只对本地玩家 / 只在冲刺中 / 只在 PreCalc / 节流 / 全程 try-catch。
    /// </summary>
    public static bool BeHitPrefix(object __instance, object[] __args, ref bool __result)
    {
        try
        {
            if (CfgTrigger?.Value != true) return true;

            var player = __instance as PlayerObj;
            if (player == null) return true;
            if (!DashInvincible.IsLocalPlayerActor(player)) return true;
            if (!DashInvincible.IsDashingNow(player)) return true;

            // PlayerHitResultCalcStage: None=0 PreCalc=1 CalcDone=2 PostHit=3
            // 只在 PreCalc 处理 —— 结算前拦掉, 且避免同一次命中在三个阶段各触发一遍
            int stage = 1;
            if (__args != null && __args.Length > 1 && __args[1] != null)
                stage = Convert.ToInt32(__args[1]);
            if (stage != 1) return true;

            float now = SafeTime();
            if (_logged++ < 20)
                Plugin.Log?.LogInfo($"[完美闪避] 冲刺中被击中 action={ActionName(player)} stage={stage} t={now:F2}");

            // --- 时缓 ---
            float cd = (float)(CfgCooldown?.Value ?? 0.25);
            if (!(now > 0f && _lastShow > 0f && now - _lastShow < cd))
            {
                _lastShow = now;
                _fired++;
                if (_showSlow != null)
                {
                    try { _showSlow.Invoke(null, new object[] { player }); }
                    catch (Exception e)
                    {
                        Plugin.Log?.LogError($"[完美闪避] ShowSlow 调用失败: {(e as TargetInvocationException)?.InnerException ?? e}");
                    }
                }
                if (_fireLogged++ < 20)
                    Plugin.Log?.LogInfo($"[完美闪避] 时缓已触发 (第 {_fired} 次)");
            }

            // --- 免伤: 跳过原逻辑并告诉调用方"这一击已被处理" ---
            if (CfgAbsorb?.Value ?? true)
            {
                __result = true;
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log?.LogError($"[完美闪避] BeHitPrefix 异常: {e}");
            return true;      // 出错就放行, 不影响游戏
        }
    }

    private static string ActionName(ActorBase a)
    {
        try { return a.ActionMgr?.CurrentActionName ?? "-"; }
        catch { return "?"; }
    }

    private static float SafeTime()
    {
        try { return UnityEngine.Time.time; } catch { return 0f; }
    }

    /// <summary>按简名扫描程序集找类型, 不依赖命名空间。</summary>
    private static Type ResolveTypeByName(string fullName)
    {
        var t = AccessTools.TypeByName(fullName);
        if (t != null) return t;

        var simple = fullName.Substring(fullName.LastIndexOf('.') + 1);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts;
            try { ts = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { ts = e.Types; }
            catch { continue; }
            if (ts == null) continue;

            var hit = ts.FirstOrDefault(x => x != null && x.Name == simple);
            if (hit != null) return hit;
        }
        return null;
    }

    private static string SafeAction(ActorBase a)
    {
        try { return a.ActionMgr?.CurrentActionName ?? "-"; }
        catch { return "?"; }
    }

    public static int Apply(Harmony harmony)
    {
        var m = ResolveCheckDodge();
        if (m == null)
        {
            Plugin.Log.LogWarning("  [极限闪避] 找不到 CheckDodge —— 冲刺全程完美闪避未生效");
            return 0;
        }

        try
        {
            harmony.Patch(m, postfix: new HarmonyMethod(
                typeof(PerfectDodge).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.Public)));
            Plugin.Log.LogInfo($"  [极限闪避] 已挂钩 {m.DeclaringType.FullName}.CheckDodge" +
                               $"({string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name))})");

            ResolveShowSlow();
            ApplyBeHitHook(harmony);
            return 1;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"  [极限闪避] 挂钩失败: {e.Message}");
            return 0;
        }
    }

    /// <summary>运行时找方法，不依赖具体命名空间。找不到返回 null。</summary>
    private static MethodBase ResolveCheckDodge()
    {
        // 1) 常见名字先直取
        foreach (var tn in new[] { "JsPort.ActorFuncUtils", "Il2CppJsPort.ActorFuncUtils", "ActorFuncUtils" })
        {
            var t = AccessTools.TypeByName(tn);
            if (t == null) continue;
            var m = AccessTools.Method(t, "CheckDodge");
            if (m != null) return m;
        }

        // 2) 全量扫描: 任何叫 ActorFuncUtils 的类型上的静态 CheckDodge(3 参数)
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts;
            try { ts = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { ts = e.Types; }
            catch { continue; }
            if (ts == null) continue;

            foreach (var t in ts)
            {
                if (t == null || t.Name != "ActorFuncUtils") continue;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    if (m.Name == "CheckDodge" && m.GetParameters().Length == 3)
                        return m;
            }
        }
        return null;
    }

    /// <summary>
    /// __args 对任意签名都可用 —— 不用去枚举精确参数表。
    /// 三个参数里凡是「本地玩家 && 正在冲刺」的 ActorBase，就把判定抬成 true。
    /// </summary>
    public static void Postfix(object[] __args, ref bool __result)
    {
        try
        {
            // 诊断: 每一次 CheckDodge 调用都记下来 —— 用来判断这个函数到底在不在
            // 「原生极限闪避」的路径上(只有抬成 true 时打日志是分不清"没被调用"和"本来就是true"的)
            if (_trace < 40)
            {
                _trace++;
                var names = new StringBuilder();
                foreach (var a in __args)
                {
                    if (a == null) { names.Append("null, "); continue; }
                    var ab = a as ActorBase;
                    names.Append(ab != null
                        ? $"{a.GetType().Name}(action={SafeAction(ab)}), "
                        : $"{a.GetType().Name}, ");
                }
                Plugin.Log?.LogInfo($"[极限闪避:call] CheckDodge({names}) -> {__result}");
            }

            if (__result) return;          // 原本就是闪避成功 —— 不动
            if (!Enabled) return;

            foreach (var a in __args)
            {
                var actor = a as ActorBase;
                if (actor == null) continue;

                if (!DashInvincible.IsLocalPlayerActor(actor)) continue;
                if (!DashInvincible.IsDashingNow(actor)) continue;

                __result = true;
                if (_logged < 20)
                {
                    _logged++;
                    Plugin.Log?.LogInfo($"[极限闪避] 冲刺中判定成功: {actor.ActionMgr?.CurrentActionName}");
                }
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Log?.LogError($"[极限闪避] Postfix 异常: {e}");
        }
    }
}
