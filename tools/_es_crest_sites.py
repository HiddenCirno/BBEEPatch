# -*- coding: utf-8 -*-
"""从 ES 的 ActionLogicGroup(es_mono0.raw) 里捞出所有 CreateBullet 指令 —— 即所有纹章生成点。"""
import io, os, re, collections

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
RAW = os.path.join(BASE, "extracted", "es_mono0.raw")
OUT = os.path.join(BASE, "tools", "_es_crest_sites_out.txt")

raw = open(RAW, "rb").read()
L = [f"es ActionLogicGroup raw = {len(raw)} bytes"]

# 1) 所有 bullet_id 指令
pat = re.compile(rb"bullet_id\s*:\s*\d+[^\x00]*")
sites = []
for m in pat.finditer(raw):
    txt = m.group(0).split(b"\x00")[0].decode("utf-8", "replace")
    # 截到合理长度(指令串以一个 \0 结束)
    txt = txt.replace("\x00", "").strip()
    sites.append((m.start(), txt))

L.append(f"\n=== CreateBullet 指令 ({len(sites)}) ===")
for off, t in sites:
    L.append(f"  @{off:<8} {t}")

# 2) 去重看都用哪些 action
acts = collections.Counter()
ids = collections.Counter()
for _, t in sites:
    mm = re.search(r"bullet_id\s*:\s*(\d+)", t)
    if mm:
        ids[mm.group(1)] += 1
    mm = re.search(r'bullet_action\s*:\s*"?([A-Za-z0-9_]+)"?', t)
    if mm:
        acts[mm.group(1)] += 1
L.append(f"\n=== bullet_id 统计 ===")
for k, v in ids.most_common():
    L.append(f"  {k}: {v}")
L.append(f"\n=== bullet_action 统计 ===")
for k, v in acts.most_common():
    L.append(f"  {k}: {v}")

# 3) 全部 action 名(用来找"向面朝方向移动"的变体)
L.append("\n=== 资源路径 Role/Es/*.ab 出现次数 (前 80) ===")
paths = collections.Counter(p.decode("ascii") for p in re.findall(rb"Role/Es/[A-Za-z0-9_]+", raw))
for k, v in paths.most_common(80):
    L.append(f"  {v:>4}  {k}")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT)
print(f"指令 {len(sites)} 条, 不同 bullet_id {len(ids)}, 不同 action {len(acts)}")
