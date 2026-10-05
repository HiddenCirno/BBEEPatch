using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace BlazblueJsPatch;

[BepInPlugin(Guid, "BBEE JS Patcher", "1.0.0")]
public class Plugin : BasePlugin
{
    public const string Guid = "ace.bbee.jspatch";

    internal static new ManualLogSource Log;
    internal static string Root;

    // ---- 模块级总开关（排查用）----
    // ⚠ 这些开关是【根本不挂载】，不是"挂上之后不干活"。
    //   区别很重要: 配置项关掉只是让代码路径提前 return, 万一有哪条写路径漏了检查,
    //   排查就会被误导(这个坑本项目已经栽过一次: RecolorEffect=false 只拦住了粒子路)。
    //   要判断"某个模块是不是罪魁", 必须用不挂钩子这种没有漏洞的方式。
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgMountJs;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgMountRecolor;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgMountCombat;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgMountExtra;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgMountEs;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgMountImgui;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgMountInput;
    /// <summary>总开关登记表(cfg 键名 → 项)。ModuleHost 靠它决定"根本不挂载"。</summary>
    internal static readonly Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>> MountSwitches
        = new Dictionary<string, BepInEx.Configuration.ConfigEntry<bool>>(StringComparer.Ordinal);

    /// <summary>供 ConfigPanel 遍历/保存用 —— BasePlugin.Config 是实例成员, 存一份静态引用。</summary>
    internal static BepInEx.Configuration.ConfigFile CfgFile;
    /// <summary>cfg 文件名, 只用于面板上的提示文案。</summary>
    internal static string CfgFileName = "(未加载)";

    public override void Load()
    {
        Log = base.Log;
        CfgFile = Config;
        try { CfgFileName = Path.GetFileName(Config.ConfigFilePath); } catch { }
        Root = Path.Combine(Paths.ConfigPath, "BlazblueJsPatch");
        Directory.CreateDirectory(Root);

        // ---- 模块级总开关 (排查"是哪个模块破坏了某个表现"用) ----
        var msec = "总开关";
        try
        {
            CfgMountJs = Config.Bind(msec, "MountJsPatches", true,
                "JS 层补丁(潜能/皮肤解锁等)。关掉 = 这些补丁完全不生效。");
            CfgMountRecolor = Config.Bind(msec, "MountRecolor", true,
                "特效换色模块。关掉 = 完全不挂钩子(不是配置里关, 是根本不挂)。");
            CfgMountCombat = Config.Bind(msec, "MountCombat", true,
                "战斗逻辑类: 冲刺无敌 / 完美闪避 / 跳跃冲刺互重置 / 技能无耗。");
            CfgMountImgui = Config.Bind(msec, "MountImgui", true,
                "IMGUI 兼容层(顶掉被 strip 的引擎 GUI 方法, 让 ConfigurationManager 不报错)。\n" +
                "只碰 GUI/GUILayout/GUIStyle, 正常与 3D 渲染无关, 但排查时一并纳入。");
            CfgMountInput = Config.Bind(msec, "MountInput", true,
                "面板打开时屏蔽游戏输入。只在面板打开时生效。");
            CfgMountExtra = Config.Bind(msec, "MountExtra", true,
                "诊断模块: 各类只读探针与转储(动作记录 / 动作结构 / 弹幕探针 / 贝德维尔)。\n" +
                "这些只观察不改行为, 平时可以整组关掉, 日志会干净很多。");
            CfgMountEs = Config.Bind(msec, "MountEsMech", true,
                "ES 机体性能: 动作变速 / 连段模组 / 纹章解放。\n" +
                "★ 单独给一个开关, 是因为这三个都属于「改机体表现」,\n" +
                "  和只读诊断混在一个 MountExtra 里的话, 想单独验证某一条机体改动就必须连诊断一起背上。");

            MountSwitches["MountJsPatches"] = CfgMountJs;
            MountSwitches["MountRecolor"] = CfgMountRecolor;
            MountSwitches["MountCombat"] = CfgMountCombat;
            MountSwitches["MountImgui"] = CfgMountImgui;
            MountSwitches["MountInput"] = CfgMountInput;
            MountSwitches["MountExtra"] = CfgMountExtra;
            MountSwitches["MountEsMech"] = CfgMountEs;
        }
        catch (Exception e) { Log.LogWarning($"读取总开关失败: {e.Message}"); }

        if (CfgMountJs?.Value != false) JsPatchManager.Init(Root);
        else Log.LogWarning(" [总开关] JS 补丁已停用");

        // ---- 冲刺无敌 可调参数 (BepInEx/config/BlazblueJsPatch.cfg) ----
        // 存的是 ConfigEntry 本身, 运行时读 .Value —— 配合 ConfigurationManager(F12) 可实时生效
        var sec = "冲刺无敌";
        try
        {
            DashInvincible.CfgEnabled = Config.Bind(sec, "Enabled", true, "是否启用冲刺无敌");
            DashInvincible.CfgKeyword = Config.Bind(sec, "ActionKeyword", "dash",
                "动作名关键字(不分大小写)。凡是动作名含此串的都算冲刺动作, 例如 es_dash / es_dashAir / es_dashend");
            DashInvincible.CfgTail = Config.Bind(sec, "TailSeconds", 0.2,
                "冲刺动作结束后, 仍然无敌的时长(秒)。本作逻辑帧固定 30fps, 0.2 秒 = 6 个逻辑帧");
            DashInvincible.CfgJumpEnabled = Config.Bind(sec, "JumpInvincible", true,
                "跳跃也享受和冲刺同款的全程无敌(动作进行中一直无敌 + 结束后 TailSeconds 尾巴)。\n" +
                "用户要的\"前0.2秒-全程-后0.2秒\"就是这个形状: 开头那段本来就在\"全程\"里。\n" +
                "两族共用一套状态, 所以冲刺结束 0.2s 内起跳, 无敌是连着的。");
            DashInvincible.CfgJumpKeyword = Config.Bind(sec, "JumpActionKeyword", "jump",
                "跳跃族动作名关键字(不分大小写)。实测动作名干净: jump / jump2 / jump3");
            DashInvincible.CfgJumpExclude = Config.Bind(sec, "JumpExcludeKeywords", "",
                "动作名含这些串的【不算跳跃】(逗号分隔)。默认空 —— 目前没发现误伤。\n" +
                "留这个键是因为关键词子串误伤在本项目栽过多次(UltraDash / dashAtk0), " +
                "以后真踩到就地加, 不用改代码。");
            DashInvincible.CfgExclude = Config.Bind(sec, "ActionExcludeKeywords", "Ultra,UD,UDA",
                "动作名含这些串的【不算冲刺】, 逗号分隔。\n" +
                "必须留这个名单: 贝德维尔(Ultra)的动作全带 Dash(UltraDashEX / UltraDashAirEX / UDA),\n" +
                "只用 dash 子串匹配会让整个 Ultra 演出期间玩家无敌 ——\n" +
                "而本作命中结算时只要一方无敌就整个跳过, Ultra 二段的命中会被吃掉。\n" +
                "实测症状: 贝德维尔的翅膀纹章消失(它正是二段命中时沿轨迹释放的)。");
            DashInvincible.CfgLevel = Config.Bind(sec, "Level", 999,
                "把无敌等级抬高到这个值(需大于敌人攻击的 InvincipalBreak 破无敌值)");
            // ---- 特效/纹章换色 ----
            var esec = "特效换色";
            RecolorConfig.Enabled = Config.Bind(esec, "RecolorEffect", true,
                "【攻击特效】染色 —— 真正的目标。\n" +
                "实测: 特效颜色烘在 prefab 的粒子系统(startColor/ColorModule)里, 材质是共享的。\n" +
                "所以改的是每个特效【实例】上的粒子系统, 共享材质一个字节都不动。\n" +
                "【总开关】同时管住粒子路和插值器路(材质着色)。关掉 = 完全不改色。");
            RecolorConfig.KeepBrightness = Config.Bind(esec, "KeepBrightness", true,
                "换色时按原颜色的亮度等比缩放, 避免变成刺眼的发光球。");
            RecolorConfig.BrightMode = Config.Bind(esec, "TintBrightMode", "peak",
                "★【保亮度口径】peak(默认) / luma / capped —— 「保亮度」保的是什么, 是个无解的取舍:\n" +
                "  · 淡色目标(如 A0F0C0): max ≈ Luma ⇒ 三种几乎一样 ⇒ 怎么都对\n" +
                "  · 饱和目标(如 CD00F0): max 远大于 Luma(3.9:1) ⇒ 三种差很远:\n" +
                "      - peak ⇒ 峰值不变(不过曝), 但整体偏暗；★ 默认, 就是旧的公式\n" +
                "      - luma ⇒ 亮度一致, 但峰值通道被推到 ~3.9 倍 ⇒ bloom 下刺眼\n" +
                "      - capped = min(luma, peak): 保感知亮度但峰值不超原色\n" +
                "◆ 2026-10-05 用户拍板: **用旧的 peak**。「大不了不用高饱和色」——\n" +
                "  代价明确: 高饱和目标色(CD00F0 这种)会偏暗; 换色时避开高饱和即是。\n" +
                "  (官方皮肤的换色目标色 max/Luma 都在 1.0~1.45, 天生就是淡色 —— 同理。)\n");
            RecolorConfig.ObjectMode = Config.Bind(esec, "TintObjectMode", "peak",
                "★【点名子物体(_TintObjects)那条路用的口径】默认 peak。\n" +
                "  为什么它必须和全局口径分开:\n" +
                "    · 点名名单里的都是**中性亮度乘数**(ring02 的 4.237 那种, S=0)\n" +
                "    · 而 `hue`(官方式)对 S=0 是**不动**的 —— 那正是官方皮肤从不碰 ring02 的原因\n" +
                "    ⇒ 全局口径设成 `hue` 时, 这条路必须仍然是 `peak`, 否则电弧的换色会被整个撤销。\n" +
                "  (两类载体的正确口径本来就不同, 不能只有一套。)");
            RecolorConfig.MaxScale = Config.Bind(esec, "TintMaxScale", 8.0,
                "Tint() 里亮度倍率的上限。**默认 8 = 与旧版完全一致**(旧代码里是写死的 8)。\n" +
                "  ⚠ 这道上限是**有代价的, 而且一直在起作用**: 实测 `glow_002_c` 需要 k=11.984,\n" +
                "    被压到 8 ⇒ 最亮的那些特写被**压暗**, 亮度分层被抹平。\n" +
                "    反过来: 把它调大 ⇒ 恢复原始亮度分层, 但最亮的会过曝成白心(\"有的绿有的偏白\")。\n" +
                "  ★ 与旧版唯一的区别: **夹断时会写一行日志**(旧版是静默截断) —— 这样\"被压掉了什么\"看得见。");
            RecolorConfig.BrightnessScale = Config.Bind(esec, "BrightnessScale", 1.0,
                "手动亮度倍率, 叠加在 KeepBrightness 之上。加色混合下洋红天然比暗蓝亮, " +
                "觉得刺眼就调小(如 0.5), 想更亮就调大。1.0 = 不额外调整。");
            RecolorConfig.TintOnParticles = Config.Bind(esec, "TintOnParticles", true,
                "【二分开关】粒子路: 写 ParticleSystem.startColor。关掉它再测一次, " +
                "用来判断[实心圆/角色变色]是不是这条造成的。");
            RecolorConfig.TintOnInterpolators = Config.Bind(esec, "TintOnInterpolators", true,
                "【二分开关】插值器路: 写 MaterialColorInterpolator 的 start/endValue。" +
                "关掉它再测一次, 用来判断症状是不是这条造成的。");
            RecolorConfig.PrivateCopy = Config.Bind(esec, "PrivateCopy", false,
                "【私有副本】把加载到的特效 prefab 复制一份, 只染副本, 再把副本当资产返回。\n" +
                "为什么必须: 材质是【全局共享】的 —— 直接改一份 Effect/Common 的材质,\n" +
                "所有用到它的特效(包括玩家真去装备的那个换色皮肤)都会跟着变, 等于污染游戏资产。\n" +
                "插值器对象同理(Instantiate 不深拷贝 SerializeReference 数组, 克隆里那份还指着原对象)。\n" +
                "复制失败会退回\"直接染原资产\"并在日志留痕(功能优先)。");
            RecolorConfig.TintSharedMaterials = Config.Bind(esec, "TintSharedMaterials", false,
                "直接写【共享】材质资产。默认关 —— 会外溢到所有使用者(污染游戏资产), " +
                "副本路已经覆盖同样的效果。只在调试时打开。");
            RecolorConfig.ForceSkinSet = Config.Bind(esec, "ForceSkinSet", false,
                "【资产重定向】把\"加载原色特效\"改成\"加载皮肤特效那一份\"。\n" +
                "依据(离线 diff effect/prefab/role/es/): 原色 es_* 共 105 个, esskin_12/13 各有 105 个\n" +
                "同名副本(完整覆盖), 且任何皮肤都没有原色之外的特效(不丢东西); 而皮肤那套我们本来就能染\n" +
                "(日志: Role.Es.EsSkin_10.es_attack1_02 染上了 4 处), 原色那套有一部分染不动\n" +
                "(颜色烘在共享材质上)。\n" +
                "=> 与其逐个解析原色特效的载体, 不如让它们变成那套能染的资产, 一次覆盖全部特效。\n" +
                "注意: 换的是皮肤那套资产(造型同名、贴图是那个皮肤自己的), 观感不接受就改 ForceSkinSetName。");
            RecolorConfig.ForceSkinSetName = Config.Bind(esec, "ForceSkinSetName", "esskin_12",
                "重定向到哪一套皮肤资产。可选: esskin_06 / esskin_10 / esskin_12 / esskin_13\n" +
                "(12 和 13 是 105/105 完整覆盖; 06 和 10 是 103/105)。");
            RecolorConfig.TintRenderers = Config.Bind(esec, "TintRenderers", false,
                "【材质路】对特效实例的渲染器做 `renderer.material`(Unity 自动新建实例 => 不污染共享资产)" +
                ", 再写按原值算出来的色。\n" +
                "2026-10-04 用户拍板打开(A 方案)。以前关着是因为踩过\"实心圆\" —— 但真正的凶手是" +
                "粒子 startColor 的渐变结构被 new MinMaxGradient() 抹掉(已修), 不是写材质这件事本身。\n" +
                "写入用的是和插值器同一个 Tint()(保留原量级与 alpha): 例如 _TintColor=(4.52,5.99,5.99) " +
                "会写成等亮度的红 (约 6,0,0)。\n" +
                "智能跳过: 同节点的 tinter **真的在驱动**这些属性时才让开 —— " +
                "剑气那条蓝拖尾的材质 particles_006_a 就是这么漏掉的(有 tinter 但没管它)。");
            RecolorConfig.TintFlame = Config.Bind(esec, "TintFlame", true,
                "★【火焰路 · 窄口径】只对 shader 名含 \"Flame\" 的渲染器写【实例材质】\n" +
                "(renderer.material = Unity 给每个渲染器建一份副本 ⇒ 不污染共享资产)。\n" +
                "依据(【特效解剖】从 esbullet 上读到的实据, 不是猜): 尾焰那颗材质\n" +
                "  turbulence_008_k2 shader=NOAH/Effect/Variant/Flame 用在 feng02 上,\n" +
                "  颜色 = _InnerFlameColor(0.823,1.200,2.770,0.427) / _OutterFlameColor(0.770,1.307,4.595,0.141),\n" +
                "  两个都不在白名单、那颗材质又没有插值器 ⇒ 粒子路/插值器路都够不着 ⇒ 尾焰一直是蓝的。\n" +
                "为什么不直接用 TintRenderers(宽口径): 那条会对每个特效实例渲染器都动手,\n" +
                "2026-10-04 就是这么把\"角色被染色/实心圆\"引进来的。这条只用只读的 sharedMaterial.shader.name\n" +
                "做判断, 非火焰渲染器连实例材质都不会建。");
            EffectSuppress.CfgEnabled = Config.Bind(esec, "SuppressOverlay", true,
                "★【特效裁剪】按名单关掉某些特效里的指定子物体。\n" +
                "起因: 冲刺时身上那层\"叠加层\"**只有原色特效有** —— 离线 diff 实证:\n" +
                "  es_dash_01 原色 = hub + guangzhu01 + Refrac + ★Other(MaterialTinterProxy)\n" +
                "  而 esskin_06/10 的副本里 Other **整个不存在**(不是调透明了), esskin_12/13 与原色一致。\n" +
                "  Other 上那 3 条插值器就是叠加层本体:\n" +
                "    (0,0.776,1.0,a=0)->(0,0.145,1.0,a=1) 蓝淡入 / 1.0->1.0 / (0,0,0,a=0)->(1.72,1.72,1.72,a=1) 白亮淡入\n" +
                "所以配换色皮肤时它反而是多余的。关掉即可。");
            EffectSuppress.CfgList = Config.Bind(esec, "SuppressList", "es_dash_01:Other",
                "规则: 逗号分隔的 `特效名片段:子物体名`。**默认空**。\n" +
                "★ 2026-10-04 深夜清空(原来是 `es_dash_01:Other`) —— 离线拆解发现:\n" +
                "   那道蓝光的**官方换色配方**(插值器 `_RemapColorFrom`/`_RemapColorTo`/`_RemapLerp`)\n" +
                "   **就挂在 `es_dash_01/Other` 上**(实测路径=es_dash_01/Other)。\n" +
                "   关掉它 = 把换色目标本身关掉 —— 线上表现正是日志里的\n" +
                "   `Tinter属性=<非颜色>` / `消费点 … 改写 0 条插值器`(怎么改都没反应)。\n" +
                "   想重新关掉, 把 `es_dash_01:Other` 填回来即可。\n" +
                "⚠ 不要写成 `:Other` 或只写名字 —— 全表 504 个特效里有 **85 个**带\n" +
                "MaterialTinterProxy@Other(buff_*/dead_*/portal_*/superarmor_*/flash_* …), 一刀切会把它们全废掉。\n" +
                "想连冲刺残影一起关(那是公共资产 silhouette_601, 所有皮肤都走它), 加一条 `silhouette_601:Other`。");
            EffectSuppress.CfgMode = Config.Bind(esec, "SuppressMode", "Hide",
                "关法: Hide = SetActive(false)(默认, 可逆) / Destroy = 直接销毁那个子物体(更彻底)。\n" +
                "为什么有两种: 那个 proxy 的插值器可能被 hub 在 Awake 里收进 InterpolatorSet,\n" +
                "那样即使物体被 SetActive(false) 动画也照样播。先试 Hide; 若画面里那层还在, 改成 Destroy。");
            RecolorConfig.TrailTint = Config.Bind(esec, "TrailTint", true,
                "★【残影路】让 ↓冲刺残影跟着我们的颜色走。\n" +
                "依据(探针实测): 残影渲染器用的是【角色材质】(SpritePalette / Custom/LitRole),\n" +
                "决定观感的是 _Emission/_EmissionX/Y/Z/_Skin1..4 = 皮肤调色板那套属性;\n" +
                "而 es_dodge_01/02 里那个 ActorTrailProxy._AddColor 运行时是 (0,0,0,0), 根本不起作用。\n" +
                "做法: 只对名单内的 trail, 给它的渲染器建【实例材质】(rt.material) 再染调色板属性 ——\n" +
                "绝不写 sharedMaterial(那是角色本体那一份, 会把角色一起染)。");
            RecolorConfig.TrailNameFilter = Config.Bind(esec, "TrailNameFilter", "es_dodge",
                "残影路的名字名单(逗号分隔, 子串匹配)。默认 es_dodge = 只处理闪避/暗影冲刺的残影。\n" +
                "留空 = 这条路什么都不做(安全默认)。");
            RecolorConfig.TintProps = Config.Bind(esec, "TintProperties",
                "_TintColor,_SubTexTintColor,_DecoTexTintColor,_HighlightColor,_BrightColor,_AmbientColor," +
                "_DissolveColor,_RemapColorFrom,_InnerFlameColor,_OutterFlameColor",
                "【着色属性名】要改写的着色器属性, 逗号分隔。\n" +
                "★ 这份名单有两半, 都是\"读出来的\", 不是猜的:\n" +
                "  ① 游戏自己的皮肤配置 `data/xlsx/avatarskinconf.ab`:\n" +
                "     _Emission/_EmissionX/Y/Z/A + _Skin1..4\n" +
                "     (proto 见 Gen/pbdef.js: SkinColor{prop,id} / SkinEffect{skinToEffect})\n" +
                "  ② 本管线自己的 `propmiss` 日志（哪个属性名没进白名单就会打出来, 实测抓到）:\n" +
                "     _SubTexTintColor(出现最多, 29 次) / _DecoTexTintColor / _DissolveColor\n" +
                "     / _HighlightColor / _BrightColor / _AmbientColor\n" +
                "  ③ 【特效解剖】工具从尾焰材质上读出来的(2026-10-04, shader=NOAH/Effect/Variant/Flame):\n" +
                "     _InnerFlameColor / _OutterFlameColor —— 这两个是尾焰的主色, 以前不在名单里,\n" +
                "     而那颗材质又没有插值器, 所以粒子路/插值器路都够不着它(见 TintFlameMaterials)。\n" +
                "  ④ ★★ 2026-10-04 深夜【离线拆解换色皮肤】得到的两条\"官方换色配方\"(权威, 别删):\n" +
                "     · `_RemapColorFrom` —— 游戏自带的重映射。原色 es_dash_01 里是\n" +
                "       `_RemapColorFrom`(0,0.145,1)蓝 + `_RemapColorTo`(1.72,1.72,1.72)中性白\n" +
                "       + 关键字 `_ENABLEREMAP_ON` ⇒ 观感就是\"白芯 + 蓝边\"。\n" +
                "       皮肤的做法是 **只把 `From` 换成自己的色相**(esskin_12 米黄/esskin_13 紫),\n" +
                "       `To` 留中性白 ⇒ \"白芯 + 皮肤色边\"。**这就是\"保留层次只换色相\"的官方实现。**\n" +
                "     · `_SubTexTintColor` —— 皮肤给特效加的着色插值器(esskin_06 粉红/esskin_10 绿),\n" +
                "       值本身带色相、HDR(>1 保亮度)。\n" +
                "⚠ 所以【只加 `From`, 不加 `To`】—— `To` 是\"白芯\", 改了就把白芯也染成目标色,\n" +
                "  等于抹平层次(和之前那个\"实心圆\"同类事故)。以前记的\"两个都改会抹平\"是对的,\n" +
                "  但结论下错了: 不是不能碰, 而是**只能碰 `From`**。\n" +
                "⚠ 也【不含】`_AddColor`: 离线+线上都证实它管的是**角色身上那层加法色(描边)**,\n" +
                "  不是电弧 —— 改了只会看到\"角色描边变色\"(2026-10-04 实测), 跟特效换色无关。\n" +
                "\n" +
                "特效的颜色烘在 prefab 的 MaterialColorInterpolator 里, 播放时逐帧写进这些材质属性 ——\n" +
                "所以只改粒子 startColor 会出现「一半变色」。\n" +
                "⚠⚠ 2026-10-04 深夜【删掉了 `_Emission,_EmissionX/Y/Z/A,_Skin1..4`】:\n" +
                "  那一组是**角色的皮肤调色板属性**(当年从 avatarskinconf 抄来的)。\n" +
                "  角色本体的视觉 prefab 名字里带 `es`, 会过名字过滤 ⇒ 这些**角色专属属性被一起染了**\n" +
                "  ⇒ 用户实测「影子和角色本体一起污染了」。**角色本体不着色是硬约束**, 不能再放回来。\n" +
                "  特效自己的着色只用得上上面那一组(_TintColor/_SubTexTintColor/_DecoTexTintColor/\n" +
                "  _HighlightColor/_BrightColor/_AmbientColor/_DissolveColor/_RemapColorFrom/火焰两色)。");
            RecolorConfig.SubTex = Config.Bind(esec, "TintSubTex", true,
                "★【副贴图着色路】照官方换色配方，直接写点名特效【实例材质】上的 `_SubTexTintColor`。\n" +
                "依据(离线拆解, 权威): 消耗 MP 的冲刺特效 `es_attackAir_02` 在原色里**没有任何颜色插值器**,\n" +
                "  皮肤换色的做法是**新增一条 `_SubTexTintColor` 插值器**(esskin_06=(1.227,0.735,0.826) 粉红 /\n" +
                "  esskin_10=(1.144,1.144,1.144))。插值器路只能改已存在的插值器 ⇒ 对这类特效天生够不着。\n" +
                "三道安全判据(缺一不可): ① 只对 `TintSubTexEffects` 点名的特效; ② 材质名含 SpritePalette 跳过;\n" +
                "  ③ shader 必须含 `NOAH/Effect/`(含 Role 的一律跳过)。写的是 rt.material(**实例**), 共享材质不动。");
            RecolorConfig.SubTexEffects = Config.Bind(esec, "TintSubTexEffects", "es_attackAir_01,es_attackAir_02,es_attackAir_02a,es_attackAir_03a,esbullet",
                "副贴图着色路的【点名特效】, 逗号分隔。名字比对是**精确**匹配(取最后一段、去掉 (Clone)),\n" +
                "不用子串 —— 否则 es_attackAir_02a 会被 es_attackAir_02 误伤。\n" +
                "现行依据: 动作 `dashSkill`(消耗 MP 的冲刺) 拉的是 `es_attackAir_02`;\n" +
                "  动作→特效对照表见 PROJECT_STATE §12.11。");
            RecolorConfig.SubTexProps = Config.Bind(esec, "TintSubTexProps", "_TintColor,_SubTexTintColor",
                "副贴图着色路要写的属性名, 逗号分隔。默认只有 `_SubTexTintColor` —— 与官方配方一致, 别乱加。");

            RecolorConfig.TintObjects = Config.Bind(esec, "TintObjects", "ring02,glow01,guangzhu01,guangzhu02",
                "★★【按子物体名下刀】要改写的**子物体**名(逗号分隔, 取名字最后一段**精确**比对)。\n" +
                "  为什么需要这条规矩: 管线原来只按【特效 hub 的名字】放行(TintSubTexEffects),\n" +
                "  于是清单之外的共享子预制**永远够不着** —— 而这些光效件恰恰都是共享子预制。\n" +
                "  ★★ 实测结论 (2026-10-05, 花了一天, 用户实机确认):\n" +
                "    ✅ `ring02`   = **那道电弧光环的本体**, 直写它材质就能变色\n" +
                "    ✅ `glow01` / `guangzhu01` / `guangzhu02` = 同路数(lod0/lod1 下的粒子渲染器,\n" +
                "       材质上都有 _TintColor), 一起上\n" +
                "    ❌ `Other01`  = **没效果**\n" +
                "  ★★★ 这条差异是本轮最值钱的一课:\n" +
                "    离线看官方皮肤, `es_stand_01` 里唯一被改的是\n" +
                "      Other01 / MaterialTinterProxy / _DecoTexTintColor  (原色强蓝 → 粉/绿/米白)\n" +
                "    于是理所当然地照着抄 —— **结果完全没反应**。\n" +
                "    原因: Proxy 是在 Play 时把值**推/拷**进目标材质的, 我们\"就地改写它的插值器\"推不动\n" +
                "    (而普通 MaterialTinter 的 InterpolatorSet 是**按引用**存数组, 所以能就地改)。\n" +
                "    ⇒ **\"官方改了 X\" ≠ \"在 X 上照抄就有效\"**。官方的机制和我们能用的机制可以不是同一条;\n" +
                "      真正可靠的是\"直写**渲染器实例材质**上的属性\"——那条路一次就通。\n" +
                "  ⚠ 生效方式: 这个值是**每次命中时现读**的 ⇒ F8 面板改完、再打一次冲刺就生效, 不用重启。\n" +
                "  ⚠ `es_stand_01` 在 `AssetExclude` 里, 所以本模块的调用被特意提到了排除名单**之前**\n" +
                "    (见 RecolorPipeline.HubReactivatePostfix 的注释) —— 否则这条路一次都不会被调用。");
            RecolorConfig.TintObjectHosts = Config.Bind(esec, "TintObjectHosts", "es_stand_01",
                "只在这些**宿主特效**(hub 名, 逗号分隔)的子树里去找 TintObjects。\n" +
                "  留空 = 任何特效的子树都找一遍（更全面, 但每次 hub 激活都要遍历子树, 更费）。\n" +
                "  ★ 用户实测路径: `PlayerV2(Clone)/ActorAnim/Motor/Renderer/Role.Es.es_stand_01/lod1/ring02`。");
            RecolorConfig.TintObjectLog = Config.Bind(esec, "TintObjectLog", true,
                "命中/未命中时打日志（排查看\"到底访问到没有\"）。");
            RecolorConfig.SkipScreenSpace = Config.Bind(esec, "SkipScreenSpace", true,
                "跳过【屏幕空间】特效。实测开着后整个画面会蒙一层颜色滤镜 ——\n" +
                "SP 技能里有一层全屏叠加特效, 它也带 _TintColor。\n" +
                "判据是 VFXEffectExtension.ScreenSpace。真想连全屏一起染就设 false。");
            RecolorConfig.TintExclude = Config.Bind(esec, "TintExclude", "silhouette_601,Common.dodge_01_black,dodge_01_black",
                "【染色排除名单】逗号分隔，按特效根节点名子串匹配。\n" +
                "用来兜底屏幕空间判据漏掉的情况，例如填 es_AH 就能整组跳过。");
            RecolorConfig.TintNameFilter = Config.Bind(esec, "TintNameFilter", "es,hit_,silhouette,darkbullet",
                "【插值器/普查/追染路的名字过滤】逗号分隔, 任一命中即可; 留空 = 沿用 EffectNameFilter。\n" +
                "⚠ 这一条以前是独立的一份 \"es_\", 和 EffectNameFilter 各走各的 ——\n" +
                "   于是 silhouette_601 过了战斗路那道门、却在这里被挡掉(日志说'命中=True'可画面没变)。\n" +
                "留空会连转场用的全屏白色遮罩(UI/Loading/Loading_0)一起染，表现为整个画面蒙一层色罩。");
            RecolorConfig.AssetFilter = Config.Bind(esec, "AssetFilter", "Role/Es,Prefab/Hit,silhouette",
                "【JIT 路线】只改路径/名字含这些串(逗号分隔, 任一命中)的 prefab 资产。\n" +
                "Role/Es = ES 角色特效(实测路径形如 Effect/Prefab/Role/Es/es_dash_01);\n" +
                "Prefab/Hit = 受击特效(Effect/Prefab/Hit/<皮肤>/hit_009) —— 后者原来一条都盖不到;\n" +
                "silhouette = 冲刺拖影 Effect/Prefab/Common/silhouette_601(公共 prefab)。");
            RecolorConfig.AssetExclude = Config.Bind(esec, "AssetExclude", "",
                "【排除名单】逗号分隔。跳过名字/路径含这些串的 prefab。\n" +
                "例如填 stand 可以跳过角色的待机光效(它会把角色变成一个发光的球)。");
            RecolorConfig.AllowList = Config.Bind(esec, "TintAllowList", "",
                "★【放行名单】逗号分隔, 命中就**越过排除名单和名字过滤**, 照染。\n" +
                "为什么需要: AssetExclude 里的 `Avatar` 是整族关键字(当初为了挡住\"角色被染色\"),\n" +
                "但冲刺那几道光影也在这族里 ——\n" +
                "  Role/Avatar/Avatar_DashShadow_AP_01/02  跟着骨骼跑的发光拖线,\n" +
                "      材质 _HighlightColor=(1.882,2.659,5.992) 等 = HDR 发光蓝(就是用户说的那道\"蓝光\")\n" +
                "  Role/Avatar/Avatar_TrailLoop_AP_01      lizi01 粒子, 蓝(0.090,0.296,1.0)/青绿(0.271,1.0,0.830)\n" +
                "有了这条就不必为了这一道光把整个 Avatar 解禁。留空 = 放行名单为空(回到原行为)。\n" +
                "⚠ 它**不越过**屏幕空间判据(防全屏色罩)和 TintExclude(你自己的显式 kill 开关)。");
            RecolorConfig.NameFilter = Config.Bind(esec, "EffectNameFilter", "es,hit_,silhouette,darkbullet",
                "【战斗特效路】名字含这些串(逗号分隔, 任一命中)才染。留空 = 全部角色都改。\n" +
                "es = ES 的特效 prefab (es_* / Role.Es.*);\n" +
                "hit_ = 受击特效 —— 它的实例名就叫 hit_009 / hit_018, 没有 es 前缀,\n" +
                "       原来整族被跳过(用户报的\"敌人受击/受击纹章没被染色\")。\n" +
                "silhouette = 冲刺拖影的正主。它是个【公共 prefab】Effect/Prefab/Common/silhouette_601,\n" +
                "       里面是 ActorTrailProxy(复制玩家形象的拖影) + 2 个 MaterialTinterProxy(蓝色叠加层)。\n" +
                "       名字里没有 es, 原来一直被跳过(用户报的\"冲刺拖影没被染色\")。\n" +
                "       ⚠ 它是公共的 ⇒ 敌方用同一个 prefab 的拖影也会一起变红。\n" +
                "⚠ 用 \"hit_\" 带下划线而不是 \"hit\": 后者会误伤任何含 hit 的名字(最典型是 white)。");
            RecolorConfig.RecolorCharacter = Config.Bind(esec, "RecolorCharacter", false,
                "【角色本体/Spine】染色。默认关 —— 实测会让角色变成刺眼的洋红, 且对攻击特效完全无效。");
            RecolorConfig.ActorId = Config.Bind(esec, "ActorId", 103401,
                "要改色的角色 id。103401 = ES。同时也会 dump 该角色各配色的原始颜色。");
            RecolorConfig.SkinFilter = Config.Bind(esec, "SkinFilter", "",
                "只改名字含此串的配色(留空 = 全部)。配色名形如 \"esskin_06\", 可先看日志里 dump 出的 skin 值。");
            RecolorConfig.Hex = Config.Bind(esec, "ColorHex", "FF00FF",
                "目标颜色。支持 FF00FF / #FF00FF / \"r,g,b\" / \"r,g,b,a\"。");
            RecolorConfig.LogOrigin = Config.Bind(esec, "LogOriginal", true,
                "dump 原始颜色值(前 60 条), 用来确认配色名与各通道的实际取值。");
            RecolorConfig.RefCounterProps = Config.Bind(esec, "RefCounterProps",
                "_TintColor,_SubTexTintColor,_DecoTexTintColor,_AddColor,_HighlightColor,_BrightColor",
                "★【叠色探针】要点名的材质属性(逗号分隔, 子串匹配)。\n" +
                "  为什么做成配置: 原来写死 `_AddColor` 那一组, 想查别的属性就得改代码重编。\n" +
                "  探针读的是引擎**自己的叠色记账表** `MaterialColorInterpolator.ReferenceCounterMap`\n" +
                "  (静态字典, key = (材质 instanceID, 属性名)), 每个条目有:\n" +
                "    referenceValue            原值(第一个插值器注册时记下)\n" +
                "    finalInterpolationEndValue 合成后的最终值\n" +
                "    counter                    有几个插值器正在叠(=0 就是历史残留)\n" +
                "  ★ 查「同一批东西颜色不一致」就看这些条目的 referenceValue 是否一致 ——\n" +
                "    条目按材质记账, 而材质是**对象池复用**的, 基准串了就会出现色差。");
            RecolorConfig.RefCounterMax = Config.Bind(esec, "RefCounterMax", 60,
                "叠色探针最多打几行明细（超了会明说）。");

            RefCounterProbe.CfgEnabled = Config.Bind(esec, "RefCounter", true,
                "★【叠色探针触发器】让上面那张引擎记账表能**随时抓**、也能**按动作自动抓**。\n" +
                "  原来探针只在 `MaterialTinterProxy` 命中时偶然触发 ⇒ 能抓到全靠运气\n" +
                "  （当前日志里就是 0 条）。而「九个纹章颜色一致吗」是个统计问题, 必须确定性抓取。");
            RefCounterProbe.CfgKey = Config.Bind(esec, "RefCounterKey", "F11",
                "手抓键（随时抓一次, **不受自动路径的限流影响**）。");
            RefCounterProbe.CfgActions = Config.Bind(esec, "RefCounterActions", "holdEX,x2,emblem,crest",
                "这些动作名(子串, 逗号分隔)一开始就自动连抓。默认对着纹章解放那一族。");
            RefCounterProbe.CfgSamples = Config.Bind(esec, "RefCounterSamples", 6,
                "每次进窗口连抓几次（counter/finalEnd 会随插值器启停变化, 单帧采样不能当结论）。");
            RefCounterProbe.CfgInterval = Config.Bind(esec, "RefCounterInterval", 0.15f,
                "连抓的间隔(秒)。");
            RefCounterProbe.CfgWindow = Config.Bind(esec, "RefCounterWindow", 2.0f,
                "抓满之后最多再等多少秒收工（防止动作名不消失时一直挂着）。");
            RecolorConfig.FollowLive = Config.Bind(esec, "FollowLive", true,
                "★ 配置一改就把【正在播】的特效立刻重染一遍(约 1 秒内追平), 而不是等下一次播放。\n" +
                "原理: 反汇编确认 MaterialTinter.Play 尾部就是 CreateInterpolatorSets,\n" +
                "而 InterpolatorSet 的构造函数把插值器数组【按引用】存下(没克隆) ——\n" +
                "所以就地改写源数组, 正在播的插值器一样看得到新值。\n" +
                "追染搭在 VFXEffectBase.Update 上: 平时只做一次整数比较, 只在配置刚变的那 ~30 帧里干活。\n" +
                "不用「活对象表」是因为那等于持有原生对象的托管引用, 特效是池化复用的, 会把它钉在池外。");

            // ---- 动作时序记录 ----
            var jsec = "动作记录";
            ActionJournal.CfgEnabled = Config.Bind(jsec, "Enabled", true,
                "【对照表用】按时间顺序记录每一次动作切换。\n" +
                "用法: 进训练场, 一招一招打、每招之间停 1~2 秒（停够就会打一条分隔线）, \n" +
                "打完把日志发我, 我按分隔线切段就能生成「招式 ↔ 内部动作名」对照表。\n" +
                "动作名字首次出现时会附带它的完整档案（时长/角色名/下一段/可被打断列表/特效数/判定段数）。");
            ActionJournal.CfgGapFrames = Config.Bind(jsec, "GapFrames", 60,
                "静默多少帧算「空档」, 打一条分隔线。本作逻辑帧 30fps, 60 帧 = 2 秒。\n" +
                "手动打得不快就调大。");
            ActionJournal.CfgProfile = Config.Bind(jsec, "Profile", true,
                "首次见到某个动作名时, 把它的完整档案一并打出来。");
            ActionJournal.CfgSkillProbe = Config.Bind(jsec, "SkillProbe", true,
                "【技能 id 探针】挂 PlayerSkillMgr.FindSkillAndChainByActionName ——\n" +
                "它是「动作名 → PlayerSkill」的映射，而 PlayerSkill 上带着 SkillId / ActorId / Input / SkillType。\n" +
                "用全潜能数据体时没有\"槽位\"概念、手工分不清 A/B/C 装的是谁，就靠这个打出来。\n" +
                "按 (动作名, 技能id) 去重，一次跑完得到完整对照表。");
            ActionJournal.CfgChain = Config.Bind(jsec, "ChainTrace", true,
                "【链路追踪】把「输入 → 技能 → 动作 → 弹幕」这条链的每一层都挂上,\n" +
                "用同一个帧号 + 缩进打出来, 日志会自然排成一条链。\n" +
                "  第0层 输入   TouchButtonStyleController.RecordSkill(InputCmd, TouchCriteria, PlayerSkill)\n" +
                "               （UI 类, 正常游玩不一定触发; 不触发就只有下面几层）\n" +
                "  第1层 技能   PlayerSkillMgr.GetSkillCastRequires / CheckSkillCanCast / SkillStartImplement\n" +
                "  第2层 动作   ActionMgr.ChangeAction\n" +
                "  第3层 弹幕   BulletMgr.createBulletImp（由 [弹幕探针] 打，按文件顺序自然接在后面）\n" +
                "InputCmd 枚举: Attack=1 Skill=3 Ultra=4 Summon=5 Dash=55 Jump=800");
            ActionJournal.CfgInputProbe = Config.Bind(jsec, "InputProbe", true,
                "【原始输入探针】挂 IInput.OnInputCommand（实现类是 GamePlay.PlayerInput）——\n" +
                "每一个输入指令都会过这里，【不管后面有没有成功出招】。\n" +
                "用来把「没按」和「按了但被吞」彻底分开：\n" +
                "之前某局钩子抓到了跳跃 17 次却一次都没抓到攻击键，无从判断是哪种情况。");
            ActionJournal.CfgAirChain = Config.Bind(jsec, "AirChainTrace", true,
                "【空中链记录】把一次空中连段里的动作按顺序编号打出来，进出各一条边界。\n" +
                "用来验证崔斯坦的「下落 → 踩踏 → 弹起 x3 → 落地」模型，\n" +
                "以及确认那两个从未出现过的槽位 fallupdd(4.2) / fallup2dd(4.5) 到底什么时候触发。");
            ActionJournal.CfgCap = Config.Bind(jsec, "LineCap", 4000,
                "最多记多少行, 防止长时间游玩把日志撑爆。");

            // ---- 动作变速（平A加速的技术验证）----
            var asec = "动作变速";
            EsActionSpeed.CfgEnabled = Config.Bind(asec, "Enabled", true,
                "把指定动作整体加速。第一个用例 = 平A 加快。\n" +
                "原理: 挂在每帧的 DeltaTimeAndScale 上, 把这个动作的步长乘大 ——\n" +
                "动作计时器/判定窗口/位移/动画一起快放, 是真正的\"动作变快\"。\n" +
                "只对本地玩家 + Actions 名单里的动作生效, 其它一帧都不碰。");
            EsActionSpeed.CfgGroups = new BepInEx.Configuration.ConfigEntry<string>[EsActionSpeed.GroupCount];
            EsActionSpeed.CfgGroups[0] = Config.Bind(asec, "Group1", "2.5 | attackD1,attack3,attackB,attackD2,attack2,attackAEX,attack4,attackD3,atkAirX,atkAir3,attack1,!attack4,rush",
                "【倍率 | 动作名单】一行一组。格式:  <倍率> | <动作1,动作2,...>\n" +
                "\n" +
                "  例:  2.5 | attack1,attack2,attackD2,attackAEX,attack3,attackB,attack4,attackD3,atkAirX\n" +
                "       1.8 | holdEX\n" +
                "       1   | (名单留空 = 该组关闭)\n" +
                "\n" +
                "匹配规则: 只认名字, 从组1往下第一组命中即生效。\n" +
                "\n" +
                "⚠ 绝不按【动作内容】匹配 —— 这是故意的隔离设计:\n" +
                "  我们克隆出来的段有独有名字 (aceN_xxx)。想只加速自己的段 → 写 aceN_xxx;\n" +
                "  想加速原版动作 → 写原名(如 attackAEX)。两者互不影响。\n" +
                "\n" +
                "倍率 1 = 不加速。\n" +
                "\n" +
                "《与窗口调节的关系》[连段模组] ChainWindowScale 设为 -1(自动) 时,\n");
            EsActionSpeed.CfgGroups[1] = Config.Bind(asec, "Group2", "3.5 | ace6_905",
                "【倍率 | 动作名单】一行一组。格式:  <倍率> | <动作1,动作2,...>\n" +
                "\n" +
                "  例:  2.5 | attack1,attack2,attackD2,attackAEX,attack3,attackB,attack4,attackD3,atkAirX\n" +
                "       1.8 | holdEX\n" +
                "       1   | (名单留空 = 该组关闭)\n" +
                "\n" +
                "匹配规则: 只认名字, 从组1往下第一组命中即生效。\n" +
                "\n" +
                "⚠ 绝不按【动作内容】匹配 —— 这是故意的隔离设计:\n" +
                "  我们克隆出来的段有独有名字 (aceN_xxx)。想只加速自己的段 → 写 aceN_xxx;\n" +
                "  想加速原版动作 → 写原名(如 attackAEX)。两者互不影响。\n" +
                "\n" +
                "倍率 1 = 不加速。\n" +
                "\n" +
                "《与窗口调节的关系》[连段模组] ChainWindowScale 设为 -1(自动) 时,\n");
            EsActionSpeed.CfgGroups[2] = Config.Bind(asec, "Group3", "1.5 | holdEX,!holdEX,dashAtk0,dashAirAtk0,dashAtkG,dashAirAtkG",
                "【倍率 | 动作名单】一行一组。格式:  <倍率> | <动作1,动作2,...>\n" +
                "\n" +
                "  例:  2.5 | attack1,attack2,attackD2,attackAEX,attack3,attackB,attack4,attackD3,atkAirX\n" +
                "       1.8 | holdEX\n" +
                "       1   | (名单留空 = 该组关闭)\n" +
                "\n" +
                "匹配规则: 只认名字, 从组1往下第一组命中即生效。\n" +
                "\n" +
                "⚠ 绝不按【动作内容】匹配 —— 这是故意的隔离设计:\n" +
                "  我们克隆出来的段有独有名字 (aceN_xxx)。想只加速自己的段 → 写 aceN_xxx;\n" +
                "  想加速原版动作 → 写原名(如 attackAEX)。两者互不影响。\n" +
                "\n" +
                "倍率 1 = 不加速。\n" +
                "\n" +
                "《与窗口调节的关系》[连段模组] ChainWindowScale 设为 -1(自动) 时,\n");
            EsActionSpeed.CfgGroups[3] = Config.Bind(asec, "Group4", "1 |",
                "【倍率 | 动作名单】一行一组。格式:  <倍率> | <动作1,动作2,...>\n" +
                "\n" +
                "  例:  2.5 | attack1,attack2,attackD2,attackAEX,attack3,attackB,attack4,attackD3,atkAirX\n" +
                "       1.8 | holdEX\n" +
                "       1   | (名单留空 = 该组关闭)\n" +
                "\n" +
                "匹配规则: 只认名字, 从组1往下第一组命中即生效。\n" +
                "\n" +
                "⚠ 绝不按【动作内容】匹配 —— 这是故意的隔离设计:\n" +
                "  我们克隆出来的段有独有名字 (aceN_xxx)。想只加速自己的段 → 写 aceN_xxx;\n" +
                "  想加速原版动作 → 写原名(如 attackAEX)。两者互不影响。\n" +
                "\n" +
                "倍率 1 = 不加速。\n" +
                "\n" +
                "《与窗口调节的关系》[连段模组] ChainWindowScale 设为 -1(自动) 时,\n");
            EsActionSpeed.CfgGroups[4] = Config.Bind(asec, "Group5", "1 | ",
                "【倍率 | 动作名单】一行一组。格式:  <倍率> | <动作1,动作2,...>\n" +
                "\n" +
                "  例:  2.5 | attack1,attack2,attackD2,attackAEX,attack3,attackB,attack4,attackD3,atkAirX\n" +
                "       1.8 | holdEX\n" +
                "       1   | (名单留空 = 该组关闭)\n" +
                "\n" +
                "匹配规则: 只认名字, 从组1往下第一组命中即生效。\n" +
                "\n" +
                "⚠ 绝不按【动作内容】匹配 —— 这是故意的隔离设计:\n" +
                "  我们克隆出来的段有独有名字 (aceN_xxx)。想只加速自己的段 → 写 aceN_xxx;\n" +
                "  想加速原版动作 → 写原名(如 attackAEX)。两者互不影响。\n" +
                "\n" +
                "倍率 1 = 不加速。\n" +
                "\n" +
                "《与窗口调节的关系》[连段模组] ChainWindowScale 设为 -1(自动) 时,\n");
            EsActionSpeed.CfgGroups[5] = Config.Bind(asec, "Group6", "1 | ",
                "【倍率 | 动作名单】一行一组。格式:  <倍率> | <动作1,动作2,...>\n" +
                "\n" +
                "  例:  2.5 | attack1,attack2,attackD2,attackAEX,attack3,attackB,attack4,attackD3,atkAirX\n" +
                "       1.8 | holdEX\n" +
                "       1   | (名单留空 = 该组关闭)\n" +
                "\n" +
                "匹配规则: 只认名字, 从组1往下第一组命中即生效。\n" +
                "\n" +
                "⚠ 绝不按【动作内容】匹配 —— 这是故意的隔离设计:\n" +
                "  我们克隆出来的段有独有名字 (aceN_xxx)。想只加速自己的段 → 写 aceN_xxx;\n" +
                "  想加速原版动作 → 写原名(如 attackAEX)。两者互不影响。\n" +
                "\n" +
                "倍率 1 = 不加速。\n" +
                "\n" +
                "《与窗口调节的关系》[连段模组] ChainWindowScale 设为 -1(自动) 时,\n");
            EsActionSpeed.CfgGroups[6] = Config.Bind(asec, "Group7", "1.5 | UltraDashEX,UltraDash,UltraDashAirEX,UltraDashAir,UltraDAend,UDdrop",
                "【倍率 | 动作名单】同上。Group7 = 贝德维尔(Ultra)。\n" +
                "只列 Ultra 专属动作 —— 千万不要把 dropend / stand 放进来,\n" +
                "那是所有落地共用的收尾动作(和 fallend 那次同一个坑)。");
            // ★ 有限取值的选项 → 用 AcceptableValueList 声明, 面板会画成一排选择按钮
            //   (自由文本框打错一个字母会静默回落到默认杠杆, 用户看不出来)。
            //
            // ⚠ 曾经把"启动崩溃"归咎于这里, 回滚过一次 —— **那是误判**。
            //   真凶是 `RuntimeLoader.GetAssetBundleFile` 那个钩子(见 Patcher.cs 的复盘)。
            //   判定依据: 去掉选择器、只留那个钩子的版本**照样崩**。
            //   但要诚实记一句: 含选择器的构建**全都死在启动**了, 所以它在实际游戏里
            //   **从没被真正跑过**。万一面板出问题, 这里是第一嫌疑, 去掉它就是一行。
            EsActionSpeed.CfgLever = Config.Bind(asec, "Lever", "Inject", new ConfigDescription(
                "往哪一层注入倍率:\n" +
                "  ★ Inject = 往 GameActionLogic.TimeScales 里塞一条「(0~总时长) 以 X 倍率播放」。\n" +
                "             这是游戏【原生】的单动作变速机制, 由游戏自己的代码作用在 dt 上 ——\n" +
                "             动作时钟/动画/位移/事件派发/判定窗口全部同一个 dt, 天然同步。\n" +
                "             不去拦任何函数参数, 所以绕开了\"Harmony 对值类型形参 ref 写回传不到原生\"\n" +
                "             这个死结(那正是 Dt 杠杆失效的根因)。**推荐, 默认。**\n" +
                "  Time  = 按指针把 ActionMgr.Time 往前推 (0x80) + 手工补派发被跳过的区间。\n" +
                "             实测能变速、纹章也修好了, 但动画跟不上, 后摇接不上 —— 属于 hack。\n" +
                "  Dt    = 改 ActionMgr.Update 的 dt        —— 实测无效(值类型形参写不回原生)\n" +
                "  Actor = 改 ActorBase.UpdateAll 的 dt     —— 未实测\n" +
                "  None  = 完全不碰动作时钟                  —— 对照用\n" +
                "⚠ 一次只开一个。从 Inject 切走时会自动撤销已注入的区间。",
                new AcceptableValueList<string>("Inject", "Time", "Dt", "Actor", "None")));
            EsActionSpeed.CfgScaleModel = Config.Bind(asec, "ScaleModel", false,
                "额外挂 ActorModel.UpdateModel —— 动画/特效层, 可以叠在任意杠杆上。\n" +
                "⚠ 和 ScaleAnim 是同一条链上的父子, 两个都开会叠乘, 一般只开 ScaleAnim。");
            EsActionSpeed.CfgRedispatch = Config.Bind(asec, "Redispatch", true,
                "★ 推 Time 之后, 把被跳过的那段时间区间用 ActionLogicRunner.ActionUpdate 补派发一次。\n" +
                "这是修复\"加速后丢纹章 / 接不上平A\"的关键。\n" +
                "原因: 动作里的事件(纹章=逻辑指令 CreateBullet、判定窗口、收招标志)不是靠读 Time 触发的,\n" +
                "而是按「这一帧从第几秒走到第几秒」这个【区间】派发的。游戏自己只派发了它那一段,\n" +
                "我们额外推的那一截没人派发 —— Time 变了, 事件没发生。\n" +
                "关掉可以对比(会重新出现丢纹章)。");
            EsActionSpeed.CfgScaleAnim = Config.Bind(asec, "ScaleAnim", false,
                "【动画钟】把 Spine 的动画推进 ActorVisualSpine.UpdateAnimation(delta) 也乘同一倍率。\n" +
                "为什么必须开: ES 是 Spine 2D 骨骼动画, 它的动画时间是**自己走的**,\n" +
                "和 ActionMgr.Time 是两口独立的钟。只推动作时钟 = 逻辑 3 倍速、动画原速,\n" +
                "表现就是「动画和判定对不上 / 动作结束了动画还没播完 / 打完卡在原地」。\n" +
                "本项默认开, 就是治这个。");
            EsActionSpeed.CfgMaxStep = Config.Bind(asec, "MaxStepPerPush", 0.016f,
                "单次推 Time 的上限(秒)。动作里有按动作时间触发的事件(纹章释放/判定窗口)，\n" +
                "一次跨太大步会把落在中间的事件整段跳过去。掉帧时 dt 会突然变大(实测见过 0.0358)，\n" +
                "正是\"有时候丢纹章\"的高发时刻。0 = 不限制。");
            EsActionSpeed.CfgSplitByHit = Config.Bind(asec, "SplitByHitData", true,
                "★ 用【原生数据】决定攻击段/后摇的分界, 而不是拍脑袋定比例。\n" +
                "依据: GameActionLogic.HitDataList —— 这就是这个动作的攻击判定时间段列表。\n" +
                "最后一段判定结束的那一刻 = 「攻击」结束、「后摇」开始。\n" +
                "关掉则退回 AccelRatio 的比例模式。");
            EsActionSpeed.CfgPostHitMargin = Config.Bind(asec, "PostHitMargin", 0.3f,
                "判定段结束后, 再多留多少秒给「收招动作」跟着一起加速。\n" +
                "判定结束不代表挥砍动作演完了(还有收势), 留一点余量看起来更自然。\n" +
                "0 = 严格按判定结束点切。");
            EsActionSpeed.CfgAccelRatio = Config.Bind(asec, "AccelRatio", 0.65f,
                "★ 只加速动作的【前百分之多少】，剩下的后摇留在原速。范围 0.1~1.0。\n" +
                "\n" +
                "为什么需要它: 取消窗口(能不能接下一段)在动作时间上是【全程敞开】的\n" +
                "(attack1~4 的 ChangeSkill 窗口 TimeCheck 都是 0.000~2.000),\n" +
                "所以没法靠加宽窗口来补偿 —— 全程加速 = 后摇和取消窗口一起被压缩,\n" +
                "搓招的实时节奏就变了。\n" +
                "\n" +
                "做法: 注入两条区间 —— [0 ~ 前N%] 用倍率, [前N% ~ 总时长] 用 x1。\n" +
                "这样挥砍/位移/判定照常快，而后摇的实时长度和原来一样，手感不变。\n" +
                "\n" +
                "1.0 = 全程加速(手感会变)；0.65 是折中起点，直接按手感调。");
            EsActionSpeed.CfgClamp = Config.Bind(asec, "ClampToDuration", true,
                "推 Time 时不许越过 TotalDuration。\n" +
                "原生代码是靠 `Time >= TotalDuration` 收尾的，推过头没好处，\n" +
                "却可能踩到按时间查表的逻辑(GetTimeScaleByTime / TimeScales 区间查找 /\n" +
                "各处\"按动作时间取第几段判定\"的索引)。默认开。");
            EsActionSpeed.CfgAnimProbe = Config.Bind(asec, "AnimProbe", true,
                "打印前若干次 UpdateAnimation / SetAnimationTime 的实参, 用来判定动画归谁驱动:\n" +
                "若 SetAnimationTime 一直在被调用 → 动画由动作时钟外部驱动, 不该再单独加速;\n" +
                "若只有 UpdateAnimation(delta) → 就是 ScaleAnim 这条路。");
            EsActionSpeed.CfgMeasure = Config.Bind(asec, "Measure", true,
                "【客观判据】每次离开名单里的动作时, 打印它的实测持续秒数 + 动作时钟最终读数。\n" +
                "开了倍率这两个数没变小 = 这个杠杆没用, 不用靠手感猜。");
            EsActionSpeed.CfgDiag = Config.Bind(asec, "Diag", true,
                "前 20 次应用时打印 dt 的前后对比, 用来确认倍率真的写进去了。");

            // ---- 动作结构转储 ----
            var stsec = "动作结构";
            ActionStructure.CfgEnabled = Config.Bind(stsec, "Enabled", true,
                "把动作的「怎么播完、怎么接下一招」原样打出来。\n" +
                "输出：动画时长/总时长、**动画播完即收招**、NextAction、\n" +
                "可打断它的动作表(Interrupt)、以及**接招窗口**(ActionSwitchs ——\n" +
                "「在这个动作的第几秒到第几秒之间，可以接哪一招」)。\n" +
                "这是判断「能不能取消接招、窗口有多宽」的唯一直接证据。");
            ActionStructure.CfgActions = Config.Bind(stsec, "Actions", "attack1,attack2,attack3,attack4",
                "要转储的动作名，逗号分隔。留空 = 所有动作(会很吵)。");
            ActionStructure.CfgOnce = Config.Bind(stsec, "OncePerAction", true,
                "每个动作只转储一次。");

            // ---- 普攻连段模组 ----
            var ccsec = "连段模组";
            SkillChainDump.CfgEnabled = Config.Bind(ccsec, "DumpEnabled", true,
                "【只读转储】把玩家每个技能组的连段链打出来 ——\n" +
                "链里有几段、每段是哪个动作、对应 skillactivate 表的哪个槽位。\n" +
                "连段的载体是 PlayerSkillMgr.m_SkChains[技能组].SkillList(PlayerSkill 的序列)，\n" +
                "改连段 = 重排这个列表。转储是为了先看清平A(Any)和下+攻击(Down)\n" +
                "到底是同一条链还是两条链 —— 猜错会把整个普攻改坏。");
            SkillChainDump.CfgDetail = Config.Bind(ccsec, "DumpDetail", true,
                "★ 转储时把每个技能的【衔接相关字段】一并打出来：\n" +
                "PreSkillOrder(前置) / ActdurStrict(最短保持=前摇门槛) / Preinputtime(预输入) /\n" +
                "Timeout / UseLongPress / Mps / ReqTriggerId / AllowGround 等。\n" +
                "用途: 布鲁诺三段是原生就能正常衔接的样例 —— 直接读它的值照抄，\n" +
                "比凭手感调 HoldSeconds 靠谱。");
            SkillChainDump.CfgOnActionChange = Config.Bind(ccsec, "DumpOnActionChange", false,
                "动作变化时也重新转储一次(链可能被重建)。比较吵，排查时才开。");

EsComboChain.CfgChains = new BepInEx.Configuration.ConfigEntry<string>[EsComboChain.ChainCount];
            for (int ci = 0; ci < EsComboChain.ChainCount; ci++)
                EsComboChain.CfgChains[ci] = Config.Bind(ccsec, "Chain" + (ci + 1), (ci == 0 ? "1 | attackD1[下]+, attack3[下], attackB[下], attackD2[下], attackB[下], attack2[下], holdEX[任意], attack1[任意]+, attack2[任意], attackD2[任意], attackAEX[任意], attack3[任意], attackB[任意], attack4[任意], attackD3[任意], atkAirX[任意]" : ci == 1 ? "4 | atkAir3[任意], atkAir3[任意], atkAir3[任意]" : ""),
                    "★ 链声明。一条链一行，Chain1..Chain6。\n" +
                "\n" +
                "  格式:  <目标链号> | <段1>, <段2>, ...\n" +
                "  段语法:  动作名[方向][+]\n" +
                "      [方向]  任意/上/下/前/后/无   缺省 = 继承源动作自身的方向\n" +
                "      +       尾缀，表示这一段允许作为【起手段】（前驱清空，能从站姿直接起手）\n" +
                "\n" +
                "  例:  Chain1 = 1 | attackD1[下]+, attack3[下], attackAEX[下], attackD2[下]\n" +
                "\n" +
                "【方向为什么写在链上】方向是「链位置」属性 —— 同一个 attack3 在平A链里要 任意、\n" +
                "在佩利诺尔链里要 下。实测：方向明确的段优先于 任意。\n" +
                "\n" +
                "【每条链只有一种节奏】宿主(继承后摇/预输入/硬地板的来源) = 该链【首段】的原生行。\n" +
                "所以首段要选一个有代表性的动作。\n" +
                "\n" +
                "【Segment 旧配置】没配 ChainN 时会回退到旧的 Sequence + Group，只改一条链。\n");
            EsComboChain.CfgTakeOver = Config.Bind(ccsec, "TakeOverNative", true,
                "★ Sequence 里出现的动作，移除链里【原生同动作段】。\n" +
                "\n" +
                "为什么必须移除：起手段靠 InputDir 竞争，同动作的原生段只要还在链里，\n" +
                "就会排在数组更前面把输入抢走 —— 实测「新佩利诺尔链」里我们自己的段一次都没被选中，\n" +
                "打的全是原生佩段。\n" +
                "\n" +
                "关掉 = 新旧并存（只用于对比观察）。");
            EsComboChain.CfgJumpReset = Config.Bind(ccsec, "JumpReset", true,
                "起跳时清空中链游标 —— 复刻原版「空中平A永远从空1开始」的规则。\n" +
                "\n" +
                "原版实测行为:\n" +
                "  空1 之后不跟空2; 空1 -> 落地 -> 起跳 -> 仍是空1; 空1 -> 空中跳跃 -> 仍是空1。\n" +
                "  (空2 只能靠上挑进) —— 这是一条**主动规则**, 不是「接不上」的副作用。\n" +
                "\n" +
                "我们把克隆段插进空中链之后, 数组推进有了落脚点, 于是落地起跳会接着往下打。\n" +
                "\n" +
                "★ 判据为什么是【起跳】而不是【落地】:\n" +
                "  · 落地 —— `drop*` / `fallmdown*` 这些名字**在空中也会出现** → 必然误伤,\n" +
                "    实测会把连段打到一半就清掉(空4 直接没了);\n" +
                "    想按「真的落地」判又找不到判据 —— ActorBase 的 0x10~0x200 里没有 Ground 标志。\n" +
                "  · 起跳 —— `jump*` 名字明确, 而且**连段打到一半不会再跳** → 零误伤。");
            EsComboChain.CfgInPlace = Config.Bind(ccsec, "InPlaceChains", "4",
                "哪些链用【原地替换】而不是【移除 + 追加】。逗号分隔组号, 例如:  4\n" +
                "\n" +
                "★ 两条链的推进机制根本不同, 所以需要两种模式:\n" +
                "  · 地面链 走 findNextSkillMatchPreOrderAndInputDir, 按【段号】匹配 -> 不看数组位置\n" +
                "  · 空中链 按【数组顺序】挪游标, 不走上面那条搜索\n" +
                "\n" +
                "实测证据(2026-10-04):\n" +
                "  选段探针全日志只服务 dash 链 —— 空中链一次都没调用过顺序号搜索\n" +
                "  输入推进 -> True  链段数=16  Cur@12(\"AttackUp2\")\n" +
                "  -> Cur@N 的 N 是【数组下标】: 11=atkAir12, 12=AttackUp2, 13=atkAir3, 顺次推进\n" +
                "\n" +
                "所以对空中链做「把原生段从中间挖掉 + 克隆追加到末尾」会让整条数组错位,\n" +
                "游标走到那一格踩空 -> 连段整条断掉(就是「空中只剩一段」的真因)。\n" +
                "\n" +
                "空 = 全部用旧模式(移除+追加), 与改动前行为一致。");
EsComboChain.CfgLinks = new BepInEx.Configuration.ConfigEntry<string>[EsComboChain.LinkCount];
            for (int li = 0; li < EsComboChain.LinkCount; li++)
                EsComboChain.CfgLinks[li] = Config.Bind(ccsec, "Link" + (li + 1), (li == 0 ? "903 <- 908,910,912" : li == 1 ? "904 <- 913" : li == 2 ? "913 <- 902" : li == 3 ? "914 <- 904" : li == 4 ? "1000 <- 12" : ""),
                    "★ 显式接线（穿插）。格式:  <目标Order> <- <前驱Order>,<前驱Order> | <目标> <- <前驱>\n" +
                "\n" +
                "  作用: 把列出的前驱【追加】到目标的 preSkillOrder —— 让同一个前驱能有多个后继。\n" +
                "  这就是【穿插】：例如让「佩线的平3」也能从「新平A线的A2」接过来。\n" +
                "\n" +
                "  例:  Link1 = 901 <- 909\n" +
                "       含义: Order 901(佩线的平3) 增加前驱 909(新平A线的A2)\n" +
                "             → 在 A2 时按 下+攻击，可以接进佩线的平3\n" +
                "\n" +
                "  ⚠ Order 号见启动日志的 [连段模组:接线] 转储（每段一行 order=NNN 名字）\n" +
                "  ⚠ 是「追加」，不覆盖 —— 不会破坏链内顺序接线\n" +
                "  ⚠ 别给带 + 的起手段加前驱（加了它就不再是起手段）");
EsComboChain.CfgDropSwitches = Config.Bind(ccsec, "ClearSwitches", "attackC:drop_down,drop*,jump*,empty | atkAirX:drop_down,drop*,jump*,empty | attackB:drop_down,drop*,jump*,empty",
                "★ 清空克隆体的「接招窗口」表。逗号分隔的【源动作名】。\n" +
                "\n" +
                "背景: 布3(attackC) 的接招窗口有 29 条, 开头就是\n" +
                "  [0] 接 drop_down  TimeCheck 0.500~2.000  模式=仅切Action\n" +
                "因为原生空中布鲁诺里 attackC 就是【收尾段】, 它本来就该允许接下落/跳跃。\n" +
                "我们把它搬进链的中段, 这些权限就成了累赘(按 下 会被下落系抢走)。\n" +
                "\n" +
                "⚠ 只清【克隆体】的表 —— 原版那份一个字节不动(已在 CloneAction 里给克隆体复制了自己的表)。\n" +
                "空 = 不清任何东西。");
            EsComboChain.CfgRewrite = Config.Bind(ccsec, "RewriteChain", true,
                "★ 只关【改写链路】，保留探针。\n" +
                "\n" +
                "CfgEnabled=false 会把模块连同探针一起停掉，于是「关掉连段改动、观察原生行为」这件事做不了。\n" +
                "设为 false = 不改链，但选段/汇点/段闸门等探针照常工作。");
            EsComboChain.CfgEnabled = Config.Bind(ccsec, "RewriteEnabled", false,
                "★ 重写普攻连段。把平A那几段替换成 Sequence 里的动作序列。\n" +
                "\n" +
                "原理: 平A = Attack 链里 InputDir=Any 的那四段(attack1~attack4)。\n" +
                "每一新段都克隆一段平A的 SkillActivate 只改 Action ——\n" +
                "所以【还是普攻连段】(普攻键起手/无消耗/走普攻判定与窗口)，\n" +
                "只是每段播的动作换了。改的是克隆体，共享表行一字节没动，\n" +
                "下+攻击的佩利诺尔、技能键的布鲁诺都照常能用。\n" +
                "\n" +
                "段数不限 —— 要几段插几段。默认关，先看转储确认结构再开。");
            EsComboChain.CfgSequence = Config.Bind(ccsec, "Sequence",
                "attack1,attack2,attackD2,attackAEX,attack3,attackB,attack4,attackD3,atkAirX",
                "连段序列，逗号分隔，**写动作名**。默认值就是你设计的那套:\n" +
                "  平1           = attack1     (槽1.9)\n" +
                "  平2           = attack2     (槽1.10)\n" +
                "  佩利诺尔2      = attackD1    (槽1.5)\n" +
                "  地面布鲁诺1    = attackAEX   (槽2.1)\n" +
                "  平3           = attack3     (槽1.11)\n" +
                "  地面布鲁诺2    = attackB     (槽2.3)\n" +
                "  平4           = attack4     (槽1.12)\n" +
                "  佩利诺尔3      = attackD2    (槽1.6)\n" +
                "  佩利诺尔4      = attackD3    (槽1.7)\n" +
                "\n" +
                "⚠ 这里【只放普攻连段】。纹章解放(holdEX)已经从这里移出去了 ——\n" +
                "  它是【技能】不是连段, 归 [纹章解放] 段自己管。\n" +
                "  混在一起时, 想调纹章就得先改连段序列, 而改连段又会连带改变纹章的触发时机,\n" +
                "  两件事互相绑架, 没法单独验证。要纹章就正常按纹章键, 走的是它自己的路径。");
            EsComboChain.CfgAdvance = Config.Bind(ccsec, "ManualAdvance", false,
                "【已废弃 · 请保持关闭】手动推进链的当前位置(Cur)。\n" +
                "\n" +
                "这东西是「找不到原生推进逻辑」时的权宜之计。后来反汇编\n" +
                "PlayerSkillChain.findAndStartSkill_Imp 才查清楚：推进靠的是\n" +
                "SkillActivateFixedPoint.PreSkillOrder(前置技能序号)，\n" +
                "修正 PreSkillOrder 之后原生推进就正常了 ——\n" +
                "再开着这个会【双重推进】，表现是每两段才播一段(奇数位全被跳过)。\n" +
                "留着只为排查，正常必须关。");
            EsComboChain.CfgHold = Config.Bind(ccsec, "HoldSeconds", -1f,
                "★ 每一段的【最短保持时间】(秒) = SkillActivateFixedPoint.ActdurStrict。\n" +
                "\n" +
                "反汇编 PlayerSkillChain.DoUpdateAndCheckInputSucc 得到的衔接判定:\n" +
                "    Cur.TimeEllaps >= SkillActivate.ActdurStrict  →  才接受下一段的输入\n" +
                "即【这一段必须播满这么久，才能被下一段打断】。\n" +
                "\n" +
                "我们克隆平A模板时把这个值一起抄了过来(很小)，所以长动作\n" +
                "(比如布鲁诺)会被后面的段提前掐掉。\n" +
                "\n" +
                "  -1 = 不修改(用平A模板的值)\n" +
                "  0  = 一按就断(和原来一样)\n" +
                "  >0 = 每段至少播这么久才允许接下一段\n" +
                "建议先试 0.6~1.0，找到\"长招不被打断、短招又跟得上\"的值。");
            EsComboChain.CfgCloneMode = Config.Bind(ccsec, "CloneMode", "All",
                "★ 连段里哪些动作要克隆成【独有的新动作】。三选一：\n" +
                "\n" +
                "  All         = 整条链全部克隆（默认；设计稿要求的终态）\n" +
                "  ExceptHost  = 只克隆非平A槽位的动作（attack1~attack4 保留原生名）\n" +
                "  Collision   = 只在动作名会跟别的链碰撞时才克隆（最早的行为）\n" +
                "\n" +
                "为什么要克隆：照抄原生行 = 连同它「在别的链里的上下文假设」一起抄进来。\n" +
                "已实测的四个卡点全是这个来源 —— 动作名被原链认领 / PrecheckActionCd=1 /\n" +
                "Timeout=0 让游标第一帧被清 / Input=Skill 不听攻击键。\n" +
                "全部克隆之后，变速/窗口/还原都只作用在我们自己身上，原版动作零影响。\n" +
                "\n" +
                "⚠ All 的代价：原生 attack1 这个名字不再出现在链上。\n" +
                "  若发现原生佩利诺尔等连段被牵连，先用 ExceptHost 做二分定位。\n");
            EsComboChain.CfgPreInputScale = Config.Bind(ccsec, "ChainWindowScale", 1.5f,
                "★ 【衔接窗口随速度放大】的倍率。\n" +
                "\n" +
                "把链路按 k 倍速播之后，所有以「动作时间」计的窗口在【现实时间】里都缩短了 k 倍 ——\n" +
                "玩家手上还是原来的节奏，游戏给的窗口却只有 1/k，搓招手感会很怪。\n" +
                "这一项把每段的 Timeout(后摇衔接窗口的上限) 乘回去，让现实时间里的窗口宽度恢复。\n" +
                "⚠ 不要去放大 Preinputtime —— 那是「一次按键的有效期」，放大会让按一下一路连过去。\n" +
                "\n" +
                "  -1 = 自动：该动作正在被 [动作变速] 加速时，就乘 [动作变速] Speed 的值（推荐）\n" +
                "   0 = 不改\n" +
                "  >0 = 手动指定倍率\n" +
                "\n" +
                "注意改的是我们自己那份技能行副本，共享的原生表行不动。");
            EsComboChain.CfgWindowOverrides = Config.Bind(ccsec, "WindowOverrides", "",
                "\u2605 \u3010\u9010\u52a8\u4f5c\u7a97\u53e3\u8986\u76d6\u3011\u2014\u2014 \u7cbe\u8c03\u624b\u611f\u7528\u3002\n" +
                "\n" +
                "\u4e09\u4e2a\u5b57\u6bb5\uff08\u8bed\u4e49\u5747\u7ecf\u53cd\u6c47\u7f16\u786e\u8ba4\uff09\uff0c\u987a\u5e8f\u56fa\u5b9a\uff1a\n" +
                "    \u540e\u6447\u7a97\u53e3(\u79d2)   = Timeout        \u4e0a\u9650\uff0c\u5230\u70b9 CurSkill.Reset\n" +
                "    \u524d\u7f6e\u8f93\u5165(\u79d2)   = Preinputtime  \u4ece\u6309\u4e0b\u90a3\u4e00\u523b\u8d77\u7b97\u591a\u4e45\u5185\u7b97\u6570\n" +
                "    \u786c\u5730\u677f(\u79d2)   = ActdurStrict  \u6ca1\u8d70\u5b8c\u524d\u8f93\u5165\u88ab\u4e22\n" +
                "\n" +
                "\u683c\u5f0f\uff1a\u52a8\u4f5c\u540d:\u540e\u6447,\u524d\u7f6e,\u786c\u5730\u677f    \u591a\u9879\u7528 | \u9694\u5f00\uff0c\u5199 - \u8868\u793a\u8be5\u9879\u4e0d\u8986\u76d6\n" +
                "\u4f8b\uff1a  attack2:0.55,0.18 | attackD2:0.7,-,0.25 | attackAEX:0.9,0.3\n" +
                "\n" +
                "\u52a8\u4f5c\u540d\u7528\u3010Sequence \u91cc\u5199\u7684\u540d\u5b57\u3011\uff08\u6e90\u52a8\u4f5c\u540d\uff09\uff0c\u4e0d\u662f\u6211\u4eec\u6539\u540d\u540e\u7684 aceN_xxx\u3002\n" +
                "\n" +
                "\u503c\u6309\u3010\u73b0\u5b9e\u79d2\u3011\u7ed9\uff0c\u5185\u90e8\u4f1a\u4e58\u8be5\u52a8\u4f5c\u7684\u901f\u5ea6\u500d\u7387 \u2014\u2014 \u6240\u4ee5\u4f60\u8c03\u597d\u7684\u624b\u611f\uff0c\u6539\u52a0\u901f\u500d\u7387\u4e5f\u4e0d\u4f1a\u5931\u6548\u3002\n" +
                "\u7a7a = \u5168\u90e8\u7528\u81ea\u52a8\u503c\uff08\u5c31\u662f\u4e0a\u9762 ChainWindowScale / TimeoutBonus / PreInputSeconds \u7b97\u51fa\u6765\u7684\uff09\u3002\n" +
                "\n" +
                "\u6bcf\u4e2a\u52a8\u4f5c\u6700\u7ec8\u751f\u6548\u7684\u4e09\u4e2a\u503c\u4f1a\u6253\u5728 [\u6574\u884c] \u65e5\u5fd7\u91cc\uff0c\u8c03\u5b8c\u5bf9\u4e00\u4e0b\u5c31\u77e5\u9053\u751f\u6ca1\u751f\u6548\u3002");
            EsComboChain.CfgTimeWin = Config.Bind(ccsec, "TimeoutBonus", 0.5f,
                "★ 【桥接段的游标窗口加成】(秒) —— 只作用于从别的链借来的动作段。\n" +
                "\n" +
                "背景（反汇编 PlayerSkillChain.DoUpdate / DoUpdateAndCheckInputSucc）:\n" +
                "  游标(CurSkill)有一个存活上限 Timeout，到了就进这条流程：\n" +
                "      CurSkill.Reset -> predictNextSkill -> SetStatus(4) -> SetCoolDown\n" +
                "  而链上有一个冷却 [+0x54]，冷却 > 0 时【所有输入被直接丢弃】。\n" +
                "  所以「能不能接下一段」的真实条件 = 冷却先走完、且游标还没超时。\n" +
                "\n" +
                "  实测: 平A 段 冷却 0.30 / Timeout 0.70  -> 窗口 0.40s  ✓\n" +
                "        借来的段(布鲁诺) 冷却与 Timeout 都 0.70 -> 窗口 0    ✗\n" +
                "\n" +
                "我们还没能定位「那个冷却值是谁按什么写的」（SetChainCD 全游戏零调用，\n" +
                "SetCoolDown 的值也不来自技能行字段）。所以先绕过：\n" +
                "把借来的段的 Timeout 拉长到这个加成以上，游标就不会撞上超时流程，\n" +
                "窗口自然出现。\n" +
                "\n" +
                "⚠ 它只影响「等下一次输入能等多久」，【不改变动作本身的时长/动画/判定】。\n" +
                "0 = 不修改（保持原样，也就是接不上）。\n" +
                "建议 0.4~0.8；如果发现连段「手感变粘」，就往下调。");
            EsComboChain.CfgPreInput = Config.Bind(ccsec, "PreInputSeconds", 0.3f,
                "★ 每一段的【按键有效期】(秒) = SkillActivateFixedPoint.Preinputtime。\n" +
                "\n" +
                "★★ 2026-10-02 反汇编更正（旧说明是错的）:\n" +
                "   判定式是  接受 <=> (当前时间 - 你按下的时刻) <= Preinputtime\n" +
                "   即它衡量的是【这次按键能活多久】，从【按下的那一刻】起算 ——\n" +
                "   不是旧文档写的「动作结束前 N 秒的窗口」。\n" +
                "   松手不清除时间戳，所以按一下就松手，这次按键同样在整个有效期内算数。\n" +
                "\n" +
                "由此直接得出两条: \n" +
                "  · 「按快接不上」的根因 = 按键在 ActdurStrict 地板开启前就过期了。\n" +
                "    能早到多早 = 地板开启时刻 - Preinputtime。\n" +
                "  · 所以想改善手感要【调大】它，而不是去动 ActdurStrict。\n" +
                "\n" +
                "⚠ 0 不等于「没有窗口」—— 填 0 会【回落到游戏内置的默认窗口】。\n" +
                "   -1 = 不修改，沿用该动作原生的值。");
            EsComboChain.CfgGroup = Config.Bind(ccsec, "Group", 1,
                "要重写的链编号(PlayerSkillGroup)。1 = Attack(普攻)。\n" +
                "2 = Skill1(技能键)。其它组见转储里的 [链N] 标题。");

            // ---- 贝德维尔翅膀纹章 ----
            var wsec = "贝德维尔";
            AhWing.CfgEnabled = Config.Bind(wsec, "Enabled", true,
                "贝德维尔(Ultra)翅膀纹章的诊断。开启后会把命中特效的【每一个渲染器】\n" +
                "逐条列出来(层级路径/类型/enabled/节点是否激活/缩放/材质颜色)。\n" +
                "线索: es_AH_01 资产本体 8 个渲染器全开, 但实例上只有 5 个启用 ——\n" +
                "恰好等于粒子数, 即 3 个 MeshRenderer 全被禁用。翅膀可能就是那 3 个 Mesh。");
            AhWing.CfgNameFilter = Config.Bind(wsec, "NameFilter", "es_AH",
                "只看名字含此串的特效实例。留空 = 全部。");
            AhWing.CfgForceMeshOn = Config.Bind(wsec, "ForceMeshOn", true,
                "【试验】把命中特效里被关掉的 Mesh/SkinnedMesh 渲染器强制打开若干帧,\n" +
                "看翅膀会不会出来 —— 这是验证\"就是被关掉了\"最快的办法。\n" +
                "只开 ForceFrames 帧, 不做全程覆盖(全程覆盖会跟游戏自己的显示逻辑打架)。");
            AhWing.CfgForceFrames = Config.Bind(wsec, "ForceFrames", 90,
                "强制打开持续多少帧。本作逻辑帧 30fps, 90 帧 = 3 秒。");
            AhWing.CfgVerbose = Config.Bind(wsec, "Verbose", true,
                "打印渲染器清单(第 0 帧与第 30 帧各一次 —— 很多特效是先建好、稍后才点亮部件,\n" +
                "只看第一帧会把\"还没点亮\"误判成\"坏了\")。");

            // ---- 纹章解放接管 ----
            // 纹章不是特效, 是弹幕 BulletObj(Id=10340101, LogicRes="esbullet")。
            // 它的 dir 是局部空间方向, 所以把 dir 绕原点平分旋转再生成 = 八个方向的环。
            var csec = "纹章解放";
            EsEmblemBurst.CfgAttach = Config.Bind(csec, "AttachBullets", "aD12:A1:0.2 | 1001:aup:0 | 1002:attackAir2+aup:0",
                "★ 技能修改·技术验证: 动作 X 同时发射动作 Y 的弹幕。\n" +
                "\n" +
                "格式:  <触发键> : <追加动作> [ : 延迟秒 [ : dir偏转角 [ : 真实方向角 ] ] ]   多项用 | 隔开\n" +
                "\n" +
                "★★ 第 4 段 和 第 5 段是**两回事**, 别混:\n" +
                "\n" +
                "   · 第4段 = 转传给弹幕的 `dir` 参数。\n" +
                "     本作是 3D 渲染的 2D 格斗, 弹幕的网格朝向从 dir 推出来;\n" +
                "     而我们追加的多半是**纹章(原地不动)**, 一转 dir 它就【转出平面变成躺着的】——\n" +
                "     表现成\"转 30/60/90 看不出变化(侧面对着你), 120/150 才翻向(转到背面)\"。\n" +
                "     想换个平面内的朝向, 用第5段。\n" +
                "\n" +
                "   · 第5段 = **在该弹幕的 Renderer 上额外偏转多少度**(屏幕平面内, 逆时针为正)。\n" +
                "     这是【基础角度 + 本值】, **不覆盖**它原有的旋转。\n" +
                "     走的是 UprightTick 那条路(只动渲染器, 不动弹幕本体), 补 10 帧防动作状态机写回。\n" +
                "     只影响外观朝向, 不影响位置/飞行/判定。\n" +
                "\n" +
                "   · 第5段【写没写】本身就是开关: 不写 = 完全不动它的渲染器(与改动前一致)。\n" +
                "\n" +
                "例:  1001:aH2EX:0:0:-90   —— 克隆②的蓄力1纹章, 视觉上朝下\n" +
                "     1001:aH2EX:0:0:90    —— 朝上(它本来多半就是这个朝向)\n" +
                "\n" +
                "触发键可以是两种:\n" +
                "  · 弹幕 action 名   —— 例 aD12  (佩1 纹章生成时触发)\n" +
                "  · 链上那一段的 Order(纯数字) —— 例 1001 (只有第 1001 段发射时才触发)\n" +
                "    需要后者是因为***同一条链上多个克隆段常常发射同一个弹幕***,\n" +
                "    按弹幕名挂会一起命中, 没法只挂给其中一段。\n" +
                "\n" +
                "例:  aD12:A1:0.2        佩1 的纹章生成时, 延迟 0.2s 再放一个 A1(布1 的剑气)\n" +
                "      1001:aH2EX:0:15   第 1001 段发射时, 附蓄力1纹章, 方向偏转 15 度\n" +
                "      1002:aH3EX:0:-15  第 1002 段发射时, 附蓄力2纹章, 方向偏转 -15 度\n" +
                "\n" +
                "★ 角度是【局部空间】偏转: 转多少度就是\"以角色朝向为基准\"转多少度,\n" +
                "  转身时方向跟着一起转。正数 = 逆时针。改完存盘即时生效(每次都现读配置)。\n" +
                "\n" +
                "弹幕 action 对照(实测): 佩1/2/3 -> aD12/aD22/aD32    布1 -> A1    布2 -> B1\n" +
                "日志里 [弹幕探针] 会打 startAction= 名字。\n" +
                "\n" +
                "★ 此路径严格复用纹章接管的「入队 -> 帧末补生成」模式:\n" +
                "  在 createBulletImp 里当场再调 CreateBulletByParams 会破坏 BulletList 的枚举(该文件原注释的教训)。");
EsEmblemBurst.CfgEnabled = Config.Bind(csec, "Enabled", true,
                "接管纹章解放：保留原本那个原地纹章，另外向 N 个方向放出同样寿命的纹章。\n" +
                "原理: 纹章是 BulletObj(弹幕)，生成入口 CreateBulletByParams 的 dir 参数是局部空间方向，\n" +
                "旋转它就能以角色朝向为基准向八方发射。");
            EsEmblemBurst.CfgBulletId = Config.Bind(csec, "BulletId", 10340101,
                "纹章弹幕的配置 id。10340101 = ES 的 esbullet，全表唯一一条 ES 专属弹幕。\n" +
                "(bulletconfig.ab / BulletConfig 表; 命名规律是 角色id*100+序号)");
            EsEmblemBurst.CfgActions = Config.Bind(csec, "CrestActions", "x1,x2,ax2",
                "哪些 bullet_action 算「纹章解放」的本体环，逗号分隔。\n" +
                "【实测确认】地面按纹章解放 -> startAction=\"x2\"\n" +
                "            空中按纹章解放 -> startAction=\"ax2\"   (a 前缀 = air)\n" +
                "⚠ 两个容易误加的:\n" +
                "  jump   —— 那是【空中跳跃产生纹章向下攻击】那条潜能的效果, 不是纹章解放。\n" +
                "            加进去的话你每跳一次就甩八个环。\n" +
                "  dashAir—— 空中冲刺用的动作。想要冲刺也甩环就自己加。\n" +
                "另外注意: 地面和空中走的是【两条不同的生成代码路径】,\n" +
                "所以钩子挂在私有汇点 createBulletImp 上才都抓得到。\n" +
                "esbullet 状态机的动作与特效绑定关系(从 actor/logicdata/esbullet.ab 读出来的):\n" +
                "    x1 / x2   -> Role/Es/es_holdFull_01      (蓄力满 = 原地大环)\n" +
                "    x3 / x32  -> Role/Es/es_holdRelease_01/02 (释放 = 飞出去的环)\n" +
                "    xup/xup2  -> Role/Es/es_holdRelease_01/02 (rush 变体)\n" +
                "生成点原文: CreateBulletIfTriggerChange → bullet_action:\"x1\",pos_x:0,pos_y:0,\n" +
                "            dir_x:1,dir_y:0,bullet_action_new:\"x2\",tag:\"es_x\"  ← 原地 (0,0)\n" +
                "升级时直接以 _new(x2) 生成, 所以两个都要列。");
            EsEmblemBurst.CfgRingCount = Config.Bind(csec, "RingCount", 8,
                "向外放出的环数量。8 = 每隔 45° 一个。0 = 关闭(只留原版行为)。");
            EsEmblemBurst.CfgStartAngle = Config.Bind(csec, "StartAngleDeg", 0f,
                "起始角度(度)。0 = 第一个环朝正前(局部 +x)。想让八个环避开正前方就填 22.5。");
            EsEmblemBurst.CfgSpread = Config.Bind(csec, "SpreadMode", "Move",
                "【怎么让八个环飞出去】实测两条路，第一条翻车第二条成：\n" +
                "  Move = 八个环用【和中间那个一模一样】的 action 与 dir 生成 —— 视觉一字不差、\n" +
                "         必然是立着的大环、生命周期天然等同；然后由我们每帧改写 ActorBase.Position\n" +
                "         把位移推出去。(推荐)\n" +
                "  Dir  = 旋转 dir，交给动作自己的位移逻辑飞。实测环会翻出平面变成\"躺着的\"，\n" +
                "         而且换 action 后表现会变成普通的发射纹章。留着做对照。");
            EsEmblemBurst.CfgPinCenter = Config.Bind(csec, "PinCenter", true,
                "把【游戏自己生成的那个原版环】钉死在出生点。\n" +
                "实测: 纹章解放升级之后, 原版那个环是会移动的(向面朝方向飞) ——\n" +
                "光放八个环不够, 中间还多一个跟着跑。开启后中间那个才是原地静止的。");
            EsEmblemBurst.CfgPinSeconds = Config.Bind(csec, "PinSeconds", 2.5f,
                "钉住持续多少秒。<=0 表示整个生命周期都钉。\n" +
                "⚠ 不建议填 <=0: 弹幕是【池化复用】的, 引用失效后继续写 Position 会挪动别的弹幕。\n" +
                "   2.5 秒够覆盖纹章的正常存活时间。");
            EsEmblemBurst.CfgPlayerOnly = Config.Bind(csec, "PlayerCasterOnly", false,
                "只接管【本地玩家】打出的纹章。【默认关】\n" +
                "关的理由: idx=10340101 本身就是 ES 专属弹幕, 这层过滤多余;\n" +
                "而且实测 ES 的招式大量由影子/分身 Actor 打出, Owner 不是 PlayerSelf ——\n" +
                "开着会把空中那批纹章整批滤掉。\n" +
                "真遇到\"敌方 ES 也甩环\"再打开。");
            var poolsec = "弹幕池";
            BulletPool.CfgEnabled = Config.Bind(poolsec, "Enabled", false,
                "【默认关 —— 实测这条路不成立】\n" +
                "它挂在 BulletMgr.PreCreateBullet 上，但本作全场一次都没调用过那个函数\n" +
                "(日志只有 已挂钩 那行，之后零条 预创建请求) —— 也就是说本作【不用预热池】: \n" +
                "弹幕是 GetFromPoolOrCreate 按需创建、死亡后回收复用。\n" +
                "所以扩大预创建数量没有任何作用。留在这里只作为记录与探针:\n" +
                "万一某个场景真的调了它, 打开就能看到真实数量。\n" +

                "弹幕池扩容。挂在 BulletMgr.PreCreateBullet 上, 把预创建数量按倍率放大。\n" +
                "\n" +
                "依据: 池是【预热】出来的 —— PreCreateBullet(normal, spine, model3d) 按视觉类型\n" +
                "(普通/Spine/3D) 预先建好一批, GetFromPoolOrCreate 不够时才即时 new。\n" +
                "所以扩容不用碰任何数据, 改传进去的数量就行。\n" +
                "\n" +
                "注意它【不根治】什么: 我们的 mover 持有的是裸引用, 弹幕一旦被回收复用,\n" +
                "旧引用就指到别人的弹幕上 —— 池子大只是让[回收得太早]发生得更少。\n" +
                "根治靠按时撒手(PinSeconds / MoveSeconds / StartFlySeconds)或每帧换主人检测。\n" +
                "扩池的真实收益: 连发/高速连段时不再频繁走即时分配, 减少卡顿。");
            BulletPool.CfgScale = Config.Bind(poolsec, "Scale", 2f,
                "预创建数量的放大倍率。1 = 不放大(只看日志里的原始数量)。\n" +
                "本作默认池子是按普通节奏的 ES 配的; 我们加了 1+8 环 / 多方向剑气之后明显偏小。");
            BulletPool.CfgLog = Config.Bind(poolsec, "Log", true,
                "打印每次预创建请求的原始数量与放大后的数量(前 20 次)。\n" +
                "必须打: 不打的话[到底扩了多少]永远说不清, 而[配了没生效]和[本来就是这么大]\n" +
                "在日志里长得一模一样。");

            EsEmblemBurst.CfgStartFlySpeed = Config.Bind(csec, "StartFlySpeed", 18f,
                "★ 自推飞行的默认速度(世界单位/秒)。规则里没写第 5 段时用它。\n" +
                "为什么必须自推: 弹幕自己的位移逻辑【不看 dir】(实测 30/60/90 无变化、120/150 才翻向 ——\n" +
                "它只拿 dir 当[朝前/朝后]的粗判据), 所以斜向 45/135 交给它必然退化。\n" +
                "本作逻辑帧 30fps。世界单位 ≈ 角色身高量级, 先给 18 试手感。");

            EsEmblemBurst.CfgStartFlySeconds = Config.Bind(csec, "StartFlySeconds", 1.2f,
                "自推飞行管多久(秒)。到期就撒手、交还给弹幕自己的逻辑。\n" +
                "⚠ 必须是有限值: 弹幕是【池化复用】的, 引用失效后继续写 Position 会挪动别的弹幕。");

            EsEmblemBurst.CfgStartBullets = Config.Bind(csec, "StartBullets",
                "fallend2:B1:0.00:180 | fallend2:B1:0.00:0 | fallmdownendEX2:B1:0.00:180 | fallmdownendEX2:B1:0.00:0 | fallmdownendEX2:B1:0.00:135 | fallmdownendEX2:B1:0.00:45 | fallmdownendEX:B1:0.00:180 | fallmdownendEX:B1:0.00:0 | fallmdownendEX:B1:0.00:135 | fallmdownendEX:B1:0.00:45 | dashAAendEX:C1:0:0:朝右 | dashAAendEX:C1:0:180:朝左",
                "★ 动作【起手】时按延迟放出弹幕。\n" +
                "   触发动作:弹幕[:延迟秒[:屏幕角度[:飞行速度]]]   多条用 | 分隔\n" +
                "   屏幕角度: 0=右 90=上 180=左 -90=下(崔斯坦没有方向性, 所以按屏幕算)\n" +
                "   写了【飞行速度】= 我们自己推着它飞(精确方向); 不写 = 只转 dir 交给弹幕自己 ——\n" +
                "   那样【只有 0/180 可靠】, 斜向会退化。\n" +
                "   触发动作:弹幕[:延迟秒[:局部偏转角[:真实方向角]]]   多条用 | 分隔\n" +
                "\n" +
                "和 AttachBullets 的唯一区别是【触发点】：\n" +
                "  AttachBullets —— 某个【弹幕生成】的那一刻顺带再放一个（挂在 BulletMgr.createBulletImp 上）\n" +
                "  StartBullets  —— 某个【动作起手】的那一刻放（挂在 ActionMgr.ChangeAction 上）\n" +
                "\n" +
                "为什么要另开一个: 崔斯坦的踩踏/落地那批动作【自己不生成弹幕】\n" +
                "  (fallupd / fallup22 / fallup2d / fallend / fallend2 都不在\"含 CreateBullet 的 28 个动作\"名单里;\n" +
                "   fallmdownendEX/EX2 那条是条件生成 trigger:11481, 实测常常不发)\n" +
                "  ⇒ 那条链上没有任何\"弹幕生成事件\"可以搭车, 用 AttachBullets 写它们会一条都不触发(而且是静默的)。\n" +
                "\n" +
                "⚠ 角度只认第 3 段的【局部偏转角】: 实测弹幕自己的位移逻辑**根本不看 dir**\n" +
                "  (30/60/90 无变化、120/150 才翻向 —— 它只拿 dir 当\"朝前/朝后\"的粗判据)。\n" +
                "  所以 0(前) / 180(后) 是可靠的, 【斜向 45/135 不可靠】——\n" +
                "  斜向要么接受退化, 要么等\"自推飞行\"那条路(目前 MoveDeg 对追加弹幕只做视觉旋转, 不产生位移)。\n" +
                "\n" +
                "位置/方向取【当前角色】的: pos = ActorBase.PositionFp, dir 用局部前方 (1,0)。\n" +
                "只对本地玩家生效(影分身也跑同一套动作, 不加这层会一次动作放好几批)。\n" +
                "需要 Enabled=true, 并且要先有过一次真弹幕(F9 那条限制同样适用)。");

            EsEmblemBurst.CfgSkipOurSegments = Config.Bind(csec, "SkipOurSegments", true,
                "【只接管原版纹章】从我们克隆出来的连段段打出的纹章，一律跳过（不钉、不放环）。\n" +
                "\n" +
                "为什么需要: 下段线末尾那个纹章解放是【克隆段】(技能行 Order=906)，\n" +
                "它必须保持原样「会移动」；而原版 holdEX 我们要的是「钉住 + 八方向静止环」。\n" +
                "两者打的是同一个弹幕 id(10340101)，光看弹幕分不出来。\n" +
                "\n" +
                "判据: 读触发它的那一行技能的 Order —— **Order >= 900 = 我们造的段**\n" +
                "(全项目统一的哨兵值, 原生 order 都是个位数/几十, 不可能碰撞)。\n" +
                "不用动作名去区分, 是因为克隆段上报的 CurrentActionName 到底是合成名\n" +
                "(ace7_906) 还是源名(holdEX) 还没验证过 —— 用 Order 就绕开了这个问题。\n" +
                "\n" +
                "要连带把克隆段的纹章也接管 → 设 false。");

            EsEmblemBurst.CfgCheckDead = Config.Bind(csec, "CheckIsDead", false,
                "每帧检查弹幕是否已死, 死了就不再动它。【默认关, 建议保持关】\n" +
                "开着的理由是: 弹幕池化复用, 引用失效后继续写 Position 会挪动别的弹幕。\n" +
                "但实测打开后八个环全变成朝面朝方向飞 —— 池里拿出来的弹幕刚创建时\n" +
                "IsDead 还带着上一世的残留值, 于是所有 mover 在第一个 tick 就被误杀。\n" +
                "防误伤改用限制时间: 把 PinSeconds / MoveSeconds 调小即可。");
            EsEmblemBurst.CfgHoldAfterMove = Config.Bind(csec, "HoldAfterMove", true,
                "推到极限之后【就地钉住不动】。默认开。\n" +
                "关掉的话会撒手交还给动作 —— 而那个动作(x2)本身就会朝面朝方向移动,\n" +
                "于是环到了极限还会继续往前飘(这就是之前那个 bug 的来源)。");
            EsEmblemBurst.CfgHoldSeconds = Config.Bind(csec, "HoldSeconds", 3f,
                "极限位置钉住多少秒。<=0 = 整个生命周期都钉。\n" +
                "⚠ 别填 <=0: 弹幕是池化复用的, 时间越长撞上复用的概率越高。");
            EsEmblemBurst.CfgMoveSeconds = Config.Bind(csec, "MoveSeconds", 1.2f,
                "Move 模式下推多少秒就撒手。之后环恢复由动作自己管，\n" +
                "于是\"飞出去\"和\"原地的绽放/追加攻击\"两段都有。");
            EsEmblemBurst.CfgSpeed = Config.Bind(csec, "MoveSpeed", 6f,
                "Move 模式的推出速度(世界单位/秒)。本作逻辑帧 30fps。");

            EsEmblemBurst.CfgRingAction = Config.Bind(csec, "RingAction", "",
                "飞出去的环用哪个 bullet_action。\n" +
                "【默认留空】= 跟中间那个原生纹章同一个动作 —— 配合 SpreadMode=Move 时这是最对的:\n" +
                "视觉一致、生命周期一致，位移由我们自己推，不需要换动作。\n" +
                "只有 SpreadMode=Dir 时才需要填 —— 那时可以试 x3 / x32 (绑 es_holdRelease_01/02，\n" +
                "\"蓄力释放\"那条，会自己飞) 或 xup2 (tag:es_rush，rush 变体)。");
            EsEmblemBurst.CfgUpright = Config.Bind(csec, "UprightMode", "Visual",
                "【扶正】本作是 3D 渲染的 2D 格斗，弹幕视觉的朝向也是从 dir 推出来的。\n" +
                "游戏自己那 26 条 CreateBullet 指令清一色 dir_x:1,dir_y:0 —— 这条环从没走过斜方向。\n" +
                "所以一旋转 dir，环就翻出平面变成\"躺着的\"。\n" +
                "扳的是 ActorModel.VisualTransform（视觉根节点，游戏自己写朝向的那一层），\n" +
                "不碰弹幕本体的逻辑位置与飞行方向。\n" +
                "  Visual  = 把每个环的视觉朝向整体照抄中间那个原生纹章 (推荐)\n" +
                "  VisualZ = 只借中心的 X/Y 倾角，保留环自己的 Z 自旋 (环仍朝各自方向转，但不翻出平面)\n" +
                "  Off     = 不扶正，环会躺着飞");
            EsEmblemBurst.CfgUprightFrames = Config.Bind(csec, "UprightFrames", 6,
                "扶正持续多少帧。动作状态机可能在随后几帧重新写朝向，补一次会被覆盖。\n" +
                "本作逻辑帧 30fps，6 帧 = 0.2 秒。还躺就调大。");
            EsEmblemBurst.CfgTraceDir = Config.Bind(csec, "TraceBulletDir", true,
                "★ 弹幕方向探针: 挂 `BulletObj.ChangeDir2` —— 弹幕朝向的【唯一写入点】。\n" +
                "\n" +
                "反汇编结论(2026-10-04):\n" +
                "  · ActorDir 用【二元组】决定朝向: m_Dir(0x10) + m_DirUp(0x20);\n" +
                "    ChangeDir2 一次写这两个字段(normalize(dir) 和它的垂直方向)。\n" +
                "  · 它的调用者共 29 个, 其中 **BulletObj.UpdateLogic 每帧都在调** ——\n" +
                "    所以\"事后掰 Renderer\"会被下一帧覆盖(实测), 而拦这里是正解。\n" +
                "  · 游戏的 ActorGetDirectionType 标签: StartDir=\"(bullet)按发射速度方向\",\n" +
                "    MovementDir=\"(bullet)按速度方向\" —— 弹幕是跟着速度转的。\n" +
                "\n" +
                "日志里连续多行相同 dir2 = 它每帧都在重算(关键判据)。上限 60 行。");
            EsEmblemBurst.CfgTraceDirActions = Config.Bind(csec, "TraceBulletDirActions", "aH2EX,aH3EX,x2,aD12,B1,A1",
                "方向探针只看这些【弹幕自己的动作名】(不是我们克隆段的 aceN_xxx)。\n" +
                "逗号分隔。留空 = 不打印。");
            EsEmblemBurst.CfgDebug = Config.Bind(csec, "Debug", true,
                "打印每次捕获到的纹章生成与放环结果。");

            BulletProbe.CfgEnabled = Config.Bind(sec, "TraceBulletSpawns", true,
                "临时诊断: 打印每个玩家弹幕的 idx / startAction / Skin。\n" +
                "纹章解放的环是 BulletObj(带伤害+破霸体判定)，不是特效 —— 这个探针用来找出它的 idx，\n" +
                "以及确认它到底有没有被生成(用来洗清「是改色把它弄没了」的嫌疑)。");
            ActionProbe.CfgEnabled = Config.Bind(sec, "TraceActionNames", true,
                "临时诊断: 按名字去重打印你用过的每一个动作名。\n" +
                "用来消掉【分不清特效没生成还是技能没按】这个混淆点。查完可以关掉。");
            SkillCostTweak.CfgNoMpCost = Config.Bind(sec, "SkillNoMpCost", true,
                "技能不消耗 MP。\n" +
                "做法: 把 PlayerSkill.MpLimitValid 钉成 false —— 它是施放门槛\n" +
                "(GetSkillCastRequires 的 mpValid / IsSkillReady 都读它)。\n" +
                "已知边界: MP 的数值扣除走数据驱动触发器, 不经过这个属性, " +
                "可能出现\"随便放但 MP 条还是会掉\"。");
            JumpDashCrossReset.CfgEnabled = Config.Bind(sec, "JumpDashCrossReset", true,
                "跳跃 ⟷ 冲刺 互相重置段数限制：跳跃之后可以重新冲刺，冲刺之后可以重新跳跃。\n" +
                "原理: 在 ActionMgr.CheckCanChangeToAction 上放行异族切换。");
            JumpDashCrossReset.CfgJumpKw = Config.Bind(sec, "JumpActionKeyword", "jump",
                "跳跃族动作名关键字(不分大小写)。实测: jump / jump2 / jump3");
            JumpDashCrossReset.CfgDashExclude = Config.Bind(sec, "DashActionExcludeKeywords", "dashAtk0,dashAirAtk0,dashAtkG,dashAirAtkG",
                "冲刺族【排除名单】(逗号分隔)。名字含这些串的不算冲刺, 不扣冲刺预算、也不回满跳跃预算。\n" +
                "\n" +
                "默认排 dashAtk0 / dashAirAtk0: 它们是【高文】的召唤技(按键 Summon/上), 不是冲刺移动 \n" +
                "—— 但名字里有 dash, 会被关键字当成冲刺扣预算, 空中连冲之后高文就按不出来。\n" +
                "(同 UltraDash 那次: 关键字子串误伤必须配排除名单)");

            JumpDashCrossReset.CfgDashKw = Config.Bind(sec, "DashActionKeyword", "dash",
                "冲刺族动作名关键字(不分大小写)。实测: dash / dashAir / dashAir2 / dashAir3 / dashend");
            JumpDashCrossReset.CfgDashBudget = Config.Bind(sec, "DashBudget", 3,
                "一次\"空中周期\"内的冲刺次数上限(对应三段冲刺)。跳跃成功后会回满。");
            JumpDashCrossReset.CfgJumpBudget = Config.Bind(sec, "JumpBudget", 3,
                "一次\"空中周期\"内的跳跃次数上限(对应三段跳)。冲刺成功后会回满。");
            JumpDashCrossReset.CfgGroundKw = Config.Bind(sec, "GroundActionKeywords",
                "stand,run,squat,runBrake,wakeup,born,push_back",
                "落地/地面动作名关键字(逗号分隔)。命中即结束「空中机动周期」并把预算回满。\n" +
                "空中攻击不在其中 —— 它不该打断周期。");
            JumpDashCrossReset.CfgUnlimited = Config.Bind(sec, "JumpDashCrossResetUnlimited", false,
                "true = 完全取消跳跃/冲刺的次数限制(预算不生效)。一般不用开。");
            JumpDashCrossReset.CfgDebug = Config.Bind(sec, "TraceGroundDash", true,
                "快速冲刺专项诊断: 只在【玩家正处地面冲刺】时打印「起手搜索返回了谁」和「StartSkill 请求谁→成功与否」。\n" +
                "用来回答\"沉默清掉之后游戏走到哪一步\"(找不到下一段 / 找到了但起招被拒)。查完可以关。");
            JumpDashCrossReset.CfgGroundDashCancel = Config.Bind(sec, "GroundDashCancelDash", true,
                "地面冲刺可以打断地面冲刺(快速冲刺)。\n" +
                "\n" +
                "反汇编(RVA 0x1BB10F0)确认拦路的是【链上的沉默】, 不是接招表:\n" +
                "    DoUpdateAndCheckInputSucc 开头: MuteRemain(+0x54) > 0 -> return false\n" +
                "    而 SetMute(0x1BB28C0) 正是 [+0x54] = muteTime\n" +
                "实测: 地面冲刺一起手本链就被沉默 0.600s, 冲刺动作本身只有 0.25s\n" +
                "—— 结束后还被自己锁 0.35s, 再按冲刺当然没反应。\n" +
                "\n" +
                "做法: 地面冲刺动作进行中清掉本链沉默 + 该转换放行。\n" +
                "不碰 ActdurStrict(原生硬地板), 所以不会变成瞬移连冲; 空中冲刺(dashAir)\n" +
                "也不受影响 —— 它走三段冲预算那套。\n" +
                "另: 地面冲刺不再消耗空中冲刺预算(预算本来就是给空中机动用的)。");
            ActionLimitTrace.CfgEnabled = Config.Bind(sec, "TraceActionLimit", false,
                "临时诊断: 记录被「段数限制」拒绝的动作切换。\n" +
                "空中连冲/连跳被卡住时, 日志会打出被拒的动作名 —— 那就是计数所在。只观察。");
            DashInvincible.CfgTraceLevel = Config.Bind(sec, "TraceInvincipalLevel", true,
                "临时诊断: 记录每次取无敌等级时的原始值 + 当时动作名。\n" +
                "用来判断「闪避档」是不是某个特定等级值, 以及冲刺动作的等级曲线。只观察, 不改行为。");
            SlowMotionTrace.CfgEnabled = Config.Bind(sec, "TraceSlowMotion", true,
                "临时诊断: 打印每次「时缓」调用的参数与调用栈。\n" +
                "用来定位极限闪避的时缓是哪个函数放出来的。找到之后可以关掉。");
            PerfectDodge.CfgTrigger = Config.Bind(sec, "PerfectDodgeOnDash", true,
                "【C 方案】冲刺期间被击中时, 自动触发完美闪避效果。\n" +
                "原理: 原生完美闪避的载体是动作里的 ActionBehitNotifyData「第X~Y帧内被击中→调脚本」,\n" +
                "我们的全程无敌让命中在到达它之前就被吸收, 所以脚本跑不到。\n" +
                "这里改在命中结算那一刻自己补上: 免伤 + 播 ShowSlow(时缓)。");
            PerfectDodge.CfgAbsorb = Config.Bind(sec, "PerfectDodgeAbsorb", true,
                "触发完美闪避时, 是否连这一击的伤害一起免掉。\n" +
                "设 false 可以单独观察'只放时缓、仍然掉血'的情况。");
            PerfectDodge.CfgCooldown = Config.Bind(sec, "PerfectDodgeCooldown", 0.25,
                "两次触发时缓之间的最小间隔(秒)。防止 GetInvincipalLevel 在没打中时也被调导致刷屏。");
            PerfectDodge.CfgEnabled = Config.Bind(sec, "PerfectDodgeWholeDash", true,
                "冲刺全程都判定为「极限闪避」(完美闪避)。\n" +
                "原理: 把 CheckDodge 在冲刺期间抬成 true —— 那批「若闪避成功」的潜能就会全程触发。");
            DashInvincible.CfgPlayerOnly = Config.Bind(sec, "PlayerOnly", true,
                "只对本地玩家生效。强烈建议保持 true —— 设为 false 会让敌人也跟着无敌, " +
                "而本作在命中结算时只要一方无敌就整个跳过, 结果是什么都打不中");
        }
        catch (Exception e) { Log.LogWarning($"读取冲刺无敌配置失败: {e.Message}"); }

        // ---- IMGUI 兼容层 (让 ConfigurationManager 能画出来) ----
        // 这些值每帧实时读取, 直接改 cfg 存盘即可生效(BepInEx 会热重载), 不用重启游戏
        var isec = "IMGUI";
        try
        {
            ImguiCompat.CfgTexMode = Config.Bind(isec, "DrawTextureMode", "Box",
                "GUI.DrawTexture 的实现方式:\n" +
                "  Box      = GUI.Box + 背景样式 (排队绘制, z序正确) [默认]\n" +
                "  Graphics = 转发给 Graphics.DrawTexture (立即绘制, 会盖住后面的控件)\n" +
                "  Off      = 不画");
            ImguiCompat.CfgPanelBg = Config.Bind(isec, "PanelBackground", false,
                "是否在窗口下再垫一层自己的深色底板(默认关, 用插件自己的背景)");
            ImguiCompat.CfgUseRealWindow = Config.Bind(isec, "UseRealWindow", false,
                "启用忠实重建的 GUI.Window 语义(WindowEmu) —— 让 CM 窗口内的控件可点。\n" +
                "默认关: 上一版一开就崩(首次走这条路就硬崩)。关着时走旧的 BeginArea 路径,不崩但 CM 窗口内点不动。");
            InputBlocker.CfgEnabled = Config.Bind(isec, "BlockGameInput", true,
                "面板打开时屏蔽游戏输入(键盘+鼠标)。\n" +
                "做法: 给 UnityEngine.Input 那批读输入的静态方法挂 Prefix, 面板开着时直接返回中性值。\n" +
                "注意: Unity 一帧的顺序是 Update → OnGUI, 面板画在 OnGUI 里, " +
                "所以对同一帧的 Update 而言有一帧延迟(开关那一帧各放行一次)。");
            InputBlocker.CfgBlockMouse = Config.Bind(isec, "BlockMouseToo", true,
                "鼠标类接口是否也挡。面板靠 IMGUI 事件工作, 不依赖 Input, 所以挡了更干净。");
            ConfigPanel.CfgEnabled = Config.Bind(isec, "ConfigPanel", true,
                "键盘配置面板。CM 的窗口内控件在本作里点不动\n" +
                "(替身 GUILayout.Window 缺少真窗口的裁剪组/控制ID作用域), 改走键盘。");
            UiHost.CfgEnabled = Config.Bind(isec, "SelfOnGuiHost", true,
                "★ 自建 OnGUI 宿主(注入一个自己的 MonoBehaviour 当面板宿主, 不再依赖 ConfigurationManager)。\n" +
                "\n" +
                "【排查用】关掉 = 根本不 AddComponent(不是\"挂上不干活\")。\n" +
                "注入托管类型进 il2cpp 域、再由 Unity 每帧回调 OnGUI, 是本插件侵入性最强的一件事;\n" +
                "万一出现无法解释的启动崩溃, 先单独关掉它看现象还在不在。\n" +
                "关掉后面板退回 ConfigurationManager 兜底路径(需要按 F1)。");
            ConfigPanel.CfgToggleKey = Config.Bind(isec, "ConfigPanelKey", "F8",
                "开关键盘配置面板的热键(KeyCode 名, 如 F8 / F9 / BackQuote)。\n" +
                "面板里: ↑↓ 选择, ←→ 改值(Shift ×10), Enter 保存, Esc 关闭。");
            BulletLab.CfgEnabled = Config.Bind(isec, "BulletLab", true,
                "★ F9 弹幕实验台。列出 BulletConfig 全表的弹幕, 点一下就放一个。\n" +
                "用途: 判断\"某个动作到底发不发弹幕\"——不用再翻日志来回试。\n" +
                "  表里有 = 它是弹幕; 表里没有 = 那多半是纯特效。\n" +
                "顶部\"最近捕获\"实时回显探针抓到的那一发, 可原地重放。\n" +
                "底部文本框可手填 名称 / id / id:名称, 表里没有的也照样试放。");
            BulletLab.CfgToggleKey = Config.Bind(isec, "BulletLabKey", "F9",
                "弹幕实验台的热键(KeyCode 名)。与 F8 配置面板互相独立, 可同开。\n" +
                "窗口可拖动(拖标题栏)。");
            FxAnatomy.CfgEnabled = Config.Bind(isec, "FxAnatomy", true,
                "★ 特效解剖: 把弹幕/特效的构成倒进日志 —— 层级、每个渲染器的材质、\n" +
                "【材质上全部着色器属性连同当前值】、粒子 startColor。\n" +
                "为什么要有它: UnityExplorer 在本作里 UI 起不来(UniverseLib 要 Il2CppStructArray<byte>,\n" +
                "而 Il2Cppmscorlib 在内存里有两份 -> Il2CppInterop 的 Single() 抛异常),\n" +
                "但我们要的信息用 metadata 里的 Shader.GetPropertyCount/GetPropertyName 就能读到。\n" +
                "关掉 = 根本不挂钩子。");
            SnapshotProbe.CfgEnabled = Config.Bind(isec, "Snapshot", true,
                "★【时停 + 现场捕获】= UnityExplorer 那一套, 自己做出来:\n" +
                "  SnapshotTimeKey 慢放循环 -> SnapshotKey 时停 -> SnapshotPickKey 捕获(名字标在屏幕上)。\n" +
                "  为什么必须自己做: UE 的 UI 在本作修不动(UniverseLib 要 Il2CppStructArray<byte>,\n" +
                "  而 Il2Cppmscorlib 在内存里有两份 -> Il2CppInterop 的 Single() 抛异常)。");
            SnapshotProbe.CfgKey = Config.Bind(isec, "SnapshotKey", "F6",
                "★ 时停开关(UnityEngine.KeyCode 名)。按一下 Time.timeScale=0, 再按恢复。\n" +
                "  ⚠ 这个键走 IMGUI 事件, 所以**冻结时照样收得到**(走 Input.GetKey* 会卡死, 栽过)。");
            SnapshotProbe.CfgFreeze = Config.Bind(isec, "SnapshotFreeze", true,
                "★ SnapshotKey 是否 = 「时停」开关（Time.timeScale = 0 / 再按恢复）。默认 true。\n" +
                "  ✅ 「恢复不了」那条已经修好: 恢复判定挂在 OnGUI 上(IMGUI 由渲染循环驱动,\n" +
                "     **不受 timeScale 影响**, 暂停菜单就是这么做的)。\n" +
                "  ⚠ 「状态机损坏」那条修不掉: 这游戏行动逻辑是时间驱动的, 冻久了/跨动作冻会打乱时序。\n" +
                "     ⇒ 冻住看清楚就赶紧按回来。");
            SnapshotProbe.CfgDumpScene = Config.Bind(isec, "SnapshotDumpScene", false,
                "时停时顺带把整个场景(所有特效 hub / 角色视觉件)递归摊进日志。**默认关**\n" +
                "  —— 用户要的是「自己捕获那个光弧」, 不是整个场景。捕获用 SnapshotPickKey。");
            SnapshotProbe.CfgFreezeMaxSec = Config.Bind(isec, "SnapshotFreezeMaxSec", 0,
                "冻结最多持续多少秒后自动恢复(兜底, 防卡死在冻结态)。**默认 0 = 不兜底**\n" +
                "  —— 你要慢慢看, 别让它在你看的时候自己解冻。真要保险就填个 60。");
            SnapshotProbe.CfgMax = Config.Bind(isec, "SnapshotMax", 24,
                "【只摊场景时】每类最多摊开几个（按层级路径去重，不是按名字 —— 场上 20 个角色视觉件\n" +
                "  全叫 \"Renderer\"，按名字去重会被压成 1 个；超了会明说）。");

            SnapshotProbe.CfgTimeKey = Config.Bind(isec, "SnapshotTimeKey", "F5",
                "★【慢放/时停循环】按一下在 SnapshotTimeSteps 里往前走一格。\n" +
                "  这个键存在的意义: 光弧只亮零点几秒, 人的反应追不上 —— 先慢放到 0.1/0.02,\n" +
                "  它就在屏幕上挂好几秒, 你从容再按 SnapshotKey 彻底停死。");
            SnapshotProbe.CfgTimeSteps = Config.Bind(isec, "SnapshotTimeSteps", "1,0.1,0.02,0",
                "慢放循环的档位（逗号分隔, 最后一个 0 = 时停）。");
            SnapshotProbe.CfgHud = Config.Bind(isec, "SnapshotHud", true,
                "是否在屏幕左上角显示 timeScale 状态 + 键位提示 + 捕获标注。");
            LiveInspector.CfgEnabled = Config.Bind(isec, "Inspector", true,
                "★【现场检查器】在被冻结的世界里列出捕获到的 GameObject，直接开关/挪位置，\n" +
                "  用「少一个就知道是谁」的办法把特效认出来。\n" +
                "  ★ 列表里**不持有任何 Unity 对象引用** —— 只存 实例ID+路径，操作时现场重找。\n" +
                "    因为这游戏的特效全是对象池的，跨帧拿着旧引用去写就是**不可 catch 的原生崩溃**。\n" +
                "  ⚠ 打开时会把光标放出来（本作平时可能是锁住的，否则面板点不到），关闭时还原。");
            LiveInspector.CfgKey = Config.Bind(isec, "InspectorKey", "F10",
                "开关面板的键。面板里: 点一行选中; [显/隐]=SetActive, [渲/空]=Renderer.enabled;\n" +
                "  下方 X±/Y±/Z± 挪位置(改步长), [重置位置], [打印详情到日志]。\n" +
                "  键盘也可: ↑↓ 选, PgUp/PgDn 跳 10, 空格=切换激活, E=切换渲染。");
            LiveInspector.CfgStep = Config.Bind(isec, "InspectorStep", 0.5f,
                "挪位置的默认步长（面板里也能改）。");
            LiveInspector.CfgMaxRows = Config.Bind(isec, "InspectorMaxRows", 200,
                "列表最多画几行（超了会明说剩多少）。用面板上的过滤缩小范围。");

            VisualTimeline.CfgEnabled = Config.Bind(isec, "VisTimeline", false,
                "★【可视性时间线】**不需要反应时间**地抓「一闪而过的东西」。\n" +
                "  VisTimelineActions 命中的动作一开始就【自动逐帧】记录场上每个渲染器的\n" +
                "  出现/显形/隐去/消失，动作结束后自动把整条时间线倒进日志。**全程零操作。**\n" +
                "  ★ 输出按【存活帧数升序】排列 —— 只活个位数帧的那个就是那道一闪而过的光弧，\n" +
                "    直接排在最前面。常驻对象活几百帧，排最后。\n" +
                "  信号源: ParticleSystem.particleCount>0（粒子型） + Renderer.isVisible（Mesh型）,\n" +
                "  外加两个「打开」事件: GameObject.SetActive(true) / Renderer.enabled=true\n" +
                "  （这两类 Instantiate 探针看不见 —— 东西早就造好了, 只是被池子收起来再放出来）。");
            VisualTimeline.CfgActions = Config.Bind(isec, "VisTimelineActions", "dashSkill,dashSkill2,dash",
                "只在这些动作名(子串, 逗号分隔)期间录制。");
            VisualTimeline.CfgGrace = Config.Bind(isec, "VisTimelineGrace", 0.8f,
                "动作名不再命中后，再继续录多少秒（特效往往比动作名晚一拍才熄灭）。");
            VisualTimeline.CfgMax = Config.Bind(isec, "VisTimelineMax", 40,
                "存活时间排行最多列几条（越靠前越一闪而过）。事件日志的硬上限 = 本值 × 20, 超了明说。");
            InstantiateProbe.CfgEnabled = Config.Bind(isec, "InstantiateProbe", true,
                "★【实例化探针】盯住 UnityEngine.Object.Instantiate（所有重载）+ ParticleSystem.Play，\n" +
                "只在 InstantiateProbeActions 命中的动作窗口内记录。\n" +
                "存在的原因: 我们只挂了两个出生口(createVisualEffect / createBulletImp), \n" +
                "  如果目标既不是特效也不是弹幕(比如直接 Instantiate 出来、或某个视觉件被 Play 打开), \n" +
                "  那两个口子一个都看不见 —— 日志干干净净, 看着像什么都没发生。\n" +
                "关掉 = 根本不挂钩子。性能: 热路径只做一次字符串比较(动作名每帧刷新)。");
            InstantiateProbe.CfgActions = Config.Bind(isec, "InstantiateProbeActions", "dashSkill,dash",
                "只在这些动作名(子串, 逗号分隔)期间记录。默认覆盖 dashSkill/dashSkill2(消耗 MP 的冲刺), \n" +
                "以及各种 dash/dashAir/attackholdDashEX。");
            InstantiateProbe.CfgMax = Config.Bind(isec, "InstantiateProbeMax", 120,
                "最多记录多少【种】对象(去重键=动作|对象名)。超了会明说, 不静默丢。");
            InstantiateProbe.CfgParticles = Config.Bind(isec, "InstantiateProbeParticles", true,
                "是否连 ParticleSystem.Play 一起记(有些东西不是造出来的, 是被打开的)。");
            DashAnatomy.CfgEnabled = Config.Bind(isec, "DashAnatomy", true,
                "★【冲刺解剖】一个动作按下, 把它拉起的**全部对象递归展开**倒进日志:\n" +
                "  层级树 / 每个节点的组件 / 每个渲染器的材质+shader+**着色属性当前值** /\n" +
                "  每个 MaterialTinter·Proxy 的**插值器逐条**(真实类名+propName+start/end) /\n" +
                "  粒子 startColor。**特效与弹幕两个出生口都覆盖**。\n" +
                "存在的原因: 这项目反复在\"动作→对象→目标\"的映射上栽跟头(把 _AddColor 当电弧、\n" +
                "  把影子当电弧、把 es_dash_01 当 MP 冲刺的特效)—— 每次都是靠推的, 没人把现场摊开看过。\n" +
                "  UE 在这游戏里用不了(见 FxAnatomy 注释) ⇒ 自己造这个摊开。\n" +
                "关掉 = 根本不挂钩子。");
            DashAnatomy.CfgActions = Config.Bind(isec, "DashAnatomyActions", "dash,esbullet",
                "只解剖【动作名 或 弹幕名】含这些串的对象(逗号分隔)。\n" +
                "dash 覆盖 dash/dash2/dashAir/dashAir2/3/dashSkill/dashSkill2/attackholdDashEX;\n" +
                "esbullet 是冲刺共用那颗弹幕(idx 10340101) —— MP 冲刺**只有弹幕、没有特效**, 这口不能少。");
            DashAnatomy.CfgDepth = Config.Bind(isec, "DashAnatomyDepth", 5,
                "递归展开的最大深度。到顶会**明说**还有几个子物体没展开(不静默截断)。");
            DashAnatomy.CfgMax = Config.Bind(isec, "DashAnatomyMax", 10,
                "本次启动最多解剖几个对象(去重键 = 动作|对象名)。到顶会明说。");
            FxAnatomy.CfgKeyword = Config.Bind(isec, "FxAnatomyKeyword", "esbullet",
                "只解剖名字含此串的弹幕。默认 esbullet = 剑气那一颗。\n" +
                "⚠ 别写成 bullet: 会连 addbuffbullet 一起匹配上, 而它是批量生成的 ——\n" +
                "第一版按实例排队, 被它刷屏刷到卡爆(日志里连着一屏全是 addbuffbullet)。\n" +
                "留空 = 全部弹幕(更危险, 请配合总上限使用)。");
            ImguiCompat.CfgVerbose = Config.Bind(isec, "Verbose", false,
                "打印前若干次 IMGUI 调用, 排查绘制问题用");
        }
        catch (Exception e) { Log.LogWarning($"读取 IMGUI 配置失败: {e.Message}"); }

        // ---- 面板宿主: 自己的 OnGUI, 不再寄生在 ConfigurationManager 上 ----
        // 必须在任何 hook 之前挂 —— 面板是排查一切问题的手段, 它自己不能是最脆弱的一环。
        UiHost.Install(this);

        var harmony = new Harmony(Guid);
        int hooked = Patcher.Apply(harmony);

        Log.LogInfo($"================================================");
        Log.LogInfo($" BBEE JS Patcher 已加载");
        Log.LogInfo($" 配置目录 : {Root}");
        Log.LogInfo($" 成功挂载 : {hooked} 个 Hook");

        // ★ 构建标记 —— 用【程序集自身】的写入时间，我改不了它，做不了假。
        //   起因：今天反复部署，多次出现"这份日志是哪一版跑出来的"说不清的情况，
        //   而其中一次（选段探针 0 条）我无法判断是"游戏加载的还是旧 DLL"还是"探针真没触发"。
        //   有了这一行，以后任何日志都能自证版本，这类歧义一次性消除。
        try
        {
            string asmPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            Log.LogInfo($" [构建标记] {asmPath}");
            Log.LogInfo($" [构建标记] 程序集写入时间 = {System.IO.File.GetLastWriteTime(asmPath):yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception e) { Log.LogWarning(" [构建标记] 读取失败: " + e.Message); }
        if (hooked == 0)
            Log.LogError(" 没有挂载到任何 Hook —— 游戏版本可能不匹配！");
        Log.LogInfo($"================================================");
    }
}
