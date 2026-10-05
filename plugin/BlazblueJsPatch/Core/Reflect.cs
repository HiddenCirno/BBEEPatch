using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// IL2CPP 反射的共用工具。
///
/// 这里的每一条都是踩出来的, 不是"看起来更保险":
///   · 成员一律按【名字子串】找, 并且不预设是字段还是属性 ——
///     Il2CppInterop 对私有字段可能生成属性, 写死 FieldInfo 会在解析处就炸。
///   · 遍历集合一律走 IEnumerable, **不要 `as IList`** ——
///     Il2CppReferenceArray&lt;T&gt; 只有泛型 IList&lt;T&gt;/IEnumerable&lt;T&gt;, 没实现非泛型 IList,
///     `as IList` 恒 null。而它后面通常跟着 `return 0`, 于是整段逻辑静默失效,
///     日志上看着像"没找到目标"(本项目栽过, 而且很难看出来)。
///   · 元素一律 TryCast —— 多态数组/GetComponentsInChildren 返回的是【基类包装】,
///     `as T` 恒 null 且不报错。
/// </summary>
internal static class Reflect
{
    internal const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static MemberInfo Member(Type t, string name, BindingFlags f)
        => (MemberInfo)t.GetProperty(name, f) ?? t.GetField(name, f);

    /// <summary>按名字片段找字段或属性 —— 不依赖 Il2CppInterop 的实际命名。</summary>
    internal static MemberInfo MemberLike(Type t, string substr, BindingFlags f)
    {
        foreach (var fi in t.GetFields(f))
            if (fi.Name.IndexOf(substr, StringComparison.OrdinalIgnoreCase) >= 0) return fi;
        foreach (var pi in t.GetProperties(f))
            if (pi.Name.IndexOf(substr, StringComparison.OrdinalIgnoreCase) >= 0) return pi;
        return null;
    }

    internal static object Read(object obj, MemberInfo mi)
    {
        if (obj == null || mi == null) return null;
        try
        {
            switch (mi)
            {
                case PropertyInfo p: return p.GetValue(obj);
                case FieldInfo f: return f.GetValue(obj);
            }
        }
        catch (Exception e) { WarnOnce(mi.Name, "读取", e); }
        return null;
    }

    internal static bool Write(object obj, MemberInfo mi, object val)
    {
        if (obj == null || mi == null) return false;
        try
        {
            switch (mi)
            {
                case PropertyInfo p: p.SetValue(obj, val); return true;
                case FieldInfo f: f.SetValue(obj, val); return true;
            }
        }
        catch (Exception e) { WarnOnce(mi.Name, "写入", e); }
        return false;
    }

    /// <summary>统一的"读失败"出口。每个成员只报一次真实原因 ——
    /// 静默吞异常会让日志显示成 "=?", 分不清"读不到"和"对象是坏的"。</summary>
    internal static void WarnOnce(string key, string what, Exception e)
        => LogEx.WarnOnce("reflect|" + what + "|" + key, $"[反射] {what} {key} 失败: {LogEx.Unwrap(e)}");

    /// <summary>多态包装 → 具体类型。失败返回 null(而不是抛)。</summary>
    internal static T Cast<T>(object o) where T : Il2CppObjectBase
    {
        try { return (o as Il2CppObjectBase)?.TryCast<T>(); }
        catch (Exception e) { WarnOnce(typeof(T).Name, "TryCast", e); return null; }
    }

    /// <summary>遍历任意 IL2CPP 集合(数组/List/Il2CppReferenceArray) —— 见类注释。</summary>
    internal static IEnumerable<object> Items(object collection)
    {
        if (collection == null) yield break;
        if (collection is IEnumerable en)
            foreach (var o in en) yield return o;
    }

    internal static string Name(UnityEngine.Object o)
    {
        try { return o == null ? "" : (o.name ?? ""); } catch { return ""; }
    }

    /// <summary>去掉 "(Clone)" 之类的实例后缀, 让同名实例归并成一条日志。</summary>
    internal static string Normalize(string n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        int i = n.IndexOf("(Clone)", StringComparison.Ordinal);
        return (i >= 0 ? n.Substring(0, i) : n).Trim();
    }

    internal static string Fmt(object c)
        => c is Color x ? $"({x.r:0.###},{x.g:0.###},{x.b:0.###},{x.a:0.###})" : (c?.ToString() ?? "null");

    /// <summary>
    /// 原生对象**真实的类名**（`il2cpp_object_get_class` + `il2cpp_class_get_name`）。
    ///
    /// 为什么必须能拿到它：本项目的数组里经常是**多态**的（`InterpolatorBase[]` 里混着好几种插值器）。
    /// 一旦按错误的具体类型去读字段，读出来的是**别的类型的位**（指针被当 float 是最容易认的），
    /// 拿到垃圾"指针"再解引用就是一次不可 catch 的原生崩溃。
    /// 打印真实类名，是把这类问题**一眼看穿**的最短路径 —— 比一个个 TryCast 猜快得多。
    /// </summary>
    internal static string KlassName(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return "(null)";
        try
        {
            IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(obj);
            if (klass == IntPtr.Zero) return "(无 klass)";
            IntPtr name = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(klass);
            return name == IntPtr.Zero ? "(无名)" : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(name);
        }
        catch (Exception e) { return "(类名读取失败:" + e.Message + ")"; }
    }
}
