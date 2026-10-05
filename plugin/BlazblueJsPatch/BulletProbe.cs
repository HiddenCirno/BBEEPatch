using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Mathematics.FixedPoint;

namespace BlazblueJsPatch;

/// <summary>
/// 弹幕探针：把每一个「由本地玩家打出的弹幕」的配置打印出来。
///
/// 为什么要查弹幕
/// ──────────────
/// 之前一直在特效层找「纹章解放」的环，找不到 —— 因为方向本来就错了。
/// 本地化里对纹章的定义是：
///     「纹章：Es 独有的攻击方式，并且会出现在剑的弧光上」
///     「原地产生纹章，敌人靠近后绽放，可造成 N 点伤害并破霸体 M 秒」
/// **有伤害 + 有破霸体 = 它是一个带判定框的战斗实体，不是纯特效。**
///
/// 而本作里"能造成伤害、有位置、有生命周期"的实体就是 `GamePlay.BulletObj`
/// （继承 `ActorBase`，跟角色/敌人同一个基类，所以它有自己的动作状态机）。
/// 生成入口是 JS 里到处在用的：
///
///     BattleBase.Cur.BulletMgr.CreateBulletByParams(
///         ActorBase caster, int idx, Fp2 pos, Fp2 dir,
///         Fp damageScale, string startAction,
///         SkillActivateFixedPointWrap skillActivate, ParamSet paramSet)
///
///     (dump.cs:244746, 已确认签名)
///
/// 每个 idx 对应一条 `BulletConfigFixedPointWrap`，里面有：
///     Id / StartAction / Skin / LogicRes / RenderConf / Width / Height / Scale
/// 其中 **Skin 就是它的视觉 prefab 名** —— 如果这里打出的是 `es_AH_02` 之类，
/// 那就跟我们一直在日志里看到的特效名对上了，链就闭合了。
///
/// 这个探针同时解决三件事
/// ──────────────────────
/// 1) **识别**：纹章解放 / 贝德维尔的纹章到底是哪个 idx、哪个 Skin、哪个 startAction。
/// 2) **定位消失原因**：如果那一条弹幕**根本没被生成**，说明问题在上游（技能/触发条件），
///    不是我们的换色 —— 这能一次性洗清嫌疑，不用再猜。
/// 3) **为「1 静 + 8 动」打地基**：`dir` 是参数，所以再造 8 个只需要把方向旋转 45°×k；
///    生命周期与 idx 绑定的那条配置同源，天然"生命周期等同"。
///
/// 去重方式仍是**按内容去重**（不是"只记前 N 条"）—— 见 ActionProbe 里踩过的坑。
/// </summary>
internal static class BulletProbe
{
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;

    private const int SeenCap = 400;
    private static readonly HashSet<string> _seen = new HashSet<string>();
    private static int _crestCount;

    // 反射缓存：BulletObj.BulletConf (0x248) -> BulletConfigFixedPointWrap
    internal static MemberInfo _confMember;   // ★ DashAnatomy 要复用（弹幕的 StartAction 在 BulletConf 上）
    private static MemberInfo _posMember;
    private static MemberInfo _casterMember;

    public static int Apply(Harmony harmony)
    {
        var t = AccessTools.TypeByName("GamePlay.BulletMgr");
        if (t == null) { Plugin.Log?.LogWarning("  [弹幕探针] 找不到 GamePlay.BulletMgr"); return 0; }

        // 同 EsEmblemBurst: 挂私有汇点 createBulletImp, 别漏掉绕过 CreateBulletByParams 的调用方
        var m = AccessTools.Method(t, "createBulletImp") ?? AccessTools.Method(t, "CreateBulletByParams");
        if (m == null) { Plugin.Log?.LogWarning("  [弹幕探针] 找不到 BulletMgr.createBulletImp"); return 0; }

        var bo = AccessTools.TypeByName("GamePlay.BulletObj");
        if (bo != null)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _confMember = (MemberInfo)bo.GetField("BulletConf", F) ?? bo.GetProperty("BulletConf", F);
        }

        var ab = AccessTools.TypeByName("GamePlay.ActorBase");
        if (ab != null)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _posMember = (MemberInfo)ab.GetProperty("Position", F) ?? ab.GetField("Position", F);
            _casterMember = (MemberInfo)ab.GetProperty("Caster", F) ?? ab.GetField("Caster", F);
        }

        try
        {
            harmony.Patch(m, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(BulletProbe), nameof(Postfix))));
            Plugin.Log.LogInfo($"  [弹幕探针] 已挂钩 BulletMgr.{m.Name} (按 idx 去重打印)");
            return 1;
        }
        catch (Exception e)
        {
            Plugin.Log?.LogWarning($"  [弹幕探针] 挂钩失败: {e.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 形参名与 dump.cs 一致 (caster / idx / pos / dir / startAction)，Harmony 按名字绑定。
    ///
    /// ⚠ 关于"要不要把 Fp2 写进签名"：文件顶部旧注释说不要，理由是"`Fp` 在 interop 里解析不了"。
    ///   但那是 **`Fp`(标量)** 的问题，**`Fp2` 是安全的** ——
    ///   `EsEmblemBurst.CreatePostfix` 长期绑着 `Fp2 pos, Fp2 dir` 且工作正常。
    ///   所以这里只加 Fp2，**不加 `Fp damageScale`**（那个才是历史雷区）。
    ///   `damageScale` 用不上，不绑就没有风险。
    /// </summary>
    public static void Postfix(GamePlay.ActorBase caster, int idx, Fp2 pos, Fp2 dir,
                               string startAction, object __result)
    {
        try
        {
            if (CfgEnabled?.Value != true) return;

            // 只看玩家相关的。caster==null 的也要看 —— JS 里大量调用是传 null 的，
            // 而且玩家的招式有很多是由影子/分身 Actor 打出来的 (Owner 不是 PlayerSelf)。
            bool mine = caster == null || DashInvincible.IsLocalPlayerActor(caster);
            string casterDesc = caster == null ? "null" : caster.GetType().Name;
            if (caster != null)
            {
                var c = ReadMember(caster, _casterMember);
                if (c != null)
                {
                    bool cMine = DashInvincible.IsLocalPlayerActor(c as GamePlay.ActorBase);
                    if (!mine && !cMine) return;
                    mine = true;
                    casterDesc += $"->caster={c.GetType().Name}{(cMine ? "(玩家)" : "")}";
                }
            }
            // ⚠ 纹章那颗要【无视 caster 过滤】。
            //   实测 ES 的招式大量由影子/分身 Actor 打出, Owner 不是 PlayerSelf ——
            //   沿用"只认本地玩家"会让空中那批纹章连日志都不出现,
            //   于是看起来像"空中没触发", 实际是探针自己把它滤掉了。
            bool crestId = idx == (EsEmblemBurst.CfgBulletId?.Value ?? 10340101);
            if (!mine && !crestId) return;

            // 纹章(10340101) **每次都记**, 不走去重 ——
            // 去重会把重复的吃掉, 而"纹章到底生成了几次"恰恰是排查翅膀消失的关键数字:
            // 如果按了技能却一次都没有, 那就是"没生成", 而不是"生成了但看不见"。
            bool isCrest = idx == (EsEmblemBurst.CfgBulletId?.Value ?? 10340101);
            string key = idx + "|" + (startAction ?? "");
            if (isCrest)
            {
                _crestCount++;
                if (_crestCount > 200) return;   // 上级保护, 正常一局不会到这个数
            }
            else if (_seen.Count >= SeenCap || !_seen.Add(key)) return;

            // 从 BulletConf 里掏 Skin / LogicRes —— Skin 就是视觉 prefab 名
            string confDesc = "";
            var conf = ReadMember(__result, _confMember);
            if (conf != null)
            {
                string skin = (ReadMember(conf, FindProp(conf, "Skin")) as string) ?? "";
                string logic = (ReadMember(conf, FindProp(conf, "LogicRes")) as string) ?? "";
                string sa = (ReadMember(conf, FindProp(conf, "StartAction")) as string) ?? "";
                confDesc = $" conf[StartAction=\"{sa}\" Skin=\"{skin}\" LogicRes=\"{logic}\"]";
            }
            else confDesc = " (BulletConf 读不到)";

            // ★ 回写给 F9「弹幕实验台」的"最近捕获"区。
            //   放在这里是因为只有这一处同时拿得到 idx / startAction / LogicRes 三件套。
            try
            {
                BulletLab.LastIdx = idx;
                BulletLab.LastName = startAction ?? "";
                BulletLab.LastRes = conf != null
                    ? ((ReadMember(conf, FindProp(conf, "LogicRes")) as string) ?? "")
                    : "";
                // 方向/位置：aH2EX、aH3EX 这类是**有方向性**的纹章，
                // 想把它嫁接到别的动作上就必须知道原生 dir 是多少。
                try { BulletLab.LastPos = pos.ToString(); } catch { BulletLab.LastPos = "?"; }
                try { BulletLab.LastDir = dir.ToString(); } catch { BulletLab.LastDir = "?"; }
                BulletLab.LastCaptureCount++;
            }
            catch { }

            // pos/dir 也打出来 —— 有方向性的纹章(aH2EX/aH3EX)靠它才知道原生朝向。
            // 去重是按 (idx|startAction) 做的，所以每个动作第一次出现时打一次，不会刷屏。
            string posS = "?", dirS = "?";
            try { posS = pos.ToString(); } catch { }
            try { dirS = dir.ToString(); } catch { }

            // ★ caster 的世界坐标 —— **没有它，pos 就只是一串读不懂的世界坐标**。
            //   我们真正要的是"弹幕相对角色从哪个点出去"，即 pos - casterPos 的偏移。
            //   实测: F9 重放会把 pos 抄成模板的坐标(看得出来但推不出偏移)，
            //   只有原生发射 + 同时有 casterPos 才能算出偏移。
            string castS = "?";
            try { if (caster != null) { var cp = ReadMember(caster, _posMember); if (cp != null) castS = cp.ToString(); } }
            catch { }

            Plugin.Log?.LogInfo($"[弹幕探针] idx={idx} startAction=\"{startAction}\" caster={casterDesc}" +
                                $"{confDesc} pos={posS} dir={dirS} casterPos={castS}" +
                                (isCrest ? $"  【纹章 #{_crestCount}】" : $"  (已见 {_seen.Count} 种)"));
        }
        catch { }
    }

    internal static PropertyInfo FindProp(object obj, string name)
    {
        try { return obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance); }
        catch { return null; }
    }

    internal static object ReadMember(object obj, MemberInfo mi)
    {
        if (obj == null || mi == null) return null;
        try
        {
            switch (mi)
            {
                case PropertyInfo p: return p.GetValue(obj);
                case FieldInfo f: return f.GetValue(obj);
            }
        }
        catch { }
        return null;
    }
}
