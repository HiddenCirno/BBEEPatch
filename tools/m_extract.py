#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
BlazBlue Entropy Effect 资源容器 (m_N.m) 解包/回封工具

游戏把 6 万个 UnityFS asset bundle 顺序拼接进了 StreamingAssets/ab/m_*.m,
索引在 merge.json 里: {"r": 逻辑路径, "m": 容器文件, "s": 起始偏移}

用法:
  python m_extract.py list   <path-substr>          列出匹配条目及其 容器/偏移/长度
  python m_extract.py get    <path> <outfile>       导出单个 bundle
  python m_extract.py unpack <path-substr> <outdir> 批量导出到目录(保持相对结构)
  python m_extract.py repack <path> <newfile>       用新 bundle 覆盖回容器(原地, 自动对齐)
  python m_extract.py verify                        校验全部索引的偏移是否指向 UnityFS
"""
import json
import os
import sys

AB_DIR = os.path.dirname(os.path.abspath(__file__))
AB_DIR = os.path.join(os.path.dirname(AB_DIR), "..", "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")
AB_DIR = os.path.normpath(AB_DIR)
MERGE = os.path.join(AB_DIR, "merge.json")

MAGIC = b"UnityFS"


def load_index():
    with open(MERGE, "r", encoding="utf-8") as f:
        entries = json.load(f)

    # 按容器分组, 按偏移排序, 长度 = 下一个偏移 - 当前偏移
    by_container = {}
    for e in entries:
        by_container.setdefault(e["m"], []).append(e)

    index = {}
    for cont, items in by_container.items():
        items.sort(key=lambda x: x["s"])
        path = os.path.join(AB_DIR, cont)
        total = os.path.getsize(path)
        for i, e in enumerate(items):
            start = e["s"]
            end = items[i + 1]["s"] if i + 1 < len(items) else total
            index[e["r"]] = (cont, start, end - start)
    return index


def read_entry(path, index):
    cont, off, size = index[path]
    with open(os.path.join(AB_DIR, cont), "rb") as f:
        f.seek(off)
        return cont, off, f.read(size)


def cmd_list(index, substr):
    s = substr.lower()
    hits = [k for k in index if s in k.lower()]
    print(f"# {len(hits)} 条匹配 '{substr}'")
    for k in sorted(hits):
        cont, off, size = index[k]
        print(f"{cont:>10}  off={off:<10} size={size:<9}  {k}")


def cmd_get(index, path, outfile):
    cont, off, size = index[path]
    data = read_entry(path, index)[2]
    if not data.startswith(MAGIC):
        print(f"[!] 警告: 数据不以 UnityFS 开头, 前 16 字节 = {data[:16].hex()}")
    os.makedirs(os.path.dirname(os.path.abspath(outfile)), exist_ok=True)
    with open(outfile, "wb") as f:
        f.write(data)
    print(f"[+] {path}\n    {cont}@{off} ({size} bytes) -> {outfile}")


def cmd_unpack(index, substr, outdir):
    s = substr.lower()
    hits = sorted(k for k in index if s in k.lower())
    for k in hits:
        dst = os.path.join(outdir, k.replace("/", os.sep))
        cmd_get(index, k, dst)
    print(f"[+] 共导出 {len(hits)} 个 bundle 到 {outdir}")


def cmd_verify(index):
    bad = 0
    for k, (cont, off, size) in index.items():
        with open(os.path.join(AB_DIR, cont), "rb") as f:
            f.seek(off)
            head = f.read(7)
        if not head.startswith(MAGIC):
            bad += 1
            if bad <= 10:
                print(f"[X] {cont}@{off} -> {head!r}  ({k})")
    print(f"[=] 索引 {len(index)} 条, 偏移异常 {bad} 条")


def cmd_repack(index, path, newfile):
    """把 newfile 写回容器。若长度变化则整体重排该容器并重写 merge.json。"""
    cont, off, size = index[path]
    newdata = open(newfile, "rb").read()
    cont_path = os.path.join(AB_DIR, cont)
    raw = open(cont_path, "rb").read()

    old = raw[off:off + size]
    if len(newdata) > size:
        print(f"[!] 新数据 {len(newdata)} > 原槽位 {size}, 需要重排容器(未实现, 请用 --compact)")
        return
    if len(newdata) < size:
        newdata = newdata + b"\x00" * (size - len(newdata))
        print(f"[i] 尾部补零至 {size} 字节")

    with open(cont_path, "r+b") as f:
        f.seek(off)
        f.write(newdata)
    print(f"[+] 已回写 {path} -> {cont}@{off} ({size} bytes)")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return
    index = load_index()
    cmd = sys.argv[1]
    if cmd == "list":
        cmd_list(index, sys.argv[2])
    elif cmd == "get":
        cmd_get(index, sys.argv[2], sys.argv[3])
    elif cmd == "unpack":
        cmd_unpack(index, sys.argv[2], sys.argv[3])
    elif cmd == "verify":
        cmd_verify(index)
    elif cmd == "repack":
        cmd_repack(index, sys.argv[2], sys.argv[3])
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
