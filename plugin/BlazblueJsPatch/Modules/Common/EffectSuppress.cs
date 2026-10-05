using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 特效裁剪 —— 按名单关掉某些特效里的**指定子物体**。
///
/// 起因（2026-10-04，用户点的最后一块）：
///   冲刺时角色身上那层"叠加层"**只有原色特效有**；换色皮肤（esskin_06/10）的同名副本里
///   那个物体**根本不存在**（不是调透明了）。于是用原色特效配换色皮肤时，这层反而是多余的、碍眼。
///   用户要的是**把它关掉**。
///
/// 离线实证（`tools/_prefdiff.py` 结构 diff + `tools/_mbdump.py` 字段树）：
///   `effect/prefab/role/es/es_dash_01.ab` 三个版本
///     原色        : hub + guangzhu01 + Refrac + ★Other(MaterialTinterProxy)
///     esskin_06/10: hub + guangzhu01 + Refrac              ← Other 不存在
///     esskin_12/13: 与原色完全一致
///   `Other` 上 MaterialTinterProxy 的 3 条插值器（duration 全 0.3s）:
///     MaterialColorInterpolator  (0, 0.776, 1.0, a=0) → (0, 0.145, 1.0, a=1)   蓝 + alpha 0→1
///     MaterialFloatInterpolator   1.0 → 1.0                                     (占位)
///     MaterialColorInterpolator  (0,0,0, a=0)         → (1.72, 1.72, 1.72, a=1) 白亮 + alpha 0→1
///   —— 就是"叠加层淡入"本身。
///
/// ⚠⚠ 为什么是"名单"而不是"名字叫 Other 就关"：
///   全表 504 个特效 prefab 里有 **85 个**含 `MaterialTinterProxy@Other`
///   （buff_* / dead_* / portal_* / superarmor_* / flash_* …），一刀切会把它们全废掉。
///   所以规则写成 `特效名片段:子物体名`，一条一条来，默认只放冲刺那一条。
///
/// ⚠ 关法有两种，因为"关掉"能不能真的止住动画取决于注册时机：
///   这个 proxy 的插值器可能被 hub 在 Awake 里收进 InterpolatorSet（那就 SetActive(false)
///   也照样播）。所以给了 <see cref="Mode"/>：
///     Hide    = SetActive(false)（默认，可逆、最保守）
///     Destroy = 直接把那个子物体销毁（更彻底，但会永久改掉这个池化实例）
///   先 Hide 试；若画面里那层还在，把 Mode 改成 Destroy 即可，不用重编译。
/// </summary>
internal static class EffectSuppress
{
    internal static ConfigEntry<bool> CfgEnabled;
    internal static ConfigEntry<string> CfgList;
    internal static ConfigEntry<string> CfgMode;

    /// <summary>已处理过的 (规则, 特效名) 组合 —— 日志只打一次, 不刷屏。</summary>
    private static readonly HashSet<string> _seen = new HashSet<string>();
    private static readonly HashSet<string> _missed = new HashSet<string>();

    private sealed class Rule
    {
        internal string Effect;     // 特效名片段
        internal string Child;      // 子物体名(全等)
    }

    private static Rule[] _rules;
    private static string _parsedFrom;

    private static bool DestroyMode =>
        string.Equals((CfgMode?.Value ?? "Hide").Trim(), "Destroy", StringComparison.OrdinalIgnoreCase);

    private static Rule[] Rules
    {
        get
        {
            string raw = CfgList?.Value ?? "";
            if (_parsedFrom == raw && _rules != null) return _rules;
            var list = new List<Rule>();
            foreach (var part in raw.Split(','))
            {
                string s = part.Trim();
                if (s.Length == 0) continue;
                int c = s.IndexOf(':');
                if (c <= 0 || c >= s.Length - 1)
                {
                    LogEx.Once("suppress|badrule|" + s,
                        $"[特效裁剪] 规则 \"{s}\" 格式不对, 应为 `特效名:子物体名` —— 已忽略");
                    continue;
                }
                list.Add(new Rule { Effect = s.Substring(0, c).Trim(), Child = s.Substring(c + 1).Trim() });
            }
            _rules = list.ToArray();
            _parsedFrom = raw;
            return _rules;
        }
    }

    // ------------------------------------------------------------------ 挂载

    public static int Apply(Harmony harmony)
    {
        if (Rules.Length == 0)
        {
            Plugin.Log?.LogInfo("  [特效裁剪] 名单为空 —— 不挂任何钩子(这是「没事干」, 不是失败)");
            return 0;
        }

        int n = 0;
        var post = new HarmonyMethod(AccessTools.Method(typeof(EffectSuppress), nameof(HubPostfix)));
        var pre = new HarmonyMethod(AccessTools.Method(typeof(EffectSuppress), nameof(HubPrefix)));

        var hub = AccessTools.TypeByName("NOAH.VFX.VFXEffectHub");
        if (hub == null) Plugin.Log?.LogWarning("  [特效裁剪] 找不到 NOAH.VFX.VFXEffectHub");
        else
        {
            foreach (var m in hub.GetMethods(Reflect.All))
            {
                if (m.DeclaringType != hub) continue;
                // Awake 用【前缀】: 越早关掉, 越可能赶在 hub 把插值器收进集合之前。
                if (m.Name == "Awake") { if (Patch(harmony, m, pre, post)) n++; continue; }
                if (m.Name == "DoStart" || m.Name == "Reactivate" || m.Name == "EnableVisualElements")
                { if (Patch(harmony, m, null, post)) n++; }
            }
        }

        var aem = AccessTools.TypeByName("GamePlay.ActorEffectMgr");
        if (aem != null)
            foreach (var m in aem.GetMethods(Reflect.All))
                if (m.Name == "createVisualEffect" && m.DeclaringType == aem && Patch(harmony, m, null, post))
                    n++;

        Plugin.Log?.LogInfo($"  [特效裁剪] 就绪 {n} 处; 名单={CfgList?.Value}, 关法={(DestroyMode ? "Destroy" : "Hide")}");
        return n;
    }

    private static bool Patch(Harmony h, System.Reflection.MethodBase m, HarmonyMethod pre, HarmonyMethod post)
    {
        try
        {
            h.Patch(m, prefix: pre, postfix: post);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log?.LogWarning($"  [特效裁剪] 挂 {m.Name} 失败: {e.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ 钩子

    public static void HubPostfix(object __instance)
        => Run(__instance, "post");

    public static void HubPrefix(object __instance)
        => Run(__instance, "pre");

    private static void Run(object instance, string where)
    {
        try
        {
            if (CfgEnabled?.Value != true || instance == null) return;
            var go = Reflect.Cast<MonoBehaviour>(instance)?.gameObject;
            if (go == null) return;
            Apply(go, where);
        }
        catch (Exception e) { Reflect.WarnOnce("suppress|run", "特效裁剪", e); }
    }

    /// <summary>对规则里匹配上的特效, 关掉它名字全等的子物体。</summary>
    private static void Apply(GameObject go, string where)
    {
        string nm = Reflect.Name(go);
        if (string.IsNullOrEmpty(nm)) return;

        foreach (var r in Rules)
        {
            if (nm.IndexOf(r.Effect, StringComparison.OrdinalIgnoreCase) < 0) continue;

            int hit = 0;
            try
            {
                var ty = Il2CppType.From(typeof(Transform));
                foreach (var c in Reflect.Items(go.GetComponentsInChildren(ty, true)))
                {
                    var tr = Reflect.Cast<Transform>(c);
                    if (tr == null) continue;
                    var child = tr.gameObject;
                    if (child == null) continue;
                    if (!string.Equals(Reflect.Name(child), r.Child, StringComparison.OrdinalIgnoreCase)) continue;

                    if (DestroyMode)
                    {
                        UnityEngine.Object.Destroy(child);
                        hit++;
                    }
                    else
                    {
                        if (child.activeSelf) child.SetActive(false);
                        hit++;
                    }
                }
            }
            catch (Exception e) { LogEx.Err("EffectSuppress.Apply", e); }

            string key = r.Effect + ">" + r.Child + "|" + nm + "|" + where;
            if (hit > 0)
            {
                if (_seen.Add(r.Effect + ">" + r.Child + "|" + nm))
                    Plugin.Log?.LogInfo($"[特效裁剪] \"{nm}\" 里关掉子物体 \"{r.Child}\" x{hit} " +
                                        $"({where}, {(DestroyMode ? "Destroy" : "SetActive(false)")})");
            }
            else if (_missed.Add(r.Effect + ">" + r.Child + "|" + nm))
            {
                // ⚠ 找不到也要说 —— 静默会让"名单没生效"看起来像"没这个问题"。
                Plugin.Log?.LogInfo($"[特效裁剪] \"{nm}\" 里**没找到**子物体 \"{r.Child}\" ({where})" +
                                    $" —— 规则 {r.Effect}:{r.Child} 对不上, 检查名字");
            }
        }
    }
}
