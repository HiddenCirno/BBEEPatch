# -*- coding: utf-8 -*-
"""在真正的汇点 SkillStartImplement 上打"目标 + 落在哪条链"。

为什么是它：三种起招路径最终都到这里（实测证据）——
    路径A 输入驱动: findAndStartSkill_Imp  -> SkillChangePreCall -> startSkill ┐
    路径B 按名选招: StartSkill -> SkillChangePreCall -> startSkill           ├→ SkillStartImplement
    路径C JS 桥   : M_SkillChangePreCall / M_SkillStartImplement 直接到此     ┘
而上一轮挂的 PlayerSkillChain::StartSkill 只捕到路径 B（空中动作），
地面布鲁诺走的是路径 C，完全绕开它 —— 所以插手点必须定在这里。

签名（dump.cs:249511，已确认，不靠印象）：
    public void SkillStartImplement(PlayerSkill psk, Fp2 lastInputDir, PlayerSkillChain chain)
返回 void；这里只绑 psk 与 chain，跳过 Fp2 以免类型名写错。
"""
import io

P = "Modules/Es/EsComboChain.cs"
s = io.open(P, encoding="utf-8").read()

# ---------------------------------------------------------------- 1) 加钩子
A = "        // ★★★ 链冷却的来源探针。"
if A not in s:
    A = "        // ★★ 动作表注入：必须先于\"游戏按名字查动作表\"。"
assert A in s, "缺插入锚点"

B = '''        // ★★★★★ 【真正的汇点】SkillStartImplement —— 三条起招路径全到这里。
        //   上一轮挂在 PlayerSkillChain::StartSkill 上，只捕到路径 B（空中动作）；
        //   地面布鲁诺走 JS 桥（路径 C）绕开了它。这里才是唯一不分来路的观察点。
        try
        {
            var mss = AccessTools.Method(t, "SkillStartImplement");
            if (mss != null)
            {
                harmony.Patch(mss, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EsComboChain), nameof(StartImplChainProbe))));
                Plugin.Log.LogInfo("  [连段模组] 已挂钩 SkillStartImplement (汇点探针: 目标+链)");
                n++;
            }
            else Plugin.Log?.LogWarning("  [连段模组] 找不到 SkillStartImplement（汇点探针不可用）");
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段模组] 挂 汇点探针 失败: {e.Message}"); }

''' + A
s = s.replace(A, B, 1)

# ---------------------------------------------------------------- 2) 探针本体
C = "    // ---------------- 全量起招探针 ----------------"
assert C in s, "缺锚点"
D = '''    // ---------------- 汇点探针 ----------------
    private static readonly HashSet<string> _implSeen = new HashSet<string>();

    /// <summary>三条起招路径的唯一汇点。打"起了哪一段 + 落在哪条链"。</summary>
    public static void StartImplChainProbe(GamePlay.PlayerSkill psk, GamePlay.PlayerSkillChain chain)
    {
        try
        {
            if (CfgEnabled?.Value != true) return;

            int order = -1; string act = "";
            try { if (psk != null) { order = OrderOf(psk); act = ActionOf(psk); } } catch { }

            int cnt = 0, idx = -1;
            string owner = "";
            try
            {
                var list = chain?.SkillList;
                cnt = SafeCount(list);
                for (int i = 0; i < cnt; i++)
                {
                    var sk = TryGet(list, i);
                    if (sk != null && ReferenceEquals(sk.Pointer, psk?.Pointer)) { idx = i; break; }
                }
                owner = chain == null ? "<null链>" : "0x" + chain.Pointer.ToInt64().ToString("X");
            }
            catch { }

            string key = order + "|" + act + "|" + cnt + "|" + idx;
            if (_implSeen.Count >= 400 || !_implSeen.Add(key)) return;

            Plugin.Log?.LogInfo($"[连段模组:汇点] SkillStartImplement 起了 \\"{act}\\" Order={order}  " +
                                $"所在链段数={cnt} index={idx} 链={owner}");
        }
        catch (Exception e) { Once("StartImplChainProbe", e); }
    }

    // ---------------- 全量起招探针 ----------------'''
s = s.replace(C, D, 1)

io.open(P, "w", encoding="utf-8").write(s)
print("汇点探针已加入")
