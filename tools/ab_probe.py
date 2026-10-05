#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""探查 UnityFS bundle 内部对象; 支持导出 TextAsset。"""
import sys
import os
import UnityPy


def probe(path, dump_dir=None):
    env = UnityPy.load(path)
    print(f"=== {path} ===")
    for i, obj in enumerate(env.objects):
        t = obj.type.name
        size = len(obj.get_raw_data())
        name = ""
        try:
            if t in ("TextAsset", "MonoBehaviour", "AssetBundle"):
                d = obj.read()
                name = getattr(d, "m_Name", None) or getattr(d, "name", "") or ""
        except Exception as e:
            name = f"<read err {e}>"
        print(f"  [{i:>3}] {t:<16} {size:>9}B  {name}")
        if dump_dir and t == "TextAsset":
            data = obj.read()
            raw = data.m_Script
            if isinstance(raw, str):
                raw = raw.encode("utf-8", "surrogateescape")
            out = os.path.join(dump_dir, f"{i:03d}_{(name or 'text').replace('/', '_')}.txt")
            os.makedirs(dump_dir, exist_ok=True)
            with open(out, "wb") as f:
                f.write(raw)
            print(f"        -> {out} ({len(raw)}B) head={raw[:60]!r}")


if __name__ == "__main__":
    probe(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
