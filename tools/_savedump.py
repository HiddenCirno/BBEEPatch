# -*- coding: utf-8 -*-
"""读苍翼的存档：LZ4 frame 包着的 protobuf。

依据（实测，不是猜）：`Save/1` 前 21 字节是
    04 22 4D 18   LZ4 帧魔数(0x184D2204)
    68            FLG: version=01, blockIndep=1, contentSize=1
    40            BD : blockMaxSize=64KB
    A8 DB 00 00 00 00 00 00   contentSize = 56232
    54            HC
    8D 9D 00 00   block size = 40333 (compressed)
之后就是 protobuf。字段名去 `Gen/pbdef.js`（JS 转储里的 proto 定义）查。

用法:
    python _savedump.py tree <文件> [层数]      # 通用把 protobuf 结构打出来
    python _savedump.py path <文件> <A.B.C>     # 顺着字段号往下钻(字段号用 . 连)
"""
import io, os, sys, json
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
import lz4.frame

S = os.path.expandvars(r"%USERPROFILE%\AppData\LocalLow\91Act\BlazBlueEntropyEffect\Steam\366115783")


def load(path):
    raw = io.open(path, "rb").read()
    if raw[:4] == b"\x04\x22\x4d\x18":
        return lz4.frame.decompress(raw), True
    return raw, False


def varint(b, off, end):
    v = 0; sh = 0
    while off < end:
        c = b[off]; off += 1
        v |= (c & 0x7F) << sh
        if not (c & 0x80):
            return v, off
        sh += 7
        if sh > 63:
            raise ValueError("varint too long")
    raise ValueError("varint 截断")


def fields(b, off=0, end=None):
    """把一段 protobuf 拆成 [(字段号, 类型, 值)]。type: v=varint s=string/LEN f32 f64"""
    if end is None:
        end = len(b)
    out = []
    while off < end:
        try:
            key, off = varint(b, off, end)
        except ValueError:
            break
        f, wt = key >> 3, key & 7
        if f == 0:
            break
        if wt == 0:
            v, off = varint(b, off, end); out.append((f, "v", v))
        elif wt == 2:
            ln, off = varint(b, off, end)
            if off + ln > end:
                raise ValueError("LEN 越界")
            out.append((f, "s", b[off:off+ln])); off += ln
        elif wt == 5:
            if off + 4 > end: raise ValueError("f32 越界")
            out.append((f, "f32", b[off:off+4])); off += 4
        elif wt == 1:
            if off + 8 > end: raise ValueError("f64 越界")
            out.append((f, "f64", b[off:off+8])); off += 8
        else:
            raise ValueError("wt %d 不支持" % wt)
    return out


def printable(s):
    try:
        t = s.decode("utf-8")
    except Exception:
        return None
    if all(32 <= ord(c) < 127 or c in "\r\n\t" for c in t):
        return t
    return None


def show(b, depth, maxdepth, indent="", budget=None):
    lines = []
    try:
        fs = fields(b)
    except Exception as e:
        return ["%s<解析失败: %s>" % (indent, e)]
    for f, t, v in fs:
        if t == "v":
            lines.append("%s#%d varint %d" % (indent, f, v))
        elif t in ("f32", "f64"):
            lines.append("%s#%d %s %s" % (indent, f, t, v.hex()))
        else:
            s = printable(v)
            if s is not None and len(s) < 200:
                lines.append("%s#%d str(%d) %r" % (indent, f, len(v), s))
            elif depth >= maxdepth:
                lines.append("%s#%d len(%d) …" % (indent, f, len(v)))
            else:
                lines.append("%s#%d len(%d) {" % (indent, f, len(v)))
                lines += show(v, depth + 1, maxdepth, indent + "  ")
                lines.append("%s}" % indent)
        if budget is not None and len(lines) > budget:
            lines.append("%s…(截断)" % indent)
            return lines
    return lines


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "tree"
    path = sys.argv[2] if len(sys.argv) > 2 else os.path.join(S, "Save", "1")
    data, was_lz4 = load(path)
    print("文件 %s\n  LZ4=%s  长度=%d" % (path, was_lz4, len(data)))
    if mode == "tree":
        n = int(sys.argv[3]) if len(sys.argv) > 3 else 3
        print("\n".join(show(data, 0, n, "", budget=400)))
    elif mode == "path":
        cur = data
        for part in sys.argv[3].split("."):
            want = int(part)
            fs = fields(cur)
            got = [v for f, t, v in fs if f == want and t == "s"]
            if not got:
                print("字段 %s 不在（这一层的字段号: %s）" % (part, sorted({f for f, _, _ in fs})))
                break
            cur = got[0]
            print("→ #%s len=%d" % (part, len(cur)))
        else:
            print("\n".join(show(cur, 0, 4, "", budget=300)))
