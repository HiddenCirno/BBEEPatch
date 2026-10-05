# -*- coding: utf-8 -*-
"""修 StartFor 的字段错位：
   StartBullets 的语法是  触发:弹幕:延迟:角度:速度[:参考系]
   我原来多留了一个 AttachBullets 才用的"视觉偏转"槽，导致：
     dashAAendEX:C1:0:0:18:L  ->  18 被吃进废槽、L 落进"速度"槽 -> 解析失败 -> hasFly=false
   => 自推飞行从来没生效过（崔斯坦那 10 条同样如此）。
"""
import io

CS = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Modules\Es\EsEmblemBurst.cs"
s = io.open(CS, encoding="utf-8").read()

a = """            // ⚠ StartBullets 的 `angle` 是【屏幕角度】，**不是** AttachBullets 那个 MoveDeg。
            //   这里刻意【不】设 hasMoveDeg：那是"只转视觉、不产生位移"的老机制，
            //   和自推飞行同时生效会双份处理同一条弹幕（一个转视觉、一个推位置）→ 表现混乱。
            //   所以 StartBullets 只走两条路：写了飞行速度 = 自推飞行；没写 = 只转 dir。
            moveDeg = 0f; hasMoveDeg = false;
            // ★ 第 5 段 = 自推飞行速度（世界单位/秒）。写了就自推飞行，方向用第 4 段的【屏幕角】。
            //   为什么拿"有没有写速度"当开关，而不是拿角度：角度 0 本身就是合法值（向右飞）。
            if (parts.Length > 4 && parts[4].Trim().Length > 0)
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

b = """            // ⚠ StartBullets 的 `angle` 是【屏幕角度】，**不是** AttachBullets 那个 MoveDeg。
            //   这里刻意【不】设 hasMoveDeg：那是"只转视觉、不产生位移"的老机制，
            //   和自推飞行同时生效会双份处理同一条弹幕（一个转视觉、一个推位置）→ 表现混乱。
            //   所以 StartBullets 只走两条路：写了飞行速度 = 自推飞行；没写 = 只转 dir。
            moveDeg = 0f; hasMoveDeg = false;

            // ★★ 字段槽位（2026-10-04 修正 off-by-one）：
            //      触发:弹幕:延迟:角度:速度[:参考系]
            //      parts[0]=弹幕 parts[1]=延迟 parts[2]=角度 parts[3]=速度 parts[4]=参考系
            //   原来多留了一个 AttachBullets 才用的"视觉偏转"槽，于是 `…:0:0:18:L`
            //   的 18 被吃进废槽、L 落进"速度"槽 -> 解析失败 -> hasFly=false
            //   ⇒ **自推飞行从来没生效过**（崔斯坦那 10 条也一样），弹幕全走自己的逻辑。
            if (parts.Length > 3 && parts[3].Trim().Length > 0)
            {
                float sp;
                if (float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out sp) && sp > 0f)
                {
                    hasFly = true; flySpeed = sp;
                }
            }
            // ★ 参考系：L/局部/朝向 = 以角色前方为 0；S/屏幕 或留空 = 屏幕/世界角。
            //   高文有方向性 -> L；崔斯坦没有 -> S(默认)。
            if (parts.Length > 4)
            {
                var fr = parts[4].Trim();
                flyLocal = fr.Equals("L", StringComparison.OrdinalIgnoreCase) ||
                           fr.Equals("local", StringComparison.OrdinalIgnoreCase) ||
                           fr.Equals("局部", StringComparison.Ordinal) ||
                           fr.Equals("朝向", StringComparison.Ordinal);
            }
            return true;"""
assert a in s, "parser block"
s = s.replace(a, b, 1)

# 顺带：飞行生效时打一条"方向参考系"的日志（免得下次又是靠猜）
a = "                        string fp = \"?\"; try { fp = r.Pos.ToString(); } catch { }"
b = ("                        if (r.HasFly)\n"
     "                        {\n"
     "                            string face = \"?\";\n"
     "                            try { face = $\"casterDir={r.Caster?.Dir} fwd={r.Caster?.TransformDirToGlobal(new Fp2((Fp)1f, (Fp)0f))}\"; } catch { }\n"
     "                            Plugin.Log?.LogInfo($\"[纹章接管:追加] 自推飞行 dir=({flyDir.x:F2},{flyDir.y:F2}) \" +\n"
     "                                                $\"参考系={(r.FlyLocal ? \"L 角色朝向\" : \"S 屏幕\")} {face}\");\n"
     "                        }\n"
     "                        string fp = \"?\"; try { fp = r.Pos.ToString(); } catch { }")
assert a in s, "log anchor"
s = s.replace(a, b, 1)

io.open(CS, "w", encoding="utf-8").write(s)
print("ok")
