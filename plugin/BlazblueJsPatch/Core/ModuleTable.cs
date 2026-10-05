using System;
using HarmonyLib;

namespace BlazblueJsPatch;

/// <summary>
/// 模块表 —— 整个插件的挂载清单。
///
/// 这张表就是"架构"本身: 哪条管线存在、按什么顺序挂、受哪个总开关管、靠哪个配置项启用。
/// 想加/删一条管线, 只改这里。
///
/// 分区约定(与 cfg 段名一致, 便于对照日志):
///   [总开关] Mount*     —— 关掉 = 根本不挂载
///   [冲刺无敌]/[战斗]    —— 战斗规则类
///   [特效换色]           —— 特效表现管线
///   [连段模组]/[动作变速]/[纹章解放] —— ES 机体性能
///   [动作记录]/[动作结构]/[贝德维尔]/[诊断] —— 只观察不改行为
///   [IMGUI]              —— 面板与输入
/// </summary>
internal static class ModuleTable
{
    internal static ModuleHost Build()
    {
        var h = new ModuleHost();

        // ---------------------------------------------------------- 战斗规则
        h.Add(ModModule.Of("dashInv", "冲刺无敌", "冲刺无敌", "MountCombat",
            () => true,
            DashInvincible.Apply));

        h.Add(ModModule.Of("perfectDodge", "完美闪避(冲刺全程)", "冲刺无敌", "MountCombat",
            () => true,
            PerfectDodge.Apply));

        h.Add(ModModule.Of("jumpDash", "跳跃⟷冲刺 互重置", "冲刺无敌", "MountCombat",
            () => true,
            JumpDashCrossReset.Apply));

        h.Add(ModModule.Of("skillCost", "技能无耗 MP", "冲刺无敌", "MountCombat",
            () => true,
            SkillCostTweak.Apply));

        // ---------------------------------------------------------- 特效表现管线
        // ⚠ 这里就是"一条独立管线"的入口。RecolorPipeline 内部再分粒子路/插值器路,
        //   总开关挂在管线上 —— 不是挂在某一条路上(上一版 RecolorEffect=false 只拦住了
        //   粒子路, 插值器路照样染, 排查时把人带偏过)。
        h.Add(ModModule.Of("recolor", "特效换色管线", "特效换色", "MountRecolor",
            () => true,
            RecolorPipeline.Apply));

        // ---------------------------------------------------------- ES 机体性能
        // 特效裁剪: 按 `特效名:子物体名` 关掉某些子物体。
        // 起因: 冲刺叠加层(es_dash_01 里的 Other/MaterialTinterProxy)只有原色特效有,
        //       配换色皮肤时反而碍眼 —— 用户要的是关掉它。见 EffectSuppress 的注释。
        h.Add(ModModule.Of("fxSuppress", "特效裁剪(关子物体)", "特效换色", "MountRecolor",
            () => EffectSuppress.CfgEnabled?.Value == true,
            EffectSuppress.Apply));

        h.Add(ModModule.Of("esSpeed", "动作变速", "动作变速", "MountEsMech",
            () => true,
            EsActionSpeed.Apply));

        h.Add(ModModule.Of("esCombo", "连段模组(重排普攻)", "连段模组", "MountEsMech",
            () => true,
            EsComboChain.Apply));

        h.Add(ModModule.Of("esEmblem", "纹章解放", "纹章解放", "MountEsMech",
            () => true,
            EsEmblemBurst.Apply));

        // ---------------------------------------------------------- 只读转储 / 诊断
        h.Add(ModModule.Of("structure", "动作结构转储", "动作结构", "MountExtra",
            () => true,
            ActionStructure.Apply));

        h.Add(ModModule.Of("journal", "动作时序记录", "动作记录", "MountExtra",
            () => true,
            ActionJournal.Apply));

        h.Add(ModModule.Of("chainDump", "技能链转储", "连段模组", "MountExtra",
            () => true,
            SkillChainDump.Apply));

        h.Add(ModModule.Of("actionProbe", "动作名探针", "诊断", "MountExtra",
            () => true,
            ActionProbe.Apply));

        h.Add(ModModule.Of("bulletProbe", "弹幕探针", "诊断", "MountExtra",
            () => true,
            BulletProbe.Apply));

        // 特效解剖: 把某个特效(默认子弹/剑气)的层级、每个渲染器的材质、
        // **材质上全部着色器属性连同当前值**、粒子 startColor 倒进日志。
        // 存在的原因: UnityExplorer 在这游戏里 UI 永远起不来(见 FxAnatomy 的注释),
        // 而我们要的信息本来就能从 metadata 里的 Shader 属性枚举 API 拿到。
        // 关掉 = **根本不挂钩子**(诊断模块应当如此, 免得"关了还在跑"误导排查)。
        // 冲刺解剖: **一个动作按下**，把它拉起的全部对象递归展开 —— 层级/组件/每个渲染器的材质
        // 与着色属性当前值/每个 Tinter 的插值器逐条(含真实类名)，特效与弹幕两个出生口都覆盖。
        // 存在的原因: 这个项目反复在"动作→对象→目标"的映射上栽跟头(_AddColor 当电弧、
        // 影子当电弧、es_dash_01 当 MP 冲刺)，每次都是靠推的；UE 又用不了 ⇒ 自己造一个"摊开"。
        // 实例化探针: 盯 `UnityEngine.Object.Instantiate`（**万物必经的出生口**）+ `ParticleSystem.Play`，
        // 只在冲刺动作窗口内记录。存在的原因: 我们挂的两个出生口(createVisualEffect / createBulletImp)
        // 都可能漏掉"直接 Instantiate 出来"或"被 Play 打开"的东西 —— 那样日志看起来像"什么都没发生"。
        // 快照: F6 冻结时间 + 把场上活着的对象（VFXEffectHub / ActorVisualBase）递归摊开。
        // 等价于"暂停 + 用鼠标抓实例"，但输出到日志 —— UE 的 UI 在本作修不动(见类注释)。
        h.Add(ModModule.Of("snapshot", "冻结+现场快照", "诊断", "MountExtra",
            () => SnapshotProbe.CfgEnabled?.Value == true,
            SnapshotProbe.Apply));

        h.Add(ModModule.Of("instProbe", "实例化探针", "诊断", "MountExtra",
            () => InstantiateProbe.CfgEnabled?.Value == true,
            InstantiateProbe.Apply));

        // 可视性时间线: 目标动作一开始就**自动逐帧**录"场上谁活着"，动作结束自动出时间线，
        // 按【存活帧数升序】排序 ⇒ 只活几帧的那个就是"一闪而过的那道光弧"。
        // 存在的原因: F6/F7 都要求人在那一瞬间按键 —— 对一个亮 0.2 秒的光弧来说，
        // 那个设计要求本身就是错的（用户原话: "没有时停, 我根本抓不住那个一闪而过的光弧"）。
        h.Add(ModModule.Of("visTimeline", "可视性时间线(自动录)", "诊断", "MountExtra",
            () => VisualTimeline.CfgEnabled?.Value == true,
            VisualTimeline.Apply));

        // 现场检查器: 在**被冻结的世界**里列出捕获到的 GameObject，直接开关/挪位置，
        // 用"少一个就知道是谁"的办法把特效认出来（用户要的"判断特效到底是谁"）。
        h.Add(ModModule.Of("inspector", "现场检查器(冻结里开关/挪物体)", "诊断", "MountExtra",
            () => LiveInspector.CfgEnabled?.Value == true,
            LiveInspector.Apply));

        // 叠色探针触发器: 让「引擎自己的叠色记账表」(ReferenceCounterMap) 能随时抓、按动作连抓。
        // 原来探针只在 MaterialTinterProxy 命中时偶然触发 ⇒ 想抓统计（九个纹章一致性）只能靠运气。
        h.Add(ModModule.Of("refCounter", "叠色探针(引擎记账表)", "诊断", "MountExtra",
            () => RefCounterProbe.CfgEnabled?.Value == true,
            RefCounterProbe.Apply));

        h.Add(ModModule.Of("dashAnatomy", "冲刺解剖", "诊断", "MountExtra",
            () => DashAnatomy.CfgEnabled?.Value == true,
            DashAnatomy.Apply));

        h.Add(ModModule.Of("fxAnatomy", "特效解剖", "诊断", "MountExtra",
            () => FxAnatomy.CfgEnabled?.Value == true,
            FxAnatomy.Apply));

        h.Add(ModModule.Of("ahWing", "贝德维尔翅膀", "贝德维尔", "MountExtra",
            () => true,
            AhWing.Apply));

        h.Add(ModModule.Of("limitTrace", "段数限制追踪", "诊断", "MountCombat",
            () => true,
            ActionLimitTrace.Apply));

        h.Add(ModModule.Of("slowTrace", "时缓追踪", "诊断", "MountCombat",
            () => true,
            SlowMotionTrace.Apply));

        // ---------------------------------------------------------- 面板 / 输入
        h.Add(ModModule.Of("imgui", "IMGUI 兼容层", "IMGUI", "MountImgui",
            null, // 兼容层没有独立开关: 关掉它 = CM 一画就抛, 没有"半开"的意义
            ImguiCompat.Apply));

        h.Add(ModModule.Of("imguiProbe", "IMGUI 事件探针", "IMGUI", "MountImgui",
            null,
            harmony =>
            {
                int n = 0;
                try { n += ImguiCompat.ApplyMouseProbe(harmony); } catch (Exception e) { LogEx.Err("ApplyMouseProbe", e); }
                try { n += ImguiCompat.ApplyEventUseProbe(harmony); } catch (Exception e) { LogEx.Err("ApplyEventUseProbe", e); }
                try { n += ImguiCompat.ApplySelfTest(harmony); } catch (Exception e) { LogEx.Err("ApplySelfTest", e); }
                return n;
            }));

        h.Add(ModModule.Of("inputBlocker", "面板打开时屏蔽输入", "IMGUI", "MountInput",
            () => true,
            InputBlocker.Apply));

        return h;
    }
}
