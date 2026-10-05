using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 特效解剖 —— 把"某个特效到底由什么构成"从猜变成读。
///
/// 为什么不用 UnityExplorer
/// ────────────────────────
/// 装上了、也真的加载了（日志有 `Loading [UnityExplorer 4.9.0]`），但它的 UI 永远出不来：
///   UniverseLib 要 `Il2CppStructArray&lt;byte&gt;` 去调 `AssetBundle.LoadFromMemory`，
///   而 `Il2CppClassPointerStore&lt;byte&gt;` 的静态构造卡在
///   `AppDomain.GetAssemblies().Single(it =&gt; it.GetName().Name == "Il2Cppmscorlib")`
///   —— 抛 "Sequence contains more than one matching element"，即内存里有**两份同名**程序集。
///   那是共享 interop 层的毛病，任何 `byte[] → il2cpp` 的路径都会踩，不是 UE 能绕开的。
///
/// 但我们要的东西本来就不需要 GUI。metadata 里这些是**公开实例方法**（dump.cs 已核对）：
///     Shader.GetPropertyCount/GetPropertyName/GetPropertyNameId/GetPropertyType
///     Material.GetColor/GetFloat/GetVector/GetInt/GetTexture/HasProperty
/// ⇒ 运行时可把材质上**每一个属性连同当前值**枚举出来，比 UE 的检视器还全。
///
/// ⚠⚠ 两个已经踩过的坑（第一版就是这么把游戏卡爆的）
/// ─────────────────────────────────────────────
/// 1. **绝不允许"按实例"排采样**。第一版是"每生成一颗子弹排一次"，而 `addbuffbullet`
///    是被批量生成的 ⇒ 同一帧几百个采样到期 ⇒ 日志刷屏 + 卡爆。
///    现在按**名字**去重（每种名字一辈子只采一次），并且
///    **每帧最多采 1 个对象、总共最多 <see cref="MaxDumps"/> 次**，超了会明说（不静默丢）。
/// 2. **`GetComponents(Il2CppType.From(typeof(Component)))` 的 `GetType().Name` 恒等于 "Component"**
///    —— 返回的是**声明类型**的包装，拿不到真实类型（第一版打出来一排 "Component"）。
///    所以组件清单改成**按已知类型逐个探**（TryCast），这也正是我们真正关心的那些组件。
///
/// ⚠ 采样必须【延迟】：子弹刚 createBulletImp 出来那一瞬间子物体还没挂上
///   （日志 `粒子=0 渲染器=2` 就是那一瞬），材质也还是没初始化的占位图。
/// </summary>
internal static class FxAnatomy
{
    internal static ConfigEntry<bool> CfgEnabled;
    internal static ConfigEntry<string> CfgKeyword;

    private const int MaxNames = 4;      // 最多解剖 4 种不同名字的对象
    private const int MaxDumps = 12;     // 本次启动最多输出 12 次解剖（硬上限）
    private const int MaxProps = 64;     // 单个材质最多列 64 条（超了会明说，不静默截断）
    private const int MaxDepth = 3;

    private sealed class Job
    {
        internal GameObject Go;
        internal float Due;              // Time.time
        internal int Left;               // 还要采几次
        internal string Name;
    }

    private static readonly List<Job> _jobs = new List<Job>();
    private static readonly HashSet<string> _planned = new HashSet<string>();
    private static int _dumps;

    // ------------------------------------------------------------------ 挂载

    public static int Apply(Harmony harmony)
    {
        var bm = AccessTools.TypeByName("GamePlay.BulletMgr");
        if (bm == null) { Plugin.Log?.LogWarning("  [特效解剖] 找不到 GamePlay.BulletMgr"); return 0; }

        int n = 0;
        var create = AccessTools.Method(bm, "createBulletImp");
        if (create == null) Plugin.Log?.LogWarning("  [特效解剖] BulletMgr.createBulletImp 不可挂");
        else
        {
            harmony.Patch(create, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(FxAnatomy), nameof(BulletPostfix))));
            n++;
        }

        // 借用现成的每帧心跳（ActionJournal / AhWing 已经在借同一个）。
        var val = AccessTools.Method(bm, "ValidateBullets");
        if (val != null)
        {
            harmony.Patch(val, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(FxAnatomy), nameof(Tick))));
            n++;
        }
        return n;
    }

    /// <summary>形参名与 dump.cs 一致（`__result` 是 BulletObj）。</summary>
    public static void BulletPostfix(object __result)
    {
        try
        {
            if (CfgEnabled?.Value != true || __result == null) return;
            if (_dumps >= MaxDumps) return;

            var b = Reflect.Cast<GamePlay.BulletObj>(__result);
            var go = b?.gameObject;                       // ActorBase.gameObject 是公开属性
            if (go == null) return;

            string nm = Reflect.Name(go);
            string kw = (CfgKeyword?.Value ?? "esbullet").Trim();
            if (kw.Length > 0 && nm.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) return;

            // ★ 按【名字】去重：同一个名字一辈子只排一次。
            //   第一版按实例排，被 addbuffbullet 的批量生成打爆过。
            if (!_planned.Add(nm)) return;
            if (_planned.Count > MaxNames)
            {
                _planned.Remove(nm);
                Plugin.Log?.LogInfo($"[特效解剖] 已达上限 {MaxNames} 种, 跳过 \"{nm}\"" +
                                    $"（这是上限, 不是没生成 —— 改 FxAnatomyKeyword 缩小范围）");
                return;
            }

            _jobs.Add(new Job { Go = go, Due = Time.time + 0.3f, Left = 2, Name = nm });
            Plugin.Log?.LogInfo($"[特效解剖] 已排入样本 \"{nm}\"（已排 {_planned.Count}/{MaxNames} 种, " +
                                $"上限 {MaxNames}）");
        }
        catch (Exception e) { Reflect.WarnOnce("fxanatomy|bullet", "特效解剖排程", e); }
    }

    /// <summary>每帧心跳：到点了就采一次。**每帧最多一个**，避免一次到期一堆。</summary>
    public static void Tick()
    {
        if (_jobs.Count == 0) return;
        try
        {
            for (int i = 0; i < _jobs.Count; i++)
            {
                var j = _jobs[i];
                if (j.Go == null) { _jobs.RemoveAt(i--); continue; }   // 已销毁/回池
                if (Time.time < j.Due) continue;

                if (_dumps >= MaxDumps)
                {
                    _jobs.Clear();
                    Plugin.Log?.LogInfo($"[特效解剖] 已达总上限 {MaxDumps} 次, 停止采样（要再看请重开一局）");
                    return;
                }

                _dumps++;
                Dump(j.Go, j.Name);

                j.Left--;
                j.Due = Time.time + 0.7f;
                if (j.Left <= 0) _jobs.RemoveAt(i);
                return;                                   // ★ 每帧只采一个
            }
        }
        catch (Exception e) { Reflect.WarnOnce("fxanatomy|tick", "特效解剖", e); }
    }

    // ------------------------------------------------------------------ 解剖

    private static void Dump(GameObject root, string name)
    {
        P($"[特效解剖] ==== #{_dumps} \"{name}\" ==== " + Safe(() => TintBrush.CarrierReport(root)));

        try
        {
            P("[特效解剖] 层级:");
            Walk(root.transform, 1, "");
        }
        catch (Exception e) { P("[特效解剖] 层级读取失败: " + e.Message); }

        try
        {
            var seen = new HashSet<IntPtr>();
            var ty = Il2CppType.From(typeof(Renderer));
            foreach (var c in Reflect.Items(root.GetComponentsInChildren(ty, true)))
            {
                var rt = Reflect.Cast<Renderer>(c);
                if (rt == null) continue;
                Material mat = null;
                try { mat = rt.sharedMaterial; } catch { }
                if (mat == null)
                {
                    P($"[特效解剖] 渲染器 \"{Reflect.Name(rt.gameObject)}\" : {RtName(rt)} 材质=null");
                    continue;
                }
                if (!seen.Add(SafePtr(mat))) continue;      // 同一个材质只列一次
                DumpMaterial(mat, Reflect.Name(rt.gameObject) + ":" + RtName(rt));
            }
        }
        catch (Exception e) { P("[特效解剖] 渲染器读取失败: " + e.Message); }

        try
        {
            var tps = Il2CppType.From(typeof(ParticleSystem));
            foreach (var c in Reflect.Items(root.GetComponentsInChildren(tps, true)))
            {
                var ps = Reflect.Cast<ParticleSystem>(c);
                if (ps == null) continue;
                string on = Reflect.Name(ps.gameObject);
                try
                {
                    var g = ps.main.startColor;
                    int mode = (int)g.m_Mode;
                    string extra = mode == 0 ? $"max={Fmt(g.m_ColorMax)}"
                                 : (mode == 2 || mode == 4) ? $"min={Fmt(g.m_ColorMin)} max={Fmt(g.m_ColorMax)}"
                                 : "渐变(keys)";
                    P($"[特效解剖] 粒子 \"{on}\" startColor mode={mode} {extra}");
                }
                catch (Exception e) { P($"[特效解剖] 粒子 \"{on}\" 读 startColor 失败: {e.Message}"); }
            }
        }
        catch (Exception e) { P("[特效解剖] 粒子读取失败: " + e.Message); }
    }

    /// <summary>
    /// 递归打印层级：名字 + active + **按已知类型探出来的**组件清单。
    ///
    /// ⚠ 不能枚举 `Component` 再看 `GetType().Name` —— 那样每一条都只会打 "Component"
    ///   （返回的是声明类型的包装）。所以这里逐个 TryCast 探测真正关心的那些组件。
    /// </summary>
    private static void Walk(Transform t, int depth, string indent)
    {
        if (t == null || depth > MaxDepth) return;
        int n;
        try { n = t.childCount; } catch { return; }
        for (int i = 0; i < n; i++)
        {
            Transform ch = null;
            try { ch = t.GetChild(i); } catch { }
            if (ch == null) continue;
            var go = ch.gameObject;

            string act = "?";
            try { act = go.activeSelf ? "" : " [未激活]"; } catch { }
            P($"[特效解剖] {indent}{Reflect.Name(go)}{act} : {Parts(go)}");
            Walk(ch, depth + 1, indent + "  ");
        }
    }

    /// <summary>真正关心的组件清单（按类型探，拿得到真实子类型名）。</summary>
    private static string Parts(GameObject go)
    {
        var list = new List<string>();
        try
        {
            if (go.GetComponent<ParticleSystem>() != null) list.Add("ParticleSystem");
            var r = go.GetComponent<Renderer>();
            if (r != null) list.Add(RtName(r));
            if (go.GetComponent<MeshFilter>() != null) list.Add("MeshFilter");
            // Animator / AudioSource 不探: 它们的 interop 程序集本工程没引用,
            // 而对"颜色写在哪"这件事也没有帮助。
            if (go.GetComponent<NOAH.VFX.MaterialTinter>() != null) list.Add("MaterialTinter");
            if (go.GetComponent<NOAH.VFX.MaterialTinterProxy>() != null) list.Add("MaterialTinterProxy");
            var fx = go.GetComponent<NOAH.VFX.VFXEffectBase>();
            if (fx != null) list.Add("VFXEffect:" + fx.GetType().Name);
        }
        catch (Exception e) { return "组件探测失败:" + e.Message; }
        return list.Count == 0 ? "(无我们关心的组件)" : string.Join(" ", list);
    }

    private static string RtName(Renderer r)
    {
        try
        {
            if (r.TryCast<TrailRenderer>() != null) return "TrailRenderer";
            if (r.TryCast<LineRenderer>() != null) return "LineRenderer";
            if (r.TryCast<MeshRenderer>() != null) return "MeshRenderer";
            if (r.TryCast<SkinnedMeshRenderer>() != null) return "SkinnedMeshRenderer";
            if (r.TryCast<ParticleSystemRenderer>() != null) return "ParticleSystemRenderer";
            if (r.TryCast<SpriteRenderer>() != null) return "SpriteRenderer";
        }
        catch { }
        return "Renderer";
    }

    /// <summary>
    /// 把一个材质的**全部**着色器属性连同当前值倒出来 —— 这是本工具的核心。
    /// 用的是 `Shader.GetPropertyCount/GetPropertyName/GetPropertyNameId/GetPropertyType`，
    /// 所以不依赖任何"候选属性名白名单"（白名单正是之前查不出尾焰的原因）。
    /// </summary>
    internal static void DumpMaterial(Material mat, string who)
    {
        string mname = "?", sname = "?";
        try { mname = mat.name; } catch { }
        Shader sh = null;
        try { sh = mat.shader; } catch { }
        int count = 0;
        try { if (sh != null) { sname = sh.name; count = sh.GetPropertyCount(); } }
        catch (Exception e) { sname = "读 shader 失败:" + e.Message; }

        P($"[特效解剖] 材质 \"{mname}\" shader=\"{sname}\" 属性数={count}  (用于 {who})");
        if (sh == null || count <= 0) return;

        int shown = 0;
        for (int i = 0; i < count && shown < MaxProps; i++)
        {
            try
            {
                string pn = sh.GetPropertyName(i);
                int id = sh.GetPropertyNameId(i);
                int ty = (int)sh.GetPropertyType(i);
                bool has = true;
                try { has = mat.HasProperty(id); } catch { }
                string val;
                switch (ty)
                {
                    case 0: val = "Color=" + Fmt(mat.GetColor(id)); break;                  // Color
                    case 1: val = "Vector=" + Fmt(mat.GetVector(id)); break;                // Vector
                    case 2: val = "Float=" + mat.GetFloat(id).ToString("0.####"); break;    // Float
                    case 3: val = "Range=" + mat.GetFloat(id).ToString("0.####"); break;    // Range
                    case 4:                                                                // Texture
                        var tx = mat.GetTexture(id);
                        val = "Texture=" + (tx == null ? "null" : SafeName(tx));
                        break;
                    case 5: val = "Int=" + mat.GetInt(id); break;                          // Int
                    default: val = "type" + ty; break;
                }
                P($"[特效解剖]     {pn} : {TypeName(ty)} {val}{(has ? "" : "  [材质上没有]")}");
                shown++;
            }
            catch (Exception e) { P($"[特效解剖]     属性 #{i} 读取失败: {e.Message}"); }
        }
        if (count > shown)
            P($"[特效解剖]     …… 还有 {count - shown} 条未列出（单材质上限 {MaxProps}，**这是截断，不是没有**）");
    }

    private static string TypeName(int t) => t switch
    {
        0 => "Color", 1 => "Vector", 2 => "Float", 3 => "Range", 4 => "Texture", 5 => "Int", _ => "?"
    };

    // ------------------------------------------------------------------ 小工具

    private static void P(string s) => Plugin.Log?.LogInfo(s);

    private static string Fmt(Color c) => $"({c.r:0.###},{c.g:0.###},{c.b:0.###},{c.a:0.###})";
    private static string Fmt(Vector4 v) => $"({v.x:0.###},{v.y:0.###},{v.z:0.###},{v.w:0.###})";

    private static string SafeName(UnityEngine.Object o)
    {
        try { return o.name ?? "?"; } catch { return "?"; }
    }

    private static IntPtr SafePtr(Il2CppObjectBase o)
    {
        try { return o.Pointer; } catch { return IntPtr.Zero; }
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch (Exception e) { return "读取失败:" + e.Message; }
    }
}
