# -*- coding: utf-8 -*-
"""两个真 bug：
 ① 预筛写成"整串里出现过这个名字" ⇒ 弹幕自己的动作(B1/x2/A1…)也被当成触发，
    计数器被污染（那 96 次"caster 不是本地玩家"其实是弹幕，不是影子）。
    改成【按规则键名】匹配（冒号前那一段），弹幕名只是值、永远不匹配。
 ② 去重窗口写在了检查之前 ⇒ ChangeAction 第一次返回 false、下一帧重试成功时，
    那次成功落在 0.1s 窗口里被吃掉 —— 这就是"偶尔丢失剑气"的机制。
    改成【只有真的入队了才记时间】。
"""
import io

CS = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Modules\Es\EsEmblemBurst.cs"
s = io.open(CS, encoding="utf-8").read()

# ---- ① 键名集合 + 判定 ----
a = "    /// <summary>各跳过原因的累计次数。永远只增不减，汇总时打印。</summary>"
b = """    /// <summary>StartBullets 里所有【规则键名】（冒号前那一段）的集合，按配置原文缓存。
    ///
    /// 为什么必须按"键名"而不是"整串里出现过"：弹幕自己的动作(A1/B1/C1/x2/aup…)
    /// 同样走 ActionMgr.ChangeAction，而它们作为【值】出现在配置里 ——
    /// 用 IndexOf 预筛会把它们全放进来，钩子每次都白跑，
    /// 而且计数被污染（实测那 96 次"caster 不是本地玩家"其实是弹幕，不是影子）。</summary>
    private static HashSet<string> _startKeys;
    private static string _startKeysRaw;

    private static bool IsStartKey(string name)
    {
        try
        {
            var raw = CfgStartBullets?.Value ?? "";
            if (_startKeys == null || !string.Equals(raw, _startKeysRaw, StringComparison.Ordinal))
            {
                _startKeysRaw = raw;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in raw.Split('|'))
                {
                    int c = item.IndexOf(':');
                    if (c > 0) set.Add(item.Substring(0, c).Trim());
                }
                _startKeys = set;
            }
            return _startKeys.Contains(name);
        }
        catch { return false; }
    }

    /// <summary>各跳过原因的累计次数。永远只增不减，汇总时打印。</summary>"""
assert a in s
s = s.replace(a, b, 1)

# 用键名判定替换原预筛
a = """            bool listed = !string.IsNullOrWhiteSpace(CfgStartBullets?.Value) &&
                          CfgStartBullets.Value.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;"""
b = """            bool listed = IsStartKey(name);          // 只有规则键名才算，弹幕名不算"""
assert a in s
s = s.replace(a, b, 1)

a = """            // 快速预筛：这一串里连动作名都没出现就直接走人（ChangeAction 是极热路径）
            if (raw.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) return;"""
b = """            // 预筛走 IsStartKey（上面已算过）—— 弹幕的动作名只是规则里的【值】，不该被当成触发
            if (!listed) return;"""
assert a in s
s = s.replace(a, b, 1)

# ---- ② 去重时间只在实际入队后记 ----
a = """            // 去重：一次切换可能被 ChangeAction 调用多次，不去重会一次动作放好几批
            float now = 0f;
            try { now = Time.realtimeSinceStartup; } catch { }
            if (_startSeen.TryGetValue(name, out float last) && now - last < 0.10f)
            { Skip(name, "0.1s 内重复(去重窗口)"); return; }
            _startSeen[name] = now;"""
b = """            // 去重：一次切换可能被 ChangeAction 调用多次，不去重会一次动作放好几批。
            // ★ 但【时间戳必须等真的入队了才写】—— 第一版写在检查之前，于是
            //   ChangeAction 第一次返回 false、游戏下一帧重试成功时，
            //   那次成功正好落在 0.1s 窗口里被吃掉 ⇒ 表现成"这一下没剑气，等一下又好了"。
            float now = 0f;
            try { now = Time.realtimeSinceStartup; } catch { }
            if (_startSeen.TryGetValue(name, out float last) && now - last < 0.10f)
            { Skip(name, "0.1s 内重复(去重窗口)"); return; }
            bool queuedAny = false;"""
assert a in s
s = s.replace(a, b, 1)

a = """                    _spawnQueued++;
                    _startLogged.TryGetValue(name, out int lg);"""
b = """                    _spawnQueued++;
                    queuedAny = true;
                    _startLogged.TryGetValue(name, out int lg);"""
assert a in s
s = s.replace(a, b, 1)

# 循环结束后写时间戳
a = """        }
        catch (Exception e) { Plugin.Log?.LogError($"[纹章接管:起手] 异常: {e.Message}"); }
    }"""
b = """            if (queuedAny) _startSeen[name] = now;    // ★ 只有真的放出东西才记时间
        }
        catch (Exception e) { Plugin.Log?.LogError($"[纹章接管:起手] 异常: {e.Message}"); }
    }"""
assert a in s
s = s.replace(a, b, 1)

io.open(CS, "w", encoding="utf-8").write(s)
print("ok")
