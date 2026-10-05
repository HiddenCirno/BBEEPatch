# -*- coding: utf-8 -*-
"""
建 **cab 名 → 容器** 索引。离线解析资产的关键一步。

背景：特效 prefab 引用的材质/贴图**不在同一个 bundle 里**，引用形状是
    archive:/CAB-1744663435988cd6cf4c57c3299f6aa0/CAB-1744663435988cd6cf4c57c3299f6aa0
（跨 bundle 按 **cab 名** 引用）。UnityPy 只有在同一个 Environment 里见过那个 cab 才解得开，
否则一律 "(读不到)" —— 这就是"离线看 prefab 说它没有材质"的真正原因。

为什么能快：块信息就在切片头部附近（少数在末尾），而切片起点 merge.json 已经给了
⇒ 每个切片只要两次极小的 seek+read，不用把 10GB 全读一遍。

产物：`_cabindex.json` = { cab名: [容器文件, 切片偏移] }

★ 名字用**正则**抽，不按节点结构解析 —— 节点结构里对齐差一个字节，
  名字就会变成 "AB-4…"（首字母被吃掉）。实测 65065 个名字里有 974 个是这种残缺货，
  害得真正要用的 cab 查不到、材质又读不出来。我们只要"名字 → 切片"，正则最稳。
"""
import json, os, re, struct, sys

BASE = os.path.dirname(os.path.abspath(__file__))
AB = os.path.join(BASE, "..", "..", "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")
OUT = os.path.join(BASE, "_cabindex.json")
IDX = json.load(open(os.path.join(AB, "merge.json"), encoding="utf-8"))

NUL = b"\x00"
RE_CAB = re.compile(rb"[Cc][Aa][Bb]-[0-9a-fA-F]{32}")


def header_fields(hdr):
    """
    → (total, csize, usize, flags, 头结束在本切片内的偏移)

    ⚠ 头部末尾是 `total(8) csize(4) usize(4) flags(4)` ⇒ 头结束在 **p+20**。
      第一版写成 p+16（少 4 字节），于是在"flags 那 4 个字节"上做解压，全部失败 ——
      表现是"扫了 6 万个切片，一个 cab 都没解出来"。**这种全零结果不是"游戏没有"，是解析错了。**
    ⚠ flags 的 0x200 = 块信息前有对齐填充 ⇒ 起点要往后扫，扫到能解压为止。
    """
    p = hdr.index(NUL, 8) + 1
    p += 4
    p = hdr.index(NUL, p) + 1
    p = hdr.index(NUL, p) + 1
    total, csize, usize, flags = struct.unpack_from(">qIII", hdr, p)
    return total, csize, usize, flags, p + 20


def _cabs_of_slice(hdr, tail):
    """从一个切片的头 + 末尾块里，正则抽出所有 cab 名。"""
    import lz4.block
    try:
        total, csize, usize, flags, hend = header_fields(hdr)
    except Exception:
        return []
    comp = flags & 0x3F
    cands = []
    if flags & 0x80:                                  # 块信息在末尾
        if len(tail) >= csize:
            cands.append(tail[:csize])
    for off in range(hend, min(hend + 64, max(0, len(hdr) - csize + 1))):
        cands.append(hdr[off:off + csize])
    for bi in cands:
        if csize <= 0 or len(bi) < csize:
            continue
        try:
            raw = bi if comp == 0 else lz4.block.decompress(bi, uncompressed_size=usize)
        except Exception:
            continue
        got = sorted(set(RE_CAB.findall(raw)))
        if got:
            return got
    return []


def build():
    seen, out, n = set(), {}, 0
    for e in IDX:
        k = (e["m"], e["s"])
        if k in seen:
            continue
        seen.add(k)
        n += 1
        if n % 5000 == 0:
            print("  ...已扫 %d 切片, cab %d" % (n, len(out)), flush=True)
        try:
            with open(os.path.join(AB, e["m"]), "rb") as fh:
                fh.seek(e["s"])
                hdr = fh.read(192)
                if hdr[:8] != b"UnityFS" + NUL:
                    continue
                total = header_fields(hdr)[0]
                tail = b""
                if total > 128:
                    fh.seek(e["s"] + total - 128)
                    tail = fh.read(128)
            for nm in _cabs_of_slice(hdr, tail):
                out.setdefault(nm.decode(), [e["m"], e["s"]])
        except Exception:
            continue
    print("扫了 %d 个切片, cab 名 %d 个" % (n, len(out)))
    return out


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    idx = build()
    json.dump(idx, open(OUT, "w", encoding="utf-8"))
    print("写入", OUT)
    bad = [k for k in idx if not RE_CAB.fullmatch(k.encode())]
    print("残缺名字:", len(bad), bad[:5])
    for k in list(idx)[:3]:
        print("  ", k, idx[k])
