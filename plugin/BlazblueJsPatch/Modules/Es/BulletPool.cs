using System;
using BepInEx.Configuration;
using HarmonyLib;

namespace BlazblueJsPatch;

/// <summary>
/// 弹幕池扩容。
///
/// 依据（`dump.cs` → `GamePlay.BulletMgr`）
/// ────────────────────────────────────
///   public void PreCreateBullet(int normal, int spine, int model3d = 0)   // 场景/战斗开始时预建
///   public BulletObj PreCreateBulletImpl(ActionLogicGroup.EVisualType visualType)
///   public BulletObj GetFromPoolOrCreate(ActionLogicGroup logicGroup)
///   public bool RecycleBullet(BulletObj bullet)
/// 即：**池是"预热"出来的**，池子大小 = 这里传进来的数量（按视觉类型：normal / spine / 3D）。
/// 所以扩容不需要碰任何数据 —— 在 `PreCreateBullet` 的 prefix 里把数量放大即可。
///
/// ⚠ 说清楚它【不解决】什么（免得期待错）
/// ─────────────────────────────────
/// 我们的 `_movers` / `_fixes` 持有的是**裸引用**（IntPtr/托管包装）。
/// 弹幕被回收复用之后，那个引用就指到**别人的弹幕**上了 —— 池子再大，这只是让
/// "回收得太早"**发生得更少**，不是根治。根治只有两条：
///   ① 按时撒手（现有 `PinSeconds` / `MoveSeconds` / `StartFlySeconds` 就是干这个的）
///   ② 每帧校验"它还是不是我那条"（`_fixes` 里的"换主人检测"是这个思路）
/// 扩池的真实收益：连发/高速连段时**不再频繁触发即时分配**（`GetFromPoolOrCreate` 的
/// new + Init 路径），减少卡顿和"刚出生就被回收"的窗口。
///
/// ⚠ 原始数量一定要打出来：不打的话"到底扩了多少"永远说不清，
///   而"配了没生效"和"本来就是这么大"在日志里长得一模一样（本项目踩过）。
/// </summary>
internal static class BulletPool
{
    internal static ConfigEntry<bool> CfgEnabled;
    internal static ConfigEntry<float> CfgScale;
    internal static ConfigEntry<bool> CfgLog;

    private static int _logged;

    public static int Apply(Harmony harmony)
    {
        int n = 0;
        if (CfgEnabled?.Value != true) return 0;
        try
        {
            var t = AccessTools.TypeByName("GamePlay.BulletMgr");
            if (t == null) { Plugin.Log?.LogWarning("  [弹幕池] 找不到 GamePlay.BulletMgr"); return 0; }

            // 默认参数 (int, int, int = 0) —— Harmony 要精确签名，所以三个都写出来
            var m = AccessTools.Method(t, "PreCreateBullet", new[] { typeof(int), typeof(int), typeof(int) })
                 ?? AccessTools.Method(t, "PreCreateBullet");
            if (m == null) { Plugin.Log?.LogWarning("  [弹幕池] 找不到 BulletMgr.PreCreateBullet"); return 0; }

            harmony.Patch(m, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(BulletPool), nameof(Pre))));
            Plugin.Log.LogInfo($"  [弹幕池] 已挂钩 BulletMgr.PreCreateBullet (扩容 x{(CfgScale?.Value ?? 1f):F2})");
            n++;
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [弹幕池] 挂钩失败: {e.Message}"); }
        return n;
    }

    /// <summary>把预创建数量按倍率放大。参数用 ref 改，就是改这一批要建多少个。</summary>
    public static void Pre(ref int normal, ref int spine, ref int model3d)
    {
        try
        {
            if (CfgLog?.Value != false && _logged < 20)
            {
                _logged++;
                Plugin.Log?.LogInfo($"[弹幕池] 预创建请求: normal={normal} spine={spine} model3d={model3d}");
            }

            float k = CfgScale?.Value ?? 1f;
            if (k <= 1f || float.IsNaN(k) || float.IsInfinity(k)) return;

            int n0 = normal, s0 = spine, m0 = model3d;
            normal = Scale(normal, k);
            spine = Scale(spine, k);
            model3d = Scale(model3d, k);

            if (CfgLog?.Value != false && _logged < 20)
                Plugin.Log?.LogInfo($"[弹幕池] 已放大: {n0}/{s0}/{m0} -> {normal}/{spine}/{model3d}  (x{k:F2})");
        }
        catch (Exception e) { Plugin.Log?.LogError($"[弹幕池] Pre 异常: {e.Message}"); }
    }

    /// <summary>0 保持 0（"这类不预热"是个有意义的取值，不该被放大成 1）。</summary>
    private static int Scale(int v, float k)
    {
        if (v <= 0) return v;
        long r = (long)Math.Round(v * (double)k);
        if (r < 1) r = 1;
        if (r > 100000) r = 100000;      // 上限护栏：配置手滑别把启动卡死
        return (int)r;
    }
}
