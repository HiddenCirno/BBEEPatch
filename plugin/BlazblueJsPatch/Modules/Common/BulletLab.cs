using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// F9「弹幕实验台」—— 把"某个动作到底发不发弹幕"从翻日志变成点按钮。
///
/// 为什么需要它
/// ────────────
/// 探针(<see cref="BulletProbe"/>)只能抓到**真的走 BulletMgr 的**那些弹幕。
/// 想判断"蓄力攻击那两下纹章是不是弹幕"，靠翻日志要来回好几轮，而且
/// 探针没抓到时有歧义：是没发射？还是压根不是弹幕？
/// 这个面板把歧义消掉：
///   · 列出**全表**弹幕（含从没被捕获过的），点一下就放 —— 表里有 = 它是弹幕
///   · 顶部实时回显**最近捕获**的弹幕，并支持原地重放
///   · 底部文本框可以手填 名称 / id / id:名称，表里没有的也照样试放
///
/// 数据来源（不需要找实例，静态直取）
/// ──────────────────────────────────
///     public static class Xlsx
///         public static XlsxLoader_BulletConfigFixedPoint BulletConfigFixedPoint { get; }
///             public ListType All { get; }        // List&lt;BulletConfigFixedPointWrap&gt;
///                 Wrap.Id / Wrap.StartAction / Wrap.LogicRes
/// 弹幕是 **(Id, StartAction) 双键**索引的（见 XlsxLoader_..._IndexType 的
/// `Dictionary&lt;ValueTuple&lt;int,string&gt;, Wrap&gt;`），所以只有名称不足以定位，
/// 名称在表里查不到时我们会退回"借用最近捕获的 Id"。
///
/// ⚠ 命名空间不确定（dump 里既像 Xlsx.XlsxLoader_... 又像全局的 Xlsx），
///   所以**走运行时反射**按属性名找，不写死类型 —— 与 ActionJournal.cs 同一套风格。
///
/// ⚠ 放弹幕**绝不能**在这里直接调 CreateBulletByParams：
///   本方法跑在 IMGUI 里，且历史教训是当场调用会破坏 BulletList 的枚举。
///   一律入队 <see cref="Pending"/>，由 EsEmblemBurst 的帧末钩子统一发射。
/// </summary>
internal static class BulletLab
{
    // ------------------------------------------------------------------ 配置

    internal static ConfigEntry<bool> CfgEnabled;
    internal static ConfigEntry<string> CfgToggleKey;

    internal static KeyCode ToggleKey
    {
        get
        {
            try
            {
                var s = CfgToggleKey?.Value;
                if (!string.IsNullOrWhiteSpace(s) && Enum.TryParse(s.Trim(), true, out KeyCode k)) return k;
            }
            catch { }
            return KeyCode.F9;
        }
    }

    // ------------------------------------------------------------------ 待发队列

    /// <summary>一次"放弹幕"请求。只装纯托管类型，真正的发射在 EsEmblemBurst 里做。</summary>
    internal sealed class Shot
    {
        public int Id;
        public string Name = "";
        /// <summary>发射方向相对"触发弹幕原本方向"的偏转角（度）。0 = 原样。</summary>
        public float Deg;
    }

    internal static readonly List<Shot> Pending = new List<Shot>();

    /// <summary>由帧末钩子调用：取走当前所有待发请求（并清空）。</summary>
    internal static Shot[] DrainPending()
    {
        if (Pending.Count == 0) return Array.Empty<Shot>();
        var a = Pending.ToArray();
        Pending.Clear();
        return a;
    }

    // ------------------------------------------------------------------ 探针回写

    /// <summary>最近一次被探针捕获的弹幕（由 EsEmblemBurst 的探针写回）。</summary>
    internal static int LastIdx;
    internal static string LastName = "";
    internal static string LastRes = "";
    internal static string LastPos = "";
    internal static string LastDir = "";
    internal static int LastCaptureCount;

    // ------------------------------------------------------------------ 表

    private sealed class Row { public int Id; public string Name = ""; public string Res = ""; }

    private static readonly List<Row> _all = new List<Row>();
    private static readonly List<Row> _shown = new List<Row>();
    private static bool _loaded;

    internal static int TableCount => _all.Count;

    // ------------------------------------------------------------------ UI 状态

    private static bool _open;
    private static bool _esOnly = true;
    private static string _search = "";
    private static string _manual = "";
    private static float _dirDeg;          // 试放/重放时的方向偏转（度）
    private static Vector2 _scroll;
    // ⚠ 高度是写死的：加密切一行就要跟着加，否则最后那个块会被挤出可见区
    //   （滑条"没显示"就是这么来的 —— 块画在区域外，看着像没加）。
    private static Rect _rect = new Rect(-1f, -1f, 470f, 680f);
    private static string _note = "";
    private static float _noteUntil;
    private static bool _dragging;
    private static Vector2 _dragOff;

    internal static bool IsOpen => _open;

    private static void Note(string s)
    {
        _note = s;
        try { _noteUntil = Time.realtimeSinceStartup + 6f; } catch { _noteUntil = 0f; }
        Plugin.Log?.LogInfo("[弹幕实验台] " + s);
    }

    // ------------------------------------------------------------------ 入口

    internal static void OnGui()
    {
        try
        {
            if (CfgEnabled?.Value != true) return;

            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown && e.keyCode == ToggleKey)
            {
                _open = !_open;
                if (_open && !_loaded) Reload();
                e.Use();
            }
            if (!_open) return;

            UiTheme.Ensure();
            // 样式没建起来就一个字都别画 —— 见 ConfigPanel.Draw 的同款注释
            if (!UiTheme.Ok) return;

            Draw();
        }
        catch (Exception ex) { LogEx.Err("BulletLab.OnGui", ex); }
    }

    // ------------------------------------------------------------------ 绘制

    private static void Draw()
    {
        if (_rect.x < 0f) _rect.x = 40f;
        if (_rect.y < 0f) _rect.y = 40f;
        if (_rect.x + _rect.width > Screen.width) _rect.x = Mathf.Max(0f, Screen.width - _rect.width);
        if (_rect.y + _rect.height > Screen.height) _rect.y = Mathf.Max(0f, Screen.height - _rect.height);

        DragHeader(Event.current);

        GUILayout.BeginArea(_rect, UiTheme.Win);
        try
        {
            Header();
            GUILayout.Space(4);
            LatestBlock();
            GUILayout.Space(4);
            FilterBar();
            GUILayout.Space(2);
            ListBlock();
            GUILayout.Space(4);
            ManualBlock();
            // ⚠ 同 ConfigPanel.Hint 的坑：**绝不能用时间去决定控件画不画** ——
            //   Layout / Repaint 是同一帧的两个事件，时间在两者之间会前进，
            //   一旦过期时刻落在中间，两遍的控件数就不一致 → 组栈失衡 → 整帧从中间断掉。
            //   控件永远存在，只改内容。
            GUILayout.Label((!string.IsNullOrEmpty(_note) && Time.realtimeSinceStartup < _noteUntil)
                            ? _note : "", UiTheme.Section);
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
            GUILayout.Label($"弹幕实验台   F{ (int)ToggleKey - (int)KeyCode.F1 + 1 }   表 {_all.Count} 条 / 显示 {_shown.Count}", UiTheme.Title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("重载表", UiTheme.Blue, GUILayout.Width(60), GUILayout.Height(20))) Reload();
            if (GUILayout.Button("关闭", UiTheme.Red, GUILayout.Width(48), GUILayout.Height(20))) _open = false;
        }
        finally { GUILayout.EndHorizontal(); }
    }

    private static void LatestBlock()
    {
        GUILayout.BeginVertical(UiTheme.Box);
        try
        {
            GUILayout.Label("── 最近捕获（探针实时回显） ──", UiTheme.Section);
            if (LastIdx == 0 && string.IsNullOrEmpty(LastName))
            {
                GUILayout.Label("（还没有捕获到任何弹幕）");
            }
            else
            {
                GUILayout.BeginHorizontal();
                try
                {
                    GUILayout.Label($"[{LastIdx}]  \"{LastName}\"   {LastRes}   (累计 {LastCaptureCount})");
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("原地重放", UiTheme.Blue, GUILayout.Width(70), GUILayout.Height(20)))
                    {
                        Fire(LastIdx, LastName);
                        Note($"已排队重放 [{LastIdx}] \"{LastName}\"");
                    }
                }
                finally { GUILayout.EndHorizontal(); }
                // 方向/位置 —— 有方向性的纹章(如 aH2EX/aH3EX)靠这里读原生朝向
                GUILayout.Label($"pos={LastPos}   dir={LastDir}", UiTheme.Section);
            }
        }
        finally { GUILayout.EndVertical(); }
    }

    private static void FilterBar()
    {
        GUILayout.BeginHorizontal(UiTheme.Box);
        try
        {
            bool es = GUILayout.Toggle(_esOnly, " 只看 esbullet ", GUILayout.Width(110));
            if (es != _esOnly) { _esOnly = es; Rebuild(); }

            string s = GUILayout.TextField(_search ?? "", GUILayout.Width(180));
            if (s != _search) { _search = s; Rebuild(); }

            if (GUILayout.Button("清空", GUILayout.Width(48), GUILayout.Height(20))) { _search = ""; Rebuild(); }
            GUILayout.Label("搜 id / 名称 / LogicRes", UiTheme.Section);
        }
        finally { GUILayout.EndHorizontal(); }
    }

    private static void ListBlock()
    {
        _scroll = GUILayout.BeginScrollView(_scroll, UiTheme.Box, GUILayout.Height(300));
        try
        {
            if (!_loaded)
            {
                GUILayout.Label("表未载入 —— 点右上「重载表」");
            }
            else if (_shown.Count == 0)
            {
                GUILayout.Label("没有匹配项");
            }
            else
            {
                for (int i = 0; i < _shown.Count; i++)
                {
                    var r = _shown[i];
                    GUILayout.BeginHorizontal();
                    try
                    {
                        GUILayout.Label($"{r.Id,10}  {r.Res,-14}  \"{r.Name}\"");
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("放", UiTheme.Blue, GUILayout.Width(34), GUILayout.Height(18)))
                        {
                            Fire(r.Id, r.Name);
                            Note($"已排队 [{r.Id}] \"{r.Name}\"");
                        }
                    }
                    finally { GUILayout.EndHorizontal(); }
                }
            }
        }
        finally { GUILayout.EndScrollView(); }
    }

    private static void ManualBlock()
    {
        GUILayout.BeginVertical(UiTheme.Box);
        try
        {
            GUILayout.Label("── 手动试放（表里没有的也照试） ──", UiTheme.Section);
            GUILayout.BeginHorizontal();
            try
            {
                _manual = GUILayout.TextField(_manual ?? "", GUILayout.Width(300));
                if (GUILayout.Button("放", UiTheme.Blue, GUILayout.Width(40), GUILayout.Height(20))) FireManual();
                if (GUILayout.Button("填最近", GUILayout.Width(60), GUILayout.Height(20)))
                    _manual = string.IsNullOrEmpty(LastName) ? LastIdx.ToString() : LastIdx + ":" + LastName;
            }
            finally { GUILayout.EndHorizontal(); }
            GUILayout.Label("可填： 名称   或   id   或   id:名称");

            // ── 方向偏转滑条：试放和「原地重放」都会带上这个角度 ──
            GUILayout.BeginHorizontal();
            try
            {
                GUILayout.Label("方向偏转", UiTheme.Section, GUILayout.Width(64));
                _dirDeg = GUILayout.HorizontalSlider(_dirDeg, -180f, 180f, GUILayout.Width(230));
                GUILayout.Label(((int)Mathf.Round(_dirDeg)).ToString() + "°", GUILayout.Width(44));
                if (GUILayout.Button("归零", GUILayout.Width(46), GUILayout.Height(18))) _dirDeg = 0f;
            }
            finally { GUILayout.EndHorizontal(); }
            GUILayout.Label("作用在发射瞬间的 dir 上（局部空间：0 = 原样，转身时方向跟着转）");
        }
        finally { GUILayout.EndVertical(); }
    }

    // ------------------------------------------------------------------ 放弹幕

    private static void Fire(int id, string name)
    {
        Pending.Add(new Shot { Id = id, Name = name ?? "", Deg = _dirDeg });
    }

    private static void FireManual()
    {
        string s = (_manual ?? "").Trim();
        if (s.Length == 0) { Note("文本框是空的"); return; }

        int id = 0;
        string name = s;

        int c = s.IndexOf(':');
        if (c > 0)
        {
            int.TryParse(s.Substring(0, c).Trim(), out id);
            name = s.Substring(c + 1).Trim();
        }
        else if (int.TryParse(s, out id))
        {
            name = "";
        }
        else
        {
            id = 0;
        }

        if (id == 0)
        {
            // 按名称在全表里反查 id —— 弹幕是 (Id, StartAction) 双键，只给名称必须补一个 Id
            foreach (var r in _all)
                if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) { id = r.Id; break; }

            if (id == 0 && LastIdx != 0)
            {
                id = LastIdx;
                Note($"表里没有 \"{name}\" —— 借用最近捕获的 idx={LastIdx} 试放（可能失败）");
            }
        }

        if (id == 0) { Note($"解析不出 id（输入 \"{s}\"）"); return; }

        Fire(id, name);
        Note($"已排队 [{id}] \"{name}\"");
    }

    // ------------------------------------------------------------------ 载表（反射）

    private static void Reload()
    {
        _all.Clear();
        _shown.Clear();
        _loaded = false;

        object loader = FindLoader();
        if (loader == null) { Note("没找到静态属性 BulletConfigFixedPoint（表类型没生成？）"); return; }

        object all = PropOrNull(loader, "All");
        if (all == null) { Note("loader.All 为 null —— 表可能还没被游戏加载，进图后再试"); return; }

        int n = CountOf(all);
        for (int i = 0; i < n; i++)
        {
            object w = ItemOf(all, i);
            if (w == null) continue;
            var r = new Row
            {
                Id = IntOf(w, "Id"),
                Name = StrOf(w, "StartAction"),
                Res = StrOf(w, "LogicRes"),
            };
            _all.Add(r);
        }

        _loaded = true;
        Rebuild();
        Note($"表已载入: {_all.Count} 条");
    }

    private static object FindLoader()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string fn;
                try { fn = asm.FullName ?? ""; } catch { continue; }
                // 游戏类型一定不在这些里，跳过它们能省掉大量 GetTypes 开销和加载异常
                if (fn.StartsWith("UnityEngine") || fn.StartsWith("System") || fn.StartsWith("mscorlib")
                    || fn.StartsWith("netstandard") || fn.StartsWith("BepInEx") || fn.StartsWith("0Harmony")
                    || fn.StartsWith("HarmonyLib") || fn.StartsWith("Il2CppInterop") || fn.StartsWith("Unity."))
                    continue;

                Type[] ts;
                try { ts = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { ts = rtle.Types; }
                catch { continue; }
                if (ts == null) continue;

                foreach (var t in ts)
                {
                    if (t == null) continue;
                    PropertyInfo p;
                    try { p = t.GetProperty("BulletConfigFixedPoint", BindingFlags.Public | BindingFlags.Static); }
                    catch { continue; }
                    if (p == null) continue;

                    object v = null;
                    try { v = p.GetValue(null); } catch { }
                    if (v != null)
                    {
                        Plugin.Log?.LogInfo($"[弹幕实验台] 表持有者 = {t.FullName}");
                        return v;
                    }
                }
            }
        }
        catch (Exception e) { LogEx.Err("BulletLab.FindLoader", e); }
        return null;
    }

    private static object PropOrNull(object o, string name)
    {
        if (o == null) return null;
        try
        {
            var t = o.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(o);
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) return f.GetValue(o);
        }
        catch { }
        return null;
    }

    private static int CountOf(object list)
    {
        try
        {
            var v = PropOrNull(list, "Count");
            if (v != null) return Convert.ToInt32(v);
            var m = list.GetType().GetMethod("get_Count", Type.EmptyTypes);
            if (m != null) return Convert.ToInt32(m.Invoke(list, null));
        }
        catch { }
        return 0;
    }

    private static object ItemOf(object list, int i)
    {
        try
        {
            var t = list.GetType();
            var p = t.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(list, new object[] { i });
            var m = t.GetMethod("get_Item", new[] { typeof(int) });
            if (m != null) return m.Invoke(list, new object[] { i });
        }
        catch { }
        return null;
    }

    private static int IntOf(object o, string name)
    {
        try { var v = PropOrNull(o, name); return v == null ? 0 : Convert.ToInt32(v); }
        catch { return 0; }
    }

    private static string StrOf(object o, string name)
    {
        try { var v = PropOrNull(o, name); return v == null ? "" : v.ToString(); }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ 过滤

    private static void Rebuild()
    {
        _shown.Clear();
        string q = (_search ?? "").Trim();

        foreach (var r in _all)
        {
            if (_esOnly && !string.Equals(r.Res, "esbullet", StringComparison.OrdinalIgnoreCase)) continue;

            if (q.Length > 0)
            {
                bool hit = r.Id.ToString().IndexOf(q, StringComparison.Ordinal) >= 0
                        || (r.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                        || (r.Res ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hit) continue;
            }

            _shown.Add(r);
            if (_shown.Count >= 500) break;   // 只截显示，不动数据；截断了会在标题的计数上看出来
        }
    }
}
