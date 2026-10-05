using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 贝德维尔（Ultra）翅膀纹章的诊断与尝试修复。
///
/// 已经掌握的两条线索
/// ──────────────────
/// 1) **渲染器普查**（EffectRecolor.Census 加的）实测：
///       Role.Es.es_AH_01        子物体=3  渲染器=8  启用=8   Mesh=3  粒子=5   ← prefab 资产
///       es_AH_01(Clone)         子物体=3  渲染器=8  启用=5   Mesh=3  粒子=5   ← 运行实例
///    实例上启用的正好是 5 —— **三个 MeshRenderer 全被禁用了**。
///    且 `未激活子物体=0`，说明是 `Renderer.enabled=false`，不是 `SetActive(false)`。
///    「巨大的翅膀」很可能就是那 3 个 Mesh。
/// 2) **贝德维尔确实会造纹章**：日志抓到 `UDA` / `UDA0` 两条 bullet_action。用户说
///    「贝德维尔只有第二段才会出翅膀」。
///
/// 这个模块做两件事
/// ────────────────
/// · **看**：把命中特效的每个渲染器逐条列出来（层级路径 / 类型 / enabled /
///   节点是否激活 / 缩放 / 材质颜色）。「生成了但看不见」有多种病因，
///   它们在计数上长得一样，必须具体到每一个渲染器才能分开。
/// · **试**：`ForceMeshOn=true` 时，在随后的若干帧里把 Mesh / SkinnedMesh
///   渲染器强制打开，看翅膀会不会出来 —— 这是验证「就是被关掉了」最快的办法。
///
/// ⚠ 只强制若干帧（ForceFrames），不做全程覆盖：
///   全程覆盖会跟游戏自己的显示逻辑打架，就算"看起来修好了"也说明不了什么。
/// </summary>
internal static class AhWing
{
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;
    internal static BepInEx.Configuration.ConfigEntry<string> CfgNameFilter;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgForceMeshOn;
    internal static BepInEx.Configuration.ConfigEntry<int> CfgForceFrames;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgVerbose;

    private class Tracked
    {
        public GameObject Go;
        public string Name;
        public int Ticks;
        public int ForceLeft;
        public HashSet<int> LoggedAt = new HashSet<int>();
        /// <summary>-1 = 还没分配曲线序号; -2 = 超出配额不打。</summary>
        public int CurveOrdinal = -1;
    }

    private static readonly List<Tracked> _list = new List<Tracked>();
    private const int MaxTrack = 40;
    /// <summary>颜色曲线的采样帧（30fps → 0~4 秒）。</summary>
    private static readonly HashSet<int> SampleTicks = new HashSet<int> { 0, 10, 20, 30, 45, 60, 90, 120 };
    /// <summary>曲线只对前 N 个实例打，免得同一局同特效重复几十遍把日志淹了。</summary>
    private const int CurveMax = 2;
    private static int _curved;

    public static int Apply(Harmony harmony)
    {
        var hub = AccessTools.TypeByName("NOAH.VFX.VFXEffectHub");
        if (hub == null) { Plugin.Log?.LogWarning("  [贝德维尔] 找不到 NOAH.VFX.VFXEffectHub"); return 0; }

        int n = 0;

        // 生成入口：DoStart / Reactivate 都会走。挂两个都行，重复命中我们自己会去重。
        foreach (var mname in new[] { "DoStart", "Reactivate" })
        {
            var m = AccessTools.Method(hub, mname);
            if (m == null) continue;
            try
            {
                harmony.Patch(m, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(AhWing), nameof(HubPostfix))));
                Plugin.Log.LogInfo($"  [贝德维尔] 已挂钩 VFXEffectHub.{mname}");
                n++;
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"  [贝德维尔] 挂 {mname} 失败: {e.Message}"); }
        }

        // 借 BulletMgr.ValidateBullets 当每帧心跳。
        // （我们只借它的"每帧被调用"这个性质，逻辑跟弹幕无关 —— 这是本项目里现成的帧钩子。）
        var bm = AccessTools.TypeByName("GamePlay.BulletMgr");
        var val = bm == null ? null : AccessTools.Method(bm, "ValidateBullets");
        if (val != null)
        {
            try
            {
                harmony.Patch(val, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(AhWing), nameof(Tick))));
                Plugin.Log.LogInfo("  [贝德维尔] 已挂钩 BulletMgr.ValidateBullets (当每帧心跳)");
                n++;
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"  [贝德维尔] 挂心跳失败: {e.Message}"); }
        }

        if (n > 0)
            Plugin.Log.LogInfo($"  [贝德维尔] 生效配置: Enabled={CfgEnabled?.Value} " +
                               $"NameFilter=\"{CfgNameFilter?.Value}\" ForceMeshOn={CfgForceMeshOn?.Value} " +
                               $"ForceFrames={CfgForceFrames?.Value}");
        return n;
    }

    private static bool Matches(string name)
    {
        var f = CfgNameFilter?.Value;
        if (string.IsNullOrWhiteSpace(f)) return true;
        return name != null && name.IndexOf(f.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static void HubPostfix(object __instance)
    {
        try
        {
            if (CfgEnabled?.Value != true) return;
            var mb = __instance as MonoBehaviour;
            if (mb == null) return;
            var go = mb.gameObject;
            if (go == null) return;
            if (!Matches(go.name)) return;

            foreach (var t in _list) if (t.Go == go) return;   // 同一个对象只跟一次
            if (_list.Count >= MaxTrack) return;

            _list.Add(new Tracked
            {
                Go = go, Name = go.name, Ticks = 0,
                ForceLeft = CfgForceMeshOn?.Value == true
                            ? Math.Max(0, CfgForceFrames?.Value ?? 90) : 0,
            });
        }
        catch { }
    }

    /// <summary>每帧心跳（挂在 BulletMgr.ValidateBullets 的 Postfix 上）。</summary>
    public static void Tick()
    {
        if (_list.Count == 0) return;

        for (int i = _list.Count - 1; i >= 0; i--)
        {
            var t = _list[i];
            try
            {
                if (t.Go == null) { _list.RemoveAt(i); continue; }

                // 第 0 帧打一次完整清单(层级/类型/缩放/颜色)。
                if (CfgVerbose?.Value == true && t.Ticks == 0 && t.LoggedAt.Add(0))
                    Dump(t, 0);

                // 之后打【颜色时间曲线】。
                //
                // 为什么必须曲线而不能只看两帧: 实测最可疑的那块是
                //     glow01  70×15  color=(0, 0.33, 2, 0)    第0帧 alpha=0
                //     glow01  70×15  color=(0, 0.35, 2, 0.06) 第30帧 alpha=0.06
                // 它显然是"渐显"的 —— 只采两点根本看不出它是涨到 1 还是就停在 0.06。
                // 而它偏偏是整组里最大的一块(70×15), 也就是"翅膀"的最大嫌疑。
                if (CfgVerbose?.Value == true && SampleTicks.Contains(t.Ticks) && t.LoggedAt.Add(t.Ticks))
                    Curve(t, t.Ticks);

                if (t.ForceLeft > 0)
                {
                    int forced = ForceMeshesOn(t.Go);
                    if (t.Ticks == 1 && forced > 0 && CfgVerbose?.Value == true)
                        Plugin.Log?.LogInfo($"[贝德维尔] 强制打开 \"{t.Name}\" 的 {forced} 个 Mesh 渲染器");
                    t.ForceLeft--;
                }

                t.Ticks++;
                if (t.Ticks > 120 && t.ForceLeft <= 0) _list.RemoveAt(i);
                else _list[i] = t;
            }
            catch { _list.RemoveAt(i); }
        }
    }

    /// <summary>把节点下所有 Mesh / SkinnedMesh 渲染器打开，返回改动的个数。</summary>
    private static int ForceMeshesOn(GameObject go)
    {
        int n = 0;
        try
        {
            foreach (var r in AllRenderers(go))
            {
                if (r == null || r.enabled) continue;
                if (r.TryCast<MeshRenderer>() != null || r.GetType().Name.Contains("SkinnedMeshRenderer"))
                {
                    r.enabled = true;
                    n++;
                }
            }
        }
        catch { }
        return n;
    }

    private static List<Renderer> AllRenderers(GameObject go)
    {
        var outl = new List<Renderer>();
        try
        {
            var comps = go.GetComponentsInChildren(
                Il2CppInterop.Runtime.Il2CppType.From(typeof(Renderer)), true);
            if (comps == null) return outl;
            foreach (var c in comps)
            {
                var r = (c as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<Renderer>();
                if (r != null) outl.Add(r);
            }
        }
        catch { }
        return outl;
    }

    private static string Path(Transform tr)
    {
        var sb = new StringBuilder();
        int guard = 0;
        while (tr != null && guard++ < 16)
        {
            sb.Insert(0, "/" + tr.gameObject.name);
            tr = tr.parent;
        }
        return sb.Length == 0 ? "?" : sb.ToString();
    }

    /// <summary>
    /// 颜色时间曲线：每个 Mesh 渲染器一行，看它的颜色（尤其 alpha）随帧怎么变。
    /// 用球谐那套判据筛不出"翅膀"，但"哪块面片最终会亮起来"是能一眼看出来的。
    /// </summary>
    private static void Curve(Tracked t, int tick)
    {
        if (t.CurveOrdinal < 0)
        {
            if (_curved >= CurveMax) { t.CurveOrdinal = -2; return; }
            t.CurveOrdinal = _curved++;
            Plugin.Log?.LogInfo($"[贝德维尔:曲线] ==== 对象 #{t.CurveOrdinal} \"{t.Name}\" ====");
        }
        if (t.CurveOrdinal == -2) return;

        try
        {
            var sb = new StringBuilder();
            sb.Append($"[贝德维尔:曲线] #{t.CurveOrdinal} t={tick,3}f");
            foreach (var r in AllRenderers(t.Go))
            {
                if (r == null) continue;
                if (r.TryCast<MeshRenderer>() == null) continue;   // 只关心 Mesh（翅膀必然是 Mesh）
                var tr = r.transform;
                string nm = tr == null ? "?" : tr.gameObject.name;
                sb.Append($"  {nm}:[{(r.enabled ? "on" : "OFF")}] {ColorOf(r)}");
            }
            Plugin.Log?.LogInfo(sb.ToString());
        }
        catch { }
    }

    private static string ColorOf(Renderer r)
    {
        try
        {
            var mr = r.TryCast<MeshRenderer>();
            var mat = mr == null ? null : mr.sharedMaterial;
            if (mat == null) return "mat=null";
            var c = mat.HasProperty("_TintColor") ? mat.GetColor("_TintColor")
                  : mat.HasProperty("_Color") ? mat.GetColor("_Color")
                  : mat.color;
            return $"({c.r:0.##},{c.g:0.##},{c.b:0.##},{c.a:0.##})";
        }
        catch { return "?"; }
    }

    /// <summary>把命中的特效实例逐渲染器列出来。</summary>
    private static void Dump(Tracked t, int tick)
    {
        try
        {
            var rs = AllRenderers(t.Go);
            var sb = new StringBuilder();
            sb.Append($"[贝德维尔] \"{t.Name}\" @第{tick}帧  渲染器 {rs.Count} 个, 自身active={t.Go.activeSelf}");
            int on = 0, meshOff = 0;
            foreach (var r in rs)
            {
                if (r == null) continue;
                var tr = r.transform;
                string kind = r.TryCast<MeshRenderer>() != null ? "Mesh"
                            : r.TryCast<ParticleSystemRenderer>() != null ? "Particle"
                            : r.GetType().Name;
                if (r.enabled) on++;
                else if (kind == "Mesh" || kind.Contains("Skinned")) meshOff++;

                var sc = tr == null ? Vector3.zero : tr.localScale;
                string col = "?";
                try
                {
                    var mat = r.TryCast<MeshRenderer>() != null
                              ? r.TryCast<MeshRenderer>().sharedMaterial : null;
                    if (mat != null)
                    {
                        var c = mat.HasProperty("_TintColor") ? mat.GetColor("_TintColor")
                              : mat.HasProperty("_Color") ? mat.GetColor("_Color")
                              : mat.color;
                        col = $"({c.r:0.##},{c.g:0.##},{c.b:0.##},{c.a:0.##})";
                    }
                }
                catch { }

                sb.Append($"\n    {kind,-9} enabled={r.enabled,-5} active={r.gameObject.activeInHierarchy,-5} " +
                          $"scale=({sc.x:0.##},{sc.y:0.##},{sc.z:0.##}) color={col}  {Path(tr)}");
            }
            sb.Append($"\n    => 启用 {on}/{rs.Count}, 其中被关掉的 Mesh {meshOff} 个");
            Plugin.Log?.LogInfo(sb.ToString());
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"[贝德维尔] dump 失败: {e.Message}"); }
    }
}
