# -*- coding: utf-8 -*-
"""
扫一遍所有特效 prefab，找出**哪些是"官方设计成可以换色"的** —— 判据：带 `_RemapColorFrom` 插值器。

为什么用这个判据：离线拆解换色皮肤时发现，皮肤就是靠改 `_RemapColorFrom` 的色相来换色的
（见 PROJECT_STATE §12.9）。所以**带这条属性的特效 = 官方预留的换色点**，
它们才是"换色应该染的东西"；不带的全是我们的白名单在无差别乱染
（`silhouette_601` / `Common.dodge_01_black` 被染红就是这么来的 —— 用户："冲刺影子变了"）。

⚡ 性能：只加载 prefab **自己那一个切片**（插值器值就在 prefab 数据里，不需要依赖图），
   所以每个几十毫秒，能扫几百个 —— 别用 load_deep，那要 1 秒一个。

用法: _recolorscan.py [路径片段]          默认 effect/prefab/role/es
"""
import collections, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _abload import slice_at, IDX   # noqa
import UnityPy

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

WANT = ("_RemapColorFrom", "_RemapColorTo", "_RemapLerp", "_SubTexTintColor")


def props_of(vpath):
    """→ {propName: (type, end值)}（只看这个 prefab 自己的对象）"""
    for e in IDX:
        if e["r"].lower() != vpath.lower():
            continue
        data = slice_at(e["m"], e["s"])
        if not data:
            return None
        env = UnityPy.load(data)
        own = None
        for o in env.objects:
            if o.type.name == "AssetBundle":
                try:
                    if (o.read_typetree().get("m_Name") or "").lower() == vpath.lower():
                        own = o.assets_file
                        break
                except Exception:
                    continue
        if own is None:
            return None
        out = {}
        for o in env.objects:
            if o.type.name != "MonoBehaviour" or o.assets_file is not own:
                continue
            try:
                tt = o.read_typetree()
            except Exception:
                continue
            for r in ((tt.get("references") or {}).get("RefIds") or []):
                d = r.get("data") or {}
                p = d.get("propName")
                if p:
                    out[p] = ((r.get("type") or {}).get("class"), d.get("endValue"))
        return out
    return None


def main():
    key = (sys.argv[1] if len(sys.argv) > 1 else "effect/prefab/role/es").lower()
    cands = [e["r"] for e in IDX if e["r"].lower().startswith(key) and e["r"].lower().endswith(".ab")]
    cands = [c for c in cands if "esskin_" not in c]          # 只看原色那一份
    print("扫 %d 个 prefab ..." % len(cands))
    hits, other = [], collections.Counter()
    for i, c in enumerate(cands):
        if i % 100 == 0:
            print("   ...%d/%d  已命中 %d" % (i, len(cands), len(hits)), flush=True)
        p = props_of(c)
        if not p:
            continue
        for k in p:
            other[k] += 1
        if any(w in p for w in WANT):
            hits.append((c, p))
    print("\n=== 带 remap/副贴图着色（官方换色点）的 %d 个 ===" % len(hits))
    for c, p in hits:
        rel = [k for k in p if k in WANT]
        print("  %-58s %s" % (c, {k: p[k][1] for k in rel}))
    print("\n=== 全部属性出现次数（前 20）===")
    for k, v in other.most_common(20):
        print("   %-24s %d" % (k, v))


if __name__ == "__main__":
    main()
