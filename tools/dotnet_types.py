#!/usr/bin/env python3
"""列出 .NET 程序集里的类型/方法 (dnfile 解析元数据表, 不受 #Strings 后缀合并影响)。

用法:
    uv run --with dnfile python tools/dotnet_types.py <dll> [名字过滤]
"""
import sys
import dnfile


def load(path):
    pe = dnfile.dnPE(path)
    md = pe.net.mdtables
    out = []
    for r in md.TypeDef.rows:
        ns = str(r.TypeNamespace or "")
        nm = str(r.TypeName or "")
        full = f"{ns}.{nm}" if ns else nm
        names = [str(i.row.Name) for i in r.MethodList if i.row is not None]
        out.append((full, names))
    return out


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    path = sys.argv[1]
    filt = sys.argv[2] if len(sys.argv) > 2 else None

    types = load(path)
    print(f"# {path}")
    print(f"# TypeDef={len(types)}")
    hits = 0
    for full, names in types:
        if filt and filt.lower() not in full.lower():
            continue
        hits += 1
        print(f"\n=== {full}  ({len(names)} 方法) ===")
        for n in names:
            print("   ", n)
    if hits == 0:
        print("(没有匹配的类型)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
