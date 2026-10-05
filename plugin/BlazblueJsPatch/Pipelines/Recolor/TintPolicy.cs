using System;
using GamePlay;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 「这个特效该不该染」—— 换色管线唯一的判据入口。
///
/// ★ 为什么过滤必须集中
/// ──────────────────
/// 这条管线上出过的每一次事故, 根因都是"某个入口漏了一道判据":
///   · 插值器路一开始一条过滤都没有 → 把转场用的全屏白遮罩
///     `Effect/Prefab/UI/Loading/Loading_0`(_TintColor=(1,1,1,0.5)) 也染了,
///     表现是【整个画面蒙了一层紫色滤镜, 而且常驻不消失】。
///   · 屏幕空间判据漏掉时, SP 技能那层全屏叠加特效会把画面变成色罩。
/// 所以判据只写一份, 所有入口都调它 —— 加过滤 = 只加一处, 不会出现"粒子路挡住了、插值器路没挡"。
///
/// ⚠ 名字过滤不能取"最顶层父节点": ES 的特效挂在玩家下面时名字会变成 "PlayerV2(Clone)",
///   既认不出是谁, 又会被过滤误杀。身份来源是最近的 VFXEffectHub 的节点名
///   (Role.Es.es_holdatk1_02 / es_AH_01(Clone)), 那才是特效自己的名字。
/// </summary>
internal static class TintPolicy
{
    private static Type _hubType;
    private static Type HubType
    {
        get
        {
            if (_hubType == null)
            {
                foreach (var n in new[] { "NOAH.VFX.VFXEffectHub", "VFXEffectHub" })
                {
                    _hubType = AccessTools_TypeByName(n);
                    if (_hubType != null) break;
                }
            }
            return _hubType;
        }
    }

    private static Type AccessTools_TypeByName(string n)
    {
        try { return HarmonyLib.AccessTools.TypeByName(n); } catch { return null; }
    }

    /// <summary>
    /// ★ 放行名单（`TintAllowList`）—— 命中的名字**越过** `AssetExclude` 与名字过滤。
    ///
    /// 为什么需要它：`AssetExclude` 里的 `Avatar` 是个**整族**关键字（当初为了挡住
    /// "角色被染色"），可冲刺那几道光影也在这族里：
    ///   `Role/Avatar/Avatar_DashShadow_AP_01/02`（跟着骨骼跑的发光拖线，
    ///    材质 `_HighlightColor=(1.882,2.659,5.992)` 等 = HDR 发光蓝）
    ///   `Role/Avatar/Avatar_TrailLoop_AP_01`（`lizi01` 粒子，蓝/青绿）
    /// 于是它们一直保持原色 —— 用户看到的那道"蓝光"就是它。
    /// 有了放行名单就不必为了这一道光把整个 `Avatar` 解禁。
    ///
    /// ⚠ **不越过**屏幕空间判据（那是防全屏色罩的）与用户自己的 `TintExclude`（显式 kill 开关）。
    /// </summary>
    internal static bool Allowed(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        try
        {
            foreach (var k in Cfg.List(RecolorConfig.AllowList?.Value))
                if (k.Length > 0 && name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 常驻特效排除名单（`AssetExclude`）—— **所有入口共用**。
    /// 名单里的东西（`es_stand_01` / `es_stand_02` / `es_standby_001` 这类挂在角色身上的待机光效）
    /// 一旦被染, 表现就是"角色变成一个发光的球"（PROJECT_STATE 里记过）。
    /// </summary>
    internal static bool Excluded(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (Allowed(name)) return false;      // ★ 放行名单优先于排除名单
        try
        {
            foreach (var k in Cfg.List(RecolorConfig.AssetExclude?.Value))
                if (k.Length > 0 && name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        catch { }
        return false;
    }

    /// <summary>取特效自己的名字(最近的 VFXEffectHub 节点名), 取不到就退回组件自己的节点名。</summary>
    internal static string EffectName(object component)
    {
        try
        {
            var mb = Reflect.Cast<MonoBehaviour>(component);
            var tr = mb?.transform;
            if (tr == null) return "";

            if (HubType != null)
            {
                var ht = Il2CppInterop.Runtime.Il2CppType.From(HubType);
                for (int i = 0; i < 10 && tr != null; i++)
                {
                    Component c = null;
                    try { c = tr.GetComponent(ht); } catch { }
                    if (c != null)
                    {
                        var n = Reflect.Name(c.gameObject);
                        if (!string.IsNullOrEmpty(n)) return n;
                    }
                    try { tr = tr.parent; } catch { tr = null; }
                }
            }
            return Reflect.Name(mb?.gameObject);
        }
        catch { return ""; }
    }

    /// <summary>
    /// 【屏幕空间特效】—— 必须放过。
    /// 实测: SP 技能里挂着一层全屏叠加特效, 它的 MaterialTinter 也带 _TintColor,
    /// 一起改了就是全屏色罩。判据是 VFXEffectExtension.ScreenSpace
    /// (那个组件 [RequireComponent(VFXEffectHub)], 挂在 hub 根节点上), 所以沿父链向上找。
    /// </summary>
    internal static bool IsScreenSpace(object component)
    {
        if (RecolorConfig.SkipScreenSpace?.Value == false) return false;
        try
        {
            var mb = Reflect.Cast<MonoBehaviour>(component);
            var tr = mb?.transform;
            if (tr == null) return false;

            var t = Il2CppInterop.Runtime.Il2CppType.From(typeof(VFXEffectExtension));
            for (int depth = 0; tr != null && depth < 6; depth++)
            {
                GameObject go = null;
                try { go = tr.gameObject; } catch { }
                if (go != null)
                {
                    var comps = go.GetComponents(t);
                    foreach (var c in Reflect.Items(comps))
                    {
                        var e = Reflect.Cast<VFXEffectExtension>(c);
                        if (e != null && e.ScreenSpace) return true;
                    }
                }
                try { tr = tr.parent; } catch { tr = null; }
            }
        }
        catch (Exception e) { LogEx.Err("TintPolicy.IsScreenSpace", e); }
        return false;
    }

    /// <summary>
    /// 插值器路的准入判定。返回 false = 不染, 并把原因记一次日志。
    ///
    /// 判定顺序(便宜的先来):
    ///   屏幕空间 → 手动排除名单 → 名字过滤
    /// </summary>
    internal static bool AllowInterpolator(object instance, string where, out string rootName)
    {
        rootName = EffectName(instance);

        if (IsScreenSpace(instance))
        {
            LogEx.Once("recolor|skip|screenspace|" + where,
                       $"[特效换色] {where} 跳过【屏幕空间】特效(全屏色罩), 不染色");
            return false;
        }

        if (Excluded(rootName))
        {
            LogEx.Once("recolor|skip|assetex|" + Reflect.Normalize(rootName),
                       $"[特效换色] \"{rootName}\" 在 AssetExclude 里(常驻特效), 任何入口都不染");
            return false;
        }
        var ex = RecolorConfig.TintExclude?.Value;
        if (!string.IsNullOrWhiteSpace(ex) && rootName.Length > 0)
        {
            foreach (var k in Cfg.List(ex))
                if (rootName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    LogEx.Once("recolor|skip|exclude|" + k,
                               $"[特效换色] \"{rootName}\" 命中排除名单 \"{k}\", 跳过");
                    return false;
                }
        }

        var kw = RecolorConfig.TintNameFilter?.Value;
        // ★ 放行名单：越过名字过滤(但它已经过了上面的屏幕空间 / AssetExclude / TintExclude 三道)
        if (Allowed(rootName))
        {
            LogEx.Once("recolor|allow|" + Reflect.Normalize(rootName),
                       $"[特效换色] \"{rootName}\" 在放行名单里 —— 越过排除/名字过滤, 照染");
            return true;
        }
        // ★ 没填就沿用【战斗特效那份】关键字 —— 这两份曾经各走各的, 于是
        //   `silhouette_601` 过了战斗路那道门、却被这里的 "es_" 挡掉,
        //   表现成"画面上没变, 可日志说它'命中=True'"。判据只能有一份。
        if (string.IsNullOrWhiteSpace(kw)) kw = RecolorConfig.NameFilter?.Value;
        if (!string.IsNullOrWhiteSpace(kw))
        {
            // 取不到名字时【不杀】—— "读不到"和"不匹配"是两件事, 混在一起就是假阴性。
            if (rootName.Length > 0)
            {
                bool hit = false;
                foreach (var k in Cfg.List(kw))          // ★ 逗号分隔多关键字, 任一命中即可(和战斗路一致)
                    if (k.Length > 0 && rootName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                if (!hit)
                {
                    LogEx.Once("recolor|skip|name|" + Reflect.Normalize(rootName),
                               $"[特效换色] \"{rootName}\" 不含名字过滤 \"{kw.Trim()}\"(任一命中即可), 跳过 " +
                               $"(这条正是防 UI 转场遮罩被染色的)");
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// 战斗特效(createVisualEffect 路)的准入判定。
    ///
    /// ⚠ 只用 Owner==PlayerSelf 太严 —— 实测 ES 的攻击特效 (es_atkAir12_*, es_attackup_*)
    ///   的 Owner 全都不是 PlayerSelf(她的招式大量由影子/分身 Actor 打出), 32 条被误杀。
    ///   所以判据是【本地玩家 或 名字命中】。
    /// </summary>
    internal static bool AllowBattleEffect(ActorBase owner, string effectName)
    {
        bool isPlayer = owner != null && DashInvincible.IsLocalPlayerActor(owner);
        if (isPlayer) return true;

        // ⚠ 常驻特效(挂在角色身上的待机光效 es_stand_01 / es_stand_02 / es_standby_001)必须在
        // **所有入口**都排除 —— 以前只有资产路看 AssetExclude, 实例路照样染它 ⇒ 角色被染红。
        if (Excluded(effectName)) return false;
        // ★ 放行名单：Owner 不是玩家时(冲刺光影由别的系统拉起), 靠它越过名字过滤。
        if (Allowed(effectName))
        {
            LogEx.Once("recolor|allow|battle|" + Reflect.Normalize(effectName),
                       $"[特效换色] 战斗特效 \"{effectName}\" 在放行名单里 —— 照染" +
                       $"(Owner={(owner == null ? "null" : owner.GetType().Name)})");
            return true;
        }
        var kw = RecolorConfig.NameFilter?.Value ?? "es,hit_";
        if (string.IsNullOrWhiteSpace(kw)) return true;    // 留空 = 全都要
        // ★ 2026-10-04: 改成【逗号分隔的多个关键字】。原来只认一个 "es",
        //   于是"受击特效"(实例名就叫 hit_009 / hit_018, 没有 es 前缀)整族被跳过 ——
        //   用户报的"敌人受击/受击纹章没被染色"就是这一条。
        //   ⚠ 关键字用 "hit_" 带下划线而不是 "hit": 后者会误伤任何名字里含 hit 的东西
        //     (最典型的是 white —— w-hit-e)。
        foreach (var k in Cfg.List(kw))
            if (k.Length > 0 && effectName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;

        // ⚠ 被名字过滤挡掉也要留痕。这里静默返回 false 的话,
        //   "特效没变色"就同时对应【没人调它】和【被过滤了】, 两者分不开 ——
        //   这就是本项目反复栽的假阴性。按名字去重, 每个名字只报一次。
        LogEx.Once("recolor|skip|battlename|" + Reflect.Normalize(effectName),
                   $"[特效换色] 战斗特效 \"{effectName}\" 不含名字过滤 \"{kw.Trim()}\"(任一命中即可), 跳过 " +
                   $"(Owner={(owner == null ? "null" : owner.GetType().Name)}, 非本地玩家)");
        return false;
    }
}
