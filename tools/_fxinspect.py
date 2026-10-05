# -*- coding: utf-8 -*-
"""离线看一个特效 prefab 里到底有什么载体（决定换色要往哪儿刷）。

用法: python _fxinspect.py effect/prefab/role/es/es_dash_02.ab [更多路径...]

做法: merge.json 给 (偏移 s, 清单文件 m)，从 m 的 s 处切开 UnityFS 块交给 UnityPy。
输出: 层级 + 组件类型 + 渲染器的材质/着色器/颜色属性 + 粒子的 startColor + 拖影的渐变色。
"""
import io, json, os, struct, sys
import UnityPy

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
GAME = os.path.join(BASE, "..")
AB = os.path.join(GAME, "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")
idx = json.load(io.open(os.path.join(AB, "merge.json"), encoding="utf-8"))
BY = {e["r"].lower(): e for e in idx}

COLOR_KEYS = ("_TintColor", "_Color", "_MainColor", "_BaseColor", "_EmissionColor",
              "_StartColor", "_EndColor", "_AddColor", "_LineColor")


def bundle_bytes(path):
    e = BY.get(path.lower())
    if e is None:
        return None
    mp = os.path.join(AB, e["m"])
    with open(mp, "rb") as f:
        f.seek(e["s"])
        d = f.read(256)
        # UnityFS 头: 签名字符串 + u32 版本 + unity 版本串 + 修订串 + i64 总长
        p = d.index(b"\x00", 8) + 1
        p += 4
        p = d.index(b"\x00", p) + 1
        p = d.index(b"\x00", p) + 1
        total = struct.unpack_from(">q", d, p)[0]
        f.seek(e["s"])
        return f.read(total)


def mat_info(m):
    out = []
    try:
        sh = m.m_Shader.read()
        out.append("shader=%s" % sh.m_Name)
    except Exception:
        pass
    try:
        sp = m.m_SavedProperties
        cols = getattr(sp, "m_Colors", None) or []
        for c in cols:
            nm = c[0]
            if any(k.lower() in nm.lower() for k in COLOR_KEYS):
                out.append("%s=%s" % (nm, tuple(round(x, 3) for x in c[1][:4])))
    except Exception as ex:
        out.append("props?%s" % ex)
    return out


def dump(path):
    data = bundle_bytes(path)
    print("=" * 78)
    print("### %s   (%s bytes)" % (path, "读取失败" if data is None else len(data)))
    if data is None:
        return
    env = UnityPy.load(data)
    names = {}
    for obj in env.objects:
        t = obj.type.name
        try:
            if t == "GameObject":
                d = obj.read()
                names[obj.path_id] = d.m_Name
        except Exception:
            pass
    for obj in env.objects:
        t = obj.type.name
        try:
            d = obj.read()
        except Exception as e:
            print("  [%s] <读失败 %s>" % (t, e)); continue
        go = None
        try:
            go = names.get(d.m_GameObject.path_id if hasattr(d, "m_GameObject") else None)
        except Exception:
            pass
        head = "[%s] %s" % (t, go or "")
        if t == "Material":
            print("  %s  %s" % (head, " | ".join(mat_info(d))))
        elif t in ("MeshRenderer", "SkinnedMeshRenderer", "ParticleSystemRenderer",
                   "SpriteRenderer", "TrailRenderer", "LineRenderer"):
            mats = []
            try:
                for mp in (d.m_Materials or []):
                    mm = mp.read()
                    mats.append("%s{%s}" % (mm.m_Name, ", ".join(mat_info(mm))))
            except Exception as e:
                mats.append("材质读失败:%s" % e)
            extra = ""
            if t == "TrailRenderer":
                try:
                    extra = " start=%s end=%s time=%.2f" % (
                        tuple(round(x, 3) for x in d.m_StartColor[:4]),
                        tuple(round(x, 3) for x in d.m_EndColor[:4]), d.m_Time)
                except Exception as e:
                    extra = " 拖影字段读失败:%s" % e
            print("  %s  %s%s" % (head, " ; ".join(mats), extra))
        elif t == "ParticleSystem":
            try:
                im = d.InitialModule
                sc = im.startColor
                print("  %s  startColor=%s" % (head, sc))
            except Exception as e:
                print("  %s  (初始模块读失败 %s)" % (head, e))
        elif t == "MonoBehaviour":
            cls = "?"
            try:
                cls = d.m_Script.read().m_ClassName
            except Exception:
                pass
            print("  %s  script=%s" % (head, cls))
        elif t in ("GameObject", "Transform", "RectTransform", "AnimationClip", "Texture2D", "Shader"):
            if t == "GameObject":
                print("  [GameObject] %s" % d.m_Name)
        else:
            print("  %s" % head)
    print()


for p in sys.argv[1:]:
    dump(p)
