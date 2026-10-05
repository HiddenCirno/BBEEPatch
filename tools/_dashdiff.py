# -*- coding: utf-8 -*-
"""
**集合相减**找"某个动作独有"的对象 —— 不猜。

背景：用户要的东西是"**MP 冲刺（dashSkill/dashSkill2）过程里存在、而普通冲刺不存在**"的，
不管它是粒子/弹幕/VFX/贴图。手工比对必然出错（这个项目一晚上在"目标是谁"上栽了四次），
而 `实例化探针` 的每条记录都带**动作名** ⇒ 按动作分组做集合差，答案就是客观的。

数据来源（都带动作名）：
  `[实例化探针] 动作="X"  Instantiate|ParticleSystem.Play  "名字"  类型=…`
用法:
  _dashdiff.py                      # 默认对比 dashSkill(+2) 与 dash/dashAir/dash2/dashAir2/3/attackholdDashEX
  _dashdiff.py 日志路径
"""
import collections, io, os, re, sys

BASE = os.path.dirname(os.path.abspath(__file__))
DEF_LOG = os.path.join(BASE, "..", "..", "BepInEx", "LogOutput.log")
RE_INST = re.compile(r'\[实例化探针\] 动作="([^"]+)"\s+(\S+)\s+"([^"]+)"(?:\s+类型=(\S+))?(?:\s+根="([^"]*)")?')

MP = ("dashSkill", "dashSkill2")
OTHER = ("dash", "dash2", "dashAir", "dashAir2", "dashAir3", "attackholdDashEX")


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    log = sys.argv[1] if len(sys.argv) > 1 else DEF_LOG
    per = collections.defaultdict(set)
    how = {}
    for line in io.open(log, encoding="utf-8", errors="replace"):
        m = RE_INST.search(line)
        if m:
            act, kind, name = m.group(1), m.group(2), m.group(3)
            root = m.group(5) or ""
            per[act].add(name)
            how[(act, name)] = kind + ("  根=" + root if root else "")

    print("日志:", os.path.basename(log))
    print("各动作记录到的对象数（去重后）:")
    for a in sorted(per, key=lambda x: -len(per[x])):
        print("   %-22s %d 种" % (a, len(per[a])))

    mp = set()
    for a in MP:
        mp |= per.get(a, set())
    ot = set()
    for a in OTHER:
        ot |= per.get(a, set())

    only_mp = sorted(mp - ot)
    only_ot = sorted(ot - mp)
    both = sorted(mp & ot)

    print("\n" + "=" * 78)
    print("★ MP 冲刺(%s) 有、普通冲刺没有的对象 —— %d 种" % ("/".join(MP), len(only_mp)))
    if not only_mp:
        print("   （空 —— 说明这次没录到 MP 冲刺, 或者它确实没造新东西）")
    for n in only_mp:
        k = how.get((MP[0], n)) or how.get((MP[1], n)) or "?"
        print("    ● %-28s 来源=%s" % (n, k))

    print("\n普通冲刺有、MP 冲刺没有的 —— %d 种" % len(only_ot))
    for n in only_ot[:20]:
        print("    ○ %s" % n)
    print("\n两边都有 —— %d 种: %s" % (len(both), ", ".join(both[:24]) if both else "（空）"))


if __name__ == "__main__":
    main()
