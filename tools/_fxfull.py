# -*- coding: utf-8 -*-
"""
**全量**拆解 / 对比特效 prefab：原色 vs 各套皮肤专属特效。

为什么不能只比值：
  `_prefdiff.py` 对 MonoBehaviour 只记类名（结构相同 = "无差异"）；
  `_trailcmp.py` 只比插值器的数值。两者都**看不见**：
    · 材质引用的**贴图**换了没有（贴图驱动的特效，换色就是换图）
    · 材质上的 `_TintColor` / shader 关键字 / float 参数
    · 组件以外的任何对象（贴图本体、Mesh、AnimationClip…）
  ⇒ 结果就是"离线说没差异，线上却明显变色"，然后拿错误结论去线上瞎试。
  这个工具把**每个对象的全部内容**（含贴图原始数据哈希）都摊开比。

用法:
    _fxfull.py <特效名>              # 自动找 原色 + esskin_* 的副本，两两对比
    _fxfull.py <特效名> --dump       # 只打印原色那一份的完整清单
"""
import collections, hashlib, io, json, os, struct, sys
import UnityPy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _abload import load_deep as ab_load, BY   # ★ 走共享加载器（整个容器的所有切片一起载）

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

BASE = os.path.dirname(os.path.abspath(__file__))
GAME = os.path.join(BASE, "..", "..")
AB = os.path.join(GAME, "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")
SKINS = ["esskin_06", "esskin_10", "esskin_12", "esskin_13"]


def load_container(vpath):
    return ab_load(vpath)


def tex_hash(obj):
    """贴图内容指纹 —— 换色皮肤如果偷换了贴图，只有这一列能看出来。"""
    try:
        raw = obj.get_raw_data()
        return hashlib.md5(raw).hexdigest()[:10] + "#%d" % len(raw)
    except Exception as e:
        return "(读不到:%s)" % e


def dump_bundle(vpath):
    """→ {键: 描述}，键 = 类型/名字/path_id，值 = 该对象的全部可见内容。"""
    env = load_container(vpath)
    out = {}
    if env is None:
        return None, {}
    # ★ 只保留**属于这个特效那个 bundle** 的对象。
    #   容器里还塞着几十个无关资产（地图/怪物技能…），不滤掉的话对比结果会被它们淹掉
    #   （实测"只在原色里有 1222 个对象、只在 esskin_06 里有 6452 个"，全是噪音）。
    own = own_files(env, vpath)
    if own is None:
        return env, {}
    names = {}
    for o in env.objects:
        if o.assets_file not in own:
            continue
        if o.type.name == "GameObject":
            try:
                names[o.path_id] = o.read().m_Name
            except Exception:
                pass

    for o in env.objects:
        if o.assets_file not in own:
            continue
        t = o.type.name
        try:
            if t == "Material":
                m = o.read()
                tt = o.read_typetree()
                sh = "?"
                try:
                    sh = m.m_Shader.read().m_Name
                except Exception as e:
                    sh = "(shader读不到)"
                sp = tt.get("m_SavedProperties", {})
                cols, flts = {}, {}
                for nm2, col in sp.get("m_Colors", []):
                    try:
                        cols[nm2] = tuple(round(float(col[k]), 4) for k in "rgba")
                    except Exception:
                        cols[nm2] = col
                for nm2, fv in sp.get("m_Floats", []):
                    flts[nm2] = round(float(fv), 4) if isinstance(fv, (int, float)) else fv
                texs = {}
                for te in tt.get("m_SavedProperties", {}).get("m_TexEnvs", []):
                    nm = te["m_Name"]
                    try:
                        tobj = te["m_Texture"]["m_FileID"], te["m_Texture"]["m_PathID"]
                        if te["m_Texture"].get("m_PathID"):
                            r = m.m_SavedProperties.m_TexEnvs
                        texs[nm] = tex_name(te)
                    except Exception:
                        texs[nm] = "?"
                kws = []
                try:
                    kws = sorted(m.m_ShaderKeywords.split())
                except Exception:
                    pass
                out["Material/" + (m.m_Name or "?")] = {
                    "shader": sh, "keywords": kws, "colors": cols, "floats": flts, "textures": texs,
                }
            elif t in ("Texture2D", "Sprite", "Mesh", "AnimationClip", "Shader"):
                nm = ""
                try:
                    nm = o.read().m_Name
                except Exception:
                    pass
                h = tex_hash(o) if t in ("Texture2D", "Mesh", "AnimationClip") else "(不哈希)"
                out["%s/%s" % (t, nm)] = {"hash": h}
            elif t in ("MeshRenderer", "ParticleSystemRenderer", "SkinnedMeshRenderer",
                       "TrailRenderer", "LineRenderer"):
                r = o.read()
                go = names.get(getattr(r, "m_GameObject", None) and r.m_GameObject.path_id, "?")
                mats = []
                try:
                    for mm in r.m_Materials:
                        mats.append("(空槽)" if mm is None else safe_name(mm))
                except Exception as e:
                    mats = ["(读失败:%s)" % e]
                out["Renderer/%s/%s" % (go, t)] = {"type": t, "materials": mats}
            elif t == "MonoBehaviour":
                tt = o.read_typetree()
                cls = ""
                try:
                    cls = o.read().m_Script.read().m_ClassName
                except Exception:
                    pass
                go = names.get(tt.get("m_GameObject", {}).get("m_PathID"), "?")
                out["Mono/%s/%s" % (go, cls)] = tt
        except Exception as e:
            out["%s/ERR" % t] = {"__err__": str(e)}
    return env, out


def own_files(env, vpath):
    """→ 装着这个特效的那个 SerializedFile 集合（拿 AssetBundle 的 m_Name 认）。"""
    for o in env.objects:
        if o.type.name != "AssetBundle":
            continue
        try:
            if (o.read_typetree().get("m_Name") or "").lower() == vpath.lower():
                return {o.assets_file}
        except Exception:
            continue
    return None


def tex_name(te):
    try:
        pt = te["m_Texture"]
        if not pt.get("m_PathID"):
            return "(空)"
        return "(fileID=%s pathID=%s)" % (pt["m_FileID"], pt["m_PathID"])
    except Exception:
        return "?"


def safe_name(pptr):
    try:
        return pptr.read().m_Name
    except Exception:
        return "(读不到 pathID=%s)" % getattr(pptr, "pathID", "?")


def top_keys(d, n=14):
    return sorted(d.keys())[:n]


def diff(a, b, aname, bname):
    ka, kb = set(a), set(b)
    only_a = sorted(ka - kb)
    only_b = sorted(kb - ka)
    if only_a:
        print("  只在 %s 里有 %d 个对象:" % (aname, len(only_a)))
        for k in only_a[:12]:
            print("    -", k)
    if only_b:
        print("  只在 %s 里有 %d 个对象:" % (bname, len(only_b)))
        for k in only_b[:12]:
            print("    +", k)
    same = 0
    diffk = []
    for k in sorted(ka & kb):
        if a[k] == b[k]:
            same += 1
        else:
            diffk.append(k)
    print("  同名对象 %d 个：相同 %d，**不同 %d**" % (len(ka & kb), same, len(diffk)))
    for k in diffk[:40]:
        print("    * %s" % k)
        da, db = a[k], b[k]
        if isinstance(da, dict) and isinstance(db, dict):
            for kk in sorted(set(da) | set(db)):
                va, vb = da.get(kk), db.get(kk)
                if va != vb:
                    sa, sb = str(va), str(vb)
                    if len(sa) > 220: sa = sa[:220] + "…"
                    if len(sb) > 220: sb = sb[:220] + "…"
                    print("        %-14s %s  ==>  %s" % (kk, sa, sb))
    return diffk


def main():
    key = sys.argv[1]
    only_dump = "--dump" in sys.argv
    base = "effect/prefab/role/es/%s.ab" % key
    if base.lower() not in BY:
        cand = [e["r"] for e in IDX if key.lower() in e["r"].lower()]
        print("找不到 %s，候选：" % base)
        for c in cand[:20]:
            print("   ", c)
        return
    print("=== 原色 %s ===" % base)
    env, ref = dump_bundle(base)
    if ref is None:
        print("  读不到"); return
    print("  对象数 %d；对象类型统计:" % len(ref))
    import collections
    for t, n in collections.Counter(k.split("/")[0] for k in ref).most_common():
        print("     %-16s %d" % (t, n))
    if only_dump:
        for k in sorted(ref):
            print("   ", k, "" if not isinstance(ref[k], dict) else
                  (" ".join("%s=%s" % (a, b) for a, b in list(ref[k].items())[:6])[:180]))
        return
    for sk in SKINS:
        vp = "effect/prefab/role/es/%s/%s.ab" % (sk, key)
        if vp.lower() not in BY:
            print("\n--- %s：没有这一套 ---" % sk); continue
        print("\n--- 对比 %s ---" % sk)
        _, other = dump_bundle(vp)
        if other is None:
            print("  读不到"); continue
        diff(ref, other, "原色", sk)


if __name__ == "__main__":
    main()
