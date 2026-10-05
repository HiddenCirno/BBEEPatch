# -*- coding: utf-8 -*-
"""Definitive extraction for the ES emblem/bullet spawn system.

Parses es.ab (es_mono0.raw) and esbullet.ab (esbullet_mono.raw) ActionLogicGroup
serializations using the confirmed Unity string layout: uint32 little-endian
length prefix, ASCII bytes, zero pad to 4 bytes.

Each GameActionLogic stores its owner group name ("es" / "esbullet") as RoleName.
So a repeated group-name string marks a new action; the next string is the action
Name.  We segment the string stream on those markers and dump, per action:
  - script calls (Function + Params) incl. CreateBullet_*
  - ChangeAction / ChangeSkill / SetActionCD / RemoveBuff / AddBuff
  - VFX (Role/..., Effect/...) and hit ids (Hit/...)
  - expression conditions
"""
import io, os, re, struct, collections

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
EX = os.path.join(BASE, "extracted")
OUT = os.path.join(BASE, "tools", "_es_map_out.txt")

L = []


def strings(raw):
    out = []
    n = len(raw)
    i = 0
    while i + 4 <= n:
        ln = struct.unpack_from("<I", raw, i)[0]
        if 1 <= ln <= 96 and i + 4 + ln <= n:
            b = raw[i + 4:i + 4 + ln]
            if all(32 <= c < 127 for c in b):
                end = i + 4 + ln
                pad = (-ln) % 4
                if end + pad <= n and (pad == 0 or raw[end:end + pad] == b"\x00" * pad):
                    out.append((i, end + pad, b.decode("ascii")))
                    i = end + pad
                    continue
        i += 4
    return out


FUNCS = {
    "CreateBullet_", "CreateBullet", "CreateBulletX", "CreateBulletU",
    "CreateBulletm", "CreateBulletIfTriggerChange", "CreateBulletTarget",
    "ChangeAction", "ChangeSkill", "SetActionCD", "RemoveBuff_es", "RemoveBuff",
    "AddBuff", "AddBuff_es", "Destroy", "Splash", "SetParam", "CallFunc",
}


def segment(raw, group):
    ss = strings(raw)
    marks = [k for k, (_, _, s) in enumerate(ss) if s == group]
    blocks = []
    for a, k in enumerate(marks):
        name = ss[k + 1][2] if k + 1 < len(ss) else "?"
        end = marks[a + 1] if a + 1 < len(marks) else len(ss)
        blocks.append((name, ss[k:end]))
    return ss, blocks


def dump_blocks(title, raw, group, only_bullet=False):
    ss, blocks = segment(raw, group)
    L.append(f"\n\n#################### {title} ####################")
    L.append(f"  raw={len(raw)}B  strings={len(ss)}  actions={len(blocks)}  group={group!r}")

    bullet_actions = []
    for name, seg in blocks:
        strs = [s for _, _, s in seg]
        has_bullet = any(s.startswith("CreateBullet") for s in strs)
        calls = []
        for j, s in enumerate(strs):
            if s in FUNCS:
                p = strs[j + 1] if j + 1 < len(strs) else ""
                calls.append((s, p))
        vfx = [s for s in strs if s.startswith(("Role/", "Effect/"))]
        hits = [s for s in strs if s.startswith("Hit/") or s.startswith("hit/")]
        conds = [s for s in strs if ("CasterAction()" in s or "ActionLogicCondition_" in s
                                     or s.startswith("ParamV("))]
        if has_bullet:
            bullet_actions.append(name)
        if only_bullet and not has_bullet:
            continue
        L.append(f"\n--- [{name}]  vfx={list(dict.fromkeys(vfx))}  hits={list(dict.fromkeys(hits))}")
        if conds:
            L.append("    cond: " + " ;; ".join(dict.fromkeys(conds)))
        for f, p in calls:
            L.append(f"    {f}  {p}")
    if only_bullet:
        L.append(f"\n  >>> actions containing CreateBullet ({len(bullet_actions)}): {bullet_actions}")
    return ss, blocks


es_raw = open(os.path.join(EX, "es_mono0.raw"), "rb").read()
esb_raw = open(os.path.join(EX, "esbullet_mono.raw"), "rb").read()

dump_blocks("es.ab  (character ALG)", es_raw, "es", only_bullet=True)
dump_blocks("esbullet.ab  (emblem bullet ALG)", esb_raw, "esbullet", only_bullet=False)

io.open(OUT, "w", encoding="utf-8").write("\n".join(L))
print("done ->", OUT, "lines:", len(L))
