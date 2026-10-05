# -*- coding: utf-8 -*-
"""StartBullets 第 6 段扩成 4 个取值（原来只有参考系）：
    S / 屏幕 / 留空      = 屏幕(世界)角，无条件      —— 崔斯坦用
    L / 局部 / 朝向      = 以角色前方为 0 的角度      —— 自推飞行时用
    朝左 / left / lft    = 屏幕角，且【只在 ES 朝左时】放
    朝右 / right / rgt   = 屏幕角，且【只在 ES 朝右时】放
用途（用户要求）：高文落地时按 ES 朝向决定放左剑气还是右剑气，
方向和崔斯坦的左右剑气一致(屏幕 180/0)，所以参考系仍是 S，只是加朝向闸门。
"""
import io, re

CS = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Modules\Es\EsEmblemBurst.cs"
PL = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Plugin.cs"
CFG = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\BepInEx\config\ace.bbee.jspatch.cfg"

RULES = ("fallend2:B1:0.00:180:18 | fallend2:B1:0.00:0:18 | "
         "fallmdownendEX2:B1:0.00:180:18 | fallmdownendEX2:B1:0.00:0:18 | "
         "fallmdownendEX2:B1:0.00:135:18 | fallmdownendEX2:B1:0.00:45:18 | "
         "fallmdownendEX:B1:0.00:180:18 | fallmdownendEX:B1:0.00:0:18 | "
         "fallmdownendEX:B1:0.00:135:18 | fallmdownendEX:B1:0.00:45:18 | "
         "dashAAendEX:C1:0:180:18:朝左 | dashAAendEX:C1:0:0:18:朝右")

s = io.open(CS, encoding="utf-8").read()

# 1) Req 字段
a = "        public bool FlyLocal;"
b = ("        public bool FlyLocal;\n\n"
     "        /// <summary>朝向闸门：0=不限，-1=只在朝左时放，+1=只在朝右时放。\n"
     "        /// 用途：高文落地按 ES 朝向放左/右剑气（方向和崔斯坦的左右一致，所以参考系仍是屏幕）。</summary>\n"
     "        public int FaceReq;")
assert a in s
s = s.replace(a, b, 1)

# 2) 朝向判定 helper
a = "    private static Vector2 GlobalDirAt(GamePlay.ActorBase caster, float deg)"
b = """    /// <summary>角色此刻是否朝左（世界 -x 方向）。
    ///
    /// 判据用 `-GlobalDirAt(caster,0)` —— 也就是**取反之后**的那个"真正的前方"。
    /// 为什么带这个取反：实测（原生那发 C1 与我们那发 pos/dir 完全相同、原生飞得对而我们飞反了）
    /// 说明 GlobalDirAt 给的全局方向与角色实际朝向差 180°，见 FlyLocal 处的三条证据。
    /// </summary>
    private static bool FacingLeft(GamePlay.ActorBase caster)
    {
        try
        {
            var f = -GlobalDirAt(caster, 0f);
            return f.x < 0f;
        }
        catch { return false; }
    }

    private static Vector2 GlobalDirAt(GamePlay.ActorBase caster, float deg)"""
assert a in s
s = s.replace(a, b, 1)

# 3) StartFor 签名 + 解析
a = ("                                 out float flySpeed, out bool hasFly, out bool flyLocal)\n"
     "    {\n"
     "        extra = null; delay = 0f; angle = 0f; moveDeg = 0f; hasMoveDeg = false;\n"
     "        flySpeed = 0f; hasFly = false; flyLocal = false;")
b = ("                                 out float flySpeed, out bool hasFly, out bool flyLocal, out int faceReq)\n"
     "    {\n"
     "        extra = null; delay = 0f; angle = 0f; moveDeg = 0f; hasMoveDeg = false;\n"
     "        flySpeed = 0f; hasFly = false; flyLocal = false; faceReq = 0;")
assert a in s
s = s.replace(a, b, 1)

a = """            if (parts.Length > 4)
            {
                var fr = parts[4].Trim();
                flyLocal = fr.Equals("L", StringComparison.OrdinalIgnoreCase) ||
                           fr.Equals("local", StringComparison.OrdinalIgnoreCase) ||
                           fr.Equals("局部", StringComparison.Ordinal) ||
                           fr.Equals("朝向", StringComparison.Ordinal);
            }
            return true;"""
b = """            if (parts.Length > 4)
            {
                var fr = parts[4].Trim();
                if (fr.Equals("L", StringComparison.OrdinalIgnoreCase) ||
                    fr.Equals("local", StringComparison.OrdinalIgnoreCase) ||
                    fr.Equals("局部", StringComparison.Ordinal) ||
                    fr.Equals("朝向", StringComparison.Ordinal))
                {
                    flyLocal = true;                       // 以角色前方为 0
                }
                else if (fr.Equals("朝左", StringComparison.Ordinal) ||
                         fr.Equals("left", StringComparison.OrdinalIgnoreCase) ||
                         fr.Equals("lft", StringComparison.OrdinalIgnoreCase))
                {
                    faceReq = -1;                          // 屏幕角，但只在朝左时放
                }
                else if (fr.Equals("朝右", StringComparison.Ordinal) ||
                         fr.Equals("right", StringComparison.OrdinalIgnoreCase) ||
                         fr.Equals("rgt", StringComparison.OrdinalIgnoreCase))
                {
                    faceReq = +1;                          // 屏幕角，但只在朝右时放
                }
                // 其余(S/屏幕/无法识别) = 屏幕角、无条件
            }
            return true;"""
assert a in s
s = s.replace(a, b, 1)

# 4) 调用点 + 赋值 + 朝向闸门
a = "out var moveDeg, out var hasMoveDeg, out var flySpeed, out var hasFly, out var flyLocal)) break;"
b = "out var moveDeg, out var hasMoveDeg, out var flySpeed, out var hasFly, out var flyLocal, out var faceReq)) break;"
assert a in s
s = s.replace(a, b, 1)

a = "                        FlyDeg = angle, FlySpeed = flySpeed, HasFly = hasFly, FlyLocal = flyLocal,"
b = ("                        FlyDeg = angle, FlySpeed = flySpeed, HasFly = hasFly, FlyLocal = flyLocal,\n"
     "                        FaceReq = faceReq,")
assert a in s
s = s.replace(a, b, 1)

# 5) 消费端：朝向闸门（放在建 mover 之前）
a = "                        Vector2 flyDir = Vector2.zero;"
b = """                        // ★ 朝向闸门：朝左的规则只在 ES 朝左时放，朝右的同理。
                        //   规则没写就 faceReq=0（不限）。静默跳过是坑，所以打一条（每动作前 3 次）。
                        if (r.FaceReq != 0)
                        {
                            bool left = FacingLeft(r.Caster);
                            if ((r.FaceReq < 0 && !left) || (r.FaceReq > 0 && left))
                            {
                                _faceSkipLogged.TryGetValue(r.Action, out int sl);
                                if (sl < 3)
                                {
                                    _faceSkipLogged[r.Action] = sl + 1;
                                    Plugin.Log?.LogInfo($"[纹章接管:追加] 跳过 \\"{r.Action}\\" —— 朝向闸门" +
                                                        $"(要求 {(r.FaceReq < 0 ? "朝左" : "朝右")}, 实际 {(left ? "朝左" : "朝右")})");
                                }
                                continue;
                            }
                        }

                        Vector2 flyDir = Vector2.zero;"""
assert a in s
s = s.replace(a, b, 1)

a = "    private static readonly HashSet<string> _startDiagSeen = new HashSet<string>();"
b = (a + "\n    /// <summary>被朝向闸门跳过的计数（按弹幕名去重，只打前几次）。</summary>\n"
     "    private static readonly Dictionary<string, int> _faceSkipLogged = new Dictionary<string, int>();")
assert a in s
s = s.replace(a, b, 1)

io.open(CS, "w", encoding="utf-8").write(s)

# 6) cfg + 默认值
lines = io.open(CFG, encoding="utf-8").read().split("\n")
st = next(i for i, l in enumerate(lines) if l.strip() == "[纹章解放]")
en = next(i for i in range(st + 1, len(lines)) if lines[i].startswith("["))
for i in range(st, en):
    if lines[i].startswith("StartBullets ="):
        lines[i] = "StartBullets = " + RULES
        break
io.open(CFG, "w", encoding="utf-8").write("\n".join(lines))

p = io.open(PL, encoding="utf-8").read()
p2, n = re.subn(r'(EsEmblemBurst\.CfgStartBullets = Config\.Bind\(csec, "StartBullets",\s*\n\s*")[^"]*(",)',
                lambda m: m.group(1) + RULES + m.group(2), p, count=1)
assert n == 1
io.open(PL, "w", encoding="utf-8").write(p2)
print("ok")
