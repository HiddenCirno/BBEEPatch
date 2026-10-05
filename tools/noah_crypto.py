#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""分析 NOAH 加密格式: 周期检测 + 已知明文攻击。"""
import os
import sys
import collections

HERE = os.path.dirname(os.path.abspath(__file__))
BASE = os.path.normpath(os.path.join(HERE, ".."))

SAMPLES = {
    "playerdata": os.path.expandvars(r"%USERPROFILE%\AppData\LocalLow\91Act\BlazBlueEntropyEffect\Steam\366115783\PlayerData"),
    "js": os.path.join(BASE, "extracted", "js_sample_dump", "000_BattleProcess_ExploreOpenTransfer.txt"),
    "gm_js": os.path.join(BASE, "extracted", "gm_dump", "000_GMAddFesActor.txt"),
}


def load(name):
    p = SAMPLES[name]
    if not os.path.exists(p):
        return None
    raw = open(p, "rb").read()
    if raw.startswith(b"NOAH"):
        return raw[:4], raw[4:]
    return None, raw


def entropy(bs):
    if not bs:
        return 0.0
    c = collections.Counter(bs)
    n = len(bs)
    import math
    return -sum((v / n) * math.log2(v / n) for v in c.values())


def period_score(data, p):
    """若周期为 p, 则 data[i]^data[i+p] 应高度集中(明文自相关)。"""
    diff = [data[i] ^ data[i + p] for i in range(len(data) - p)]
    c = collections.Counter(diff)
    top = c.most_common(1)[0]
    return top[1] / len(diff), top[0]


def main():
    for name in SAMPLES:
        magic, body = load(name)
        if body is None:
            print(f"--- {name}: 无样本 ---")
            continue
        print(f"\n=== {name} === magic={magic} len={len(body)} entropy={entropy(body):.3f}")
        print("  头部:", body[:32].hex(" "))
        # 单字节 XOR 检测
        best = None
        for k in range(256):
            dec = bytes(b ^ k for b in body[:256])
            printable = sum(32 <= c < 127 or c in (9, 10, 13) for c in dec) / len(dec)
            if best is None or printable > best[1]:
                best = (k, printable, dec[:40])
        print(f"  最佳单字节 XOR key=0x{best[0]:02x} 可打印率={best[1]:.2f} -> {best[2]!r}")
        # 周期检测
        scores = []
        for p in range(1, 65):
            if p < len(body):
                s, v = period_score(body, p)
                scores.append((s, p, v))
        scores.sort(reverse=True)
        print("  自相关 top8 (score, period, mode):")
        for s, p, v in scores[:8]:
            print(f"    period={p:<4} score={s:.4f} mode=0x{v:02x}")


if __name__ == "__main__":
    main()
