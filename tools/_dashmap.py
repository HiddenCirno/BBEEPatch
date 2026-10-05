# -*- coding: utf-8 -*-
"""
从日志里拼出【动作 → 拉起了哪些特效 / 哪些弹幕】的对照表。

为什么这么做而不是继续挂探针：
  想知道"某个动作到底发生了什么事"，日志里其实已经有两行现成的——
    `[特效换色:seen] createVisualEffect "…" owner=… 当前动作="XXX"`   ← 特效出生（我们加的绑定）
    `[弹幕探针] idx=N startAction="XXX" … conf[… LogicRes="YYY"]`      ← 弹幕出生（早就有了）
  把它们按动作聚合，就是一张**权威的调用表**，比推测快得多，而且不用重跑游戏。

用法: _dashmap.py [日志路径] [动作名关键字(可选)]
      _dashmap.py                         # 默认读 BepInEx/LogOutput.log, 列出所有动作
      _dashmap.py "" dash                 # 只看含 "dash" 的动作
"""
import io, os, re, sys, collections

BASE = os.path.dirname(os.path.abspath(__file__))
DEF_LOG = os.path.join(BASE, "..", "..", "BepInEx", "LogOutput.log")

RE_EFF = re.compile(r'\[特效换色:seen\] createVisualEffect "([^"]+)" .*?当前动作="([^"]*)"')
RE_BUL = re.compile(r'\[弹幕探针\] idx=(\d+) startAction="([^"]*)" .*?LogicRes="([^"]*)"')


def leaf(name):
    s = name
    if "(" in s:
        s = s[:s.index("(")]
    if "." in s:
        s = s.split(".")[-1]
    return s.strip()


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    log = sys.argv[1] if len(sys.argv) > 1 and sys.argv[1] else DEF_LOG
    key = sys.argv[2].lower() if len(sys.argv) > 2 and sys.argv[2] else ""

    eff = collections.defaultdict(collections.Counter)     # 动作 -> 特效 -> 次数
    bul = collections.defaultdict(collections.Counter)     # 动作 -> 弹幕(idx|res) -> 次数
    if not os.path.exists(log):
        print("找不到日志:", log); return
    for line in io.open(log, encoding="utf-8", errors="replace"):
        m = RE_EFF.search(line)
        if m:
            eff[m.group(2)][leaf(m.group(1))] += 1
        m = RE_BUL.search(line)
        if m:
            bul[m.group(2)]["%s(%s)" % (m.group(3), m.group(1))] += 1

    acts = sorted(set(eff) | set(bul))
    for a in acts:
        if key and key not in a.lower():
            continue
        print("=" * 84)
        print("动作 \"%s\"" % a)
        if eff.get(a):
            print("  特效 (%d 种):" % len(eff[a]))
            for n, c in eff[a].most_common():
                print("      %-34s ×%d" % (n, c))
        if bul.get(a):
            print("  弹幕 (%d 种):" % len(bul[a]))
            for n, c in bul[a].most_common():
                print("      %-34s ×%d" % (n, c))


if __name__ == "__main__":
    main()
