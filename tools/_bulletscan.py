# -*- coding: utf-8 -*-
"""从 BepInEx 日志里抽出「动作 -> 它发射的弹幕 action」对照表。

原理：弹幕探针行 ([弹幕探针] ... startAction="X") 是按时间顺序插在
日志里的，紧邻它之前的 【技能】"Y" / 【动作】"Y" 行就是当时在播的动作。
用法: python _bulletscan.py [日志路径]
"""
import io, re, sys, collections

LOG = sys.argv[1] if len(sys.argv) > 1 else r"BepInEx/LogOutput.log"

re_action = re.compile(u'\u3010\u52a8\u4f5c\u3011"([^"]*)"')
re_skill  = re.compile(u'\u3010\u6280\u80fd\u3011"([^"]*)"')
re_bullet = re.compile(u'\\[\u5f39\u5e55\u63a2\u9488\\]\\s*idx=(\\d+)\\s+startAction="([^"]*)"')
re_push   = re.compile(u'\\[\u63a8\u8fdb\\][^\\n]*?"([^"]*)"\\s*->\\s*"([^"]*)"')

cur_action = ""
cur_skill = ""
pairs = collections.OrderedDict()
by_action = collections.OrderedDict()

for line in io.open(LOG, encoding="utf-8", errors="replace"):
    m = re_action.search(line)
    if m: cur_action = m.group(1)
    m = re_skill.search(line)
    if m: cur_skill = m.group(1)
    m = re_push.search(line)
    if m: cur_action = m.group(2)
    m = re_bullet.search(line)
    if m:
        bullet = m.group(2)
        a = cur_skill or cur_action
        pairs[(a, bullet)] = pairs.get((a, bullet), 0) + 1
        by_action.setdefault(a, collections.OrderedDict())
        by_action[a][bullet] = by_action[a].get(bullet, 0) + 1

print("== by action ==")
for a, bs in by_action.items():
    print(u"  %-14s -> %s" % (a, u", ".join(u"%s(x%d)" % (b, c) for b, c in bs.items())))
print("\n== pairs ==")
for (a, b), c in pairs.items():
    print(u"  %-14s  %-8s x%d" % (a, b, c))
