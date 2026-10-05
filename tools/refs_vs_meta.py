#!/usr/bin/env python3
"""把插件引用的 UnityEngine.* API 与游戏 global-metadata (dump.cs) 对照。

背景
----
BepInEx 的 interop 程序集 (BepInEx/interop/*.dll) 由 Cpp2IL 生成, 里面【补全】了
Unity 引擎的完整 API 表面。但其中一部分方法在 global-metadata.dat 里并不存在
(被 UnityLinker 的方法级 strip 删掉了), 那些方法的 IL 体是一根共享桩, 一调用就抛
    System.NotSupportedException: Method unstripping failed

它们【有方法体】, 所以 Harmony 可以顶掉; 但不能直接调用。

本脚本算出「插件引用了、但元数据里没有」= 必须 shim 的那批方法。

用法:
    uv run --with dnfile python tools/refs_vs_meta.py <插件dll> [dump.cs路径]
"""
import re
import sys

import dnfile

DEFAULT_DUMP = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\dump\dump.cs"

_DECL = re.compile(r"\b(?:class|struct|enum|interface)\s+([A-Za-z_][\w`]*)")
_MEMBER = re.compile(r"[ \t]([\w.<>`]+)\(")
_FIELD = re.compile(r"^[ \t]*(?:public|internal|private|protected)[ \t]+(?:static[ \t]+|readonly[ \t]+|const[ \t]+)*[\w.<>\[\],?]+[ \t]+([A-Za-z_]\w*)[ \t]*;")


def parse_dump(path):
    """返回 {类型名: set(成员名)}。以 `// TypeDefIndex:` 行为锚点切块, 容忍泛型/基类。"""
    lines = open(path, encoding="utf-8", errors="replace").read().splitlines()
    anchors = []
    for k, l in enumerate(lines):
        if "// TypeDefIndex:" in l and (l.startswith("public") or l.startswith("internal")):
            m = _DECL.search(l)
            if m:
                anchors.append((k, m.group(1)))

    out = {}
    for idx, (k, name) in enumerate(anchors):
        stop = anchors[idx + 1][0] if idx + 1 < len(anchors) else len(lines)
        members = set()
        for l in lines[k:stop]:
            if l.endswith("{ }"):
                mm = _MEMBER.search(l)
                if mm:
                    members.add(mm.group(1))
            elif ";" in l.split("//")[0]:
                # 字段声明:  public float r; // 0x0   /   public static Rect s_ToolTipRect; // 0x40
                mm = _FIELD.search(l)
                if mm:
                    members.add(mm.group(1))
        out.setdefault(name, set()).update(members)
    return out


def owner_name(r):
    try:
        row = r.Class.row
        if row is None:
            return ""
        tn = getattr(row, "TypeName", None)
        if tn is None:
            return ""
        ns = getattr(row, "TypeNamespace", None)
        ns = str(ns) if ns is not None else ""
        return f"{ns}.{tn}" if ns else str(tn)
    except Exception:
        return ""


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    dll = sys.argv[1]
    dump = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_DUMP
    meta = parse_dump(dump)

    pe = dnfile.dnPE(dll)
    md = pe.net.mdtables

    wanted = {
        "GUI", "GUILayout", "GUILayoutUtility", "GUILayoutOption", "GUIUtility",
        "GUIStyle", "GUIStyleState", "GUISkin", "GUIContent", "Event", "EventType",
        "Texture", "Texture2D", "Graphics", "Screen", "Rect",
        "Vector2", "Vector3", "Vector4", "Color", "Color32", "Mathf", "Quaternion",
        "MonoBehaviour", "GameObject", "Object", "Time", "Input", "KeyCode",
        "Application", "Debug", "Cursor",
    }

    refs = set()
    for r in md.MemberRef.rows:
        cls = owner_name(r)
        if not cls:
            continue
        short = cls.split(".")[-1]
        if short in wanted:
            refs.add((short, str(r.Name)))

    missing, present = [], []
    for cls, name in sorted(refs):
        (present if name in meta.get(cls, set()) else missing).append(f"{cls}.{name}")

    print(f"# {dll}")
    print(f"# 引用的 Unity 类成员 {len(refs)} 个: 元数据里有 {len(present)} / 缺失(桩) {len(missing)}\n")
    print("### 缺失 -> 一调用就抛 NotSupportedException, 需要用 Harmony 顶掉")
    for s in missing:
        print("   ", s)
    print("\n### 存在 -> 可直接用")
    for s in present:
        print("   ", s)
    return 0


if __name__ == "__main__":
    sys.exit(main())
