# -*- coding: utf-8 -*-
"""在"JS 驱动连段"的唯一漏斗上打探针。

漏斗：PlayerSkillChain.FindAndStartSkillFromMidByOrder(int startOrder)
     调用者 = GamePlay_PlayerSkillChain_Wrap::M_FindAndStartSkillFromMidByOrder (JsPort 桥)
            + 各角色 JS 的 lambda（已知 ES 的 ActorJs_hz.<>c.<b__12_4> 在里面）

要回答：
  1. JS 到底在驱动哪些 Order —— 是不是 "HeroConfig.Quality + 1" 那种线性递增
  2. 它认不认我们造的 aceN_* 段（Order 900+）
     认 → 穿插有路；不认 → 这就是"接不进原生连段"的直接原因

另外给两条搜索函数加【调用计数器】：上次"入口动 41 次、搜索只记 1 次"这个矛盾
没有第三方证据，无法判断是探针失效还是真没调用。计数器就是那个第三方证据。

⚠ 按 PROJECT_STATE §5.1.1 的规矩：高频探针用【自己的】去重表，不蹭 LogEx.Once 的全局额度。
"""
import io

P = "Modules/Es/EsComboChain.cs"
s = io.open(P, encoding="utf-8").read()

# ---------------------------------------------------------------- 1) 注册钩子
A = "        // ★★★ 链冷却的来源探针。"
if A not in s:
    A = "        // ★★ 动作表注入：必须先于\"游戏按名字查动作表\"。"
assert A in s, "找不到插入锚点"

B = '''        // ★★★★★ 【JS 驱动连段的唯一漏斗】(2026-10-03)
        //   反汇编确认：FindAndStartSkillFromMidByOrder 的调用者只有两处 ——
        //       PlayerSkillChain_Wrap::M_FindAndStartSkillFromMidByOrder   (JsPort 桥)
        //       ActorJs_hz.<>c.<b__12_4>                                     (ES 角色 JS)
        //   而 b__12_4 的逻辑（RVA 0x17343A0）是：
        //       if (BulletMgr.GetBulletsByCasterAndActionGroupAndTag(...).Count == 0)
        //           chain.FindAndStartSkillFromMidByOrder(HeroConfig.Quality + 1);
        //   也就是说：**连段推进的条件是"弹幕清空"，Order 来源是 Quality+1**，
        //   跟 preSkillOrder 那套搜索无关（那只是输入驱动的支线）。
        //   打在这个漏斗上，一次回答两件事：JS 驱动了哪些 Order / 认不认我们的 aceN_*。
        try
        {
            var ct3 = AccessTools.TypeByName("GamePlay.PlayerSkillChain");
            var m = ct3 == null ? null : AccessTools.Method(ct3, "FindAndStartSkillFromMidByOrder");
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(MidByOrderPostfix))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 FindAndStartSkillFromMidByOrder (JS驱动漏斗探针)");
                n++;
            }
            else Plugin.Log?.LogWarning("  [连段模组] 找不到 FindAndStartSkillFromMidByOrder");
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 漏斗探针 失败: {e.Message}"); }

        // ★★ 搜索调用计数器（交叉验证用）—— 上次"入口动 41 次、搜索只记 1 次"无法判断真假，
        //   这个计数器就是第三方证据：入口次数与搜索次数应当同量级。
        try
        {
            var ct4 = AccessTools.TypeByName("GamePlay.PlayerSkillChain");
            foreach (var pair in new[] {
                new[] { "findNextSkillMatchPreOrderAndInputDir", "主搜索" },
                new[] { "findStartingSkillMatchInputDir", "兜底搜索" } })
            {
                var m = ct4 == null ? null : AccessTools.Method(ct4, pair[0]);
                if (m == null) continue;
                harmony.Patch(m, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(SearchCountPrefix))));
                Plugin.Log.LogInfo($"  [连段模组] 已挂钩 {pair[0]} (调用计数器)");
                n++;
            }
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 计数器 失败: {e.Message}"); }

''' + A
s = s.replace(A, B, 1)

# ---------------------------------------------------------------- 2) 探针方法
C = "    private static readonly HashSet<string> _nextSeen = new HashSet<string>();"
assert C in s, "缺 _nextSeen"
D = C + '''

    // ---------------- JS 驱动漏斗 ----------------
    private static readonly HashSet<string> _midSeen = new HashSet<string>();
    private static long _searchMain, _searchFall;

    /// <summary>搜索调用计数器（只计数，按 500 次打一条）。</summary>
    public static void SearchCountPrefix(string __0)
    {
        try
        {
            // 两个方法共用一个 prefix，靠参数名区分不了 —— 用调用栈深度无意义，
            // 所以统一计数，再由 MidByOrderPostfix 打总量。够用了。
            _searchMain++;
            if (_searchMain % 500 == 0)
                Plugin.Log?.LogInfo($"[连段模组:计数] 搜索函数累计被调用 {_searchMain} 次");
        }
        catch { }
    }

    /// <summary>JS 驱动连段的漏斗：谁、把链推到哪个 Order。</summary>
    public static void MidByOrderPostfix(GamePlay.PlayerSkillChain __instance, int startOrder, bool __result)
    {
        try
        {
            if (CfgEnabled?.Value != true || __instance == null) return;

            int cnt = 0, idx = -1, curOrder = -1;
            string curAct = "", resOrder = "-", resAct = "";
            try
            {
                var list = __instance.SkillList;
                cnt = SafeCount(list);
                for (int i = 0; i < cnt; i++)
                {
                    var sk = TryGet(list, i);
                    if (sk == null) continue;
                    int o = OrderOf(sk);
                    if (o == startOrder) { resOrder = o.ToString(); resAct = ActionOf(sk); idx = i; }
                }
                var c = __instance.Cur;
                if (c != null) { curOrder = c.Order; curAct = c.Action ?? ""; }
            }
            catch { }

            string key = startOrder + "|" + resOrder + "|" + __result;
            if (_midSeen.Count >= 300 || !_midSeen.Add(key)) return;

            Plugin.Log?.LogInfo($"[连段模组:JS驱动] FindAndStartSkillFromMidByOrder({startOrder}) -> {__result}  " +
                                $"段数={cnt} 目标@{idx}(Order={resOrder} \"{resAct}\")  " +
                                $"当前 Order={curOrder} \"{curAct}\"  搜索累计={_searchMain}");
        }
        catch (Exception e) { Once("MidByOrder", e); }
    }'''
s = s.replace(C, D, 1)

io.open(P, "w", encoding="utf-8").write(s)
print("漏斗探针 + 计数器 已加入")
