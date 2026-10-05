using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BlazblueJsPatch;

/// <summary>
/// 临时诊断：追踪「时缓」是从哪儿调起来的。
///
/// 背景
/// ────
/// 「极限闪避」（= 完美闪避）的时缓效果 = `BattleBase.Cur.BattleTimeControl.SetSlowMotion(...)`。
/// 反查它的 9 个调用点发现：全部是 JS 封装 + BuffFuncsJs 的 lambda，
/// **没有一个是核心战斗路径** —— 说明时缓是 buff 驱动的，光看静态调用图找不到入口。
///
/// 所以改成运行时抓：把 SetSlowMotion / ShowSlow 挂上 Postfix，
/// 打印参数 + 托管调用栈。你触发一次原版极限闪避，日志里就会直接出现
/// 「是哪个方法在放时缓」。
///
/// 类型解析全部走运行时，不硬编命名空间。
/// </summary>
internal static class SlowMotionTrace
{
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;

    private static bool Enabled => CfgEnabled?.Value ?? true;
    private static int _logged;
    private const int MaxLog = 40;

    public static int Apply(Harmony harmony)
    {
        int n = 0;
        n += Hook(harmony, "GamePlay.BattleTimeControl", "SetSlowMotion", nameof(SetSlowMotionPostfix));
        n += Hook(harmony, "JsPort.ActorFuncUtils", "ShowSlow", nameof(ShowSlowPostfix));
        if (n > 0)
            Plugin.Log.LogInfo($"  [时缓追踪] 已挂钩 {n} 处 (关闭: [冲刺无敌] TraceSlowMotion = false)");
        return n;
    }

    private static int Hook(Harmony harmony, string typeName, string methodName, string postfixName)
    {
        var t = ResolveType(typeName);
        if (t == null) { Plugin.Log.LogWarning($"  [时缓追踪] 找不到类型 {typeName}"); return 0; }

        var m = AccessTools.Method(t, methodName);
        if (m == null) { Plugin.Log.LogWarning($"  [时缓追踪] 找不到 {typeName}.{methodName}"); return 0; }

        try
        {
            harmony.Patch(m, postfix: new HarmonyMethod(
                typeof(SlowMotionTrace).GetMethod(postfixName, BindingFlags.Static | BindingFlags.Public)));
            Plugin.Log.LogInfo($"  [时缓追踪] 已挂钩 {t.FullName}.{methodName}");
            return 1;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"  [时缓追踪] 挂钩 {typeName}.{methodName} 失败: {e.Message}");
            return 0;
        }
    }

    /// <summary>按简名扫描，不依赖命名空间。</summary>
    private static Type ResolveType(string fullName)
    {
        var t = AccessTools.TypeByName(fullName);
        if (t != null) return t;

        var simple = fullName.Substring(fullName.LastIndexOf('.') + 1);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] ts;
            try { ts = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { ts = e.Types; }
            catch { continue; }
            if (ts == null) continue;

            var hit = ts.FirstOrDefault(x => x != null && x.Name == simple);
            if (hit != null) return hit;
        }
        return null;
    }

    public static void SetSlowMotionPostfix(object[] __args)
    {
        try
        {
            if (!Enabled || _logged >= MaxLog) return;
            _logged++;
            var a = string.Join(", ", __args.Select(Describe));
            Plugin.Log?.LogInfo($"[时缓] SetSlowMotion({a})\n{Stack()}");
        }
        catch (Exception e) { Plugin.Log?.LogError($"[时缓] 追踪异常: {e.Message}"); }
    }

    public static void ShowSlowPostfix(object[] __args)
    {
        try
        {
            if (!Enabled || _logged >= MaxLog) return;
            _logged++;
            var a = string.Join(", ", __args.Select(Describe));
            Plugin.Log?.LogInfo($"[时缓] ShowSlow({a})\n{Stack()}");
        }
        catch (Exception e) { Plugin.Log?.LogError($"[时缓] 追踪异常: {e.Message}"); }
    }

    private static string Describe(object o)
    {
        if (o == null) return "null";
        try
        {
            // Fp 是定点数, ToString 能给出数值
            return $"{o.GetType().Name}({o})";
        }
        catch { return o.GetType().Name; }
    }

    /// <summary>取托管调用栈, 只保留前若干帧, 并挤掉 Harmony/BepInEx 自身的噪声。</summary>
    private static string Stack()
    {
        try
        {
            var st = new StackTrace(false);
            var sb = new StringBuilder("    调用栈:");
            int shown = 0;
            foreach (var f in st.GetFrames() ?? Array.Empty<StackFrame>())
            {
                var m = f.GetMethod();
                if (m == null) continue;
                var decl = m.DeclaringType?.FullName ?? "?";
                if (decl.StartsWith("HarmonyLib") || decl.StartsWith("BepInEx") ||
                    decl.StartsWith("BlazblueJsPatch") || decl.StartsWith("System.") ||
                    decl.StartsWith("Il2CppInterop")) continue;

                sb.Append("\n      <- ").Append(decl).Append('.').Append(m.Name);
                if (++shown >= 8) break;
            }
            return shown == 0 ? "    调用栈: (只剩框架帧)" : sb.ToString();
        }
        catch (Exception e) { return $"    调用栈读取失败: {e.Message}"; }
    }
}
