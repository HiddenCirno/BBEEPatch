using System;
using HarmonyLib;

namespace BlazblueJsPatch;

/// <summary>
/// 一个可挂载模块的描述。
///
/// 为什么要有这层
/// ──────────────
/// 上一版 `Patcher.Apply` 是一根 180 行的直线: 19 个模块的挂载顺序、总开关判断、
/// `try/catch` 全糊在一个方法里, 想回答"现在到底挂了哪些、谁挂了 0 个 Hook、
/// 谁被总开关挡了"只能翻日志。而本项目的排查方式恰恰是
/// 【关掉一个模块看现象还在不在】—— 这个动作必须一眼可见、可复现。
///
/// 所以把「挂载」变成一个数据结构: 顺序、开关、结果都摆在 <see cref="ModuleHost"/> 里。
///
/// ⚠ 总开关语义: 关掉 = **根本不挂载**, 不是"挂上之后不干活"。
///   区别很重要 —— 配置项关掉只是让代码路径提前 return, 万一有哪条写路径漏了检查,
///   排查就会被误导(本项目栽过一次: RecolorEffect=false 只拦住了粒子路)。
///   要判断"某个模块是不是罪魁", 必须用不挂钩子这种没有漏洞的方式。
/// </summary>
internal sealed class ModModule
{
    /// <summary>稳定标识(日志/面板用)。</summary>
    internal string Id;
    /// <summary>面板/日志里的中文名。</summary>
    internal string Title;
    /// <summary>所属配置段 —— 面板按它分组显示状态。</summary>
    internal string Section;
    /// <summary>总开关里的键名。null = 不受总开关管辖(如 JS 层补丁)。</summary>
    internal string MountKey;
    /// <summary>模块自身的启用判据。null = 没有独立开关。</summary>
    internal Func<bool> IsEnabled;
    /// <summary>实际挂钩。返回值 = 成功挂上的 Hook 数。</summary>
    internal Func<Harmony, int> Install;

    // ---- 结果(由 ModuleHost 填) ----
    internal bool Mounted;
    internal int HookCount;
    /// <summary>没挂载的原因(null = 挂上了)。每个"没挂"都必须说清为什么 ——
    /// 静默跳过会让日志看起来像"这个模块没问题", 而实际是根本没跑。</summary>
    internal string Skipped;

    internal string Status
    {
        get
        {
            if (Skipped != null) return "未挂载: " + Skipped;
            if (!Mounted) return "未尝试";
            return HookCount > 0 ? $"已挂载 {HookCount} 处" : "已挂载(0 处 Hook)";
        }
    }

    internal static ModModule Of(string id, string title, string section, string mountKey,
                                Func<bool> enabled, Func<Harmony, int> install)
        => new ModModule { Id = id, Title = title, Section = section, MountKey = mountKey,
                           IsEnabled = enabled, Install = install };
}
