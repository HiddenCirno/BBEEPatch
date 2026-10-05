using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// IMGUI 兼容层 —— 让 BepInEx.ConfigurationManager 这类插件能跑起来。
///
/// 问题
/// ────
/// 本作是 IL2CPP, 且关闭的引擎代码被 UnityLinker 做了【方法级 strip】。
/// 于是 global-metadata.dat 里没有 GUI.DrawTexture / GUILayout.Window 等一批方法。
///
/// 但 BepInEx 的 interop 程序集 (BepInEx/interop/UnityEngine.IMGUIModule.dll) 由
/// Cpp2IL 生成, 它【把引擎 API 表面补全了】—— 缺的那些方法体是一根共享桩, 一调用就抛:
///     System.NotSupportedException: Method unstripping failed
///
/// 实测对比 (tools/refs_vs_meta.py):
///   dump.cs(元数据)      : GUI 66 个成员, GUILayout 36 个  ← 没有 Window/DrawTexture
///   interop 程序集        : GUI 240 个,     GUILayout 214 个 ← 全都有
///   ConfigurationManager 引用 129 个 Unity 成员, 其中【只有 13 个是桩】
///
/// 所以不需要 fork 插件, 也不需要从 Mono 版 Unity 拉原版 dll(那条路在 IL2CPP 下
/// 只能拿到编译期符号, 运行时绑不上)。这些桩【有真实 IL 方法体】(RVA 非 0),
/// Harmony 直接顶掉即可。
///
/// 13 个桩里:
///   DragWindow / FocusControl / FocusWindow / SetNextControlName / set_m_Ptr  -> 空实现
///   get_tooltip                                                             -> ""
///   其余用【已验证存活】的 API 自己实现 (GUILayout.BeginArea /
///   GUI.TextField / Graphics.DrawTexture / Event.current / GUILayoutUtility.GetRect)
///
/// ⚠⚠ 一条比"哪些是桩"更要紧的规则: **重载也要对得上号。**
///   不是"方法名在 metadata 里就行"—— 名字在、但那个【重载】不在, 一样是空桩。
///   本项目真实中过的两枪:
///     · `GUI.TextField(rect, text, maxLength, style)` —— 四参重载不存在(只有三参),
///       异常被 catch 吞掉 → 字符型配置的输入框一个像素都不画。
///     · `GUI.Box(rect, text, style)` —— 三参(string)重载不存在
///       (只有 `Box(Rect, string)` 和 `Box(Rect, GUIContent, GUIStyle)`)。
///   现在这两处都换成 metadata 里【逐字确认过】的重载: 三参 `GUI.TextField` / `GUI.Label`。
///   加任何新控件前, 先 `grep` dump.cs 确认重载, 不要凭 IDE 补全。
/// </summary>
internal static class ImguiCompat
{
    /// <summary>要顶掉的桩: 类型全名 -> 方法名(该名字的所有重载)</summary>
    private static readonly (string Type, string[] Methods)[] Targets =
    {
        ("UnityEngine.GUI", new[]
        {
            "DrawTexture", "DragWindow", "FocusControl", "FocusWindow",
            "SetNextControlName", "SelectionGrid", "get_tooltip",
        }),
        ("UnityEngine.GUILayout", new[]
        {
            "Window", "TextField", "TextArea", "MaxWidth",
        }),
        ("UnityEngine.GUIStyle", new[]
        {
            "set_m_Ptr", "op_Implicit",
        }),
    };

    // ------------------------------------------------------------------ 可调参数

    /// <summary>DrawTexture 的实现方式: Box(queue 正确) / Graphics(立即模式, 会盖住内容) / Off</summary>
    internal static BepInEx.Configuration.ConfigEntry<string> CfgTexMode;
    /// <summary>是否在窗口下再垫一层自己的底板</summary>
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgPanelBg;
    /// <summary>打印前若干次调用, 用于排查</summary>
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgVerbose;
    /// <summary>是否启用忠实重建的 GUI.Window 语义(WindowEmu)。默认关 —— 见 DoWindow 里的说明。</summary>
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgUseRealWindow;

    private static string TexMode => CfgTexMode?.Value ?? "Box";
    private static bool PanelBg => CfgPanelBg?.Value ?? false;
    private static bool Verbose => CfgVerbose?.Value ?? false;

    private static int _trace;

    private static void Trace(string msg)
    {
        if (!Verbose || _trace > 120) return;
        _trace++;
        Plugin.Log?.LogInfo($"[IMGUI:trace] {msg}");
    }

    // ------------------------------------------------------------------ 事件诊断
    //
    // 症状: 面板画得出来, 但按钮点不动、折叠区展不开。
    // 这类问题只有两种可能, 必须先分清是哪一种, 否则全是白猜:
    //   (A) 输入事件(MouseDown/MouseUp/...)根本没送进 OnGUI —— 只有 Repaint/Layout
    //   (B) 事件送到了, 但控件矩形/命中判定不对
    // 所以这里统计【每种事件类型各来了多少次】, 一眼就能区分。

    private static readonly Dictionary<string, int> _evtCount = new Dictionary<string, int>();
    private static readonly HashSet<string> _evtSeen = new HashSet<string>();
    private static int _evtTotal;
    private static int _winCalls;

    /// <summary>事件统计的采样上限。
    /// ⚠ 这是【每帧每事件】都会跑的热路径, 而且 `e.type.ToString()` 每次都分配字符串。
    ///   诊断目的在前几千个事件时就达成了, 之后继续统计纯属给 GC 找事 ——
    ///   所以到顶就整个短路(不再 ToString、不再动字典), 只留一个廉价计数器。</summary>
    private const int EventSampleCap = 2000;

    private static void NoteEvent()
    {
        if (_evtTotal >= EventSampleCap) { _evtTotal++; return; }

        string k;
        int raw = -1;
        try
        {
            var e = Event.current;
            if (e == null) { k = "null"; }
            else
            {
                k = e.type.ToString();
                try { raw = (int)e.type; } catch { }
            }
        }
        catch { k = "err"; }

        _evtCount.TryGetValue(k, out var c);
        _evtCount[k] = c + 1;
        _evtTotal++;

        try { var ev = Event.current; if (ev != null) NoteKey(ev); } catch { }

        if (_evtSeen.Add(k))
            Plugin.Log?.LogInfo($"[IMGUI:evt] 首次收到事件类型 {k} (raw={raw})");
        else if (raw >= 0 && k.IndexOf("repaint", StringComparison.OrdinalIgnoreCase) >= 0 && _evtCount[k] == 2)
            Plugin.Log?.LogInfo($"[IMGUI:evt] Repaint 计数已到 2, raw={raw}");

        // 只在前几次报一下底层输入状态 —— 用来区分
        //   "IMGUI 事件流被过滤了"(输入 API 正常, 只是不给 OnGUI)
        //   "鼠标根本没被识别"(输入 API 自己也是死的)
        if (_evtTotal <= 3)
        {
            string info;
            try
            {
                info = $"mousePresent={Input.mousePresent} pos={Input.mousePosition} " +
                       $"btn0={Input.GetMouseButton(0)} lock={Cursor.lockState} vis={Cursor.visible} " +
                       $"screen={Screen.width}x{Screen.height}";
            }
            catch (Exception ie) { info = $"读输入状态失败: {ie.Message}"; }
            Plugin.Log?.LogInfo($"[IMGUI:evt] 输入状态 #{_evtTotal}: {info}");
        }

        if (Verbose && _evtTotal % 300 == 0)
        {
            var parts = _evtCount.OrderByDescending(p => p.Value)
                                 .Select(p => $"{p.Key}={p.Value}");
            Plugin.Log?.LogInfo($"[IMGUI:evt] 累计 {_evtTotal}: {string.Join(" ", parts)}");
        }
    }

    // ------------------------------------------------------------------ 自检按钮
    //
    // 光看 CM 的按钮没用 —— 得先确认"我们自己的按钮能不能点"。
    // 挂 CM 的 OnGUI 做 Postfix, 画一个我们完全掌控的小窗。
    //   点了有反应 -> IMGUI 输入是通的, 问题在 CM 自己的布局/命中
    //   点了没反应 -> 输入事件压根没进来, 整个方向要换

    private static bool _postFixSeen;
    private static int _cmOnGui;

    private static Harmony _harmony;
    private static bool _selfTestAttached;
    private static int _selfTestTries;
    /// <summary>延迟失败重试的上限 —— 不是为了性能, 是为了别在真挂不上时无限刷日志。</summary>
    private const int SelfTestMaxTries = 600;   // ≈ 10 秒(每帧 OnGUI 至少一次)

    public static int ApplySelfTest(Harmony harmony)
    {
        _harmony = harmony;
        return TryAttachSelfTest() ? 1 : 0;
    }

    /// <summary>
    /// 找到 ConfigurationManager 的 OnGUI 并挂上自检窗。
    ///
    /// 坑: 插件 Load() 时 CM 的程序集【还没进 AppDomain】, AccessTools.TypeByName 直接
    /// 返回 null(实测日志: "Could not find type named ConfigurationManager.ConfigurationManager",
    /// 而同一份日志里明明有 Il2CppInterop 注册它的记录)。这是加载顺序问题, 不是类型不存在。
    /// 所以改成: 每次 OnGUI 都试一次, 直到挂上为止。
    /// </summary>
    private static bool TryAttachSelfTest()
    {
        if (_selfTestAttached || _harmony == null) return _selfTestAttached;
        if (_selfTestTries++ > SelfTestMaxTries)
        {
            if (_selfTestTries == SelfTestMaxTries + 1)
                Plugin.Log?.LogWarning("  [IMGUI] 自检按钮放弃挂载(尝试次数用尽), ConfigurationManager 可能没装");
            return false;
        }

        // ⚠ 顺序很重要: 实测真正被 Unity 调用的是【外层】的 ConfigurationManager.ConfigurationManager.OnGUI
        //   (证据: Event.Use 探针抓到的栈是
        //      UnityEngine.GUI.Button <- ConfigurationManager.ConfigurationManager.OnGUI
        //      <- Trampoline_...ConfigurationManagerBehaviour.OnGUI )
        //   而上一版我挂的是嵌套类型 +ConfigurationManagerBehaviour —— 挂错了,
        //   所以那条 postfix 一次都没跑过, 自检窗自然也从没出现。
        var t = FindType("ConfigurationManager.ConfigurationManager")
             ?? FindType("ConfigurationManager.ConfigurationManager+ConfigurationManagerBehaviour");
        if (t == null) return false;

        var onGui = t.GetMethod("OnGUI", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (onGui == null) return false;

        try
        {
            _harmony.Patch(onGui, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(ImguiCompat), nameof(OnGuiSelfTest))));
            _selfTestAttached = true;
            Plugin.Log.LogInfo($"  [IMGUI] 自检按钮已挂到 {t.FullName}.OnGUI (第 {_selfTestTries} 次尝试)");
            return true;
        }
        catch (Exception e)
        {
            _selfTestTries = SelfTestMaxTries + 1;
            Plugin.Log?.LogWarning($"  [IMGUI] 自检按钮挂载失败: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 自己按名字找类型, 不走 AccessTools.TypeByName。
    /// 后者会 GetTypes() 枚举整个程序集 —— 而 interop 程序集里有大量"格式非法"的类型
    /// (unity 的 strip 副作用), 每枚举一次就刷一屏 TypeLoadException 警告。
    /// Assembly.GetType(name, false) 只查元数据表, 不枚举, 干净得多。
    /// </summary>
    private static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    public static void OnGuiSelfTest()
    {
        NoteEvent();

        // 这里是 CM 整个 OnGUI 跑完之后 —— 最外层, 不会污染 CM 窗口的布局组。
        if (!_postFixSeen)
        {
            _postFixSeen = true;
            Plugin.Log?.LogInfo("[IMGUI] CM OnGUI 的 Postfix 已生效 (自绘配置面板画在这里)");
        }

        // 常开计数器: CM 的 OnGUI 每帧都在跑吗?
        if (++_cmOnGui % 300 == 0)
            Plugin.Log?.LogInfo($"[IMGUI:cnt] CM.OnGUI 已调用 {_cmOnGui} 次 " +
                                $"(当前事件={TryEventType()})");

        // ⚠ 这里【只】画键盘面板 —— 它严格 try/finally 配对, 任何异常都不会把
        //   GUILayout 的组栈搞不平衡。之前那个自检窗就是因为缺 finally 而污染了整个
        //   IMGUI 状态, 反过来把 CM 的控件一起弄废了(见文件里 DrawSelfTest 的墓碑注释)。
        //
        // ⚠ 这只是【兜底】。面板的正门是 UiHost(自建 MonoBehaviour 的 OnGUI) ——
        //   挂在 CM 上意味着"必须先按 F1 唤出那个本来就点不动的原版窗口",
        //   这是上一版的架构错误。UiHost 挂成功后这里会直接返回, 不会重复绘制
        //   (重复绘制会让 GUILayout 组栈当场失衡)。
        ConfigPanel.OnGuiFromCmFallback();
    }


    // ------------------------------------------------------------------ 抓"谁吃掉了鼠标事件"
    //
    // IMGUI 的事件是【按脚本顺序依次派发】的: 前面任何一个 OnGUI 调了 Event.Use(),
    // 后面的脚本就再也看不到这个事件。实测键盘事件能到 CM、鼠标事件到不了,
    // 而 Input.mousePosition 是活的 —— 就是有人把鼠标事件吃掉了。
    //
    // Unity 的控件在【原生代码】里消费事件时不会走托管 Event.Use(), 所以这里抓的是
    // 显式调用 Use() 的那种。抓到了就直接看到调用栈上的类名; 抓不到说明是原生全屏控件
    // 消费的(那种情况看谁在消费得换别的办法)。

    private static readonly HashSet<string> _useSeen = new HashSet<string>();
    private static int _useLogged;

    public static bool EventUsePrefix(Event __instance)
    {
        try
        {
            var t = __instance?.type;
            if (t == EventType.MouseDown || t == EventType.MouseUp ||
                t == EventType.MouseDrag || t == EventType.ScrollWheel ||
                t == EventType.MouseMove)
            {
                // 栈要够深 —— 上面几层全是 Il2CppInterop 的跳板(il2cpp_runtime_invoke /
                // 泛型 shim), 只取 4 层的话看到的全是噪音, 完全看不出调用方是谁。
                // 这里取 40 层, 再把跳板帧滤掉, 只留"有意义的类名"。
                var st = new System.Diagnostics.StackTrace(1, false);
                var frames = st.GetFrames();
                var sb = new System.Text.StringBuilder();
                int kept = 0;
                for (int i = 0; i < frames.Length && kept < 10; i++)
                {
                    var m = frames[i].GetMethod();
                    if (m == null) continue;
                    var tn = m.DeclaringType?.FullName ?? "?";
                    if (tn.IndexOf("Il2CppInterop", StringComparison.Ordinal) >= 0) continue;
                    if (tn.IndexOf("HarmonyLib", StringComparison.Ordinal) >= 0) continue;
                    if (tn.IndexOf("System.Diagnostics", StringComparison.Ordinal) >= 0) continue;
                    if (tn.IndexOf("ImguiCompat", StringComparison.Ordinal) >= 0) continue;
                    sb.Append(tn).Append('.').Append(m.Name).Append(" <- ");
                    kept++;
                }
                var key = sb.ToString();
                if (_useLogged < 8 && _useSeen.Add(key))
                {
                    _useLogged++;
                    Plugin.Log?.LogInfo($"[IMGUI:use] {t} 被 Event.Use() 消费: {key}");
                }
            }
        }
        catch { }
        return true;   // 绝不改变原行为
    }

    public static int ApplyEventUseProbe(Harmony harmony)
    {
        var t = FindType("UnityEngine.Event");
        if (t == null) { Plugin.Log?.LogWarning("  [IMGUI] 找不到 UnityEngine.Event"); return 0; }
        var use = t.GetMethod("Use", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
               ?? t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                   .FirstOrDefault(m => m.Name == "Use" && m.GetParameters().Length == 0);
        if (use == null) { Plugin.Log?.LogWarning("  [IMGUI] Event.Use 不存在, 抓不到"); return 0; }
        try
        {
            harmony.Patch(use, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(ImguiCompat), nameof(EventUsePrefix))));
            Plugin.Log.LogInfo("  [IMGUI] Event.Use 探针已挂 (抓谁消费了鼠标事件)");
            return 1;
        }
        catch (Exception e)
        {
            Plugin.Log?.LogWarning($"  [IMGUI] Event.Use 探针挂载失败: {e.Message}");
            return 0;
        }
    }

    // 键盘事件是通的 —— 顺便验证一下能不能靠键盘操作用户界面,
    // 万一鼠标这条路修不动, 至少还有一条能用的路。
    // ------------------------------------------------------------------ Event.current 探针
    //
    // 上一轮的教训: NoteEvent 只在 DoWindow 里采样, 而 DoWindow 只在 CM 调
    // GUILayout.Window 时才跑 —— 等轮到那里事件早被别人 Use() 光了, 于是得出
    // "鼠标事件没进来"的【错误结论】。
    // 这次直接在 Event.current 这个源头采: 不管是谁、在哪一步读的, 都跑不掉。
    // 顺便把 IMGUI 的鼠标坐标和裸 Input.mousePosition 印在一起 —— 两者不一致就是
    // 坐标系/缩放错位(那会导致"看着点到了、其实点在别处")。

    private static int _mouseProbe;

    public static void EventCurrentPostfix(ref Event __result)
    {
        try
        {
            if (__result == null || _mouseProbe >= 14) return;
            var t = __result.type;
            if (t != EventType.MouseDown && t != EventType.MouseUp &&
                t != EventType.MouseDrag && t != EventType.MouseMove) return;

            _mouseProbe++;
            Vector3 raw;
            try { raw = Input.mousePosition; } catch { raw = new Vector3(-1, -1, 0); }
            Plugin.Log?.LogInfo($"[IMGUI:mouse] {t} imguiPos={__result.mousePosition} " +
                                $"btn={__result.button} rawPos=({raw.x:F0},{raw.y:F0})");
        }
        catch { }
    }

    public static int ApplyMouseProbe(Harmony harmony)
    {
        var t = FindType("UnityEngine.Event");
        if (t == null) return 0;
        var get = t.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
        if (get == null) { Plugin.Log?.LogWarning("  [IMGUI] 找不到 Event.current 的 getter"); return 0; }
        try
        {
            harmony.Patch(get, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(ImguiCompat), nameof(EventCurrentPostfix))));
            Plugin.Log.LogInfo("  [IMGUI] Event.current 探针已挂 (直接看原始事件流)");
            return 1;
        }
        catch (Exception e)
        {
            Plugin.Log?.LogWarning($"  [IMGUI] Event.current 探针挂载失败: {e.Message}");
            return 0;
        }
    }

    private static readonly HashSet<string> _keySeen = new HashSet<string>();

    private static void NoteKey(Event e)
    {
        try
        {
            if (e.type != EventType.KeyDown) return;
            var k = e.keyCode.ToString();
            if (_keySeen.Count < 40 && _keySeen.Add(k))
                Plugin.Log?.LogInfo($"[IMGUI:key] 收到按键 {k}");
        }
        catch { }
    }

    // ------------------------------------------------------------------ 安装

    public static int Apply(Harmony harmony)
    {
        int n = 0, stubs = 0;

        var voidMi = AccessTools.Method(typeof(ImguiCompat), nameof(VoidPrefix));
        var strMi = AccessTools.Method(typeof(ImguiCompat), nameof(StringPrefix));
        var intMi = AccessTools.Method(typeof(ImguiCompat), nameof(IntPrefix));
        var rectMi = AccessTools.Method(typeof(ImguiCompat), nameof(RectPrefix));
        var optMi = AccessTools.Method(typeof(ImguiCompat), nameof(OptionPrefix));
        var styleMi = AccessTools.Method(typeof(ImguiCompat), nameof(StylePrefix));

        foreach (var (typeName, methods) in Targets)
        {
            var t = AccessTools.TypeByName(typeName);
            if (t == null) { Plugin.Log.LogWarning($"  [IMGUI] 找不到类型 {typeName}"); continue; }

            foreach (var name in methods)
            {
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Static | BindingFlags.Instance))
                {
                    if (m.Name != name) continue;

                    // 按返回类型选对应的 Prefix —— 这样不用去枚举每个重载的精确签名
                    MethodInfo prefix;
                    var rt = m.ReturnType;
                    if (rt == typeof(void)) prefix = voidMi;
                    else if (rt == typeof(string)) prefix = strMi;
                    else if (rt == typeof(int)) prefix = intMi;
                    else if (rt == typeof(Rect)) prefix = rectMi;
                    else if (rt == typeof(GUIStyle)) prefix = styleMi;
                    else if (rt.Name == "GUILayoutOption") prefix = optMi;
                    else continue;

                    try
                    {
                        harmony.Patch(m, prefix: new HarmonyMethod(prefix));
                        n++;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"  [IMGUI] 顶掉 {typeName}.{name} 失败: {e.Message}");
                    }
                }
                stubs++;
            }
        }

        Plugin.Log.LogInfo($"  [IMGUI] 已顶掉 {n} 个引擎桩方法 (覆盖 {stubs} 个名字)");
        return n;
    }

    // ------------------------------------------------------------------ Prefix 分派
    // Harmony 的 __args 对任意签名都可用, 所以一个 Prefix 能吃掉同名的一整组重载。

    public static bool VoidPrefix(MethodBase __originalMethod, object[] __args)
    {
        try
        {
            switch (__originalMethod.Name)
            {
                case "DrawTexture":
                    return DoDrawTexture(__args);
                default:
                    // DragWindow / FocusControl / FocusWindow / SetNextControlName / set_m_Ptr
                    // —— 对"没有多窗口管理器"的我们来说全是空操作, 直接吞掉
                    return false;
            }
        }
        catch (Exception e)
        {
            Plugin.Log?.LogError($"[IMGUI] {__originalMethod.Name} 异常: {e}");
            return false;   // 绝不能放行, 放行就是"Method unstripping failed"
        }
    }

    public static bool StringPrefix(MethodBase __originalMethod, object[] __args, ref string __result)
    {
        try
        {
            switch (__originalMethod.Name)
            {
                case "get_tooltip":
                    __result = "";
                    break;
                case "TextField":
                    __result = DoTextField(__args, false);
                    break;
                case "TextArea":
                    __result = DoTextField(__args, true);
                    break;
                default:
                    __result = null;
                    break;
            }
        }
        catch (Exception e)
        {
            Plugin.Log?.LogError($"[IMGUI] {__originalMethod.Name} 异常: {e}");
            __result = null;
        }
        return false;
    }

    public static bool IntPrefix(MethodBase __originalMethod, object[] __args, ref int __result)
    {
        try
        {
            if (__originalMethod.Name == "SelectionGrid")
                __result = DoSelectionGrid(__args, __originalMethod.DeclaringType?.Name == "GUILayout");
        }
        catch (Exception e) { Plugin.Log?.LogError($"[IMGUI] SelectionGrid 异常: {e}"); }
        return false;
    }

    public static bool RectPrefix(MethodBase __originalMethod, object[] __args, ref Rect __result)
    {
        try
        {
            if (__originalMethod.Name == "Window")
                __result = DoWindow(__args);
        }
        catch (Exception e)
        {
            Plugin.Log?.LogError($"[IMGUI] Window 异常: {e}");
            if (__args.Length > 1 && __args[1] is Rect r) __result = r;
        }
        return false;
    }

    public static bool OptionPrefix(MethodBase __originalMethod, object[] __args, ref GUILayoutOption __result)
    {
        try
        {
            if (__originalMethod.Name == "MaxWidth" && __args.Length > 0 && __args[0] is float w)
                __result = MakeOption("maxWidth", w);
        }
        catch (Exception e) { Plugin.Log?.LogError($"[IMGUI] MaxWidth 异常: {e}"); }
        return false;
    }

    public static bool StylePrefix(MethodBase __originalMethod, object[] __args, ref GUIStyle __result)
    {
        // GUIStyle.op_Implicit(string) —— 极少被调用, 给个兜底
        try
        {
            var skin = GUI.skin;
            __result = skin != null ? skin.label : null;
        }
        catch { __result = null; }
        return false;
    }

    // ------------------------------------------------------------------ 实现

    private static Texture2D _px;
    private static GUIStyle _panel;
    private static bool _panelTried;

    private static GUIStyle PanelStyle()
    {
        if (_panelTried) return _panel;
        _panelTried = true;
        try
        {
            _px = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _px.SetPixel(0, 0, new Color(0.06f, 0.06f, 0.08f, 0.95f));
            _px.Apply();
            _panel = new GUIStyle();
            _panel.normal.background = _px;
        }
        catch (Exception e) { Plugin.Log?.LogError($"[IMGUI] 建背景样式失败: {e}"); }
        return _panel;
    }

    /// <summary>
    /// 等价于 GUI.DrawTexture。
///
    ///
    /// 默认走 GUI.Box + 背景样式 —— 这正是 ConfigurationManager 自己 non-IL2CPP 分支的做法。
    /// 关键原因: GUI.DrawTexture 是【排队】绘制, 会按调用顺序与其它 GUI 内容合成;
    /// 而 Graphics.DrawTexture 是【立即】绘制, 不受 IMGUI 队列约束 —— 用它的话,
    /// CM 先画的窗口背景会盖在随后排队的所有控件之上, 结果就是"只有一块板子"。
    /// </summary>
    private static readonly Dictionary<IntPtr, GUIStyle> _texStyles = new Dictionary<IntPtr, GUIStyle>();

    private static bool DoDrawTexture(object[] a)
    {
        if (a.Length < 2) return false;
        var rect = (Rect)a[0];
        var tex = a[1] as Texture2D;
        if (tex == null) return false;

        var mode = TexMode;
        if (mode.Equals("Off", StringComparison.OrdinalIgnoreCase)) return false;

        if (!mode.Equals("Graphics", StringComparison.OrdinalIgnoreCase))
        {
            var style = StyleFor(tex);
            if (style != null)
            {
                GUI.Label(rect, "", style);
                Trace($"DrawTexture(Box) {rect.width}x{rect.height} tex={tex.width}x{tex.height}");
                return false;
            }
        }

        Graphics.DrawTexture(rect, tex, new Rect(0f, 0f, tex.width, tex.height), 0, 0, 0, 0);
        Trace($"DrawTexture(Graphics) {rect.width}x{rect.height}");
        return false;
    }

    private static GUIStyle StyleFor(Texture2D tex)
    {
        var key = tex.Pointer;
        if (_texStyles.TryGetValue(key, out var s) && s != null) return s;
        try
        {
            s = new GUIStyle { normal = { background = tex } };
            _texStyles[key] = s;
            return s;
        }
        catch (Exception e)
        {
            Plugin.Log?.LogWarning($"[IMGUI] 建纹理样式失败, 退回 Graphics: {e.Message}");
            return null;
        }
    }

    // ---- 窗口: GUILayout.BeginArea 代替 GUILayout.Window, 拖动自己算 ----

    private static readonly Dictionary<int, Vector2> _winPos = new Dictionary<int, Vector2>();
    private static int _dragId;
    private static Vector2 _dragOff;
    private const float TitleH = 22f;

    private static Rect DoWindow(object[] a)
    {
        // 事件统计放在这里 —— 只要 CM 在画窗口, 就一定会经过, 不依赖自检按钮是否挂上。
        NoteEvent();

        // ★ 决定性事实: CM 的主窗到底画没画?
        //   一次都不调用 => 面板根本没打开, "窗口内控件点不动"是个伪命题,
        //                   真正的问题在窗口【外】那个切换按钮上。
        if (_winCalls++ == 0)
            Plugin.Log?.LogInfo($"[IMGUI:cnt] GUILayout.Window 首次被调用 id={Convert.ToInt32(a[0])} " +
                                $"rect=({((Rect)a[1]).x:F0},{((Rect)a[1]).y:F0}," +
                                $"{((Rect)a[1]).width:F0}x{((Rect)a[1]).height:F0}) evt={TryEventType()}");
        else if (_winCalls % 300 == 0)
            Plugin.Log?.LogInfo($"[IMGUI:cnt] GUILayout.Window 已调用 {_winCalls} 次 (evt={TryEventType()})");
        TryAttachSelfTest();

        int id = Convert.ToInt32(a[0]);
        var rect = (Rect)a[1];
        object fn = a.Length > 2 ? a[2] : null;

        if (_winPos.TryGetValue(id, out var p))
            rect = new Rect(p.x, p.y, rect.width, rect.height);
        else
            _winPos[id] = new Vector2(rect.x, rect.y);

        HandleDrag(id, ref rect);

        // ---- 解析 title / style / options (从第 4 个参数起, 顺序随重载变) ----
        GUIContent title = null;
        GUIStyle style = null;
        GUILayoutOption[] opts = null;
        for (int i = 3; i < a.Length; i++)
        {
            switch (a[i])
            {
                case GUIContent c: title = c; break;
                case string str: if (title == null) title = new GUIContent(str); break;
                // GUIContent(Texture) 这个重载 interop 没生成, 纹理标题用不上(CM 走的是 string/GUIContent)
                case GUIStyle st: style = st; break;
                case GUILayoutOption[] o: opts = o; break;
            }
        }
        if (title == null)
        {
            try { title = GUIContent.none; } catch { title = null; }
        }
        if (style == null)
        {
            try { style = GUI.skin != null ? GUI.skin.window : null; } catch { }
        }

        // ---- 内容: 走忠实重建的 GUI.Window 语义 (见 WindowEmu.cs) ----
        //   上一版这里是 GUILayout.BeginArea 的近似替身 —— 它缺了 SelectIDList
        //   (窗口专属的控制 ID 列表), 于是窗口内外的控件挤在同一个 ID 列表里互相顶号,
        //   表现就是"点谁都不对 / 点在 A 上却触发了 B"。这正是命中判定错位的病根。
        //   WindowEmu 按 Unity 反编译出来的 CallWindowDelegate + LayoutedWindow 逐行复刻。
        // ⚠ 默认关闭: 上一版一开就崩(第一次走这条路就硬崩)。
        //   新路径里有原生调用, 猜错一步就是进程级崩溃, 所以改成显式 opt-in,
        //   默认走老的 BeginArea 路径(不崩但 CM 窗口内点不动)。
        if (CfgUseRealWindow?.Value == true && WindowEmu.Available)
        {
            if (PanelBg)
            {
                var bg = PanelStyle();
                if (bg != null) GUI.Label(rect, "", bg);
            }
            // 窗口外皮 (替代被 strip 的 Internal_DoWindow 画的边框)
            // ⚠ GUIStyle.Draw / WindowEmu 里都是原生调用, 传 null 进去就是访问违例 —— 必须挡死。
            if (style != null && title != null)
            {
                try { style.Draw(rect, title, false, false, false, false); }
                catch (Exception e) { WarnWindowSkin(e); }
            }
            WindowEmu.DoWindow(id, rect, fn, style, title, opts);
        }
        else
        {
            WarnWindowEmuOnce();
            GUILayout.BeginArea(rect);
            try
            {
                if (PanelBg)
                {
                    var bg = PanelStyle();
                    if (bg != null) GUI.Label(new Rect(0f, 0f, rect.width, rect.height), "", bg);
                }
                GUILayout.BeginArea(new Rect(5f, 5f,
                    Math.Max(1f, rect.width - 10f), Math.Max(1f, rect.height - 10f)));
                try { InvokeWindowFn(fn, id); }
                catch (Exception ie)
                {
                    var real = ie is TargetInvocationException tie && tie.InnerException != null
                        ? tie.InnerException : ie;
                    Plugin.Log?.LogError($"[IMGUI] 窗口 id={id} 的内容函数抛异常: {real}");
                }
                finally { GUILayout.EndArea(); }
            }
            finally { GUILayout.EndArea(); }
        }

        Trace($"Window id={id} rect={rect.x:F0},{rect.y:F0} {rect.width:F0}x{rect.height:F0} " +
              $"evt={(TryEventType())} fn={fn?.GetType().Name}");

        // ⚠ 这里【不能】再画我们自己的控件了。
        //   实测: 在 DoWindow 里插控件会把 CM 窗口那组的控件序列打乱, Layout 与 Repaint
        //   两帧对不上, 于是报 "Getting control 3's position in a group with only 3 controls",
        //   控件 ID 每帧都在变, GUIUtility.hotControl 配不上对 —— CM 的按钮就永远不触发。
        //   自检窗/键盘面板已挪到 OnGuiSelfTest (CM 的 OnGUI Postfix), 那是最外层的组。

        return rect;
    }

    private static int _emuWarned, _skinWarned;

    private static void WarnWindowEmuOnce()
    {
        if (_emuWarned++ > 0) return;
        // 之前这行把两种情况混成一句"原语不全", 结果 Resolve() 返回 null(原语其实齐备)
        // 时打印成 "原语不全()" —— 完全误导。现在分开说。
        var why = WindowEmu.Resolve();
        if (why == null)
            Plugin.Log?.LogInfo("[IMGUI] UseRealWindow=false, 按配置走旧的 BeginArea 路径 " +
                                "(原语其实是齐备的, 想试新路径就把 UseRealWindow 改成 true)");
        else
            Plugin.Log?.LogWarning($"[IMGUI] WindowEmu 原语不全: {why} —— 退回 BeginArea 近似路径");
    }

    private static void WarnWindowSkin(Exception e)
    {
        if (_skinWarned++ > 0) return;
        Plugin.Log?.LogWarning($"[IMGUI] GUIStyle.Draw 画窗口外皮失败(退回无边框): {e.Message}");
    }

    // ------------------------------------------------------------------ 墓碑: 自检窗
    //
    // 这里曾经有个 DrawSelfTest() —— 一个我们完全掌控的小窗, 用来判定
    // "IMGUI 的按钮到底能不能点"。它的结论是能点(鼠标事件、命中判定、坐标换算全都正常),
    // 任务完成。
    //
    // 但它后来变成了 bug 来源, 所以删掉。教训值得留着:
    //   它的结构是
    //       try { GUILayout.BeginArea(r); ... GUILayout.EndArea(); } catch { 记日志 }
    //   —— BeginArea 之后【没有配对的 finally】。一旦中间的 Label/Button 抛
    //   "Getting control N's position in a group with only M controls",
    //   EndArea 就永远不执行, GUILayout 的组栈留在不平衡状态; 而这个异常每帧都抛,
    //   于是整个 IMGUI 状态被持续污染 —— 连 CM 自己的控件都跟着一起废。
    //
    //   → IMGUI 里任何 BeginArea/BeginScrollView/BeginHorizontal 都必须 try/finally 配对。
    //     诊断代码尤其要这样: 它一旦出错, 会污染你本来想诊断的那个对象。

    private static int _noDelegateLogged;

    /// <summary>
    /// 调用 GUI.WindowFunction。
    ///
    /// 坑: Il2CppInterop 生成的委托类型 (UnityEngine.GUI+WindowFunction) 并【不是】
    /// System.Delegate —— 它是 Il2CppObjectBase 的派生类, 带一个
    /// `public virtual new void Invoke(Int32)`。所以 `fn is Delegate` 恒为 false,
    /// DynamicInvoke 这条路走不通, 必须反射调它的 Invoke。
    /// </summary>
    private static void InvokeWindowFn(object fn, int id)
    {
        if (fn == null) return;

        if (fn is Delegate d) { d.DynamicInvoke(new object[] { id }); return; }

        var t = fn.GetType();
        var mi = t.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance,
                             null, new[] { typeof(int) }, null)
                 ?? t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                      .FirstOrDefault(m => m.Name == "Invoke" && m.GetParameters().Length == 1);

        if (mi == null)
        {
            if (_noDelegateLogged++ < 3)
                Plugin.Log?.LogError($"[IMGUI] {t.FullName} 上找不到 Invoke(Int32), 内容无法绘制");
            return;
        }

        mi.Invoke(fn, new object[] { id });
    }

    private static string TryEventType()
    {
        try { return Event.current?.type.ToString() ?? "?"; }
        catch { return "err"; }
    }

    private static void HandleDrag(int id, ref Rect rect)
    {
        Event e = null;
        try { e = Event.current; } catch { }
        if (e == null) return;

        var title = new Rect(rect.x, rect.y, rect.width, TitleH);
        switch (e.type)
        {
            case EventType.MouseDown:
                if (e.button == 0 && title.Contains(e.mousePosition))
                {
                    _dragId = id;
                    _dragOff = e.mousePosition - new Vector2(rect.x, rect.y);
                }
                break;

            case EventType.MouseDrag:
                if (_dragId == id)
                {
                    var np = e.mousePosition - _dragOff;
                    _winPos[id] = np;
                    rect = new Rect(np.x, np.y, rect.width, rect.height);
                }
                break;

            case EventType.MouseUp:
                if (_dragId == id) _dragId = 0;
                break;
        }
    }

    // ---- 文本框: GUILayout.TextField/TextArea 没了, 但 GUI.TextField 还活着 ----

    private static string DoTextField(object[] a, bool area)
    {
        var text = (a.Length > 0 ? a[0] as string : null) ?? "";
        int maxLength = 0;
        GUIStyle style = null;
        GUILayoutOption[] opts = null;

        for (int i = 1; i < a.Length; i++)
        {
            if (a[i] is int mi) maxLength = mi;
            else if (a[i] is GUIStyle s) style = s;
            else if (a[i] is GUILayoutOption[] o) opts = o;
        }

        GUISkin skin = null;
        try { skin = GUI.skin; } catch { }

        var baseStyle = style ?? (area ? skin?.textArea : skin?.textField);
        float h = area ? 44f : 18f;

        Rect r;
        try { r = GUILayoutUtility.GetRect(1f, h, baseStyle, opts); }
        catch { r = new Rect(0f, 0f, 100f, h); }

        // ⚠⚠ 这里以前调的是 `GUI.TextField(r, text, maxLength, style)` —— **四参重载**。
        //   本作 metadata 里 `GUI.TextField` 只有【三参】那一个
        //   (dump.cs 全类只有 `TextField(Rect, string, GUIStyle)` 一条);
        //   四参版本只是 interop 程序集补出来的空桩, 一调就抛
        //   `System.NotSupportedException: Method unstripping failed`,
        //   异常被本方法的 catch 吞掉后返回 null ——
        //   表现就是【字符型配置的输入框一个像素都不画】, 而按钮/标签全都正常。
        //
        //   maxLength 因此只能丢掉了: 本作根本没实现带长度限制的那个重载。
        //   真要限长, 得在托管侧自己截(GUI.TextField 没有别的上限入口)。
        if (maxLength > 0 && text.Length > maxLength) text = text.Substring(0, maxLength);

        // GUI.TextArea 同样被 strip 了, 多行用 textArea 样式 + 同一个 TextField 表达
        return GUI.TextField(r, text, baseStyle);
    }

    // ---- 选择网格: 用一排按钮凑出来 ----

    private static readonly Dictionary<int, int> _gridSel = new Dictionary<int, int>();

    private static int DoSelectionGrid(object[] a, bool layout)
    {
        int sel = layout ? Convert.ToInt32(a[0]) : Convert.ToInt32(a[1]);
        string[] texts = null;
        int xCount = 1;
        GUIStyle style = null;
        GUILayoutOption[] opts = null;

        foreach (var o in a)
        {
            if (o is string[] ss) texts = ss;
            else if (o is GUIStyle gs) style = gs;
            else if (o is GUILayoutOption[] go) opts = go;
            else if (o is int xi && o != a[0]) xCount = xi;
        }
        if (texts == null || texts.Length == 0) return sel;
        if (xCount <= 0) xCount = 1;

        Rect cell;
        if (layout)
        {
            cell = GUILayoutUtility.GetRect(1f, 24f, style, (GUILayoutOption[])null);
        }
        else
        {
            var anchor = (Rect)a[0];
            cell = new Rect(anchor.x, anchor.y, 100f, 24f);
        }

        if (layout) GUILayout.BeginHorizontal();
        try
        {
            for (int i = 0; i < texts.Length; i++)
            {
                bool on = (i == sel);
                if (layout)
                {
                    if (on) GUI.color = new Color(0.4f, 0.8f, 1f);
                    if (GUILayout.Button(texts[i])) sel = i;
                    if (on) GUI.color = Color.white;
                }
                else
                {
                    var r = new Rect(cell.x + (i % xCount) * cell.width,
                                     cell.y + (i / xCount) * cell.height,
                                     cell.width, cell.height);
                    if (GUI.Button(r, texts[i])) sel = i;
                }
            }
        }
        finally { if (layout) GUILayout.EndHorizontal(); }

        return sel;
    }

    private static GUILayoutOption MakeOption(string kind, float v)
    {
        try
        {
            var t = typeof(GUILayoutOption);
            var nt = t.GetNestedType("Type", BindingFlags.Public | BindingFlags.NonPublic);
            if (nt != null && nt.IsEnum)
            {
                var val = Enum.Parse(nt, kind);
                var ctor = t.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { nt, typeof(float) }, null);
                if (ctor != null) return (GUILayoutOption)ctor.Invoke(new object[] { val, v });
            }
        }
        catch { }
        return GUILayout.Width(v);
    }
}
