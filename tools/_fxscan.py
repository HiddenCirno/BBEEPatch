# -*- coding: utf-8 -*-
"""全量扫特效 prefab 的两个属性，用来找"某道没被染色/不知道哪来的特效"。

① `VFXEffectExtension.ScreenSpace` —— 屏幕空间(全屏)特效。我们的换色管线**故意跳过**它们
   (`SkipScreenSpace=true`，因为 SP 那层全屏叠加会变成整屏滤镜)，所以它们会**保持原色**。
② 每个 ParticleSystem 的 startColor —— 想找"蓝光"就按颜色筛。

用法:
    python _fxscan.py screenspace            # 列出所有屏幕空间特效
    python _fxscan.py blue [关键字]           # 列出粒子偏蓝的特效(可加名字关键字)
    python _fxscan.py info <路径片段>         # 看某个/某组特效的完整载体+颜色
"""
import io, os, re, sys
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _prefdiff as P
import UnityPy


def load(path):
    data = P.bundle_bytes(path)
    if not data:
        return None
    try:
        env = UnityPy.load(data)
    except Exception:
        return None
    names = {}
    for obj in env.objects:
        if obj.type.name == "GameObject":
            try:
                names[obj.path_id] = obj.read().m_Name
            except Exception:
                pass
    return env, names


def screen_space(path):
    r = load(path)
    if not r:
        return None
    env, names = r
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            cls = obj.read().m_Script.read().m_ClassName
        except Exception:
            continue
        if cls != "VFXEffectExtension":
            continue
        try:
            tt = obj.read_typetree()
        except Exception:
            continue
        return (bool(tt.get("ScreenSpace")), tt.get("ScreenSpaceDir"), tt.get("ZeroOrder"))
    return None


def colors(path, limit=12):
    r = load(path)
    if not r:
        return []
    env, names = r
    out = []
    for obj in env.objects:
        if obj.type.name != "ParticleSystem":
            continue
        try:
            d = obj.read()
            sc = d.InitialModule.startColor
            go = names.get(d.m_GameObject.m_PathID, "?")
            mx = tuple(round(x, 3) for x in sc.maxColor[:4]) if hasattr(sc.maxColor, "__getitem__") else None
            mn = tuple(round(x, 3) for x in sc.minColor[:4]) if hasattr(sc.minColor, "__getitem__") else None
            out.append((go, getattr(sc, "minMaxState", "?"), mx, mn))
        except Exception:
            out.append(("?", "?", None, None))
    return out[:limit]


def all_prefabs():
    return sorted(e["r"] for e in P.idx
                  if re.match(r"effect/prefab/(role/es/[^/]+|common/[^/]+)\.ab$", e["r"], re.I))


def is_blue(c):
    if not c:
        return False
    r, g, b = c[0], c[1], c[2]
    return b > 0.75 and b >= r + 0.15 and b >= g + 0.1


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "screenspace"
    if mode == "screenspace":
        print("屏幕空间(全屏)特效 —— 这些会被换色管线跳过, 保持原色:")
        for p in all_prefabs():
            ss = screen_space(p)
            if ss and ss[0]:
                print("   ★ %-58s ScreenSpaceDir=%s ZeroOrder=%s" % (p, ss[1], ss[2]))
    elif mode == "blue":
        kw = sys.argv[2].lower() if len(sys.argv) > 2 else ""
        print("粒子偏蓝的特效%s:" % (" (名字含 %s)" % kw if kw else ""))
        for p in all_prefabs():
            if kw and kw not in p.lower():
                continue
            cs = colors(p)
            blues = [c for c in cs if is_blue(c[2])]
            if blues:
                ss = screen_space(p)
                tag = "屏幕空间" if (ss and ss[0]) else "        "
                print("   %s %-56s %s" % (tag, p, "; ".join("%s=%s" % (c[0], c[2]) for c in blues[:3])))
    elif mode == "info":
        frag = sys.argv[2].lower()
        for p in all_prefabs():
            if frag in p.lower():
                ss = screen_space(p)
                print("=" * 90)
                print("%s   ScreenSpace=%s" % (p, ss))
                for go, mode_, mx, mn in colors(p):
                    print("    粒子 %-16s mode=%s max=%s min=%s" % (go, mode_, mx, mn))
