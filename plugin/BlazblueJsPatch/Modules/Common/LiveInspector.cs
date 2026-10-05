using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 现场检查器 —— **在被冻结的世界里**列出捕获到的 GameObject，直接开关/挪位置，
/// 用"少一个就知道是谁"的办法把特效认出来。
///
/// 用户需求（原话）：
///   「ES 身上的东西太多了，我们得有一个面板列出所有被捕获的 GO，
///     来在被冻结的世界里调整它们的激活与否和位置，来判断特效到底是谁」
///
/// 这就是 UnityExplorer 的 Object List + Inspector 那一栏，自己做。
/// 配套: SnapshotTimeKey(慢放) → SnapshotKey(时停) → SnapshotPickKey(捕获) → 就是这个面板。
///
/// ★★ 最关键的一条设计决定: **列表里不持有任何 Unity 对象引用。**
///   只存 `实例ID + 路径字符串`；每次要操作时**现场按实例ID重新找一遍**。
///   原因（本项目为这类事崩过，见 PROJECT_STATE）：这游戏的特效全是**对象池**的，
///   池里的对象会被回收复用 —— 跨帧拿着旧包装/裸指针去写，写的就是别人的内存，
///   那是**不可 catch 的原生崩溃**（.NET Core 没法把原生 AV 变成托管异常）。
///   按ID现场重找的代价是"点一次按钮扫一遍场景"(几百个对象, 几百微秒)，
///   换的是"永远不会写错对象"。**这个交换永远划算。**
///
/// ⚠ IMGUI 纪律（照抄 BulletLab/ConfigPanel 的教训）：
///   · **绝不用时间去决定控件画不画** —— Layout / Repaint 是同帧两个事件，
///     控件数一旦不一致就是组栈失衡, 整帧从中间断掉 ⇒ 控件永远存在, 只改内容。
///   · 面板开着时 `InputBlocker` 会连 `Input.mousePosition` 一起屏蔽（它挡的是源头），
///     所以读鼠标要**优先走 IMGUI 的 Event.current.mousePosition**。
/// </summary>
internal static class LiveInspector
{
    internal static ConfigEntry<bool> CfgEnabled;
    internal static ConfigEntry<string> CfgKey;
    internal static ConfigEntry<float> CfgStep;
    internal static ConfigEntry<int> CfgMaxRows;

    private sealed class Item
    {
        public int Id;              // Go.GetInstanceID() —— 唯一的"引用"，合法且可跨帧
        public string Path = "";
        public bool Active;         // 扫描那一刻的快照（不是实时值）
        public bool Rendered;
        public Vector3 Pos;         // 扫描那一刻的世界位置（用来"重置位置"）
        public bool Touched;        // 被我们改过（"还原全部"要负责把它还原）
        public bool Active0;        // 我们动手**之前**的值
        public bool Rendered0;
    }

    private static bool _open;
    private static readonly List<Item> _items = new List<Item>();
    private static readonly List<Item> _shown = new List<Item>();
    private static int _sel = -1;
    private static Vector2 _scroll;
    private static Rect _rect = new Rect(40f, 30f, 620f, 760f);
    private static string _search = "";
    private static string _note = "";
    private static float _noteUntil;
    private static bool _dragging;
    private static Vector2 _dragOff;
    private static float _step = 0.5f;
    private static bool _playerOnly = true;
    private static bool _visibleOnly;
    private static bool _cursorSaved;
    private static bool _cursorVisible;
    private static CursorLockMode _cursorLock;

    internal static bool IsOpen => _open;

    // 文本框缓冲（编辑期间显示"正在打的字"，否则半截输入下一帧就被原值覆盖）
    private static readonly Dictionary<string, string> _buf = new Dictionary<string, string>();

    private static KeyCode ToggleKey
    {
        get
        {
            var s = (CfgKey?.Value ?? "F10").Trim();
            if (!string.IsNullOrWhiteSpace(s) && Enum.TryParse(s, true, out KeyCode k)) return k;
            return KeyCode.F10;
        }
    }

    public static int Apply(Harmony harmony)
    {
        // 本模块**不挂任何 Hook**（纯 IMGUI 面板 + 按需扫描）。
        // 按本项目纪律，"没有 Hook"也必须说清楚，否则挂载表里那行看起来像坏了。
        Plugin.Log?.LogInfo($"  [现场检查器] 就绪（IMGUI 驱动, 无需 Hook）; 开关键={CfgKey?.Value}");
        return 0;
    }

    // ------------------------------------------------------------------ 入口

    internal static void OnGui()
    {
        try
        {
            if (CfgEnabled?.Value != true) return;
            var e = Event.current;
            if (e == null) return;

            if (e.type == EventType.KeyDown && e.keyCode == ToggleKey)
            {
                _open = !_open;
                if (_open) { Scan(); ApplyCursor(true); }
                else ApplyCursor(false);
                e.Use();
            }
            if (!_open) return;

            UiTheme.Ensure();
            if (!UiTheme.Ok) return;      // 皮肤没建起来就一个字都别画（见 ConfigPanel 同款注释）

            if (e.type == EventType.KeyDown) HandleKeys(e);
            Draw(e);
        }
        catch (Exception ex) { LogEx.Err("LiveInspector.OnGui", ex); }
    }

    /// <summary>
    /// 面板开着时把光标放出来、解锁。
    /// 本作平时大概是把光标藏起来/锁住的，那样 IMGUI 面板**点不到**。
    /// 关闭时还原成打开前的状态（不假设游戏想要什么）。
    /// </summary>
    private static void ApplyCursor(bool on)
    {
        try
        {
            if (on)
            {
                if (!_cursorSaved)
                {
                    _cursorVisible = Cursor.visible;
                    _cursorLock = Cursor.lockState;
                    _cursorSaved = true;
                }
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
            else if (_cursorSaved)
            {
                Cursor.visible = _cursorVisible;
                Cursor.lockState = _cursorLock;
                _cursorSaved = false;
            }
        }
        catch (Exception ex) { Reflect.WarnOnce("inspect|cursor", "光标还原", ex); }
    }

    // ------------------------------------------------------------------ 键盘（鼠标之外的备选）

    private static void HandleKeys(Event e)
    {
        if (_shown.Count == 0) return;
        switch (e.keyCode)
        {
            case KeyCode.UpArrow:
                _sel = _sel <= 0 ? _shown.Count - 1 : _sel - 1;
                ScrollToSel(); e.Use(); break;
            case KeyCode.DownArrow:
                _sel = _sel >= _shown.Count - 1 ? 0 : _sel + 1;
                ScrollToSel(); e.Use(); break;
            case KeyCode.PageUp:
                _sel = Math.Max(0, _sel - 10); ScrollToSel(); e.Use(); break;
            case KeyCode.PageDown:
                _sel = Math.Min(_shown.Count - 1, _sel + 10); ScrollToSel(); e.Use(); break;
            case KeyCode.Space:
                // ⚠ 键盘这边同样安全优先: 空格 = 安全的那一个; 破坏性的必须带 Shift。
                if (e.shift) ToggleActive(); else ToggleRender();
                e.Use(); break;
            case KeyCode.E:
                // 位置微调用键盘更快（Shift+E 反向）：按住不松就连着挪
                NudgeSel(e.shift ? -1f : +1f); e.Use(); break;
            case KeyCode.R:
                ResetPos(); e.Use(); break;
        }
    }

    /// <summary>键盘微调选中项的位置（默认沿 X 轴）—— 挪位置是"认特效"最直接的手段。</summary>
    private static void NudgeSel(float sign)
    {
        var it = Current();
        if (it == null) { Note("没有选中项"); return; }
        var go = FindById(it.Id);
        NudgeGo(it, go, 0, sign);
    }

    private static void ScrollToSel()
    {
        // 让选中行尽量留在可见区（粗略：按行高 22px 推）
        _scroll.y = Mathf.Max(0f, _sel * 22f - 200f);
    }

    // ------------------------------------------------------------------ 绘制

    private static void Draw(Event e)
    {
        _rect.x = Mathf.Clamp(_rect.x, -_rect.width + 80f, Screen.width - 80f);
        _rect.y = Mathf.Clamp(_rect.y, 0f, Screen.height - 30f);

        DragHeader(e);

        GUILayout.BeginArea(_rect, UiTheme.Win);
        try
        {
            Header();
            GUILayout.Space(3f);
            SelectedBlock();
            GUILayout.Space(3f);
            FilterBar();
            GUILayout.Space(2f);
            ListBlock();
            GUILayout.Space(3f);
            // ⚠ 控件永远存在，只改内容 —— 绝不用时间决定画不画（见类注释）
            GUILayout.Label((!string.IsNullOrEmpty(_note) && Time.realtimeSinceStartup < _noteUntil) ? _note : "",
                            UiTheme.Section);
        }
        finally { GUILayout.EndArea(); }
    }

    private static void DragHeader(Event e)
    {
        if (e == null) return;
        bool inHeader = e.mousePosition.x >= _rect.x && e.mousePosition.x <= _rect.x + _rect.width
                     && e.mousePosition.y >= _rect.y && e.mousePosition.y <= _rect.y + 24f;
        if (e.type == EventType.MouseDown && inHeader)
        {
            _dragging = true;
            _dragOff = e.mousePosition - new Vector2(_rect.x, _rect.y);
        }
        else if (e.type == EventType.MouseUp) _dragging = false;
        else if (_dragging && e.type == EventType.MouseDrag)
        {
            _rect.x = e.mousePosition.x - _dragOff.x;
            _rect.y = e.mousePosition.y - _dragOff.y;
            e.Use();
        }
    }

    private static void Header()
    {
        GUILayout.BeginHorizontal();
        try
        {
            GUILayout.Label($"现场检查器   {ToggleKey} 开关   共 {_items.Count} 个 / 显示 {_shown.Count}" +
                            (Time.timeScale <= 0.0001f ? "   ⏸ 已时停" : $"   timeScale={Time.timeScale:0.###}"),
                            UiTheme.Title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("刷新", UiTheme.Blue, GUILayout.Width(50f), GUILayout.Height(20f))) Scan();
            if (GUILayout.Button("关闭", UiTheme.Red, GUILayout.Width(48f), GUILayout.Height(20f)))
            { _open = false; ApplyCursor(false); }
        }
        finally { GUILayout.EndHorizontal(); }
    }

    private static void FilterBar()
    {
        GUILayout.BeginHorizontal();
        try
        {
            bool p = GUILayout.Toggle(_playerOnly, " 只在玩家身上 ", GUILayout.Width(120f));
            if (p != _playerOnly) { _playerOnly = p; Rescan(); }
            bool v = GUILayout.Toggle(_visibleOnly, " 只看当前可见 ", GUILayout.Width(120f));
            if (v != _visibleOnly) { _visibleOnly = v; Rescan(); }
            GUILayout.Label("过滤:", UiTheme.Label, GUILayout.Width(40f));
            Text(ref _search, 150f, "search");
        }
        finally { GUILayout.EndHorizontal(); }
    }

    private static void SelectedBlock()
    {
        GUILayout.BeginVertical(UiTheme.Box);
        try
        {
            var it = Current();
            if (it == null)
            {
                GUILayout.Label("── 选中: （列表里点一行；或 ℹ 用 ↑↓ 选）──", UiTheme.Section);
                return;
            }
            GUILayout.Label($"── 选中: \"{it.Path}\"", UiTheme.Section);

            // 位置：这里显示的也是**重找之后现读的实时值**，不是缓存的
            var go = FindById(it.Id);
            Vector3 pos = it.Pos;
            bool act = it.Active;
            bool rend = it.Rendered;
            if (go != null)
            {
                try { pos = go.transform.position; } catch { }
                try { act = go.activeSelf; } catch { }
                rend = RendererOn(go, it.Rendered);
                it.Pos = pos; it.Active = act; it.Rendered = rend;
            }
            GUILayout.Label($"位置 ({pos.x:0.###}, {pos.y:0.###}, {pos.z:0.###})   " +
                            $"激活={act}   渲染={rend}   {(go == null ? "⚠ 已经不在了" : "")}");

            GUILayout.BeginHorizontal();
            try
            {
                Nudge("X-", go, 0, -1f);
                Nudge("X+", go, 0, +1f);
                Nudge("Y-", go, 1, -1f);
                Nudge("Y+", go, 1, +1f);
                Nudge("Z-", go, 2, -1f);
                Nudge("Z+", go, 2, +1f);
                GUILayout.Label("步长", UiTheme.Label, GUILayout.Width(34f));
                Text(ref _stepText, 56f, "step");
                if (float.TryParse(_stepText, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float st) && st > 0f) _step = st;
            }
            finally { GUILayout.EndHorizontal(); }

            GUILayout.BeginHorizontal();
            try
            {
                // ★ 安全动作放前面、按默认路径上 —— `Renderer.enabled` 只是"不画"，
                //   不触发 Unity 的 OnEnable/OnDisable，也**不会惊动对象池**。
                if (GUILayout.Button("切换 渲染 enabled  【安全】", UiTheme.Blue, GUILayout.Height(20f))) ToggleRender();
                if (GUILayout.Button("重置位置", UiTheme.Btn, GUILayout.Height(20f))) ResetPos();
                if (GUILayout.Button("打印详情到日志", UiTheme.Btn, GUILayout.Height(20f))) DumpSel();
                if (GUILayout.Button("★ 还原全部改动", UiTheme.Blue, GUILayout.Height(20f))) UndoAll();
            }
            finally { GUILayout.EndHorizontal(); }

            GUILayout.BeginHorizontal();
            try
            {
                // ⚠⚠ SetActive 从"随手点"降级成"按住 Shift 才生效"的破坏性动作。
                bool shift = false;
                try { var ev = Event.current; shift = ev != null && ev.shift; } catch { }
                if (GUILayout.Button(shift ? "⚠ 切换 激活 SetActive（会触发对象池回收！）" : "⚠ 切换 激活 SetActive（按住 Shift 才生效）",
                                     UiTheme.Red, GUILayout.Height(20f)))
                {
                    if (shift) ToggleActive();
                    else Note("SetActive 是破坏性动作：**按住 Shift 再点**。\n" +
                              "原因：本作特效全是 RecycleRoot 对象池的，SetActive(false) 会把对象判为空闲并被别处复用\n" +
                              "（实测：隐藏整个角色再显示 ⇒ 光环消失不回来 + 角色渲染损坏）。\n" +
                              "★ 想「少一个看是谁」，请用上面的【渲染 enabled】——它只影响绘制，不惊动池子。");
                }
            }
            finally { GUILayout.EndHorizontal(); }
        }
        finally { GUILayout.EndVertical(); }
    }

    private static string _stepText = "0.5";

    private static void Nudge(string label, GameObject go, int axis, float sign)
    {
        if (!GUILayout.Button(label, UiTheme.Btn, GUILayout.Width(32f), GUILayout.Height(20f))) return;
        if (go == null) { Note("对象已经不在了（池子收走了）—— 按【刷新】重扫"); return; }
        var it = Current();
        if (it != null && !it.Touched) { it.Active0 = it.Active; it.Rendered0 = it.Rendered; it.Touched = true; }
        NudgeGo(it, go, axis, sign);
    }

    private static void NudgeGo(Item it, GameObject go, int axis, float sign)
    {
        if (go == null) { Note("对象已经不在了（池子收走了）—— 按【刷新】重扫"); return; }
        if (it != null && !it.Touched) { it.Active0 = it.Active; it.Rendered0 = it.Rendered; it.Touched = true; }
        try
        {
            var p = go.transform.position;
            float d = _step * sign;
            if (axis == 0) p.x += d; else if (axis == 1) p.y += d; else p.z += d;
            go.transform.position = p;
            Note($"\"{Reflect.Name(go)}\" 位置 -> ({p.x:0.###}, {p.y:0.###}, {p.z:0.###})");
        }
        catch (Exception e) { Note("挪位置失败: " + LogEx.Unwrap(e)); }
    }

    private static void ListBlock()
    {
        GUILayout.Label("── 列表（点 ○ 选中；[渲/空] = Renderer.enabled【安全】。" +
                        "⚠ SetActive 已从行里撤掉 —— 它会被对象池回收, 现在只在选中区按住 Shift 才生效）──",
                        UiTheme.Section);
        _scroll = GUILayout.BeginScrollView(_scroll, UiTheme.Box, GUILayout.Height(Mathf.Max(120f, _rect.height - 300f)));
        try
        {
            int max = CfgMaxRows?.Value ?? 200;
            int n = Math.Min(_shown.Count, max);
            for (int i = 0; i < n; i++)
            {
                var it = _shown[i];
                bool selected = i == _sel;
                GUILayout.BeginHorizontal(selected ? UiTheme.RowHi : (i % 2 == 0 ? UiTheme.Box : UiTheme.BoxAlt));
                try
                {
                    // 选中按钮
                    if (GUILayout.Button(selected ? "●" : "○", UiTheme.Btn, GUILayout.Width(24f), GUILayout.Height(18f)))
                    { _sel = i; }

                    // 开关(要现找对象, 但只在点的时候)
                    // ⚠ 行里**只留安全的那一个**（Renderer.enabled）。
                    //   SetActive 原来也在行里, 结果"顺手点一下"就把对象丢给了池子 ——
                    //   工具的危险动作不该长在"一行一个、手滑就点到"的位置上。
                    if (GUILayout.Button(it.Rendered ? "渲" : "空", it.Rendered ? UiTheme.ToggleOn : UiTheme.ToggleOff,
                                         GUILayout.Width(30f), GUILayout.Height(18f)))
                    { _sel = i; ToggleItem(it, false); }
                    if (it.Touched)
                        GUILayout.Label("✎", UiTheme.Section, GUILayout.Width(14f));   // 标出"被我改过"

                    GUILayout.Label(it.Path, UiTheme.Label);
                }
                finally { GUILayout.EndHorizontal(); }
            }
            if (_shown.Count > n)
                GUILayout.Label($"…（还有 {_shown.Count - n} 行没列 —— 上限 InspectorMaxRows={max}，" +
                                $"用上面的过滤缩小范围，或把上限调大）", UiTheme.Section);
        }
        finally { GUILayout.EndScrollView(); }
    }

    /// <summary>
    /// 自己画文本框。
    /// ⚠ 不能用 `GUILayout.TextField` —— 本作被 UnityLinker 做过方法级 strip，
    ///   它在 metadata 里**不存在**（见 UiWidgets 的长注释）。
    ///   能用的只有三参 `GUI.TextField(Rect, string, GUIStyle)`。
    /// </summary>
    private static void Text(ref string value, float width, string key)
    {
        if (!_buf.TryGetValue(key, out var text)) { text = value ?? ""; _buf[key] = text; }
        var opts = new[] { GUILayout.Width(width), GUILayout.Height(20f) };
        var rect = GUILayoutUtility.GetRect(width, 20f, UiTheme.Field, opts);
        var edited = GUI.TextField(rect, text ?? "", UiTheme.Field);
        if (string.Equals(edited, text, StringComparison.Ordinal)) return;
        _buf[key] = edited;
        value = edited;
    }

    // ------------------------------------------------------------------ 扫描

    private static void Scan()
    {
        _items.Clear();
        int scanned = 0, noGo = 0, skipped = 0;
        try
        {
            var arr = FindAll(typeof(Renderer));
            var seen = new HashSet<int>();
            foreach (var o in Reflect.Items(arr))
            {
                var r = Reflect.Cast<Renderer>(o);
                if (r == null) continue;
                scanned++;
                try
                {
                    var go = r.gameObject;
                    if (go == null) { noGo++; continue; }
                    int id;
                    try { id = go.GetInstanceID(); } catch { continue; }
                    if (id == 0 || !seen.Add(id)) continue;

                    string path = PathOf(go);
                    bool active = false, rendered = false;
                    try { active = go.activeSelf; } catch { }
                    try { rendered = r.enabled; } catch { }
                    if (_playerOnly && path.IndexOf("Player", StringComparison.OrdinalIgnoreCase) < 0) { skipped++; continue; }
                    if (_visibleOnly && !(active && rendered)) { skipped++; continue; }

                    Vector3 pos = Vector3.zero;
                    try { pos = go.transform.position; } catch { }

                    _items.Add(new Item { Id = id, Path = path, Active = active, Rendered = rendered, Pos = pos });
                }
                catch { noGo++; }
            }
        }
        catch (Exception e) { Note("扫描失败: " + LogEx.Unwrap(e)); }

        _items.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        Rescan();
        _sel = Math.Min(_sel, _shown.Count - 1);
        if (_sel < 0 && _shown.Count > 0) _sel = 0;
        Note($"扫描完成: 渲染器 {scanned} 个 → 去重后 {_items.Count} 个" +
             $"（玩家过滤掉 {skipped}, 取不到 GO {noGo}）");
    }

    /// <summary>只按当前过滤条件重排显示列表，不重新扫场景。</summary>
    private static void Rescan()
    {
        _shown.Clear();
        foreach (var it in _items)
        {
            if (!string.IsNullOrEmpty(_search) &&
                it.Path.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _shown.Add(it);
        }
        if (_sel >= _shown.Count) _sel = _shown.Count - 1;
    }

    private static Item Current()
        => _sel >= 0 && _sel < _shown.Count ? _shown[_sel] : null;

    /// <summary>
    /// **按实例ID现场重找对象** —— 这是本模块唯一允许的"引用"方式。
    /// ⚠ 必须带 includeInactive：我们要找回来的东西往往正是**刚被自己关掉**的那些，
    ///   只扫激活对象会永远找不回它（然后表现为"关了就开不回来"）。
    /// </summary>
    private static GameObject FindById(int id)
    {
        if (id == 0) return null;
        try
        {
            foreach (var o in Reflect.Items(FindAll(typeof(Transform))))
            {
                var t = Reflect.Cast<Transform>(o);
                if (t == null) continue;
                GameObject g = null;
                try { g = t.gameObject; } catch { continue; }
                if (g == null) continue;
                try { if (g.GetInstanceID() == id) return g; } catch { }
            }
        }
        catch (Exception e) { Reflect.WarnOnce("inspect|find", "现场检查器/重找对象", e); }
        return null;
    }

    private static bool RendererOn(GameObject go, bool dflt)
    {
        try { var r = go.GetComponent<Renderer>(); return r == null ? dflt : r.enabled; }
        catch { return dflt; }
    }

    /// <summary>取场上所有 T。优先带 includeInactive 的那条重载（关掉的也要能找到）。</summary>
    private static object FindAll(Type t)
    {
        var ty = Il2CppInterop.Runtime.Il2CppType.From(t);
        try { return UnityEngine.Object.FindObjectsOfType(ty, true); }
        catch { return UnityEngine.Object.FindObjectsOfType(ty); }
    }

    // ------------------------------------------------------------------ 操作

    private static void ToggleActive()
    {
        var it = Current();
        if (it == null) { Note("没有选中项"); return; }
        ToggleItem(it, true);
    }

    private static void ToggleRender()
    {
        var it = Current();
        if (it == null) { Note("没有选中项"); return; }
        ToggleItem(it, false);
    }

    private static void ToggleItem(Item it, bool active)
    {
        var go = FindById(it.Id);
        if (go == null) { Note("对象已经不在了（池子收走了）—— 按【刷新】重扫"); return; }
        try
        {
            if (!it.Touched) { it.Active0 = it.Active; it.Rendered0 = it.Rendered; it.Touched = true; }

            if (active)
            {
                // ⚠⚠ 破坏性：本作特效全是 RecycleRoot 对象池的。
                //   SetActive(false) 会被池子当成"空闲"而回收复用（实测: 隐藏整个角色再显示
                //   ⇒ 光环消失不回来 + 角色渲染损坏）。要做"少一个看是谁"请用 Renderer.enabled。
                bool v = !go.activeSelf;
                go.SetActive(v);
                it.Active = v;
                Note($"⚠ \"{it.Path}\" SetActive({v}) —— 破坏性操作, 可用【还原全部改动】救回已记录的项");
            }
            else
            {
                // ★ 安全路径：一次把这个 GameObject 上**所有**渲染器一起开关。
                //   只翻第一个的话，多材质对象会"关了一半"，看起来像没效果。
                var rs = go.GetComponents(Il2CppInterop.Runtime.Il2CppType.From(typeof(Renderer)));
                int n = 0, on = 0;
                foreach (var o in Reflect.Items(rs))
                {
                    var r = Reflect.Cast<Renderer>(o);
                    if (r == null) continue;
                    bool v = !r.enabled;
                    r.enabled = v;
                    n++;
                    if (v) on++;
                }
                if (n == 0) { Note($"\"{it.Path}\" 上没有 Renderer"); return; }
                it.Rendered = on > 0;
                Note($"\"{it.Path}\" Renderer.enabled = {it.Rendered}（{n} 个渲染器，{on} 个开）");
            }
        }
        catch (Exception e) { Note("切换失败: " + LogEx.Unwrap(e)); }
    }

    /// <summary>
    /// ★ 还原全部改动 —— 把动过的东西恢复成**动手之前**的样子。
    ///
    /// 为什么必须有: 现场检查器是"靠破坏来辨认"的工具, 一次实验会连改几十个对象;
    /// 没有一键还原的话, 实验做一半人就迷路了（而且实测 SetActive 会真的搞坏东西）。
    /// ⚠ 只认"我们改过的"（Touched）—— 不去碰用户/游戏自己的状态。
    /// ⚠ 位置也一起还原（挪位置同样是这个工具的主要手段）。
    /// </summary>
    private static void UndoAll()
    {
        int n = 0, missing = 0;
        foreach (var it in _items)
        {
            if (!it.Touched) continue;
            var go = FindById(it.Id);
            if (go == null) { missing++; continue; }
            try
            {
                if (go.activeSelf != it.Active0) go.SetActive(it.Active0);
                var rs = go.GetComponents(Il2CppInterop.Runtime.Il2CppType.From(typeof(Renderer)));
                foreach (var o in Reflect.Items(rs))
                {
                    var r = Reflect.Cast<Renderer>(o);
                    if (r != null) r.enabled = it.Rendered0;
                }
                go.transform.position = it.Pos;
                it.Active = it.Active0; it.Rendered = it.Rendered0;
                it.Touched = false;
                n++;
            }
            catch (Exception e) { Reflect.WarnOnce("inspect|undo|" + it.Id, "还原对象", e); }
        }
        Note($"还原完成: {n} 个已还原" + (missing > 0 ? $", {missing} 个已经不在场上了（池子收走了, 救不回）" : ""));
    }

    private static void ResetPos()
    {
        var it = Current();
        if (it == null) { Note("没有选中项"); return; }
        var go = FindById(it.Id);
        if (go == null) { Note("对象已经不在了 —— 按【刷新】重扫"); return; }
        try
        {
            go.transform.position = it.Pos;
            Note($"\"{it.Path}\" 位置已重置回扫描时的 ({it.Pos.x:0.###}, {it.Pos.y:0.###}, {it.Pos.z:0.###})");
        }
        catch (Exception e) { Note("重置失败: " + LogEx.Unwrap(e)); }
    }

    private static void DumpSel()
    {
        var it = Current();
        if (it == null) { Note("没有选中项"); return; }
        var go = FindById(it.Id);
        if (go == null) { Note("对象已经不在了 —— 按【刷新】重扫"); return; }
        try
        {
            Plugin.Log?.LogInfo($"[现场检查器] ══ 打印 \"{it.Path}\" ══");
            DashAnatomy.DumpPublic(go, $"现场检查器 ▸ \"{it.Path}\"");
            Note("详情已写进日志");
        }
        catch (Exception e) { Note("打印失败: " + LogEx.Unwrap(e)); }
    }

    private static void Note(string s)
    {
        _note = s;
        try { _noteUntil = Time.realtimeSinceStartup + 8f; } catch { _noteUntil = 0f; }
        Plugin.Log?.LogInfo("[现场检查器] " + s);
    }

    // ------------------------------------------------------------------ 工具

    private static string PathOf(GameObject go)
    {
        try
        {
            var parts = new List<string>();
            var t = go.transform;
            for (int i = 0; i < 8 && t != null; i++)
            {
                parts.Add(Reflect.Name(t.gameObject));
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }
        catch { return Reflect.Name(go); }
    }
}
