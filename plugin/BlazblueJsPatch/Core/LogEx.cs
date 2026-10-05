using System;
using System.Collections.Generic;

namespace BlazblueJsPatch;

/// <summary>
/// 去重日志。
///
/// 为什么不用"只打前 N 条"
/// ──────────────────────
/// 本项目在探针限条数上【栽过 5 次】: 预算会被噪声吃光
/// (rush→AttackUp2 爆发 / ChangeAction("") 空串 / stand→squat 每帧重试),
/// 于是"日志里没有"这一个现象同时对应【真的没走这条路】和【只是没轮到打印】,
/// 两者分不开 —— 这就是假阴性, 比不打日志更坏。
///
/// 规则:
///   · 新 key 永远打得出来(不管前面已经打了多少)
///   · 老 key 每 <see cref="RepeatEvery"/> 次提醒一次, 并附累计次数
///   · 每个 key 只用于【可枚举的小集合】(名字/配对), 不是无限增长的
/// </summary>
internal static class LogEx
{
    /// <summary>同一个 key 每隔多少次重复再提醒一次。</summary>
    internal const int RepeatEvery = 400;
    /// <summary>不同 key 的上限, 防止名字空间爆炸把内存吃光。</summary>
    private const int KeyCap = 4000;

    private static readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.Ordinal);
    private static readonly object _gate = new object();

    /// <summary>
    /// 按 key 去重地打一条 Info。
    /// 返回 true 表示这次真的写出去了(调用方可据此决定要不要做更贵的二次处理)。
    /// </summary>
    internal static bool Once(string key, string msg)
    {
        if (!ShouldEmit(key)) return false;
        Plugin.Log?.LogInfo(msg);
        return true;
    }

    internal static bool WarnOnce(string key, string msg)
    {
        if (!ShouldEmit(key)) return false;
        Plugin.Log?.LogWarning(msg);
        return true;
    }

    private static bool ShouldEmit(string key)
    {
        if (key == null) key = "";
        lock (_gate)
        {
            if (_counts.TryGetValue(key, out var c))
            {
                _counts[key] = c + 1;
                return c % RepeatEvery == 0;      // 第 0 次(首次) / 第 N 次 提醒
            }
            if (_counts.Count >= KeyCap) return false;
            _counts[key] = 1;
            return true;
        }
    }

    /// <summary>累计次数, 用于"打了 N 次之后收尾一条汇总"。</summary>
    internal static int CountOf(string key)
    {
        lock (_gate) return _counts.TryGetValue(key ?? "", out var c) ? c : 0;
    }

    /// <summary>异常统一出口。参数是【场景描述】而不是异常消息 —— 异常原文附在后面。</summary>
    internal static void Err(string where, Exception e)
        => WarnOnce("err|" + where, $"[{where}] 异常: {Unwrap(e)}");

    internal static string Unwrap(Exception e)
    {
        while (e is System.Reflection.TargetInvocationException tie && tie.InnerException != null) e = tie.InnerException;
        return e == null ? "(null)" : e.GetType().Name + ": " + e.Message;
    }
}
