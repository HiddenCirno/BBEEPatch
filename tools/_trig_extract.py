# -*- coding: utf-8 -*-
"""抽出 ES(103401) 各技能行里的【触发器】字段。

字段号(来自 dump.cs 的 FieldNumber 常量):
    2=ActorId  3=Group  4=Order  5=Action  6=StartTrigger  7=ExitTrigger
    8=Input    9=InputDir  10=ReqTriggerId  15=PreSkillOrder

想知道的事: 平A 行 vs 布鲁诺 行的 StartTrigger/ExitTrigger/ReqTriggerId 差别 ——
如果"原版能接、嫁接接不上"的根因是触发器握手, 这里就是证据。
"""
import io, os, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
data = open(os.path.join(BASE, "extracted", "skillactivatefixedpoint.ab.bin"), "rb").read()
OUT = os.path.join(BASE, "tools", "_trig_extract_out.txt")


def rdvar(b, i):
    """读一个 varint, 返回 (值, 新下标)"""
    r = 0
    s = 0
    while i < len(b):
        c = b[i]
        i += 1
        r |= (c & 0x7F) << s
        if not (c & 0x80):
            return r, i
        s += 7
        if s > 63:
            break
    return None, i


def parse(b):
    """解析一条 SkillActivateFixedPoint 消息 → dict"""
    f = {}
    i = 0
    while i < len(b):
        tag, i = rdvar(b, i)
        if tag is None:
            break
        fn, wt = tag >> 3, tag & 7
        if wt == 0:
            v, i = rdvar(b, i)
            f.setdefault(fn, []).append(v)
        elif wt == 2:
            ln, i = rdvar(b, i)
            if ln is None or i + ln > len(b):
                break
            f.setdefault(fn, []).append(b[i:i + ln])
            i += ln
        elif wt == 5:
            i += 4
        elif wt == 1:
            i += 8
        else:
            break
    return f


def s(f, n):
    v = f.get(n)
    if not v:
        return ""
    x = v[0]
    if isinstance(x, bytes):
        try:
            return x.decode("utf-8")
        except Exception:
            return repr(x)
    return str(x)


def ilist(f, n):
    """repeated int —— 可能是 packed 也可能是逐个 varint"""
    out = []
    for x in f.get(n, []):
        if isinstance(x, bytes):
            j = 0
            while j < len(x):
                v, j = rdvar(x, j)
                if v is None:
                    break
                out.append(v)
        else:
            out.append(x)
    return out


def slist(f, n):
    return [x.decode("utf-8", "replace") for x in f.get(n, []) if isinstance(x, bytes)]


rows = []
for i in range(len(data) - 1):
    if data[i] != 0x10:            # field 2 (ActorId), varint  → 0x10
        continue
    try:
        val, j = rdvar(data, i + 1)
    except Exception:
        continue
    if val != 103401:
        continue
    # 以这个字段为起点向前找消息头不可靠; 改为从 i 起解析一小段, 再看能否读出 action
    msg = parse(data[i:i + 400])
    a = s(msg, 5)
    if not a:
        continue
    rows.append((ilist(msg, 3), ilist(msg, 4), a, slist(msg, 6), slist(msg, 7),
                 ilist(msg, 10), ilist(msg, 15)))

# 去重(同一个起点可能重复命中)
seen = set()
L = []
L.append("group order action                      StartTrigger        ExitTrigger        ReqTriggerId  preSkillOrder")
for g, o, a, st, et, rq, pre in rows:
    key = (tuple(g), tuple(o), a)
    if key in seen:
        continue
    seen.add(key)
    L.append(f"{(g or ['?'])[0]:>5} {(o or [0])[0]:>5} {a:<26} "
             f"{str(st):<19} {str(et):<18} {str(rq):<13} {pre}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print(f"ES 行数 {len(seen)} -> tools/_trig_extract_out.txt")
print("\n".join(L[:40]))
