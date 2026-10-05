# -*- coding: utf-8 -*-
"""StartBullets 加"参考系"第 6 段: S=屏幕(默认, 崔斯坦用) / L=角色朝向(高文用)。
为什么需要: 弹幕自己的位移逻辑不可靠(dir 只当"朝前/朝后"的粗判据),
实测高文朝左朝右, 剑气一律往右飞。所以自推飞行时方向必须我们自己算:
  S -> (cos, sin) 屏幕/世界角
  L -> GlobalDirAt(caster, deg) 以角色前方为 0 的全局方向(模块里已有这个helper)
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
         "dashAAendEX:C1:0:0:18:L")

s = io.open(CS, encoding="utf-8").read()

# 1) Req 字段
a = "        /// <summary>配置里给了飞行角才算自推飞行（见 `StartFor` 的解析）。</summary>\n        public bool HasFly;"
b = ("        /// <summary>配置里给了飞行角才算自推飞行（见 `StartFor` 的解析）。</summary>\n"
     "        public bool HasFly;\n\n"
     "        /// <summary>飞行方向的参考系：false=屏幕/世界角(崔斯坦用)，true=角色朝向(高文用)。\n"
     "        /// 为什么必须能选：高文有方向性、崔斯坦没有。而弹幕自己的位移逻辑不可靠，\n"
     "        /// 只能我们自己算全局方向 —— 这两种参考系算出来的全局方向是两回事。</summary>\n"
     "        public bool FlyLocal;")
assert a in s, "HasFly field"
s = s.replace(a, b, 1)

# 2) StartFor 签名 + 解析
a = ("                                 out float flySpeed, out bool hasFly)\n"
     "    {\n"
     "        extra = null; delay = 0f; angle = 0f; moveDeg = 0f; hasMoveDeg = false;\n"
     "        flySpeed = 0f; hasFly = false;")
b = ("                                 out float flySpeed, out bool hasFly, out bool flyLocal)\n"
     "    {\n"
     "        extra = null; delay = 0f; angle = 0f; moveDeg = 0f; hasMoveDeg = false;\n"
     "        flySpeed = 0f; hasFly = false; flyLocal = false;")
assert a in s, "StartFor sig"
s = s.replace(a, b, 1)

a = """            if (parts.Length > 4 && parts[4].Trim().Length > 0)
            {
                float sp;
                if (float.TryParse(parts[4].Trim(), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out sp) && sp > 0f)
                {
                    hasFly = true; flySpeed = sp;
                }
            }
            return true;"""
b = """            if (parts.Length > 4 && parts[4].Trim().Length > 0)
            {
                float sp;
                if (float.TryParse(parts[4].Trim(), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out sp) && sp > 0f)
                {
                    hasFly = true; flySpeed = sp;
                }
            }
            // ★ 第 6 段 = 飞行方向的参考系：L/局部/朝向 = 以角色前方为 0；S/屏幕 或留空 = 屏幕/世界角。
            //   高文有方向性 -> L；崔斯坦没有 -> S(默认，也是既有行为)。
            if (parts.Length > 5)
            {
                var fr = parts[5].Trim();
                flyLocal = fr.Equals("L", StringComparison.OrdinalIgnoreCase) ||
                           fr.Equals("local", StringComparison.OrdinalIgnoreCase) ||
                           fr.Equals("局部", StringComparison.Ordinal) ||
                           fr.Equals("朝向", StringComparison.Ordinal);
            }
            return true;"""
assert a in s, "StartFor parse"
s = s.replace(a, b, 1)

# 3) 调用点
a = "out var moveDeg, out var hasMoveDeg, out var flySpeed, out var hasFly)) break;"
b = "out var moveDeg, out var hasMoveDeg, out var flySpeed, out var hasFly, out var flyLocal)) break;"
assert a in s, "call site"
s = s.replace(a, b, 1)

a = "                        FlyDeg = angle, FlySpeed = flySpeed, HasFly = hasFly,"
b = "                        FlyDeg = angle, FlySpeed = flySpeed, HasFly = hasFly, FlyLocal = flyLocal,"
assert a in s, "Req assign"
s = s.replace(a, b, 1)

# 4) 消费端：按参考系算全局方向
a = """                            float rad = (float)(r.FlyDeg * Math.PI / 180.0);
                            _movers.Add(new Mover
                            {
                                Ring = b,
                                Pin = false,
                                Dir = new Vector2((float)Math.Cos(rad), (float)Math.Sin(rad)),"""
b = """                            // 参考系：L = 以角色前方为 0（GlobalDirAt 就是干这个的，纹章解放八方向用的同一个 helper）
                            //         S/空 = 屏幕/世界角（崔斯坦用，因为它没有方向性）
                            Vector2 flyDir;
                            if (r.FlyLocal) flyDir = GlobalDirAt(r.Caster, r.FlyDeg);
                            else
                            {
                                float rad = (float)(r.FlyDeg * Math.PI / 180.0);
                                flyDir = new Vector2((float)Math.Cos(rad), (float)Math.Sin(rad));
                            }
                            _movers.Add(new Mover
                            {
                                Ring = b,
                                Pin = false,
                                Dir = flyDir,"""
assert a in s, "consumer dir"
s = s.replace(a, b, 1)

io.open(CS, "w", encoding="utf-8").write(s)

# 5) cfg + 默认值
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
assert n == 1, "StartBullets default"
io.open(PL, "w", encoding="utf-8").write(p2)
print("ok")
