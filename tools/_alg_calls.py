# -*- coding: utf-8 -*-
"""从 ActionLogicGroup 原始数据里抽出【脚本调用】(ActionScriptCall)。

格式(已由前一轮确认):
    字符串 = [u32 小端长度][ASCII 字节][补 0 到 4 字节对齐]
    一次调用 = 两个【连续】字符串 {Function, Params}

所以只要把字符串表按顺序扫出来, 再找相邻对里 Params 含关键字的,
就能拿到 "哪个函数被调用、参数是什么"。
"""
import io, re, struct, sys

PATH = sys.argv[1] if len(sys.argv) > 1 else \
    r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\extracted\es_mono0.raw"
KEY = sys.argv[2] if len(sys.argv) > 2 else "CheckOrder"

d = open(PATH, "rb").read()
strs = []          # (offset, text)
i = 0
n = len(d)
while i + 4 <= n:
    ln = struct.unpack_from("<I", d, i)[0]
    if 1 <= ln <= 400 and i + 4 + ln <= n:
        raw = d[i + 4:i + 4 + ln]
        if all(32 <= b < 127 or b in (9, 10, 13) for b in raw):
            strs.append((i, raw.decode("ascii")))
            i += 4 + ((ln + 3) // 4) * 4
            continue
    i += 1

print(f"字符串总数 {len(strs)}")
hits = 0
for k in range(len(strs) - 1):
    off_a, fa = strs[k]
    off_b, pb = strs[k + 1]
    if off_b != off_a + 4 + ((len(fa) + 3) // 4) * 4:
        continue                      # 不是相邻的两个字符串
    if KEY not in pb:
        continue
    hits += 1
    print(f"\n[{hits}] @{off_a}")
    print(f"    Function = {fa!r}")
    print(f"    Params   = {pb!r}")

print(f"\n命中 {hits} 处 (关键字 {KEY!r})")

# 顺带把所有出现过的脚本函数名去重列出 —— 便于看全貌
funcs = {}
for k in range(len(strs) - 1):
    off_a, fa = strs[k]
    off_b, pb = strs[k + 1]
    if off_b != off_a + 4 + ((len(fa) + 3) // 4) * 4:
        continue
    if ":" in pb and re.match(r'^[A-Za-z_][\w]*$', fa):
        funcs.setdefault(fa, 0)
        funcs[fa] += 1
print("\n全部脚本函数名(出现次数):")
for f, c in sorted(funcs.items(), key=lambda x: -x[1]):
    print(f"   {c:4d}  {f}")
