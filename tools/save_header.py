#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用多份存档样本推断 Save 文件 21 字节头的结构 (校验和假设检验)。"""
import glob
import hashlib
import os
import struct
import zlib

ROOT = os.path.expandvars(r"%USERPROFILE%\AppData\LocalLow\91Act\BlazBlueEntropyEffect\Steam\366115783")


def collect():
    paths = [os.path.join(ROOT, "Save", "1"),
             os.path.join(ROOT, "Save", "Backup", "1"),
             os.path.join(ROOT, "Backup", "1")]
    paths += sorted(glob.glob(os.path.join(ROOT, "ColdBackup", "1", "*")))
    out = []
    for p in paths:
        if not os.path.exists(p):
            continue
        d = open(p, "rb").read()
        out.append((os.path.basename(os.path.dirname(p)) + "/" + os.path.basename(p), d))
    return out


def fnv1a32(b):
    h = 0x811C9DC5
    for c in b:
        h ^= c
        h = (h * 0x01000193) & 0xFFFFFFFF
    return h


def fnv1a64(b):
    h = 0xCBF29CE484222325
    for c in b:
        h ^= c
        h = (h * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
    return h


HASHES = {
    "crc32LE": lambda b: struct.pack("<I", zlib.crc32(b) & 0xFFFFFFFF),
    "crc32BE": lambda b: struct.pack(">I", zlib.crc32(b) & 0xFFFFFFFF),
    "adler32": lambda b: struct.pack(">I", zlib.adler32(b) & 0xFFFFFFFF),
    "md5": lambda b: hashlib.md5(b).digest(),
    "sha1": lambda b: hashlib.sha1(b).digest(),
    "sha256": lambda b: hashlib.sha256(b).digest(),
    "fnv1a32": lambda b: struct.pack("<I", fnv1a32(b)),
    "fnv1a64": lambda b: struct.pack("<Q", fnv1a64(b)),
}


def main():
    files = collect()
    print(f"{len(files)} 个存档样本\n")
    print(f"{'name':<26} {'hdr':<45} {'len':>7}")
    for n, d in files:
        print(f"{n:<26} {d[:21].hex(' '):<45} {len(d):>7}")

    # 目标: 找出哪 2 字节字段等于某个哈希的某个切片
    fields = {"hdr6_8": slice(6, 8), "hdr14_18": slice(14, 18), "hdr15_19": slice(15, 19),
              "hdr18_21": slice(18, 21), "hdr6_14": slice(6, 14)}
    print("\n--- 校验和匹配检验 (跨全部样本一致才算命中) ---")
    for fname, sl in fields.items():
        want = [d[sl] for _, d in files]
        if len(set(want)) == 1:
            print(f"  {fname}: 全部相同 = {want[0].hex()} (常量字段)")
    for fname, sl in fields.items():
        for hname, hf in HASHES.items():
            for bodyoff in (0, 21):
                for skip in range(0, 4):
                    ok = True
                    for _, d in files:
                        h = hf(d[bodyoff:])
                        if h[skip:skip + (sl.stop - sl.start)] != d[sl]:
                            ok = False
                            break
                    if ok:
                        print(f"  [!] 命中: {fname} == {hname}[{skip}:] over {'body' if bodyoff else 'whole'}")


if __name__ == "__main__":
    main()
