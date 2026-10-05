using System;
using System.Reflection;
using HarmonyLib;

namespace BlazblueJsPatch;

/// <summary>
/// 让技能不消耗 MP。
///
/// MP 消耗是怎么被表达的（这段是查出来的，不是猜的）
/// ────────────────────────────────────────────────
/// 1) 配置层: `BaseActorPotentialConf` 的**第 14 号 protobuf 字段** = `SkillCost`。
///    实测该字段取值 10 / 15 / 20 / 25 / 30 / 35 / 50 / 100，
///    而本作 MP 上限正好是 100（`GetActorMaxAttrValue` 里 `case EAttrType.Mp: return 100`）
///    —— 562 条潜能里有 72 条带非零值。**这个字段就是 MP 消耗。**
///
/// 2) 运行层: 技能在运行时是 `GamePlay.PlayerSkill`，它带:
///        public List<PlayerSkillMpSlotCache> MpLimitCaches;   // 0x50
///        public bool MpLimitValid  { get; }
///        public Fp   MpLimitCached { get; }
///    其中
///        public struct PlayerSkillMpSlotCache { int Slot; Fp MpLimit; Fp MpChange; Fp MpChangeLowbound; }
///
///    `get_MpLimitValid` 的机器码只有一句判据:
///        mov rax, [rbx+0x50]          ; MpLimitCaches
///        cmp dword ptr [rax+0x18], 0  ; List<T>._size
///        setg al                      ; return Count > 0
///    —— 即 **"有 MP 限制条目 == 这个技能耗 MP"**。
///
/// 3) 谁在读它（xref 结果，共 5 处）:
///        GamePlay.GetSkillCastRequires(curSkill, plo, out …, out groundValid, out mpValid)  ← 施放门槛
///        GamePlay.IsSkillReady(skill, player)                                                ← 就绪判定
///        BattleSkillBtnList.UpdateCtrls()                                                    ← 按钮状态
///        GamePlay.GetSkillMpProgress(skill, player, progressList)                            ← UI 进度
///        (PuerTS 的 G_MpLimitValid)
///
/// 所以**施放门槛就是 `MpLimitValid`**。把它钉成 false，技能就不再要求 MP。
///
/// ⚠ 已知边界（写清楚，免得当成 bug）
/// ────────────────────────────────
/// · 这解决的是"**能不能放**"。MP 的**数值扣除**走的是数据驱动的触发器系统
///   （`ActorAttrMgr.ChangeMPBy` 的 86 个调用者里绝大多数是 `initFuncsChecked` 的 lambda），
///   不经过这两个属性。所以可能出现"技能随便放、但 MP 条还是会掉"。
///   真出现的话，下一步是精确挂扣除点，而不是继续在这两个属性上打转。
///
/// · `PlayerSkill.IsCostSp()` 说明技能还分**SP 消耗**一类。这个类只管 MP，
///   SP 技能不受影响 —— 需要的话另说（机制同理，但要先找到 SP 的闸门）。
/// </summary>
internal static class SkillCostTweak
{
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgNoMpCost;

    private static bool On => CfgNoMpCost?.Value != false;

    public static int Apply(Harmony harmony)
    {
        var t = AccessTools.TypeByName("GamePlay.PlayerSkill");
        if (t == null) { Plugin.Log?.LogWarning("  [技能无耗] 找不到 GamePlay.PlayerSkill"); return 0; }

        int n = 0;

        // ---- 1. 施放门槛: MpLimitValid -> false ----
        var valid = t.GetProperty("MpLimitValid", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
        if (valid != null)
        {
            try
            {
                harmony.Patch(valid, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(SkillCostTweak), nameof(MpLimitValidPostfix))));
                Plugin.Log.LogInfo("  [技能无耗] 挂钩 PlayerSkill.MpLimitValid -> false (施放不再要求 MP)");
                n++;
            }
            catch (Exception e) { Plugin.Log?.LogWarning($"  [技能无耗] 挂 MpLimitValid 失败: {e.Message}"); }
        }
        else Plugin.Log?.LogWarning("  [技能无耗] 找不到 PlayerSkill.MpLimitValid");

        return n;
    }

    public static void MpLimitValidPostfix(ref bool __result)
    {
        if (On) __result = false;
    }
}
