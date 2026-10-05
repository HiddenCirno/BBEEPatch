using System;
using System.Reflection;
using HarmonyLib;
using GamePlay;

namespace BlazblueJsPatch;

/// <summary>
/// 临时诊断：找出「段数限制计数」到底卡在哪个动作上。
///
/// 背景
/// ────
/// 需求是「跳跃重置冲刺计数、冲刺重置跳跃计数」。
/// 静态分析走不通：GameActionLogic 92 个字段里没有"段数限制"，
/// ActionMgr.m_actionChangeLimit 只在 changeActionImp 里被碰过一次，
/// 而配置表里 ActorActionName_/Desc_ 与 TriggerName_/Desc_ 是两套编号，靠 id 对不上。
///
/// 所以运行时抓：ActionMgr.CheckCanChangeToAction(name) 是"能不能切到这个动作"的裁决点。
/// 空中连冲/连跳被卡住时它必然对某个动作名返回 false —— 拿到那个名字就知道计数落在哪。
///
/// ⚠ 教训（第一版把日志刷了 584 行还一条数据没抓到）
/// ────────────────────────────────────────
///   1. 【不要】在 Postfix 热路径里调 AccessTools.Field ——
///      它在找不到时会【自己打一条 Warning】, 每次调用刷一条,
///      而 Owner 在 Il2CppInterop 里是属性不是字段, 于是每次必失败、每次必刷屏。
///   2. 反射句柄一律在 Apply 时解析一次并缓存。
///   3. 身份判断失败时【不能】默默丢弃 —— 那样补丁装了等于没装。
///      这里的策略是: 解析不到 Owner 就关掉过滤, 全部照记(有行数上限兜底)。
/// </summary>
internal static class ActionLimitTrace
{
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;

    private static bool Enabled => CfgEnabled?.Value ?? true;
    private static int _lines;
    private const int MaxLines = 200;

    // 启动时解析一次, 之后只读
    private static MemberInfo _ownerMember;      // ActionMgr.Owner  (属性或字段)
    private static PropertyInfo _curActionProp;  // ActionMgr.CurrentActionName
    private static bool _filterUsable = true;    // 解析不到就去掉玩家过滤

    public static int Apply(Harmony harmony)
    {
        var t = AccessTools.TypeByName("GamePlay.ActionMgr");
        if (t == null) { Plugin.Log?.LogWarning("  [段数限制] 找不到 GamePlay.ActionMgr"); return 0; }

        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // 原生反射, 不经过 HarmonyX —— 找不到也不会刷日志
        _ownerMember = (MemberInfo)t.GetProperty("Owner", F) ?? t.GetField("Owner", F);
        _curActionProp = t.GetProperty("CurrentActionName", F);

        if (_ownerMember == null)
        {
            _filterUsable = false;
            Plugin.Log?.LogWarning("  [段数限制] 解析不到 ActionMgr.Owner —— 已关闭玩家过滤, 全部记录");
        }
        else
        {
            Plugin.Log?.LogInfo($"  [段数限制] Owner 解析为 {( _ownerMember is PropertyInfo ? "属性" : "字段")}");
        }

        int n = 0;
        n += Hook(harmony, t, "CheckCanChangeToAction", nameof(CheckPostfix));
        n += Hook(harmony, t, "ChangeAction", nameof(ChangePostfix));
        return n;
    }

    private static int Hook(Harmony harmony, Type t, string method, string postfix)
    {
        var m = AccessTools.Method(t, method);
        if (m == null) { Plugin.Log?.LogWarning($"  [段数限制] 找不到 ActionMgr.{method}"); return 0; }
        try
        {
            harmony.Patch(m, postfix: new HarmonyMethod(
                typeof(ActionLimitTrace).GetMethod(postfix, BindingFlags.Static | BindingFlags.Public)));
            Plugin.Log.LogInfo($"  [段数限制] 已挂钩 ActionMgr.{method}");
            return 1;
        }
        catch (Exception e)
        {
            Plugin.Log?.LogWarning($"  [段数限制] 挂钩 {method} 失败: {e.Message}");
            return 0;
        }
    }

    /// <summary>被拒绝的动作名 —— 这就是被「段数限制」卡住的那个。</summary>
    public static void CheckPostfix(object __instance, string name, ref bool __result)
    {
        try
        {
            if (!Enabled || __result) return;          // 只记被拒的
            if (!IsLocalPlayer(__instance)) return;
            if (_lines++ >= MaxLines) return;
            Plugin.Log?.LogInfo($"[段数限制] 拒绝切换 -> {name}   (当前动作={Current(__instance)})");
        }
        catch (Exception e) { Plugin.Log?.LogError($"[段数限制] Check 异常: {e.Message}"); }
    }

    /// <summary>跳跃/冲刺的切换结果 —— 成功的和失败的都记, 用来判断卡在哪一道门。</summary>
    public static void ChangePostfix(object __instance, string name, ref bool __result)
    {
        try
        {
            if (!Enabled) return;
            if (__result && string.IsNullOrEmpty(name)) return;
            if (!__result &&
                (string.IsNullOrEmpty(name) ||
                 (name.IndexOf("dash", StringComparison.OrdinalIgnoreCase) < 0 &&
                  name.IndexOf("jump", StringComparison.OrdinalIgnoreCase) < 0))) return;
            if (name.IndexOf("dash", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("jump", StringComparison.OrdinalIgnoreCase) < 0) return;
            if (!IsLocalPlayer(__instance)) return;
            if (_lines++ >= MaxLines) return;
            Plugin.Log?.LogInfo($"[段数限制] 切换{(__result ? "" : "【失败】")} -> {name}");
        }
        catch (Exception e) { Plugin.Log?.LogError($"[段数限制] Change 异常: {e.Message}"); }
    }

    private static bool IsLocalPlayer(object actionMgr)
    {
        if (!_filterUsable) return true;              // 解析不到身份 -> 不过滤, 保证有数据
        var owner = ReadOwner(actionMgr) as ActorBase;
        if (owner == null) return true;
        return DashInvincible.IsLocalPlayerActor(owner);
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
        try { return _curActionProp?.GetValue(actionMgr) as string ?? "-"; }
        catch { return "?"; }
    }
}
