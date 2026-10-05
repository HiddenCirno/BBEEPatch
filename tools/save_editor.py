#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
苍翼：混沌效应 —— 存档编辑器 / 数据体(传承)修改器

存档格式 (已逆向确认):
    Save/<槽位> = LZ4 帧  (magic 04 22 4D 18, FLG 0x68 含 content size)
                  └─ 嵌套 DataSave protobuf
                     top { 1:"AutoSave", 2: [ DataSave{1:模型名, 2:数据...} ... ] }

数据体 (FesActor) = STFesActor, 存在 ModelPlayerNewFesActorPack 里:
    ModelPlayer.fesActorPack.dict[uid] -> STFesActor
    STFesActor.id               = 角色 id (field 2)
    STFesActor.inheritSkillIndex= 传承技能索引 (field 36)  -> BaseActorConf.Get(id).inheritSkill[index]
    STFesActor.inheritSkillLevel= 传承技能等级 (field 37)

用法:
    python save_editor.py tree                    显示存档整体结构
    python save_editor.py list                    列出所有数据体
    python save_editor.py skills <角色id>          列出该角色的可传承技能
    python save_editor.py set-inherit <uid> <技能id> [等级]
    python save_editor.py set-score <uid> <分值>
    python save_editor.py show <uid>               显示某个数据体全部字段
"""
import json
import os
import shutil
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
BASE = os.path.normpath(os.path.join(HERE, ".."))
GAME = os.path.normpath(os.path.join(BASE, ".."))
AB_DIR = os.path.join(GAME, "BlazblueEntropyEffect_Data", "StreamingAssets", "ab")
SAVE_ROOT = os.path.expandvars(r"%USERPROFILE%\AppData\LocalLow\91Act\BlazBlueEntropyEffect\Steam")

# ---------------------------------------------------------------- 存档头字段
F_UID, F_ACTOR, F_SCORE = 1, 2, 16
F_POTENTIALS, F_TALENTS, F_SKILLS = 15, 17, 26
F_BLESSES_SC, F_ACTIVE_BLESSES = 8, 14
F_INHERIT_IDX, F_INHERIT_LV = 36, 37
F_INHERIT_BLESS, F_INHERIT_BLESSES = 25, 30
BASE_ACTOR_INHERIT_SKILL = 27      # BaseActorConf.inheritSkill (packed repeated uint32)


# ================================================================ protobuf 树
# 表示: message = [ (field_number, wire_type, payload) ]
#   wire 0 -> payload 是 int ;  wire 1/5 -> payload 是 bytes ;  wire 2 -> payload 是 bytes
VARINT, I64, LEN, SGROUP, EGROUP, I32 = 0, 1, 2, 3, 4, 5


def read_varint(b, i):
    r = 0
    s = 0
    while True:
        c = b[i]
        i += 1
        r |= (c & 0x7F) << s
        if not (c & 0x80):
            return r, i
        s += 7
        if s > 70:
            raise ValueError("varint too long")


def write_varint(v):
    out = bytearray()
    while True:
        c = v & 0x7F
        v >>= 7
        if v:
            out.append(c | 0x80)
        else:
            out.append(c)
            return bytes(out)


def parse_msg(b, start=0, end=None):
    """返回 (fields, next_offset)。fields 保留原始顺序，未知字段不丢失。"""
    if end is None:
        end = len(b)
    out = []
    i = start
    while i < end:
        key, i = read_varint(b, i)
        num, wire = key >> 3, key & 7
        if num == 0:
            raise ValueError(f"bad field number 0 at {i}")
        if wire == VARINT:
            v, i = read_varint(b, i)
            out.append([num, wire, v])
        elif wire == I64:
            out.append([num, wire, b[i:i + 8]]); i += 8
        elif wire == LEN:
            n, i = read_varint(b, i)
            out.append([num, wire, b[i:i + n]]); i += n
        elif wire == I32:
            out.append([num, wire, b[i:i + 4]]); i += 4
        else:
            raise ValueError(f"unsupported wire {wire} (field {num})")
    return out, i


def ser_msg(fields):
    out = bytearray()
    for num, wire, v in fields:
        out += write_varint((num << 3) | wire)
        if wire == VARINT:
            out += write_varint(v)
        elif wire == LEN:
            out += write_varint(len(v)) + v      # ← LEN 必须补长度前缀
        else:
            out += v
    return bytes(out)


def get(fields, num):
    return [f for f in fields if f[0] == num]


def get1(fields, num, default=None):
    r = get(fields, num)
    return r[0][2] if r else default


def set_var(fields, num, value):
    """设置/新增一个 varint 字段(保留位置, 已存在则改值)。"""
    for f in fields:
        if f[0] == num and f[1] == VARINT:
            f[2] = value
            return fields
    fields.append([num, VARINT, value])
    return fields


def del_field(fields, num):
    return [f for f in fields if f[0] != num]


def as_sub(fields, num):
    """把 field num 的 LEN 载荷解析为子消息(取第一个)。"""
    f = get1(fields, num)
    if f is None:
        return None
    m, _ = parse_msg(f)
    return m


def pack_varints(values):
    """打包 repeated uint32 (packed 编码的载荷)。"""
    out = bytearray()
    for v in values:
        out += write_varint(v)
    return bytes(out)


# ================================================================ LZ4 帧
def lz4_decode(data):
    import lz4.frame
    if data[:4] != bytes.fromhex("04224d18"):
        raise ValueError(f"不是 LZ4 帧, 头 4 字节 = {data[:4].hex()}")
    return lz4.frame.decompress(data)


def lz4_encode(raw):
    import lz4.frame
    return lz4.frame.compress(raw, block_linked=False, content_checksum=False)


# ================================================================ 资源读取
_ab_index = None


def ab_index():
    global _ab_index
    if _ab_index is None:
        entries = json.load(open(os.path.join(AB_DIR, "merge.json"), encoding="utf-8"))
        by = {}
        for e in entries:
            by.setdefault(e["m"], []).append(e)
        idx = {}
        for cont, items in by.items():
            items.sort(key=lambda x: x["s"])
            total = os.path.getsize(os.path.join(AB_DIR, cont))
            for i, e in enumerate(items):
                nxt = items[i + 1]["s"] if i + 1 < len(items) else total
                idx[e["r"]] = (cont, e["s"], nxt - e["s"])
        _ab_index = idx
    return _ab_index


def load_text_asset(rel_path):
    """从 .m 容器取 TextAsset 内容; 带 NOAH 头的自动解密。"""
    sys.path.insert(0, HERE)
    from noah_codec import decode
    import UnityPy

    cont, off, size = ab_index()[rel_path]
    with open(os.path.join(AB_DIR, cont), "rb") as f:
        f.seek(off)
        blob = f.read(size)
    env = UnityPy.load(blob)
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        ta = obj.read()
        raw = ta.m_Script
        if isinstance(raw, str):
            # 二进制 TextAsset 必须用 surrogateescape 才能无损往返;
            # surrogatepass 会编成 CESU-8, 破坏 0x80-0xFF 的字节
            raw = raw.encode("utf-8", "surrogateescape")
        return decode(raw, ta.m_Name), ta.m_Name
    return None, None


# ================================================================ BaseActorConf
_base_actor_cache = None


def base_actor_conf():
    """返回 {actorId: {'name': str, 'inheritSkill': [id...]}}"""
    global _base_actor_cache
    if _base_actor_cache is not None:
        return _base_actor_cache
    raw, _ = load_text_asset("data/xlsx/baseactorconf.ab")
    if raw is None:
        _base_actor_cache = {}
        return _base_actor_cache
    res = {}
    i = 0
    while i + 4 <= len(raw):
        n = struct.unpack_from("<I", raw, i)[0]
        if n == 0 or i + 4 + n > len(raw):
            break
        try:
            fields, _ = parse_msg(raw, i + 4, i + 4 + n)
        except Exception:
            i += 1
            continue
        aid = get1(fields, 1)
        if aid is not None:
            name = get1(fields, 99)
            if isinstance(name, bytes):
                name = name.decode("utf-8", "replace")
            packed = get1(fields, BASE_ACTOR_INHERIT_SKILL)
            skills = []
            if packed:
                j = 0
                while j < len(packed):
                    v, j = read_varint(packed, j)
                    skills.append(v)
            res[aid] = {"name": name or "", "inheritSkill": skills}
        i += 4 + n
    _base_actor_cache = res
    return res


# ================================================================ 存档读写
def find_fes_actors(root_fields):
    """递归找出所有 STFesActor 消息(连同其所在容器), 返回 [(容器fields, 索引, 消息)]

    判定依据: 一条消息的 field 2 == 已知角色 id, 且带 field 1 (uid)。
    STFesActor 的 field 2 就是角色 id —— 这个白名单比"看字段有没有 36/37"
    可靠得多 (大量数据体并不带 inheritSkillIndex)。
    """
    known = set(base_actor_conf().keys())
    found = []

    def is_fes(m):
        first = get1(m, 1)
        if not isinstance(first, int):
            return False
        aid = get1(m, 2)
        if not isinstance(aid, int):
            return False
        if known:
            return aid in known
        # 配置表读不到时的兜底
        nums = {f[0] for f in m if f[1] == VARINT}
        return 6 in nums or 8 in nums or 15 in nums or 16 in nums

    def rec(fields, chain, depth=0):
        if depth > 12:
            return
        for idx, f in enumerate(fields):
            if f[1] != LEN:
                continue
            try:
                sub, _ = parse_msg(f[2])
            except Exception:
                continue
            if is_fes(sub):
                found.append((chain + [(fields, idx)], sub))
                continue
            rec(sub, chain + [(fields, idx)], depth + 1)

    rec(root_fields, [])
    return found


def apply_edit(chain, edited_msg):
    """把修改过的子消息逐层向上重新序列化, 返回(新的)根 fields。

    这一步是必须的: 嵌套消息在父消息里是以【原始字节】保存的,
    只改子消息对象、不往回写, 存盘时父层仍是旧字节 —— 存档会被写坏。
    """
    cur = edited_msg
    for parent_fields, idx in reversed(chain):
        parent_fields[idx][2] = ser_msg(cur)
        cur = parent_fields
    return cur


def load_save(path):
    raw = lz4_decode(open(path, "rb").read())
    fields, _ = parse_msg(raw)
    return raw, fields


def save_save(path, fields, backup=True):
    if backup:
        bak = path + ".bak"
        if not os.path.exists(bak):
            shutil.copy2(path, bak)
            print(f"[i] 已备份原存档 -> {bak}")
    payload = ser_msg(fields)
    open(path, "wb").write(lz4_encode(payload))
    print(f"[+] 已写入 {path}  ({len(payload)} 字节 protobuf)")


def slot_path(slot):
    return os.path.join(SAVE_ROOT, "*", "Save", str(slot))


def resolve_save(slot):
    import glob
    cands = glob.glob(slot_path(slot))
    if not cands:
        raise SystemExit(f"找不到存档槽 {slot}, 查找过: {slot_path(slot)}")
    return cands[0]


# ================================================================ 命令
def cmd_tree(path):
    raw, fields = load_save(path)
    print(f"存档: {path}")
    print(f"LZ4 解压后 {len(raw)} 字节")
    # 顶层: 1=AutoSave, 2=[各模型]
    for f in get(fields, 2):
        try:
            e, _ = parse_msg(f[2])
        except Exception:
            continue
        t = get1(e, 1)
        if isinstance(t, bytes):
            t = t.decode("utf-8", "replace")
        n_sub = len([x for x in e if x[0] in (2, 3)])
        size = sum(len(x[2]) for x in e if x[1] == LEN)
        print(f"  {t:<34} 子节点={n_sub:<3} {size} 字节")


def cmd_list(path):
    raw, fields = load_save(path)
    actors = find_fes_actors(fields)
    conf = base_actor_conf()
    if not actors:
        print("存档里没有找到数据体 (可能还没通关过任何角色)")
        return
    print(f"共 {len(actors)} 个数据体:\n")
    print(f"{'uid':>6}  {'角色id':>7}  {'名称':<12} {'分值':>10}  {'传承技能':<24} {'等级'}")
    print("-" * 78)
    for _, m in actors:
        uid = get1(m, F_UID, 0)
        aid = get1(m, F_ACTOR, 0)
        sc = get1(m, F_SCORE)
        if isinstance(sc, bytes):
            sc = struct.unpack("<d", sc)[0]
        idx = get1(m, F_INHERIT_IDX, 0)
        lv = get1(m, F_INHERIT_LV, 0)
        info = conf.get(aid, {})
        sk = info.get("inheritSkill", [])
        cur = sk[idx] if idx < len(sk) else f"<越界 idx={idx}>"
        score_s = "-" if sc is None else str(round(sc))
        print(f"{uid:>6}  {aid:>7}  {info.get('name','?'):<10} {score_s:>10}  {str(cur):<24} Lv{lv}")


def cmd_skills(actor_id):
    actor_id = int(actor_id)
    conf = base_actor_conf()
    info = conf.get(actor_id)
    if not info:
        print(f"BaseActorConf 里没有角色 {actor_id}")
        return
    print(f"角色 {actor_id} ({info['name']}) 的可传承技能:")
    for i, s in enumerate(info["inheritSkill"]):
        print(f"   [{i}] 技能 id = {s}")


def cmd_set_inherit(path, uid, skill_id, level):
    uid, skill_id, level = int(uid), int(skill_id), int(level)
    raw, fields = load_save(path)
    actors = find_fes_actors(fields)
    conf = base_actor_conf()
    for chain, m in actors:
        if get1(m, F_UID) != uid:
            continue
        aid = get1(m, F_ACTOR)
        sk = conf.get(aid, {}).get("inheritSkill", [])
        if skill_id in sk:
            idx = sk.index(skill_id)
        else:
            # 该技能不在角色的可传承表里 -> 追加到表尾并指向它(需要同时改配置, 见说明)
            print(f"[!] 技能 {skill_id} 不在角色 {aid} 的 inheritSkill 表 {sk} 中")
            print(f"    仅改索引无法生效(客户端会用 BaseActorConf 校验)。请改用 JS 补丁方式,")
            print(f"    或先把技能 id 加进 BaseActorConf。")
            return
        set_var(m, F_INHERIT_IDX, idx)
        set_var(m, F_INHERIT_LV, level)
        root = apply_edit(chain, m)
        print(f"[+] 数据体 uid={uid} (角色 {aid} {conf[aid]['name']}) "
              f"传承技能 -> id={skill_id} (index {idx}) Lv{level}")
        save_save(path, root)
        return
    print(f"没找到 uid={uid} 的数据体")


def cmd_set_score(path, uid, score):
    uid, score = int(uid), float(score)
    raw, fields = load_save(path)
    for chain, m in find_fes_actors(fields):
        if get1(m, F_UID) != uid:
            continue
        set_var(m, F_SCORE, 0)  # 占位, 下面直接改 wire
        for f in m:
            if f[0] == F_SCORE:
                f[1] = I64
                f[2] = struct.pack("<d", score)
                break
        else:
            m.append([F_SCORE, I64, struct.pack("<d", score)])
        root = apply_edit(chain, m)
        print(f"[+] 数据体 uid={uid} 分值 -> {score}")
        save_save(path, root)
        return
    print(f"没找到 uid={uid} 的数据体")


def cmd_show(path, uid):
    uid = int(uid)
    raw, fields = load_save(path)
    conf = base_actor_conf()
    for _, m in find_fes_actors(fields):
        if get1(m, F_UID) != uid:
            continue
        aid = get1(m, F_ACTOR)
        print(f"=== 数据体 uid={uid}  角色 {aid} ({conf.get(aid,{}).get('name','?')}) ===")
        for num, wire, v in m:
            if wire == VARINT:
                print(f"  field {num:<3} varint  {v}")
            elif wire == I64:
                d = struct.unpack("<d", v)[0]
                f32 = struct.unpack("<f", v[:4])[0]
                print(f"  field {num:<3} i64     double={d}  (float32={f32})")
            elif wire == LEN:
                try:
                    sub, _ = parse_msg(v)
                    if len(sub) and sum(1 for x in sub if x[0] <= 40) == len(sub):
                        print(f"  field {num:<3} msg({len(v)}) 子字段 {[x[0] for x in sub][:12]}")
                        continue
                except Exception:
                    pass
                txt = v.decode("utf-8", "replace")
                if all(32 <= ord(c) < 127 or c in "\r\n\t" for c in txt[:40]) and len(v) < 80:
                    print(f"  field {num:<3} str     {txt!r}")
                else:
                    print(f"  field {num:<3} bytes   {v[:32].hex(' ')}")
        return
    print(f"没找到 uid={uid}")


# ================================================================ 潜能 / 传承
# STBless { 1:id, 2:quality, 3:active, 4:timestamp, 5:source,
#           6: 子消息列表, 其 f3/f4 = quality }
F_BLESSES, F_INHERIT_BLESSES = 8, 30


def _set_bless_quality(m, q):
    set_var(m, 2, q)
    for f in get(m, 6):
        try:
            sub, _ = parse_msg(f[2])
        except Exception:
            continue
        set_var(sub, 3, q)
        set_var(sub, 4, q)
        f[2] = ser_msg(sub)


def _clone_bless(tmpl):
    return [[f[0], f[1], f[2]] for f in tmpl]


def _make_bless(bid, q, tmpl=None):
    if tmpl is not None:
        m = _clone_bless(tmpl)
    else:
        ts = max((get1(tmpl, 4, 0) if tmpl else 0), 0) or 1766858686
        m = [[1, VARINT, bid], [2, VARINT, q], [3, VARINT, 1],
             [4, VARINT, ts], [5, VARINT, 4],
             [6, LEN, ser_msg([[3, VARINT, q], [4, VARINT, q]])]]
    set_var(m, 1, bid)
    _set_bless_quality(m, q)
    return m


def _bless_entries(m, field):
    for f in get(m, field):
        try:
            s2, _ = parse_msg(f[2])
        except Exception:
            continue
        yield f, s2


def cmd_blesses(path, uid=None):
    import locale as loc
    raw, fields = load_save(path)
    conf = base_actor_conf()
    for _, m in find_fes_actors(fields):
        u = get1(m, F_UID)
        if uid is not None and u != uid:
            continue
        aid = get1(m, F_ACTOR)
        print(f"=== uid={u}  {conf.get(aid,{}).get('name','?')} (id={aid}) ===")
        for label, fld in (("blesses 自带的", F_BLESSES), ("inheritBlesses 传承的", F_INHERIT_BLESSES)):
            items = []
            for _, s2 in _bless_entries(m, fld):
                bid, q = get1(s2, 1), get1(s2, 2)
                items.append(f"{bid}/{loc.quality_name(q)}(q{q}) {loc.bless_name(bid) or '?'}")
            print(f"   {label:<22} {items if items else '[]'}")
        print()


def cmd_set_bless(path, uid, bid, q, field):
    import locale as loc
    uid, bid, q = int(uid), int(bid), int(q)
    raw, fields = load_save(path)
    conf = base_actor_conf()
    for chain, m in find_fes_actors(fields):
        if get1(m, F_UID) != uid:
            continue
        # 优先用同一个 bless 的既有条目做模板, 保留 field 6 结构
        tmpl = None
        for fld in (field, F_BLESSES, F_INHERIT_BLESSES):
            for _, s2 in _bless_entries(m, fld):
                if get1(s2, 1) == bid:
                    tmpl = s2
                    break
            if tmpl:
                break

        done = False
        for f, s2 in _bless_entries(m, field):
            if get1(s2, 1) != bid:
                continue
            old = get1(s2, 2)
            _set_bless_quality(s2, q)
            f[2] = ser_msg(s2)
            print(f"[+] uid={uid} 字段{field}: {bid} 品质 {loc.quality_name(old)}(q{old}) -> "
                  f"{loc.quality_name(q)}(q{q})  [{loc.bless_name(bid)}]")
            done = True
            break
        if not done:
            newm = _make_bless(bid, q, tmpl)
            m.append([field, LEN, ser_msg(newm)])
            print(f"[+] uid={uid} 字段{field}: 新增 {bid} 品质 {loc.quality_name(q)}(q{q})  "
                  f"[{loc.bless_name(bid)}]{'  (沿用模板)' if tmpl else '  (新建)'}")
        root = apply_edit(chain, m)
        save_save(path, root)
        return
    print(f"没找到 uid={uid} 的数据体")


# ================================================================ 皮肤/配色
# ModelPlayerNewSkinPack -> payload.f1 (STSkinPack) -> f1 = packed repeated uint32
# 皮肤配置: SkinUnlockConf { 1:id, 2:type(Avatar=210/Skin=250), 3:unlockType,
#                            6:convertRelationId, ... }
# 装备/显示判定: AvatarUtil.IsSkinUnlocked(id) == (skinPack.skins 含 id)
UNLOCK_NAMES = {1: "Resource(兑换)", 4: "Dlc", 5: "FightReward(bossRush)",
                7: "BaseActorUnlock", 10: "AchievementTaskReward",
                23: "DeadCellResource", 34: "CoopModeUnlock"}


def unpack_varints(b):
    out = []
    i = 0
    while i < len(b):
        v, i = read_varint(b, i)
        out.append(v)
    return out


def _skin_chain(fields, model_name="ModelPlayerNewSkinPack"):
    """返回 (chain, skinpack_fields, idx_of_packed)"""
    chain = []
    cur = fields
    for idx, f in enumerate(cur):
        if f[1] != LEN:
            continue
        try:
            e, _ = parse_msg(f[2])
        except Exception:
            continue
        t = get1(e, 1)
        if isinstance(t, bytes) and t.decode("utf-8", "replace") == model_name:
            chain.append((cur, idx))
            cur = e
            break
    else:
        return None, None, None
    for want in (2, 1):
        idx = next((i for i, f in enumerate(cur) if f[0] == want and f[1] == LEN), None)
        if idx is None:
            return None, None, None
        chain.append((cur, idx))
        cur = parse_msg(cur[idx][2])[0]
    sidx = next((i for i, f in enumerate(cur) if f[0] == 1), None)
    return chain, cur, sidx


def _skin_conf():
    """解析 skinunlockconf -> [(id, type, unlockType tuple, convertRelationId)]"""
    raw, _ = load_text_asset("data/xlsx/skinunlockconf.ab")
    out = []
    i = 0
    while i + 4 <= len(raw):
        n = struct.unpack_from("<I", raw, i)[0]
        if n == 0 or i + 4 + n > len(raw):
            break
        try:
            f, _ = parse_msg(raw, i + 4, i + 4 + n)
            packed = get1(f, 3)
            ut = tuple(unpack_varints(packed)) if packed else ()
            out.append((get1(f, 1), get1(f, 2), ut, get1(f, 6)))
        except Exception:
            pass
        i += 4 + n
    return out


def _actor_names():
    return {aid: v["name"] for aid, v in base_actor_conf().items()}


def cmd_skins(path, only_locked=True):
    import locale as loc
    raw, fields = load_save(path)
    chain, sp, sidx = _skin_chain(fields)
    if chain is None:
        print("没找到 ModelPlayerNewSkinPack")
        return
    owned = set(unpack_varints(sp[sidx][2])) if sidx is not None else set()
    names = _actor_names()
    print(f"存档已拥有 {len(owned)} 个皮肤/配色\n")
    from collections import Counter
    stat = Counter()
    locked = []
    for sid, typ, ut, cid in _skin_conf():
        has = sid in owned
        stat[(ut, has)] += 1
        if not has:
            locked.append((sid, typ, ut, cid))
    print("按解锁类型统计 (已拥有 / 未拥有):")
    for ut in sorted({k[0] for k in stat}, key=str):
        y = stat.get((ut, True), 0)
        n = stat.get((ut, False), 0)
        label = ", ".join(UNLOCK_NAMES.get(x, f"type{x}") for x in ut) or "无条件"
        print(f"   {label:<24} 已拥有 {y:>3}  未拥有 {n:>3}")
    if only_locked:
        print("\n未拥有的条目 (按解锁类型分组，示例):")
        from collections import defaultdict
        g = defaultdict(list)
        for sid, typ, ut, cid in locked:
            g[ut].append(sid)
        for ut, ids in sorted(g.items(), key=lambda kv: -len(kv[1])):
            label = ", ".join(UNLOCK_NAMES.get(x, f"type{x}") for x in ut) or "无条件"
            ex = []
            for sid in ids[:6]:
                hero = int(str(sid)[:6]) if len(str(sid)) >= 6 else 0
                ex.append(f"{sid}({names.get(hero, '?')})")
            print(f"   [{label}] {len(ids)} 个: {' '.join(ex)}")


def cmd_unlock_skins(path, mode):
    """mode: resource(只解锁兑换型) / all / <数字 unlockType>"""
    raw, fields = load_save(path)
    chain, sp, sidx = _skin_chain(fields)
    if chain is None:
        print("没找到 ModelPlayerNewSkinPack")
        return
    owned = set(unpack_varints(sp[sidx][2])) if sidx is not None else set()
    before = len(owned)
    added = []
    for sid, typ, ut, cid in _skin_conf():
        if sid in owned:
            continue
        ok = (mode == "all") or (mode == "resource" and 1 in ut) or \
             (mode.isdigit() and int(mode) in ut)
        if ok:
            owned.add(sid)
            added.append(sid)
    if not added:
        print("没有需要新增的条目")
        return
    if sidx is None:
        sp.append([1, LEN, pack_varints(sorted(owned))])
    else:
        sp[sidx][2] = pack_varints(sorted(owned))
    root = apply_edit(chain, sp)
    print(f"[+] 模式={mode}  新增 {len(added)} 个: {added[:10]}{' ...' if len(added) > 10 else ''}")
    print(f"    皮肤总数 {before} -> {len(owned)}")
    save_save(path, root)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return
    cmd = sys.argv[1]
    if cmd == "skills":
        cmd_skills(sys.argv[2]); return
    if cmd == "locale-name":          # locale-name 冲刺影子  -> 反查 key
        import locale as loc
        for term in sys.argv[2:]:
            print(f"{term} -> {loc.get(term)!r}")
        return
    slot = sys.argv[2] if len(sys.argv) > 2 else "1"
    path = resolve_save(slot)
    if cmd == "tree":
        cmd_tree(path)
    elif cmd == "list":
        cmd_list(path)
    elif cmd == "set-inherit":
        cmd_set_inherit(path, sys.argv[3], sys.argv[4], sys.argv[5] if len(sys.argv) > 5 else 1)
    elif cmd == "set-score":
        cmd_set_score(path, sys.argv[3], sys.argv[4])
    elif cmd == "show":
        cmd_show(path, sys.argv[3])
    elif cmd == "blesses":            # blesses [uid]
        cmd_blesses(path, int(sys.argv[3]) if len(sys.argv) > 3 else None)
    elif cmd == "set-ibless":         # set-ibless <uid> <blessId> <quality>   (改传承)
        cmd_set_bless(path, sys.argv[3], sys.argv[4], sys.argv[5], F_INHERIT_BLESSES)
    elif cmd == "set-bless":          # set-bless  <uid> <blessId> <quality>   (改自带)
        cmd_set_bless(path, sys.argv[3], sys.argv[4], sys.argv[5], F_BLESSES)
    elif cmd == "skins":              # skins [slot]
        cmd_skins(path)
    elif cmd == "unlock-skins":       # unlock-skins <slot> resource|all|<类型号>
        cmd_unlock_skins(path, sys.argv[3])
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
