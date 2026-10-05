# -*- coding: utf-8 -*-
"""读潜能配置表 `data/xlsx/baseactorpotentialconf.ab`（按 id 查）。

格式（实测）：TextAsset 里是一串 `<u32 小端长度><protobuf BaseActorPotentialConf>`。
字段名来自 `_pbdef.py`（从 pbdef.js 的 encode 体反推）：
    #1 id  #2 rank  #5 skillGroup  #6 score  #7 triggerInfo  #9 isUltraSkill
    #10 addDamageAttack #11 addDamageSkill #12 addDamageJump #13 addDamageDash
    #14 skillCost  #17 actorId  #19 iconId  #24 isDamageSkill

用法:
    python _potdb.py <id> [更多 id...]
    python _potdb.py --actor 103401
"""
import io, os, struct, sys
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _prefdiff as P
from _savedump import fields


def load():
    data = P.bundle_bytes("data/xlsx/baseactorpotentialconf.ab")
    import UnityPy
    env = UnityPy.load(data)
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        d = obj.read()
        b = d.m_Script.encode("utf-8", "surrogateescape")
        out, off = [], 0
        while off + 4 <= len(b):
            ln = struct.unpack_from("<I", b, off)[0]
            off += 4
            if ln == 0 or off + ln > len(b):
                break
            out.append(b[off:off + ln])
            off += ln
        return out
    return []


def num(b, want, d=0):
    for f, t, v in fields(b):
        if f == want and t == "v":
            return v
    return d


def flt(b, want, d=0.0):
    for f, t, v in fields(b):
        if f == want and t == "f32":
            return struct.unpack("<f", v)[0]
    return d


def desc(b):
    return ("id=%-6d rank=%-2d 组=%-3d score=%-4d 超大=%d 伤害技=%d " +
            "攻%.2f 技%.2f 跳%.2f 冲%.2f 耗能=%-3d icon=%-6d actor=%d") % (
        num(b, 1), num(b, 2), num(b, 5), num(b, 6), num(b, 9), num(b, 24),
        flt(b, 10), flt(b, 11), flt(b, 12), flt(b, 13), num(b, 14), num(b, 19), num(b, 17))


if __name__ == "__main__":
    recs = load()
    byid = {num(r, 1): r for r in recs}
    print("潜能表条数:", len(recs))
    if len(sys.argv) > 1 and sys.argv[1] == "--actor":
        aid = int(sys.argv[2])
        for i in sorted(byid):
            if num(byid[i], 17) == aid:
                print("  ", desc(byid[i]))
    else:
        for a in sys.argv[1:]:
            i = int(a)
            r = byid.get(i)
            print("  %-6s %s" % (i, desc(r) if r else "**表里没有这个 id**"))
