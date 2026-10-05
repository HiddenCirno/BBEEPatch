# -*- coding: utf-8 -*-
"""按【值】比较特效 prefab 里 MaterialTinter / MaterialTinterProxy / ActorTrailProxy 的插值器。

⚠ 为什么需要它: `_prefdiff.py` 的签名对 MonoBehaviour 只记【类名】，两个 prefab 只要组件一样
   就报"完全一致" —— 里面插值器的数值差异**看不到**。（我上一轮就是被这个骗了，
   得出了"皮肤副本零差异 ⇒ 引擎不是靠 prefab 换色"的错误结论。）

用法: python _trailcmp.py <名字片段>         # 原色与 4 套皮肤逐个比
"""
import io, os, re, sys
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _prefdiff as P
import UnityPy

SKINS = ["esskin_06", "esskin_10", "esskin_12", "esskin_13"]
CLASSES = ("MaterialTinter", "MaterialTinterProxy", "ActorTrailProxy", "ActorTrail", "TrailRenderer")


def interps(path):
    """→ [(物体, 脚本类, [ (类型, propName, start, end, duration) ... ])]"""
    data = P.bundle_bytes(path)
    if not data:
        return None
    env = UnityPy.load(data)
    names = {}
    for o in env.objects:
        if o.type.name == "GameObject":
            try:
                names[o.path_id] = o.read().m_Name
            except Exception:
                pass
    out = []
    for o in env.objects:
        if o.type.name != "MonoBehaviour":
            continue
        try:
            cls = o.read().m_Script.read().m_ClassName
        except Exception:
            continue
        if cls not in CLASSES:
            continue
        tt = o.read_typetree()
        go = names.get(tt.get("m_GameObject", {}).get("m_PathID"), "?")
        items = []
        for r in (tt.get("references", {}) or {}).get("RefIds", []) or []:
            d = (r or {}).get("data") or {}
            cls2 = (r or {}).get("type", {}).get("class", "?")
            def col(c):
                if not isinstance(c, dict):
                    return c
                return tuple(round(c.get(k, 0), 3) for k in ("r", "g", "b", "a"))
            items.append((cls2, d.get("propName"),
                          col(d.get("startValue")) if isinstance(d.get("startValue"), dict) else d.get("startValue"),
                          col(d.get("endValue")) if isinstance(d.get("endValue"), dict) else d.get("endValue"),
                          round(d.get("duration", 0), 3)))
        out.append((go, cls, items))
    return out


def show(path, label):
    r = interps(path)
    if r is None:
        print("  %-28s 读不到" % label)
        return None
    key = []
    for go, cls, items in r:
        for it in items:
            key.append((go, cls) + it)
    print("  %-28s %s" % (label, " 无插值器" if not key else ""))
    for k in sorted(key, key=str):
        print("      %-6s %-20s %-26s %-11s start=%-26s end=%s" % (k[0], k[1], k[2], k[3], k[4], k[5]))
    return set(key)


if __name__ == "__main__":
    frag = sys.argv[1] if len(sys.argv) > 1 else "es_dodge"
    base = sorted(p for p in P.BY if re.search(frag, p, re.I) and "prefab/role/es/" in p
                  and "/esskin_" not in p)
    for p in base:
        print("=" * 100)
        print("###", p)
        ref = show(p, "原色")
        for sk in SKINS:
            d, fn = os.path.split(p)
            cand = "%s/%s/%s" % (d, sk, fn)
            if cand.lower() not in P.BY:
                continue
            got = show(cand, sk)
            if ref is not None and got is not None and ref != got:
                print("      ★★ 数值有差异！")
