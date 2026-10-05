#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
本地化查表。

localization_chs.ab 的内部格式: 连续的 (uint32 keyHash, uint32 len, utf8 string) 记录,
keyHash = LocalizationManager.CalculateTagHash(key)  (与 noah_codec.calculate_hash 同函数)。

已知的键格式:
    InheritSkill_<id>       传承技能名
    InheritSkillDesc_<id>   传承技能描述
    ActorActionName_<id>    动作名
    ActorActionDesc_<id>    动作描述
    TriggerName_<id>        天赋触发器名   <- 潜能/天赋的实际名字在这里
    TriggerDesc_<id>        天赋触发器描述
    BlessQuality_<n>        品质名 (1白 2蓝 3紫 4金 5红)
    PotentialName_<id>      部分潜能名
"""
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from noah_codec import calculate_hash  # noqa: E402

_CACHE = None


def _load():
    global _CACHE
    if _CACHE is not None:
        return _CACHE
    from save_editor import load_text_asset
    raw, _ = load_text_asset("data/localization/localization_chs.ab")
    d = raw
    m = {}
    i = 0
    while i < len(d) - 8:
        n = struct.unpack_from("<I", d, i)[0]
        if 0 < n < 400 and i + 4 + n <= len(d):
            try:
                s = d[i + 4:i + 4 + n].decode("utf-8")
                m[struct.unpack_from("<I", d, i - 4)[0]] = s
                i += 4 + n
                continue
            except Exception:
                pass
        i += 1
    _CACHE = m
    return m


def get(key):
    return _load().get(calculate_hash(key))


def raw(h):
    return _load().get(h & 0xFFFFFFFF)


QUALITY = {1: "白", 2: "蓝", 3: "紫", 4: "金", 5: "红"}


def quality_name(q):
    return QUALITY.get(q, f"?{q}")


def trigger_name(tid):
    return raw(calculate_hash("TriggerName_" + str(tid)))


def trigger_desc(tid):
    return raw(calculate_hash("TriggerDesc_" + str(tid)))


def bless_name(bid):
    """潜能/祝福名：先试 TriggerName_(id*10+1)，再试 PotentialName_<id>"""
    return trigger_name(bid * 10 + 1) or get("PotentialName_" + str(bid))


def inherit_skill_name(sid):
    return get("InheritSkill_" + str(sid))


def actor_action_name(aid):
    return get("ActorActionName_" + str(aid))


if __name__ == "__main__":
    for a in sys.argv[1:]:
        print(f"{a} -> {get(a)!r}")
