using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using NOAH.VFXInterpolator;
using UnityEngine;

namespace BlazblueJsPatch;

/// <summary>
/// 换色的"笔" —— 把目标色刷到实际的着色载体上。
///
/// ★★ 两个必须记住的事实(反汇编得到, 别再当假设)
/// ─────────────────────────────────────────────
/// 1. `MaterialTinter.Play(entries, proxy)` 的尾部【就是】调 `CreateInterpolatorSets(entries, ...)`
///    —— 也就是说每次播放都会重建插值器集合, 而不是只在创建时建一次。
/// 2. `InterpolatorSet` 的构造函数把传进来的 `InterpolatorBase[]` **按引用存进 +0x40**,
///    一个字节都没克隆:
///        mov qword ptr [r14 + 0x40], rdi      ; rdi = inInterpolators 参数
///    所以集合和源数组指向的是**同一批 MaterialColorInterpolator 对象**。
///
///    推论: 就地改写源数组里的 startValue/endValue, 对【已经在播】的特效同样生效 ——
///    这就是"配置改了立刻变色"能成立的原因(见 <see cref="RecolorPipeline"/> 的 Update 追染)。
///
/// ★ 为什么要记【原始值】
/// ────────────────────
/// 上一版是**破坏性改写**: 直接把 startValue/endValue 覆盖成染过的颜色。
/// 于是第二次换色时, 它把"已经染过的颜色"当成"原色"再染一遍 ——
/// 亮度换算 `k = 原亮度/目标亮度` 有了累积效应,
/// BrightnessScale ≠ 1 时每换一次色整体亮度就翻一倍(实测症状: 越换越刺眼)。
/// 现在每个插值器的原值只记一次, 之后永远从原值算 —— 换色可逆、可重复。
/// </summary>
internal static class TintBrush
{
    private struct Orig { internal Color Start; internal Color End; }

    private static readonly Dictionary<IntPtr, Orig> _origin = new Dictionary<IntPtr, Orig>();
    private const int OriginCap = 8000;

    private static int _rewritten;

    internal static int RewrittenCount => _rewritten;
    internal static int OriginCount => _origin.Count;

    /// <summary>
    /// 感知亮度（Rec.709）。
    ///
    /// ⚠⚠ 这里原来用的是 `max(r,g,b)` —— **那是错的亮度口径**，而且它一个错
    ///    **同时**解释了两个看似相反的症状（用户原话：
    ///    "深色相会出现类似取反的效果变得深暗、不透明，对浅色系又会变得刺眼"）：
    ///
    ///      k = max(orig) / max(target)   ⇒   结果的感知亮度 = Luma(target) · k
    ///                                         = Luma(orig) · [Luma(t)/max(t)] / [Luma(o)/max(o)]
    ///
    ///    ⇒ 它"保"的是**最大通道**，不是亮度。而 `max` 与 `Luma` 的偏离程度**因色而异**：
    ///
    ///    · **中性原色**（R=G=B，如 ring02 的 `_TintColor` = 4.237 灰）
    ///      配**饱和目标色**时 `max(t) ≫ Luma(t)` ⇒ 结果比原来**暗**
    ///      ⇒ 观感"深暗、发实、像不透明"（透明感就是这么丢的：暗下来就不像半透明了）
    ///    · **单通道尖峰原色**（如超亮蓝 5.647/6.525/11.984，主亮全在蓝通道，
    ///      而蓝在感知上权重只有 0.0722）⇒ `max(o) ≫ Luma(o)` ⇒ 结果比原来**亮**
    ///      ⇒ 观感"刺眼、发白"（实测 Luma 6.73 → 8.67，且原来是单通道尖峰、
    ///        现在摊到三个通道上，视觉上的亮度落差比这个数还大）
    ///
    /// 改成 Rec.709 后，"保亮度"才真的是保亮度：结果的 Luma 恒等于原色的 Luma。
    /// ⚠ 幂等性仍然成立（本函数是"重复写不漂移"的不动点，见 RecolorConfig.Target）：
    ///    Tint(Tint(o)) 的 Luma 还是 Luma(orig) ⇒ k 不变 ⇒ 结果不变。
    /// </summary>
    private static float Luma(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

    /// <summary>
    /// 把原色染成目标色, **保留原 alpha**。
    /// alpha 是"出现→消失"的包络曲线, 动了它整个特效的节奏就毁了。
    /// </summary>
    internal static Color Tint(Color orig, RecolorConfig.Target t)
    {
        float lumO = Luma(orig);
        float lumT = Luma(t.Color);
        float maxO = Math.Max(orig.r, Math.Max(orig.g, orig.b));
        float maxT = Math.Max(t.Color.r, Math.Max(t.Color.g, t.Color.b));

        float kLuma = (lumT > 0.0001f && lumO > 0.0001f) ? lumO / lumT : 1f;   // 保感知亮度
        float kPeak = (maxT > 0.0001f && maxO > 0.0001f) ? maxO / maxT : 1f;   // 保最大通道

        // ★★ 「保亮度」到底保什么，是个**无解的取舍** —— 取决于目标色的饱和程度：
        //   · 饱和目标(如 CD00F0)：`max` 远大于 `Luma`（比例 3.9:1）
        //       - 保峰值 ⇒ 不过曝、不刺眼，但整体**偏暗**（用户: "深色相变得深暗"）
        //       - 保亮度 ⇒ 亮度一致，但峰值通道被推到原来的 ~3.9 倍
        //                  ⇒ 在 bloom / 色调映射下**刺眼**（用户: "CD00F0 明显刺眼"）
        //   · 淡色目标(如 A0F0C0)：`max ≈ Luma`（比例 0.91）⇒ 两种口径几乎一样 ⇒ **怎么都对**
        //     （用户实测"对 A0F0C0 这种亮色系正常了" —— 正好印证这个分析）
        //   ⇒ 所以不是一个"哪个对"的问题，而是**必须显式选一个口径**。
        //
        // ★★ 默认口径 `capped` = `min(kLuma, kPeak)`，规则一句话说得清：
        //     **保感知亮度，但绝不允许结果峰值超过原色峰值。**
        //   为什么是它（用用户的两个实测数据点反推，不是拍脑袋选的中点）：
        //     · 淡色目标 A0F0C0：kLuma(7.82) < kPeak(12.73) ⇒ 取 kLuma
        //       —— 正好等于用户说的"正常了"那一版 ✓
        //     · 饱和目标 CD00F0：kLuma(28.2) > kPeak(12.73) ⇒ 被压到 kPeak
        //       —— 峰值回到原色水平 ⇒ 不再过曝刺眼；
        //          而且比旧版**更亮**（旧版还有一道 k≤8 的静默夹断，把它压到峰值 7.5）
        //       ⇒ 用户之前抱怨的"深色相变深暗"，很大程度上就是那道夹断造成的。
        //   物理含义也直白：**绝不把光效的核心烧得比原来更烫**（bloom 阈值就是这么判的）。
        // ⚠ 兜底也必须是**旧算法**（peak + cap 8）。曾经兜底写的是 "capped"/32 ——
        //   那等于留了一道暗门：配置一旦没绑上（键被删、名字打错、新装的 cfg 生成失败），
        //   效果会**悄悄换成另一套口径**，而日志上看不出任何异常。兜底必须等于"默认行为"。
        // 口径从 Target 上读（不是全局配置）—— 点名名单那条路要用不同的口径，见 ObjectTarget。
        string mode = t.Mode ?? "peak";

        // ★ `hue` 模式 —— **照着官方换色皮肤的做法**：逐键换色相，保留原色自己的
        //   饱和度(S)与亮度(V)结构，只把色相换成目标色的。
        //
        //   离线铁证（`tools/_skindiff_out.txt`）：官方改动总是**保住每个键各自的 alpha**
        //   （start 的 0.784 → 0.784、end 的 0.000 → 0.000），只换 RGB 色相。
        //   而且 `ring02` 那个 **S=0 的中性乘数 (4.237,4.237,4.237)** 四套皮肤**一次都没动** ——
        //   因为中性值换色相等于不变。⇒ 官方**只重绘本来就有颜色的载体**。
        //
        //   与默认 `peak` 的两个本质区别：
        //     ① **中性(S=0)原色保持不变** —— 不会把亮度乘数强行染成目标色；
        //     ② **(0,0,0,x) 保持是黑** —— 不会像 `peak` 那样因 max=0 走 k=1 兜底
        //        而把"暗色渐入"变成"满亮目标色"。
        //   ⇒ 层的相对配比不被破坏，这才是"叠色"能对得上的前提。
        if (mode.Equals("hue", StringComparison.OrdinalIgnoreCase))
        {
            float mx = Math.Max(orig.r, Math.Max(orig.g, orig.b));
            float mn = Math.Min(orig.r, Math.Min(orig.g, orig.b));
            float sat = mx > 0.00001f ? (mx - mn) / mx : 0f;      // 原色饱和度(HSV 的 S)
            float tmx = Math.Max(t.Color.r, Math.Max(t.Color.g, t.Color.b));
            if (tmx <= 0.00001f || lumO <= 0.00001f)
                return new Color(orig.r, orig.g, orig.b, orig.a);  // 中性/全黑：原样返回

            // 目标色的"满饱和方向"（最大分量归一为 1），再缩放到**原色自己的亮度**
            float dx = t.Color.r / tmx, dy = t.Color.g / tmx, dz = t.Color.b / tmx;
            float dl = 0.2126f * dx + 0.7152f * dy + 0.0722f * dz;
            float sc = dl > 0.00001f ? lumO / dl : 1f;
            dx *= sc; dy *= sc; dz *= sc;
            float g = 0.2126f * dx + 0.7152f * dy + 0.0722f * dz;  // 该满饱和色的亮度(=lumO)

            // ★ 关键一步：按**原色的饱和度**在"同亮度灰"与"满饱和目标色"之间插值。
            //   sat=0（中性）⇒ 结果 = 灰 = 原亮度 ⇒ 不变；sat=1 ⇒ 满饱和目标色。
            float r2 = g + (dx - g) * sat;
            float g2 = g + (dy - g) * sat;
            float b2 = g + (dz - g) * sat;
            return new Color(r2, g2, b2, orig.a);
        }

        float k;
        if (mode.Equals("luma", StringComparison.OrdinalIgnoreCase)) k = kLuma;
        else if (mode.Equals("capped", StringComparison.OrdinalIgnoreCase)) k = Math.Min(kLuma, kPeak);
        else k = kPeak;   // peak = 旧公式（默认）

        if (!t.KeepBright) k = 1f;
        // 手动亮度倍率叠加在"保持原亮度"之上: 加色混合下洋红(1,0,1)=2.0 天然比暗蓝亮,
        // 即使亮度已对齐, 观感仍可能偏刺眼, 这里给一个直接压暗的旋钮。
        k *= t.Scale;
        if (k < 0f) k = 0f;

        // ⚠ 上限不能再是 8: 用 Luma 口径后，"中性灰 → 饱和色"这类换算的比例本来就很大
        //   （要把两个通道清零还要保住总亮度, 剩下的通道必须抬得很高, 这是物理上正确的）。
        //   8 那道夹断会让结果**够不到目标色的观感**（ring02 那种 4.237 的中性乘数就会中招）。
        //   ⇒ 抬到 TintMaxScale(默认 16)，而且**夹断时必须留痕** —— 静默截断是本项目反复栽的模式。
        float cap = (float)(RecolorConfig.MaxScale?.Value ?? 8.0);   // 兜底 = 旧版那道 8
        if (cap <= 1f) cap = 8f;
        if (k > cap)
        {
            LogEx.Once("recolor|clamp", $"[特效换色] 亮度倍率被夹断: {k:0.###} -> {cap}（TintMaxScale 可调）");
            k = cap;
        }
        return new Color(t.Color.r * k, t.Color.g * k, t.Color.b * k, orig.a);
    }

    /// <summary>
    /// 第一次见到这个插值器时记下原值。之后一律从原值算 —— 见类注释。
    /// 用【原生指针】当 key: 每次 TryCast 都会产生新的托管包装, 引用比较靠不住。
    /// </summary>
    private static Orig OriginOf(MaterialColorInterpolator mc)
    {
        IntPtr key;
        try { key = mc.Pointer; } catch { return new Orig { Start = mc.startValue, End = mc.endValue }; }

        if (_origin.TryGetValue(key, out var o)) return o;
        o = new Orig { Start = mc.startValue, End = mc.endValue };
        if (_origin.Count >= OriginCap)
        {
            // 兜底: 长局下来效果实例会不断新建, 表不能无限涨。
            // 清空只是让这些实例的"原值"退化成当前值(一次性亮度漂移), 不会崩。
            _origin.Clear();
            LogEx.Once("recolor|origincap", $"[特效换色] 原值表达到 {OriginCap} 条, 已清空重建(长局正常现象)");
        }
        _origin[key] = o;
        return o;
    }

    /// <summary>就地改写一个 InterpolatorBase[] 里所有颜色插值器。</summary>
    internal static int RewriteArray(object arr, RecolorConfig.Target t, string where)
    {
        if (RecolorConfig.TintOnInterpolators?.Value == false) return 0;   // 二分开关
        int n = 0;
        try
        {
            var props = Cfg.List(t.Props);
            foreach (var item in Reflect.Items(arr))
            {
                if (item == null) continue;

                // ⚠ 数组元素是【基类 InterpolatorBase 包装】—— 多态 SerializeReference 数组
                //   在 Il2CppInterop 下的固有退化。基类包装上找不到 startValue/endValue
                //   (旧版日志里就卡在这), 必须按具体类型 TryCast 重新包装指针。
                var mc = Reflect.Cast<MaterialColorInterpolator>(item);
                if (mc == null)
                {
                    // 纹理/向量等插值器没有颜色, 忽略是正常的 —— 但要说清是"哪种"被忽略了
                    LogEx.Once("recolor|skipinterp|" + item.GetType().FullName,
                               $"[特效换色] 跳过非颜色插值器 {item.GetType().FullName}");
                    continue;
                }

                string prop = "";
                try { prop = mc.propName ?? ""; } catch { }
                if (!PropMatches(prop, props))
                {
                    // ⚠ 属性名不在 TintProperties 里也要留痕。静默 continue 的后果是:
                    //   把 TintProperties 配错了, 整条插值器路一声不吭地全空转,
                    //   而日志上"什么都没发生" —— 正是本项目反复栽的那个假阴性模式。
                    LogEx.Once("recolor|propmiss|" + prop,
                               $"[特效换色] 插值器 propName=\"{prop}\" 不在 TintProperties " +
                               $"({string.Join("/", props)}) 里, 跳过 —— 配错的话整条路会全空转");
                    continue;
                }

                var o = OriginOf(mc);
                LogElem(where, prop, o, mc);

                try
                {
                    mc.startValue = Tint(o.Start, t);
                    mc.endValue = Tint(o.End, t);
                    n++;
                }
                catch (Exception e) { Reflect.WarnOnce(prop, "写插值器", e); }
            }
            _rewritten += n;
        }
        catch (Exception e) { LogEx.Err("TintBrush.RewriteArray/" + where, e); }
        return n;
    }

    private static readonly HashSet<string> _elemSeen = new HashSet<string>();

    private static void LogElem(string where, string prop, Orig o, MaterialColorInterpolator mc)
    {
        if (!RecolorConfig.Verbose) return;
        if (_elemSeen.Count >= 40) return;
        if (!_elemSeen.Add(where + "|" + prop)) return;
        Plugin.Log?.LogInfo($"[特效换色:elem] {where} prop=\"{prop}\" " +
                            $"原生 start={Reflect.Fmt(o.Start)} end={Reflect.Fmt(o.End)} " +
                            $"-> 现在 end={Reflect.Fmt(mc.endValue)}");
    }

    private static bool PropMatches(string propName, string[] kws)
    {
        if (string.IsNullOrEmpty(propName)) return false;
        foreach (var k in kws)
            if (k.Length > 0 && propName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    // ------------------------------------------------------------------ 粒子

    /// <summary>
    /// 改粒子颜色。
    ///
    /// ⚠ 绝不能直接 `main.startColor = new MinMaxGradient(color)` ——
    ///   那会把原来的 alpha 和渐变结构一起抹掉。原本会淡出/半透明的粒子会变成
    ///   恒定不透明的纯色, 屏幕上就是一个又大又亮的球(实测踩过)。
    ///   正确做法: **只换 RGB, 保留原有的 alpha**。
    /// </summary>
    internal static int TintParticles(GameObject go, RecolorConfig.Target t)
    {
        if (go == null) return 0;
        int ok = 0;
        try
        {
            var ty = Il2CppInterop.Runtime.Il2CppType.From(typeof(ParticleSystem));
            var comps = go.GetComponentsInChildren(ty, true);
            foreach (var c in Reflect.Items(comps))
            {
                // ⚠ 不能写 `c as ParticleSystem` —— GetComponentsInChildren(Il2CppSystem.Type)
                //   返回的是 Component 包装对象, as 恒 null 且静默跳过。
                var ps = Reflect.Cast<ParticleSystem>(c);
                ok += TintOnePs(ps, t);
            }
        }
        catch (Exception e) { LogEx.Err("TintBrush.TintParticles", e); }
        return ok;
    }

    /// <summary>
    /// 改粒子颜色 —— ★ 必须【按模式就地改字段】, 不能整个换掉。
    ///
    /// 事故记录（2026-10-04, 用户报"冲刺纹章/纹章解放变成一个实心圆"）:
    ///   原来写的是 `main.startColor = new MinMaxGradient(color)`。
    ///   实测 interop 里 `MinMaxGradient.color` **只有 getter 没有 setter**(get_color 有、set_color 0 个),
    ///   而 ctor(Color) 造出来的是【纯色模式】的结构 —— 于是把原本是
    ///   **渐变(Gradient)** 的 startColor 整个换成了单色 ⇒ 那圈"出现→淡出"的 alpha 包络没了,
    ///   屏幕上就是一个不透明实心圆。
    ///
    /// 好在字段是暴露的(m_Mode / m_Color / m_ColorMin / m_ColorMax 都在 interop 里,
    /// 而 m_GradientMin/Max 里真正的 colorKeys / SetKeys 没暴露)。
    /// 所以做法是: 装箱 -> 按 m_Mode 只改颜色字段 -> 拆箱写回, **结构一个字节都不动**。
    ///   m_Mode: 0=Color 1=Gradient 2=TwoColors 3=TwoGradients (Unity 的 MinMaxGradientState)
    ///   · 0 -> 改 m_Color
    ///   · 2 -> 改 m_ColorMin / m_ColorMax(两个都保留各自 alpha)
    ///   · 1/3 -> 【不染】并留痕: 渐变改不动, 硬改就是实心圆。
    /// </summary>
    internal static int TintOnePs(ParticleSystem ps, RecolorConfig.Target t)
    {
        if (ps == null) return 0;
        try
        {
            var main = ps.main;
            var g = main.startColor;              // 包装对象(指向 native 那份 MinMaxGradient)
            int n = TintMmGradient(g, t);
            if (n > 0)
            {
                main.startColor = g;
                PsLogOnce((int)g.m_Mode, g);
            }
            return n;
        }
        catch (Exception e) { Reflect.WarnOnce("startColor", "写粒子", e); return 0; }
    }

    /// <summary>按 `m_Mode` 只改颜色字段(结构不动) —— 粒子路和通用扫描共用。</summary>
    private static int TintMmGradient(ParticleSystem.MinMaxGradient g, RecolorConfig.Target t)
    {
        if (g == null) return 0;
        int mode = (int)g.m_Mode;
        switch (mode)
        {
            case 0:      // Color —— ★ Unity 把"唯一那个颜色"存在 m_ColorMax 里
                g.m_ColorMax = Tint(g.m_ColorMax, t);      // Tint 保留它自己的 alpha(淡出靠它)
                return 1;

            case 2:      // TwoColors
            case 4:      // RandomColor(同样用 min/max)
                g.m_ColorMin = Tint(g.m_ColorMin, t);
                g.m_ColorMax = Tint(g.m_ColorMax, t);
                return 2;

            case 1:      // Gradient
            case 3:      // TwoGradients
                return TintGradient(g.m_GradientMax, t) + TintGradient(g.m_GradientMin, t);

            default:
                LogEx.Once("ps|mode|" + mode, $"[特效换色] 未知 startColor 模式 {mode}, 跳过");
                return 0;
        }
    }

    /// <summary>
    /// 渐变路 —— 逐【颜色键】改 RGB, 保留每个键自己的 alpha。
    /// 依据(从 interop 程序集摊开看到的, 不是猜):
    ///   Gradient.colorKeys : Il2CppStructArray&lt;GradientColorKey&gt;  get+set 都可写
    ///   GradientColorKey   : 值类型, 公开字段 color / time
    /// 原值同样只记一次(按 Gradient 原生指针), 否则换色会累积(和插值器那次同一个道理)。
    /// </summary>
    private static int TintGradient(Gradient gr, RecolorConfig.Target t)
    {
        if (gr == null) return 0;
        IntPtr key;
        try { key = gr.m_Ptr; } catch { return 0; }
        if (key == IntPtr.Zero) return 0;

        var keys = gr.colorKeys;
        if (keys == null) return 0;
        int len;
        try { len = keys.Length; } catch { return 0; }
        if (len <= 0) return 0;

        if (!_gradOrigin.TryGetValue(key, out var orig) || orig.Length != len)
        {
            orig = new Color[len];
            for (int i = 0; i < len; i++)
            {
                var k = keys[i];
                orig[i] = k.color;
            }
            if (_gradOrigin.Count >= 3000) _gradOrigin.Clear();
            _gradOrigin[key] = orig;
        }

        for (int i = 0; i < len; i++)
        {
            var k = keys[i];                  // 值类型副本
            k.color = Tint(orig[i], t);       // 从原值算, 保留该键原 alpha
            keys[i] = k;                      // 写回数组元素
        }
        try { gr.colorKeys = keys; } catch { }   // setter 存在; 写上更保险
        return len;
    }

    private static readonly Dictionary<IntPtr, Color[]> _gradOrigin = new Dictionary<IntPtr, Color[]>();

    private static readonly HashSet<int> _psLogged = new HashSet<int>();

    /// <summary>每种模式打一次实际值 —— 用来验证"哪个字段才是真颜色", 别再靠猜。</summary>
    private static void PsLogOnce(int mode, ParticleSystem.MinMaxGradient g)
    {
        if (mode != 0 && mode != 2) return;
        if (_psLogged.Count >= 6 || !_psLogged.Add(mode)) return;
        try
        {
            Plugin.Log?.LogInfo($"[特效换色:ps] 粒子 startColor mode={mode} " +
                                $"max={Reflect.Fmt(g.m_ColorMax)} min={Reflect.Fmt(g.m_ColorMin)} " +
                                $"color(只读)={Reflect.Fmt(g.color)}");
        }
        catch { }
    }

    /// <summary>把某个 GameObject(特效实例 / prefab 资产) 上能染的都染了。</summary>
    internal static int TintGameObject(GameObject go, RecolorConfig.Target t)
    {
        if (go == null) return 0;
        int ok = RecolorConfig.TintOnParticles?.Value == false ? 0 : TintParticles(go, t);
        ok += TintTintersOn(go, t);
        // ⚠ 材质/拖影路默认【关】(2026-10-04 用户实测):
        //   写 renderer.material 的 _TintColor 这类属性对加色混合的特效是错的 ——
        //   那层的颜色不是最终颜色, 写进去会变成"实心圆", 还误伤到别的特效。
        //   开关留着是方便以后换思路(比如只对特定 shader 白名单开), 默认不要打开。
        // ⚠ 材质路默认关(见 TintMaterials 的事故记录): 引擎对特效从不写材质。
        // ⚠ 材质路默认关(见 TintMaterials 的事故记录): 引擎对特效从不写材质。
        if (RecolorConfig.TintRenderers?.Value == true) ok += TintMaterials(go, t);
        // ★ 火焰路（窄口径）: 只对 shader 名含 "Flame" 的渲染器写实例材质。
        //   依据见 TintFlameMaterials 的注释 —— 尾焰的颜色 `_InnerFlameColor`/`_OutterFlameColor`
        //   既不在白名单、那颗材质也没有插值器, 只有这条路够得着。默认开, 可随时关。
        if (RecolorConfig.TintFlame?.Value != false) ok += TintFlameMaterials(go, t);
        return ok;
    }

    // ------------------------------------------------------------------ 材质 / 拖影

    /// <summary>
    /// 材质路 —— 补前两条路都够不着的载体。
    ///
    /// 为什么需要它(2026-10-04, 用户报"有一部分没被染色"):
    ///   实测点名的四类全是 hub 型 prefab(离线解开看了 es_dash_02 / es_rushup_01 /
    ///   hit_009 / es_esbullet_dasha_001): 里面的拖影 `tuowei01`、线 `line01`、刀光
    ///   `sharp001` 这些渲染器, 颜色并不总在 ParticleSystem.startColor 里 ——
    ///   它们的材质才是颜色的最终来源。而本管线原来只改
    ///   ① ParticleSystem.startColor ② MaterialTinter 的颜色插值器,
    ///   于是"钩子明明命中了、也改写了 0~N 条, 可画面就是不变"。
    ///
    /// ⚠ 两条安全约束(否则会把已经染好的东西染第二遍 ⇒ 亮度累积, 本项目栽过):
    ///   1. 同一节点上已经有 MaterialTinter / MaterialTinterProxy 的【跳过】——
    ///      那种渲染器的颜色由 tinter 驱动, 我们不该再插手。
    ///   2. 只写 `renderer.material`(Unity 会给每个渲染器建一份实例), **绝不动 sharedMaterial**,
    ///      否则会把整批共用同一材质的特效一起改掉。
    ///   3. 只写配置里列出的属性名(TintProperties), 不猜别的属性。
    /// </summary>
    /// <summary>
    /// ★ 火焰路（窄口径材质路）—— **只对 shader 名含 "Flame" 的渲染器**写实例材质。
    ///
    /// 为什么要单独开一条（2026-10-04，日志实据，不是猜的）：
    ///   `esbullet`（剑气那颗）里有 4 个材质，只有一个是火焰着色器：
    ///     `turbulence_008_k2`  shader=NOAH/Effect/Variant/Flame  用于 `feng02`
    ///   它的颜色是
    ///     `_InnerFlameColor`  = (0.823, 1.200, 2.770, 0.427)   内焰
    ///     `_OutterFlameColor` = (0.770, 1.307, 4.595, 0.141)   外焰
    ///   —— 两个都**不在** `TintProperties` 白名单里；而且这颗材质**没有插值器**
    ///   （日志 `Tinter属性=&lt;无插值器&gt;`）⇒ 粒子路和插值器路都够不着它，
    ///   这就是尾焰一直是蓝的、怎么调都染不上的原因。
    ///
    /// 为什么是"窄口径"而不是直接开 <see cref="TintMaterials"/>：
    ///   宽口径会对**每个**特效实例渲染器都建实例材质并写白名单属性 ——
    ///   2026-10-04 就是这么把"角色被染色 / 实心圆"引进来的。这里先用 shader 名把范围
    ///   卡死在火焰材质上（读 `sharedMaterial.shader.name` 只是只读判断，**不给非火焰渲染器建实例**）。
    ///
    /// 两条约束与 <see cref="TintMaterials"/> 一致，别改：
    ///   · 只写 `renderer.material`（Unity 给每个渲染器建一份实例）—— 绝不动 sharedMaterial；
    ///   · 同节点 tinter 真在驱动这些属性时让开（<see cref="TinterCovers"/>）。
    /// </summary>
    internal static int TintFlameMaterials(GameObject go, RecolorConfig.Target t)
    {
        if (go == null) return 0;
        int n = 0;
        try
        {
            var props = Cfg.List(t.Props);
            var ty = Il2CppInterop.Runtime.Il2CppType.From(typeof(Renderer));
            foreach (var c in Reflect.Items(go.GetComponentsInChildren(ty, true)))
            {
                var rt = Reflect.Cast<Renderer>(c);
                if (rt == null) continue;

                // 先用【只读】的 sharedMaterial 判断是不是火焰材质 ——
                // 这样非火焰渲染器一个实例都不会建出来。
                Material sm = null;
                string sh = null;
                try { sm = rt.sharedMaterial; } catch { }
                if (sm == null) continue;
                try { sh = sm.shader == null ? null : sm.shader.name; } catch { }
                if (string.IsNullOrEmpty(sh) || sh.IndexOf("Flame", StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (TinterCovers(rt, props)) continue;

                Material mat = null;
                try { mat = rt.material; } catch { }
                if (mat == null) continue;

                int k = TintMaterial(mat, t);
                if (k > 0)
                {
                    n += k;
                    LogEx.Once("flame|" + Reflect.Normalize(Reflect.Name(rt.gameObject)),
                        $"[特效换色:火焰] \"{Reflect.Name(rt.gameObject)}\" shader=\"{sh}\" " +
                        $"写了 {k} 条属性 -> {Cfg.ToHex(t.Color)}");
                }
            }
        }
        catch (Exception e) { LogEx.Err("TintBrush.TintFlameMaterials", e); }
        return n;
    }

    /// <summary>
    /// ★ 按【裸指针】改写一个 `InterpolatorBase[]` 里的颜色插值器。
    ///
    /// 用途：`ActorTrail._materialIpp`(0x78) 是**私有字段**，Il2CppInterop 不生成私有字段
    /// ⇒ 拿不到 MemberInfo，只能自己按偏移读（il2cpp 数组：长度在 +0x18、元素从 +0x20 起）。
    ///
    /// 为什么需要：那道蓝光 = `ActorTrailProxy._AddColor` 的原值 `(0, 0.380, 1.000)`；
    /// 皮肤有专属特效时引擎加载的是皮肤那份 prefab（`_AddColor` 是皮肤的值），
    /// 没有专属特效时就加载原色那份 ⇒ 蓝。我们改 proxy 上的源对象**画面不变**
    /// ⇒ 说明 `TrailContext.Play` 里是**按值**取走的，所以还要改 trail 自己这份。
    ///
    /// ⚠ 只写插值器的 start/end 值, **一个材质都不碰**（碰材质就是染角色本体, 踩过）。
    /// </summary>
    internal static int TintRawInterpolatorArray(IntPtr fieldAddr, RecolorConfig.Target t, string where)
    {
        const int OFF_ARR_LEN = 0x18, OFF_ARR_ITEMS = 0x20;
        if (fieldAddr == IntPtr.Zero) return 0;
        IntPtr arr;
        try { arr = System.Runtime.InteropServices.Marshal.ReadIntPtr(fieldAddr); } catch { return 0; }
        if (arr == IntPtr.Zero)
        {
            LogEx.Once("rawarr|null|" + where, $"[特效换色] {where}: 数组指针是空的(还没被赋值?)");
            return 0;
        }
        int len;
        try { len = System.Runtime.InteropServices.Marshal.ReadInt32(arr + OFF_ARR_LEN); } catch { return 0; }
        if (len <= 0 || len > 64)
        {
            LogEx.Once("rawarr|len|" + where + "|" + len, $"[特效换色] {where}: 长度 {len} 不合常理, 跳过(没瞎读)");
            return 0;
        }
        int n = 0;
        for (int i = 0; i < len; i++)
        {
            IntPtr item;
            try { item = System.Runtime.InteropServices.Marshal.ReadIntPtr(arr + OFF_ARR_ITEMS + i * IntPtr.Size); }
            catch { break; }
            if (item == IntPtr.Zero) continue;

            // ⚠⚠ `InterpolatorBase[]` 里**不是只有颜色插值器** —— 实测第 2 条就不是：
            //   把它的内存按 MaterialColorInterpolator 解释，读出来是 start=(0,1,0,0)
            //   end=(3056289000000,0,3036726000000,0)（**指针位被当成 float** 的味道）。
            //   往里写 = 把颜色值糊到别的类型对象上 ⇒ 堆损坏 / 下一次解引用就崩（2026-10-04 23:17 实测）。
            //   ⇒ 碰任何字段之前**必须先验类型**（TryCast 走 il2cpp 的类型判定，安全）。
            var typed = new Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase(item)
                            .TryCast<MaterialColorInterpolator>();
            if (typed == null)
            {
                LogEx.Once("rawarr|notcolor|" + where + "|" + Reflect.KlassName(item),
                    $"[特效换色] {where}: 第 {i} 条是 \"{Reflect.KlassName(item)}\"（不是颜色插值器）, 跳过");
                continue;
            }
            try
            {
                var mc = typed;                                 // 用指针现造包装, 不长期持有
                string prop = "";
                try { prop = mc.propName ?? ""; } catch { }
                if (!PropMatches(prop, Cfg.List(t.Props)))
                {
                    LogEx.Once("rawarr|miss|" + where + "|" + prop,
                        $"[特效换色] {where}: 第 {i} 条 propName=\"{prop}\" 不在白名单, 跳过");
                    continue;
                }
                var o = OriginOf(mc);
                mc.startValue = Tint(o.Start, t);
                mc.endValue = Tint(o.End, t);
                LogEx.Once("rawarr|ok|" + where + "|" + prop,
                    $"[特效换色] {where}: prop=\"{prop}\" 原 end={Reflect.Fmt(o.End)} -> 现在 end={Reflect.Fmt(mc.endValue)}");
                n++;
            }
            catch (Exception e) { Reflect.WarnOnce("rawarr|" + where, "改裸指针插值器", e); }
        }
        if (n == 0)
            LogEx.Once("rawarr|zero|" + where, $"[特效换色] {where}: 读了 {len} 条, 一条都没改写");
        return n;
    }

    /// <summary>
    /// ★★ 副贴图着色 —— **直接写特效自己实例材质上的 `_SubTexTintColor`**。
    ///
    /// 依据（离线拆解，权威）：消耗 MP 的冲刺特效 `es_attackAir_02` 在**原色里没有任何颜色插值器**，
    /// 皮肤换色的做法是**新增一条 `_SubTexTintColor` 插值器**
    /// （esskin_06 end=(1.227,0.735,0.826) 粉红 / esskin_10 end=(1.144,1.144,1.144)）。
    /// 插值器路只能改已存在的插值器 ⇒ 对这类特效够不着，只能照官方做法直写属性。
    ///
    /// 安全性靠三道**名字判据**（本项目在"写材质"上栽过，这三道一道都不能省）：
    ///   ① 调用方已按**点名特效**过滤（`RecolorConfig.SubTexEffects`）；
    ///   ② 材质名含 `SpritePalette` ⇒ 角色本体，跳过；
    ///   ③ shader 名不含 `NOAH/Effect/`（含 `Role`）⇒ 角色/其它系统，跳过。
    /// 另外写的是 `rt.material`（**实例**），共享材质一律不动 —— 影响面就是这一个渲染器。
    ///
    /// 幂等：`Tint()` 用"目标色的最大通道归一 + 保留原亮度" ⇒ 同一个目标色重复写是**不动点**
    /// （实测 (0.983,1.017,1.304)→红 得 (1.304,0,0)，再写一次还是 (1.304,0,0)）⇒ 可以每帧复查。
    /// </summary>
    internal static int TintSubTexMaterials(GameObject go, RecolorConfig.Target t, string prop)
    {
        if (go == null || string.IsNullOrEmpty(prop)) return 0;
        int n = 0;
        try
        {
            var ty = Il2CppInterop.Runtime.Il2CppType.From(typeof(Renderer));
            foreach (var c in Reflect.Items(go.GetComponentsInChildren(ty, true)))
            {
                var rt = Reflect.Cast<Renderer>(c);
                if (rt == null) continue;

                Material m = null;
                try { m = rt.material; } catch { continue; }        // 实例材质: 只影响这个渲染器
                if (m == null) continue;

                string mn = "", sh = "";
                try { mn = m.name ?? ""; } catch { }
                if (mn.IndexOf("SpritePalette", StringComparison.OrdinalIgnoreCase) >= 0) continue;   // ② 角色
                try { var s = m.shader; if (s != null) sh = s.name ?? ""; } catch { }
                if (sh.IndexOf("Role", StringComparison.OrdinalIgnoreCase) >= 0) continue;            // ③ 角色
                if (sh.IndexOf("NOAH/Effect/", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    LogEx.Once("subtex|skipshader|" + sh, $"[特效换色:副贴图] 跳过非特效 shader \"{sh}\"（材质 {mn}）");
                    continue;
                }

                bool has;
                try { has = m.HasProperty(prop); } catch { continue; }
                if (!has) continue;

                Color cur;
                try { cur = m.GetColor(prop); } catch { continue; }
                // ⚠⚠ 这里**必须读当前值**，不要改成"记一次原值"（2026-10-05 栽过一次，见下）。
                //
                // 我一度把它改成 `MatOrigin(m, prop)`（只记第一次看到的整条 Color）——
                // 想解决"同一实例在动画不同时刻被采样 ⇒ 结果不同"的**非确定性**。
                // 结果是**视觉回归**：阴影/残影变成**实心不透明**（用户："怎么又出现实心阴影了"）。
                //
                // 两个原因，都写在血里：
                //   ① `MatOrigin` 冻结的是**整条 Color，包括 alpha**。而这些属性的 alpha 是
                //      "出现→消失"的**包络曲线** —— 冻死它 = 特效不再淡入淡出 = 实心。
                //      本文件 Tint() 的注释自己就写着这条规矩，等于自己违反自己。
                //   ② 渐入类属性**第一次采样往往就是 `(0,0,0,0)`**（`_TintColor` 的 start 值），
                //      而 `Tint((0,0,0,0), t)` 里 lum=0 会走 `k=1` 兜底 ⇒ 结果 = **目标色满量程**
                //      ⇒ 本该"从全透明渐入"的东西，被一次性写成"满亮不透明" ⇒ 实心。
                //
                // ⇒ 结论：**这种属性的"正确值"是随时间变的，不存在一个可以缓存的原值。**
                //   非确定性那件事另找办法（新加的「叠色探针」就是去读引擎自己的记账表
                //   `ReferenceCounterMap` 拿证据，而不是靠猜）。
                Color nv = Tint(cur, t);
                try { m.SetColor(prop, nv); } catch { continue; }
                n++;
                // ⚠⚠ 去重键里必须带上【生效口径 + 目标色】(2026-10-05 修的)。
                //   原来只有 (材质, 属性) ⇒ 同一局里改了口径(比如 peak -> hue)之后，
                //   之前打过的材质**再也不会重新打**，于是日志里只剩第一阶段，
                //   第二阶段一条没有 —— 表现就是"日志看不出线索"，而实际是**根本没记**。
                //   诊断日志的第一职责是"能对账"，去重不能把阶段信息吃掉。
                LogEx.Once("subtex|" + mn + "|" + prop + "|" + Cfg.ToHex(t.Color) + "|" + (t.Mode ?? "?"),
                    $"[特效换色:副贴图|{t.Mode}] \"{mn}\" {prop}: {Reflect.Fmt(cur)} -> {Reflect.Fmt(nv)}");
            }
        }
        catch (Exception e) { LogEx.Err("TintBrush.TintSubTexMaterials", e); }
        return n;
    }

    internal static int TintMaterials(GameObject go, RecolorConfig.Target t)
    {
        if (go == null) return 0;
        int n = 0;
        try
        {
            var props = Cfg.List(t.Props);
            var ty = Il2CppInterop.Runtime.Il2CppType.From(typeof(Renderer));
            var comps = go.GetComponentsInChildren(ty, true);
            foreach (var c in Reflect.Items(comps))
            {
                var rt = Reflect.Cast<Renderer>(c);
                if (rt == null) continue;
                // 智能跳过: 同节点的 tinter **真的在驱动**这些属性时才让开(它每帧会覆盖我们);
                // 有 tinter 但没覆盖到(比如 MaterialMask 只管一部分材质) ⇒ 我们要补上。
                // 实测(2026-10-04): 剑气那条蓝拖尾的材质 `particles_006_a`
                // (_TintColor=(4.52,5.99,5.99,1)) 就是这么漏掉的 —— 节点上有 tinter,
                // 可它没管这块材质 ⇒ 旧规则"有 tinter 就跳过"把它整块跳过了。
                if (TinterCovers(rt, props)) continue;

                // 拖影自带 start/end 颜色 —— 先按原 alpha 染, 再走材质
                if (rt.TryCast<TrailRenderer>() is TrailRenderer trail) TintTrail(trail, t);

                Material mat = null;
                string mn = "?";
                try { mat = rt.material; } catch { }    // 约束 2: 实例材质(Unity 自动复制 ⇒ 不污染共享资产)
                if (mat == null) continue;
                try { mn = mat.name ?? "?"; } catch { }
                int k2 = TintMaterial(mat, t);
                if (k2 > 0 && _matInstSeen.Add(Reflect.Normalize(mn)) && _matInstSeen.Count <= 60)
                    Plugin.Log?.LogInfo($"[特效换色:材质实例] \"{mn}\" 写了 {k2} 条属性 " +
                                        $"(首属性原值 {Reflect.Fmt(MatOrigin(mat, props.Length > 0 ? props[0] : "_TintColor"))})");
                n += k2;
            }
        }
        catch (Exception e) { LogEx.Err("TintBrush.TintMaterials", e); }
        return n;
    }

    // ================================================================== ★ 私有副本

    /// <summary>
    /// 把加载到的特效 prefab 复制成【我们自己的副本】, 之后所有染色只碰副本 ——
    /// 游戏自己的换色资产(含共享材质)一个字节都不动。
    ///
    /// 为什么必须复制（用户 2026-10-04 的设计要求）:
    ///   · **材质是全局共享的** —— 改一份 `Effect/Common/...` 的材质, 所有用到它的特效、
    ///     包括玩家真去装备的那个换色皮肤, 都会跟着变 ⇒ 等于污染了游戏自己的资产。
    ///   · **插值器对象**同理: `Instantiate` 不会深拷贝 SerializeReference 数组,
    ///     克隆出来的 prefab 里那份 `MaterialInterpolators` 还指着**原对象** ⇒
    ///     就地改写等于改到原资产上。
    /// 所以做三层: ① 克隆 GameObject ② 每个渲染器的材质 `new Material(原)` 换成副本
    /// ③ 每个插值器数组新建一份、元素逐个新建并拷贝字段。
    ///
    /// ⚠ 副本只当"模板"用(SetActive(false) + DontDestroyOnLoad), 游戏之后从它身上 Instantiate 实例。
    /// </summary>
    internal static GameObject ClonePrivate(GameObject src)
    {
        if (src == null) return null;
        GameObject clone = null;
        try
        {
            clone = UnityEngine.Object.Instantiate(src);
            if (clone == null) return null;
            clone.name = src.name;                       // 去掉 "(Clone)" —— 不破坏按名字的判断
            try { UnityEngine.Object.DontDestroyOnLoad(clone); } catch { }
            try { clone.SetActive(false); } catch { }

            int mats = 0, interps = 0;

            // ② 材质副本
            var rty = Il2CppInterop.Runtime.Il2CppType.From(typeof(Renderer));
            foreach (var c in Reflect.Items(clone.GetComponentsInChildren(rty, true)))
            {
                var rt = Reflect.Cast<Renderer>(c);
                if (rt == null) continue;
                try
                {
                    var m = rt.sharedMaterial;
                    if (m == null) continue;
                    var copy = new Material(m);
                    try { copy.name = m.name; } catch { }
                    rt.sharedMaterial = copy;
                    mats++;
                }
                catch { }
            }

            // ③ 插值器副本(名字带 interpolator 的字段/属性)
            var cty = Il2CppInterop.Runtime.Il2CppType.From(typeof(Component));
            foreach (var c in Reflect.Items(clone.GetComponentsInChildren(cty, true)))
            {
                var comp = Reflect.Cast<Component>(c);
                if (comp == null) continue;
                Type ct = comp.GetType();
                foreach (var mi in MembersLike(ct))
                {
                    var arr = ReadMember(comp, mi);
                    if (arr == null) continue;
                    var list = new List<object>();
                    foreach (var it in Reflect.Items(arr)) list.Add(it);
                    if (list.Count == 0) continue;

                    var copied = new List<object>();
                    bool ok = true;
                    foreach (var it in list)
                    {
                        var cp = ShallowCopy(it);
                        if (cp == null) { ok = false; break; }
                        copied.Add(cp);
                    }
                    if (!ok || copied.Count != list.Count) continue;      // 复制不成就别动 —— 宁可不染也不污染

                    var ne = BuildArrayOf(mi, copied);
                    if (ne == null) continue;
                    try { WriteMember(comp, mi, ne); interps += copied.Count; } catch { }
                }
            }

            LogEx.Once("recolor|clone|" + Reflect.Normalize(src.name),
                       $"[特效换色] 私有副本 \"{src.name}\": 材质 {mats} 份, 插值器 {interps} 个 (原资产不动)");
            return clone;
        }
        catch (Exception e)
        {
            LogEx.Err("TintBrush.ClonePrivate", e);
            return clone ?? src;      // 复制失败就退回原资产(功能优先, 但会在日志里留痕)
        }
    }

    /// <summary>名字里带 interpolator 的字段/属性。</summary>
    private static IEnumerable<System.Reflection.MemberInfo> MembersLike(Type t)
    {
        foreach (var f in t.GetFields(Reflect.All))
            if (f.Name.IndexOf("interpolator", StringComparison.OrdinalIgnoreCase) >= 0) yield return f;
        foreach (var p in t.GetProperties(Reflect.All))
            if (p.CanRead && p.CanWrite &&
                p.Name.IndexOf("interpolator", StringComparison.OrdinalIgnoreCase) >= 0) yield return p;
    }

    /// <summary>新建一个同类型对象并浅拷贝可写成员 —— 用来把插值器从原资产上摘下来。</summary>
    private static object ShallowCopy(object src)
    {
        if (src == null) return null;
        try
        {
            Type t = src.GetType();
            var ctor = t.GetConstructor(Type.EmptyTypes);
            if (ctor == null) return null;                 // 没有无参构造就别冒险
            object dst = ctor.Invoke(null);
            if (dst == null) return null;
            foreach (var f in t.GetFields(Reflect.All))
            {
                if (f.IsStatic) continue;
                try { f.SetValue(dst, f.GetValue(src)); } catch { }
            }
            foreach (var p in t.GetProperties(Reflect.All))
            {
                if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                try { p.SetValue(dst, p.GetValue(src)); } catch { }
            }
            return dst;
        }
        catch { return null; }
    }

    private static object BuildArrayOf(System.Reflection.MemberInfo mi, List<object> items)
    {
        Type et = null;
        if (mi is System.Reflection.FieldInfo fi) et = fi.FieldType.GetElementType();
        else if (mi is System.Reflection.PropertyInfo pi) et = pi.PropertyType.GetElementType();
        if (et == null) return null;
        try
        {
            var arr = Array.CreateInstance(et, items.Count);
            for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
            return arr;
        }
        catch { return null; }
    }

    private static void WriteMember(object obj, System.Reflection.MemberInfo mi, object val)
    {
        if (mi is System.Reflection.FieldInfo f) f.SetValue(obj, val);
        else if (mi is System.Reflection.PropertyInfo p) p.SetValue(obj, val);
    }

    /// <summary>
    /// ★ 材质路(2026-10-04) —— 直接改【材质资产】上的白名单属性。
    ///
    /// 为什么必须走这条（用户原话："我们已经彻底拆解了换色特效的应用方式，直接基于这个路线重做"）:
    ///   引擎自己的颜色链路是 `SkinColor.prop` → 颜色表 → **写进材质的 shader 属性**。
    ///   而特效这边，颜色大量烘在**共享材质**上（`Effect/Common`、`Effect/Material` 那些 bundle），
    ///   实例侧的 tinter 全是空数组 —— 这就是"tinter 明明改写了 3~8 处、画面纹丝不动"的原因
    ///   （剑气拖尾就是这一类）。
    ///
    /// 写什么值: 和插值器每帧写的是**同一个函数** `Tint(原值)`, 所以两者不打架;
    ///   原值按 (材质指针, 属性名) 只记一次 ⇒ 换色可逆、可重复(不会亮度累积)。
    /// 语义与"实心圆"事故的区别: 那时是**凭空塞一个不透明亮色**;
    ///   这里是**照原值算出来的**(保留原 alpha、沿用原量级), 和引擎自己的插值器行为一致。
    /// </summary>
    internal static int TintMaterial(Material mat, RecolorConfig.Target t)
    {
        if (mat == null) return 0;
        int n = 0;
        try
        {
            foreach (var prop in Cfg.List(t.Props))
            {
                if (prop.Length == 0) continue;
                bool has = false;
                try { has = mat.HasProperty(prop); } catch { }
                if (!has) continue;
                var o = MatOrigin(mat, prop);
                try { mat.SetColor(prop, Tint(o, t)); n++; }
                catch (Exception e) { Reflect.WarnOnce("mat|" + prop, "写材质", e); }
            }
        }
        catch (Exception e) { LogEx.Err("TintBrush.TintMaterial", e); }
        return n;
    }

    /// <summary>
    /// 拖影路: 把拖影自己的渲染器材质**先复制一份再染** ——
    /// 不碰角色的材质(免得把角色本体也染了), 也不碰共享资产。
    /// `ActorTrail` 的运行时机材质的原值 = 角色的材质, 所以这一步也顺带把"复制"做了。
    /// </summary>
    /// <summary>副本专用: 直接写渲染器的 sharedMaterial —— 那是 ClonePrivate 复制出来的副本, 不会外溢。</summary>
    internal static int TintMaterialsOnClone(GameObject go, RecolorConfig.Target t)
    {
        if (go == null) return 0;
        int n = 0;
        try
        {
            var ty = Il2CppInterop.Runtime.Il2CppType.From(typeof(Renderer));
            foreach (var c in Reflect.Items(go.GetComponentsInChildren(ty, true)))
            {
                var rt = Reflect.Cast<Renderer>(c);
                if (rt == null) continue;
                Material m = null;
                try { m = rt.sharedMaterial; } catch { }
                if (m == null) continue;
                n += TintMaterial(m, t);
            }
        }
        catch (Exception e) { LogEx.Err("TintBrush.TintMaterialsOnClone", e); }
        return n;
    }

    private static void TintTrail(TrailRenderer tr, RecolorConfig.Target t)
    {
        try
        {
            var s = tr.startColor;
            var e2 = tr.endColor;
            tr.startColor = Tint(s, t);      // Tint 保留原 alpha
            tr.endColor = Tint(e2, t);
        }
        catch (Exception e) { Reflect.WarnOnce("trail", "写拖影", e); }
    }

    private static readonly HashSet<string> _matInstSeen = new HashSet<string>();

    /// <summary>
    /// 同节点上的 tinter 是否**真的在驱动**白名单里的属性。
    /// 是 ⇒ 让开(它每帧会把颜色写回去, 我们插手没意义还可能双重相乘);
    /// 有 tinter 但它的插值器没覆盖这些属性 ⇒ 返回 false, 由材质路补上。
    /// </summary>
    private static bool TinterCovers(Renderer rt, string[] props)
    {
        try
        {
            var go = rt.gameObject;
            var cty = Il2CppInterop.Runtime.Il2CppType.From(typeof(Component));
            foreach (var c in Reflect.Items(go.GetComponents(cty)))
            {
                var comp = Reflect.Cast<Component>(c);
                if (comp == null) continue;
                Type ct = comp.GetType();
                bool looksTinter = ct.Name.IndexOf("Tinter", StringComparison.OrdinalIgnoreCase) >= 0
                                || ct.Name.IndexOf("Trail", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!looksTinter) continue;

                foreach (var mi in MembersLike(ct))
                {
                    foreach (var it in Reflect.Items(ReadMember(comp, mi)))
                    {
                        var mc = Reflect.Cast<MaterialColorInterpolator>(it);
                        if (mc == null) continue;
                        string p = "";
                        try { p = mc.propName ?? ""; } catch { }
                        if (PropMatches(p, props)) return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }

    private static bool HasTinterOn(Renderer rt)
    {
        try
        {
            var go = rt.gameObject;
            var t1 = Il2CppInterop.Runtime.Il2CppType.From(typeof(NOAH.VFX.MaterialTinter));
            var t2 = Il2CppInterop.Runtime.Il2CppType.From(typeof(NOAH.VFX.MaterialTinterProxy));
            if (go.GetComponents(t1).Count > 0) return true;   // ⚠ 非泛型返回, 用 Count 不要用 as IList
            if (go.GetComponents(t2).Count > 0) return true;
        }
        catch { }
        return false;
    }

    /// <summary>材质属性的原值表。key = 材质指针 + 属性名的哈希 —— 与插值器那份分开存, 不串。</summary>
    private static readonly Dictionary<long, Color> _matOrigin = new Dictionary<long, Color>();
    private const int MatOriginCap = 4000;

    private static Color MatOrigin(Material mat, string prop)
    {
        long key;
        try { key = (long)mat.Pointer + ((long)prop.GetHashCode() << 40); }
        catch { return Color.white; }

        if (_matOrigin.TryGetValue(key, out var c)) return c;
        try { c = mat.GetColor(prop); } catch { c = Color.white; }
        if (_matOrigin.Count >= MatOriginCap)
        {
            _matOrigin.Clear();
            LogEx.Once("recolor|matorigincap", $"[特效换色] 材质原值表达到 {MatOriginCap} 条, 已清空重建(长局正常现象)");
        }
        _matOrigin[key] = c;
        return c;
    }

    /// <summary>
    /// Tinter 里到底写的是什么属性名 —— "改写 0 条插值器"那个谜就靠这行解。
    /// 拖影/剑气这类特效的 Tinter 往往【只有贴图/其它类型的插值器, 没有颜色插值器】,
    /// 或者颜色插值器的 propName 不在 TintProperties 里 —— 前者再多挂钩子也没用,
    /// 后者只要把名字加进配置就行。两者必须能分辨, 否则又是一轮瞎猜。
    /// </summary>
    private static string TinterProps(GameObject go)
    {
        try
        {
            var names = new List<string>();
            TintOneType<NOAH.VFX.MaterialTinter>(go, default(RecolorConfig.Target), t =>
            {
                var arr = Reflect.Read(t, InterpolatorsOf(t.GetType()));
                foreach (var item in Reflect.Items(arr))
                {
                    if (item == null) continue;
                    var mc = Reflect.Cast<MaterialColorInterpolator>(item);
                    if (mc == null) { if (names.Count < 8) names.Add("<非颜色>"); continue; }
                    string p = ""; try { p = mc.propName ?? ""; } catch { }
                    if (names.Count < 8) names.Add(p.Length == 0 ? "<空名>" : p);
                }
                return 0;
            });
            // VFXEffectBase 子类那份也一起报(拖影的颜色就在那儿)
            var tb = Il2CppInterop.Runtime.Il2CppType.From(typeof(NOAH.VFX.VFXEffectBase));
            foreach (var c in Reflect.Items(go.GetComponentsInChildren(tb, true)))
            {
                var b = Reflect.Cast<NOAH.VFX.VFXEffectBase>(c);
                if (b == null) continue;
                var f = Reflect.Member(b.GetType(), "MaterialInterpolators", Reflect.All);
                if (f == null) continue;
                foreach (var item in Reflect.Items(Reflect.Read(b, f)))
                {
                    if (item == null) continue;
                    var mc = Reflect.Cast<MaterialColorInterpolator>(item);
                    string p = "";
                    if (mc != null) { try { p = mc.propName ?? ""; } catch { } }
                    else p = "<非颜色>";
                    if (names.Count < 12) names.Add(b.GetType().Name + ":" + (p.Length == 0 ? "<空名>" : p));
                }
            }
            return names.Count == 0 ? "  Tinter属性=<无插值器>" : "  Tinter属性=" + string.Join("/", names.ToArray());
        }
        catch (Exception e) { return "  Tinter属性读取失败:" + e.Message; }
    }

    /// <summary>
    /// ★ 材质探针 —— "tinter 都改写了、画面就是不变"时, 答案在这行。
    ///
    /// 背景（2026-10-04）: `esbullet`(剑气) 的 tinter 已经写入 3~8 处, 可拖尾颜色依旧 ——
    /// 说明那道颜色不在 tinter/粒子/拖影上, 而在**材质**上（那批材质在 `effect/common`、
    /// `effect/material` 这些**共享 bundle** 里, 我们的 asset 钩子从来碰不到它们）。
    /// 这行打出每个渲染器的 材质名 / shader名 / 白名单属性的当前值,
    /// 用来回答两件事: ① 拖尾到底用哪个属性 ② 那个属性的原值是多少(决定该怎么写才不像"实心圆")。
    /// </summary>
    internal static void ProbeMaterials(GameObject go, RecolorConfig.Target t)
    {
        if (go == null) return;
        try
        {
            var props = Cfg.List(t.Props);
            var ty = Il2CppInterop.Runtime.Il2CppType.From(typeof(Renderer));
            foreach (var c in Reflect.Items(go.GetComponentsInChildren(ty, true)))
            {
                var rt = Reflect.Cast<Renderer>(c);
                if (rt == null) continue;
                Material m = null;
                string mn = "?", sn = "?";
                try { m = rt.sharedMaterial; } catch { }
                if (m == null) { try { m = rt.material; } catch { } }
                if (m == null) continue;
                try { mn = m.name ?? "?"; } catch { }
                try { sn = m.shader?.name ?? "?"; } catch { }
                if (!_matProbed.Add(mn + "|" + sn)) continue;      // 每个(材质,shader)只打一次
                if (_matProbed.Count > 60) return;

                // ★ 打【全部候选颜色属性】, 不再只打白名单 ——
                //   第一次就是这样漏掉关键信息的: `_TintColor` 的 RGB 已经是红的, 可拖尾还是蓝的,
                //   说明那个 shader 多半只用 `_TintColor.a`(透明度), 颜色其实来自**贴图**或**别的属性**。
                var vals = new List<string>();
                foreach (var p in ProbeProps)
                {
                    try
                    {
                        if (!m.HasProperty(p)) continue;
                        var col = m.GetColor(p);
                        vals.Add($"{p}={Reflect.Fmt(col)}");
                    }
                    catch { }
                }
                try
                {
                    var tex = m.mainTexture;
                    if (tex != null) vals.Add($"mainTexture=\"{tex.name}\"");
                }
                catch { }
                string shProps = "";
                try { shProps = $" shaderProps={m.shader?.GetPropertyCount()}"; } catch { }
                Plugin.Log?.LogInfo($"[特效换色:材质探针] \"{Reflect.Name(go)}\" 渲染器={rt.GetType().Name} " +
                                    $"材质=\"{mn}\" shader=\"{sn}\"" +
                                    (vals.Count > 0 ? "  白名单属性: " + string.Join("  ", vals.ToArray())
                                                    : "  (白名单属性一个都没有)"));
            }
        }
        catch (Exception e) { LogEx.Err("TintBrush.ProbeMaterials", e); }
    }

    private static readonly HashSet<string> _matProbed = new HashSet<string>();

    /// <summary>探针候选属性名 —— 白名单 + 常见着色器颜色属性, 用来一次性看清"颜色到底在哪个属性上"。</summary>
    private static readonly string[] ProbeProps = {
        "_TintColor", "_Color", "_MainColor", "_BaseColor", "_EmissionColor", "_EmissiveColor",
        "_SubTexTintColor", "_DecoTexTintColor", "_HighlightColor", "_BrightColor", "_AmbientColor",
        "_DissolveColor", "_RemapColorFrom", "_RemapColorTo", "_AlphaColor", "_GlowColor",
        "_Emission", "_EmissionX", "_EmissionY", "_EmissionZ", "_EmissionA",
        "_Skin1", "_Skin2", "_Skin3", "_Skin4", "_OutlineColor", "_ShadowColor",
    };

    /// <summary>载体清单 —— "这个特效到底有什么可染的"。用户报"没变色"时, 先看这行。</summary>
    internal static string CarrierReport(GameObject go)
    {
        if (go == null) return "go=null";
        try
        {
            int ps = 0, tinter = 0, rend = 0, trail = 0;
            var tps = Il2CppInterop.Runtime.Il2CppType.From(typeof(ParticleSystem));
            foreach (var c in Reflect.Items(go.GetComponentsInChildren(tps, true))) if (Reflect.Cast<ParticleSystem>(c) != null) ps++;
            var trd = Il2CppInterop.Runtime.Il2CppType.From(typeof(Renderer));
            foreach (var c in Reflect.Items(go.GetComponentsInChildren(trd, true)))
            {
                var r = Reflect.Cast<Renderer>(c);
                if (r == null) continue;
                rend++;
                if (r.TryCast<TrailRenderer>() != null) trail++;
            }
            var tt = Il2CppInterop.Runtime.Il2CppType.From(typeof(NOAH.VFX.MaterialTinter));
            tinter += go.GetComponentsInChildren(tt, true).Count;
            var tp = Il2CppInterop.Runtime.Il2CppType.From(typeof(NOAH.VFX.MaterialTinterProxy));
            tinter += go.GetComponentsInChildren(tp, true).Count;
            // VFXEffectBase 子类(拖影 ActorTrailProxy 等)也各带一份 MaterialInterpolators —— 单独报出来,
            // 否则"拖影没变色"时会误以为它没有颜色载体。
            int fx = 0;
            var tb = Il2CppInterop.Runtime.Il2CppType.From(typeof(NOAH.VFX.VFXEffectBase));
            foreach (var c in Reflect.Items(go.GetComponentsInChildren(tb, true)))
                if (Reflect.Cast<NOAH.VFX.VFXEffectBase>(c) != null) fx++;
            return $"粒子={ps} 渲染器={rend}(拖影={trail}) Tinter={tinter} 效果组件={fx}{TinterProps(go)}";
        }
        catch (Exception e) { return "清单读取失败:" + e.Message; }
    }

    /// <summary>
    /// 找到 GameObject 下所有 MaterialTinter / MaterialTinterProxy, 改写它们的颜色插值器。
    ///
    /// ⚠ 两个都必须要: 粒子的颜色烘在 ParticleSystem.startColor 里,
    ///   而 Mesh 型特效(es_AH_02 那种)只有 MaterialTinter,
    ///   粒子型特效则【两层都有】—— 只改 startColor 就会出现"一半变色"。
    /// </summary>
    internal static int TintTintersOn(GameObject go, RecolorConfig.Target t)
    {
        if (go == null) return 0;
        int n = 0;
        n += TintOneType<NOAH.VFX.MaterialTinter>(go, t, tinter =>
        {
            int k = RewriteArray(Reflect.Read(tinter, InterpolatorsOf(tinter.GetType())), t, "MaterialTinter");
            k += RewriteSets(tinter, t);
            return k;
        });
        n += TintOneType<NOAH.VFX.MaterialTinterProxy>(go, t, proxy =>
            RewriteArray(Reflect.Read(proxy, ProxyInterpolators), t, "MaterialTinterProxy"));
        // ★★ ActorTrailProxy —— 加拉哈德 ↓冲刺那道残影的唯一载体（`es_dodge_01/02` 的 `Other`）。
        //    颜色在它的 `MaterialInterpolators` 里, propName = `_AddColor`
        //    （原色 (0,0.380,1.000) 加色蓝；皮肤各自不同值 ⇒ 引擎本来就是靠这个 prefab 换色的）。
        //    形态和 MaterialTinter 一模一样: VFXEffectBase + MaterialInterpolators。
        n += TintOneType<NOAH.VFX.ActorTrailProxy>(go, t, px =>
        {
            var arr = Reflect.Read(px, InterpolatorsOf(px.GetType()));
            int k = RewriteArray(arr, t, "ActorTrailProxy");
            // ★ 这条路上全表只有 6 个载体, 所以【不打条数上限】地把写完之后的实值打出来 ——
            //   光看"写了 N 处"证明不了画面会变(play 时可能已被拷走), 得看值本身。
            try
            {
                foreach (var item in Reflect.Items(arr))
                {
                    var mc = Reflect.Cast<NOAH.VFXInterpolator.MaterialColorInterpolator>(item);
                    if (mc == null) continue;
                    string pn = "";
                    try { pn = mc.propName ?? ""; } catch { }
                    LogEx.Once("trailpx|" + Reflect.Normalize(Reflect.Name(go)) + "|" + pn,
                               $"[特效换色:残影层] \"{Reflect.Name(go)}\" prop=\"{pn}\" " +
                               $"写完的 start={Reflect.Fmt(mc.startValue)} end={Reflect.Fmt(mc.endValue)}");
                }
            }
            catch (Exception e) { LogEx.Err("TintBrush.TintTintersOn.ActorTrailProxy", e); }
            return k;
        });
        n += TintAllInterpolators(go, t);   // ★ 通用扫描(注意: 目前是死的, 见下)
        return n;
    }

    // ================================================================== ★ 通用插值器扫描

    /// <summary>
    /// ★★ 通用扫描 —— 引擎里的"着色组件"远不止两种, 别再逐个点名。
    ///
    /// 实测清单（`dump.cs`, 2026-10-04。名字和字段都写在这儿, 免得下次又漏）:
    ///   MaterialTinter        : VFXEffectBase, ITagedTinter   MaterialInterpolators    : InterpolatorBase[]
    ///   MaterialTinterProxy   : VFXEffectBase                 MaterialInterpolators    : InterpolatorBase[]
    ///   ActorTrailProxy       : VFXEffectBase                 MaterialInterpolators    : InterpolatorBase[]（常是空数组）
    ///   ActorTrail            : VFXEffectBase, ITagedTinter   **`_materialIpp`**       : InterpolatorBase[]   ← 名字不同!
    ///   ParticleTinter        : VFXEffectBase, ITagedTinter   ParticleInterpolators    : ParticleInterpolatorController[]  ← 类型也不同!
    ///   TextureSheetAnimation : VFXEffectBase, ITagedTinter
    ///
    /// 本管线原来只认前两种 ⇒ 冲刺拖影(`ActorTrail`)、多段跳的环/剑气拖影(`ParticleTinter`)
    /// 全都够不着 —— 日志里表现成"访问到了、可改写 0 处"。
    ///
    /// 规则: 扫每个组件身上【名字里带 interpolator 的字段/属性】;
    ///   · 元素是 MaterialColorInterpolator -> 走老规矩(原值表 + propName 白名单)
    ///   · 是别的容器/插值器 -> 递归: 把里面【名字带 color】的 Color / MinMaxGradient 成员染掉,
    ///     再顺着带 interpolator 的成员往下一层(如 ParticleInterpolatorController.m_interpolator)
    /// </summary>
    internal static int TintAllInterpolators(GameObject go, RecolorConfig.Target t)
    {
        if (go == null) return 0;
        int n = 0;
        try
        {
            // ⚠⚠ 这条路【从来没工作过】，别再信它（2026-10-04 实测 + 日志）：
            //   `GetComponentsInChildren(typeof(Component))` 回来的是【声明类型】的包装，
            //   `comp.GetType()` 恒等于 Component ⇒ 下面 `GetFields("interpolator")` 一个都找不到
            //   ⇒ 整个通用扫描静默返回 0（表现就是"访问到了、可改写 0 处"）。
            //   和 FxAnatomy 踩的是同一个坑（那里也记了一笔）。
            //   要真修就得改成【按已知类型逐个 TryCast 探】(参照 TintTintersOn 的 TintOneType<T>)，
            //   但那会让一批从没被染过的东西突然开始被染 —— 属于"一次只动一个变量"之外的改动，
            //   留到确实需要时再动。这里只留一条痕迹，免得又被当成"活的"。
            LogEx.Once("recolor|sweep|dead",
                       "[特效换色] 通用扫描(TintAllInterpolators)当前是死代码: interop 只给 Component 包装, " +
                       "GetType() 拿不到真实类型 —— 它一直静默返回 0。要覆盖新载体请显式加 TintOneType<T>。");
            var ty = Il2CppInterop.Runtime.Il2CppType.From(typeof(Component));
            foreach (var c in Reflect.Items(go.GetComponentsInChildren(ty, true)))
            {
                var comp = Reflect.Cast<Component>(c);
                if (comp == null) continue;
                Type ct = comp.GetType();
                foreach (var fi in ct.GetFields(Reflect.All))
                {
                    if (fi.Name.IndexOf("interpolator", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    n += Sweep(ReadMember(comp, fi), t, ct.Name, 0);
                }
                foreach (var pi in ct.GetProperties(Reflect.All))
                {
                    if (!pi.CanRead || pi.Name.IndexOf("interpolator", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    n += Sweep(ReadMember(comp, pi), t, ct.Name, 0);
                }
            }
        }
        catch (Exception e) { LogEx.Err("TintBrush.TintAllInterpolators", e); }
        return n;
    }

    private static object ReadMember(object obj, System.Reflection.MemberInfo mi)
    {
        try
        {
            switch (mi)
            {
                case System.Reflection.FieldInfo f: return f.GetValue(obj);
                case System.Reflection.PropertyInfo p: return p.CanRead ? p.GetValue(obj) : null;
            }
        }
        catch { }
        return null;
    }

    private static int Sweep(object v, RecolorConfig.Target t, string owner, int depth)
    {
        if (v == null || depth > 3) return 0;
        int n = 0; bool any = false;
        foreach (var it in Reflect.Items(v)) { any = true; n += SweepOne(it, t, owner, depth); }
        return any ? n : SweepOne(v, t, owner, depth);
    }

    private static int SweepOne(object it, RecolorConfig.Target t, string owner, int depth)
    {
        if (it == null) return 0;
        Type ty = it.GetType();

        // ① 既有的颜色插值器: 原值表 + 属性白名单
        var mc = Reflect.Cast<MaterialColorInterpolator>(it);
        if (mc != null)
        {
            string prop = "";
            try { prop = mc.propName ?? ""; } catch { }
            if (!PropMatches(prop, Cfg.List(t.Props))) return 0;
            var o = OriginOf(mc);
            try
            {
                mc.startValue = Tint(o.Start, t);
                mc.endValue = Tint(o.End, t);
                return 1;
            }
            catch { return 0; }
        }

        int n = 0;
        // ② 名字带 color 的成员 —— 粒子插值器那类就走这条
        if (RecolorConfig.TintOnInterpolators?.Value == false) return 0;   // 同受二分开关管

        foreach (var pi in ty.GetProperties(Reflect.All))
        {
            if (!pi.CanRead || !pi.CanWrite) continue;
            if (pi.Name.IndexOf("color", StringComparison.OrdinalIgnoreCase) < 0) continue;
            try
            {
                if (pi.PropertyType == typeof(Color))
                {
                    pi.SetValue(it, Tint((Color)pi.GetValue(it), t));
                    LogEx.Once("recolor|sweep|" + ty.Name + "|" + pi.Name,
                               $"[特效换色] 通用扫描: {ty.Name}.{pi.Name} 已染");
                    n++;
                }
                else if (pi.PropertyType.Name.IndexOf("MinMaxGradient", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var g = pi.GetValue(it);
                    var gm = Reflect.Cast<ParticleSystem.MinMaxGradient>(g);
                    if (gm != null) n += TintMmGradient(gm, t);
                }
            }
            catch { }
        }
        foreach (var fi in ty.GetFields(Reflect.All))
        {
            if (fi.FieldType != typeof(Color)) continue;
            if (fi.Name.IndexOf("color", StringComparison.OrdinalIgnoreCase) < 0) continue;
            try
            {
                fi.SetValue(it, Tint((Color)fi.GetValue(it), t));
                LogEx.Once("recolor|sweepf|" + ty.Name + "|" + fi.Name,
                           $"[特效换色] 通用扫描: {ty.Name}.{fi.Name} 已染");
                n++;
            }
            catch { }
        }
        // ③ 容器里还挂着底层插值器(ParticleInterpolatorController.m_interpolator / _materialIpp …)
        foreach (var fi in ty.GetFields(Reflect.All))
            if (fi.Name.IndexOf("interpolator", StringComparison.OrdinalIgnoreCase) >= 0)
                n += Sweep(ReadMember(it, fi), t, ty.Name, depth + 1);
        foreach (var pi in ty.GetProperties(Reflect.All))
            if (pi.CanRead && pi.Name.IndexOf("interpolator", StringComparison.OrdinalIgnoreCase) >= 0)
                n += Sweep(ReadMember(it, pi), t, ty.Name, depth + 1);
        return n;
    }

    /// <summary>
    /// ★ 拖影路 —— 任何 `VFXEffectBase` 子类都可能自带一份 `MaterialInterpolators`。
    ///
    /// 事故记录（2026-10-04，用户报"冲刺拖影"始终不生效）:
    ///   冲刺拖影的正主是 `Effect/Prefab/Common/silhouette_601`，里面挂的是
    ///   **`ActorTrailProxy : VFXEffectBase`**（复制玩家形象的拖影）。
    ///   它的颜色在 **自己那份 `MaterialInterpolators`(0x60)** 里 ——
    ///   而本管线原来只认 `MaterialTinter` / `MaterialTinterProxy` 两种组件的同名字段，
    ///   于是日志里这个特效**明明被访问到了**（hub 激活 + 普查都到了），却永远 `改写 0 处`。
    ///
    /// 做法: 按字段名 `MaterialInterpolators` 在【任意 VFXEffectBase 子类】上找一遍并改写 ——
    ///   字段名在这个引擎里是一致的（MaterialTinter 与 ActorTrailProxy 同名），
    ///   所以不用给每种效果各写一条路。
    /// </summary>
    private static int TintEffectBases(GameObject go, RecolorConfig.Target t)
    {
        if (go == null) return 0;
        int n = 0;
        try
        {
            var ty = Il2CppInterop.Runtime.Il2CppType.From(typeof(NOAH.VFX.VFXEffectBase));
            foreach (var c in Reflect.Items(go.GetComponentsInChildren(ty, true)))
            {
                var b = Reflect.Cast<NOAH.VFX.VFXEffectBase>(c);
                if (b == null) continue;
                var f = Reflect.Member(b.GetType(), "MaterialInterpolators", Reflect.All);
                if (f == null) continue;
                int k = RewriteArray(Reflect.Read(b, f), t, "VFXEffectBase/" + b.GetType().Name);
                if (k > 0)
                    LogEx.Once("recolor|fxbase|" + b.GetType().Name,
                               $"[特效换色] {b.GetType().Name} 的 MaterialInterpolators 改写 {k} 条");
                n += k;
            }
        }
        catch (Exception e) { LogEx.Err("TintBrush.TintEffectBases", e); }
        return n;
    }

    private static int TintOneType<T>(GameObject go, RecolorConfig.Target t, Func<T, int> apply)
        where T : Il2CppObjectBase
    {
        int n = 0;
        try
        {
            var ty = Il2CppInterop.Runtime.Il2CppType.From(typeof(T));
            var comps = go.GetComponentsInChildren(ty, true);
            int seen = 0;
            foreach (var c in Reflect.Items(comps))
            {
                var comp = Reflect.Cast<T>(c);       // 同上: 基类包装必须 TryCast
                if (comp == null) continue;
                seen++;
                n += apply(comp);
            }
            if (seen > 0)
                LogEx.Once("recolor|found|" + typeof(T).Name + "|" + Reflect.Normalize(go.name),
                           $"[特效换色] \"{go.name}\" 上有 {seen} 个 {typeof(T).Name}");
        }
        catch (Exception e) { LogEx.Err("TintBrush.TintOneType/" + typeof(T).Name, e); }
        return n;
    }

    private static System.Reflection.MemberInfo _interpField;
    private static System.Reflection.MemberInfo ProxyInterpolators
        => _proxyInterp ?? (_proxyInterp = Reflect.Member(typeof(NOAH.VFX.MaterialTinterProxy), "MaterialInterpolators", Reflect.All));
    private static System.Reflection.MemberInfo _proxyInterp;
    private static System.Reflection.MemberInfo _setsField;

    private static System.Reflection.MemberInfo InterpolatorsOf(Type tinterType)
    {
        // ⚠⚠ 原来写的是 `_interpField ?? (_interpField = Reflect.Member(tinterType, ...))` ——
        //   **缓存一次之后就忽略传入的类型**：第一次被 MaterialTinter 调用后，后面传
        //   ActorTrailProxy 进来也只会拿到 MaterialTinter 的那个 FieldInfo，
        //   于是**按错误的偏移**去读另一个类的对象 ⇒ 读出来的不是插值器数组，
        //   表现成"载体认到了、可改写 0 处"，日志只有一句没头没脑的
        //   `跳过非颜色插值器 NOAH.VFXInterpolator.InterpolatorBase`（那是包装类型名，不是真实类型）。
        //   改成【按类型分别缓存】。
        if (_interpCache.TryGetValue(tinterType, out var cached)) return cached;
        var m = Reflect.Member(tinterType, "MaterialInterpolators", Reflect.All);
        _interpCache[tinterType] = m;
        if (m == null)
            LogEx.Once("recolor|nointerp|" + tinterType.Name,
                       $"[特效换色] {tinterType.Name} 上找不到 MaterialInterpolators 成员");
        return m;
    }

    private static readonly Dictionary<Type, System.Reflection.MemberInfo> _interpCache
        = new Dictionary<Type, System.Reflection.MemberInfo>();

    /// <summary>
    /// 兜底: 把运行时缓存 `_interpolatorSets` 里的数组也改一遍。
    ///
    /// 反汇编已证明集合持有的是**同一个数组引用**(ctor 把参数直接存进 +0x40),
    /// 所以这一步理论上冗余。留着是因为"理论上"三个字在本项目翻过车 ——
    /// 开销只是一次字段读取 + 一次空判断, 错了也只是白跑一趟。
    /// </summary>
    private static int RewriteSets(NOAH.VFX.MaterialTinter tinter, RecolorConfig.Target t)
    {
        try
        {
            _setsField = _setsField ?? Reflect.Member(tinter.GetType(), "_interpolatorSets", Reflect.All);
            var sets = Reflect.Read(tinter, _setsField);
            if (sets == null) return 0;
            int n = 0;
            foreach (var s in Reflect.Items(sets))
            {
                if (s == null) continue;
                var arr = Reflect.Read(s, Reflect.Member(s.GetType(), "interpolators", Reflect.All));
                n += RewriteArray(arr, t, "InterpolatorSet");
            }
            return n;
        }
        catch (Exception e) { LogEx.Err("TintBrush.RewriteSets", e); return 0; }
    }
}
