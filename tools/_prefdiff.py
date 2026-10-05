# -*- coding: utf-8 -*-
"""离线对比两个特效 prefab 的【内容差异】。

用途: 皮肤套装里的 `esskin_XX/<同名>.ab` 与原色 `es/<同名>.ab` 是**同名副本**,
文件清单层面看不出差别(105/105 全覆盖), 差别在**里面**。这个脚本把两个 prefab 的
结构签名拉平成可比较的行, 只打差异。

用法:
    python _prefdiff.py <原色.ab> <皮肤.ab> [更多成对路径...]
    python _prefdiff.py --batch <名字片段>       # 自动对 4 套皮肤各比一次

签名内容(每个对象一行):
    GameObject      名字
    ParticleSystem  startColor (mode,max,min)
    *Renderer       材质名
    MonoBehaviour   脚本类名
    Material        shader 名 + 颜色属性值
"""
import io, json, os, re, struct, sys, collections
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

BASE = os.path.dirname(os.path.abspath(__file__))
AB = os.path.join(BASE, "..", "..", "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")
idx = json.load(io.open(os.path.join(AB, "merge.json"), encoding="utf-8"))
BY = {e["r"].lower(): e for e in idx}

SKINS = ["esskin_03", "esskin_06", "esskin_10", "esskin_12", "esskin_13"]


def bundle_bytes(path):
    e = BY.get(path.lower())
    if e is None:
        return None
    mp = os.path.join(AB, e["m"])
    with open(mp, "rb") as f:
        f.seek(e["s"])
        d = f.read(256)
        p = d.index(b"\x00", 8) + 1
        p += 4
        p = d.index(b"\x00", p) + 1
        p = d.index(b"\x00", p) + 1
        total = struct.unpack_from(">q", d, p)[0]
        f.seek(e["s"])
        return f.read(total)


def name_map(env):
    names = {}
    for obj in env.objects:
        if obj.type.name != "GameObject":
            continue
        try:
            names[obj.path_id] = obj.read().m_Name
        except Exception:
            pass
    return names


def go_of(d, names):
    try:
        return names.get(d.m_GameObject.path_id)
    except Exception:
        return None


def sig(path):
    """把 prefab 拉平成 Counter[(类型, 物体名, 细节)]。"""
    data = bundle_bytes(path)
    if data is None:
        return None
    import UnityPy
    env = UnityPy.load(data)
    names = name_map(env)
    rows = collections.Counter()
    for obj in env.objects:
        t = obj.type.name
        try:
            d = obj.read()
        except Exception:
            rows[(t, "?", "<读失败>")] += 1
            continue
        go = go_of(d, names)
        if t == "GameObject":
            rows[("GameObject", d.m_Name, "")] += 1
        elif t == "ParticleSystem":
            try:
                sc = d.InitialModule.startColor
                rows[("ParticleSystem", go, "mode=%s max=%s min=%s" % (
                    getattr(sc, "minMaxState", "?"),
                    tuple(round(x, 3) for x in sc.maxColor[:4]),
                    tuple(round(x, 3) for x in sc.minColor[:4])))] += 1
            except Exception as e:
                rows[("ParticleSystem", go, "<读失败:%s>" % e)] += 1
        elif t in ("MeshRenderer", "SkinnedMeshRenderer", "ParticleSystemRenderer",
                   "SpriteRenderer", "TrailRenderer", "LineRenderer"):
            mats = []
            try:
                for mp in (d.m_Materials or []):
                    mats.append(mp.read().m_Name)
            except Exception:
                mats.append("?")
            extra = ""
            if t == "TrailRenderer":
                try:
                    extra = " start=%s end=%s" % (
                        tuple(round(x, 3) for x in d.m_StartColor[:4]),
                        tuple(round(x, 3) for x in d.m_EndColor[:4]))
                except Exception:
                    pass
            rows[(t, go, "mat=" + ",".join(mats) + extra)] += 1
        elif t == "MonoBehaviour":
            cls = "?"
            try:
                cls = d.m_Script.read().m_ClassName
            except Exception:
                pass
            rows[("MonoBehaviour", go, cls)] += 1
        elif t == "Material":
            det = ""
            try:
                det = "shader=" + d.m_Shader.read().m_Name
            except Exception:
                pass
            try:
                for c in (d.m_SavedProperties.m_Colors or []):
                    nm = c[0]
                    if re.search(r"color|flame|tint", nm, re.I):
                        det += " %s=%s" % (nm, tuple(round(x, 3) for x in c[1][:4]))
            except Exception:
                pass
            rows[("Material", go, det)] += 1
        else:
            rows[(t, go, "")] += 1
    return rows


def diff(a_path, b_path, label=""):
    a, b = sig(a_path), sig(b_path)
    print("=" * 100)
    print("### %s" % label)
    print("  A = %s" % a_path)
    print("  B = %s" % b_path)
    if a is None or b is None:
        print("  !! 有一个读不到 (A=%s B=%s)" % (a is not None, b is not None))
        return
    ka, kb = set(a), set(b)
    only_a = sorted(ka - kb)
    only_b = sorted(kb - ka)
    if not only_a and not only_b:
        print("  [OK] 结构签名完全一致（差异不在 prefab 内部）")
    for t in only_a:
        print("  A 独有 x%d : %s" % (a[t], fmt(t)))
    for t in only_b:
        print("  B 独有 x%d : %s" % (b[t], fmt(t)))
    # 计数不同的
    for t in sorted(ka & kb):
        if a[t] != b[t]:
            print("  数量不同     : %s   A=%d B=%d" % (fmt(t), a[t], b[t]))


def fmt(t):
    return "[%s] %s  %s" % (t[0], t[1], t[2])


def batch(frag):
    """自动找出所有含 frag 的原色资产, 与各皮肤同名副本逐一对比。"""
    base = sorted(p for p in BY if re.search(frag, p, re.I) and "prefab/role/es/" in p
                  and "/esskin_" not in p)
    for p in base:
        for sk in SKINS:
            d, fn = os.path.split(p)
            cand = "%s/%s/%s" % (d, sk, fn)
            if cand.lower() in BY:
                diff(p, cand, "%s  vs  %s" % (os.path.basename(p), sk))


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "--batch":
        batch(sys.argv[2])
    elif len(sys.argv) >= 3:
        args = sys.argv[1:]
        for i in range(0, len(args) - 1, 2):
            diff(args[i], args[i + 1])
    else:
        print(__doc__)
