# -*- coding: utf-8 -*-
"""
查一个 prefab 里**每个渲染器挂了什么材质**（空槽 = 运行时会被 Unity 填成 InternalErrorShader）。

为什么需要它：日志里出现 `Hidden/InternalErrorShader (Instance)` 时，有两种可能 ——
  ① 游戏自己的 prefab 那一格**本来就是空的**（Unity 给个错误 shader 占位）；
  ② 被我们的代码搞坏了。
离线看 prefab 就能一眼分辨，不用拿游戏去试。

用法: _prefmat.py <ab路径关键字> [prefab名关键字]
      _prefmat.py esbullet
"""
import io, json, os, struct, sys
import UnityPy

BASE = os.path.dirname(os.path.abspath(__file__))
GAME = os.path.join(BASE, "..", "..")
AB = os.path.join(GAME, "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")
idx = json.load(io.open(os.path.join(AB, "merge.json"), encoding="utf-8"))
BY = {e["r"].lower(): e for e in idx}


def find_ab(key):
    hits = [e for e in idx if key.lower() in e["r"].lower()]
    return hits


def bundle_bytes(path):
    """⚠ 资产不是一个个 .ab 文件 —— merge.json 给的是【容器文件 + 偏移 + 大小】,
    要按它的头算出真实长度再从容器里切出来（照抄 _mbdump.py 的做法, 别自己猜）。"""
    e = BY.get(path.lower())
    if e is None:
        return None
    with open(os.path.join(AB, e["m"]), "rb") as f:
        f.seek(e["s"])
        d = f.read(256)
        p = d.index(b"\x00", 8) + 1
        p += 4
        p = d.index(b"\x00", p) + 1
        p = d.index(b"\x00", p) + 1
        total = struct.unpack_from(">q", d, p)[0]
        f.seek(e["s"])
        return f.read(total)


def dump(abpath, namekey):
    data = bundle_bytes(abpath)
    if data is None:
        print("  读不到:", abpath); return
    env = UnityPy.load(data)
    for obj in env.objects:
        if obj.type.name not in ("GameObject", "Material"):
            continue
        try:
            d = obj.read()
        except Exception:
            continue
        nm = getattr(d, "m_Name", "") or ""
        if obj.type.name == "Material":
            sh = getattr(d, "m_Shader", None)
            shn = "?"
            try:
                if sh is not None:
                    shn = sh.read().m_Name
            except Exception:
                shn = "(shader 读不到)"
            print("  [材质] %-28s shader=%s" % (nm or "(无名)", shn))
            continue
        if namekey and namekey.lower() not in nm.lower():
            continue
        comps = getattr(d, "m_Component", []) or []
        rows = []
        for c in comps:
            try:
                comp = c.component if hasattr(c, "component") else c
                co = comp.read()
            except Exception:
                continue
            ctn = type(co).__name__
            if "Renderer" not in ctn:
                continue
            mats = []
            try:
                for m in (co.m_Materials or []):
                    mats.append(m.read().m_Name if m is not None else "(空槽)")
            except Exception as e:
                mats.append("(读失败:%s)" % e)
            rows.append("%s: %s" % (ctn, ", ".join(mats) if mats else "(无材质列表)"))
        if rows:
            print("  [物体] %-28s" % (nm or "(无名)"))
            for r in rows:
                print("        ", r)


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    key = sys.argv[1] if len(sys.argv) > 1 else "esbullet"
    namekey = sys.argv[2] if len(sys.argv) > 2 else None
    hits = find_ab(key)
    print("匹配到 %d 个 ab:" % len(hits))
    for e in hits[:10]:
        print("  ", e["r"])
    for e in hits[:4]:
        print("\n=== %s ===" % e["r"])
        dump(e["r"], namekey)


if __name__ == "__main__":
    main()
