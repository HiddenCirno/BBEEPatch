# -*- coding: utf-8 -*-
"""
**多来源**集合相减：找出"MP 冲刺(dashSkill/dashSkill2) 有、普通冲刺没有"的东西。

为什么要有第二版：第一版只读了 `实例化探针` 一个来源，而特效是**池化复用**的 ——
MP 冲刺时只是"重新激活/播放"，不一定有新 Instantiate ⇒ 差集算出来是空的、白跑一轮。
这份日志里其实有**三个**带动作名的来源，合起来才完整：

  ① `[冲刺解剖] ═════ <动作> ▸ 特效 "<名字>"`      —— 动作 → 特效
  ② `[弹幕探针] startAction="<动作>" … LogicRes="<资源>"` —— 动作 → 弹幕（弹幕自带 startAction）
  ③ `[实例化探针] 动作="<动作>" <方式> "<名字>"`     —— 动作 → 被造/被播放的对象

用法: _dashdiff2.py [日志路径]
"""
import collections, io, os, re, sys

BASE = os.path.dirname(os.path.abspath(__file__))
DEF_LOG = os.path.join(BASE, "..", "..", "BepInEx", "LogOutput.log")
RE_ANAT = re.compile(r'冲刺解剖[^\]]*\] ═+ (.+?) ▸ (特效|弹幕) "([^"]+)"')
RE_BUL = re.compile(r'\[弹幕探针\] idx=(\d+) startAction="([^"]*)"')
RE_INST = re.compile(r'\[实例化探针\] 动作="([^"]+)"\s+(\S+)\s+"([^"]+)"(?:.*?根="([^"]*)")?')
RE_TIME = re.compile(r'GetTimeScaleByTime\("([^"]+)"')

MP = ("dashSkill", "dashSkill2")
OTHER = ("dash", "dash2", "dashAir", "dashAir2", "dashAir3", "attackholdDashEX")


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    log = sys.argv[1] if len(sys.argv) > 1 else DEF_LOG
    eff = collections.defaultdict(set)      # 动作 -> 特效名
    bul = collections.defaultdict(set)      # 动作 -> 弹幕资源
    obj = collections.defaultdict(set)      # 动作 -> 对象名(带来源)
    ran = collections.Counter()             # 动作出现过（变速日志）

    for line in io.open(log, encoding="utf-8", errors="replace"):
        m = RE_ANAT.search(line)
        if m:
            if m.group(2) == "特效":
                eff[m.group(1)].add(m.group(3))
            else:
                obj[m.group(1)].add("弹幕:" + m.group(3))
        m = RE_BUL.search(line)
        if m:
            bul[m.group(2)].add("idx=%s" % m.group(1))
        m = RE_INST.search(line)
        if m:
            obj[m.group(1)].add("%s:%s%s" % (m.group(2), m.group(3),
                                             ("(根=%s)" % m.group(4)) if m.group(4) else ""))
        m = RE_TIME.search(line)
        if m:
            ran[m.group(1)] += 1

    print("日志:", os.path.basename(log))
    print("\n[v] 日志里出现过的动作（变速日志统计，前 20）:")
    for a, c in ran.most_common(20):
        print("     %-24s ×%d" % (a, c))

    print("\n[解剖/探针] 按动作归类:")
    for a in sorted(set(list(eff) + list(bul) + list(obj))):
        print("     %-22s 特效=%-2d 弹幕=%-2d 对象=%-3d" % (a, len(eff[a]), len(bul[a]), len(obj[a])))

    def dump(title, A, B, nameA, nameB):
        a = set()
        for k in A:
            a |= A.get(k, set())
        b = set()
        for k in B:
            b |= B.get(k, set())
        only = sorted(a - b)
        print("\n" + "=" * 80)
        print("%s —— %d 个" % (title, len(only)))
        if not only:
            print("     （空）")
        for x in only:
            print("     ● %s" % x)
        print("     两边都有 %d 个: %s" % (len(a & b), ", ".join(sorted(a & b)[:16])))

    dump("★【特效】MP 冲刺有、普通冲刺没有", {k: eff[k] for k in MP}, {k: eff[k] for k in OTHER}, "MP", "普通")
    dump("★【弹幕】MP 冲刺有、普通冲刺没有", {k: bul[k] for k in MP}, {k: bul[k] for k in OTHER}, "MP", "普通")
    dump("★【对象】MP 冲刺有、普通冲刺没有", {k: obj[k] for k in MP}, {k: obj[k] for k in OTHER}, "MP", "普通")


if __name__ == "__main__":
    main()
