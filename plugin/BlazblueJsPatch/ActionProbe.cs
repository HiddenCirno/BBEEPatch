using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace BlazblueJsPatch;

/// <summary>
/// 临时诊断：打印玩家用过的**每一个动作名**（按名字去重）。
///
/// 为什么需要它
/// ────────────
/// 查「纹章解放」的本体时一直卡在同一个混淆点上：
/// **分不清"那个特效没生成"和"那个技能你根本没按"**。
///
/// 现有的动作名日志是**被过滤的** —— 只有段数限制/冲刺相关的 trace 会打印，
/// 所以一局下来日志里只有 `UltraDashEX` / `jump` 这几个，看不出技能到底按没按。
///
/// 这个探针挂在 `ActionMgr.ChangeAction` 上，**不设任何过滤**，
/// 按名字去重（上限 500 个不同名字，不会像"前 N 条"那样截断）。
///
/// 用法：按一次目标技能，然后在日志里找新出现的动作名 ——
/// 拿它跟同一时刻生成的 `[特效换色:seen]` 特效名对照，就能确定
/// 「这个技能用的是什么动作」和「它到底生成了什么特效」。
/// </summary>
internal static class ActionProbe
{
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;

    private static readonly HashSet<string> _seen = new HashSet<string>();
    private static MemberInfo _ownerMember;

    public static int Apply(Harmony harmony)
    {
        var t = AccessTools.TypeByName("GamePlay.ActionMgr");
        if (t == null) { Plugin.Log?.LogWarning("  [动作探针] 找不到 GamePlay.ActionMgr"); return 0; }

        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        _ownerMember = (MemberInfo)t.GetProperty("Owner", F) ?? t.GetField("Owner", F);

        var m = AccessTools.Method(t, "ChangeAction");
        if (m == null) { Plugin.Log?.LogWarning("  [动作探针] 找不到 ActionMgr.ChangeAction"); return 0; }

        try
        {
            harmony.Patch(m, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(ActionProbe), nameof(Postfix))));
            Plugin.Log.LogInfo("  [动作探针] 已挂钩 ActionMgr.ChangeAction (按名字去重打印所有动作)");
            return 1;
        }
        catch (Exception e)
        {
            Plugin.Log?.LogWarning($"  [动作探针] 挂钩失败: {e.Message}");
            return 0;
        }
    }

    public static void Postfix(object __instance, string name)
    {
        try
        {
            if (CfgEnabled?.Value != true) return;
            if (string.IsNullOrEmpty(name)) return;
            if (__instance == null || _ownerMember == null) return;

            // 只看本地玩家 —— 敌人的动作名会把日志淹掉
            var owner = ReadMember(__instance, _ownerMember);
            if (owner == null || !DashInvincible.IsLocalPlayerActor(owner as GamePlay.ActorBase)) return;

            if (_seen.Count >= 500 || !_seen.Add(name)) return;
            Plugin.Log?.LogInfo($"[动作探针] 动作 \"{name}\"  (已见 {_seen.Count} 种)");
        }
        catch { }
    }

    private static object ReadMember(object obj, MemberInfo mi)
    {
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
