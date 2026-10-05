# -*- coding: utf-8 -*-
"""技能修改·技术验证：动作 X 同时发射动作 Y 的弹幕。

做法（严格复用 EsEmblemBurst 已验证的模式）：
  1. 在 createBulletImp 的 Postfix 里【只入队】，绝不当场再调 CreateBulletByParams
     —— 那是在 BulletMgr 遍历 BulletList 的调用栈里，当场追加会破坏枚举（文件原注释的教训）。
  2. 在 ValidateBullets 的 Postfix（帧末）里真正补生成，每个只放 1 个。

配置（[纹章解放] 段）：
    AttachBullets = aD12:A1 | aD22:A1
                   触发弹幕action : 要追加的弹幕action

为什么用【弹幕 action】当触发键而不是玩家动作名：
  玩家动作名要顺着 caster 反查 ActionMgr，链路长、易错；
  而弹幕 action 是 createBulletImp 的直接实参，就在手上，零额外依赖。
  （实测 ES 的映射：attackD1/D2/D3 -> aD12/aD22/aD32；attackAEX(布1) -> A1）
"""
import io

P = "Modules/Es/EsEmblemBurst.cs"
s = io.open(P, encoding="utf-8").read()

# ---------------------------------------------------------------- 1) 配置字段
A = "    internal static BepInEx.Configuration.ConfigEntry<string> CfgRingAction;"
assert A in s
s = s.replace(A, A + """
    /// <summary>追加弹幕: "触发弹幕action:要追加的弹幕action | ..."，如 "aD12:A1"。</summary>
    internal static BepInEx.Configuration.ConfigEntry<string> CfgAttach;
    /// <summary>追加弹幕的待生成队列（帧末统一补生成，理由同 _queue）。</summary>
    private static readonly List<Req> _attach = new List<Req>();""", 1)

# ---------------------------------------------------------------- 2) 入队（在 CreatePostfix 里）
B = """            if (_reentrant) return;
            if (__result == null) return;"""
assert B in s
B2 = B + """

            // ★★★ 技能修改·技术验证：把某个弹幕的生成【追加】成另一个弹幕。
            //   例: aD12(佩1 的纹章) 生成时, 同时放一个 A1(布1 的剑气)。
            //   ⚠ 只入队 —— 当场调 CreateBulletByParams 会破坏 BulletList 的枚举（见文件顶部注释）。
            try
            {
                string extra = AttachFor(startAction);
                if (extra != null)
                    _attach.Add(new Req
                    {
                        Mgr = __instance, Caster = caster, Idx = idx, Pos = pos,
                        DamageScale = damageScale, Action = extra,
                        Skill = skillActivate, Params = paramSet, Center = __result,
                        Dir = dir, MoveMode = true,
                    });
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"[纹章接管:追加] 入队失败: {e.Message}"); }"""
s = s.replace(B, B2, 1)

# ---------------------------------------------------------------- 3) 解析映射
C = "    private static string[] ActionList()"
assert C in s
C2 = '''    /// <summary>查"这个弹幕要不要额外追加一个"。返回要追加的弹幕 action，没有则 null。</summary>
    private static string AttachFor(string startAction)
    {
        var raw = CfgAttach?.Value;
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrEmpty(startAction)) return null;
        foreach (var item in raw.Split('|'))
        {
            int c = item.IndexOf(':');
            if (c <= 0) continue;
            if (string.Equals(item.Substring(0, c).Trim(), startAction, StringComparison.OrdinalIgnoreCase))
            {
                var v = item.Substring(c + 1).Trim();
                if (v.Length > 0) return v;
            }
        }
        return null;
    }

    private static string[] ActionList()'''
s = s.replace(C, C2, 1)

# ---------------------------------------------------------------- 4) 帧末补生成
D = "        if (_queue.Count == 0) return;"
assert D in s
D2 = """        // ---- 追加弹幕（技能修改验证）: 每个只放 1 个，方向沿用触发它的那一下 ----
        if (_attach.Count > 0)
        {
            var ab = _attach.ToArray();
            _attach.Clear();
            _reentrant = true;
            try
            {
                foreach (var r in ab)
                {
                    if (r.Mgr == null) continue;
                    try
                    {
                        var b = r.Mgr.CreateBulletByParams(r.Caster, r.Idx, r.Pos, r.Dir,
                                                           r.DamageScale, r.Action, r.Skill, r.Params);
                        Plugin.Log?.LogInfo($"[纹章接管:追加] 额外放出 \\"{r.Action}\\" " +
                                            $"(触发 idx={r.Idx}) {(b != null ? "成功" : "返回null")}");
                    }
                    catch (Exception e) { Plugin.Log?.LogWarning($"[纹章接管:追加] 放出失败: {e.Message}"); }
                }
            }
            finally { _reentrant = false; }
        }

""" + D
s = s.replace(D, D2, 1)

io.open(P, "w", encoding="utf-8").write(s)
print("追加弹幕通道已加入")
