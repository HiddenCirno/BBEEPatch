using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BlazblueJsPatch;

/// <summary>
///  一条文本替换规则。JS 是压缩过的单行代码, 直接用子串替换即可, 不需要解析 AST。
/// </summary>
internal sealed class Rule
{
    public string Id = "";
    public string Module = "";      // 匹配模块相对路径的子串(忽略大小写, 不含 .js)
    public string Find = "";
    public string Replace = "";
    public bool Enabled = true;
    public bool Required;           // true = 找不到就报错, 用于确认补丁真的生效
    public int Hits;                // 本次运行命中次数

    public override string ToString() => $"{Id} [module~={Module}]";
}

internal static class JsPatchManager
{
    private static readonly List<Rule> Rules = new();
    private static string _root, _sourcesDir, _overridesDir;
    private static bool _dumpEnabled = true;
    private static bool _verbose;
    private static readonly HashSet<string> DumpedThisRun = new();

    public static void Init(string root)
    {
        _root = root;
        _sourcesDir = Path.Combine(root, "sources");
        _overridesDir = Path.Combine(root, "overrides");
        Directory.CreateDirectory(_sourcesDir);
        Directory.CreateDirectory(_overridesDir);

        LoadRules(Path.Combine(root, "patches.txt"));
        WriteDefaultConfig(Path.Combine(root, "patches.txt"));

        Plugin.Log.LogInfo($"读出 {Rules.Count} 条替换规则, {(_dumpEnabled ? "开启" : "关闭")} JS 转储");
        foreach (var r in Rules)
            Plugin.Log.LogInfo($"   规则 {r}");
    }

    // ---------------------------------------------------------------- 规则文件

    private static void WriteDefaultConfig(string path)
    {
        if (File.Exists(path)) return;
        File.WriteAllText(path, DefaultConfig, new UTF8Encoding(false));
        Plugin.Log.LogInfo($"已生成默认规则文件: {path}");
    }

    private const string DefaultConfig = @"# BBEE JS Patcher 规则文件
# 格式:
#   [settings] 段: dump = true/false (把游戏加载的每个 JS 原样转储到 sources/), verbose = true/false
#   [rule]     段: 一条替换规则, 键值对形式
#       id       规则名(随意)
#       module   模块路径的子串(忽略大小写), 例如 BattleExploreBuyBless
#       find     要被替换掉的原文(必须与 JS 里的完全一致, 含空格)
#       replace  替换成什么
#       required true/false —— 设 true 时若没匹配上会在日志里报警
#       enabled  true/false
# 值里可用 \n \t \\ 转义。
#
# 另外还可以整文件覆盖: 把改好的 js 放到 overrides/ 下,
# 路径与 sources/ 里转储出来的相对路径一致即可。

[settings]
dump    = true
verbose = true

# --- 规则写在这里 ---

";

    private static void LoadRules(string path)
    {
        if (!File.Exists(path)) return;
        Rule cur = null;
        bool inSettings = false;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

            if (line.Equals("[rule]", StringComparison.OrdinalIgnoreCase)) { cur = new Rule(); Rules.Add(cur); inSettings = false; continue; }
            if (line.Equals("[settings]", StringComparison.OrdinalIgnoreCase)) { cur = null; inSettings = true; continue; }

            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string k = line.Substring(0, eq).Trim().ToLowerInvariant();
            string v = Unescape(line.Substring(eq + 1).Trim());

            if (inSettings)
            {
                if (k == "dump") _dumpEnabled = v.Equals("true", StringComparison.OrdinalIgnoreCase);
                else if (k == "verbose") _verbose = v.Equals("true", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (cur == null) continue;

            switch (k)
            {
                case "id": cur.Id = v; break;
                case "module": cur.Module = v; break;
                case "find": cur.Find = v; break;
                case "replace": cur.Replace = v; break;
                case "required": cur.Required = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "enabled": cur.Enabled = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
            }
        }
        // 用注释掉的模板行填充默认 id
        foreach (var r in Rules)
            if (string.IsNullOrEmpty(r.Id)) r.Id = r.Module + "#" + Rules.IndexOf(r);
    }

    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\', _ => s[i] });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 路径归一化

    /// <summary>把 loader 给的各种路径形式统一成 sources/ 下的相对路径。</summary>
    private static string Normalize(string filepath)
    {
        if (string.IsNullOrEmpty(filepath)) return "unknown.js";
        var p = filepath.Replace('\\', '/').Trim();

        // 去掉常见的根前缀
        foreach (var prefix in new[] { "Data/Js/", "data/js/", "Data/js/", "Js/", "js/" })
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { p = p.Substring(prefix.Length); break; }

        while (p.StartsWith("./")) p = p.Substring(2);
        if (!p.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) p += ".js";
        return p;
    }

    // ---------------------------------------------------------------- 主流程

    /// <summary>在 JS 源码交给 V8 编译之前调用。返回替换后的源码。</summary>
    public static string Process(string filepath, string content, string source)
    {
        try
        {
            if (content == null) return null;
            var rel = Normalize(filepath);

            Dump(rel, content);

            // 1) 整文件覆盖
            var ov = Path.Combine(_overridesDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(ov))
            {
                var text = File.ReadAllText(ov, Encoding.UTF8);
                Plugin.Log.LogInfo($"[OVERRIDE] {rel}  <- {ov}  ({content.Length} -> {text.Length} 字符)");
                return text;
            }

            // 2) 文本替换规则
            string result = content;
            foreach (var r in Rules)
            {
                if (!r.Enabled || string.IsNullOrEmpty(r.Find)) continue;
                if (!rel.Replace('\\', '/').Contains(r.Module, StringComparison.OrdinalIgnoreCase)) continue;

                int idx = result.IndexOf(r.Find, StringComparison.Ordinal);
                if (idx < 0)
                {
                    if (r.Required)
                        Plugin.Log.LogError($"[MISS ] 规则 {r.Id} 在 {rel} 中未找到目标文本 —— 游戏可能已更新");
                    else if (_verbose)
                        Plugin.Log.LogWarning($"[miss ] 规则 {r.Id} 在 {rel} 中未匹配");
                    continue;
                }
                int count = 0;
                while (idx >= 0) { count++; idx = result.IndexOf(r.Find, idx + r.Replace.Length, StringComparison.Ordinal); }
                result = result.Replace(r.Find, r.Replace);
                r.Hits += count;
                Plugin.Log.LogInfo($"[PATCH] {rel}  规则 {r.Id}  替换 {count} 处");
            }

            if (_verbose && result != content)
                Plugin.Log.LogInfo($"[PATCH] {rel}  {content.Length} -> {result.Length} 字符");

            return result;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"处理 {filepath} 出错: {e}");
            return content;   // 出错就放行原文件, 绝不因为补丁让游戏起不来
        }
    }

    private static void Dump(string rel, string content)
    {
        if (!_dumpEnabled) return;
        if (!DumpedThisRun.Add(rel)) return;      // 每个模块只在首次加载时写一次
        try
        {
            var dst = Path.Combine(_sourcesDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            if (!File.Exists(dst)) File.WriteAllText(dst, content, new UTF8Encoding(false));
            if (_verbose) Plugin.Log.LogInfo($"[dump ] {rel} ({content.Length} 字符)");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"转储 {rel} 失败: {e.Message}"); }
    }

    public static void ReportHits()
    {
        foreach (var r in Rules)
            if (r.Hits > 0) Plugin.Log.LogInfo($"规则 {r.Id} 累计命中 {r.Hits} 次");
    }
}
