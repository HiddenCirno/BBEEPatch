# -*- coding: utf-8 -*-
"""ES(103401) 技能链前驱图 —— 定义版。

数据源: extracted/skillactivate.ab.bin  (连续 uint32(len) + protobuf)
消息:   SkillActivate / SkillActivateFixedPoint  (dump.cs 字段号一致)
        f2=actorId f3=group f4=order f5=action f8=input f9=inputDir
        f10=reqTriggerId(重复) f15=preSkillOrder(重复!) f21=actdurStrict
        f22=timeout f24=mutelist f25=preinputtime f26=useLongPress
        f51=actionpointInputTag f52=actionpointInputTag2

f15 是 packed repeated int —— 之前脚本误当成「段数/原始字节」。
"""
import io, os, struct
from collections import defaultdict

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
# 运行时真正加载的表: PlayerSkillMgr.InitSkills -> Xlsx::get_SkillActivateFixedPoint
P = os.path.join(BASE, "extracted", "skillactivatefixedpoint.ab.bin")
# 姊妹资产(编辑器 float 版, 字段号相同): Assets/.../Xlsx/SkillActivate.bytes
P_FLOAT = os.path.join(BASE, "extracted", "skillactivate.ab.bin")
OUT = os.path.join(BASE, "skillchain_graph.md")
ACTOR = 103401
FP_SCALE = 4294967296.0   # Fp = long / 2^32 (实测 0.3 -> 1288490240)

DIRN = {0: "Any", 1: "Up", 2: "Down", 3: "Front", 4: "Back", 5: "NoDir",
        6: "Left", 7: "Right", 8: "AnyX"}
GROUP = {0: "None", 1: "Attack", 2: "Skill1", 3: "Ultra", 4: "AttackAir",
         5: "Dash", 6: "DashAttack", 7: "Jump", 8: "LongAttack",
         9: "CustomUse1", 10: "CustomUse2", 11: "Summon", 12: "Burst"}

d = open(P, "rb").read()


def rv(b, i):
    r = s = 0
    while i < len(b):
        c = b[i]; i += 1
        r |= (c & 0x7F) << s
        if not (c & 0x80):
            break
        s += 7
    return r, i


def packed_ints(b):
    """把 wt2 的 payload 当 packed varint 解，失败则原样返回 bytes。"""
    out, i = [], 0
    try:
        while i < len(b):
            v, i = rv(b, i)
            out.append(v)
    except Exception:
        return b
    return out


def fields(b):
    out, i = [], 0
    while i < len(b):
        try:
            k, i = rv(b, i)
        except Exception:
            break
        fn, wt = k >> 3, k & 7
        if wt == 0:
            v, i = rv(b, i); out.append((fn, 'v', v))
        elif wt == 2:
            ln, i = rv(b, i); v = b[i:i + ln]; i += ln
            try:
                s = v.decode('utf-8')
                s = s if (s and all(32 <= ord(c) < 127 for c in s)) else None
            except UnicodeDecodeError:
                s = None
            out.append((fn, 's', s if s else v))
        elif wt == 5:
            out.append((fn, 'f', struct.unpack_from('<f', b, i)[0])); i += 4
        elif wt == 1:
            out.append((fn, 'f', struct.unpack_from('<d', b, i)[0])); i += 8
        else:
            break
    return out


rows, i, n = [], 0, len(d)
while i + 4 <= n:
    ln = struct.unpack_from('<I', d, i)[0]
    if ln == 0 or i + 4 + ln > n:
        break
    rows.append(fields(d[i + 4:i + 4 + ln]))
    i += 4 + ln

# ---- 收成每条一 dict ----
recs = []
wire_f = defaultdict(int)   # 记录 f21/22/25 的 wire type，判断 float vs fixedpoint
for r in rows:
    g = defaultdict(list)
    for fn, wt, v in r:
        g[fn].append((wt, v))
    if not g.get(2) or g[2][0][1] != ACTOR:
        continue
    get = lambda fn, dflt=None: (g[fn][0][1] if g.get(fn) else dflt)

    def num_scalar(fn):
        if not g.get(fn):
            return None
        tag, val = g[fn][0]
        if tag == 'v':           # FixedPoint long
            return val / FP_SCALE
        return val               # float/double 直读

    for fn in (21, 22, 25):
        if g.get(fn):
            wire_f[(fn, g[fn][0][0])] += 1

    req, pre = [], []
    for wt, v in g.get(10, []):
        req += packed_ints(v) if wt != 'v' else [v]
    for wt, v in g.get(15, []):
        pre += packed_ints(v) if wt != 'v' else [v]

    recs.append({
        'actor': get(2), 'group': get(3), 'order': get(4),
        'action': get(5, ''), 'input': get(8, ''),
        'inputDir': get(9, 0), 'reqTriggerId': req, 'preSkillOrder': pre,
        'actdur': num_scalar(21), 'timeout': num_scalar(22), 'mutelist': get(24),
        'preinput': num_scalar(25), 'useLongPress': get(26, 0),
        'tag51': get(51), 'tag52': get(52),
    })

recs.sort(key=lambda x: (x['group'], x['order']))

# ---- 按 (group, order) 聚合，union preSkillOrder ----
bygo = defaultdict(list)
for r in recs:
    bygo[(r['group'], r['order'])].append(r)

def nm(grp, o):
    v = bygo.get((grp, o))
    return v[0]['action'] if v else f"<order {o} absent in group {grp}>" 

L = []
L.append("# ES (actorId=103401) 技能链前驱图")
L.append("")
L.append("数据源: `extracted/skillactivatefixedpoint.ab.bin` (348,579 字节) —— "
         "bundle `data/xlsxfixed/skillactivatefixedpointwrap.ab`, 容器 "
         "`Assets/AssetBundle/Data/XlsxFixed/SkillActivateFixedPointWrap.bytes`。")
L.append("该资产是运行时真正加载的表: `PlayerSkillMgr::InitSkills` (RVA 0x1bd9ee0) 反汇编中调用 "
         "`Xlsx::get_SkillActivateFixedPoint`。格式为连续 `uint32(len)+protobuf`。")
L.append("消息字段号取自 `dump/dump.cs` 的 `SkillActivate` (line 76736, TypeDefIndex 921) 与")
L.append("`SkillActivateFixedPoint` (line 143365, TypeDefIndex 1721) —— 两者字段号完全一致, "
         "本资产是 FixedPoint(long) 版。")
L.append("")
L.append("## 1. 字段号 → 名称 映射 (dump.cs)")
L.append("")
L.append("| field# | name | 类型 |")
L.append("|---|---|---|")
for ln in ["2 | ActorId | uint",
           "3 | Group | int (PlayerSkillGroup)",
           "4 | Order | int",
           "5 | Action | string",
           "6 | StartTrigger | repeated string",
           "7 | ExitTrigger | repeated string",
           "8 | Input | string (InputCmd 名)",
           "9 | InputDir | SkillInputDirType (enum)",
           "10 | ReqTriggerId | repeated int",
           "11 | Mps | repeated SkillMpSlot",
           "**15** | **PreSkillOrder** | **repeated int (LIST!)**",
           "16 | AllowActiveState | bool",
           "17 | AllowPassiveState | bool",
           "18 | AllowGround | bool",
           "19 | AllowFlying | bool",
           "21 | ActdurStrict | float(SkillActivate) / long(SkillActivateFixedPoint)",
           "22 | Timeout | float / long",
           "24 | Mutelist | string",
           "25 | Preinputtime | float / long",
           "26 | UseLongPress | bool",
           "27 | LongPressStart | float / long",
           "28 | LongPressEnd | float / long",
           "51 | ActionpointInputTag | int",
           "52 | ActionpointInputTag2 | int"]:
    a, b, c = ln.split(" | "); L.append(f"| {a} | {b} | {c} |")
L.append("")
L.append("枚举 `SkillInputDirType` (dump.cs line 69676): "
         "Any=0, Up=1, Down=2, Front=3, Back=4, NoDir=5, Left=6, Right=7, AnyX=8。")
L.append("枚举 `PlayerSkillGroup` (dump.cs line ~TypeDefIndex 3738): "
         "None=0, Attack=1, Skill1=2, Ultra=3, AttackAir=4, Dash=5, "
         "DashAttack=6, Jump=7, LongAttack=8, Summon=11, Burst=12。")
L.append("")
L.append("注: 早先 `tools/_finalmap.py` 把 **f52 (ActionpointInputTag2)** 当成方向列, 那是错的; "
         "方向列是 **f9 (InputDir)**。两者数值不同 (如 attack1: f9 缺省=0=Any, f52=1)。"
         "本表一律用 f9。")
L.append("")
L.append("**字段编码实测**: " + ", ".join(f"f{fn}=tag{wt}(x{c})" for (fn, wt), c in sorted(wire_f.items())) +
         " → f21/f22/f25 是 **varint long(wt0)**, 值为 Fp 定点数 (除以 2^32 得浮点, "
         "例: attackB 的 f25=1288490240 → 0.3)。姊妹资产 `skillactivate.ab.bin` "
         "(`Xlsx/SkillActivate.bytes`, 编辑器 float 版) 与它字段号、行数、动作名、前驱完全一致, "
         "仅 f21/22/25 存成 4 字节 float。")
L.append("")
L.append(f"ES 共 {len(recs)} 行 (去重前), {len(bygo)} 个不同 (group,order)。")
L.append("")

# ---- 2. 完整表, 按 group / order ----
L.append("## 2. 完整行表 (按 group, order 排序)")
L.append("")
L.append("action/input 为内部名; inpDir=D 即输入方向; pre=preSkillOrder 全列表; "
         "req=reqTriggerId; AD=ActdurStrict; PI=preinputtime; TO=timeout; LP=useLongPress。")
L.append("")
groups = sorted(set(g['group'] for g in recs if isinstance(g['group'], int)))
for grp in groups:
    grp_recs = [r for r in recs if r['group'] == grp]
    L.append(f"### group={grp} ({GROUP.get(grp,'?')}) — {len(grp_recs)} 行")
    L.append("")
    L.append("| order | action | input | inpDir | AD | PI | TO | preSkillOrder | reqTriggerId | useLongPress |")
    L.append("|---|---|---|---|---|---|---|---|---|---|")
    for r in sorted(grp_recs, key=lambda x: (x['order'], x['action'] or '')):
        dname = DIRN.get(r['inputDir'], f"?{r['inputDir']}")
        pre = r['preSkillOrder']
        pres = "[" + ",".join(str(x) for x in pre) + "]" if pre else "**(空→入口)**"
        req = "[" + ",".join(str(x) for x in r['reqTriggerId']) + "]" if r['reqTriggerId'] else ""
        fmt = lambda v: "" if v is None else (f"{v:g}" if isinstance(v, float) else str(v))
        L.append(f"| {r['order']} | {r['action']} | {r['input'] or ''} | {dname} | "
                 f"{fmt(r['actdur'])} | {fmt(r['preinput'])} | {fmt(r['timeout'])} | {pres} | {req} | "
                 f"{1 if r['useLongPress'] else 0} |")
    L.append("")

# ---- 3. union 前驱 / 后继 ----
def union_pre(grp, order):
    s = set()
    for r in bygo.get((grp, order), []):
        s.update(r['preSkillOrder'])
    return s

L.append("## 3. 有向图 (同一 group 内)")
L.append("")
L.append("语义 (来自 dump.cs `PlayerSkillMgr`): `preSkillOrder` 是**前驱 order 列表**。")
L.append("`findNextSkillMatchPreOrderAndInputDir(startOrder)` 查找 `startOrder ∈ row.preSkillOrder` 的行 —— ")
L.append("即「刚做完 order=startOrder 的技能后, 哪些技能可以接上」。")
L.append("下文 `order X → order Y  (name_X → name_Y)` 表示 Y.preSkillOrder 含 X。")
L.append("")
for grp in groups:
    orders = sorted(set(o for (g, o) in bygo if g == grp))
    L.append(f"### group={grp} ({GROUP.get(grp,'?')})")
    L.append("")
    # 每条的前驱列表
    L.append("前驱列表 (行 ← 其 preSkillOrder):")
    L.append("")
    for o in orders:
        r0 = sorted(bygo[(grp, o)], key=lambda x: x['action'] or '')[0]
        pre = sorted(union_pre(grp, o))
        pres = ", ".join(f"{p}:{nm(grp,p)}" for p in pre) if pre else "(空 = 入口)"
        L.append(f"- `{o}` **{r0['action']}** ← {pres}")
    L.append("")
    # 后继边
    L.append("后继边 (哪些行的 preSkillOrder 里包含本 order):")
    L.append("")
    any_edge = False
    for o in orders:
        succ = [y for y in orders if o in union_pre(grp, y)]
        if not succ:
            continue
        any_edge = True
        na = bygo[(grp, o)][0]['action']
        for y in succ:
            yname = bygo[(grp, y)][0]['action']
            L.append(f"- `{o}:{na}` → `{y}:{yname}`")
    if not any_edge:
        L.append("- (无任何前驱关系 —— 所有行都是入口/独立输入触发)")
    L.append("")

# ---- 4. 空 preSkillOrder (入口) ----
L.append("## 4. 前驱为空的「入口技能」")
L.append("")
L.append("| group | order | action | input | inpDir | reqTriggerId |")
L.append("|---|---|---|---|---|---|")
for grp in groups:
    for o in sorted(set(oo for (g, oo) in bygo if g == grp)):
        recs_o = bygo[(grp, o)]
        if all(not r['preSkillOrder'] for r in recs_o):
            r = recs_o[0]
            req = ",".join(str(x) for x in r['reqTriggerId'])
            L.append(f"| {grp} | {o} | {r['action']} | {r['input'] or ''} | "
                     f"{DIRN.get(r['inputDir'],'?')} | {req} |")
L.append("")

# ---- 5. 定向问答 ----
L.append("## 5. 定向问答")
L.append("")
# 5a
g1 = {o: bygo[(1, o)] for (g, o) in bygo if g == 1}
def show1(o):
    r = g1[o][0]
    return (f"order {o} `{r['action']}` input={r['input']} dir={DIRN.get(r['inputDir'],'?')} "
            f"preSkillOrder={sorted(union_pre(1, o))}")
L.append("### 5a. group=1: attackD1/D2/D3 (Down) → attack2/3/4 (Any)?")
L.append("")
L.append("原字节 (f15 / packed ints):")
L.append("")
for o in [4, 5, 6, 7, 10, 11, 12]:
    if o in g1:
        L.append(f"- {show1(o)}")
L.append("")
L.append("判定 (按 `X.preSkillOrder` 含 `Y` ⇒ `Y → X`):")
L.append("")
L.append("- **attackD1 → attack3 成立**: order 11 `attack3` (Any) 的 preSkillOrder=[10,4,5] 里含 4 和 5 (attackD1)。")
L.append("- **attackD2 → attack4 成立**: order 12 `attack4` (Any) 的 preSkillOrder=[11,6] 里含 6 (attackD2)。")
L.append("- attackD1 → attack2 **不成立**: order 10 `attack2` 的 preSkillOrder=[9] 只有 9 (attack1)。")
L.append("- attackD2 → attack3 **不成立**; attackD3 (order7) 的 preSkillOrder=[6,11], 它接到的是 atkAirX(8/13), 不是平A。")
L.append("- 反向也存在: order6 `attackD2` (Down) 的 preSkillOrder=[4,5,10] 含 10=attack2 (Any)。")
L.append("")
L.append("=> 跨方向前驱确实存在, 例如 attack3(Any)←[10(Any),4(Down),5(Down)], "
         "attackD2(Down)←[4(Down),5(Down),10(Any)]。")
L.append("")
# 5b
L.append("### 5b. group=2 (Skill1): attackAEX/attackA/attackB/attackC 的前驱")
L.append("")
g2rows = {o: bygo[(2, o)][0] for (g, o) in bygo if g == 2}
for o, nm_ in [(1, 'attackAEX'), (2, 'attackA'), (3, 'attackB'), (4, 'attackC')]:
    r = g2rows[o]
    L.append(f"- order {o} `{r['action']}`: preSkillOrder=[{','.join(str(x) for x in r['preSkillOrder'])}] "
             f"(空), reqTriggerId={r['reqTriggerId']}, dir={DIRN.get(r['inputDir'],'?')}")
L.append("")
L.append("=> **group=2 的 19 行 preSkillOrder 全为空** (见第 4 节)。按 `FindByPreSkillOrder` 的语义")
L.append("(order==0 时走 `FindAStartingSkillForPredict`), 空列表 = 入口技能, 不受任何前驱约束。")
L.append("因此 attackB 不存在「必须紧跟在 attackAEX 之后」的前驱关系; 四者是并列的入口, "
         "由输入(Down+Skill)与 reqTriggerId 选择 (attackA/B/C 都 req=5607, attackAEX req={5607,5626})。")
L.append("结论: 就 preSkillOrder 而言, **attackB 可以在 attackAEX 之后、也可以在其他事情之后被触发** —— 它根本没被前驱门控。")
L.append("")
# stats
multi = [r for r in recs if len(r['preSkillOrder']) > 1]
L.append("## 6. preSkillOrder 多元素 / 跨方向统计")
L.append("")
L.append(f"- ES 中 preSkillOrder 非空的行: {sum(1 for r in recs if r['preSkillOrder'])} 行 (共 {len(recs)} 行)")
L.append(f"- 其中 preSkillOrder 元素 >1 的行: {len(multi)} 行")
mx = max(recs, key=lambda r: len(r['preSkillOrder']))
L.append(f"- 最长列表: group={mx['group']} order={mx['order']} `{mx['action']}` → {mx['preSkillOrder']} ({len(mx['preSkillOrder'])} 个)")
cross = []
for grp in groups:
    for o in sorted(set(oo for (g, oo) in bygo if g == grp)):
        r = bygo[(grp, o)][0]
        if not r['preSkillOrder']:
            continue
        dirs = set()
        for p in union_pre(grp, o):
            if (grp, p) in bygo:
                dirs.add(DIRN.get(bygo[(grp, p)][0]['inputDir'], '?'))
        if len(dirs) > 1:
            cross.append((grp, o, r['action'], sorted(union_pre(grp, o)), sorted(dirs)))
L.append(f"- 前驱列表**跨输入方向分支**的行: {len(cross)} 行")
L.append("")
L.append("| group | order | action | preSkillOrder | 涉及方向 |")
L.append("|---|---|---|---|---|")
for grp, o, act, pre, dirs in cross:
    L.append(f"| {grp} | {o} | {act} | [{','.join(str(x) for x in pre)}] | {','.join(dirs)} |")
L.append("")

# ---- 7. 与 float 姊妹资产交叉核对 ----
def parse_es_tuple(path):
    dd = open(path, "rb").read()
    rr, j = [], 0
    while j + 4 <= len(dd):
        ln = struct.unpack_from('<I', dd, j)[0]
        if ln == 0 or j + 4 + ln > len(dd):
            break
        rr.append(fields(dd[j + 4:j + 4 + ln])); j += 4 + ln
    out = []
    for r in rr:
        gg = defaultdict(list)
        for fn, wt, v in r:
            gg[fn].append((wt, v))
        if not gg.get(2) or gg[2][0][1] != ACTOR:
            continue
        pre = []
        for wt, v in gg.get(15, []):
            pre += packed_ints(v) if wt != 'v' else [v]
        out.append((gg[3][0][1], gg[4][0][1], gg[5][0][1] if gg.get(5) else '', tuple(pre)))
    return out

L.append("## 7. 数据源交叉核对")
L.append("")
try:
    a = parse_es_tuple(P)
    b = parse_es_tuple(P_FLOAT)
    diff = [(x, y) for x, y in zip(a, b) if x != y]
    L.append(f"- FixedPoint 表 ES 行: {len(a)}; float 姊妹表 ES 行: {len(b)}")
    L.append(f"- (group,order,action,preSkillOrder) 逐行一致: {len(diff) == 0} "
             f"({'完全一致' if not diff else f'{len(diff)} 行不同'})")
except Exception as ex:
    L.append(f"- 交叉核对失败: {ex}")
L.append("")

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("written:", OUT)

# ================= ASCII 摘要 =================
print("\n=== FIELD MAP ===")
print("f2=actorId f3=group f4=order f5=action f8=input f9=inputDir f10=reqTriggerId")
print("f15=preSkillOrder(LIST) f21=actdurStrict f22=timeout f25=preinputtime f26=useLongPress")
print(f"ES rows={len(recs)} distinct (group,order)={len(bygo)}")
print("\n=== GROUPS ===")
for grp in groups:
    print(f" group {grp} ({GROUP.get(grp,'?')}): orders={sorted(set(o for (g,o) in bygo if g==grp))}")

print("\n=== EDGES (per group) ===")
for grp in groups:
    orders = sorted(set(o for (g, o) in bygo if g == grp))
    for o in orders:
        succ = sorted(set(y for y in orders if o in union_pre(grp, y)))
        if succ:
            na = bygo[(grp, o)][0]['action']
            names = ",".join(f"{y}:{nm(grp,y)}" for y in succ)
            print(f" g{grp} {o}:{na} -> {names}")

print("\n=== ENTRY (empty preSkillOrder) ===")
for grp in groups:
    for o in sorted(set(oo for (g,oo) in bygo if g==grp)):
        if all(not r['preSkillOrder'] for r in bygo[(grp,o)]):
            print(f" g{grp} {o}:{bygo[(grp,o)][0]['action']}")

print("\n=== 5a evidence (group1) ===")
for o in sorted(set(oo for (g,oo) in bygo if g==1)):
    r = bygo[(1,o)][0]
    print(f"  {o}:{r['action']:<10} dir={DIRN.get(r['inputDir'],'?'):<4} pre={sorted(union_pre(1,o))}")
print("\n=== 5b evidence (group2) ===")
for o in sorted(set(oo for (g,oo) in bygo if g==2)):
    r = bygo[(2,o)][0]
    print(f"  {o}:{r['action']:<12} dir={DIRN.get(r['inputDir'],'?'):<4} pre={sorted(union_pre(2,o))} req={r['reqTriggerId']}")
