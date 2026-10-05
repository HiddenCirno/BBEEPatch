# -*- coding: utf-8 -*-
"""把 es.ab 里每个 ChangeSkill 脚本调用归属到它所在的动作块。

动作块头 = 长度前缀字符串 "es" 后面紧跟的那一个字符串(动作名)。
把相邻的 {Function, Params} 对扫出来, 再用离它最近的前一个 "es" 头定归属。
"""
import io, os, re, struct

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
raw = open(os.path.join(BASE, "extracted", "es_mono0.raw"), "rb").read()
OUT = os.path.join(BASE, "tools", "_changeskill_out.txt")


def markers(raw, group):
    gb = group.encode()
    pat = re.compile(b"\\x%02x\\x00\\x00\\x00" % len(gb) + re.escape(gb) + b"\\x00" * ((-len(gb)) % 4))
    out = []
    for m in pat.finditer(raw):
        n = m.end()
        ln = int.from_bytes(raw[n:n + 4], "little") if n + 4 <= len(raw) else 0
        if 1 <= ln <= 40:
            b = raw[n + 4:n + 4 + ln]
            if all(32 <= c < 127 for c in b):
                out.append((m.start(), b.decode()))
    return out


marks = markers(raw, "es")


def enclosing(off):
    act = "?"
    for mo, nm in marks:
        if mo < off:
            act = nm
        else:
            break
    return act


# 字符串表
strs = []
i, n = 0, len(raw)
while i + 4 <= n:
    ln = struct.unpack_from("<I", raw, i)[0]
    if 1 <= ln <= 400 and i + 4 + ln <= n:
        b = raw[i + 4:i + 4 + ln]
        if all(32 <= c < 127 or c in (9, 10, 13) for c in b):
            strs.append((i, b.decode("ascii")))
            i += 4 + ((ln + 3) // 4) * 4
            continue
    i += 1

L = []
per_action = {}
for k in range(len(strs) - 1):
    oa, fa = strs[k]
    ob, pa = strs[k + 1]
    if ob != oa + 4 + ((len(fa) + 3) // 4) * 4:
        continue
    if ":" not in pa or not re.match(r'^[A-Za-z_]\w*$', fa):
        continue
    act = enclosing(oa)
    per_action.setdefault(act, []).append((fa, pa))

L.append(f"动作块数 {len(marks)}; 有脚本调用的动作 {len(per_action)}")
for act in sorted(per_action):
    L.append(f"\n===== [{act}] =====")
    for fa, pa in per_action[act]:
        L.append(f"   {fa}({pa})")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))

# 只把含 ChangeSkill / SkillChainReset 的打出来
print("=== 含 ChangeSkill 的动作块 ===")
for act in sorted(per_action):
    hits = [(f, p) for f, p in per_action[act] if f in ("ChangeSkill", "SkillChainReset", "SkillSetCD")]
    if hits:
        print(f"\n[{act}]")
        for f, p in hits:
            print(f"   {f}({p})")
print("\n-> 全量已写入 tools/_changeskill_out.txt")
