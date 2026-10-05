using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using GamePlay;   // BattleBase.Cur.PlayerSelf —— 本地玩家
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 实例化探针 —— 盯住**最底层的对象出生口**，看一个动作到底造出了什么东西。
///
/// 为什么要有它（用户原话："看 dashSkill 和 dashSkill2 的过程里到底发生了什么，
/// 它是怎么被播放的，它不一定是粒子、弹幕或者 VFX，但一定有东西把它实例化了出来"）
/// ─────────────────────────────────────────────────────────────────────────
/// 我们前面挂的出生口只有两个：`ActorEffectMgr.createVisualEffect`（特效）和
/// `BulletMgr.createBulletImp`（弹幕）。**如果那道闪光/圆弧既不叫特效也不叫弹幕** ——
/// 比如它是直接 `Object.Instantiate` 出来的、或者是某个视觉件被 SetActive/Play 打开 ——
/// 那两个口子**一个都看不见它**（日志里干干净净，看起来像"什么都没发生"）。
///
/// 所以这里改盯**万物必经的那一关**：`UnityEngine.Object.Instantiate`（所有重载 + 泛型实例化）。
/// 不管造出来的是粒子、Spine、Mesh、材质、UI 还是随便什么，都得从这儿过。
///
/// ⚠ 三条纪律（否则必然卡爆 —— Instantiate 每秒可能被调上千次）：
///   1. **只在动作窗口内记录**：动作名缓存在每帧心跳里刷新（不在热路径上读原生 getter），
///      热路径只做一次字符串比较；
///   2. **按"动作|对象名"去重**：同一动作下每种对象只报一次；
///   3. **硬上限 + 超了明说**（不静默丢）。
/// ⚠ 绝不用 `__args`（struct 参数装箱 ⇒ 无效指针 ⇒ 原生崩溃，本项目栽过）。
/// </summary>
internal static class InstantiateProbe
{
    internal static ConfigEntry<bool> CfgEnabled;
    internal static ConfigEntry<string> CfgActions;
    internal static ConfigEntry<int> CfgMax;
    internal static ConfigEntry<bool> CfgParticles;

    private static readonly HashSet<string> _seen = new HashSet<string>();
    private static int _lines;
    private static string _act = "";          // 每帧刷新一次的动作名（热路径只读它）
    private static bool _warnedCap;

    // ------------------------------------------------------------------ 挂载

    public static int Apply(Harmony harmony)
    {
        int n = 0;
        var obj = AccessTools.TypeByName("UnityEngine.Object");
        if (obj == null) { Plugin.Log?.LogWarning("  [实例化探针] 找不到 UnityEngine.Object"); return 0; }
        var post = new HarmonyMethod(AccessTools.Method(typeof(InstantiateProbe), nameof(InstPostfix)));
        foreach (var m in obj.GetMethods(Reflect.All))
        {
            if (m.Name != "Instantiate" || !m.IsStatic) continue;
            if (m.ReturnType == null || m.ReturnType.IsValueType) continue;   // 只挂返回引用类型的
            try
            {
                harmony.Patch(m, postfix: post);
                n++;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"  [实例化探针] 挂 Instantiate 失败({m.Name}/{m.GetParameters().Length}参): {e.Message}");
            }
        }

        // 有些东西不是"造出来"的，而是"打开"的 —— 粒子系统被 Play 是典型。
        if (CfgParticles?.Value != false)
        {
            var ps = AccessTools.TypeByName("UnityEngine.ParticleSystem");
            var play = ps == null ? null : AccessTools.Method(ps, "Play", new Type[0]);
            if (play != null)
            {
                try
                {
                    harmony.Patch(play, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(InstantiateProbe), nameof(PsPlayPostfix))));
                    n++;
                }
                catch (Exception e) { Plugin.Log?.LogWarning($"  [实例化探针] 挂 ParticleSystem.Play 失败: {e.Message}"); }
            }
        }

        // 心跳：每帧刷一次"当前动作"，顺手把该说的说了。
        var bm = AccessTools.TypeByName("GamePlay.BulletMgr");
        var val = bm == null ? null : AccessTools.Method(bm, "ValidateBullets");
        if (val != null)
        {
            harmony.Patch(val, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(InstantiateProbe), nameof(Tick))));
            n++;
        }

        if (n > 0)
            Plugin.Log?.LogInfo($"  [实例化探针] 就绪 {n} 处; 监听动作关键字=\"{CfgActions?.Value}\"");
        return n;
    }

    // ------------------------------------------------------------------ 心跳

    /// <summary>每帧一次：刷新当前动作（从这里读原生 getter，热路径上就不读了）。</summary>
    public static void Tick()
    {
        try
        {
            if (CfgEnabled?.Value != true) return;
            _act = CurrentPlayerAction();
        }
        catch { }
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

    /// <summary>
    /// 关键字缓存。
    ///
    /// ⚠⚠ 这里是**热路径**（`Instantiate` 每秒可能被调上千次）。
    ///   上一版直接写 `Cfg.List(...)` —— 那玩意**每次调用都 Split 出一个新数组**，
    ///   于是每一帧几百次分配 ⇒ GC 风暴 ⇒ 用户实测"卡卡卡卡卡"。
    ///   现在只在 cfg 字符串**变化时**才重新切，平时就是一次字符串比较。
    ///   （教训：热路径上**任何**会分配的东西都得缓存，包括看起来人畜无害的 Cfg.List。）
    /// </summary>
    private static string[] _kws;
    private static string _kwsSrc;

    private static string[] Keywords()
    {
        string src = CfgActions?.Value ?? "dashSkill,dash";
        if (_kws == null || !ReferenceEquals(_kwsSrc, src))
        {
            _kws = Cfg.List(src);
            _kwsSrc = src;
        }
        return _kws;
    }

    private static bool InWindow()
    {
        if (CfgEnabled?.Value != true) return false;
        string act = _act;
        if (string.IsNullOrEmpty(act)) return false;
        var kws = Keywords();
        for (int i = 0; i < kws.Length; i++)
        {
            var k = kws[i];
            if (k != null && k.Length > 0 && act.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ 出生口

    /// <summary>⚠ 不用 `__args`（struct 装箱 ⇒ 无效指针 ⇒ 原生崩溃）。`__result` 已经够定位了 ——
    /// 克隆出来的名字通常就带着原名（`xxx(Clone)`）。</summary>
    public static void InstPostfix(object __result)
    {
        try
        {
            if (__result == null || !InWindow()) return;
            Report(__result, "Instantiate", _act, RootOf(__result));
        }
        catch { }
    }

    /// <summary>
    /// `ParticleSystem.Play` —— **这里现场读动作名，不用缓存**。
    ///
    /// ⚠ 为什么：Play 的调用频率很低（一次特效开播才几次），而**每帧刷新的缓存会漏** ——
    ///   实测 MP 冲刺期间一条都没记到（特效是池化复用的，冲刺时只是"重新激活/播放"，
    ///   而缓存在那一帧可能还没刷成 dashSkill）⇒ 差集算出来是空的, 白跑一轮。
    ///   热路径纪律只对 `Instantiate` 那种"每秒上千次"的口子适用。
    /// 顺带把**根物体名**一起记 —— 这样能直接看出"这个粒子属于哪只特效"。
    /// </summary>
    public static void PsPlayPostfix(object __instance)
    {
        try
        {
            if (__instance == null || CfgEnabled?.Value != true) return;
            string act = CurrentPlayerAction();
            if (!MatchAct(act)) return;
            Report(__instance, "ParticleSystem.Play", act, RootOf(__instance));
        }
        catch { }
    }

    private static bool MatchAct(string act)
    {
        if (string.IsNullOrEmpty(act)) return false;
        var kws = Keywords();
        for (int i = 0; i < kws.Length; i++)
        {
            var k = kws[i];
            if (k != null && k.Length > 0 && act.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    /// <summary>沿父链找到根物体名（最多 8 层）—— 用来判断"这个粒子属于哪只特效"。</summary>
    private static string RootOf(object comp)
    {
        try
        {
            var mb = Reflect.Cast<MonoBehaviour>(comp);
            var t = mb?.transform;
            for (int i = 0; i < 8 && t != null; i++)
            {
                var p = t.parent;
                if (p == null) return Reflect.Name(t.gameObject);
                t = p;
            }
        }
        catch { }
        return "?";
    }

    private static void Report(object o, string how, string act, string root)
    {
        var uo = Reflect.Cast<UnityEngine.Object>(o);
        if (uo == null) return;
        string nm = Reflect.Name(uo);
        if (nm.Length == 0) return;
        act = string.IsNullOrEmpty(act) ? _act : act;

        string key = act + "|" + nm;
        if (!_seen.Add(key)) return;

        int max = CfgMax?.Value ?? 120;
        if (_lines >= max)
        {
            if (!_warnedCap)
            {
                _warnedCap = true;
                Plugin.Log?.LogInfo($"[实例化探针] 已达上限 {max} 种（动作 {_act}）—— " +
                                    $"这是上限, 不是没生成; 要看得更多就把 InstantiateProbeMax 调大");
            }
            return;
        }
        _lines++;
        IntPtr p = IntPtr.Zero;
        try { p = ((Il2CppObjectBase)o).Pointer; } catch { }
        Plugin.Log?.LogInfo($"[实例化探针] 动作=\"{act}\"  {how}  \"{nm}\"  类型={Reflect.KlassName(p)}" +
                            $"  根=\"{root}\"  (第 {_lines}/{max} 种)");
    }
}
