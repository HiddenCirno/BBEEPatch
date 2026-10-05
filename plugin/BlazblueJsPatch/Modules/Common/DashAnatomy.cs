using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using GamePlay;   // BattleBase.Cur.PlayerSelf
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 冲刺解剖 —— 一个动作按下去，把**它拉起的全部对象递归展开**倒进日志。
///
/// 为什么要它（用户原话："通过类似特效解剖的方式，把所有的冲刺动作产生的对象递归展开，
/// 这样我们就能直观的知道到底有什么了"）
/// ──────────────────────────────────────────────────────────────────
/// 前面在冲刺换色上耗了一晚上，根因是"**对象→目标**"这层映射全靠推断：
///   把 `_AddColor` 当成电弧（其实是角色描边）→ 把影子当成电弧 → 把 `es_dash_01` 当成 MP 冲刺的特效
///   （其实那只是空中冲刺；MP 冲刺**一只特效都不拉，只发弹幕**）。
/// 每次推断都基于"我看到的某一行日志"，而**没人把整个现场摊开看过**。
/// UE 又用不了（见 FxAnatomy 的注释：`Il2CppClassPointerStore<byte>` 静态构造抛异常）。
/// ⇒ 那就自己造这个"摊开"：出生口挂钩 → 延迟采样 → **递归 Walk** 整棵树，
///    每个节点列出组件、每个渲染器的材质与**着色属性当前值**、每个 Tinter 的插值器逐条（含真实类名）。
///
/// 覆盖两个出生口（实测缺一不可）：
///   · **特效**：`ActorEffectMgr.createVisualEffect` —— `dash` 拉 4 只（es_attackAir_02/es_dash_01/02/es_dodge_02）
///   · **弹幕**：`BulletMgr.createBulletImp` —— 所有冲刺动作都发 `esbullet`(idx 10340101)，
///     而 `dashSkill`/`dashSkill2`（消耗 MP 的那个）**只有弹幕、没有特效**
///
/// ⚠ 三条硬性纪律（都是本项目用血换来的）：
///   1. **按"动作|根对象名"去重**，绝不按实例排 —— FxAnatomy 第一版按实例排被 `addbuffbullet`
///      的批量生成打爆过（同帧几百个到期 ⇒ 刷屏 + 卡爆）。
///   2. **延迟采样 + 每帧最多一个** —— 出生那一瞬子物体还没挂上、材质还是占位图。
///   3. **不静默截断** —— 深度/条数/总次数任何一处封顶都要明说。
/// </summary>
internal static class DashAnatomy
{
    internal static ConfigEntry<bool> CfgEnabled;
    internal static ConfigEntry<string> CfgActions;
    internal static ConfigEntry<int> CfgDepth;
    internal static ConfigEntry<int> CfgMax;

    private const int MaxLinesPerDump = 500;   // 单次解剖最多 500 行（超了明说）
    private const int MaxInterps = 12;         // 单个 tinter 最多列 12 条插值器

    /// <summary>材质上**永远要列**的属性（判定"颜色到底在谁身上"就靠这一组）。</summary>
    private static readonly string[] WatchProps =
    {
        "_TintColor", "_SubTexTintColor", "_DecoTexTintColor", "_AddColor",
        "_RemapColorFrom", "_RemapColorTo", "_RemapLerp",
        "_HighlightColor", "_BrightColor", "_AmbientColor", "_DissolveColor",
        "_EmissionColor", "_InnerFlameColor", "_OutterFlameColor",
        "_Color", "_BaseColor", "_Emission",
    };

    private sealed class Job
    {
        internal GameObject Go;
        internal float Due;
        internal int Left;
        internal string Header;
    }

    private static readonly List<Job> _jobs = new List<Job>();
    private static readonly HashSet<string> _done = new HashSet<string>();
    private static int _dumps;
    private static int _lines;
    private static MemberInfo _aemOwner;        // ActorEffectMgr.Owner

    // ------------------------------------------------------------------ 挂载

    public static int Apply(Harmony harmony)
    {
        int n = 0;
        var aem = AccessTools.TypeByName("GamePlay.ActorEffectMgr");
        if (aem == null) Plugin.Log?.LogWarning("  [冲刺解剖] 找不到 GamePlay.ActorEffectMgr");
        else
        {
            _aemOwner = Reflect.Member(aem, "Owner", Reflect.All);
            foreach (var m in aem.GetMethods(Reflect.All))
            {
                if (m.Name != "createVisualEffect" || m.DeclaringType != aem) continue;
                if (m.ReturnType.Name != "VFXEffectHub") continue;
                try
                {
                    harmony.Patch(m, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(DashAnatomy), nameof(EffectPostfix))));
                    n++;
                }
                catch (Exception e) { Plugin.Log?.LogWarning($"  [冲刺解剖] 挂 createVisualEffect 失败: {e.Message}"); }
            }
        }

        var bm = AccessTools.TypeByName("GamePlay.BulletMgr");
        if (bm == null) Plugin.Log?.LogWarning("  [冲刺解剖] 找不到 GamePlay.BulletMgr");
        else
        {
            var create = AccessTools.Method(bm, "createBulletImp");
            if (create != null)
            {
                harmony.Patch(create, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(DashAnatomy), nameof(BulletPostfix))));
                n++;
            }
            var val = AccessTools.Method(bm, "ValidateBullets");
            if (val != null)
            {
                harmony.Patch(val, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(DashAnatomy), nameof(Tick))));
                n++;
            }
        }
        if (n > 0)
            Plugin.Log?.LogInfo($"  [冲刺解剖] 就绪 {n} 处; 监听动作关键字=\"{CfgActions?.Value}\" " +
                                $"深度={CfgDepth?.Value} 上限={CfgMax?.Value}");
        return n;
    }

    // ------------------------------------------------------------------ 出生口

    /// <summary>
    /// 特效出生。⚠ **不能用 `__args` 取动作名** —— 本项目栽过"struct 参数装箱 ⇒ 无效指针 ⇒ 原生崩溃"。
    /// 所以走 `ActorEffectMgr.Owner`（`__instance` 上现成的字段）→ `ActionMgr.CurrentActionName`。
    /// </summary>
    public static void EffectPostfix(object __instance, object __result)
    {
        try
        {
            if (CfgEnabled?.Value != true || __instance == null || __result == null) return;
            var owner = Reflect.Read(__instance, _aemOwner) as GamePlay.ActorBase;
            string act = ActionOf(owner);
            if (!Match(act)) return;
            var go = Reflect.Cast<MonoBehaviour>(__result)?.gameObject;
            if (go == null) return;
            Schedule(go, $"{act} ▸ 特效 \"{Reflect.Name(go)}\"");
        }
        catch (Exception e) { Reflect.WarnOnce("dasanatomy|eff", "冲刺解剖(特效)", e); }
    }

    /// <summary>弹幕出生。所有冲刺动作都发 `esbullet`；MP 冲刺**只有弹幕**，所以这一口必须有。</summary>
    public static void BulletPostfix(object __result)
    {
        try
        {
            if (CfgEnabled?.Value != true || __result == null) return;
            var b = Reflect.Cast<GamePlay.BulletObj>(__result);
            var go = b?.gameObject;
            if (go == null) return;
            string nm = Reflect.Name(go);
            string act = BulletStartAction(__result);
            // 命中条件：动作名 或 弹幕名 任一命中关键字（默认 dash / esbullet）
            if (!Match(act) && !Match(nm)) return;
            Schedule(go, $"{(act.Length > 0 ? act : "?")} ▸ 弹幕 \"{nm}\"");
        }
        catch (Exception e) { Reflect.WarnOnce("dasanatomy|bul", "冲刺解剖(弹幕)", e); }
    }

    /// <summary>动作变化时，把本地玩家本体排一份解剖（去重键里带动作名，所以每个动作各一份）。</summary>
    private static void TrySchedulePlayer()
    {
        try
        {
            if (CfgEnabled?.Value != true) return;
            // ⚠ 每个 early return 都要说明原因 —— 本项目规范（"静默 return 会让人整轮日志空白、又得猜"）。
            var battle = BattleBase.Cur;
            if (battle == null) { LogOnce("dasanatomy|nobattle", "[冲刺解剖] 玩家本体: BattleBase.Cur = null（不在战斗里?）"); return; }
            // ⚠ `battle.PlayerSelf`（interop 属性）实测读出来是 **null**，但它是 0x58 上的**字段**
            //   ⇒ 改裸指针读（本项目反复验证过的规律: interop 读不到就按偏移读）。
            IntPtr bp = IntPtr.Zero;
            try { bp = ((Il2CppObjectBase)battle).Pointer; } catch { }
            if (bp == IntPtr.Zero) { LogOnce("dasanatomy|nobp", "[冲刺解剖] 玩家本体: BattleBase 指针拿不到"); return; }
            IntPtr selfP = System.Runtime.InteropServices.Marshal.ReadIntPtr(bp + 0x58);   // BattleBase.PlayerSelf
            if (selfP == IntPtr.Zero) { LogOnce("dasanatomy|noself", "[冲刺解剖] 玩家本体: BattleBase+0x58(PlayerSelf) = null"); return; }
            var self = Reflect.Cast<GamePlay.ActorBase>(new Il2CppObjectBase(selfP));
            if (self == null) { LogOnce("dasanatomy|noself2", "[冲刺解剖] 玩家本体: PlayerSelf 转 ActorBase 失败"); return; }
            string act = self.ActionMgr?.CurrentActionName ?? "";
            if (act.Length == 0) { LogOnce("dasanatomy|noact", "[冲刺解剖] 玩家本体: 当前动作名为空"); return; }
            if (act == _lastAct) return;
            _lastAct = act;
            // 只关心冲刺族，免得把每个动作都剖一遍
            bool hit = false;
            foreach (var k in Cfg.List(CfgActions?.Value ?? "dash,esbullet"))
                if (k.Length > 0 && act.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
            if (!hit) return;
            // ⚠⚠ 这里全部走**裸指针**。原因：实测 `self.ActorModel` / `self.SmoothObject`
            //   这两个 interop 字段读出来是 null（字段没生成或读法不对），而本项目里
            //   "私有/读不到的字段一律按偏移读"是既定路子。
            //   dump.cs 布局：ActorBase.SmoothObject=0x28 / ActorBase.ActorModel=0x30
            //                VisualObject.m_targetFlash=0x90 / m_targetAction=0x98 / m_visualInfo=0xB0
            IntPtr ap = IntPtr.Zero;
            try { ap = ((Il2CppObjectBase)self).Pointer; } catch { }
            if (ap == IntPtr.Zero) return;

            string flash = "(读不到)";
            try
            {
                IntPtr vo = System.Runtime.InteropServices.Marshal.ReadIntPtr(ap + 0x28);   // SmoothObject
                if (vo != IntPtr.Zero)
                {
                    byte fl = System.Runtime.InteropServices.Marshal.ReadByte(vo + 0x90);    // m_targetFlash
                    string ta = RawStr(System.Runtime.InteropServices.Marshal.ReadIntPtr(vo + 0x98));
                    IntPtr vi = System.Runtime.InteropServices.Marshal.ReadIntPtr(vo + 0xB0); // List<VisualInfo>
                    int viN = -1;
                    if (vi != IntPtr.Zero) { try { viN = System.Runtime.InteropServices.Marshal.ReadInt32(vi + 0x18); } catch { } }
                    flash = $"m_targetFlash={fl != 0} m_targetAction=\"{ta}\" visualInfo={viN}";
                }
                else flash = "SmoothObject=null";
            }
            catch (Exception e) { flash = "(读失败:" + e.Message + ")"; }
            Plugin.Log?.LogInfo($"[冲刺解剖] 动作=\"{act}\" 玩家 VisualObject: {flash}");

            // 玩家本体的 GameObject：ActorModel 是 MonoBehaviour（这才是能拿 gameObject 的入口）
            GameObject pgo = null;
            try
            {
                IntPtr mp = System.Runtime.InteropServices.Marshal.ReadIntPtr(ap + 0x30);   // ActorModel
                if (mp != IntPtr.Zero)
                    pgo = Reflect.Cast<MonoBehaviour>(new Il2CppObjectBase(mp))?.gameObject;
            }
            catch { }
            if (pgo == null) { Plugin.Log?.LogInfo($"[冲刺解剖] 动作=\"{act}\" 玩家本体 GameObject 拿不到（跳过这份）"); return; }
            Schedule(pgo, $"{act} ▸ 玩家本体", important: true);
        }
        catch { }
    }

    private static int _playerDumps;   // "玩家本体"独立配额

    private static void Schedule(GameObject go, string header, bool important = false)
    {
        // ★ 去重键 = 动作 + 根对象名（**不是实例** —— 按实例排会被批量生成打爆）
        string name = Reflect.Name(go);
        string key = header;
        if (!_done.Add(key))
        {
            LogOnce("dasanatomy|dup|" + key, $"[冲刺解剖] \"{key}\" 已经剖过了, 跳过（去重键=动作|对象名）");
            return;
        }
        // ⚠ 上一版"玩家本体"被特效配额挤掉了（实测: 特效铺满 16 个上限 ⇒ 玩家本体一份都没排上）
        //   ⇒ 重要样本走**独立配额**。这也是本项目的老教训: 共享一个上限 = 重要的被噪音挤掉。
        if (important)
        {
            if (_playerDumps >= 8) return;
            _playerDumps++;
        }
        else if (_done.Count > (CfgMax?.Value ?? 8))
        {
            _done.Remove(key);
            Plugin.Log?.LogInfo($"[冲刺解剖] 已达上限 {CfgMax?.Value} 种（这是上限, 不是没生成）—— " +
                                $"想多看几个就把 DashAnatomyMax 调大");
            return;
        }
        // 两次采样：第一次看结构，第二次看"值有没有落下去"（0.4s / 0.9s）
        _jobs.Add(new Job { Go = go, Due = Time.time + 0.4f, Left = 2, Header = header });
        Plugin.Log?.LogInfo($"[冲刺解剖] 排入样本 \"{key}\"（已排 {_done.Count}/{CfgMax?.Value}, 0.4s / 0.9s 各剖一次）");
    }

    /// <summary>每帧心跳（借 `BulletMgr.ValidateBullets`）。**每帧最多剖一个**。</summary>
    private static string _lastAct = "";

    private static int _tickN;

    public static void Tick()
    {
        if (++_tickN == 30)
            Plugin.Log?.LogInfo("[冲刺解剖] 心跳正常（ValidateBullets 每帧在跑）");
        // ★ 动作一变就**顺便把本地玩家本体也剖一份**。
        //   为什么：实测 MP 冲刺与普通冲刺拉起的**特效/弹幕/材质值完全一致**（值级对比为空），
        //   而"玩家身上的闪光"这类东西根本不在特效里 —— 它在**角色自己的视觉**上。
        //   要区分"MP 冲刺独有"，就得把角色本体也纳入逐行对比。
        TrySchedulePlayer();

        if (_jobs.Count == 0) return;
        try
        {
            for (int i = 0; i < _jobs.Count; i++)
            {
                var j = _jobs[i];
                if (j.Go == null) { _jobs.RemoveAt(i--); continue; }        // 已销毁/回池
                if (Time.time < j.Due) continue;
                if (_dumps >= 40)
                {
                    _jobs.Clear();
                    Plugin.Log?.LogInfo("[冲刺解剖] 已达总次数上限 40, 停止（重开局可再采）");
                    return;
                }
                _dumps++;
                _lines = 0;
                Dump(j.Go, j.Header, j.Left == 2 ? "结构" : "0.5s 后");
                j.Left--;
                j.Due = Time.time + 0.5f;
                if (j.Left <= 0) _jobs.RemoveAt(i);
                return;                                                     // ★ 每帧只剖一个
            }
        }
        catch (Exception e) { Reflect.WarnOnce("dasanatomy|tick", "冲刺解剖", e); }
    }

    // ------------------------------------------------------------------ 解剖本体

    /// <summary>供【快照】模块调用：摊开任意一个 GameObject（同一套递归+材质属性）。</summary>
    internal static void DumpPublic(GameObject root, string header)
    {
        _lines = 0;
        Dump(root, header, "快照");
    }

    private static void Dump(GameObject root, string header, string when)
    {
        P($"[冲刺解剖:{when}] ═════ {header} ═════");
        P($"[冲刺解剖:{when}]   根: \"{Reflect.Name(root)}\"  active={Safe(() => root.activeSelf.ToString())}");
        Walk(root.transform, 0, "  ");
        if (_lines >= MaxLinesPerDump)
            P($"[冲刺解剖:{when}]   （已到单次 {MaxLinesPerDump} 行上限, 余下未列 —— 深度可调 DashAnatomyDepth）");
        P($"[冲刺解剖:{when}] ═════ 结束（共 {_lines} 行）═════");
    }

    private static void Walk(Transform t, int depth, string indent)
    {
        if (t == null || _lines >= MaxLinesPerDump) return;
        int maxDepth = CfgDepth?.Value ?? 5;
        GameObject go;
        try { go = t.gameObject; } catch { return; }
        if (go == null) return;

        P($"[冲刺解剖] {indent}\"{Reflect.Name(go)}\"  {Parts(go)}");
        if (depth >= maxDepth)
        {
            int n = 0;
            try { n = t.childCount; } catch { }
            if (n > 0) P($"[冲刺解剖] {indent}  …（深度到顶, 还有 {n} 个子物体未展开: DashAnatomyDepth）");
            return;
        }
        int cnt = 0;
        try { cnt = t.childCount; } catch { }
        for (int i = 0; i < cnt && _lines < MaxLinesPerDump; i++)
        {
            Transform c = null;
            try { c = t.GetChild(i); } catch { }
            if (c != null) Walk(c, depth + 1, indent + "  ");
        }
    }

    /// <summary>
    /// 一个节点上**按已知类型逐个探**（`GetComponents(typeof(Component))` 的 GetType() 恒等于 "Component"，
    /// 拿不到真实类型 —— 见 FxAnatomy 的踩坑记录）。这也正是我们真正关心的那些组件。
    /// </summary>
    private static string Parts(GameObject go)
    {
        var sb = new StringBuilder();
        TryProbe<Renderer>(go, sb, r => RendererDesc(r));
        TryProbe<ParticleSystem>(go, sb, ps => ParticleDesc(ps));
        TryProbe<NOAH.VFX.MaterialTinter>(go, sb, t => TinterDesc("MaterialTinter", t, "MaterialInterpolators", typeof(NOAH.VFX.MaterialTinter)));
        TryProbe<NOAH.VFX.MaterialTinterProxy>(go, sb, t => TinterDesc("MaterialTinterProxy", t, "MaterialInterpolators", typeof(NOAH.VFX.MaterialTinterProxy)));
        TryProbe<NOAH.VFX.ActorTrailProxy>(go, sb, t => TinterDesc("ActorTrailProxy", t, "MaterialInterpolators", typeof(NOAH.VFX.ActorTrailProxy)));
        TryProbe<MeshFilter>(go, sb, mf => "Mesh=" + Safe(() => mf.mesh == null ? "null" : mf.mesh.name));
        ClassOf(go, "NOAH.VFX.VFXEffectHub", sb, "VFXEffectHub");
        ClassOf(go, "NOAH.VFX.VFXEffectExtension", sb, "VFXEffectExtension");
        ClassOf(go, "NOAH.VFX.TransformMotorProxy", sb, "TransformMotorProxy");
        ClassOf(go, "NOAH.VFX.MaterialCollector", sb, "MaterialCollector");
        ClassOf(go, "GamePlay.ActorVisualBase", sb, "ActorVisualBase");
        return sb.Length == 0 ? "（无我们关心的组件）" : sb.ToString();
    }

    private static void TryProbe<T>(GameObject go, StringBuilder sb, Func<T, string> desc) where T : Il2CppObjectBase
    {
        try
        {
            var ty = Il2CppType.From(typeof(T));
            var comps = go.GetComponents(ty);
            foreach (var c in Reflect.Items(comps))
            {
                var v = Reflect.Cast<T>(c);
                if (v == null) continue;
                sb.Append("  [").Append(typeof(T).Name).Append("]").Append(desc(v));
            }
        }
        catch (Exception e) { sb.Append("  [").Append(typeof(T).Name).Append(" 探测失败:").Append(e.Message).Append(']'); }
    }

    /// <summary>
    /// 一次性解析好的类型（找不到就记 null, **不再重试**）。
    ///
    /// ⚠⚠ 这里踩过大坑：原来每个节点都调 `AccessTools.TypeByName(name)` ——
    ///   那个 API **每次都会重扫所有程序集**，找不到还要打 Warning。
    ///   于是一个特效几百个节点 ⇒ 几百次全程序集扫描 ⇒ **黄色警告刷屏 + 卡顿**
    ///   （日志实证: `Could not find type named NOAH.VFX.VFXEffectExtension` ×130、
    ///    `GetTypesFromAssembly: assembly UnityEngine.CoreModule` ×995）。
    ///   教训：热路径上**任何**反射都得先解析再缓存。
    /// </summary>
    private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

    private static Type CachedType(string name)
    {
        if (_typeCache.TryGetValue(name, out var t)) return t;
        try { t = AccessTools.TypeByName(name); } catch { t = null; }
        _typeCache[name] = t;          // ★ null 也缓存 —— 找不到就别每次都重扫
        return t;
    }

    private static void ClassOf(GameObject go, string typeName, StringBuilder sb, string label)
    {
        try
        {
            var t = CachedType(typeName);
            if (t == null) return;
            var ty = Il2CppType.From(t);
            foreach (var c in Reflect.Items(go.GetComponents(ty)))
            {
                if (c == null) continue;
                IntPtr p = IntPtr.Zero;
                try { p = ((Il2CppObjectBase)c).Pointer; } catch { }
                sb.Append("  [").Append(label).Append(']');
                if (label == "VFXEffectExtension")
                {
                    // ScreenSpace 是我们判断"全屏层"的依据, 顺手打出来
                    var m = Reflect.Member(t, "ScreenSpace", Reflect.All);
                    if (m != null) sb.Append(" ScreenSpace=").Append(Safe(() => (Reflect.Read(c, m) ?? "?").ToString()));
                }
            }
        }
        catch { }
    }

    private static string RendererDesc(Renderer r)
    {
        var sb = new StringBuilder();
        sb.Append(" type=").Append(Reflect.KlassName(PtrOf(r)));
        string mn = "null", sh = "";
        Material mat = null;
        try { mat = r.sharedMaterial; if (mat != null) mn = SafeName(mat); } catch { }
        try { if (mat != null && mat.shader != null) sh = mat.shader.name ?? ""; } catch { }
        sb.Append(" shared=\"").Append(mn).Append('"');
        if (sh.Length > 0) sb.Append(" shader=\"").Append(sh).Append('"');
        if (mat != null) sb.Append(ColorsOf(mat));
        return sb.ToString();
    }

    private static string ParticleDesc(ParticleSystem ps)
    {
        var sb = new StringBuilder(" ");
        sb.Append(Safe(() => "startColor=" + Fmt(ps.main.startColor.color)));
        sb.Append(Safe(() => " 存活=" + ps.particleCount));
        return sb.ToString();
    }

    /// <summary>材质上的**着色属性当前值**：关注名单一个不落 + 其余"非恒等色"的颜色属性。</summary>
    private static string ColorsOf(Material m)
    {
        var sb = new StringBuilder();
        try
        {
            int cnt = m.shader == null ? 0 : m.shader.GetPropertyCount();
            for (int i = 0; i < cnt && i < 200; i++)
            {
                string pn = Safe(() => m.shader.GetPropertyName(i));
                if (string.IsNullOrEmpty(pn)) continue;
                bool watch = false;
                foreach (var w in WatchProps) if (pn == w) { watch = true; break; }
                bool isColor = false;
                try { isColor = m.shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Color; } catch { }
                if (!watch && !isColor) continue;
                if (!m.HasProperty(pn)) continue;
                Color c;
                try { c = m.GetColor(pn); } catch { continue; }
                // 非关注名单的：只列"不是恒等色"的，免得 74 个属性全倒出来
                if (!watch && c.r == 1f && c.g == 1f && c.b == 1f) continue;
                if (!watch && c.r == 0f && c.g == 0f && c.b == 0f) continue;
                sb.Append("  ").Append(pn).Append('=').Append(Fmt(c));
            }
        }
        catch (Exception e) { sb.Append("  (属性枚举失败:").Append(e.Message).Append(')'); }
        return sb.ToString();
    }

    /// <summary>
    /// Tinter / Proxy 的**插值器逐条**（真实类名 + propName + start/end）。
    /// `InterpolatorBase[]` 里是**多态**的 —— 按错误类型硬解释会读出垃圾指针（本项目崩过），
    /// 所以这里一律先 TryCast，再打**真实类名**。
    /// </summary>
    private static string TinterDesc(string label, object tinter, string memberName, Type type)
    {
        var sb = new StringBuilder(" ");
        try
        {
            var mi = Reflect.Member(type, memberName, Reflect.All);
            if (mi == null) { sb.Append("(找不到字段 ").Append(memberName).Append(')'); return sb.ToString(); }
            var arr = Reflect.Read(tinter, mi);
            if (arr == null) { sb.Append("插值器=null"); return sb.ToString(); }
            IntPtr ap = IntPtr.Zero;
            try { ap = ((Il2CppObjectBase)arr).Pointer; } catch { }
            int len = -1;
            try { len = System.Runtime.InteropServices.Marshal.ReadInt32(ap + 0x18); } catch { }
            sb.Append("插值器=").Append(len).Append('条');
            for (int i = 0; i < len && i < MaxInterps; i++)
            {
                IntPtr item;
                try { item = System.Runtime.InteropServices.Marshal.ReadIntPtr(ap + 0x20 + i * IntPtr.Size); }
                catch { break; }
                if (item == IntPtr.Zero) { sb.Append("  [").Append(i).Append("]null"); continue; }
                string cls = Reflect.KlassName(item);
                var mc = new Il2CppObjectBase(item).TryCast<NOAH.VFXInterpolator.MaterialColorInterpolator>();
                if (mc != null)
                {
                    sb.Append("  [").Append(i).Append("]").Append(cls)
                      .Append(" prop=\"").Append(Safe(() => mc.propName)).Append('"')
                      .Append(" start=").Append(Fmt(mc.startValue))
                      .Append(" end=").Append(Fmt(mc.endValue));
                }
                else sb.Append("  [").Append(i).Append("]").Append(cls).Append("(非颜色插值器)");
            }
            if (len > MaxInterps) sb.Append($"  …（还有 {len - MaxInterps} 条未列）");
        }
        catch (Exception e) { sb.Append("(读失败:").Append(e.Message).Append(')'); }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 小工具

    /// <summary>il2cpp 字符串: klass@0 monitor@8 length@0x10 chars@0x14(UTF-16)。</summary>
    private static string RawStr(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        try
        {
            int len = System.Runtime.InteropServices.Marshal.ReadInt32(p + 0x10);
            if (len <= 0 || len > 512) return "";
            return System.Runtime.InteropServices.Marshal.PtrToStringUni(p + 0x14, len);
        }
        catch { return ""; }
    }

    private static bool Match(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var k in Cfg.List(CfgActions?.Value ?? "dash,esbullet"))
            if (k.Length > 0 && s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static string ActionOf(GamePlay.ActorBase a)
    {
        try { return a?.ActionMgr?.CurrentActionName ?? ""; } catch { return ""; }
    }

    /// <summary>弹幕的 `StartAction`（BulletConf 上的字段，BulletProbe 用的同一套反射）。</summary>
    private static string BulletStartAction(object bullet)
    {
        try
        {
            var conf = Reflect.Read(bullet, BulletProbe._confMember);
            if (conf == null) return "";
            var mi = BulletProbe.FindProp(conf, "StartAction");
            return mi == null ? "" : (Reflect.Read(conf, mi) as string) ?? "";
        }
        catch { return ""; }
    }

    private static readonly HashSet<string> _said = new HashSet<string>();
    private static void LogOnce(string key, string msg)
    {
        if (_said.Add(key)) Plugin.Log?.LogInfo(msg);
    }

    private static void P(string s)
    {
        _lines++;
        Plugin.Log?.LogInfo(s);
    }

    private static IntPtr PtrOf(Il2CppObjectBase o)
    {
        try { return o.Pointer; } catch { return IntPtr.Zero; }
    }

    private static string SafeName(UnityEngine.Object o)
    {
        try { return o.name ?? ""; } catch { return "?"; }
    }

    private static string Fmt(Color c) => $"({c.r:0.###},{c.g:0.###},{c.b:0.###},{c.a:0.###})";

    private static string Safe(Func<string> f)
    {
        try { return f() ?? ""; } catch { return "(读失败)"; }
    }
}
