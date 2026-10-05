# -*- coding: utf-8 -*-
"""修复"自定义动作 -> 原版动作"的串扰。

两个独立成因，都必须修：

【1】变速名单里写的是【源动作名】(Sequence 里的名字)，
     但被克隆的段实际叫 aceN_xxx。于是 attackD2/attackAEX/... 这些词
     命中的是【原版动作】—— 原版佩利诺尔被 2.5 倍速连带加速，
     现实时间里的衔接窗口跟着被压扁。

【2】CloneAction 是整块内存拷贝，引用型字段(尤其 TimeScales List<ActionTimeScaleRange> @0xA0)
     和源对象是同一个 List 对象。就算名字匹配对了，往克隆体注入变速
     也会改到原版动作的列表。

修法：
  [1] EsComboChain 暴露 SourceOf(effName) / IsHijackedSource(name)；
      Match() 先挡掉"被劫持的原版名"，再把名单里的源名映射到我们的实际动作名。
  [2] CloneAction 之后给克隆体一个【自己的】TimeScales 空表。
"""
import io

# ================================================================ EsComboChain.cs
P = "Modules/Es/EsComboChain.cs"
s = io.open(P, encoding="utf-8").read()

A = """    /// <summary>这个名字是不是我们造的\"新平A动作\"。给动作变速模块用。</summary>
    internal static bool IsOurs(string action)"""
assert A in s, "缺 IsOurs"

B = """    /// <summary>我们造的动作名 -> 它的源动作名（ace1_902 -> attackD2）。不是我们造的返回 null。</summary>
    internal static string SourceOf(string effName)
    {
        try
        {
            string v;
            return (!string.IsNullOrEmpty(effName) && _synth.TryGetValue(effName, out v)) ? v : null;
        }
        catch { return null; }
    }

    /// <summary>这个动作名是不是【被我们劫持了源名的原版动作】。
    ///
    /// 我们把 attackD2 复制成了 ace1_902 挂进平A链；原版那个 attackD2 还在。
    /// 配置里写 attackD2 指的是【我们的段】—— 所以原版那个必须被挡掉，
    /// 否则变速/窗口会串到原版佩利诺尔上（实测就是这么翻车的）。</summary>
    internal static bool IsHijackedSource(string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name) || _synth.Count == 0) return false;
            foreach (var kv in _synth)
                if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>这个名字是不是我们造的\"新平A动作\"。给动作变速模块用。</summary>
    internal static bool IsOurs(string action)"""
s = s.replace(A, B, 1)

# [2] CloneAction 之后给克隆体独立的 TimeScales
C = """            Marshal.Copy(buf, 0, dst.Pointer + 0x10, bytes);

            return dst;"""
assert C in s, "缺 CloneAction 收尾"
D = """            Marshal.Copy(buf, 0, dst.Pointer + 0x10, bytes);

            // ★★★★★ 【必须隔离的一处】TimeScales 是引用型字段，整块拷贝之后
            //   克隆体和源对象【共用同一个 List 对象】(@0xA0 List<ActionTimeScaleRange>)。
            //   变速模块往克隆体注入倍率时，会连原版动作一起改 ——
            //   表现为"我们链上的佩利诺尔污染了原版佩利诺尔"。必须给它一张自己的表。
            try
            {
                dst.TimeScales = new Il2CppSystem.Collections.Generic.List<GamePlay.ActionTimeScaleRange>();
            }
            catch (Exception e) { Once("CloneAction|TimeScales", e); }

            return dst;"""
s = s.replace(C, D, 1)

io.open(P, "w", encoding="utf-8").write(s)
print("EsComboChain.cs: 已加 SourceOf / IsHijackedSource / TimeScales 隔离")

# ================================================================ EsActionSpeed.cs
P2 = "Modules/Es/EsActionSpeed.cs"
t = io.open(P2, encoding="utf-8").read()

E = """        foreach (var g in Groups())
            if (g.Names.Contains(name)) { _curFactor = g.K; return true; }
        _curFactor = 1f;"""
assert E in t, "缺 Match 主体"
F = """        // ★【隔离，第一道】被我们劫持了源名的原版动作，一律不碰。
        //   配置里写 attackD2 指的是我们链上那一段，不是原版佩利诺尔。
        if (EsComboChain.IsHijackedSource(name)) { _curFactor = 1f; return false; }

        // 我们的段实际叫 aceN_xxx，但配置里写的是源名（Sequence 里那个）—— 两边都要认。
        string src = EsComboChain.SourceOf(name);
        foreach (var g in Groups())
        {
            if (g.Names.Contains(name)) { _curFactor = g.K; return true; }
            if (src != null && g.Names.Contains(src)) { _curFactor = g.K; return true; }
        }
        _curFactor = 1f;"""
t = t.replace(E, F, 1)

# SpeedOf 走同一套映射，否则连段模组算窗口时会算出 1.0
G = """            if (string.IsNullOrEmpty(name)) return 1f;
            foreach (var g in Groups())
                if (g.Names.Contains(name)) return g.K;"""
assert G in t, "缺 SpeedOf 主体"
H = """            if (string.IsNullOrEmpty(name)) return 1f;
            if (EsComboChain.IsHijackedSource(name)) return 1f;      // 原版动作不受我们的名单影响
            string src = EsComboChain.SourceOf(name);
            foreach (var g in Groups())
            {
                if (g.Names.Contains(name)) return g.K;
                if (src != null && g.Names.Contains(src)) return g.K;
            }"""
t = t.replace(G, H, 1)

io.open(P2, "w", encoding="utf-8").write(t)
print("EsActionSpeed.cs: 已加两道隔离")
