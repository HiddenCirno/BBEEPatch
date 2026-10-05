# -*- coding: utf-8 -*-
"""
把某个特效 prefab 里**每个 Tinter 的全部插值器**（propName + start/end）逐条打出来，
并同时打原色与各套皮肤的那一份 —— 用来回答"官方到底改的是哪一个对象/哪一条属性"。

为什么要有它（这一轮的教训）：
  `_skindiff_all.py` 只列**差异**。一旦某个对象在四套皮肤里都没被改，
  它就**一次都不会出现在输出里** —— 我们会把"没出现"误读成"官方没动它/它不重要"。
  而真正的情况可能是"它的颜色走的是**别人身上的 Proxy tinter**"（本作有 MaterialTinterProxy）。
  ⇒ 想看清一个对象，就得把**全部**插值器摊开看，而不是只看 diff。

用法: _tinterdump.py <特效名>            # 例: _tinterdump.py es_stand_01
      _tinterdump.py es_stand_01 ring02  # 只看名字含 ring02 的对象
"""
import io
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _abload import slice_at, IDX            # noqa: E402
import UnityPy                                # noqa: E402

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
SKINS = ["esskin_06", "esskin_10", "esskin_12", "esskin_13"]
BASE_DIR = "effect/prefab/role/es/"


def fmt(v):
    if isinstance(v, dict):
        return "(" + ",".join("%.3f" % v.get(k, 0) for k in ("r", "g", "b", "a")) + ")"
    return str(v)


def collect(vpath):
    """→ {对象名: {类名: [(propName, start, end)]}}"""
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
        out = {}
        for o in env.objects:
            if o.assets_file is not own or o.type.name != "MonoBehaviour":
                continue
            try:
                tt = o.read_typetree()
                cls = o.read().m_Script.read().m_ClassName
                go = names.get((tt.get("m_GameObject") or {}).get("m_PathID"), "?")
                rows = out.setdefault(go, {}).setdefault(cls, [])
                for r in ((tt.get("references") or {}).get("RefIds") or []):
                    d = r.get("data") or {}
                    p = d.get("propName")
                    if not p:
                        continue
                    rows.append((p, d.get("startValue"), d.get("endValue")))
            except Exception:
                continue
        return out
    return None


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return
    fx = sys.argv[1]
    filt = sys.argv[2] if len(sys.argv) > 2 else ""

    targets = [("原色", BASE_DIR + fx + ".ab")] + \
              [(s, BASE_DIR + s + "/" + fx + ".ab") for s in SKINS]

    for label, path in targets:
        got = collect(path)
        if got is None:
            print("── %-9s %s  （没有这个切片）" % (label, path))
            continue
        print("── %-9s %s" % (label, path))
        for go in sorted(got):
            if filt and filt.lower() not in go.lower():
                continue
            for cls, rows in sorted(got[go].items()):
                if not rows:
                    continue
                print("    %s / %s" % (go, cls))
                for p, s, e in rows:
                    print("        %-22s %s  =>  %s" % (p, fmt(s), fmt(e)))
    print()
    print("★ 读法: 同一个对象在 原色 与 各皮肤 下**同名 propName** 的 end 值不同 = 官方就是靠它换色的。")


main()
