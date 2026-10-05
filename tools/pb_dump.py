#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""通用 protobuf 结构转储器 (无需 .proto)。"""
import os
import sys

WT = {0: "varint", 1: "fixed64", 2: "LEN", 3: "SGROUP", 4: "EGROUP", 5: "fixed32"}


def rv(b, i):
    r = 0
    s = 0
    while True:
        c = b[i]
        i += 1
        r |= (c & 0x7F) << s
        if not (c & 0x80):
            return r, i
        s += 7


def is_printable(b):
    if not b:
        return False
    try:
        s = b.decode("utf-8")
    except UnicodeDecodeError:
        return False
    return sum(1 for c in s if c.isprintable()) / len(s) > 0.9


def dump(b, off, end, depth, out, maxdepth=6):
    ind = "  " * depth
    i = off
    while i < end:
        try:
            key, i = rv(b, i)
        except IndexError:
            return
        if key == 0:
            return
        f, wt = key >> 3, key & 7
        if wt == 0:
            v, i = rv(b, i)
            out.append(f"{ind}#{f} = {v}")
        elif wt == 5:
            v = int.from_bytes(b[i:i + 4], "little"); i += 4
            out.append(f"{ind}#{f} = {v} (f32)")
        elif wt == 1:
            v = int.from_bytes(b[i:i + 8], "little"); i += 8
            out.append(f"{ind}#{f} = {v} (f64)")
        elif wt == 2:
            ln, i = rv(b, i)
            sub = b[i:i + ln]; i += ln
            if is_printable(sub) and len(sub) < 200:
                out.append(f"{ind}#{f} = str({len(sub)}) {sub.decode('utf-8')!r}")
            else:
                out.append(f"{ind}#{f} = blob({len(sub)})")
                if len(sub) < len(b) and depth < maxdepth:
                    try:
                        inner = []
                        dump(sub, 0, len(sub), depth + 1, inner, maxdepth)
                        if len(inner) >= 2:
                            out.extend(inner[:40])
                        else:
                            out.append(f"{'  '*(depth+1)}{sub[:32].hex(' ')}")
                    except Exception:
                        out.append(f"{'  '*(depth+1)}{sub[:32].hex(' ')}")
        else:
            return


def main():
    path = sys.argv[1]
    skip = int(sys.argv[2]) if len(sys.argv) > 2 else 0
    d = open(path, "rb").read()
    out = []
    dump(d, skip, len(d), 0, out)
    print("\n".join(out[:400]))
    print(f"\n[总行数 {len(out)}]")


if __name__ == "__main__":
    main()
