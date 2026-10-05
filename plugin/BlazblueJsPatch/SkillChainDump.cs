using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace BlazblueJsPatch;

/// <summary>
/// 普攻连段的"模组"到底存在哪 —— 先把真实数据打出来。
///
/// 连段的载体
/// ──────────
/// `PlayerSkillMgr` 里是**按技能组**分的一条条链：
///
///     PlayerSkillMgr
///         private PlayerSkillChain[] m_SkChains;   // 0x30   下标 = PlayerSkillGroup
///     PlayerSkillChain
///         public List&lt;PlayerSkill&gt; SkillList;     // 0x18   ★ 这一组的段序列 = 连段模组
///         public int SkillType;                    // 0x24
///         private InputCmd m_Input;                // 0x68
///         public int CastTimes;                    // 0x64
///     PlayerSkill
///         public int SkillId;                      // 0x18
///         public int ActorId;                      // 0x1C
///         public SkillActivateFixedPointWrap SkillActivate; // 0x20  ★ 指向 skillactivate 表那一行
///         public int AttrOrder;                    // 0x28
///         public InputCmd Input;                   // 0x44
///
/// `SkillActivate` 就是我们之前反查出来的那张表的一行，上面有
/// `Action`(动作名) / `Input` / `InputDir` / `Group` / `Order` / `BulletId` …
///
/// 所以"把平1-平2-平3-平4 改成平1-平2-佩利诺尔2-布鲁诺1-平3-…"这件事，
/// 本质就是**重排某个 group 的 SkillList**。但动手之前必须先看清：
///   · 平A(Any) 和下+攻击(Down) 到底是在同一条链里、还是两条链
///   · 每条链里的段是按什么顺序排的
///   · 每个 PlayerSkill 的 Input / Group / Order 是什么
/// 猜错一个就会把整个普攻改坏，所以这一步只做"只读转储"。
/// </summary>
internal static class SkillChainDump
{
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgEnabled;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgOnActionChange;
    internal static BepInEx.Configuration.ConfigEntry<bool> CfgDetail;

    private static readonly HashSet<string> _dumped = new HashSet<string>();
    private static readonly string[] GROUP_NAMES =
    {
        "None", "Attack", "Skill1", "Ultra", "AttackAir", "Dash", "DashAttack",
        "Jump", "LongAttack", "CustomUse1", "CustomUse2", "Summon", "Burst"
    };

    public static int Apply(Harmony harmony)
    {
        int n = 0;
        var t = AccessTools.TypeByName("GamePlay.PlayerSkillMgr");
        if (t == null) { Plugin.Log?.LogWarning("  [连段转储] 找不到 GamePlay.PlayerSkillMgr"); return 0; }

        // InitSkills 建链，建完立刻转储
        try
        {
            var m = AccessTools.Method(t, "InitSkills", Type.EmptyTypes);
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(SkillChainDump), nameof(InitPostfix))));
                Plugin.Log.LogInfo("  [连段转储] 已挂钩 PlayerSkillMgr.InitSkills");
                n++;
            }
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段转储] 挂 InitSkills 失败: {e.Message}"); }

        // 动作变化时也看一眼（链可能被重建）
        try
        {
            var m = AccessTools.Method(t, "NotifyActionChanged");
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(SkillChainDump), nameof(NotifyPostfix))));
                Plugin.Log.LogInfo("  [连段转储] 已挂钩 PlayerSkillMgr.NotifyActionChanged");
                n++;
            }
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"  [连段转储] 挂 NotifyActionChanged 失败: {e.Message}"); }

        Plugin.Log.LogInfo($"  [连段转储] 生效配置: Enabled={CfgEnabled?.Value} 动作变化时也转储={CfgOnActionChange?.Value}");
        return n;
    }

    public static void InitPostfix(GamePlay.PlayerSkillMgr __instance)
    {
        if (CfgEnabled?.Value != true) return;
        if (!IsPlayer(__instance)) return;
        // ⚠ InitSkills 建的是【通用链】(Dash/Jump，ActorId=1002)，
        //   Attack 链这时还是空的 —— 实测「链[1] Attack 段数=0」。
        //   真正有内容要等它被重建/首次使用，所以这里只是顺手看一眼，
        //   主转储放在 NotifyActionChanged（每次动作变化都会走）。
        TryDump(__instance, "InitSkills");
    }

    public static void NotifyPostfix(GamePlay.PlayerSkillMgr __instance)
    {
        if (CfgEnabled?.Value != true) return;
        if (!IsPlayer(__instance)) return;
        TryDump(__instance, "NotifyActionChanged");
    }

    /// <summary>只在「Attack 链有内容」时才真正转储，且只转储一次。
    /// 这样不管链是哪个时机建起来的，都能抓到它成型后的样子。</summary>
    private static void TryDump(GamePlay.PlayerSkillMgr mgr, string why)
    {
        try
        {
            int n = AttackCount(mgr);
            if (n <= 0) return;
            string key = "attack" + n;
            if (!_dumped.Add(key)) return;
            Dump(mgr, why + $" (Attack 段数={n})");
        }
        catch { }
    }

    private static int AttackCount(GamePlay.PlayerSkillMgr mgr)
    {
        IntPtr mp = mgr.Pointer;
        if (mp == IntPtr.Zero) return 0;
        IntPtr arr = ReadPtr(mp, 0x30);
        if (arr == IntPtr.Zero) return 0;
        IntPtr chain = ReadPtr(arr, 0x20 + 1 * 8);      // [1] = Attack
        if (chain == IntPtr.Zero) return 0;
        IntPtr list = ReadPtr(chain, 0x18);
        return list == IntPtr.Zero ? 0 : ReadI32(list, 0x18);
    }

    private static bool IsPlayer(GamePlay.PlayerSkillMgr mgr)
    {
        try
        {
            var o = mgr?.Owner;                       // 0x10 PlayerObj
            return o != null && DashInvincible.IsLocalPlayerActor(o);
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ 转储

    private static void Dump(GamePlay.PlayerSkillMgr mgr, string why)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("╔═══════════════════════════════════════════════════════════════════════");
            sb.AppendLine($"║ 普攻连段模组转储  (触发={why})");
            sb.AppendLine("╚═══════════════════════════════════════════════════════════════════════");

            // ⚠ `m_SkChains` 是【私有】字段，Il2CppInterop 只生成公开字段，
            //   反射 `GetField("m_SkChains")` 恒为 null（上一版就是这么失败的）。
            //   改走裸指针 —— 布局来自 dump.cs，这条路上我们一直很稳。
            IntPtr mp = mgr.Pointer;
            if (mp == IntPtr.Zero) { Plugin.Log?.LogInfo(sb + " (mgr.Pointer 为空)"); return; }

            IntPtr arr = ReadPtr(mp, 0x30);                 // PlayerSkillChain[]
            if (arr == IntPtr.Zero) { Plugin.Log?.LogInfo(sb + " (m_SkChains 为空)"); return; }
            int arrLen = ReadI32(arr, 0x18);                // 数组长度
            sb.AppendLine($"║ m_SkChains 长度 = {arrLen}");

            bool any = false;
            for (int g = 0; g < arrLen; g++)
            {
                IntPtr chain = ReadPtr(arr, 0x20 + g * 8);
                if (chain == IntPtr.Zero) continue;

                IntPtr list = ReadPtr(chain, 0x18);         // SkillList
                int cnt = list == IntPtr.Zero ? 0 : ReadI32(list, 0x18);
                if (cnt == 0 && g != 1) continue;           // 空链不占篇幅（Attack 组永远打出来）
                any = true;

                sb.AppendLine($"┌─ 链[{g}] {GroupName(g)}  段数={cnt}   " +
                              $"链SkillType={ReadI32(chain, 0x24)}  链Input={ReadI32(chain, 0x68)}  " +
                              $"Status={ReadI32(chain, 0x20)}  CastTimes={ReadI32(chain, 0x64)}");

                IntPtr items = list == IntPtr.Zero ? IntPtr.Zero : ReadPtr(list, 0x10);
                for (int i = 0; i < cnt && i < 40; i++)
                {
                    IntPtr sk = ReadPtr(items, 0x20 + i * 8);
                    if (sk == IntPtr.Zero) continue;

                    int sid = ReadI32(sk, 0x18), actor = ReadI32(sk, 0x1C);
                    int attr = ReadI32(sk, 0x28), skInput = ReadI32(sk, 0x44);
                    IntPtr wrap = ReadPtr(sk, 0x20);        // SkillActivateFixedPointWrap
                    IntPtr proto = wrap == IntPtr.Zero ? IntPtr.Zero : ReadPtr(wrap, 0x10);

                    string act = MStr(proto, 0x28);         // action_
                    string inp = MStr(proto, 0x40);         // input_
                    int idir = proto == IntPtr.Zero ? -1 : ReadI32(proto, 0x48);
                    int grp = proto == IntPtr.Zero ? -1 : ReadI32(proto, 0x1C);
                    int ord = proto == IntPtr.Zero ? -1 : ReadI32(proto, 0x20);

                    // ★ 衔接相关的字段一并摊开 —— 这是"连段怎么衔接"的原始依据。
                    //   布鲁诺三段(attackAEX/attackB/attackC)是原生就能正常衔接的样例，
                    //   把它的值读出来照抄，比我自己拍脑袋设 HoldSeconds 靠谱得多。
                    //   这里要走托管包装 + 反射读属性 —— 裸指针读这些 protobuf 字段太脆。
                    object sa = null;
                    try { sa = wrap == IntPtr.Zero ? null : new SkillActivateFixedPointWrap(wrap); } catch { }
                    string pre = ListOfInt(proto, "PreSkillOrder");
                    string mps = ListOfObj(sa, "Mps");
                    string trig = ListOfInt(proto, "ReqTriggerId");
                    sb.AppendLine($"│   [{i}] \"{act}\"   表槽={grp}.{ord}  按键={inp}/{DirName(idir)}");
                    sb.AppendLine($"│        A: SkillId={sid} ActorId={actor} AttrOrder={attr}   skill.Input={skInput}" +
                                  $"   (proto=0x{proto.ToInt64():X})");
                    if (CfgDetail?.Value == true)
                    {
                        sb.AppendLine($"│        衔接: PreOrder=[{pre}]  ActdurStrict={Fld(sa, "ActdurStrict")}" +
                                      $"  PreInput={Fld(sa, "Preinputtime")}  Timeout={Fld(sa, "Timeout")}");
                        sb.AppendLine($"│        长按: UseLongPress={Fld(sa, "UseLongPress")} " +
                                      $"{Fld(sa, "LongPressStart")}~{Fld(sa, "LongPressEnd")}  " +
                                      $"Lasting={Fld(sa, "LastingDuration")}  PreCD={Fld(sa, "PrecheckActionCd")}");
                        sb.AppendLine($"│        条件: MP=[{mps}] ReqTrigger=[{trig}]  " +
                                      $"Ground={Fld(sa, "AllowGround")} Active={Fld(sa, "AllowActiveState")} " +
                                      $"Fly={Fld(sa, "AllowFlying")}");
                    }
                }
                sb.AppendLine("└───────────────────────────────────────────────────────────────────────");
            }

            if (!any) sb.AppendLine("║ (所有链都是空的)");
            Plugin.Log?.LogInfo(sb.ToString());
        }
        catch (Exception ex) { Plugin.Log?.LogWarning($"[连段转储] 异常: {ex.Message}"); }
    }

    private static string GroupName(int g)
    {
        if (g >= 0 && g < GROUP_NAMES.Length) return GROUP_NAMES[g];
        return "Group" + g;
    }

    // ------------------------------------------------------------------ 裸读（布局来自 dump.cs）

    private static IntPtr ReadPtr(IntPtr p, int off)
    {
        if (p == IntPtr.Zero) return IntPtr.Zero;
        IntPtr v = System.Runtime.InteropServices.Marshal.ReadIntPtr(p, off);
        long x = v.ToInt64();
        if (x < 0x10000 || x > 0x7FFFFFFFFFFF || (x & 7) != 0) return IntPtr.Zero;
        return v;
    }

    private static int ReadI32(IntPtr p, int off)
    {
        if (p == IntPtr.Zero) return 0;
        try { return System.Runtime.InteropServices.Marshal.ReadInt32(p, off); } catch { return 0; }
    }

    private static string MStr(IntPtr p, int off)
    {
        IntPtr s = ReadPtr(p, off);
        if (s == IntPtr.Zero) return "";
        try { return Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(s); } catch { return "<err>"; }
    }

    /// <summary>SkillInputDirType: Any=0 Up=1 Down=2 Front=3 Back=4 NoDir=5 Left=6 Right=7 AnyX=8
    /// ⚠ 上一版用的是 EInputDirection 的映射(0=None,1=Any,2=Up,3=Down)，整体错了一位，
    ///   把所有方向都念反了 —— 直接导致"佩利诺尔是下+攻击"这条对不上。</summary>
    private static string DirName(int v)
    {
        switch (v)
        {
            case 0: return "Any";
            case 1: return "Up";
            case 2: return "Down";
            case 3: return "Front";
            case 4: return "Back";
            case 5: return "NoDir";
            default: return "v" + v;
        }
    }

    // ------------------------------------------------------------------ 反射取值

    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<GamePlay.PlayerSkillChain> GetChains(GamePlay.PlayerSkillMgr mgr)
    {
        try
        {
            var f = typeof(GamePlay.PlayerSkillMgr).GetField("m_SkChains",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            var v = f?.GetValue(mgr);
            return v as Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<GamePlay.PlayerSkillChain>;
        }
        catch (Exception e) { Plugin.Log?.LogWarning($"[连段转储] 读 m_SkChains 失败: {e.Message}"); return null; }
    }

    private static object GetList(GamePlay.PlayerSkillChain chain)
    {
        try
        {
            var f = typeof(GamePlay.PlayerSkillChain).GetField("SkillList",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f?.GetValue(chain);
        }
        catch { return null; }
    }

    private static int Count(object list)
    {
        if (list == null) return 0;
        try { var p = list.GetType().GetProperty("Count"); return p == null ? 0 : Convert.ToInt32(p.GetValue(list)); }
        catch { return 0; }
    }

    /// <summary>读 SkillActivate 上的某个属性并转成可打印的字符串。</summary>
    private static string Fld(object sa, string name)
    {
        var v = GetMember(sa, name);
        if (v == null) return "-";
        try { return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return v.ToString(); }
    }

    /// <summary>读 protobuf 上的 repeated int 字段。
    /// ⚠ 必须走【protobuf 消息本身】(`new SkillActivateFixedPoint(protoPtr)`)，
    ///   不要去枚举 wrap 上那个 IList —— Il2CppInterop 出来的 IList 走非泛型
    ///   IEnumerable 会抛/返回空，实测就是 `PreOrder=[?]`。
    ///   protobuf 的 RepeatedField&lt;int&gt; 是真正的 .NET 集合，枚举没问题。</summary>
    private static string ListOfInt(IntPtr protoPtr, string propName)
    {
        try
        {
            if (protoPtr == IntPtr.Zero) return "";
            var msg = new SkillActivateFixedPoint(protoPtr);
            var v = GetMember(msg, propName);
            if (v == null) return "";
            var parts = new List<string>();
            foreach (var x in (IEnumerable)v) { parts.Add(x?.ToString() ?? ""); if (parts.Count > 12) break; }
            return string.Join(",", parts.ToArray());
        }
        catch (Exception e) { return "<err:" + e.GetType().Name + ":" + e.Message + ">"; }
    }

    private static string ListOfObj(object o, string name)
    {
        try
        {
            var v = GetMember(o, name);
            if (v == null) return "";
            var parts = new List<string>();
            foreach (var x in (IEnumerable)v) { parts.Add(x?.ToString() ?? ""); if (parts.Count > 4) break; }
            return string.Join(",", parts.ToArray());
        }
        catch { return "?"; }
    }

    /// <summary>⚠ 不用非泛型 IList —— Il2CppReferenceArray&lt;T&gt; 没实现它，`as IList` 恒为 null。
    /// 泛型 IEnumerable&lt;T&gt; 是实现了的，所以走非泛型 IEnumerable 迭代。</summary>
    private static IEnumerable Iterate(object list)
    {
        if (list is IEnumerable e) return e;
        return new object[0];
    }

    private static object GetMember(object o, string name)
    {
        if (o == null) return null;
        try
        {
            var t = o.GetType();
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var pi = t.GetProperty(name, F);
            if (pi != null) return pi.GetValue(o);
            var fi = t.GetField(name, F);
            return fi?.GetValue(o);
        }
        catch { return null; }
    }

    private static int Int(object o, string name)
    {
        var v = GetMember(o, name);
        try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
    }

    private static string Str(object o, string name)
    {
        var v = GetMember(o, name);
        return v?.ToString() ?? "";
    }
}
