#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
NOAH 资产混淆编解码器
完全逆向自 GameAssembly.dll:
  NOAH.Asset.AssetManagerBase.GetBytes(TextAsset)   RVA 0x34F8090
  NOAH.Asset.AssetManagerBase.DecodeBytes           RVA 0x34F71C0
  NOAH.Asset.AssetManagerBase.ObfuscateBytes        RVA 0x34F9300
  StringExtensions.CalculateHash                    RVA 0x3149320
  NOAH.Core.IOUtil.MatchMagic / .cctor              RVA 0x34FFDE0 / 0x34FFFE0

调用链:
    keyHash = CalculateHash(asset.name)
    if (keyHash != 0 && MatchMagic(bytes)) bytes = ObfuscateBytes(RemoveMagic(bytes), keyHash)

混淆算法 (旋转 4 字节 XOR):
    key    = keyHash 的小端 4 字节
    accum  = -1
    for i in 0 .. n-1:
        if i % 4 == 0: accum += 1
        data[i] ^= key[(accum + i) % 4]        # 等价 key[(i + i//4) % 4]

对合运算, 加解密同一个函数。
"""
import os
import sys

MAGIC = b"NOAH"
MUL = 0x89ABCDEF
SEED = 0x01234567


def calculate_hash(name: str) -> int:
    """StringExtensions.CalculateHash(string) — 取每字符低字节。"""
    h = SEED
    for ch in name:
        h = ((h ^ (ord(ch) & 0xFF)) * MUL) & 0xFFFFFFFF
    return (h * MUL) & 0xFFFFFFFF


def obfuscate(data: bytes, key_hash: int) -> bytes:
    key = bytes([(key_hash >> (8 * i)) & 0xFF for i in range(4)])
    out = bytearray(data)
    accum = -1
    for i in range(len(out)):
        if i % 4 == 0:
            accum += 1
        out[i] ^= key[(accum + i) & 3]
    return bytes(out)


def decode(data: bytes, name: str) -> bytes:
    """按资产名解密。

    严格对齐游戏 DecodeBytes 的行为:
        if (keyHash == 0 || !MatchMagic(data)) return data;   // 没有 NOAH 头就原样返回
    """
    if not data.startswith(MAGIC):
        return data
    kh = calculate_hash(name)
    body = data[4:]
    return obfuscate(body, kh) if kh else body


def encode(payload: bytes, name: str, with_magic: bool = True) -> bytes:
    """回封: 加密并按需加回 NOAH magic。"""
    kh = calculate_hash(name)
    body = obfuscate(payload, kh) if kh else payload
    return (MAGIC + body) if with_magic else body


# --------------------------------------------------------------------------
if __name__ == "__main__":
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""

    if cmd == "hash":
        for n in sys.argv[2:]:
            print(f"0x{calculate_hash(n):08X}  {n}")

    elif cmd == "dec":            # dec <资产名> <密文文件> [输出]
        name, src = sys.argv[2], sys.argv[3]
        out = decode(open(src, "rb").read(), name)
        dst = sys.argv[4] if len(sys.argv) > 4 else src + ".dec"
        open(dst, "wb").write(out)
        print(f"[+] {name} -> {dst} ({len(out)} B)")
        print(out[:300].decode("utf-8", "replace"))

    elif cmd == "enc":            # enc <资产名> <明文文件> [输出]
        name, src = sys.argv[2], sys.argv[3]
        out = encode(open(src, "rb").read(), name)
        dst = sys.argv[4] if len(sys.argv) > 4 else src + ".enc"
        open(dst, "wb").write(out)
        print(f"[+] {name} -> {dst} ({len(out)} B)")

    else:
        print(__doc__)
