# -*- coding: utf-8 -*-
"""把起手触发的"跳过日志"从【限条数】改成【计数 + 定期汇总】。
原因：原来 _startDiagSeen 上限 60 条，超过就不再打 ⇒ 后面的跳过全成假阴性，
"偶尔丢失"根本看不见（本项目"探针限条数制造假阴性"栽过 5 次）。
新做法：
  · 每个原因一个计数器，第 1 次 + 每 10 次打一条（永远打得出来，不会哑）
  · 每 10 秒汇总一次：触发命中 / 实际放出 / 各原因跳过数
"""
import io

CS = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Modules\Es\EsEmblemBurst.cs"
s = io.open(CS, encoding="utf-8").read()

# 1) 计数器字段
a = """    /// <summary>起手触发的诊断去重表（按 "动作|原因"）。上限 60 条，防刷屏。</summary>
    private static readonly HashSet<string> _startDiagSeen = new HashSet<string>();"""
b = """    /// <summary>起手触发的诊断去重表（按 "动作|原因"）。上限 60 条，防刷屏。</summary>
    private static readonly HashSet<string> _startDiagSeen = new HashSet<string>();

    // ---- 计数式诊断（不再限条数：限条数会制造假阴性，本项目栽过 5 次）----
    /// <summary>各跳过原因的累计次数。永远只增不减，汇总时打印。</summary>
    private static readonly Dictionary<string, int> _skipReasons = new Dictionary<string, int>();
    /// <summary>配置里点名的动作"到达钩子"的次数（含成功与失败）。</summary>
    private static int _hookHits;
    /// <summary>我们真正入队放出的弹幕数。</summary>
    private static int _spawnQueued;
    /// <summary>汇总间隔计时。</summary>
    private static float _sumAcc;
    private static int _sumLogged;"""
assert a in s
s = s.replace(a, b, 1)

# 2) Skip 函数
a = "    private static void StartDiag(string name, string why)"
b = """    /// <summary>记一次"跳过"并计数。第 1 次 + 每 10 次各打一条 —— 永远打得出来，不会哑。</summary>
    private static void Skip(string name, string why)
    {
        try
        {
            int c;
            _skipReasons.TryGetValue(why, out c);
            _skipReasons[why] = ++c;
            if (c == 1 || c % 10 == 0)
                Plugin.Log?.LogInfo($"[纹章接管:起手] 跳过({why}) 第 {c} 次 —— 最近一个是 \\"{name}\\"");
        }
        catch { }
    }

    /// <summary>每 10 秒汇总一次：命中/放出/各原因跳过。判断"有没有丢"就看这张表。</summary>
    private static void Summarize(float dt)
    {
        try
        {
            if (_hookHits == 0 && _skipReasons.Count == 0 && _spawnQueued == 0) return;
            _sumAcc += dt;
            if (_sumAcc < 10f) return;
            _sumAcc = 0f;
            if (++_sumLogged > 200) return;          // 只防日志爆炸，正常战斗用不到

            var sb = new System.Text.StringBuilder();
            sb.Append($"[纹章接管:起手] 10s 汇总: 钩子命中 {_hookHits} 次, 入队放出 {_spawnQueued} 发");
            if (_skipReasons.Count > 0)
            {
                sb.Append(", 跳过: ");
                foreach (var kv in _skipReasons) sb.Append(kv.Key).Append('×').Append(kv.Value).Append("  ");
            }
            else sb.Append(", 跳过: 无");
            Plugin.Log?.LogInfo(sb.ToString());
        }
        catch { }
    }

    private static void StartDiag(string name, string why)"""
assert a in s
s = s.replace(a, b, 1)

# 3) 每个 return 换成 Skip（保留第一次的明细日志）
reps = [
 ('            if (listed) StartDiag(name, $"到达钩子 (result={__result})");',
  '            if (listed) { _hookHits++; if (_hookHits <= 20) Plugin.Log?.LogInfo($"[纹章接管:起手] 钩子命中 \\"{name}\\" (result={__result})"); }'),
 ('            if (listed) StartDiag(name, "没触发: ChangeAction 返回 false（动作没真的切过去）");',
  '            if (listed) Skip(name, "ChangeAction 返回 false");'),
 ('            if (CfgEnabled?.Value != true) { if (listed) StartDiag(name, "没触发: 纹章接管 Enabled=false"); return; }',
  '            if (CfgEnabled?.Value != true) { if (listed) Skip(name, "纹章接管 Enabled=false"); return; }'),
 ('            { if (listed) StartDiag(name, "没触发: 还没有弹幕实参模板（先在游戏里放一次真弹幕）"); return; }',
  '            { if (listed) Skip(name, "还没有弹幕实参模板"); return; }'),
 ('            { if (listed) StartDiag(name, "没触发: caster 不是本地玩家"); return; }',
  '            { if (listed) Skip(name, "caster 不是本地玩家"); return; }'),
 ('            { StartDiag(name, "没触发: 0.1s 内重复（去重窗口）"); return; }',
  '            { Skip(name, "0.1s 内重复(去重窗口)"); return; }'),
]
for a2, b2 in reps:
    assert a2 in s, a2[:50]
    s = s.replace(a2, b2, 1)

# 4) 入队时计数
a = """                    _startLogged.TryGetValue(name, out int lg);"""
b = """                    _spawnQueued++;
                    _startLogged.TryGetValue(name, out int lg);"""
assert a in s
s = s.replace(a, b, 1)

# 5) 朝向闸门也走 Skip
a = """                                _faceSkipLogged.TryGetValue(r.Action, out int sl);
                                if (sl < 3)
                                {
                                    _faceSkipLogged[r.Action] = sl + 1;
                                    Plugin.Log?.LogInfo($"[纹章接管:追加] 跳过 \\"{r.Action}\\" —— 朝向闸门" +
                                                        $"(要求 {(r.FaceReq < 0 ? "朝左" : "朝右")}, 实际 {(left ? "朝左" : "朝右")})");
                                }
                                continue;"""
b = """                                Skip(r.Action, $"朝向闸门要求{(r.FaceReq < 0 ? "朝左" : "朝右")}");
                                continue;"""
assert a in s
s = s.replace(a, b, 1)

# 6) 帧末驱动汇总
a = "        MoveTick(dtf);\n        UprightTick();\n        SurvTick(dtf);"
b = "        MoveTick(dtf);\n        UprightTick();\n        SurvTick(dtf);\n        Summarize(dtf);"
assert a in s
s = s.replace(a, b, 1)

io.open(CS, "w", encoding="utf-8").write(s)
print("ok")
