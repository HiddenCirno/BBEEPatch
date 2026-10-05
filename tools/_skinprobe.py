# -*- coding: utf-8 -*-
"""
拆解【换色皮肤】的专属特效：把 原色 / esskin_06 / 10 / 12 / 13 的每条插值器摊开对照。

为什么要专门做这个：皮肤**只带 prefab、不带任何材质/贴图**（实测 440 个 esskin 资产全是
`effect/prefab/...`），所以它的换色配方**只能写在 prefab 数据里** ——
也就是 `MaterialColorInterpolator`（propName/start/end）+ `VariantToggle[]`/`VariantKey` 这些字段。
把这条配方读出来，就等于拿到了"官方是怎么把蓝白电弧换成皮肤色"的标准答案，
我们照着它做就行（而不是继续猜 `_AddColor` 还是 `_TintColor`）。

用法: _skinprobe.py <特效名> [特效名...]
      _skinprobe.py es_dash_01 es_attackhlod_02
"""
import json, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _abload import load_deep, BY   # noqa

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
SKINS = ["esskin_06", "esskin_10", "esskin_12", "esskin_13"]


def interps(vpath):
    """→ {脚本类名: [ {prop, start, end, duration, toggles, ...} ]}（只取我们这个 bundle 的对象）"""
    env = load_deep(vpath)
    if env is None:
        return None
    own = None
    for o in env.objects:
        if o.type.name != "AssetBundle":
            continue
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
        cls = ""
        try:
            cls = o.read().m_Script.read().m_ClassName
        except Exception:
            pass
        refs = (tt.get("references") or {}).get("RefIds") or []
        items = []
        for r in refs:
            d = r.get("data") or {}
            if not d:
                continue
            items.append({
                "type": (r.get("type") or {}).get("class"),
                "prop": d.get("propName"),
                "start": d.get("startValue"),
                "end": d.get("endValue"),
                "dur": d.get("duration"),
                "delay": d.get("delay"),
                "restore": d.get("restore"),
                "toggles": d.get("toggles"),
            })
        if items:
            out[cls] = items
    return out


def key(it):
    return (it["type"], it["prop"])


def main():
    for name in sys.argv[1:]:
        base = "effect/prefab/role/es/%s.ab" % name
        if base.lower() not in BY:
            print("!! 没有 %s" % base)
            continue
        ref = interps(base)
        print("=" * 96)
        print("### %s" % name)
        if ref is None:
            print("   读不到"); continue
        for cls, items in ref.items():
            print("  [%s] %d 条插值器" % (cls, len(items)))
            for it in items:
                print("      %-26s %-18s start=%s end=%s dur=%s toggles=%s" % (
                    it["type"], it["prop"], it["start"], it["end"], it["dur"],
                    (str(it["toggles"])[:40] if it["toggles"] else "")))
        for sk in SKINS:
            vp = "effect/prefab/role/es/%s/%s.ab" % (sk, name)
            if vp.lower() not in BY:
                continue
            oth = interps(vp)
            if oth is None:
                continue
            print("  --- %s ---" % sk)
            for cls in sorted(set(ref) | set(oth)):
                a = {key(i): i for i in ref.get(cls, [])}
                b = {key(i): i for i in oth.get(cls, [])}
                for k in sorted(set(a) | set(b), key=str):
                    x, y = a.get(k), b.get(k)
                    if x == y:
                        continue
                    print("      [%s] %s" % (cls, k))
                    for f in ("start", "end", "dur", "delay", "restore", "toggles"):
                        xv = None if x is None else x.get(f)
                        yv = None if y is None else y.get(f)
                        if xv != yv:
                            print("          %-8s %s  ==>  %s" % (f, xv, yv))


if __name__ == "__main__":
    main()
