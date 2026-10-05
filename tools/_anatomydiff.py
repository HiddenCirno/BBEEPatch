# -*- coding: utf-8 -*-
"""
**值级对比**：同一个对象在【MP 冲刺】和【普通冲刺】下的解剖逐行比 —— 找出"数值层面的差别"。

为什么走到这一步：
  三个"有什么"的来源（特效名 / 弹幕 idx / 被实例化的对象）在 MP 冲刺和普通冲刺之间
  **完全一致**（集合差为空）⇒ 差别不在"有哪些对象", 而在"**同一批对象被怎么用**"
  —— 参数、颜色、粒子数、缩放的差异。
  而 `冲刺解剖` 恰好把每个节点的**组件+材质着色属性当前值+粒子 startColor** 都倒出来了
  ⇒ 把两次的解剖体**按行归一化后做差**, 差异就是答案（数值级的"独有"）。

用法: _anatomydiff.py [日志路径]
"""
import collections, io, os, re, sys

BASE = os.path.dirname(os.path.abspath(__file__))
DEF_LOG = os.path.join(BASE, "..", "..", "BepInEx", "LogOutput.log")
RE_HEAD = re.compile(r'冲刺解剖:([^\]]*)\] ═+ (.+?) ▸ (特效|弹幕|玩家本体) "([^"]+)" ═+')

MP = ("dashSkill", "dashSkill2")


def blocks(log):
    """→ {(动作, 类型, 名字): [行...]}"""
    out = collections.OrderedDict()
    cur = None
    for line in io.open(log, encoding="utf-8", errors="replace"):
        m = RE_HEAD.search(line)
        if m:
            cur = (m.group(2), m.group(3), m.group(4))
            out.setdefault(cur, [])
            continue
        if cur is None:
            continue
        if '冲刺解剖' not in line:
            continue
        s = line.split('] ', 1)[-1].rstrip()
        if '═════' in s:                    # 结束行
            cur = None
            continue
        out[cur].append(s)
    return out


def norm(line):
    """归一化：去掉不同实例间的数字尾巴，保留"对象名 + 属性=值"这种可比内容。"""
    s = re.sub(r'\(Instance\)', '', line)
    s = re.sub(r'\(Clone\)', '', s)
    return s.strip()


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    log = sys.argv[1] if len(sys.argv) > 1 else DEF_LOG
    bs = blocks(log)
    print("日志:", os.path.basename(log))
    print("解剖块（动作, 类型, 对象）:")
    for k, v in bs.items():
        print("   %-40s %d 行" % (str(k), len(v)))

    # 按 (类型, 名字) 分组，取 MP 侧与非 MP 侧各一条来比
    bykey = collections.defaultdict(dict)
    for (act, kind, name), body in bs.items():
        bykey[(kind, name)][act] = body

    for (kind, name), per in bykey.items():
        mp = [(a, b) for a, b in per.items() if any(a.startswith(x) for x in MP)]
        ot = [(a, b) for a, b in per.items() if not any(a.startswith(x) for x in MP)]
        if not mp or not ot:
            continue
        actA, bodyA = mp[0]
        actB, bodyB = ot[0]
        sa, sb = set(norm(x) for x in bodyA), set(norm(x) for x in bodyB)
        onlyA = sorted(sa - sb)
        onlyB = sorted(sb - sa)
        print("\n" + "=" * 84)
        print("### %s %s：\"%s\"(%d 行)  vs  \"%s\"(%d 行)" % (kind, name, actA, len(bodyA), actB, len(bodyB)))
        if not onlyA and not onlyB:
            print("    ⇒ 逐行完全一致（这个对象不是区分点）")
            continue
        print("   — 只在 [%s] 出现/不同 —— %d 行" % (actA, len(onlyA)))
        for x in onlyA[:40]:
            print("      M | %s" % x[:200])
        print("   — 只在 [%s] 出现/不同 —— %d 行" % (actB, len(onlyB)))
        for x in onlyB[:40]:
            print("      N | %s" % x[:200])


if __name__ == "__main__":
    main()
