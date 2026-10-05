# -*- coding: utf-8 -*-
"""
**把全部 ES 换色特效拆一遍**：原色 vs 每套皮肤的专属副本，逐个比"皮肤到底改了什么"。

为什么是"全部"而不是逐个猜：这个项目在"目标定位"上反复栽跟头（`_AddColor` 当电弧、
影子当电弧、es_dash_01 当 MP 冲刺），根因都是**只知道局部**。皮肤的换色点本身是有客观答案的——
**哪个特效的副本和原色不一样，皮肤就改了哪个**。把它一次性列全，就不用再猜了。

只加载 prefab 自己那一个切片（插值器值就在 prefab 数据里），所以快。
比三类差异：
  ① 插值器：新增 / 删除 / 值变了（propName + start/end）
  ② 渲染器引用的材质：换了没有（名字对比）
  ③ 对象：多了/少了哪些（含 added 的 MaterialTinter/MaterialCollector）

用法: _skindiff_all.py [皮肤名...]        默认 4 套皮肤全跑
"""
import collections, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _abload import slice_at, IDX
import UnityPy

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
SKINS = ["esskin_06", "esskin_10", "esskin_12", "esskin_13"]
BASE_DIR = "effect/prefab/role/es/"
_VALS = {}   # (皮肤, 特效, 插值器键) -> (start, end, dur)


def loads(vpath):
    """→ (插值器字典, 渲染器材质名列表, 对象键集合)"""
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
        names = {}
        for o in env.objects:
            if o.type.name == "GameObject":
                try:
                    names[o.path_id] = o.read().m_Name
                except Exception:
                    pass

        interps, mats, objs = {}, [], set()
        for o in env.objects:
            if o.assets_file is not own:
                continue
            t = o.type.name
            try:
                if t == "MonoBehaviour":
                    tt = o.read_typetree()
                    cls = o.read().m_Script.read().m_ClassName
                    go = names.get((tt.get("m_GameObject") or {}).get("m_PathID"), "?")
                    objs.add("Mono/%s/%s" % (go, cls))
                    for r in ((tt.get("references") or {}).get("RefIds") or []):
                        d = r.get("data") or {}
                        p = d.get("propName")
                        if not p:
                            continue
                        k = (go, cls, p)
                        interps[k] = (d.get("startValue"), d.get("endValue"), d.get("duration"))
                elif t in ("MeshRenderer", "ParticleSystemRenderer", "TrailRenderer", "LineRenderer"):
                    r = o.read_typetree()
                    go = names.get((r.get("m_GameObject") or {}).get("m_PathID"), "?")
                    objs.add("Renderer/%s/%s" % (go, t))
                    nm = []
                    for m in (r.get("m_Materials") or []):
                        try:
                            nm.append("null" if m is None else "%s" % (m.get("m_PathID")))
                        except Exception:
                            nm.append("?")
                    mats.append((go, t, tuple(nm)))
            except Exception:
                continue
        return interps, mats, objs


def fmt(v):
    if isinstance(v, dict):
        return "(" + ",".join("%.3f" % v.get(k, 0) for k in ("r", "g", "b", "a")) + ")"
    return str(v)


def main():
    skins = sys.argv[1:] or SKINS
    base_names = collections.defaultdict(list)
    for e in IDX:
        r = e["r"]
        low = r.lower()
        if not low.startswith(BASE_DIR) or not low.endswith(".ab"):
            continue
        rest = r[len(BASE_DIR):]
        if rest.lower().startswith("esskin_"):
            continue
        base_names[rest].append(r)

    per_skin = {}
    _VALS.clear()
    for sk in skins:
        hits = []
        for name in sorted(base_names):
            sp = "%s%s/%s" % (BASE_DIR, sk, name)
            if sp.lower() not in {e["r"].lower() for e in IDX}:
                continue
            a = loads(BASE_DIR + name)
            b = loads(sp)
            if not a or not b:
                continue
            ia, ma, oa = a
            ib, mb, ob = b
            add = [k for k in ib if k not in ia]
            rem = [k for k in ia if k not in ib]
            chg = [(k, ia[k], ib[k]) for k in ib if k in ia and ia[k] != ib[k]]
            obj_add = sorted(ob - oa)
            mat_chg = [x for x in mb if x not in ma]
            for k in add:
                _VALS[(sk, name, k)] = ib[k]
            if add or rem or chg or obj_add or mat_chg:
                hits.append((name, add, rem, chg, obj_add, mat_chg))
        per_skin[sk] = hits

    for sk, hits in per_skin.items():
        print("=" * 96)
        print("### %s：改了 %d 个特效" % (sk, len(hits)))
        for name, add, rem, chg, obj_add, mat_chg in hits:
            print("  ● %s" % name)
            for k in add:
                v = _VALS.get((sk, name, k))
                print("       + 新增插值器 物体=%-14s 脚本=%-16s prop=%-18s start=%s end=%s"
                      % (k[0], k[1], k[2], fmt(v[0]) if v else "?", fmt(v[1]) if v else "?"))
            for k in rem:
                print("       - 删掉插值器 物体=%s 脚本=%s prop=%s" % (k[0], k[1], k[2]))
            for k, va, vb in chg:
                print("       ~ 改值      物体=%-14s 脚本=%-16s prop=%-18s %s/%s  ==>  %s/%s"
                      % (k[0], k[1], k[2], fmt(va[0]), fmt(va[1]), fmt(vb[0]), fmt(vb[1])))
            if obj_add:
                print("       · 多的对象: %s" % ", ".join(obj_add[:8]))
            if mat_chg:
                print("       · 材质引用变了: %s" % ", ".join("%s:%s" % (x[0], x[1]) for x in mat_chg[:6]))


if __name__ == "__main__":
    main()
