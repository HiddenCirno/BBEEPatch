using System;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 配置面板的【自己人】OnGUI 宿主。
///
/// 为什么必须有它
/// ──────────────
/// 上一版面板的 OnGUI 入口是挂在 ConfigurationManager 的 OnGUI **Postfix** 上的
/// (ImguiCompat.OnGuiSelfTest)。后果是:
///   · 必须先按 F1 把原版 CM 窗口叫出来, 我们的面板才会被绘制 ——
///     而那个 CM 窗口在本作里控件是点不动的, 属于"为了开一个不可用的面板
///     而去开另一个不可用的面板"。
///   · CM 不在场(没装/没启用/改了热键)时, 面板直接人间蒸发。
/// 这是一个纯粹的**架构依赖错误**: 我们的 UI 不该寄生在别人的 UI 上。
///
/// 正确做法: 让 Unity 直接调用我们自己的 OnGUI。
///   BasePlugin.AddComponent&lt;T&gt;() 会把托管类型注入 il2cpp 类型系统
///   并挂到 BepInEx 的管理器 GameObject(常驻、DontDestroyOnLoad)上,
///   之后 Unity 就会像对待任何 MonoBehaviour 一样每帧给这里派发 IMGUI 事件。
///
/// ⚠ IL2CPP 注入的 MonoBehaviour 两条硬性要求:
///   1. 必须有一个 `(IntPtr)` 构造函数 —— 它只是给 interop 包指针用的, 不要在里面干活
///   2. 不能是泛型类
///   两条都不满足时 AddComponent 会抛, 我们捕获后自动退回 CM 兜底路径, 不会连面板一起丢。
/// </summary>
/// <remarks>
/// 必须是 <c>public</c> 且构造函数形如 <c>(IntPtr)</c> —— ClassInjector 是按这两条来找注入点的,
/// 少一条 AddComponent 就抛。
/// </remarks>
public class UiHost : MonoBehaviour
{
    public UiHost(IntPtr ptr) : base(ptr) { }

    /// <summary>Component 挂上了(不代表 Unity 真的会调 OnGUI)。</summary>
    internal static bool Active { get; private set; }

    /// <summary>
    /// **Unity 真的调用过 OnGUI 了**。
    ///
    /// 这两个状态必须分开: AddComponent 成功只说明类型注入 + 挂载没抛异常,
    /// 完全不保证 Unity 会给我们派发 IMGUI 事件(方法表没接上、宿主对象被销毁……)。
    /// 只信 <see cref="Active"/> 的话, 一旦 OnGUI 不来, 面板会【静默死掉】而且兜底路径
    /// 还被自己关着 —— 比改造前更糟。所以兜底看的是这个标志。
    /// </summary>
    internal static bool Alive { get; private set; }

    /// <summary>
    /// 第一次收到 OnGUI 的帧号。
    ///
    /// ⚠ 兜底路径要靠它做"交接": Unity 对同一个事件会依次调用所有 OnGUI 脚本,
    ///   而 Layout 和 Repaint 是**两个事件**。假设第一帧里 CM 排在前面:
    ///     事件 Layout  → CM 先画(此时还没 Alive), 然后我们收到 OnGUI 把 Alive 置 true
    ///     事件 Repaint → CM 再进来时看到 Alive 已 true 就撒手 —— 而我们的 Claim 又因为
    ///                    "同一帧同一事件" 把它挡掉 … 结果是 Repaint 谁都没画。
    ///   GUILayout 的 Layout 与 Repaint 数量对不上就是组栈失衡, 属于本项目明确警告过的坑。
    ///   所以交接必须发生在【帧边界】: 第一次收到 OnGUI 的那一帧仍由 CM 画完整, 从下一帧起才换人。
    /// </summary>
    internal static int FirstAliveFrame { get; private set; } = -1;

    private static bool _firstGuiLogged;

    /// <summary>
    /// 在 Plugin.Load() 里调用。
    /// 失败不影响插件其它功能 —— 只把面板入口降级回 CM。
    /// </summary>
    /// <summary>
    /// 总开关。关掉 = **根本不 AddComponent**(不是"挂上不干活")。
    ///
    /// 加这个开关是为了排查: 注入一个托管 MonoBehaviour 进 il2cpp 域、再由 Unity 每帧回调 OnGUI,
    /// 是本插件里**侵入性最强**的一件事。如果游戏出现无法解释的启动崩溃,
    /// 第一件该做的事就是把它单独关掉, 看现象还在不在 ——
    /// 而不是去猜崩溃点附近的业务代码(那种猜法已经错过一次了)。
    /// 关掉后面板退回 ConfigurationManager 兜底路径。
    /// </summary>
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;

    internal static bool Install(BepInEx.Unity.IL2CPP.BasePlugin plugin)
    {
        if (CfgEnabled?.Value == false)
        {
            Active = false;
            Plugin.Log?.LogWarning("  [UI] 自建 OnGUI 宿主已按配置关闭(SelfOnGuiHost=false), 面板退回 CM 兜底");
            return false;
        }
        try
        {
            plugin.AddComponent<UiHost>();
            Active = true;
            Plugin.Log?.LogInfo("  [UI] 自绘面板宿主已挂载 (不再依赖 ConfigurationManager)");
        }
        catch (Exception e)
        {
            Active = false;
            Plugin.Log?.LogWarning($"  [UI] 自建 OnGUI 宿主失败, 退回 CM 兜底路径: {LogEx.Unwrap(e)}");
        }
        return Active;
    }

    private void OnGUI()
    {
        if (!_firstGuiLogged)
        {
            _firstGuiLogged = true;
            Alive = true;
            try { FirstAliveFrame = Time.frameCount; } catch { FirstAliveFrame = 0; }
            Plugin.Log?.LogInfo("[UI] UiHost.OnGUI 已被 Unity 调用 —— 面板不再需要 F1 唤醒 " +
                                $"(从下一帧起完全接管; 本帧仍由兜底路径画完整, 见 FirstAliveFrame 的说明)");
        }
        try
        {
            ConfigPanel.OnGui();
            BulletLab.OnGui();   // F9 弹幕实验台（独立开关/独立窗口，与 F8 互不影响）
            // 时停三件套 —— **必须走 IMGUI 事件**: 本作里 Input.GetKeyDown 收不到,
            // 而且 timeScale=0 时只有 IMGUI(渲染循环驱动)还能收到按键, 见 SnapshotProbe 注释。
            SnapshotProbe.OnGui();
            LiveInspector.OnGui();   // F10 现场检查器（冻结世界里开关/挪物体, 认特效）
            RefCounterProbe.OnGui();  // F11 叠色探针（抓引擎自己的 ReferenceCounterMap）
        }
        catch (Exception e)
        {
            // 绝不能把异常漏给 Unity —— IMGUI 里未配对的 Begin/End 会污染整个 GUI 状态,
            // 后果是连游戏自己的 UI 一起画不出来。
            LogEx.Err("UiHost.OnGUI", e);
        }
    }
}
