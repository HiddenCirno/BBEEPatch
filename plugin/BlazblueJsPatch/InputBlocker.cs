using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 面板打开时屏蔽游戏输入。
///
/// 为什么不是简单地 Event.Use()
/// ──────────────────────────
/// `Event.current.Use()` 只影响 IMGUI 的事件派发, 管不到游戏逻辑 ——
/// 本作的输入是走 `UnityEngine.Input` 那套静态 API 读的(在 Update 里),
/// 所以必须在**源头**把它挡掉。
///
/// 做法: 给 `UnityEngine.Input` 上那批"读输入"的静态方法挂 Prefix,
/// 面板开着时直接返回中性值(bool=false / float=0 / Vector=zero / string=""),
/// 并跳过原方法。面板关着就原样放行。
///
/// 时序说明
/// ────────
/// Unity 一帧的顺序是 Update → LateUpdate → OnGUI。我们的面板在 OnGUI 里画,
/// 所以"面板已开"这个状态对【本帧的 Update】来说是上一帧的值 —— 有一帧延迟。
/// 这是这类屏蔽的固有代价; 反过来也意味着**关面板的那一帧**游戏就能立刻收到输入了。
/// </summary>
internal static class InputBlocker
{
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgBlockMouse;

    /// <summary>
    /// 面板开着 = 游戏不该收到输入。
    ///
    /// ⚠ 新增面板时**必须把它的 IsOpen 加到这里**，否则在新面板上点按钮会同时被游戏吃掉。
    ///   （既存的漏网之鱼: BulletLab 开了一直没接进来 —— 它的按钮点击会漏给游戏。
    ///     本项目对这类"漏接线"有纪律: 每个"没有"都要说清为什么, 而这里是**根本没人检查过**。）
    /// </summary>
    private static bool Active
        => CfgEnabled?.Value != false && (ConfigPanel.IsOpen || BulletLab.IsOpen || LiveInspector.IsOpen);

    private static readonly HashSet<string> _patched = new HashSet<string>();

    /// <summary>要屏蔽的方法名。按返回类型自动挑对应的 Prefix。</summary>
    private static readonly string[] Names =
    {
        "GetKey", "GetKeyDown", "GetKeyUp",
        "GetMouseButton", "GetMouseButtonDown", "GetMouseButtonUp",
        "GetButton", "GetButtonDown", "GetButtonUp",
        "GetAxis", "GetAxisRaw",
        "get_anyKey", "get_anyKeyDown", "get_inputString",
        "get_mouseScrollDelta", "get_mousePosition",
        "ResetInputAxes",
    };

    private static readonly string[] MouseOnly =
    {
        "GetMouseButton", "GetMouseButtonDown", "GetMouseButtonUp",
        "get_mouseScrollDelta", "get_mousePosition",
    };

    public static int Apply(Harmony harmony)
    {
        var t = FindType("UnityEngine.Input");
        if (t == null) { Plugin.Log?.LogWarning("  [输入屏蔽] 找不到 UnityEngine.Input"); return 0; }

        var boolMi = AccessTools.Method(typeof(InputBlocker), nameof(BoolPrefix));
        var floatMi = AccessTools.Method(typeof(InputBlocker), nameof(FloatPrefix));
        var strMi = AccessTools.Method(typeof(InputBlocker), nameof(StringPrefix));
        var v2Mi = AccessTools.Method(typeof(InputBlocker), nameof(Vec2Prefix));
        var v3Mi = AccessTools.Method(typeof(InputBlocker), nameof(Vec3Prefix));
        var voidMi = AccessTools.Method(typeof(InputBlocker), nameof(VoidPrefix));

        int n = 0;
        foreach (var name in Names)
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != name) continue;

                HarmonyMethod pre;
                var rt = m.ReturnType;
                if (rt == typeof(void)) pre = new HarmonyMethod(voidMi);
                else if (rt == typeof(bool)) pre = new HarmonyMethod(boolMi);
                else if (rt == typeof(float)) pre = new HarmonyMethod(floatMi);
                else if (rt == typeof(string)) pre = new HarmonyMethod(strMi);
                else if (rt == typeof(Vector2)) pre = new HarmonyMethod(v2Mi);
                else if (rt == typeof(Vector3)) pre = new HarmonyMethod(v3Mi);
                else continue;

                try
                {
                    harmony.Patch(m, prefix: pre);
                    n++;
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning($"  [输入屏蔽] 挂 Input.{name} 失败: {e.Message}");
                }
            }
            _patched.Add(name);
        }

        Plugin.Log.LogInfo($"  [输入屏蔽] 已挂 {n} 个 Input 读接口 (面板开启时游戏收不到输入)");
        return n;
    }

    private static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { var x = asm.GetType(fullName, false); if (x != null) return x; }
            catch { }
        }
        return null;
    }

    /// <summary>鼠标类接口是否也要挡 —— 面板本身靠 IMGUI 事件, 不依赖 Input, 所以挡掉更干净。</summary>
    private static bool Block(string name)
    {
        if (!Active) return false;
        bool blockMouse = CfgBlockMouse == null || CfgBlockMouse.Value;
        if (blockMouse) return true;
        return Array.IndexOf(MouseOnly, name) < 0;
    }

    public static bool BoolPrefix(MethodBase __originalMethod, ref bool __result)
    {
        if (!Block(__originalMethod?.Name)) return true;
        __result = false;
        return false;
    }

    public static bool FloatPrefix(MethodBase __originalMethod, ref float __result)
    {
        if (!Block(__originalMethod?.Name)) return true;
        __result = 0f;
        return false;
    }

    public static bool StringPrefix(MethodBase __originalMethod, ref string __result)
    {
        if (!Block(__originalMethod?.Name)) return true;
        __result = "";
        return false;
    }

    public static bool Vec2Prefix(MethodBase __originalMethod, ref Vector2 __result)
    {
        if (!Block(__originalMethod?.Name)) return true;
        __result = Vector2.zero;
        return false;
    }

    public static bool Vec3Prefix(MethodBase __originalMethod, ref Vector3 __result)
    {
        if (!Block(__originalMethod?.Name)) return true;
        __result = Vector3.zero;
        return false;
    }

    public static bool VoidPrefix(MethodBase __originalMethod)
    {
        if (!Block(__originalMethod?.Name)) return true;
        return false;   // ResetInputAxes: 面板开着时直接吞掉原方法
    }
}
