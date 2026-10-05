# -*- coding: utf-8 -*-
"""part2: 重写 ApplyChain -> 多链；MakeSkill 接收 Seg（方向/起手）。"""
import io

P = "Modules/Es/EsComboChain.cs"
s = io.open(P, encoding="utf-8").read()

# ---------------------------------------------------------------- 1) 替换 ApplyChain 整体
start = s.index("    private static void ApplyChain(GamePlay.PlayerSkillMgr mgr)")
end = s.index("    /// <summary>\n    /// 探针: `findNextSkillMatchPreOrderAndInputDir` 的结果。", start)

NEW = '''    private static void ApplyChain(GamePlay.PlayerSkillMgr mgr)
    {
        // ★ 只关改写、保留探针（给"关掉连段改动、观察原生行为"用）
        if (CfgRewrite != null && CfgRewrite.Value != true) return;

        var specs = ChainsOrLegacy();
        if (specs.Count == 0) return;

        // ★ 必须在动手改之前扫全部链的原生行（读原生值用；改完再扫会读到自己造的东西）
        BuildNativeLookup(mgr);

        foreach (var spec in specs) ApplyOneChain(mgr, spec);
    }

    /// <summary>重写【一条】链。
    ///
    /// 与旧实现的两个根本差别（§3.6.1e E）：
    ///   1. 不再"找平A块并原地替换" —— 改为【移除原生同动作段 + 追加我们的段】。
    ///      旧模型只替换平A槽位，于是佩利诺尔/布鲁诺那些原生段**留在链里**，
    ///      和我们的克隆段条件相同却排在数组更前面 → 我们的段一次都选不中（实测）。
    ///   2. 宿主（继承时序的来源）= 【链的首段】的原生行。旧模型用"被替换的平A行"，
    ///      但新模型里链不再绑定平A槽位。一条链只有一种节奏，首段即基准。
    /// </summary>
    private static void ApplyOneChain(GamePlay.PlayerSkillMgr mgr, ChainSpec spec)
    {
        int g = spec.Group;
        if (g < 0 || g > 12 || spec.Segs.Count == 0) return;

        IntPtr arr = ReadPtr(mgr.Pointer, OFF_CHAINS);
        if (arr == IntPtr.Zero) return;
        int arrLen = ReadI32(arr, 0x18);
        if (g >= arrLen) return;
        IntPtr chainPtr = ReadPtr(arr, 0x20 + g * 8);
        if (chainPtr == IntPtr.Zero) return;

        var list = GetList(chainPtr);
        if (list == null) return;
        int cnt = SafeCount(list);
        if (cnt == 0) return;

        // 幂等：只用 Order>=900 这一个哨兵判据（绝不用内容特征，见旧注释的教训）
        if (HasOurMarker(list, cnt))
        {
            if (_applied.Add(g))
                Plugin.Log?.LogInfo($"[连段模组] 链[{g}] 已注入(Order>=900)，跳过 —— 避免重复拆装重置推进位置");
            return;
        }

        // ---- 宿主 = 链首段对应的原生行 ----
        string firstAct = spec.Segs[0].Action;
        GamePlay.PlayerSkill host = null;
        for (int i = 0; i < cnt; i++)
        {
            var sk = TryGet(list, i);
            if (sk == null) continue;
            if (string.Equals(ActionOf(sk), firstAct, StringComparison.OrdinalIgnoreCase))
            {
                IntPtr pp = ReadPtr(sk.SkillActivate?.Pointer ?? IntPtr.Zero, OFF_WRAP_DATA);
                if (pp != IntPtr.Zero && ReadI32(pp, OFF_ORDER) < 900) { host = sk; break; }
            }
        }
        if (host == null)
        {
            // 首段在链里没有原生行（例如它只存在于别的链）→ 用链里第一个原生段兜底
            for (int i = 0; i < cnt; i++)
            {
                var sk = TryGet(list, i);
                if (sk == null) continue;
                IntPtr pp = ReadPtr(sk.SkillActivate?.Pointer ?? IntPtr.Zero, OFF_WRAP_DATA);
                if (pp != IntPtr.Zero && ReadI32(pp, OFF_ORDER) < 900) { host = sk; break; }
            }
        }
        if (host == null) host = TryGet(list, 0);
        if (host == null) return;

        GamePlay.PlayerSkillChain chainObj = null;
        try { chainObj = new GamePlay.PlayerSkillChain(chainPtr); } catch { }

        // ---- 造我们的段 ----
        var made = new List<GamePlay.PlayerSkill>();
        for (int i = 0; i < spec.Segs.Count; i++)
        {
            var ps = MakeSkill(host, spec.Segs[i], 900 + i, g, chainObj, i == 0 ? -1 : 900 + i - 1);
            if (ps == null)
            {
                WarnOnce("build|" + g + "|" + i, $"链[{g}] 第 {i + 1} 段 \\"{spec.Segs[i].Action}\\" 造不出来，该链中止");
                return;
            }
            made.Add(ps);
            try { _ours.Add(ps.Pointer); } catch { }
        }

        // ---- 备份整条链（还原用）----
        var backup = new List<GamePlay.PlayerSkill>();
        for (int i = 0; i < cnt; i++)
        {
            var sk = TryGet(list, i);
            if (sk != null) backup.Add(sk);
        }
        _bkOrig[g] = backup;
        _bkStart[g] = 0;

        // ---- 移除原生同动作段（§3.6.1e E：否则它会抢在我们的克隆段前面）----
        int removed = 0;
        if (CfgTakeOver?.Value != false)
        {
            for (int i = SafeCount(list) - 1; i >= 0; i--)
            {
                var sk = TryGet(list, i);
                if (sk == null) continue;
                if (OrderOf(sk) >= 900) continue;                  // 我们自己的不动
                string a = ActionOf(sk);
                if (string.IsNullOrEmpty(a)) continue;
                bool hit = false;
                foreach (var sg in spec.Segs)
                    if (string.Equals(sg.Action, a, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                if (hit) { SafeRemoveAt(list, i); removed++; }
            }
        }

        // ---- 追加我们的段 ----
        for (int i = 0; i < made.Count; i++) list.Add(made[i]);

        _applied.Add(g);
        DumpOurSegments(list, "重写后");
        Plugin.Log?.LogInfo($"[连段模组] 链[{g}] 重写完成: 移除原生同动作段 {removed} 个, 追加 {made.Count} 段, " +
                            $"现共 {SafeCount(list)} 段");
    }

'''
s = s[:start] + NEW + s[end:]

# ---------------------------------------------------------------- 2) MakeSkill 接收 Seg
A = """    private static GamePlay.PlayerSkill MakeSkill(GamePlay.PlayerSkill tmpl, string action, int newOrder, int group,
                                                  GamePlay.PlayerSkillChain chain, int prevOrder)
    {
        try
        {
            var sa = tmpl.SkillActivate;"""
B = """    private static GamePlay.PlayerSkill MakeSkill(GamePlay.PlayerSkill tmpl, Seg seg, int newOrder, int group,
                                                  GamePlay.PlayerSkillChain chain, int prevOrder)
    {
        try
        {
            string action = seg.Action;
            var sa = tmpl.SkillActivate;"""
assert A in s, "MakeSkill 签名锚点"
s = s.replace(A, B, 1)

# ---------------------------------------------------------------- 3) 方向由段声明覆盖
A2 = """            catch (Exception e) { Once("InputDir", e); }"""
B2 = A2 + """
            // 段声明里的方向优先（方向是【链位置】属性 —— 同一动作在不同链里方向不同）
            if (seg.Dir >= 0)
            {
                try { copy.InputDir = (SkillInputDirType)seg.Dir; } catch { }
            }"""
assert A2 in s, "InputDir 锚点"
s = s.replace(A2, B2, 1)

# ---------------------------------------------------------------- 4) 起手标记 -> 前驱清空
A3 = "            if (prevOrder >= 0)\n            {\n                try\n                {\n                    var po = copy.PreSkillOrder;"
B3 = """            // ★ 起手段（尾缀 +）：前驱清空 —— 这样它才能从站姿/任意输入直接起手
            if (seg.Entry)
            {
                try { copy.PreSkillOrder?.Clear(); } catch (Exception e) { Once("EntryClear", e); }
            }
            else if (prevOrder >= 0)
            {
                try
                {
                    var po = copy.PreSkillOrder;"""
assert A3 in s, "preSkillOrder 锚点"
s = s.replace(A3, B3, 1)

io.open(P, "w", encoding="utf-8").write(s)
print("part2 完成")
