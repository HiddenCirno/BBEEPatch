using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;

namespace BlazblueJsPatch;

/// <summary>
/// 模块注册表 —— 顺序、开关、结果。
///
/// 挂载顺序是有意义的(按依赖排), 所以列表本身就是要维护的东西, 而不是一个 Dictionary。
/// 每个模块的挂钩失败都被隔离在它自己身上: 一个模块抛异常, 后面的照样挂上。
/// (上一版也是这个行为, 只是散在 Patcher 里; 现在集中且可见。)
/// </summary>
internal sealed class ModuleHost
{
    private readonly List<ModModule> _mods = new List<ModModule>();

    internal IReadOnlyList<ModModule> Modules => _mods;

    internal ModModule Add(ModModule m) { _mods.Add(m); return m; }

    /// <summary>总开关取值。缺省(还没绑定/读失败)当开 —— 与上一版语义一致。</summary>
    internal static bool SwitchOn(string key)
    {
        if (string.IsNullOrEmpty(key)) return true;
        try
        {
            foreach (var e in Plugin.MountSwitches)
                if (e.Key == key) return e.Value.Value;
        }
        catch (Exception ex) { LogEx.Err("ModuleHost.SwitchOn", ex); }
        return true;
    }

    internal int MountAll(Harmony harmony)
    {
        int total = 0;
        foreach (var m in _mods)
        {
            if (!SwitchOn(m.MountKey))
            {
                m.Skipped = $"[总开关] {m.MountKey}=false (根本不挂载)";
                continue;
            }
            if (m.IsEnabled != null)
            {
                bool on;
                try { on = m.IsEnabled(); }
                catch (Exception e) { on = false; LogEx.Err("ModuleHost.IsEnabled/" + m.Id, e); }
                if (!on) { m.Skipped = "模块开关关闭"; continue; }
            }

            try
            {
                m.HookCount = m.Install(harmony);
                m.Mounted = true;
                total += m.HookCount;
            }
            catch (Exception e)
            {
                m.Skipped = "挂钩抛异常: " + LogEx.Unwrap(e);
                Plugin.Log?.LogError($"[模块] {m.Title} 挂载失败: {e}");
            }
        }
        Report();
        return total;
    }

    /// <summary>把整张表打进日志。排查第一步就是看它。</summary>
    internal void Report()
    {
        var sb = new StringBuilder();
        sb.Append("---- 模块挂载表 ----");
        foreach (var m in _mods)
            sb.Append($"\n  {(m.Mounted ? "✔" : "·")} [{m.Section}] {m.Title}  —  {m.Status}");
        var zero = _mods.Where(m => m.Mounted && m.HookCount == 0).ToList();
        if (zero.Count > 0)
            sb.Append($"\n  ⚠ {zero.Count} 个模块挂上了 0 处 Hook: " +
                      string.Join(" / ", zero.Select(m => m.Title)) +
                      "  (通常是版本不匹配或签名变了)");
        Plugin.Log?.LogInfo(sb.ToString());
    }
}
