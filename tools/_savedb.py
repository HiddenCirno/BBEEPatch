# -*- coding: utf-8 -*-
"""把存档里的【数据体】(FesActor) 解出来对比 —— 潜能 / 传承 / 策略 / 分数。

结构（`_pbdef.py` 从 pbdef.js 反推 + `_savedump.py` 存档实测）：
    存档 = LZ4 frame(protobuf)
      └ #2 重复 { #1 模型名, #2 负载 }
    ModelPlayerNewFesActorPack 的负载
      └ #1(一条) → 里面是【每条一个 #1】= map<uid, STFesActor>   ← 数据体本体（实测 18 条）
    STFesActor（字段号来自 pbdef.js 的 encode 体）：
      #1 uid  #2 id(角色)  #16 score(double!)
      #15 potentials : STPotential[]   {#1 id, #3 active, #5 order}      ← 潜能
      #17 talents    : STTalent[]      {#1 id, #2 quality, #3 inheritNum, #4 fromActorId} ← 传承
      #26 skills     : STActiveSkill[] {#1 id, #2 level, #3 fromActorId} ← 策略
      #8  blesses    : STBless[]                                          ← 祝福
      #36/#37 inheritSkillIndex/Level
    ⚠ score 是 **double**(fixed64)，用 varint 读会得 0 —— 踩过。

用法:  python _savedb.py [角色id]         # 默认 103401 = ES
"""
import io, os, struct, sys
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _savedump import load, fields, printable

S = os.path.expandvars(r"%USERPROFILE%\AppData\LocalLow\91Act\BlazBlueEntropyEffect\Steam\366115783")
ACTOR = int(sys.argv[1]) if len(sys.argv) > 1 else 103401


def dbl(b):
    return struct.unpack("<d", b[:8])[0]


def num(b, want, d=0):
    for f, t, v in fields(b):
        if f == want and t == "v":
            return v
    return d


def score_of(b):
    for f, t, v in fields(b):
        if f == 16 and t == "f64":
            return dbl(v)
    return 0.0


def sub(b, want):
    return [v for f, t, v in fields(b) if f == want and t == "s"]


def load_actors():
    data, _ = load(os.path.join(S, "Save", "1"))
    pack = None
    for f, t, v in fields(data):
        if f == 2 and t == "s":
            s = fields(v)
            if next((printable(x[2]) for x in s if x[0] == 1 and x[1] == "s"), "") == "ModelPlayerNewFesActorPack":
                pack = next((x[2] for x in s if x[0] == 2 and x[1] == "s"), b"")
    body = sub(pack, 1)[0]
    out = []
    for e in sub(body, 1):
        uid = num(e, 1)
        val = sub(e, 2)
        if not val:
            continue
        a = val[0]
        out.append(dict(
            uid=uid, id=num(a, 2), score=score_of(a),
            pot=[(num(p, 1), num(p, 3), num(p, 5)) for p in sub(a, 15)],
            tal=[(num(t2, 1), num(t2, 2), num(t2, 3), num(t2, 4)) for t2 in sub(a, 17)],
            skl=[(num(s2, 1), num(s2, 2), num(s2, 3)) for s2 in sub(a, 26)],
            bls=[(num(b2, 1), num(b2, 2)) for b2 in sub(a, 8)],
            isi=num(a, 36), isl=num(a, 37),
        ))
    return out


if __name__ == "__main__":
    rows = load_actors()
    mine = [r for r in rows if r["id"] == ACTOR]
    print("数据体总数 %d, 其中角色 %d 的 %d 个\n" % (len(rows), ACTOR, len(mine)))
    print("%-5s %-9s %-6s %-6s %-6s %-6s %-7s %s" %
          ("uid", "score", "潜能", "传承", "策略", "祝福", "传技/级", "潜能有开启的"))
    for r in sorted(mine, key=lambda x: -x["score"]):
        act = [p[0] for p in r["pot"] if p[1]]
        print("%-5s %-9.1f %-6d %-6d %-6d %-6d %-7s %s" %
              (r["uid"], r["score"], len(r["pot"]), len(r["tal"]), len(r["skl"]), len(r["bls"]),
               "%d/%d" % (r["isi"], r["isl"]), ",".join(str(x) for x in sorted(act))[:60]))
    print()
    for r in sorted(mine, key=lambda x: -x["score"]):
        print("=" * 92)
        print("uid=%s  score=%.1f  潜能%d 传承%d 策略%d 祝福%d  传技=%d 传级=%d" %
              (r["uid"], r["score"], len(r["pot"]), len(r["tal"]), len(r["skl"]), len(r["bls"]), r["isi"], r["isl"]))
        print("  潜能(id:启用):", ", ".join("%d%s" % (p[0], "*" if p[1] else "") for p in sorted(r["pot"])))
        print("  传承(id/品质/继承数/来自):", ", ".join("%d/%d/%d/%d" % t for t in sorted(r["tal"])) or "无")
        print("  策略(id/等级/来自):", ", ".join("%d/%d/%d" % s for s in sorted(r["skl"])) or "无")
        print("  祝福(id/品质):", ", ".join("%d/%d" % b for b in sorted(r["bls"])) or "无")
