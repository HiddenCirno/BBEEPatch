using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// `GUI.Window` / `GUILayout.Window` 的忠实重建。
///
/// 为什么不照抄 Unity 的实现
/// ────────────────────────
/// Unity 的实际调用链(反编译 BepInEx/unity-libs/UnityEngine.IMGUIModule.dll, 2022.3.62, 与本作同版本):
///
///   GUILayout.Window(id, rect, func, title, style, options)
///       └─ GUILayout.DoWindow(...)
///            ├─ new LayoutedWindow(func, rect, title, options, style)
///            └─ GUI.Window(id, rect, layoutedWindow.DoWindow, title, style)
///                 └─ GUI.DoWindow(..., forceRectOnLayout: true)
///                      └─ Internal_DoWindow(id, s_OriginalID, rect, func, title, style, skin, forceRect)   ← native
///                           └─ GUI.CallWindowDelegate(func, id, instanceID, skin, forceRect, w, h, style)
///
/// 本作被 strip 掉的只有 `Internal_DoWindow`(native) 和 `GUI.Window` 本身;
/// 但 **`GUI.CallWindowDelegate` 是活的**(dump.cs:1190956), 而窗口的 ID 作用域切换、
/// 布局缓存建立、回调调用这三件核心事全在它里面:
///
///   CallWindowDelegate(func, id, instanceID, skin, forceRect, w, h, style):
///       GUILayoutUtility.SelectIDList(id, isWindow: true);          // ← 窗口专属的控制 ID 列表
///       if (Layout) GUILayoutUtility.BeginWindow(id, style, forceRect ? {Width(w),Height(h)} : null);
///       else        GUILayoutUtility.BeginWindow(id, GUIStyle.none, null);
///       func?.Invoke(id);
///       if (Layout) GUILayoutUtility.Layout();
///
/// 而 `LayoutedWindow.DoWindow` 负责把该窗口的 topLevel 摆到正确位置:
///
///   var topLevel = GUILayoutUtility.current.topLevel;
///   if (Layout) { topLevel.resetCoords = true; topLevel.rect = m_ScreenRect;
///                 if (m_Options != null) topLevel.ApplyOptions(m_Options);
///                 topLevel.isWindow = true; topLevel.windowID = windowID; topLevel.style = m_Style; }
///   else        { topLevel.ResetCursor(); }
///   m_Func(windowID);
///
/// 上一版用 `GUILayout.BeginArea` 顶替, **完全没有做 SelectIDList 这一步** ——
/// 于是 CM 窗口内的控件和窗口外的控件挤在同一个控制 ID 列表里互相顶号,
/// 表现就是"点谁都不对/点在 A 上却触发了 B"。这就是命中判定错位的真正病根。
///
/// 这里按上面两段源码逐行复刻, 只用【已验证存活】的 API, 改动最小、语义最接近原版。
/// </summary>
internal static class WindowEmu
{
    // ------------------------------------------------------------------ 反射句柄

    private static bool _resolved;
    private static MethodInfo _miBeginWindow, _miLayout, _miApplyOptions, _miResetCursor;
    private static MemberInfo _fiCurrent, _fiTopLevel, _fiResetCoords, _fiRect, _fiIsWindow,
                              _fiWindowId, _fiStyle, _fiEntries;
    private static MemberInfo _fiMinW, _fiMaxW, _fiMinH, _fiMaxH;

    private static string _fail;

    /// <summary>返回 null 表示原语齐备; 否则是缺失说明(用于日志/回退)。</summary>
    internal static string Resolve()
    {
        if (_resolved) return _fail;
        _resolved = true;
        try
        {
            var util = Find("UnityEngine.GUILayoutUtility");
            var cache = Find("UnityEngine.GUILayoutUtility+LayoutCache");
            var group = Find("UnityEngine.GUILayoutGroup");
            var entry = Find("UnityEngine.GUILayoutEntry");
            if (util == null || cache == null || group == null || entry == null)
            { _fail = $"类型缺失 util={util != null} cache={cache != null} group={group != null} entry={entry != null}"; return _fail; }

            _miBeginWindow = util.GetMethod("BeginWindow", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            _miLayout = util.GetMethod("Layout", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
            _miApplyOptions = group.GetMethod("ApplyOptions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _miResetCursor = group.GetMethod("ResetCursor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            _fiCurrent = Member(util, "current");
            _fiTopLevel = Member(cache, "topLevel");
            _fiResetCoords = Member(group, "resetCoords");
            _fiIsWindow = Member(group, "isWindow");
            _fiWindowId = Member(group, "windowID");
            _fiRect = Member(entry, "rect");
            _fiStyle = Member(entry, "style");
            _fiEntries = Member(group, "entries");
            _fiMinW = Member(entry, "minWidth");
            _fiMaxW = Member(entry, "maxWidth");
            _fiMinH = Member(entry, "minHeight");
            _fiMaxH = Member(entry, "maxHeight");

            var missing = new List<string>();
            if (_miBeginWindow == null) missing.Add("BeginWindow");
            if (_miLayout == null) missing.Add("Layout");
            if (_miApplyOptions == null) missing.Add("ApplyOptions");
            if (_miResetCursor == null) missing.Add("ResetCursor");
            if (_fiCurrent == null) missing.Add("current");
            if (_fiTopLevel == null) missing.Add("topLevel");
            if (_fiResetCoords == null) missing.Add("resetCoords");
            if (_fiIsWindow == null) missing.Add("isWindow");
            if (_fiWindowId == null) missing.Add("windowID");
            if (_fiRect == null) missing.Add("rect");
            if (_fiStyle == null) missing.Add("style");
            if (_fiEntries == null) missing.Add("entries");
            if (_fiMinW == null) missing.Add("minWidth");
            if (_fiMaxW == null) missing.Add("maxWidth");
            if (_fiMinH == null) missing.Add("minHeight");
            if (_fiMaxH == null) missing.Add("maxHeight");
            if (missing.Count > 0) _fail = "缺: " + string.Join(", ", missing);
        }
        catch (Exception e) { _fail = e.Message; }
        return _fail;
    }

    internal static bool Available => Resolve() == null;

    /// <summary>按全名找类型 —— 不枚举程序集, 免得 interop 里那堆非法类型刷屏。</summary>
    private static Type Find(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { var t = asm.GetType(fullName, false); if (t != null) return t; }
            catch { }
        }
        return null;
    }

    private static MemberInfo Member(Type t, string name) =>
        (MemberInfo)t.GetProperty(name, BindingFlags.Instance | BindingFlags.Static |
                                       BindingFlags.Public | BindingFlags.NonPublic)
        ?? t.GetField(name, BindingFlags.Instance | BindingFlags.Static |
                           BindingFlags.Public | BindingFlags.NonPublic);

    private static object Get(MemberInfo m, object obj)
    {
        try
        {
            switch (m) { case PropertyInfo p: return p.GetValue(obj); case FieldInfo f: return f.GetValue(obj); }
        }
        catch { }
        return null;
    }

    private static bool Set(MemberInfo m, object obj, object v)
    {
        try
        {
            switch (m) { case PropertyInfo p: p.SetValue(obj, v); return true; case FieldInfo f: f.SetValue(obj, v); return true; }
        }
        catch (Exception e) { Warn($"写 {m.Name}", e); }
        return false;
    }

    private static void Warn(string what, Exception e)
    {
        if (!_warned.Add(what)) return;
        Plugin.Log?.LogWarning($"[WindowEmu] {what} 失败: " +
                               $"{(e as TargetInvocationException)?.InnerException?.Message ?? e.Message}");
    }
    private static readonly HashSet<string> _warned = new HashSet<string>();

    // ------------------------------------------------------------------ 主逻辑

    /// <summary>复刻 CallWindowDelegate + LayoutedWindow。返回窗口矩形。</summary>
    private static readonly HashSet<string> _steps = new HashSet<string>();

    /// <summary>每走到一个新步骤就记一条 —— 崩了之后日志里最后一条就是崩在哪一步。</summary>
    private static void Step(string what)
    {
        if (_steps.Count >= 40 || !_steps.Add(what)) return;
        Plugin.Log?.LogInfo($"[WindowEmu:step] {what}");
    }


    // ------------------------------------------------------------------ 矩形探针
    //
    // 命中判定用的是 GUILayoutEntry.rect(布局期算好、存在条目里), 绘制读的也是它。
    // 所以"画得出来但点不动"只可能是两种:
    //   (a) rect 本身就是错的/全零  -> 那画的位置也该是错的
    //   (b) rect 是对的, 但鼠标位置和它不在同一坐标系(或者鼠标压根没到这儿)
    // 这两种表相一样, 修法完全不同 —— 打出来看。
    private static int _rectLog;

    private static string FmtRect(object r) =>
        r is Rect x ? $"({x.x:F0},{x.y:F0} {x.width:F0}x{x.height:F0})" : "?";

    private static void LogRects(object top, string where)
    {
        if (_rectLog >= 24) return;
        _rectLog++;
        string info;
        try
        {
            // entries 声明在 GUILayoutGroup 上; 若 top 拿到的是基类 GUILayoutEntry 包装,
            // 直接反射读会抛 "Object does not match target type"(这个坑本会话踩过三次了)。
            // 所以按运行时类型现查一次。
            var list = Get(_fiEntries, top) as System.Collections.IList;
            if (list == null)
            {
                try
                {
                    var m = top.GetType().GetProperty("entries",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? (MemberInfo)top.GetType().GetField("entries",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m != null) list = Get(m, top) as System.Collections.IList;
                }
                catch { }
            }
            int n = list?.Count ?? -1;
            string head = "";
            for (int i = 0; i < 3 && list != null && i < n; i++)
                head += $" [{i}]={FmtRect(Get(_fiRect, list[i]))}";
            string mp = "?";
            try { var e = Event.current; mp = e == null ? "null" : $"{e.type}@{e.mousePosition}"; } catch { }
            info = $"top={FmtRect(Get(_fiRect, top))} entries={n}{head} mouse={mp}";
        }
        catch (Exception ex) { info = "读取失败: " + ex.Message; }
        Plugin.Log?.LogInfo($"[WindowEmu:rect] {where} {info}");
    }

    internal static Rect DoWindow(int id, Rect rect, object fn, GUIStyle style,
                                  GUIContent title, GUILayoutOption[] opts)
    {
        var evt = Event.current;
        bool isLayout = evt != null && evt.type == EventType.Layout;
        if (evt != null && (evt.type == EventType.MouseDown || evt.type == EventType.MouseUp))
            LogRects(CurrentTopLevel(), $"鼠标事件({evt.type})");

        GUISkin prevSkin = null;
        try { prevSkin = GUI.skin; } catch { }

        try
        {
            // ---- CallWindowDelegate 开头: 切到该窗口专属的控制 ID / 布局缓存 ----
            //   ⚠ 这一步是上一版缺失的关键。BeginWindow 内部也会 SelectIDList,
            //     但 Unity 是先单独选一次(它会新建/取出该 window 的 LayoutCache)。
            Step("1 SelectIDList");
            InvokeUtil("SelectIDList", true, id, true);
            Step("2 SelectIDList ok");

            // ---- CallWindowDelegate: Layout 用真 style, 否则一律 GUIStyle.none ----
            if (_miBeginWindow != null)
            {
                // style 为 null 时用 GUIStyle.none; 两者都拿不到就跳过 BeginWindow
                var useStyle = isLayout ? style : SafeNoneStyle();
                if (useStyle == null) useStyle = SafeNoneStyle();
                if (useStyle != null)
                {
                    Step($"3 BeginWindow layout={isLayout}");
                    try { _miBeginWindow.Invoke(null, new object[] { id, useStyle, null }); }
                    catch (Exception e) { Warn("BeginWindow", e); }
                    Step("4 BeginWindow ok");
                }
            }

            // ---- LayoutedWindow.DoWindow: 把本窗口的 topLevel 摆到窗口矩形 ----
            var top = CurrentTopLevel();
            if (top != null)
            {
                if (isLayout)
                {
                    Step("5 config topLevel");
                    // ★★ resetCoords 必须保持 false —— 又一个"照抄 LayoutedWindow 就错"的地方。
                    //
                    //   GUILayoutGroup.SetHorizontal / SetVertical 里:
                    //       if (resetCoords) { x = 0f; }   /   if (resetCoords) { y = 0f; }
                    //   它唯一的作用就是把子控件的起点归零。
                    //
                    //   整个 IMGUI 模块里 resetCoords 只有这两处【读】, 没有任何一处【写 false】——
                    //   也就是说在原版里它是靠【原生窗口侧】清掉的。而我们走的是纯托管分支,
                    //   没人清, 于是它一直是 true, 子控件全被钉在 x=0/y=0 ——
                    //   实测表现就是"控件画在面板左侧的空处"。
                    //
                    //   我们要的语义正好相反: 从 topLevel.rect(窗口矩形) 开始排。
                    Set(_fiResetCoords, top, false);
                    Set(_fiRect, top, rect);
                    if (opts != null && _miApplyOptions != null)
                    {
                        try { _miApplyOptions.Invoke(top, new object[] { opts }); }
                        catch (Exception e) { Warn("ApplyOptions", e); }
                    }
                    // ★★ 这里【必须】是 false —— 不能照抄 LayoutedWindow 的 true。
                    //
                    //   GUILayoutUtility.LayoutSingleGroup 里:
                    //     if (!i.isWindow) { CalcWidth / SetHorizontal / CalcHeight / SetVertical }  ← 纯托管
                    //     else { Internal_GetWindowRect(windowID); ... Internal_MoveWindow(windowID, ...) } ← 原生窗口系统
                    //
                    //   isWindow=true 会去调原生窗口系统, 而这个窗口【从来没注册过】——
                    //   注册靠 BeginWindows / Internal_BeginWindows, 那套在本作里被 strip(计数为 0)。
                    //   实测死因就是这里: 前 8 步全过, 第 9 步 Layout() 直接访问违例。
                    //
                    //   而 isWindow=false 的分支用的是 i.rect.x / i.rect.y ——
                    //   正是我们上一步亲手设好的窗口屏幕矩形, 语义完全对得上, 且全程不碰原生。
                    //
                    //   安全性已核实: isWindow / windowID 在整个 GUILayoutGroup 里都只声明不读;
                    //   GUILayoutUtility 里也只在 LayoutSingleGroup 读 isWindow 这一处 —— 改它无副作用。
                    Set(_fiIsWindow, top, false);
                    Step("6 topLevel ok");
                    Set(_fiWindowId, top, id);
                    if (style != null) Set(_fiStyle, top, style);
                }
                else if (_miResetCursor != null)
                {
                    try { _miResetCursor.Invoke(top, null); }
                    catch (Exception e) { Warn("ResetCursor", e); }
                }
            }

            // ---- 调用用户窗口函数 ----
            Step("7 invoke fn");
            InvokeWindowFn(fn, id);
            Step("8 invoke ok");

            // ---- CallWindowDelegate 收尾 ----
            if (isLayout)
            {
                // ★★ 钉住 min/max, 否则宽度会被夹成 0。
                //   LayoutSingleGroup 的非窗口分支在 CalcWidth() 【之前】就把 i.minWidth/i.maxWidth
                //   存进了局部变量, 之后用 Mathf.Clamp(算出的宽度, 那对旧值) 定宽。
                //   而 BeginWindow 每次 Layout 都 new GUILayoutGroup(), 旧值是初始的 0 ——
                //   Clamp(x, 0, 0) 恒为 0, 于是整个窗口宽度归零, 子控件全被压扁。
                //   把 min/max 先设成窗口矩形尺寸, 这对局部变量就捕获到正确值, Clamp 变成恒等映射。
                var w = Math.Max(1f, rect.width);
                var h = Math.Max(1f, rect.height);
                Set(_fiMinW, top, w); Set(_fiMaxW, top, w);
                Set(_fiMinH, top, h); Set(_fiMaxH, top, h);

                Step("9 Layout()");
                InvokeUtil("Layout", false, null, null);
                Step("10 Layout ok");
                LogRects(CurrentTopLevel(), "Layout后");
            }
        }
        catch (Exception e)
        {
            Plugin.Log?.LogError($"[WindowEmu] id={id} 异常: {e}");
        }
        finally
        {
            try { if (prevSkin != null) GUI.skin = prevSkin; } catch { }
        }

        return rect;
    }

    private static void InvokeUtil(string name, bool wantReturn, object a1, object a2)
    {
        try
        {
            var util = _miBeginWindow?.DeclaringType;
            var m = name == "Layout" ? _miLayout : util?.GetMethod(name,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null) return;
            var args = m.GetParameters().Length switch
            {
                0 => null,
                1 => new[] { a1 },
                _ => new[] { a1, a2 },
            };
            m.Invoke(null, args);
        }
        catch (Exception e) { Warn(name, e); }
    }

    private static GUIStyle SafeNoneStyle()
    {
        try { return GUIStyle.none; } catch { return null; }
    }

    private static object CurrentTopLevel()
    {
        try
        {
            var cache = Get(_fiCurrent, null);
            if (cache == null) return null;
            return Get(_fiTopLevel, cache);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ 调用窗口函数
    //
    // 坑(踩过): Il2CppInterop 生成的委托类型 (UnityEngine.GUI+WindowFunction) 并不是
    // System.Delegate —— 它是 Il2CppObjectBase 的派生类, 带一个 virtual Invoke(Int32)。
    // 所以 `fn is Delegate` 恒为 false, DynamicInvoke 走不通, 必须反射调 Invoke。

    private static int _noDelegateLogged;

    internal static void InvokeWindowFn(object fn, int id)
    {
        if (fn == null) return;
        try
        {
            if (fn is Delegate d) { d.DynamicInvoke(new object[] { id }); return; }

            var t = fn.GetType();
            var mi = t.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance,
                                 null, new[] { typeof(int) }, null)
                     ?? t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                          .FirstOrDefault(m => m.Name == "Invoke" && m.GetParameters().Length == 1);
            if (mi == null)
            {
                if (_noDelegateLogged++ < 3)
                    Plugin.Log?.LogError($"[WindowEmu] {t.FullName} 上找不到 Invoke(Int32), 窗口内容无法绘制");
                return;
            }
            mi.Invoke(fn, new object[] { id });
        }
        catch (Exception e)
        {
            var real = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
            Plugin.Log?.LogError($"[WindowEmu] 窗口 id={id} 的内容函数抛异常: {real}");
        }
    }
}
