#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""批量解包 data/js/**.ab 并解密 NOAH TextAsset -> 可读 JS 源码树。"""
import json
import os
import sys
import traceback

import UnityPy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from noah_codec import decode, calculate_hash  # noqa: E402

GAME = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect"
AB_DIR = os.path.join(GAME, "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")
OUT = os.path.join(GAME, "_modding", "js_src")


def build_index():
    entries = json.load(open(os.path.join(AB_DIR, "merge.json"), encoding="utf-8"))
    by = {}
    for e in entries:
        by.setdefault(e["m"], []).append(e)
    idx = {}
    for cont, items in by.items():
        items.sort(key=lambda x: x["s"])
        total = os.path.getsize(os.path.join(AB_DIR, cont))
        for i, e in enumerate(items):
            idx[e["r"]] = (cont, e["s"], (items[i + 1]["s"] if i + 1 < len(items) else total) - e["s"])
    return idx


def main(prefix="data/js/"):
    idx = build_index()
    targets = sorted(k for k in idx if k.startswith(prefix))
    print(f"[i] 目标 {len(targets)} 个 bundle", flush=True)
    ok = fail = 0
    for n, key in enumerate(targets):
        cont, off, size = idx[key]
        try:
            with open(os.path.join(AB_DIR, cont), "rb") as f:
                f.seek(off)
                blob = f.read(size)
            env = UnityPy.load(blob)
            for obj in env.objects:
                if obj.type.name != "TextAsset":
                    continue
                ta = obj.read()
                raw = ta.m_Script
                if isinstance(raw, str):
                    raw = raw.encode("utf-8", "surrogateescape")
                name = ta.m_Name
                js = decode(raw, name)
                dst = os.path.join(OUT, key[len(prefix):])
                if not dst.endswith(".js"):
                    dst = os.path.splitext(dst)[0] + ".js"
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                with open(dst, "wb") as f:
                    f.write(js)
                ok += 1
        except Exception:
            fail += 1
            if fail <= 5:
                print(f"[!] {key}: {traceback.format_exc().splitlines()[-1]}", flush=True)
        if (n + 1) % 200 == 0:
            print(f"    {n+1}/{len(targets)} ...", flush=True)
    print(f"[=] 完成: 成功 {ok}, 失败 {fail} -> {OUT}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "data/js/")
