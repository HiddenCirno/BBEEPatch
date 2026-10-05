using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 自绘配置面板。
///
/// 为什么不修 ConfigurationManager
/// ────────────────────────────────
/// CM 的窗口要重建 `GUI.Window`, 而它在本作里依赖两处**原生状态**:
///   · `Internal_GetWindowRect(windowID)` —— 窗口得先向原生窗口系统注册
///     (注册靠 BeginWindows / Internal_BeginWindows, 本作全被 strip)
///   · `resetCoords` —— 由原生侧清标志, 纯托管路径没人清
/// **不是不能修, 是性价比不划算。** 自己画即可, 绕开 CM 那一整套。
///
/// ⚠ 入口必须是自己人
/// ──────────────────
/// 上一版把 OnGUI 挂在 CM 的 OnGUI Postfix 上 —— 必须先按 F1 唤出原版 CM 窗口
/// 我们的面板才会被画。现在由 <see cref="UiHost"/> 提供宿主;
/// CM 那条只作为兜底(<see cref="OnGuiFromCmFallback"/>), 且两者永不重复绘制。
///
/// ⚠ IL2CPP / IMGUI 三条铁律
/// ─────────────────────────
///   1. 所有 Begin/End 必须 try/finally 配对。漏一个 finally, 组栈就永久失衡,
///      异常每帧都抛 —— 会把你本来想诊断的东西一起弄废(自检窗就是这么死的)。
///   2. 任何异常都不能漏给 Unity。
///   3. 运行时 new 的 Texture2D 必须设 hideFlags(见 UiTheme)。
/// </summary>
internal static class ConfigPanel
{
    internal static ConfigEntry<bool> CfgEnabled;
    internal static ConfigEntry<string> CfgToggleKey;

    private static bool _open;
    internal static bool IsOpen => _open;

    private static Rect _rect = new Rect(-1f, -1f, 720f, 800f);
    private static Vector2 _scroll;
    private static string _filter = "";
    private static string _hint = "";
    private static float _hintUntil;
    private static int _rowCounter;
    private static int _errLogged;

    private static readonly List<ConfigEntryBase> _items = new List<ConfigEntryBase>();
    private static readonly List<ConfigEntryBase> _shown = new List<ConfigEntryBase>();

    /// <summary>
    /// 段的显示顺序。
    /// ⚠ 这里【只影响显示顺序】, 不改 cfg 里的段名 ——
    ///   BepInEx 不覆盖已写入 .cfg 的项(栽过 3 次), 改段名 = 用户调好的值全部作废
    ///   (尤其是连段 Sequence 和那些时序秒数, 都是踩着坑调出来的)。
    ///   没列到的段排在最后。
    /// </summary>
    private static readonly string[] SectionOrder =
    {
        "总开关",
        "战斗",
        "冲刺无敌",
        "特效换色",
        "ES机体",
        "动作变速",
        "连段模组",
        "纹章解放",
        "贝德维尔",
        "动作结构",
        "动作记录",
        "IMGUI",
        "诊断",
    };

    private static bool On => CfgEnabled?.Value != false;

    // ------------------------------------------------------------------ 入口

    private static KeyCode ToggleKey
    {
        get
        {
            var s = CfgToggleKey?.Value;
            try
            {
                if (!string.IsNullOrWhiteSpace(s) && Enum.TryParse(s.Trim(), true, out KeyCode k)) return k;
            }
            catch { }
            return KeyCode.F8;
        }
    }

    /// <summary>由 UiHost 调用 —— 主路径。</summary>
    internal static void OnGui()
    {
        if (!On) return;
        var e = Event.current;
        if (e == null) return;
        if (!Claim(e)) return;          // 同一事件只画一次, 见 Claim

        if (e.type == EventType.KeyDown && e.keyCode == ToggleKey)
        {
            _open = !_open;
            if (_open) Rebuild();
            e.Use();
            return;
        }

        if (!_open) return;
        try { Draw(); }
        catch (Exception ex)
        {
            // 绝不能把异常漏出去 —— IMGUI 里未配对的 Begin/End 会污染整个 GUI 状态。
            if (_errLogged++ < 3) Plugin.Log?.LogError($"[UI] 面板绘制异常: {LogEx.Unwrap(ex)}");
        }
    }

    /// <summary>
    /// 由 ConfigurationManager 的 OnGUI Postfix 调用 —— 只在自建宿主【确实没在工作】时接管。
    ///
    /// ⚠ 判据是 <see cref="UiHost.Alive"/>(Unity 真的调过 OnGUI)而不是 <c>Active</c>(挂载没抛异常)。
    ///   两者差别很要命: 挂上了但 Unity 不派发事件时, 只看 Active 会把兜底一起关掉, 面板静默死掉。
    /// </summary>
    internal static void OnGuiFromCmFallback()
    {
        // ⚠ 交接必须落在帧边界上, 不能一看到 Alive 就撒手 ——
        //   Unity 的 Layout 与 Repaint 是两个独立事件, 而 CM 可能排在 UiHost 前面。
        //   若在收到 OnGUI 的【那一帧】就撒手, 会出现"Layout 由 CM 画、Repaint 谁都没画"
        //   (Claim 又会挡掉 UiHost 的同帧重复), 组栈当场失衡。
        if (UiHost.Alive)
        {
            int f;
            try { f = Time.frameCount; } catch { return; }
            if (f != UiHost.FirstAliveFrame) return;
        }
        OnGui();
    }

    // 双入口防重: 两个入口在同一帧都活着的瞬间会把整个面板画两遍,
    // 而 IMGUI 的 Layout 与 Repaint 两遍数量对不上就报
    // "Getting control N's position in a group with only M controls", 组栈当场失衡。
    private static int _claimFrame = -1;
    private static EventType _claimType;

    private static bool Claim(Event e)
    {
        try
        {
            int f = Time.frameCount;
            if (f == _claimFrame && e.type == _claimType) return false;
            _claimFrame = f;
            _claimType = e.type;
            return true;
        }
        catch { return true; }
    }

    internal static bool Toggle()
    {
        _open = !_open;
        if (_open) Rebuild();
        return _open;
    }

    private static void Rebuild()
    {
        _items.Clear();
        var cfg = Plugin.CfgFile;
        if (cfg == null) return;
        foreach (var kv in cfg)
        {
            if (kv.Key.Section == "IMGUI") continue;   // 兼容层的调试项, 平时用不到
            _items.Add(kv.Value);
        }
        _items.Sort(CompareEntries);
        _scroll = Vector2.zero;
        Refresh();
    }

    private static int Rank(string section)
    {
        int i = Array.IndexOf(SectionOrder, section);
        return i < 0 ? SectionOrder.Length : i;
    }

    private static int CompareEntries(ConfigEntryBase a, ConfigEntryBase b)
    {
        int r = Rank(a.Definition.Section).CompareTo(Rank(b.Definition.Section));
        return r != 0 ? r : string.CompareOrdinal(a.Definition.Key, b.Definition.Key);
    }

    private static void Refresh()
    {
        _shown.Clear();
        var f = _filter?.Trim() ?? "";
        foreach (var it in _items)
        {
            if (f.Length == 0 ||
                it.Definition.Key.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 ||
                it.Definition.Section.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                _shown.Add(it);
        }
    }

    // ------------------------------------------------------------------ 绘制

    private static void Draw()
    {
        UiTheme.Ensure();
        // 样式没建起来就一个字都别画 —— 否则会拿着 null 样式去 BeginArea,
        // 每帧一片异常, 还会因为 Begin/End 配不上对而污染整个 IMGUI 组栈。
        if (!UiTheme.Ok) return;
        if (_rect.x < 0f) _rect.x = Mathf.Max(20f, Screen.width - _rect.width - 40f);
        if (_rect.y < 0f) _rect.y = 40f;

        DragWindow(Event.current);

        GUILayout.BeginArea(_rect, UiTheme.Win);
        try
        {
            Header();
            GUILayout.Space(4);
            FilterBar();
            GUILayout.Space(4);
            Body();
            GUILayout.Space(2);
            // ⚠⚠ 绝不能用「时间」来决定控件画不画。
            //   Layout 和 Repaint 是**同一帧里的两个事件**，而 Time.realtimeSinceStartup
            //   在两者之间会前进 —— 只要 _hintUntil 正好落在这中间，就会出现
            //     Layout  画了 7 个控件
            //     Repaint 只画 6 个
            //   → 组栈失衡 → "Getting control 6's position in a group with only 6
            //     controls when doing repaint" → Draw() 从中间抛掉 →
            //     **排在后面的控件那一帧全都不画**（表现为"某个配置项显示不正常"）。
            //   正确做法：控件**永远存在**，只改它的内容。
            GUILayout.Label((!string.IsNullOrEmpty(_hint) && Time.realtimeSinceStartup < _hintUntil)
                            ? _hint : "", UiTheme.Section);
        }
        finally { GUILayout.EndArea(); }
    }

    private static void Header()
    {
        GUILayout.BeginHorizontal();
        try
        {
            GUILayout.Label($"BBEE 配置面板    {_shown.Count}/{_items.Count}", UiTheme.Title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("全部重置", UiTheme.Red, GUILayout.Width(70), GUILayout.Height(20))) ResetAll();
            if (GUILayout.Button("保存", UiTheme.Blue, GUILayout.Width(56), GUILayout.Height(20))) Save();
            if (GUILayout.Button("关闭", UiTheme.Red, GUILayout.Width(56), GUILayout.Height(20))) _open = false;
        }
        finally { GUILayout.EndHorizontal(); }
    }

    private static void FilterBar()
    {
        GUILayout.BeginHorizontal(UiTheme.Box);
        try
        {
            GUILayout.Label("过滤", UiTheme.Label, GUILayout.Width(36));
            var rect = GUILayoutUtility.GetRect(1f, 20f, UiTheme.Field, new[] { GUILayout.ExpandWidth(true) });
            var nf = GUI.TextField(rect, _filter ?? "", UiTheme.Field);
            if (!string.Equals(nf, _filter, StringComparison.Ordinal)) { _filter = nf; Refresh(); }
        }
        finally { GUILayout.EndHorizontal(); }
    }

    private static void Body()
    {
        // GUILayout 的滚动条只认 GUI.skin 上的样式, 临时替换再还原
        var origSb = GUI.skin.verticalScrollbar;
        var origTh = GUI.skin.verticalScrollbarThumb;
        try
        {
            GUI.skin.verticalScrollbar = UiTheme.Box;
            GUI.skin.verticalScrollbarThumb = UiTheme.Btn;

            _scroll = GUILayout.BeginScrollView(_scroll);
            try
            {
                _rowCounter = 0;
                string last = null;
                foreach (var it in _shown)
                {
                    var sec = it.Definition.Section;
                    if (sec != last)
                    {
                        last = sec;
                        GUILayout.Space(2);
                        GUILayout.Label($"── {sec} ──", UiTheme.Section);
                    }
                    DrawRow(it);
                }
                if (_shown.Count == 0) GUILayout.Label("(没有匹配的配置项)", UiTheme.Label);
            }
            finally { GUILayout.EndScrollView(); }
        }
        finally
        {
            GUI.skin.verticalScrollbar = origSb;
            GUI.skin.verticalScrollbarThumb = origTh;
        }
    }

    private static void DrawRow(ConfigEntryBase it)
    {
        var style = (_rowCounter++ % 2 == 0) ? UiTheme.Box : UiTheme.BoxAlt;
        GUILayout.BeginHorizontal(style);
        try
        {
            DrawKeyLabel(it);

            // 字形别用 ⟲ (U+27F2) —— 游戏字体里没有, 画出来是个空框, 看起来像"没这个按钮"。
            if (GUILayout.Button("重置", UiTheme.Btn, GUILayout.Width(40), GUILayout.Height(20)))
                ResetOne(it);

            switch (it.BoxedValue)
            {
                case bool b:
                    if (GUILayout.Button(b ? "开启" : "关闭", b ? UiTheme.ToggleOn : UiTheme.ToggleOff,
                                         GUILayout.Width(64), GUILayout.Height(20)))
                        SetFromControl(it, !b);
                    break;

                case int _:
                case float _:
                case double _:
                    UiWidgets.Number(it, it.BoxedValue);
                    break;

                // ★ 枚举型选项(绑定时给了 AcceptableValueList) → 一排选择按钮。
                //   必须排在 hex 颜色和普通字符串【之前】。
                //   (曾因一场启动崩溃被误删过一次, 真凶是 JS 加载器钩子, 见 Patcher.cs。)
                case string s when UiWidgets.ChoiceRow(it, s):
                    break;

                case string s when UiWidgets.IsHexColor(s):
                    UiWidgets.ColorRow(it, s);
                    break;

                case string s:
                    UiWidgets.TextField(it, 0f, t => t);
                    break;

                default:
                    UiWidgets.TextField(it, 0f, t => t);
                    break;
            }

            GUILayout.FlexibleSpace();
        }
        finally { GUILayout.EndHorizontal(); }
    }

    /// <summary>
    /// 配置项名字 + 悬停时在底部显示它的一句说明。
    ///
    /// ⚠ 这里【绝不能】图省事写成 `GUILayout.Label(new GUIContent(text, tip), style, opts)`:
    ///   本作 metadata 里 `GUILayout.Label` **只有 string 参数的两个重载**
    ///   (`Label(string, GUILayoutOption[])` / `Label(string, GUIStyle, GUILayoutOption[])`),
    ///   带 GUIContent 的那个只存在于 interop 程序集 —— 是空桩, 一调就抛
    ///   `Method unstripping failed`。而 Draw() 的异常会被 OnGui 捕获,
    ///   症状是【整个面板一个控件都不画】, 排查起来完全看不出跟这行有关。
    ///   这正是本项目刚修过的第 21 条坑, 换个地方又踩了一遍 —— 所以记在这里。
    /// </summary>
    private static void DrawKeyLabel(ConfigEntryBase it)
    {
        var rect = GUILayoutUtility.GetRect(196f, 20f, UiTheme.Label, Array.Empty<GUILayoutOption>());
        GUI.Label(rect, it.Definition.Key, UiTheme.Label);

        try
        {
            if (rect.Contains(Event.current.mousePosition)) Hint(ShortDesc(it));
        }
        catch { }
    }

    private static readonly Dictionary<string, string> _descCache = new Dictionary<string, string>();

    private static string ShortDesc(ConfigEntryBase it)
    {
        var key = it.Definition.Section + "/" + it.Definition.Key;
        if (_descCache.TryGetValue(key, out var d)) return d;
        var raw = it.Description?.Description ?? "";
        // 第一行就够了 —— 完整说明太长, tooltip 里放不下也没人读
        int nl = raw.IndexOf('\n');
        if (nl >= 0) raw = raw.Substring(0, nl);
        if (raw.Length > 160) raw = raw.Substring(0, 160) + "…";
        _descCache[key] = raw;
        return raw;
    }

    private static void DragWindow(Event e)
    {
        if (e == null) return;
        var bar = new Rect(_rect.x, _rect.y, _rect.width, 26f);
        switch (e.type)
        {
            case EventType.MouseDown:
                if (e.button == 0 && bar.Contains(e.mousePosition))
                { _dragId = 1; _dragOff = e.mousePosition - new Vector2(_rect.x, _rect.y); }
                break;
            case EventType.MouseDrag:
                if (_dragId == 1)
                {
                    var p = e.mousePosition - _dragOff;
                    _rect = new Rect(p.x, p.y, _rect.width, _rect.height);
                    e.Use();
                }
                break;
            case EventType.MouseUp:
                _dragId = 0;
                break;
        }
    }

    private static int _dragId;
    private static Vector2 _dragOff;

    // ------------------------------------------------------------------ 操作

    /// <summary>
    /// 写配置值。**不动编辑缓冲** —— 因为输入框正是靠缓冲留住"打到一半的字",
    /// 这里清掉的话打字会当场被配置值顶回去, 表现就是"打字没反应"。
    /// 按钮驱动的改动请用 <see cref="SetFromControl"/>。
    /// </summary>
    internal static void Set(ConfigEntryBase it, object v)
    {
        try
        {
            it.BoxedValue = v;
            Hint($"{it.Definition.Key} = {v}");
        }
        catch (Exception e) { Hint($"改值失败: {LogEx.Unwrap(e)}"); }
    }

    /// <summary>
    /// 按钮(± / ◀▶ / 开关)驱动的改动。
    ///
    /// ⚠ 必须顺手丢掉编辑缓冲, 否则那几行文本会**永远停在按下按钮之前的值**:
    ///   缓冲一旦存在, 输入框就只显示缓冲, 不再回读配置。
    ///   (上一版就是这个毛病: 按 ◀ 换色、按 + 改数值, 旁边的框还是老数字。)
    /// </summary>
    internal static void SetFromControl(ConfigEntryBase it, object v)
    {
        UiWidgets.DropBuffer(it);
        Set(it, v);
    }

    private static void ResetOne(ConfigEntryBase it)
    {
        try
        {
            it.BoxedValue = it.DefaultValue;
            UiWidgets.DropBuffer(it);   // 缓冲也要丢, 否则显示的还是旧文本
            Hint($"{it.Definition.Key} 已恢复默认 = {it.BoxedValue}");
        }
        catch (Exception e) { Hint($"重置失败: {LogEx.Unwrap(e)}"); }
    }

    private static void ResetAll()
    {
        int n = 0;
        foreach (var it in _items)
        {
            try { it.BoxedValue = it.DefaultValue; n++; } catch { }
        }
        UiWidgets.DropAllBuffers();
        Hint($"已把 {n} 项恢复默认 (记得点保存)");
    }

    private static void Save()
    {
        try { Plugin.CfgFile?.Save(); Hint("已保存到 " + Plugin.CfgFileName); }
        catch (Exception e) { Hint($"保存失败: {LogEx.Unwrap(e)}"); }
    }

    private static void Hint(string s) { _hint = s; _hintUntil = Time.realtimeSinceStartup + 3f; }
}
