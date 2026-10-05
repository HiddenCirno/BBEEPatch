# -*- coding: utf-8 -*-
"""读 skillactivate 表里 ES(103401) 的冲刺相关技能行 —— 重点是 ActdurStrict / Timeout / Mutelist。

字段号 → 名字来自 dump.cs 里 SkillActivateFixedPoint 的 protobuf 常量:
  f3 Group  f4 Order  f5 Action  f8 Input  f9 InputDir  f15 PreSkillOrder
  f16/17 AllowActive/Passive  f18 AllowGround  f19 AllowFlying
  f21 ActdurStrict  f22 Timeout  f23 TimeoutAddcd  f24 Mutelist
  f25 Preinputtime  f26 UseLongPress  f27/28 LongPressStart/End
  f31 LastingDuration  f32 PrecheckActionCd  f37 BulletId  f51 ActionpointInputTag
"""
import io, os, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
P = os.path.join(BASE, "extracted", "skillactivate.ab.bin")
d = open(P, "rb").read()
Q = 4294967296.0   # Fp: Q32.32

def rv(b, i):
    r = s = 0
    while i < len(b):
        c = b[i]; i += 1
        r |= (c & 0x7F) << s
        if not (c & 0x80): break
        s += 7
    return r, i

def fields(b):
    out, i = [], 0
    while i < len(b):
        try: k, i = rv(b, i)
        except Exception: break
        fn, wt = k >> 3, k & 7
        if wt == 0:
            v, i = rv(b, i); out.append((fn, 'v', v))
        elif wt == 2:
            ln, i = rv(b, i); v = b[i:i+ln]; i += ln; out.append((fn, 'b', v))
        elif wt == 5:
            v = struct.unpack_from("<f", b, i)[0]; i += 4; out.append((fn, 'f', v))
        elif wt == 1:
            v = struct.unpack_from("<d", b, i)[0]; i += 8; out.append((fn, 'd', v))
        else:
            break
    return out

def s(v):
    try: return v.decode("utf-8")
    except Exception: return repr(v)

rows, i, n = [], 0, len(d)
while i + 4 <= n:
    ln = struct.unpack_from("<I", d, i)[0]
    if ln == 0 or ln > 20000 or i + 4 + ln > n:
        i += 1; continue
    fs = fields(d[i+4:i+4+ln])
    g = {}
    for fn, wt, v in fs: g.setdefault(fn, []).append((wt, v))
    def one(fn, dflt=None):
        return g[fn][0][1] if fn in g else dflt
    if one(2) == 103401:
        rows.append((one(3), one(4), s(one(5, b'')), s(one(8, b'')), one(9),
                     one(15), one(18), one(19),
                     one(21, 0), one(22, 0), one(23, 0), s(one(24, b'')),
                     one(25, 0), one(32), one(34), one(35), one(51)))
    i += 4 + ln

print("ES(103401) 技能行 %d 条" % len(rows))
print("Group Order Action                 Input   Dir  PreSkill 地面/空中   ActdurStrict Timeout  +cd    Mutelist                PreInput PreCd auto turn 槽")
for (grp, ord_, act, inp, dir_, pre, gnd, fly, ad, to, tac, mut, pi, pcd, at, td, tag) in rows:
    if not any(k in act for k in ("dash", "Dash", "jump", "attackhold")):
        continue
    print("%5s %5s %-20s %-7s %-4s %-8s %s/%s   %8.3f %8.3f %6.3f  %-22s %7.3f %5s %4s %4s %s" % (
        grp, ord_, act, inp, dir_, pre, gnd, fly,
        ad / Q, to / Q, tac / Q, mut or "-", pi / Q, pcd, at, td, tag))
