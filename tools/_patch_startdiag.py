# -*- coding: utf-8 -*-
"""给 StartBullets 的每一道早期 return 补诊断日志。
本项目铁律：静默 return 会让人把"被挡住"误读成"没触发"（已栽过 5 次）。
"""
import io

CS = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Modules\Es\EsEmblemBurst.cs"
s = io.open(CS, encoding="utf-8").read()

# 1) 诊断表
a = "    private static readonly Dictionary<string, int> _startLogged = new Dictionary<string, int>();"
b = a + """
    /// <summary>起手触发的诊断去重表（按 "动作|原因"）。上限 60 条，防刷屏。</summary>
    private static readonly HashSet<string> _startDiagSeen = new HashSet<string>();"""
assert a in s, "startLogged"
s = s.replace(a, b, 1)

# 2) 诊断函数
a = "    // ------------------------------------------------------------------ 动作起手触发"
b = """    /// <summary>起手触发没发生的【原因】—— 配置里点名了却没过闸时一定要打出来。
    /// 为什么必须有它：这些 return 全是静默的，而"被某道闸挡住"和"压根没调用到"
    /// 在日志里长得一模一样（本项目为此栽过 5 次，见 PROJECT_STATE 的踩坑清单）。</summary>
    private static void StartDiag(string name, string why)
    {
        try
        {
            if (_startDiagSeen.Count >= 60 || !_startDiagSeen.Add(name + "|" + why)) return;
            Plugin.Log?.LogInfo($"[纹章接管:起手] \\"{name}\\" 这次没触发: {why}");
        }
        catch { }
    }

    // ------------------------------------------------------------------ 动作起手触发"""
assert a in s, "section marker"
s = s.replace(a, b, 1)

# 3) 各处 return 补日志
reps = [
 ('            if (!__result || __instance == null || string.IsNullOrEmpty(name)) return;\n'
  '            if (CfgEnabled?.Value != true) return;\n'
  '\n'
  '            var raw = CfgStartBullets?.Value;\n'
  '            if (string.IsNullOrWhiteSpace(raw)) return;\n'
  '            // 快速预筛：这一串里连动作名都没出现就直接走人（ChangeAction 是极热路径）\n'
  '            if (raw.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) return;\n'
  '            // 没有实参模板就没法生成（与 F9 面板同一限制：先在游戏里过一次真弹幕）\n'
  '            if (!_lastLive.HasValue || _lastLive.Value.Mgr == null) return;\n',

  '            if (string.IsNullOrEmpty(name)) return;\n'
  '            // 只诊断"配置里点名了"的动作 —— 其余动作这条路每帧都在过，不能打日志\n'
  '            bool listed = !string.IsNullOrWhiteSpace(CfgStartBullets?.Value) &&\n'
  '                          CfgStartBullets.Value.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;\n'
  '            if (listed) StartDiag(name, $"到达钩子 (result={__result})");\n'
  '            if (!__result || __instance == null) return;\n'
  '            if (listed) StartDiag(name, "没触发: ChangeAction 返回 false（动作没真的切过去）");\n'
  '            if (CfgEnabled?.Value != true) { if (listed) StartDiag(name, "没触发: 纹章接管 Enabled=false"); return; }\n'
  '\n'
  '            var raw = CfgStartBullets?.Value;\n'
  '            if (string.IsNullOrWhiteSpace(raw)) return;\n'
  '            // 没有实参模板就没法生成（与 F9 面板同一限制：先在游戏里过一次真弹幕）\n'
  '            if (!_lastLive.HasValue || _lastLive.Value.Mgr == null)\n'
  '            { if (listed) StartDiag(name, "没触发: 还没有弹幕实参模板（先在游戏里放一次真弹幕）"); return; }\n'),
]
for a2, b2 in reps:
    assert a2 in s, "return block"
    s = s.replace(a2, b2, 1)

# 4) caster / 去重 两处
a = ('            GamePlay.ActorBase caster = null;\n'
     '            try { caster = __instance.Owner; } catch { }\n'
     '            // 只对本地玩家生效：影子/分身也跑同一套动作，不加这一层会一次动作放好几批\n'
     '            if (caster == null || !DashInvincible.IsLocalPlayerActor(caster)) return;\n')
b = ('            GamePlay.ActorBase caster = null;\n'
     '            try { caster = __instance.Owner; } catch { }\n'
     '            // 只对本地玩家生效：影子/分身也跑同一套动作，不加这一层会一次动作放好几批\n'
     '            if (caster == null || !DashInvincible.IsLocalPlayerActor(caster))\n'
     '            { if (listed) StartDiag(name, "没触发: caster 不是本地玩家"); return; }\n')
assert a in s, "caster guard"
s = s.replace(a, b, 1)

a = ('            if (_startSeen.TryGetValue(name, out float last) && now - last < 0.10f) return;\n')
b = ('            if (_startSeen.TryGetValue(name, out float last) && now - last < 0.10f)\n'
     '            { StartDiag(name, "没触发: 0.1s 内重复（去重窗口）"); return; }\n')
assert a in s, "dedup"
s = s.replace(a, b, 1)

io.open(CS, "w", encoding="utf-8").write(s)
print("ok")
