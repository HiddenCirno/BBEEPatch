# -*- coding: utf-8 -*-
"""1) 弹幕池模块默认关（PreCreateBullet 全场从未被调用 ⇒ 本作不用预热池，这条路不成立）
   2) 加"生成后存活探针"：0.35s 后回看那条弹幕还在不在 —— 把"连按丢剑气"钉死是
      "生成成功但随后被清掉"还是"根本没生成"。
"""
import io

CS = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Modules\Es\EsEmblemBurst.cs"
PL = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Plugin.cs"
CFG = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\BepInEx\config\ace.bbee.jspatch.cfg"

# ---- 1) 弹幕池默认关 ----
s = io.open(PL, encoding="utf-8").read()
a = 'BulletPool.CfgEnabled = Config.Bind(poolsec, "Enabled", true,'
b = ('BulletPool.CfgEnabled = Config.Bind(poolsec, "Enabled", false,\n'
     '                "【默认关 —— 实测这条路不成立】\\n" +\n'
     '                "它挂在 BulletMgr.PreCreateBullet 上，但本作全场一次都没调用过那个函数\\n" +\n'
     '                "(日志只有 已挂钩 那行，之后零条 预创建请求) —— 也就是说本作【不用预热池】: \\n" +\n'
     '                "弹幕是 GetFromPoolOrCreate 按需创建、死亡后回收复用。\\n" +\n'
     '                "所以扩大预创建数量没有任何作用。留在这里只作为记录与探针:\\n" +\n'
     '                "万一某个场景真的调了它, 打开就能看到真实数量。\\n" +\n')
assert a in s
io.open(PL, "w", encoding="utf-8").write(s.replace(a, b, 1))

# ---- 2) 存活探针 ----
s = io.open(CS, encoding="utf-8").read()

# 表 + 结构
a = "    private static readonly List<Mover> _movers = new List<Mover>();"
b = """    private static readonly List<Mover> _movers = new List<Mover>();

    /// <summary>存活探针：生成后过一小段回看这条弹幕还在不在。
    ///
    /// 为什么需要它：实测"连按崔斯坦会丢剑气"，而日志显示 76 发全部【生成成功】、
    /// 零次返回 null ⇒ 丢失发生在生成之后。两种可能必须分开：
    ///   ① 生成成功但随后被清掉（游戏动作自带 BulletClearTarget / DeleteBulletByTag，
    ///      踩踏链动作极密，我们附的剑气继承了实参模板的 tag ⇒ 刚生成就被清）
    ///   ② 生成了但没显示（位置/缩放/可见性）
    /// 这一行日志直接把 ① 和 ② 分开。
    /// </summary>
    private static readonly List<Surv> _surv = new List<Surv>();

    private struct Surv
    {
        public GamePlay.BulletObj B;
        public string Act;
        public float Left;      // 还要等多久才回看
    }

    /// <summary>由帧末冲洗调用：到点回看每条弹幕是否还活着。</summary>
    private static void SurvTick(float dt)
    {
        if (_surv.Count == 0) return;
        for (int i = _surv.Count - 1; i >= 0; i--)
        {
            var s = _surv[i];
            s.Left -= dt;
            if (s.Left > 0f) { _surv[i] = s; continue; }
            _surv.RemoveAt(i);
            try
            {
                bool dead = s.B == null || IsDead(s.B);
                Plugin.Log?.LogInfo($"[纹章接管:存活] \\"{s.Act}\\" 0.35s 后: {(dead ? "★已消失/已死（被清掉）" : "仍在")}");
            }
            catch (Exception e) { Once("SurvTick", e); }
        }
    }"""
assert a in s
s = s.replace(a, b, 1)

# 生成后登记
a = "                        string fp = \"?\"; try { fp = r.Pos.ToString(); } catch { }"
b = ("                        if (b != null && _surv.Count < 60)\n"
     "                            _surv.Add(new Surv { B = b, Act = r.Action, Left = 0.35f });\n"
     "                        string fp = \"?\"; try { fp = r.Pos.ToString(); } catch { }")
assert a in s
s = s.replace(a, b, 1)

# 每帧驱动
a = "        MoveTick(dtf);\n        UprightTick();"
b = "        MoveTick(dtf);\n        UprightTick();\n        SurvTick(dtf);"
assert a in s
s = s.replace(a, b, 1)

io.open(CS, "w", encoding="utf-8").write(s)
print("ok")
