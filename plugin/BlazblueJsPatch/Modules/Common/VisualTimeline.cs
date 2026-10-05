using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using GamePlay;   // BattleBase.Cur.PlayerSelf —— 本地玩家
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 可视性时间线 —— **不需要反应时间**地抓"一闪而过的东西"。
///
/// 为什么要有它（用户原话：「不行，没有时停，我根本抓不住那个一闪而过的光弧」）
/// ─────────────────────────────────────────────────────────────────────────
/// 前面几版工具（F6 快照 / F7 鼠标拾取）都要求**人在那一瞬间按键**。
/// 对一个只亮零点几秒的光弧来说，这个设计要求本身就是错的 ——
/// **不该让人去追时间，该让程序一直录，事后回放。**
///
/// 做法：目标动作一开始就**自动逐帧**扫场上所有"活着的渲染器"，记下每个对象的
/// 出现/显形/隐去/消失，动作结束后自动把整条时间线倒进日志。**全程零操作。**
///
/// ★ 输出的排序是这份工具的灵魂：**按「存活帧数」升序**。
///   常驻的东西（角色本体、场景）活几百帧，排最后；
///   那道一闪而过的光弧只活个位数帧 ⇒ **直接排在最前面**，一眼就是它。
///
/// 两个信号源（覆盖粒子型 和 Mesh 型两种特效）：
///   · `ParticleSystem.particleCount &gt; 0`   → 粒子型（有人正在发射）
///   · `Renderer.isVisible`（排除 ParticleSystemRenderer） → Mesh/网格型
///   另外还挂了两个「打开」事件（**不是「造出来」的事件，所以 Instantiate 探针看不见**）：
///   · `GameObject.SetActive(true)` —— 对象池/RecycleRoot 复活的必经之路
///   · `Renderer.set_enabled(true)` —— 单独把一个渲染器点亮
///
/// ⚠ 纪律（否则卡爆）：
///   · 只在动作窗口内扫描（平时每帧只做一次字符串比较）；
///   · 路径字符串**只在对象第一次被看到时才拼**（常驻对象每帧拼路径 == GC 风暴，本项目栽过）；
///   · 硬上限 + 超了明说（不静默丢）。
/// </summary>
internal static class VisualTimeline
{
    internal static ConfigEntry<bool> CfgEnabled;
    internal static ConfigEntry<string> CfgActions;
    internal static ConfigEntry<float> CfgGrace;
    internal static ConfigEntry<int> CfgMax;

    private static string _act = "";
    private static bool _recording;
    private static float _lastMatchAt;
    private static int _frame;
    private static int _events;
    private static int _dropped;

    private sealed class Rec
    {
        public string Path = "";
        public string Info = "";
        public bool Live;
        public int FirstFrame = -1;
        public int LiveFrames;
    }

    private static readonly Dictionary<int, Rec> _recs = new Dictionary<int, Rec>();
    private static readonly HashSet<int> _cur = new HashSet<int>();
    private static readonly HashSet<string> _logged = new HashSet<string>();

    // ------------------------------------------------------------------ 挂载

    public static int Apply(Harmony harmony)
    {
        int n = 0;

        // 「打开」事件之一：对象池复活。**这是 Instantiate 探针看不见的那一类** ——
        // 东西早就造好了，只是被 SetActive(false) 收起来，用的时候再 true。
        try
        {
            var goTy = AccessTools.TypeByName("UnityEngine.GameObject");
            var setActive = goTy == null ? null : AccessTools.Method(goTy, "SetActive", new[] { typeof(bool) });
            if (setActive != null)
            {
                harmony.Patch(setActive, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(VisualTimeline), nameof(SetActivePostfix))));
                n++;
            }
            else Plugin.Log?.LogWarning("  [可视性时间线] 找不到 GameObject.SetActive(bool)");
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [可视性时间线] 挂 SetActive 失败: {e.Message}"); }

        // 「打开」事件之二：单独点亮一个渲染器（不动 GameObject）。
        try
        {
            var rTy = AccessTools.TypeByName("UnityEngine.Renderer");
            var setter = rTy == null ? null : AccessTools.PropertySetter(rTy, "enabled");
            if (setter != null)
            {
                harmony.Patch(setter, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(VisualTimeline), nameof(RendererEnabledPostfix))));
                n++;
            }
            else Plugin.Log?.LogWarning("  [可视性时间线] 找不到 Renderer.enabled setter（这个口子会漏）");
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [可视性时间线] 挂 Renderer.enabled 失败: {e.Message}"); }

        // 心跳：借 BulletMgr.ValidateBullets（本项目通用逻辑帧）。
        // ⚠ 这个方法是**多个模块共用的心跳**（InstantiateProbe / DashAnatomy 也挂着），
        //   Harmony 允许多个 postfix 共存，互不影响。
        var bm = AccessTools.TypeByName("GamePlay.BulletMgr");
        var val = bm == null ? null : AccessTools.Method(bm, "ValidateBullets");
        if (val != null)
        {
            harmony.Patch(val, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(VisualTimeline), nameof(Tick))));
            n++;
        }
        else Plugin.Log?.LogWarning("  [可视性时间线] 找不到 BulletMgr.ValidateBullets（心跳没了, 不会录）");

        if (n > 0)
            Plugin.Log?.LogInfo($"  [可视性时间线] 就绪 {n} 处; 监听动作关键字=\"{CfgActions?.Value}\"（自动录制, 无需按键）");
        return n;
    }

    // ------------------------------------------------------------------ 「打开」事件

    /// <summary>
    /// ⚠ 这里**只能读形参**，绝不能碰 `__args`（struct 参数装箱 ⇒ 无效指针 ⇒ 原生崩溃，本项目栽过）。
    /// </summary>
    public static void SetActivePostfix(GameObject __instance, bool value)
    {
        try
        {
            if (!value || !_recording) return;
            if (__instance == null) return;
            Note(PathOf(__instance), "SetActive(true)");
        }
        catch { }
    }

    public static void RendererEnabledPostfix(Renderer __instance, bool value)
    {
        try
        {
            if (!value || !_recording) return;
            var go = __instance == null ? null : __instance.gameObject;
            if (go == null) return;
            Note(PathOf(go), "Renderer.enabled=true");
        }
        catch { }
    }

    /// <summary>事件型命中：去重后记一条日志，不参与逐帧统计（统计只认"真的被画出来"）。</summary>
    private static void Note(string path, string how)
    {
        int cap = (CfgMax?.Value ?? 40) * 20;
        if (_events >= cap) { _dropped++; return; }
        if (!_logged.Add("EV|" + how + "|" + path)) return;
        _events++;
        Plugin.Log?.LogInfo($"[时间线] ▶ {how}  \"{path}\"   (第 {_frame} 帧)");
    }

    // ------------------------------------------------------------------ 心跳

    public static void Tick()
    {
        try
        {
            if (CfgEnabled?.Value != true) return;
            _act = CurrentPlayerAction();
            bool match = InWindow(_act);
            float now = Time.unscaledTime;
            if (match) _lastMatchAt = now;

            if (!_recording)
            {
                if (!match) return;
                _recording = true;
                _frame = 0; _events = 0; _dropped = 0;
                _recs.Clear(); _cur.Clear(); _logged.Clear();
                Plugin.Log?.LogInfo($"[时间线] ● 开始录制 —— 动作=\"{_act}\"（逐帧扫场上存活对象, 结束后自动出时间线）");
            }
            else
            {
                // 动作名会随连段切换，只要还在命中关键字就继续录；
                // 都不命中后给 CfgGrace 秒宽限（特效往往比动作名晚一拍才消）。
                float grace = CfgGrace?.Value ?? 0.8f;
                if (!match && now - _lastMatchAt > grace) { Stop(); return; }
            }

            _frame++;
            Scan();
        }
        catch (Exception e) { Reflect.WarnOnce("vistl|tick", "可视性时间线", e); }
    }

    private static string CurrentPlayerAction()
    {
        try
        {
            var battle = BattleBase.Cur;
            var self = battle?.PlayerSelf;
            if (self == null) return "";
            return self.ActionMgr?.CurrentActionName ?? "";
        }
        catch { return ""; }
    }

    private static string[] _kws;
    private static string _kwsSrc;

    private static bool InWindow(string act)
    {
        if (string.IsNullOrEmpty(act)) return false;
        string src = CfgActions?.Value ?? "dashSkill,dashSkill2,dash";
        if (_kws == null || !ReferenceEquals(_kwsSrc, src))
        {
            var parts = src.Split(',');
            var list = new List<string>();
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            _kws = list.ToArray();
            _kwsSrc = src;
        }
        foreach (var k in _kws)
            if (act.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    // ------------------------------------------------------------------ 逐帧扫描

    private static void Scan()
    {
        _cur.Clear();

        // ① 粒子型：有粒子在飞 = 活着。
        //    用 particleCount 而不是 isVisible —— ParticleSystemRenderer 即使一颗粒子都没有
        //    也报 isVisible=true（包围盒还在），那样"出现/消失"永远测不出来。
        try
        {
            var arr = UnityEngine.Object.FindObjectsOfType(
                Il2CppInterop.Runtime.Il2CppType.From(typeof(ParticleSystem)));
            foreach (var o in Reflect.Items(arr))
            {
                var ps = Reflect.Cast<ParticleSystem>(o);
                if (ps == null) continue;
                bool live;
                try { live = ps.particleCount > 0; } catch { continue; }
                if (!live) continue;
                var go = SafeGo(ps);
                if (go == null) continue;
                Mark(go);
            }
        }
        catch (Exception e) { Reflect.WarnOnce("vistl|scanps", "可视性时间线/粒子扫描", e); }

        // ② Mesh/网格型：真的被画出来了 = 活着。
        //    ⚠ 排除 ParticleSystemRenderer（上面那条已经管了；重复统计会把"存活帧数"灌成两倍，
        //      那正好毁掉本工具唯一的判据 —— 升序排行）。
        try
        {
            var arr = UnityEngine.Object.FindObjectsOfType(
                Il2CppInterop.Runtime.Il2CppType.From(typeof(Renderer)));
            foreach (var o in Reflect.Items(arr))
            {
                var r = Reflect.Cast<Renderer>(o);
                if (r == null) continue;
                bool isPs;
                try { isPs = r is ParticleSystemRenderer; } catch { isPs = false; }
                if (isPs) continue;
                bool live;
                try { live = r.isVisible; } catch { continue; }
                if (!live) continue;
                var go = SafeGo(r);
                if (go == null) continue;
                Mark(go);
            }
        }
        catch (Exception e) { Reflect.WarnOnce("vistl|scanr", "可视性时间线/渲染器扫描", e); }

        // ③ 上一帧还活着、这一帧没了 ⇒ 隐去。
        //    ★ 路径在这里是从 Rec 里直接取的（对象刚消失，路径必然还是对的），不用回查。
        List<int> dead = null;
        foreach (var kv in _recs)
        {
            if (kv.Value.Live && !_cur.Contains(kv.Key))
            {
                (dead ?? (dead = new List<int>())).Add(kv.Key);
            }
        }
        if (dead != null)
        {
            foreach (var id in dead)
            {
                var rec = _recs[id];
                rec.Live = false;
                if (_events >= (CfgMax?.Value ?? 40) * 20) { _dropped++; continue; }
                _events++;
                Plugin.Log?.LogInfo($"[时间线] － 隐去  \"{rec.Path}\"   (存活 {rec.LiveFrames} 帧, 第 {_frame} 帧熄灭)");
            }
        }
    }

    /// <summary>
    /// 记一笔「这一帧它活着」。
    /// ⚠ 路径字符串**只在这个对象第一次被看到时才拼** —— 常驻对象每帧拼一次路径就是 GC 风暴
    ///   （本项目在 `Cfg.List()` / `AccessTools.TypeByName` 上栽过同一个坑）。
    /// </summary>
    private static void Mark(GameObject go)
    {
        int id = SafeId(go);
        if (id == 0) return;
        // 本帧已经被另一个扫描器记过了（同一个 GO 上既挂 ParticleSystem 又挂别的渲染器）——
        // 不拦的话同一帧会 +2，"存活帧数"就被灌成两倍，正好毁掉升序排行这个唯一判据。
        if (!_cur.Add(id)) return;

        if (_recs.TryGetValue(id, out var rec))
        {
            rec.LiveFrames++;
            if (!rec.Live)
            {
                rec.Live = true;
                if (_events < (CfgMax?.Value ?? 40) * 20)
                {
                    _events++;
                    Plugin.Log?.LogInfo($"[时间线] ＋ 显形  \"{rec.Path}\"   (第 {_frame} 帧)");
                }
                else _dropped++;
            }
            return;
        }

        var nr = new Rec
        {
            Path = PathOf(go),
            Info = Summarize(go),
            Live = true,
            FirstFrame = _frame,
            LiveFrames = 1,
        };
        _recs[id] = nr;
        if (_events < (CfgMax?.Value ?? 40) * 20)
        {
            _events++;
            Plugin.Log?.LogInfo($"[时间线] ＊ 新对象 \"{nr.Path}\"   (第 {_frame} 帧)  {nr.Info}");
        }
        else _dropped++;
    }

    private static GameObject SafeGo(Component c)
    {
        try { return c == null ? null : c.gameObject; } catch { return null; }
    }

    private static int SafeId(GameObject go)
    {
        try { return go.GetInstanceID(); } catch { return 0; }
    }

    // ------------------------------------------------------------------ 收尾

    private static void Stop()
    {
        _recording = false;
        int total = _recs.Count;
        Plugin.Log?.LogInfo($"[时间线] ○ 录制结束 —— 共 {_frame} 帧, 见过 {total} 个对象" +
                            (_dropped > 0 ? $", 超上限丢弃 {_dropped} 条事件（不是没有, 是没记）" : ""));

        // ★ 按「存活帧数」升序 —— **只活几帧的那个就是一闪而过的东西**。
        var list = new List<Rec>(_recs.Values);
        list.Sort((a, b) =>
        {
            int c = a.LiveFrames.CompareTo(b.LiveFrames);
            if (c != 0) return c;
            return a.FirstFrame.CompareTo(b.FirstFrame);
        });
        int max = CfgMax?.Value ?? 40;
        int shown = Math.Min(max, list.Count);
        Plugin.Log?.LogInfo("[时间线] ── 存活时间排行（升序: 越靠前 = 越一闪而过）──");
        for (int i = 0; i < shown; i++)
        {
            var r = list[i];
            // ⚠ 插值字符串的对齐格式（`{x,4}`）在本项目的编译环境里一律 CS1739
            //   （FACT.md 只记了负对齐，实测正对齐也一样炸）⇒ 手工补位。
            Plugin.Log?.LogInfo($"[时间线]  {i + 1}. " +
                                $"{("存活 " + r.LiveFrames + " 帧").PadRight(14)}" +
                                $"首现第 {r.FirstFrame} 帧  \"{r.Path}\"{(r.Live ? "  (结束时仍在)" : "")}");
        }
        if (list.Count > shown)
            Plugin.Log?.LogInfo($"[时间线]  …（其余 {list.Count - shown} 个存活更久, 未列 —— " +
                                $"上限 VisTimelineMax={max}；真要看就把上限调大）");
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

    /// <summary>一行说清「这是什么」。⚠ 用 `sharedMaterial`，不是 `material`（后者会**克隆一份材质**）。</summary>
    private static string Summarize(GameObject go)
    {
        var sb = new StringBuilder();
        try
        {
            var r = go.GetComponent<Renderer>();
            if (r != null)
            {
                var mats = r.sharedMaterials;
                int n = 0;
                try { n = mats == null ? 0 : mats.Length; } catch { }
                sb.Append("材质[").Append(n).Append("]=");
                for (int i = 0; i < Math.Min(n, 2); i++)
                {
                    Material m = null;
                    try { m = mats[i]; } catch { }
                    if (i > 0) sb.Append(", ");
                    if (m == null) { sb.Append("(null)"); continue; }
                    sb.Append('"').Append(Reflect.Name(m)).Append('"');
                    try { sb.Append('<').Append(m.shader == null ? "无着色器" : m.shader.name).Append('>'); } catch { }
                }
            }
        }
        catch { }
        return sb.Length == 0 ? "" : sb.ToString();
    }
}
