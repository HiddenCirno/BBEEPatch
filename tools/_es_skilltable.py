# -*- coding: utf-8 -*-
"""输出 ES(103401) 的完整动作表，并顺便查 f10 那个 id 在本地化里是什么。

skillactivate 字段（推断）:
  f2 = actorId          f3 = 技能槽/类别   f4 = 序号
  f5 = 内部动作名        f6/f7 = 起手/收招标记  f8 = 输入类型(Skill/Attack/Dash/Jump)
  f10 = 名称/规范 id (varint)   f11 = 参数块   f15 = 段数
  f24 = 帧数据 JSON     f33 = UI 图标路径
"""
import io, os, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
P = os.path.join(BASE, "extracted", "skillactivate.ab.bin")
LOC = os.path.join(BASE, "extracted", "raw", "localization_chs.ab")
OUT = os.path.join(BASE, "tools", "_es_skilltable_out.txt")
M = 0x89ABCDEF


def chash(s):
    h = 0x01234567
    for c in s.encode("utf-8"):
        h = ((h ^ c) * M) & 0xFFFFFFFF
    return (h * M) & 0xFFFFFFFF


d = open(P, "rb").read()


def rv(b, i):
    r = s = 0
    while i < len(b):
        c = b[i]; i += 1
        r |= (c & 0x7F) << s
        if not (c & 0x80):
            break
        s += 7
    return r, i


def fields(b):
    out, i = [], 0
    while i < len(b):
        try:
            k, i = rv(b, i)
        except Exception:
            break
        fn, wt = k >> 3, k & 7
        if wt == 0:
            v, i = rv(b, i); out.append((fn, 'v', v))
        elif wt == 2:
            ln, i = rv(b, i); v = b[i:i + ln]; i += ln
            try:
                s = v.decode('utf-8')
                s = s if all(32 <= ord(c) < 127 for c in s) and s else None
            except UnicodeDecodeError:
                s = None
            out.append((fn, 's', s if s else v))
        elif wt == 5:
            out.append((fn, 'f', struct.unpack_from('<f', b, i)[0])); i += 4
        elif wt == 1:
            i += 8; out.append((fn, 'f', 0.0))
        else:
            break
    return out


rows, i, n = [], 0, len(d)
while i + 4 <= n:
    ln = struct.unpack_from('<I', d, i)[0]
    if ln == 0 or i + 4 + ln > n:
        break
    rows.append(fields(d[i + 4:i + 4 + ln]))
    i += 4 + ln

# 本地化表
d2 = open(LOC, 'rb').read()
loc = {}
j = 0
while j + 8 <= len(d2):
    k, ln = struct.unpack_from("<II", d2, j)
    if ln > 4000 or j + 8 + ln > len(d2):
        j += 1; continue
    try:
        loc.setdefault(k, d2[j + 8:j + 8 + ln].decode("utf-8"))
    except UnicodeDecodeError:
        pass
    j += 8 + ln

es = [r for r in rows if any(fn == 2 and v == 103401 for fn, wt, v in r)]
L = [f"总条目 {len(rows)}, 其中 ES(103401) {len(es)} 条"]

INPUTS = {}
for r in es:
    g = {}
    for fn, wt, v in r:
        g.setdefault(fn, v)
    INPUTS.setdefault(g.get(8, '?'), []).append(g)

for kind in sorted(INPUTS):
    L.append(f"\n########## 输入={kind}  ({len(INPUTS[kind])} 条) ##########")
    for g in INPUTS[kind]:
        name = g.get(5, '?')
        fid = g.get(10)
        named = ""
        if isinstance(fid, int):
            for pre in ("ActorActionName_", "TriggerName_", "PotentialName_"):
                t = loc.get(chash(pre + str(fid)))
                if t:
                    named = f"  [{pre}{fid} = {t[:30]}]"
                    break
        extra = []
        if isinstance(g.get(15), int): extra.append(f"段={g[15]}")
        f24 = g.get(24)
        if isinstance(f24, str): extra.append(f"帧={f24[:40]}")
        L.append(f"  slot={g.get(3)} idx={g.get(4):<3} \"{name}\"{named}  {' '.join(extra)}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT)
