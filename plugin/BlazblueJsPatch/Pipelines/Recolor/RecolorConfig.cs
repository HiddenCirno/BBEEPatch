using System;
using BepInEx.Configuration;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 特效换色管线的全部配置项。
///
/// 集中在一个类里, 是因为这条管线之前散着出过事:
/// 【总开关只拦住了粒子路, 插值器路照样染色】——
/// 于是"关掉换色后特效还在变色", 排查时被引导到"有持久化缓存"这种错误结论上。
/// 现在所有入口都从这里读同一个 <see cref="Enabled"/>, 没有第二条判据。
///
/// ⚠ 配置项本身还是由 Plugin.cs 绑定(集中在 <c>Core/ConfigSchema</c> 的换色段),
///   这里只声明字段 —— 免得配置分散到每个文件里, 又变成"找不到在哪配"。
/// </summary>
internal static class RecolorConfig
{
    // ---- 总开关与目标色 ----
    internal static ConfigEntry<bool> Enabled;          // 特效换色 / RecolorEffect
    internal static ConfigEntry<string> Hex;            // 目标颜色
    internal static ConfigEntry<bool> KeepBrightness;
    internal static ConfigEntry<double> BrightnessScale;
    /// <summary>`Tint()` 里亮度倍率的上限（夹断时会留痕，不静默截断）。见 TintBrush.Luma 的长注释。</summary>
    internal static ConfigEntry<double> MaxScale;
    /// <summary>`Tint()` 的保亮度口径: blend(几何平均, 默认) / luma(保感知亮度) / peak(保最大通道)。
    /// 见 TintBrush.Luma 的长注释 —— 这是个无解的取舍, 取决于目标色的饱和程度。</summary>
    internal static ConfigEntry<string> BrightMode;
    /// <summary>点名子物体(TintObjects)那条路用的口径。默认 peak —— 见 ObjectTarget 的注释。</summary>
    internal static ConfigEntry<string> ObjectMode;

    // ---- 残影路(Trail) ----
    /// <summary>残影路总开关。只对名字命中 TrailNameFilter 的 trail 生效。</summary>
    internal static ConfigEntry<bool> TrailTint;
    /// <summary>残影路名单(逗号分隔, 子串匹配)。留空 = 这条路什么都不做。</summary>
    internal static ConfigEntry<string> TrailNameFilter;

    // ---- 着色通道 ----
    internal static ConfigEntry<string> TintProps;

    /// <summary>材质/拖影路(写 renderer.material)。**默认关** —— 见 TintBrush.TintMaterials 的事故记录。</summary>
    internal static ConfigEntry<bool> TintRenderers;

    /// <summary>★ 火焰路(窄口径): 只对 shader 名含 "Flame" 的渲染器写实例材质。
    /// **默认开** —— 尾焰的颜色只在材质上、且没有插值器, 这是唯一够得着的路。
    /// 见 TintBrush.TintFlameMaterials。</summary>
    internal static ConfigEntry<bool> TintFlame;

    // ---- ★ 副贴图着色路（照官方换色配方直写材质）----
    /// <summary>
    /// ★★ 副贴图着色路开关。
    ///
    /// 为什么需要它（2026-10-04 深夜，离线拆解得到的**权威依据**）：
    /// 那些"原色里根本没有颜色插值器"的特效（典型：消耗 MP 的冲刺 `es_attackAir_02`），
    /// 皮肤换色的做法是**新增一条 `_SubTexTintColor` 插值器**（实测 esskin_06 = (1.227,0.735,0.826)）。
    /// 而我们的插值器路只能改**已存在**的插值器 ⇒ 对这类特效天生够不着。
    /// ⇒ 照官方做法，直接把那个属性写到**特效自己的实例材质**上。
    /// </summary>
    internal static ConfigEntry<bool> SubTex;
    /// <summary>点名特效（逗号分隔）。只对名字命中的特效写材质 —— 范围越小越安全。</summary>
    internal static ConfigEntry<string> SubTexEffects;
    /// <summary>要写的属性（默认只有 `_SubTexTintColor`，与官方配方一致）。</summary>
    internal static ConfigEntry<string> SubTexProps;

    // ---- ★★ 按【子物体名】下刀（2026-10-05 补上的一条规矩）----
    //
    // 为什么原来的规矩不够：
    //   管线原来只按**特效 hub 的名字**放行（`SubTexEffects` = es_attackAir_01 …），
    //   于是"名字不在清单里的特效"下面那些**共享子预制**永远够不着 —— 哪怕它们才是真正的载体。
    //
    // 离线铁证（`tools/_skindiff_out.txt`，4 套官方皮肤全表差异）：
    //   `guangzhu01` 的 `MaterialTinter._TintColor` end 值
    //     原色 = (5.647, 6.525, 11.984)   ← **蓝通道是红通道的 2.1 倍**，就是那道蓝白电弧
    //     esskin_06 → (2.119, 1.369, 1.421)   浅粉白
    //     esskin_10 → (1.399, 2.188, 1.311)   浅绿白
    //     esskin_12 → (5.609, 5.907, 4.987)   米白
    //   而 `guangzhu02` / `ring02` 在四套皮肤的差异里**一次都没出现** ⇒ 官方根本没动它们。
    //   ⚠ 另外：`guangzhu01` 是**共享子预制**（同时被 es_stand_01 / es_fall_04 用），
    //     这就是用户说的"渲染器是共用的" —— 也正因为共用，**绝不能靠 SetActive 隔离**。
    /// <summary>点名子物体（逗号分隔，取名字最后一段精确比对）。命中就改写**它自己**的着色插值器。</summary>
    internal static ConfigEntry<string> TintObjects;
    /// <summary>只在这些特效(hub 名)的子树里找 `TintObjects`（空 = 全场任何子树都找，慎用）。</summary>
    internal static ConfigEntry<string> TintObjectHosts;
    /// <summary>找不到时就打一条说明（排查看"到底访问到没有"）。</summary>
    internal static ConfigEntry<bool> TintObjectLog;

    // ---- 资产重定向（"推倒重来"的核心）----
    /// <summary>把原色特效的加载请求改成皮肤特效那一份（见 RecolorPipeline.LoadAssetPrefix）。</summary>
    internal static ConfigEntry<bool> ForceSkinSet;
    /// <summary>用哪一套皮肤资产（esskin_06 / 10 / 12 / 13）。12、13 是完整覆盖的。</summary>
    internal static ConfigEntry<string> ForceSkinSetName;
    /// <summary>是否用【私有副本】: 复制一套资产再染色, 不污染游戏自己的资产。</summary>
    internal static ConfigEntry<bool> PrivateCopy;
    /// <summary>粒子路开关（二分用）。关掉 = 不写 ParticleSystem.startColor。</summary>
    internal static ConfigEntry<bool> TintOnParticles;
    /// <summary>插值器路开关（二分用）。关掉 = 不写 MaterialColorInterpolator。</summary>
    internal static ConfigEntry<bool> TintOnInterpolators;
    /// <summary>是否直接写【共享】材质资产(会外溢到所有使用者)。默认关 —— 副本路已经覆盖。</summary>
    internal static ConfigEntry<bool> TintSharedMaterials;

    // ---- 过滤策略 ----
    /// <summary>★ 放行名单：命中的名字**越过** `AssetExclude` 与名字过滤。
    /// 用途：`Avatar` 整族被排除是为了挡住"角色被染色"，但冲刺那道光影
    /// (`Role/Avatar/Avatar_DashShadow_*` / `Avatar_TrailLoop_*`) 也在这族里，
    /// 于是它一直保持原色 —— 用这个名单单独放行它们，不必放宽整个 Avatar。
    /// ⚠ 不越过屏幕空间判据与你自己的 `TintExclude`（那两道是安全阀）。</summary>
    internal static ConfigEntry<string> AllowList;
    internal static ConfigEntry<string> NameFilter;      // 战斗特效: 名字含此串
    internal static ConfigEntry<string> TintNameFilter;  // 插值器路的名字过滤
    internal static ConfigEntry<string> TintExclude;
    internal static ConfigEntry<bool> SkipScreenSpace;
    internal static ConfigEntry<bool> FollowLive;        // ★ 新增: 配置改了立刻追染正在播的特效

    // ---- JIT prefab 路 ----
    internal static ConfigEntry<string> AssetFilter;
    internal static ConfigEntry<string> AssetExclude;

    // ---- 角色本体(默认关) ----
    internal static ConfigEntry<bool> RecolorCharacter;
    internal static ConfigEntry<int> ActorId;
    internal static ConfigEntry<string> SkinFilter;

    // ---- 诊断 ----
    internal static ConfigEntry<bool> LogOrigin;
    /// <summary>叠色探针要点名的材质属性（逗号分隔）。原来写死 `_AddColor` 那一组 —— 换个目标就得改代码重编。</summary>
    internal static ConfigEntry<string> RefCounterProps;
    /// <summary>叠色探针最多打几行明细。</summary>
    internal static ConfigEntry<int> RefCounterMax;

    internal static bool On => Enabled?.Value == true;
    internal static bool Verbose => LogOrigin?.Value == true;
    /// <summary>追染开关。配了就得有人读 —— 一个"配了没人用"的开关比没有更坏:
    /// 用户关掉它以为关了, 行为却一点没变, 于是开始怀疑"是不是有缓存"这类错误结论。</summary>
    internal static bool FollowLiveOn => FollowLive?.Value != false;

    // ------------------------------------------------------------------ 目标色

    /// <summary>
    /// 目标色 + 策略指纹。
    ///
    /// 指纹变了 = 需要把【已经在播的】特效重新染一遍(见 TintRegistry)。
    /// 只比颜色是不够的: 亮度策略/通道列表/名字过滤都可能单独改。
    /// </summary>
    internal struct Target
    {
        internal Color Color;
        internal bool KeepBright;
        internal float Scale;
        internal string Props;
        /// <summary>该 Target 用的亮度口径（peak/hue/luma/capped）。</summary>
        internal string Mode;

        internal string Fingerprint =>
            Cfg.ToHex(Color) + "|" + (KeepBright ? 1 : 0) + "|" + Scale.ToString("0.###") + "|" + Props;
    }

    private static Target _cached;
    private static Target _objCached;
    private static int _revision;
    private static string _seenHex, _seenProps;
    private static bool _seenKeep;
    private static float _seenScale = float.NaN;
    private static float _seenMax = float.NaN;
    private static string _seenMode;
    private static string _seenObjMode;

    /// <summary>
    /// 策略版本号。
    ///
    /// ⚠ 这个属性【每帧 × 每个在播特效】都会被读一次(追染搭在 VFXEffectBase.Update 上),
    ///   所以它必须**零分配**: 直接比原始配置值, 不去拼指纹字符串。
    ///   上一版思路是拼一个 "RRGGBB|1|1.5|_TintColor" 的指纹再比 —— 那等于每帧造几千个字符串,
    ///   纯粹给 GC 找事。只有真的变了才自增。
    /// </summary>
    internal static int Revision
    {
        get
        {
            var hex = Hex?.Value;
            var props = TintProps?.Value ?? "_TintColor";
            var keep = KeepBrightness?.Value != false;
            var scale = (float)(BrightnessScale?.Value);
            if (scale <= 0f) scale = 1f;
            // ★ TintMaxScale 也要进 Revision —— 否则改了它不会触发追染,
            //   已经在播的特效会保持旧倍率（违背"配置改了要立刻变色"这条既有约定）。
            var maxs = (float)(MaxScale?.Value);
            if (maxs <= 1f) maxs = 32f;
            var mode = BrightMode?.Value ?? "peak";    // 兜底 = 旧公式, 别写别的
            var objMode = ObjectMode?.Value ?? "peak";

            bool changed = !string.Equals(hex, _seenHex, StringComparison.Ordinal)
                        || !string.Equals(props, _seenProps, StringComparison.Ordinal)
                        || keep != _seenKeep
                        || Math.Abs(scale - _seenScale) > 1e-6f
                        || Math.Abs(maxs - _seenMax) > 1e-6f
                        || !string.Equals(mode, _seenMode, StringComparison.Ordinal)
                        || !string.Equals(objMode, _seenObjMode, StringComparison.Ordinal);
            if (!changed) return _revision;

            _seenHex = hex; _seenProps = props; _seenKeep = keep; _seenScale = scale;
            _seenMax = maxs; _seenMode = mode; _seenObjMode = objMode;
            _cached = new Target
            {
                Color = Cfg.ParseColor(hex, Color.magenta),
                KeepBright = keep,
                Scale = scale,
                Props = props,
                Mode = mode,
            };
            _objCached = _cached;
            _objCached.Mode = objMode;
            _revision++;
            return _revision;
        }
    }

    /// <summary>当前目标色/策略。先刷新一次 <see cref="Revision"/> 保证是最新的。</summary>
    internal static Target Current
    {
        get { _ = Revision; return _cached; }
    }

    /// <summary>
    /// ★【点名子物体专用的 Target】—— `TintObjects` 那条路走它。
    /// 两类载体的"正确口径"不同, 所以必须有两套:
    ///   · **本来就有颜色的**(S>0) ⇒ 全局口径(建议 `hue`, 官方式: 换色相/保结构/保 alpha)
    ///   · **中性亮度乘数**(S=0) 但确实想让它着色(ring02/glow01/guangzhu01/02) ⇒ 必须 `peak`
    ///     (因为 `hue` 对 S=0 **不动** —— 那正是官方皮肤不碰 ring02 的原因，
    ///      用它等于把今天做出来的电弧换色整个撤销)
    /// </summary>
    internal static Target ObjectTarget { get { _ = Revision; return _objCached; } }

    /// <summary>目标通道列表(逗号分隔, 已预切)。</summary>
    internal static string[] PropList => Cfg.List(Current.Props);
}
