using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 配置读写的小工具。
///
/// ⚠ 铁律（栽过 3 次）: BepInEx **不覆盖**已经写进 .cfg 的项。
///   改代码里的默认值必须同时改 cfg 文件, 否则用户那边毫无变化。
///   所以这里的默认值只是"第一次生成 cfg 时写什么", 不是一个能生效的开关。
/// </summary>
internal static class Cfg
{
    internal static ConfigFile File => Plugin.CfgFile;

    internal static ConfigEntry<T> Bind<T>(string section, string key, T def, string desc)
        => Plugin.CfgFile.Bind(section, key, def, desc);

    // ------------------------------------------------------------------ 解析

    /// <summary>逗号/分号分隔 → 小写去空的集合(忽略大小写)。</summary>
    internal static string[] List(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var parts = raw.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
        var outp = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.Length > 0) outp.Add(t);
        }
        return outp.ToArray();
    }

    /// <summary>名字是否命中关键字表(子串, 忽略大小写)。空表 = 不命中。</summary>
    internal static bool Hits(string name, string[] keywords)
    {
        if (string.IsNullOrEmpty(name) || keywords == null) return false;
        foreach (var k in keywords)
            if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>
    /// 「命中关键字、但不在排除名单里」。
    ///
    /// ⚠ 排除名单不是可选项。血泪: 关键字 `dash` 匹配到了贝德维尔的
    ///   UltraDashEX / UDA, 于是整个 Ultra 演出期间玩家无敌 ——
    ///   而本作命中结算只要一方无敌就整个跳过, Ultra 二段的命中被吃掉,
    ///   症状是【贝德维尔的翅膀纹章消失】。任何子串匹配都必须配排除名单。
    /// </summary>
    internal static bool HitsExcept(string name, string[] keywords, string[] exclude)
    {
        if (!Hits(name, keywords)) return false;
        return !(Hits(name, exclude));
    }

    internal static Color ParseColor(string s, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        try
        {
            var t = s.Trim().TrimStart('#');
            if (t.IndexOf(',') >= 0)
            {
                var p = t.Split(',');
                if (p.Length < 3) return fallback;
                float Get(int i)
                    => float.TryParse(p[i].Trim(), System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f;
                var c = new Color(Get(0), Get(1), Get(2), p.Length > 3 ? Get(3) : 1f);
                if (c.r > 1f || c.g > 1f || c.b > 1f) c = new Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a);
                return c;
            }
            if (t.Length >= 6)
                return new Color(
                    Convert.ToInt32(t.Substring(0, 2), 16) / 255f,
                    Convert.ToInt32(t.Substring(2, 2), 16) / 255f,
                    Convert.ToInt32(t.Substring(4, 2), 16) / 255f,
                    t.Length >= 8 ? Convert.ToInt32(t.Substring(6, 2), 16) / 255f : 1f);
        }
        catch { }
        return fallback;
    }

    internal static string ToHex(Color c)
        => $"{(int)Mathf.Clamp(c.r * 255f, 0f, 255f):X2}" +
           $"{(int)Mathf.Clamp(c.g * 255f, 0f, 255f):X2}" +
           $"{(int)Mathf.Clamp(c.b * 255f, 0f, 255f):X2}";
}
