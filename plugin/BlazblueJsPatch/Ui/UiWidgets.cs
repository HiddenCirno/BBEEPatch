using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 面板控件。
///
/// ★ 关于文本框: 为什么绕开 GUILayout.TextField
/// ──────────────────────────────────────────
/// 游戏被 UnityLinker 做了方法级 strip, `GUILayout.TextField` 在 metadata 里【不存在】,
/// 由 ImguiCompat 顶掉。而 ImguiCompat 上一版内部调的是
///     GUI.TextField(rect, text, maxLength, style)     ← 四参重载
/// 但本作 metadata 里 `GUI.TextField` **只有三参那一个**
/// (dump.cs 里全类只有 `TextField(Rect, string, GUIStyle)` 一条)。
/// 四参版本只存在于 interop 程序集, 是一根"一调就抛 Method unstripping failed"的空桩,
/// 异常被 ImguiCompat 的 catch 吞掉后返回 null —— 于是【字符串配置项的输入框一个像素都不画】。
/// 而 bool / int / float 走的是 Button + Label, 全是活着的原生方法, 所以只有"字符参数"坏。
///
/// 修法: 输入框自己调【确定存活】的三参 `GUI.TextField`,
/// 不再把最关键的一个控件压在别人的模拟层上。
/// </summary>
internal static class UiWidgets
{
    private static readonly Dictionary<string, string> _buf = new Dictionary<string, string>();

    /// <summary>丢掉这一项的编辑缓冲(字符串项和数值项各有一份, 都要丢) ——
    /// 漏掉数值那份的话, 重置之后显示的还是重置前打了一半的字。</summary>
    internal static void DropBuffer(ConfigEntryBase it)
    {
        _buf.Remove(KeyOf(it));
        _buf.Remove("num:" + KeyOf(it));
    }

    internal static void DropAllBuffers() => _buf.Clear();

    private static string KeyOf(ConfigEntryBase it) => it.Definition.Section + "/" + it.Definition.Key;

    internal static bool Shift
    {
        get { try { var e = Event.current; return e != null && e.shift; } catch { return false; } }
    }

    // ------------------------------------------------------------------ 文本框

    /// <summary>
    /// 带本地编辑缓冲的文本输入框。
    ///
    /// 缓冲的意义: 编辑期间显示的是【正在打的字】, 不是配置里的值。
    /// 否则半截输入(颜色打了一半 / 列表逗号还没敲完)一旦解析失败,
    /// 下一帧就被原值覆盖 —— 打字看起来完全没反应。
    /// 只有解析成功才写回配置。
    /// </summary>
    internal static void TextField(ConfigEntryBase it, float width, Func<string, object> parse)
    {
        var key = KeyOf(it);
        if (!_buf.TryGetValue(key, out var text)) { text = it.BoxedValue?.ToString() ?? ""; _buf[key] = text; }

        // ⚠⚠ `GetRect(a, b, style, opts)` 的 a / b 是【精确宽高】，不是最小/最大。
        //   原来传的是 (1f, 20f) —— 于是框**真的只有 1 像素宽**：
        //   框在、高度也对、就是点不到。ExpandWidth 在这个重载下也不生效。
        //
        //   对照证据（同一个重载，一处正常一处坏）：
        //     ConfigPanel.DrawKeyLabel : GetRect(196f, 20f, ...)  → 标签显示正常 ✓
        //     UiWidgets.TextField      : GetRect(  1f, 20f, ...)  → 输入框 1 像素 ✗
        //
        //   所以必须给一个**真实数字**。调用方传 0 = 用面板里的默认框宽
        //   （面板 720 宽 - 标签 196 - 重置按钮 40 - 内边距/滚动条 ≈ 420）。
        float w = width > 0f ? width : 420f;
        var opts = new[] { GUILayout.Width(w), GUILayout.Height(20f) };

        // 先要矩形(这一步同时占好布局控制 ID), 再用原生三参 GUI.TextField 画。
        // 顺序不能反 —— 反过来 Layout 与 Repaint 两帧的控件 ID 对不上, 输入会丢失焦点。
        var rect = GUILayoutUtility.GetRect(w, 20f, UiTheme.Field, opts);
        var edited = GUI.TextField(rect, text ?? "", UiTheme.Field);

        if (string.Equals(edited, text, StringComparison.Ordinal)) return;
        _buf[key] = edited;
        var parsed = parse(edited);
        if (parsed != null) ConfigPanel.Set(it, parsed);
    }

    // ------------------------------------------------------------------ 数字

    internal static void Number(ConfigEntryBase it, object v)
    {
        switch (v)
        {
            case int i: Step(it, i, Shift ? 10 : 1); break;
            case float f: StepF(it, f, Shift ? 1f : 0.1f); break;
            case double d: StepD(it, d, Shift ? 1.0 : 0.1); break;
        }
    }

    private static void Step(ConfigEntryBase it, int cur, int delta)
    {
        if (GUILayout.Button("−", UiTheme.Btn, GUILayout.Width(28), GUILayout.Height(20)))
            ConfigPanel.SetFromControl(it, cur - delta);
        GUILayout.Label(cur.ToString(CultureInfo.InvariantCulture), UiTheme.Value, GUILayout.Width(84));
        if (GUILayout.Button("+", UiTheme.Btn, GUILayout.Width(28), GUILayout.Height(20)))
            ConfigPanel.SetFromControl(it, cur + delta);
    }

    private static void StepF(ConfigEntryBase it, float cur, float delta)
    {
        if (GUILayout.Button("−", UiTheme.Btn, GUILayout.Width(28), GUILayout.Height(20)))
            ConfigPanel.SetFromControl(it, (float)Math.Round(cur - delta, 4));
        // 手动输入: 数值项也允许直接打字, 弹幕列表这种长数字靠点太慢
        EditableNum(it, cur.ToString("0.###", CultureInfo.InvariantCulture),
                    t => float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? (object)f : null);
        if (GUILayout.Button("+", UiTheme.Btn, GUILayout.Width(28), GUILayout.Height(20)))
            ConfigPanel.SetFromControl(it, (float)Math.Round(cur + delta, 4));
    }

    private static void StepD(ConfigEntryBase it, double cur, double delta)
    {
        if (GUILayout.Button("−", UiTheme.Btn, GUILayout.Width(28), GUILayout.Height(20)))
            ConfigPanel.SetFromControl(it, Math.Round(cur - delta, 4));
        EditableNum(it, cur.ToString("0.###", CultureInfo.InvariantCulture),
                    t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (object)d : null);
        if (GUILayout.Button("+", UiTheme.Btn, GUILayout.Width(28), GUILayout.Height(20)))
            ConfigPanel.SetFromControl(it, Math.Round(cur + delta, 4));
    }

    /// <summary>数值项的输入框 —— 复用 TextField 的缓冲逻辑, 但用独立 key 前缀避免和字符串项串味。</summary>
    private static void EditableNum(ConfigEntryBase it, string cur, Func<string, object> parse)
    {
        var key = "num:" + KeyOf(it);
        if (!_buf.TryGetValue(key, out var text)) { text = cur; _buf[key] = text; }

        var rect = GUILayoutUtility.GetRect(1f, 20f, UiTheme.Field, new[] { GUILayout.Width(84) });
        var edited = GUI.TextField(rect, text ?? "", UiTheme.Field);
        if (string.Equals(edited, text, StringComparison.Ordinal)) return;

        _buf[key] = edited;
        var parsed = parse(edited);
        if (parsed != null) ConfigPanel.Set(it, parsed);
    }

    // ------------------------------------------------------------------ 枚举型选项

    /// <summary>
    /// 有限取值的字符串选项 → 一排选择按钮。返回 true 表示"这一行已经画完了"。
    ///
    /// 取值来自绑定时给的 <see cref="AcceptableValueList{T}"/>，例如
    /// `Config.Bind(sec, "Lever", "Inject", new ConfigDescription(desc, new AcceptableValueList&lt;string&gt;(...)))`。
    /// cfg 里也会自动带上 `# Acceptable values: ...` 注释。
    ///
    /// ⚠ 这个特性曾经因为一场启动崩溃被误删过一次（真凶是 JS 加载器的钩子，不是它）。
    ///   恢复时保留这条注释，免得下次再被当成嫌疑犯。
    /// </summary>
    internal static bool ChoiceRow(ConfigEntryBase it, string current)
    {
        if (!(it.Description?.AcceptableValues is AcceptableValueList<string> list)) return false;
        var opts = list.AcceptableValues;
        if (opts == null || opts.Length == 0) return false;

        foreach (var o in opts)
        {
            bool on = string.Equals(o, current, StringComparison.OrdinalIgnoreCase);
            if (GUILayout.Button(o, on ? UiTheme.ToggleOn : UiTheme.ToggleOff,
                                 GUILayout.Width(58), GUILayout.Height(20)) && !on)
                ConfigPanel.SetFromControl(it, o);   // 走 SetFromControl: 顺手清编辑缓冲
        }
        return true;
    }

    // ------------------------------------------------------------------ 颜色

    internal static void ColorRow(ConfigEntryBase it, string hex)
    {
        var c = Cfg.ParseColor(hex, Color.white);

        var r = GUILayoutUtility.GetRect(52f, 18f, UiTheme.Swatch, GUILayout.Width(52));
        var prev = GUI.color;
        GUI.color = c;
        // ⚠ 用 GUI.Label 而不是 GUI.Box: 本作 metadata 里 `GUI.Box` **只有**
        //   `Box(Rect, string)` 和 `Box(Rect, GUIContent, GUIStyle)` 两个重载 ——
        //   `Box(Rect, string, GUIStyle)` 只存在于 interop 程序集, 是空桩, 一调就抛。
        //   GUI.Label(Rect, string, GUIStyle) 是【确认在 metadata 里】的,
        //   而且给一个只有 normal.background 的样式时, 它画的就是那块背景 —— 效果等同。
        GUI.Label(r, "", UiTheme.Swatch);
        GUI.color = prev;
        GUILayout.Space(4);

        if (GUILayout.Button("◀", UiTheme.Btn, GUILayout.Width(26), GUILayout.Height(20)))
            ConfigPanel.SetFromControl(it, CycleHue(hex, -15f));
        if (GUILayout.Button("▶", UiTheme.Btn, GUILayout.Width(26), GUILayout.Height(20)))
            ConfigPanel.SetFromControl(it, CycleHue(hex, +15f));

        TextField(it, 110f, t =>
        {
            var t2 = t.Trim().TrimStart('#');
            if (t2.Length == 6 && IsHex(t2)) return Cfg.ToHex(Cfg.ParseColor(t2, Color.white));
            if (t.IndexOf(',') >= 0)
            {
                var pc = Cfg.ParseColor(t, new Color(-1f, -1f, -1f, 0f));
                if (pc.r >= 0f && pc.g >= 0f && pc.b >= 0f) return Cfg.ToHex(pc);
            }
            return null;   // 认不出就不写回, 等用户打完
        });
    }

    internal static bool IsHexColor(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        var t = s.TrimStart('#');
        return t.Length == 6 && IsHex(t);
    }

    private static bool IsHex(string t)
    {
        foreach (var ch in t)
            if (!Uri.IsHexDigit(ch)) return false;
        return true;
    }

    private static string CycleHue(string hex, float deg)
    {
        var c = Cfg.ParseColor(hex, Color.white);
        Color.RGBToHSV(c, out var h, out var s, out var v);
        if (s < 0.05f) s = 1f;                     // 灰/白给满饱和, 否则转了看不出
        h = Mathf.Repeat(h + deg / 360f, 1f);
        return Cfg.ToHex(Color.HSVToRGB(h, s, v));
    }
}
