# -*- coding: utf-8 -*-
"""从 JS 转储的 `Gen/pbdef.js`（protobuf.js 静态生成代码）里抽出 proto 定义。

依据（看代码得出的，不是猜）：静态生成的 encode 长这样 ——
    XXX.encode=function encode(e,t){return t=t||$Writer.create(),
        null!=e.cmd&&Object.hasOwnProperty.call(e,"cmd")&&t.uint32(8).int32(e.cmd), ... ,t}
所以 `t.uint32(<字段号>).<类型>(e.<字段名>)` 就是一条字段定义。

用法:
    python _pbdef.py find <关键字>          # 哪些消息/字段含这个关键字
    python _pbdef.py msg <消息名>           # 打印该消息的全部字段
    python _pbdef.py msgs                   # 列出所有消息名
"""
import io, os, re, sys
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

PB = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                  "..", "..", "BepInEx", "config", "BlazblueJsPatch", "sources", "Gen", "pbdef.js")

_txt = None


def text():
    global _txt
    if _txt is None:
        _txt = io.open(PB, encoding="utf-8", errors="replace").read()
    return _txt


# 字段: t.uint32(8).int32(e.cmd)   /  t.uint32(10).string(e.name)
FIELD = re.compile(r"\.uint32\((\d+)\)\.([a-zA-Z0-9_]+)\(([a-z])\.([A-Za-z0-9_]+)\)")
# ★ 消息类型的字段不是 (e.x) 而是 `.fork()`，名字写在前面。两种写法都要认:
#     null!=e.actors&&e.actors.length&&t.uint32(26).fork()
#     if(null!=e.actors&&e.actors.length)for(...)t.uint32(26).fork()...
# ⇒ 只抓 .fork() 的位置, 再往前找最近的 `e.<名字>`（限 240 字符内）
FORK = re.compile(r"t\.uint32\((\d+)\)\.fork\(\)")
# $root.STAttr.encode(e.baseAttrs[...]  —— 消息类型字段的"类型名 + 字段名"来源
ENC_REF = re.compile(r"\$root\.([A-Za-z0-9_$]+)\.encode\(e\.([A-Za-z0-9_]+)")
NAME = re.compile(r"e\.([A-Za-z0-9_]+)")
# 消息: Foo.encode=function encode(e,t){
ENC = re.compile(r"([A-Za-z0-9_$]+)\.encode=function encode\(([a-z]),([a-z])\)")


def messages():
    """{消息名: [(字段号, 类型, 字段名)]}"""
    t = text()
    out = {}
    marks = [(m.start(), m.group(1), m.group(2)) for m in ENC.finditer(t)]
    for i, (pos, name, var) in enumerate(marks):
        # ⚠ 只取 encode 体 —— 到下一个 `,X.encodeDelimited=` 为止。
        #   不然会把同一个消息的 decode 体也算进来, 那里的 fork/字段名会把字段号带偏
        #   (踩过: STFesActor 被解析出两条 #2、类型还互相矛盾)。
        stop = t.find(".encodeDelimited=", pos)
        end = stop if 0 < stop else (marks[i + 1][0] if i + 1 < len(marks) else len(t))
        body = t[pos:end]
        fs = []
        for mm in FIELD.finditer(body):
            num, typ, v, fname = mm.group(1), mm.group(2), mm.group(3), mm.group(4)
            if v != var:
                continue
            k = int(num); fs.append((k >> 3, typ, fname))
        # ★ 消息字段的真实写法（看代码得出）:
        #     $root.STAttr.encode(e.baseAttrs[r], t.uint32(50).fork()).ldelim()
        #     t.uint32(114).fork().uint32(8).uint32(o[r]), $root.STActiveBless.encode(e.activeBlesses[o[r]], ...)
        #   ⇒ 每个 `.fork()` 就近找一个 `$root.<类型>.encode(e.<名字` 即可(前后都找, 取最近的)。
        enc = [(m.start(), m.group(1), m.group(2)) for m in ENC_REF.finditer(body)]
        prev_end = -10 ** 9
        for mm in FORK.finditer(body):
            # map 字段的内层 `.fork()` 属于 map entry(key/value), 不是外层消息的字段 ——
            # 紧跟在另一个 .fork() 后面的就跳过(实测 activeBlesses 那个 map 会多出一条假字段)。
            if mm.start() - prev_end < 90:
                prev_end = mm.end()
                continue
            prev_end = mm.end()
            num = int(mm.group(1))
            best, bd = None, 10 ** 9
            for pos, ctype, cname in enc:
                dist = abs(pos - mm.start())
                if dist < bd:
                    bd, best = dist, (ctype, cname)
            if best is None or bd > 300:
                fs.append((num >> 3, "msg:?", "?"))
            else:
                fs.append((num >> 3, "msg:" + best[0], best[1]))
        if fs:
            out[name] = fs
    return out


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "msgs"
    M = messages()
    if mode == "msgs":
        print("消息总数", len(M))
        for n in sorted(M):
            print("   ", n, len(M[n]))
    elif mode == "msg":
        n = sys.argv[2]
        for num, typ, fname in sorted(M.get(n, [])):
            print("   #%-3d %-10s %s" % (num, typ, fname))
    elif mode == "find":
        kw = sys.argv[2].lower()
        print("=== 消息名含 %s ===" % kw)
        for n in sorted(M):
            if kw in n.lower():
                print("   %s (%d 字段)" % (n, len(M[n])))
        print("=== 字段名含 %s ===" % kw)
        hits = {}
        for n, fs in M.items():
            for num, typ, fname in fs:
                if kw in fname.lower():
                    hits.setdefault(n, []).append((num, typ, fname))
        for n in sorted(hits):
            print("   %-34s %s" % (n, ", ".join("#%d:%s" % (a, c) for a, _, c in sorted(hits[n])[:8])))
