using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GamePlay;   // BattleBase.Cur.PlayerSelf
using HarmonyLib;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 叠色探针触发器 —— 让 <see cref="RecolorPipeline.DumpRefCounters"/> 能**随时抓**、也能**按动作自动抓**。
///
/// 为什么要有它（用户："在纹章解放那一下抓一次 ReferenceCounterMap，看九个纹章的一致性"）
/// ─────────────────────────────────────────────────────────────────────────
/// 原来那个探针只挂在"`MaterialTinterProxy` 命中"这一条路径上（`if (_refProbed &lt; 3)`），
/// 于是**能不能抓到全看运气**：proxy 没被命中就一条都不出（当前的日志里就是 0 条）。
/// 而我们要问的问题是个**统计问题**——"同一批的九个纹章，记账条目一致吗"——
/// 靠碰运气抓不到，必须能**在指定时刻确定性地抓下来**。
///
/// 两种触发：
///   · `RefCounterKey`（默认 F11）：随时手抓一次。**不受"只打 3 次"限流**（手动必须每次都有）。
///   · 动作窗口：`RefCounterActions` 命中的动作一开始，就**连续抓 N 次**
///     （默认每 0.15s 一次、共 6 次）—— 因为 `counter` / `finalEnd` 会随插值器的启停变化，
///     单帧采样只能看到一个瞬间（本项目在"单帧采样不能当结论"上栽过）。
///
/// ⚠ 两条纪律：
///   · 热键走 IMGUI 事件（本作 `Input.GetKeyDown` 收不到，见 SnapshotProbe 的注释）；
///   · 动作名从每帧心跳里缓存，热路径上不做原生 getter 调用。
/// </summary>
internal static class RefCounterProbe
{
    internal static ConfigEntry<bool> CfgEnabled;
    internal static ConfigEntry<string> CfgKey;
    internal static ConfigEntry<string> CfgActions;
    internal static ConfigEntry<int> CfgSamples;
    internal static ConfigEntry<float> CfgInterval;
    internal static ConfigEntry<float> CfgWindow;

    private static bool _arming;
    private static float _t0;
    private static int _taken;
    private static string _act = "";
    private static string[] _kws;
    private static string _kwsSrc;

    public static int Apply(Harmony harmony)
    {
        int n = 0;
        var bm = AccessTools.TypeByName("GamePlay.BulletMgr");
        var val = bm == null ? null : AccessTools.Method(bm, "ValidateBullets");
        if (val != null)
        {
            harmony.Patch(val, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(RefCounterProbe), nameof(Tick))));
            n++;
        }
        else Plugin.Log?.LogWarning("  [叠色探针触发器] 找不到 BulletMgr.ValidateBullets（心跳没了, 不会自动抓）");

        Plugin.Log?.LogInfo($"  [叠色探针触发器] 就绪 {n} 处; 手抓键={CfgKey?.Value}, " +
                            $"自动抓动作=\"{CfgActions?.Value}\"（进窗口后连抓 {CfgSamples?.Value} 次）");
        return n;
    }

    /// <summary>热键（IMGUI 事件 —— 冻结时也能收到，见 SnapshotProbe）。由 UiHost.OnGUI 调用。</summary>
    public static void OnGui()
    {
        try
        {
            if (CfgEnabled?.Value != true) return;
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;
            if (e.keyCode != HotKey) return;
            e.Use();
            Plugin.Log?.LogInfo($"[叠色探针] 🖱 手动抓取（按 {CfgKey?.Value}）");
            RecolorPipeline.DumpRefCounters("手动抓取");
        }
        catch (Exception ex) { LogEx.Err("RefCounterProbe.OnGui", ex); }
    }

    private static KeyCode HotKey
    {
        get
        {
            var s = (CfgKey?.Value ?? "F11").Trim();
            if (!string.IsNullOrWhiteSpace(s) && Enum.TryParse(s, true, out KeyCode k)) return k;
            return KeyCode.F11;
        }
    }

    public static void Tick()
    {
        try
        {
            if (CfgEnabled?.Value != true) return;
            _act = CurrentPlayerAction();
            bool match = InWindow(_act);
            float now = Time.unscaledTime;

            if (!_arming)
            {
                if (!match) return;
                _arming = true;
                _t0 = now; _taken = 0;
                Plugin.Log?.LogInfo($"[叠色探针] ● 动作命中 \"{_act}\" —— 开始连抓 " +
                                    $"{CfgSamples?.Value} 次（每 {(CfgInterval?.Value ?? 0.15f):0.##}s 一次）");
            }

            float dt = now - _t0;
            float iv = CfgInterval?.Value ?? 0.15f;
            if (iv <= 0f) iv = 0.15f;
            int want = CfgSamples?.Value ?? 6;

            if (_taken < want && dt >= iv * _taken)
            {
                _taken++;
                RecolorPipeline.DumpRefCounters($"自动#{_taken} 动作={_act}");
            }

            float win = CfgWindow?.Value ?? 2.0f;
            if (_taken >= want && (!match || dt > win)) _arming = false;
        }
        catch (Exception e) { Reflect.WarnOnce("refprobe|tick", "叠色探针触发器", e); }
    }

    private static string CurrentPlayerAction()
    {
        try
        {
            var self = BattleBase.Cur?.PlayerSelf;
            if (self == null) return "";
            return self.ActionMgr?.CurrentActionName ?? "";
        }
        catch { return ""; }
    }

    private static bool InWindow(string act)
    {
        if (string.IsNullOrEmpty(act)) return false;
        string src = CfgActions?.Value ?? "holdEX,x2,emblem,crest";
        if (_kws == null || !ReferenceEquals(_kwsSrc, src))
        {
            var list = new List<string>();
            foreach (var p in src.Split(','))
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
}
