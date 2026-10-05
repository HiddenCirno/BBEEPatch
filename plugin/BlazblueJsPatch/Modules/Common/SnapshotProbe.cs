using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 时停 + 现场捕获 —— **把 UnityExplorer 那套行为原样做出来**。
///
/// 用户要求（原话）：
///   「我TM再说一遍我要时停，我不要整个场景，我要去自己捕获那个光弧，
///     就像 UnityExplorer 的行为，一，模，一，样！」
///
/// UE 里到底是哪几件事（对齐着做，不多不少）：
///   · **Time 面板的 timeScale**  → `F5` 循环 1 → 0.1 → 0.02 → 0（时停）；想更细可改 cfg。
///                                  ★ 存在的意义: 光弧只亮零点几秒, 人的反应追不上;
///                                    先慢放到 0.1/0.02, 它就在屏幕上挂好几秒, 从容再停死。
///   · **Stop time（暂停）**       → `F6` 冻结 `Time.timeScale = 0` / 再按恢复。
///                                  ⚠ 默认**不摊整个场景**（用户: 「我不要整个场景」）。
///   · **点谁就知道谁**            → `F7` 把场上渲染器按离鼠标的距离排序, 并把**名字直接画在
///                                  它自己的屏幕位置上**, 连材质/着色器一起。冻结后慢慢看。
///                                  ★ 不用鼠标移来移去: 游戏里光标可能被锁/隐藏, 靠位置点不准;
///                                    画在屏幕上, 你读屏就行。
///
/// ⚠⚠ 冻结的两条坑（都已处理，但要记着）：
///   ① **恢复不了**：上一版把恢复判定挂在 `ValidateBullets`（游戏逻辑帧）上，
///      而 `timeScale=0` 一冻，**逻辑帧自己就停了** ⇒ 收不到按键 ⇒ 卡死。
///      **修法**：恢复判定挂在这里的 `OnGui` —— IMGUI 由**渲染循环**驱动，
///      **不受 timeScale 影响**（暂停菜单就是这么做的）。另有 `SnapshotFreezeMaxSec` 兜底。
///   ② **状态机损坏**：这游戏行动逻辑是时间驱动的，冻结久了/跨动作冻结会打乱时序。
///      这是游戏设计使然，修不掉 —— 所以**冻完记得尽快按回来**。
///
/// ⚠ 另外两条铁律（本项目血泪）：
///   · `Renderer.material` 会**当场克隆一份材质**（会泄漏、也会改变渲染）⇒ 一律用 `sharedMaterial`。
///   · 不跨帧持有任何 Unity 裸指针/包装（对象池会回收复用，为此崩过）⇒ 每帧/每次按键重新取。
/// </summary>
internal static class SnapshotProbe
{
    internal static ConfigEntry<bool> CfgEnabled;
    internal static ConfigEntry<string> CfgKey;
    internal static ConfigEntry<string> CfgTimeKey;
    internal static ConfigEntry<string> CfgTimeSteps;
    internal static ConfigEntry<string> CfgPickKey;
    internal static ConfigEntry<int> CfgPickMax;
    internal static ConfigEntry<bool> CfgHud;
    internal static ConfigEntry<int> CfgMax;

    internal static ConfigEntry<bool> CfgFreeze;
    internal static ConfigEntry<bool> CfgDumpScene;
    internal static ConfigEntry<int> CfgFreezeMaxSec;

    private static bool _frozen;
    private static float _frozenAt;
    private static float _savedScale = 1f;

    // 上一次拾取的结果（用来在屏幕上画标注）—— 只存**字符串和屏幕坐标**，
    // 不存任何 Unity 对象引用（池化对象跨帧引用会崩）。
    private sealed class Mark
    {
        public float X, Y;
        public string Text = "";
    }
    private static readonly List<Mark> _marks = new List<Mark>();

    public static int Apply(Harmony harmony)
    {
        int n = 0;
        try
        {
            var names = new List<string>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string nm = null;
                try { nm = asm.GetName().Name; } catch { }
                if (nm != null && nm.Equals("Il2Cppmscorlib", StringComparison.OrdinalIgnoreCase))
                {
                    string loc = "";
                    try { loc = asm.Location ?? ""; } catch { }
                    names.Add(loc.Length > 0 ? loc : "(无路径: 动态/内存程序集)");
                }
            }
            Plugin.Log?.LogInfo($"[时停] Il2Cppmscorlib 份数 = {names.Count}" +
                                (names.Count > 0 ? "；来源: " + string.Join(" | ", names.ToArray()) : ""));
        }
        catch (Exception e) { LogEx.Err("SnapshotProbe.ProbeAssemblies", e); }

        // ⚠ 本模块**不挂任何 Hook**（全靠 IMGUI 事件 + FindObjectsOfType），所以 n 恒为 0，
        //   这里的就绪行必须无条件打 —— 否则挂载表里会显示"已挂载(0 处 Hook)"却没有任何说明，
        //   看起来像坏了（本项目纪律: 每个"没有"都要说清为什么）。
        Plugin.Log?.LogInfo($"  [时停] 就绪（IMGUI 驱动, 无需 Hook）; " +
                            $"时停={CfgKey?.Value}, 慢放循环={CfgTimeKey?.Value}, 捕获={CfgPickKey?.Value}" +
                            $"（冻结时这几个键照样有效 —— 走 IMGUI, 不受 timeScale 影响）");
        return 0;
    }

    /// <summary>
    /// ⚠⚠ 热键**必须走 IMGUI 事件**，不能用 `Input.GetKeyDown`。
    ///   实测：本作里 `Input.GetKeyDown(F6)` 收不到（用户："F6 怎么不好使了"），
    ///   而 F8 配置面板 / F9 弹幕实验台走的是 `Event.current.type == KeyDown`。
    ///   再加上 `InputBlocker` 会在面板打开时拦住游戏的 `GetKey*`。
    /// ★ 而且这里是**唯一能在 `timeScale = 0` 时收到按键**的地方（见类注释①）。
    ///   由 `UiHost.OnGUI` 每帧调用。
    /// </summary>
    public static void OnGui()
    {
        try
        {
            if (CfgEnabled?.Value != true) return;
            var e = Event.current;
            if (e == null) return;

            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == HotKey) { e.Use(); Fire(); return; }
                if (e.keyCode == TimeKey) { e.Use(); CycleTimeScale(); return; }
                if (e.keyCode == PickKey) { e.Use(); Pick(); return; }
            }
            FreezeWatchdog();
            DrawHud(e);
        }
        catch (Exception ex) { LogEx.Err("SnapshotProbe.OnGui", ex); }
    }

    private static KeyCode ParseKey(ConfigEntry<string> cfg, KeyCode dflt)
    {
        var s = (cfg?.Value ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(s) && Enum.TryParse(s, true, out KeyCode k)) return k;
        return dflt;
    }

    private static KeyCode HotKey => ParseKey(CfgKey, KeyCode.F6);
    private static KeyCode TimeKey => ParseKey(CfgTimeKey, KeyCode.F5);
    private static KeyCode PickKey => ParseKey(CfgPickKey, KeyCode.F7);

    // ------------------------------------------------------------------ 时间

    /// <summary>
    /// 唯一的 timeScale 出口 —— 所有改动都从这里过，保证 `_frozen` / `_savedScale` 与真实值一致。
    /// </summary>
    private static void ApplyTimeScale(float v, string why)
    {
        float old = 1f;
        try { old = Time.timeScale; } catch { }
        try { Time.timeScale = v; } catch (Exception e) { LogEx.Err("SnapshotProbe.ApplyTimeScale", e); return; }

        if (v > 0.0001f) _savedScale = v;
        bool wasFrozen = _frozen;
        _frozen = v <= 0.0001f;
        if (_frozen && !wasFrozen) { try { _frozenAt = Time.unscaledTime; } catch { } }
        if (!_frozen && wasFrozen) _marks.Clear();   // 解冻 = 场景重新动起来, 旧标注会错位, 清掉

        string tag = _frozen ? "⏸ 时停" : (v < 1f ? $"🐢 慢放 ×{v:0.###}" : "▶ 正常");
        Plugin.Log?.LogInfo($"[时停] {tag} —— timeScale {old:0.###} -> {v:0.###}（{why}）");
    }

    /// <summary>F5：在 `SnapshotTimeSteps` 里循环（默认 1 → 0.1 → 0.02 → 0）。</summary>
    private static void CycleTimeScale()
    {
        var steps = TimeSteps();
        if (steps.Length == 0) return;
        float cur = 1f;
        try { cur = Time.timeScale; } catch { }
        int idx = 0;
        float best = float.MaxValue;
        for (int i = 0; i < steps.Length; i++)
        {
            float d = Math.Abs(steps[i] - cur);
            if (d < best) { best = d; idx = i; }
        }
        int next = (idx + 1) % steps.Length;
        ApplyTimeScale(steps[next], $"循环 {idx + 1}/{steps.Length} -> {next + 1}/{steps.Length}");
    }

    private static float[] _steps;
    private static string _stepsSrc;

    private static float[] TimeSteps()
    {
        string src = CfgTimeSteps?.Value ?? "1,0.1,0.02,0";
        if (_steps == null || !ReferenceEquals(_stepsSrc, src))
        {
            var list = new List<float>();
            foreach (var p in src.Split(','))
            {
                if (float.TryParse(p.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float f))
                    list.Add(f);
            }
            _steps = list.ToArray();
            _stepsSrc = src;
        }
        return _steps;
    }

    // ------------------------------------------------------------------ 冻结 / 快照

    private static void Fire()
    {
        try
        {
            if (CfgEnabled?.Value != true) return;
            if (CfgFreeze?.Value != true)
            {
                Plugin.Log?.LogInfo($"[时停] 当前 SnapshotFreeze=false（只摊场景不冻结）—— 按 {CfgKey?.Value} 摊开");
                DumpScene();
                return;
            }
            if (!_frozen)
            {
                ApplyTimeScale(0f, $"按 {CfgKey?.Value} 冻结");
                if (CfgDumpScene?.Value == true) DumpScene();
                else Plugin.Log?.LogInfo($"[时停] （默认不摊整个场景；要摊就把 SnapshotDumpScene 开成 true）" +
                                        $"，现在可以用 {PickKey} 捕获光标附近的东西");
            }
            else ApplyTimeScale(_savedScale <= 0f ? 1f : _savedScale, $"按 {CfgKey?.Value} 恢复");
        }
        catch (Exception e) { LogEx.Err("SnapshotProbe.Fire", e); }
    }

    /// <summary>
    /// 自动恢复兜底 —— 防「冻住之后再也解不开」。
    /// ⚠ 挂在 `OnGui` 上（每帧的 GUI 回调），**不受 timeScale 影响**；
    ///   挂在游戏逻辑帧上就等于没有（逻辑帧自己都被冻停了）—— 上一版就是这么卡死的。
    /// </summary>
    private static void FreezeWatchdog()
    {
        if (!_frozen) return;
        int cap = CfgFreezeMaxSec?.Value ?? 0;
        if (cap <= 0) return;
        float now;
        try { now = Time.unscaledTime; } catch { return; }
        if (now - _frozenAt > cap) ApplyTimeScale(_savedScale <= 0f ? 1f : _savedScale, $"超时 {cap}s 兜底");
    }

    // ------------------------------------------------------------------ 捕获

    private sealed class Cand
    {
        public float D;          // 到鼠标的屏幕距离(px)
        public Vector3 Sp;       // 屏幕坐标(y 向上, 与 Input.mousePosition 同向)
        public string Path = "";
        public string Info = "";
    }

    /// <summary>
    /// F7 —— 「点谁就知道谁」。
    /// 把场上每个渲染器的世界包围盒中心投到屏幕坐标，按离鼠标的距离排序，
    /// 并把**名字直接画在那个位置上**（冻结时场景不动，标注就稳稳贴在物体上）。
    /// ★ 之所以画在屏幕上而不是只认"鼠标底下那一个"：游戏里光标可能被锁/隐藏，
    ///   而且光弧只有几像素宽，靠鼠标点根本点不准 —— 画出来读屏最省事。
    /// </summary>
    private static void Pick()
    {
        try
        {
            int max = CfgPickMax?.Value ?? 24;

            var cam = MainCamera();
            if (cam == null)
            {
                Plugin.Log?.LogInfo("[时停] ✗ 场上没有可用相机（Camera.main 为空且 allCameras 也是空），本次放弃");
                return;
            }

            Vector2 mouse;
            string mouseSrc;
            TryMouse(out mouse, out mouseSrc);

            int sw = 0, sh = 0;
            try { sw = Screen.width; sh = Screen.height; } catch { }
            string camName = "";
            try { camName = Reflect.Name(cam); } catch { }
            Plugin.Log?.LogInfo($"[时停] 🖱 捕获: 鼠标=({mouse.x:0},{mouse.y:0}) [{mouseSrc}]  " +
                                $"屏幕={sw}x{sh}  相机=\"{camName}\"  timeScale={Time.timeScale:0.###}");

            var cands = new List<Cand>();
            int scanned = 0, offScreen = 0, threw = 0;

            var arr = UnityEngine.Object.FindObjectsOfType(
                Il2CppInterop.Runtime.Il2CppType.From(typeof(Renderer)));
            foreach (var o in Reflect.Items(arr))
            {
                var r = Reflect.Cast<Renderer>(o);
                if (r == null) continue;
                scanned++;
                try
                {
                    var go = r.gameObject;
                    if (go == null) continue;
                    // ⚠ sharedMaterial, 不是 material —— 后者会克隆材质。
                    Material m = null;
                    try { m = r.sharedMaterial; } catch { }
                    if (m == null) continue;                       // 没材质的跳过（不可能是那道弧）

                    var b = r.bounds;
                    var sp = cam.WorldToScreenPoint(b.center);
                    if (sp.z <= 0f) { offScreen++; continue; }      // 在相机背后
                    if (sp.x < -200f || sp.x > sw + 200f || sp.y < -200f || sp.y > sh + 200f)
                    { offScreen++; continue; }                      // 屏幕外老远（比如 UI/天空盒）

                    float dx = sp.x - mouse.x, dy = sp.y - mouse.y;
                    var c = new Cand
                    {
                        D = Mathf.Sqrt(dx * dx + dy * dy),
                        Sp = sp,
                        Path = PathOf(go),
                    };
                    string shName = "";
                    try { shName = m.shader == null ? "无着色器" : m.shader.name; } catch { }
                    c.Info = $"材质\"{Reflect.Name(m)}\"<{shName}>";
                    cands.Add(c);
                }
                catch { threw++; }
            }

            cands.Sort((a, b) => a.D.CompareTo(b.D));
            Plugin.Log?.LogInfo($"[时停] 扫过渲染器 {scanned} 个 → 屏幕上候选 {cands.Count} 个" +
                                $"（屏幕外/背后 {offScreen}, 读取异常 {threw}）");

            _marks.Clear();
            int shown = Math.Min(max, cands.Count);
            for (int i = 0; i < shown; i++)
            {
                var c = cands[i];
                int guiY = sh - (int)c.Sp.y;                       // 屏幕坐标(y上) -> GUI坐标(y下)
                string line = $"#{i + 1} {c.Path}";
                _marks.Add(new Mark { X = c.Sp.x, Y = guiY, Text = line });
                // ⚠ 插值字符串的对齐格式（`{x,5}`）在本项目的编译环境里一律报 CS1739
                //   （FACT.md 只记了负对齐 `{-7}`，实测**正对齐也一样炸**）⇒ 手工补位。
                Plugin.Log?.LogInfo($"[时停]   #{i + 1}  {("距鼠标 " + ((int)c.D) + "px").PadRight(14)}  " +
                                    $"屏({c.Sp.x:0},{c.Sp.y:0})  \"{c.Path}\"  {c.Info}");
            }
            if (cands.Count > shown)
                Plugin.Log?.LogInfo($"[时停]   …（其余 {cands.Count - shown} 个更远, 未列；上限 SnapshotPickMax={max}）");
            Plugin.Log?.LogInfo($"[时停] ✓ 已把 #{1}-{shown} 的名字标到屏幕上对应位置（冻结时不会错位）。" +
                                $"**看得见的那道弧上压着的那个编号，就是它。**");
        }
        catch (Exception e) { LogEx.Err("SnapshotProbe.Pick", e); }
    }

    // ------------------------------------------------------------------ 屏幕标注 / HUD

    private static GUIStyle _markStyle;
    private static GUIStyle _hudStyle;

    private static void DrawHud(Event e)
    {
        if (CfgHud?.Value != true) return;
        if (e.type != EventType.Repaint) return;      // 只在重绘事件里画, 免得 Layout 事件报错

        try
        {
            if (_hudStyle == null)
            {
                _hudStyle = new GUIStyle(GUI.skin.label);
                _hudStyle.fontSize = 14;
                _hudStyle.normal.textColor = Color.yellow;
            }
            if (_markStyle == null)
            {
                _markStyle = new GUIStyle(GUI.skin.label);
                _markStyle.fontSize = 12;
                _markStyle.normal.textColor = Color.white;
            }

            string state = _frozen ? "⏸ 已时停" : (Time.timeScale < 1f ? $"🐢 ×{Time.timeScale:0.###}" : "▶ 正常");
            GUI.Label(new Rect(8f, 8f, 900f, 20f),
                $"[BBEE] timeScale={Time.timeScale:0.###} {state}   " +
                $"{TimeKey}=慢放循环  {HotKey}=时停/恢复  {PickKey}=捕获(把名字标到屏幕上)",
                _hudStyle);

            if (_marks.Count > 0)
            {
                var old = GUI.color;
                foreach (var m in _marks)
                {
                    var size = _markStyle.CalcSize(new GUIContent(m.Text));
                    // 标在物体中心**偏右上**, 免得盖住目标本身
                    var rect = new Rect(m.X + 6f, m.Y - size.y - 6f, size.x + 8f, size.y + 4f);
                    GUI.color = new Color(0f, 0f, 0f, 0.65f);
                    GUI.DrawTexture(rect, Texture2D.whiteTexture);
                    GUI.color = old;
                    GUI.Label(new Rect(rect.x + 4f, rect.y + 2f, size.x, size.y), m.Text, _markStyle);
                }
            }
        }
        catch (Exception ex) { Reflect.WarnOnce("snap|hud", "时停 HUD 绘制", ex); }
    }

    // ------------------------------------------------------------------ 场景摊开（可选, 默认关）

    private static void DumpScene()
    {
        int max = CfgMax?.Value ?? 24;
        DumpType<NOAH.VFX.VFXEffectHub>("特效 hub", max);
        // ⚠ `ActorVisualBase` 在**全局命名空间**里（dump.cs 实测 Namespace 为空），
        //   不是 `GamePlay.ActorVisualBase` —— 写成后者会编译不过。
        DumpType<ActorVisualBase>("角色视觉件", max);
    }

    private static void DumpType<T>(string label, int max) where T : Il2CppObjectBase
    {
        try
        {
            var arr = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.From(typeof(T)));
            int total = 0, done = 0, skipped = 0, hitCap = 0;
            var seen = new HashSet<string>();
            foreach (var o in Reflect.Items(arr))
            {
                var c = Reflect.Cast<T>(o);
                if (c == null) continue;
                total++;
                var go = Reflect.Cast<MonoBehaviour>(c)?.gameObject;
                if (go == null) continue;

                // ⚠⚠ 去重键必须用**层级路径**，不能只用名字。
                //   实测: 场上 20 个角色视觉件**全叫 "Renderer"**，按名字去重会被压成 1 个。
                string path = PathOf(go);
                if (!seen.Add(path)) { skipped++; continue; }
                if (done >= max) { hitCap++; continue; }
                done++;
                Plugin.Log?.LogInfo($"[时停] ── {label} \"{path}\" ──");
                DashAnatomy.DumpPublic(go, $"时停 ▸ {label} \"{path}\"");
            }
            string tail = "";
            if (skipped > 0) tail += $", 路径重复跳过 {skipped}";
            if (hitCap > 0) tail += $", 超上限 {max} 未列 {hitCap}";
            Plugin.Log?.LogInfo($"[时停] {label}: 场上 {total} 个, 摊开 {done} 个{tail}" +
                                (tail.Length > 0 ? "（都是明确计数的, 不是静默丢）" : ""));
        }
        catch (Exception e) { LogEx.Err("SnapshotProbe.DumpType/" + label, e); }
    }

    // ------------------------------------------------------------------ 工具

    /// <summary>向上拼出层级路径（最多 8 层）—— 用来区分同名对象（「Renderer」满场都是）。</summary>
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

    /// <summary>
    /// 取鼠标位置。两条路都试：
    ///   · `Input.mousePosition` —— 与 `WorldToScreenPoint` 同坐标系（左下原点），首选；
    ///     但游戏可能把光标锁住/隐藏，那时它**不跟着手走**。
    ///   · IMGUI 的 `Event.current.mousePosition` —— **左上原点**，要用屏幕高翻转。
    /// ⚠ 正因为鼠标可能不可靠，捕获结果**同时画在屏幕上**，不依赖光标。
    /// </summary>
    private static bool TryMouse(out Vector2 mouse, out string src)
    {
        mouse = Vector2.zero; src = "";
        try
        {
            var p = Input.mousePosition;
            if (p.x != 0f || p.y != 0f) { mouse = new Vector2(p.x, p.y); src = "Input"; return true; }
        }
        catch { }
        try
        {
            var e = Event.current;
            if (e != null)
            {
                float h = 0f;
                try { h = Screen.height; } catch { }
                mouse = new Vector2(e.mousePosition.x, h - e.mousePosition.y);
                src = "IMGUI(已翻转y)";
                return true;
            }
        }
        catch { }
        return false;
    }

    private static Camera MainCamera()
    {
        try { var c = Camera.main; if (c != null) return c; } catch { }
        try
        {
            foreach (var o in Reflect.Items(Camera.allCameras))
            {
                var c = Reflect.Cast<Camera>(o);
                if (c != null) return c;
            }
        }
        catch { }
        return null;
    }
}
