#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""读取 data/xlsx/*.ab 配置表 (明文 protobuf, 未被 NOAH 混淆)。

格式
----
    连续的 [uint32 小端长度][protobuf 记录]
    每条记录本身是一个 protobuf 消息, 字段号即列号。

用法
----
    # 列出容器里有哪些 xlsx
    uv run python tools/xlsx_dump.py list [关键词]

    # 导出并解析某张表 (打印字段统计 + 前 N 条)
    uv run python tools/xlsx_dump.py dump <表名, 如 baseactorpotentialconf> [条数]

    # 把表里所有整数/嵌套整数收集起来, 反查本地化名字
    # (这是定位「某个名字对应哪一行」的关键手段 —— 盲目猜 id 范围是猜不到的)
    uv run python tools/xlsx_dump.py names <表名> [名字过滤]

依赖: uv run --with lz4 --with UnityPy python tools/xlsx_dump.py ...
"""
import os
import re
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

EXTRACTED = os.path.join(os.path.dirname(HERE), "extracted")

# 本地化里已知的键前缀
PREFIXES = [
    "ActorActionName_", "ActorActionDesc_", "TriggerName_", "TriggerDesc_",
    "PotentialName_", "PotentialDesc_", "ActorSkill_", "ActorSkillDesc_",
    "TalentName_", "TalentDesc_", "BuffName_", "BuffDesc_",
    "ActorAbility_", "ActorUltra_", "ActorUltraDesc_", "InheritSkill_",
]


def read_varint(b, i):
    r = s = 0
    while True:
        x = b[i]
        i += 1
        r |= (x & 0x7F) << s
        s += 7
        if not x & 0x80:
            return r, i


def parse_records(data):
    """[uint32 len][msg] 循环 -> [bytes, ...]"""
    out = []
    i = 0
    while i + 4 <= len(data):
        n = struct.unpack_from("<I", data, i)[0]
        if n == 0 or i + 4 + n > len(data):
            break
        out.append(data[i + 4:i + 4 + n])
        i += 4 + n
    return out


def parse_msg(m):
    """protobuf 消息 -> {字段号: [值, ...]}; LEN 字段的值是 bytes。"""
    out = {}
    i = 0
    while i < len(m):
        try:
            k, i = read_varint(m, i)
        except Exception:
            break
        f, w = k >> 3, k & 7
        if f == 0:
            break
        try:
            if w == 0:
                v, i = read_varint(m, i)
                out.setdefault(f, []).append(v)
            elif w == 2:
                ln, i = read_varint(m, i)
                out.setdefault(f, []).append(m[i:i + ln])
                i += ln
            elif w == 5:
                out.setdefault(f, []).append(m[i:i + 4]); i += 4
            elif w == 1:
                out.setdefault(f, []).append(m[i:i + 8]); i += 8
            else:
                break
        except Exception:
            break
    return out


def msg_all_ints(m):
    """递归收集一条记录里出现的所有整数 (含嵌套消息里的)。"""
    found = set()

    def walk(msg, depth=0):
        if depth > 4:
            return
        for f, vals in parse_msg(msg).items():
            for v in vals:
                if isinstance(v, int):
                    found.add(v)
                elif isinstance(v, (bytes, bytearray)) and 0 < len(v) <= 8:
                    try:
                        found.add(read_varint(v, 0)[0])
                    except Exception:
                        pass
                    walk(v, depth + 1)

    walk(m)
    return found


def load_table(name):
    """提取并解码一张 xlsx 表, 返回记录列表。"""
    ab = os.path.join(EXTRACTED, name + ".ab")
    if not os.path.exists(ab):
        subprocess.run([sys.executable, os.path.join(HERE, "m_extract.py"),
                        "get", "data/xlsx/%s.ab" % name, ab], check=True)
    import UnityPy
    env = UnityPy.load(ab)
    for o in env.objects:
        if o.type.name == "TextAsset":
            d = o.read()
            raw = d.m_Script.encode("utf-8", "surrogateescape")
            return parse_records(raw)
    return []


def cmd_list(kw=None):
    out = subprocess.run([sys.executable, os.path.join(HERE, "m_extract.py"), "list", "data/xlsx/"],
                         capture_output=True, text=True, encoding="utf-8", errors="replace").stdout
    for line in out.splitlines():
        if kw and kw.lower() not in line.lower():
            continue
        print(line)


def cmd_dump(name, n=10):
    recs = load_table(name)
    print("# %s : %d 条记录" % (name, len(recs)))
    for idx, r in enumerate(recs[:n]):
        f = parse_msg(r)
        print("\n--- #%d (%d bytes) ---" % (idx, len(r)))
        for g, vals in sorted(f.items()):
            show = []
            for v in vals:
                show.append(v if isinstance(v, int) else "len%d:%s" % (len(v), v[:12].hex()))
            print("   f%-3d %s" % (g, show))
    return recs


def cmd_names(name, filt=None):
    """把表里所有整数拿去反查本地化, 打印命中的名字。"""
    import locale as L
    recs = load_table(name)
    ids = set()
    for r in recs:
        ids |= msg_all_ints(r)
    print("# %s : %d 条记录, 收集到 %d 个整数 id" % (name, len(recs), len(ids)))
    for i in sorted(ids):
        for p in PREFIXES:
            nm = L.get(p + str(i))
            if nm and (not filt or filt in nm):
                print("   %-6d %s%s = %s" % (i, p, i, nm))


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    cmd = sys.argv[1]
    if cmd == "list":
        cmd_list(sys.argv[2] if len(sys.argv) > 2 else None)
    elif cmd == "dump":
        cmd_dump(sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 10)
    elif cmd == "names":
        cmd_names(sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)
    else:
        print(__doc__)
