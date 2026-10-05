# -*- coding: utf-8 -*-
"""
离线资产加载器（共享模块）—— 解决"cab-xxx not found"。

背景（踩了两轮才搞明白）：
  `merge.json` 给的是 **容器文件 + 偏移**，而一个容器文件里是**几十个 bundle 首尾相接**
  （实测 m_878.m = 64 个切片，正好铺满 8.44 MB）。
  只按偏移切出**一个**切片去 `UnityPy.load()`，资产之间的 `cab-xxx` 交叉引用就找不到
  ⇒ 材质/贴图一律读成 "(读不到)"，很容易被误读成"这资产里没材质"。

正确做法：把**整个容器的所有切片**一起喂给 UnityPy（`UnityPy.load(*slices)`），
  它们共享一个 Environment，cab 就能按名字互找。
"""
import json, os, struct, sys
import UnityPy

BASE = os.path.dirname(os.path.abspath(__file__))
GAME = os.path.join(BASE, "..", "..")
AB = os.path.join(GAME, "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")
IDX = json.load(open(os.path.join(AB, "merge.json"), encoding="utf-8"))
BY = {e["r"].lower(): e for e in IDX}
_CACHE = {}


def _slice_total(d, off):
    if d[off:off + 8] != b"UnityFS\x00":
        return None
    b = d[off:off + 256]
    p = b.index(b"\x00", 8) + 1
    p += 4
    p = b.index(b"\x00", p) + 1
    p = b.index(b"\x00", p) + 1
    return struct.unpack_from(">q", b, p)[0]


def slice_at(container, off):
    """只切出容器里**指定偏移的那一个切片**（bundle）。
    依赖解析时用这个 —— 加载整个容器会把几十个无关 bundle 全拖进来
    （实测一个特效就能膨胀到 48 万对象、25 秒），既慢又淹掉结果。"""
    d = open(os.path.join(AB, container), "rb").read()
    t = _slice_total(d, off)
    return d[off:off + t] if t else None


def slices(container):
    d = open(os.path.join(AB, container), "rb").read()
    out, off = [], 0
    while off + 32 < len(d):
        t = _slice_total(d, off)
        if not t or t <= 0 or off + t > len(d):
            break
        out.append(d[off:off + t])
        off += t
    return out


def load(vpath):
    """只加载该资产所在容器的切片（引用到别的 bundle 时仍会读不到 —— 用 load_deep）。"""
    key = vpath.lower()
    if key in _CACHE:
        return _CACHE[key]
    e = BY.get(key)
    env = None
    if e is not None:
        env = UnityPy.load(*slices(e["m"]))
    _CACHE[key] = env
    return env


_CABIDX = None


def cab_index():
    """cab 名 → (容器文件, 切片偏移)。由 `_cabindex.py` 生成。"""
    global _CABIDX
    if _CABIDX is None:
        p = os.path.join(BASE, "_cabindex.json")
        _CABIDX = json.load(open(p, encoding="utf-8")) if os.path.exists(p) else {}
    return _CABIDX


def _externals(env):
    """
    收集 env 里全部 SerializedFile 的外部依赖（跨 bundle 的 cab 名）。

    ⚠ 形状：`env.files` 的值是 **BundleFile**，SerializedFile 在它的 `.files` 里，
      externals 又是 `FileIdentifier(archive:/CAB-x/CAB-x)` 这种对象。
      只遍历 env.files 会一个都拿不到（表现就是"没有任何外部依赖" → 于是 material 全读不到）。
      CAB 名 = 路径最后一段。
    """
    out = set()
    for bf in getattr(env, "files", {}).values():
        inner = getattr(bf, "files", None)
        it = inner.values() if hasattr(inner, "values") else (inner or [])
        for sf in it:
            for x in (getattr(sf, "externals", None) or []):
                s = None
                if isinstance(x, str):
                    s = x
                else:
                    for attr in ("path", "name", "fileName"):
                        s = getattr(x, attr, None)
                        if s:
                            break
                    if s is None and isinstance(x, (list, tuple)) and x:
                        s = x[0]
                if s:
                    out.add(str(s).replace("\\", "/").rstrip("/").split("/")[-1])
    return out


def load_deep(vpath, rounds=5, max_containers=60):
    """
    ★ 递归加载依赖 —— **这是能真正读到材质/贴图的关键**。

    为什么：特效 prefab 引用的材质/贴图**不在同一个 bundle 里**
    （实测 `es_dodge_01` 的容器里只有地图/陷阱那些无关资产，
      而材质 `streak_024_b` 在 `effect/common/material/15_streak/…` 那个容器里）。
    引用形状是 `archive:/CAB-<hex>/CAB-<hex>` ⇒ 用 `_cabindex.json`（cab 名 → 容器）反查，
    把那些容器也载进来，PPtr 才解得开。
    只加载 prefab 自己的容器 ⇒ 所有 `m_Materials` 都读成 "(读不到)"，
    很容易被误读成"这个特效没有材质"。
    """
    key = vpath.lower()
    ck = "deep|" + key
    if ck in _CACHE:
        return _CACHE[ck]

    idx = cab_index()
    e0 = BY.get(key)
    if e0 is None:
        _CACHE[ck] = None
        return None

    # BFS：每轮把上一轮解出来的外部 cab 反查成容器，把它的切片加进来，直到不再有新依赖。
    blob, seen = [], {(e0["m"], e0["s"])}
    blob += slices(e0["m"])
    env = None
    for _ in range(rounds):
        env = UnityPy.load(*blob)
        added = 0
        for cab in _externals(env):
            hit = idx.get(cab) or idx.get(cab.upper()) or idx.get(cab.lower())
            if not hit:
                continue
            k = (hit[0], hit[1])
            if k in seen:
                continue
            seen.add(k)
            one = slice_at(hit[0], hit[1])
            if one:
                blob.append(one)
            added += 1
            if len(seen) > max_containers:
                break
        if added == 0 or len(seen) > max_containers:
            break
    env = UnityPy.load(*blob)
    _CACHE[ck] = env
    return env


def find(key, limit=30):
    return [e["r"] for e in IDX if key.lower() in e["r"].lower()][:limit]


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    print("容器样例 m_878.m 切片数:", len(slices("m_878.m")))
    for vp in sys.argv[1:]:
        env = load(vp)
        if env is None:
            print("%s: 找不到/读不到" % vp); continue
        n = len(list(env.objects))
        print("%s: 对象 %d" % (vp, n))


if __name__ == "__main__":
    main()
