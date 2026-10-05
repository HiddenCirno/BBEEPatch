# -*- coding: utf-8 -*-
"""给"所有起招的唯一汇点" PlayerSkillChain::StartSkill 打全量探针，并标出来路。

调用图（反汇编确认，2026-10-03）：

    【路径 A 输入驱动】
      DoUpdateAndCheckInputSucc -> findAndStartSkill_Imp(-1)
        -> findNextSkillMatchPreOrderAndInputDir / findStartingSkillMatchInputDir
        -> StartSkill -> SkillChangePreCall -> startSkill -> SkillStartImplement

    【路径 B 按动作名】 ChangeSkillByActionName(动作名) ─┐
    【路径 C JS 桥】    PlayerSkillChain_Wrap::M_StartSkill ─┴→ StartSkill -> ...同上

三条路全汇到 StartSkill —— 所以只在这一处打"目标 + 来路"，就能一次看清原生连段怎么走。

⚠ 旧探针给 StartSkill 带了节流(第1次 + 每300次)，把中间调用全漏了 ——
   这正是"地面布鲁诺明明过 SkillChangePreCall、StartSkill 却查不到"的原因（今天第六次同类）。
   这版改成自己带上限的去重表，不节流、不蹭 LogEx.Once 的全局额度。
"""
import io

Q = chr(92) + '"'   # C# 源码里的 \"

P = "Modules/Es/EsComboChain.cs"
s = io.open(P, encoding="utf-8").read()

# ---------------------------------------------------------------- 1) 换掉 StartSkill 的挂钩
A = '''            foreach (var mn in new[] { "DoUpdateAndCheckInputSucc", "StartSkill" })'''
assert A in s, "找不到 StartSkill 挂钩循环"
B = '''            // ★ StartSkill 单独一份全量探针（原先把它们合在一起、还带节流，漏掉了大部分调用）
            try
            {
                var ms = AccessTools.Method(ct, "StartSkill");
                if (ms != null)
                {
                    harmony.Patch(ms, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(EsComboChain), nameof(StartSkillProbe))));
                    Plugin.Log.LogInfo("  [连段模组] 已挂钩 PlayerSkillChain.StartSkill (全量起招探针)");
                    n++;
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 StartSkill 全量探针 失败: {e.Message}"); }

            // 来路标记：给两个上游入口挂 prefix，StartSkill 时读标记
            try
            {
                var mB = AccessTools.Method(t, "ChangeSkillByActionName");
                if (mB != null)
                {
                    harmony.Patch(mB, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(EsComboChain), nameof(ViaChangePrefix))));
                    Plugin.Log.LogInfo("  [连段模组] 已挂钩 ChangeSkillByActionName(prefix) (来路标记B)");
                    n++;
                }
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 来路标记B 失败: {e.Message}"); }
            try
            {
                var wt = AccessTools.TypeByName("GamePlay.PlayerSkillChain_Wrap");
                var mC = wt == null ? null : AccessTools.Method(wt, "M_StartSkill");
                if (mC != null)
                {
                    harmony.Patch(mC, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(EsComboChain), nameof(ViaJsPrefix))));
                    Plugin.Log.LogInfo("  [连段模组] 已挂钩 PlayerSkillChain_Wrap.M_StartSkill(prefix) (来路标记C)");
                    n++;
                }
                else Plugin.Log?.LogWarning("  [连段模组] 找不到 PlayerSkillChain_Wrap.M_StartSkill（来路C标记不可用）");
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 来路标记C 失败: {e.Message}"); }

            foreach (var mn in new[] { "DoUpdateAndCheckInputSucc" })'''
s = s.replace(A, B, 1)

# ---------------------------------------------------------------- 2) 探针本体
C = "    // ---------------- JS 驱动漏斗 ----------------"
assert C in s, "缺插入锚点"
D = '''    // ---------------- 全量起招探针 ----------------
    private static readonly HashSet<string> _startSeen = new HashSet<string>();
    private static bool _viaChange, _viaJs;

    public static void ViaChangePrefix() { _viaChange = true; }
    public static void ViaJsPrefix() { _viaJs = true; }

    /// <summary>所有起招的唯一汇点。打"目标 + 来路"。</summary>
    public static void StartSkillProbe(GamePlay.PlayerSkillChain __instance,
                                       GamePlay.PlayerSkill psk, bool __result)
    {
        // 先读后清 —— 标记必须在本次消费掉，否则会污染下一次
        bool viaChange = _viaChange, viaJs = _viaJs;
        _viaChange = false; _viaJs = false;
        try
        {
            if (CfgEnabled?.Value != true || __instance == null) return;

            int order = -1; string act = "";
            try { if (psk != null) { order = OrderOf(psk); act = ActionOf(psk); } } catch { }

            int cnt = 0; try { cnt = SafeCount(__instance.SkillList); } catch { }
            int cur = -1; string curAct = "";
            try { var c = __instance.Cur; if (c != null) { cur = c.Order; curAct = c.Action ?? ""; } } catch { }

            string path = viaChange ? "按名选招(B)" : (viaJs ? "JS桥(C)" : "输入/搜索(A)");
            string key = order + "|" + act + "|" + path + "|" + __result;
            if (_startSeen.Count >= 400 || !_startSeen.Add(key)) return;

            Plugin.Log?.LogInfo($"[连段模组:起招] StartSkill({Q}{act}{Q} Order={order}) -> {__result}  " +
                                $"链段数={cnt} Cur={cur}({Q}{curAct}{Q})  来路={path}");
        }
        catch (Exception e) { Once("StartSkillProbe", e); }
    }

    // ---------------- JS 驱动漏斗 ----------------'''
s = s.replace(C, D, 1)

io.open(P, "w", encoding="utf-8").write(s)
print("全量起招探针 + 来路标记 已加入")
