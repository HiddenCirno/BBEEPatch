# -*- coding: utf-8 -*-
"""打印 minidump 的流目录 —— 排查"_mindump.py 扫不到栈"时先看这个。"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _mindump import load, streams

p = sys.argv[1]
buf = load(p)
print('文件大小', len(buf))
NAMES = {3: 'ThreadList', 4: 'ModuleList', 5: 'Memory64List', 6: 'Exception',
         7: 'SystemInfo', 9: 'MemoryList', 15: 'MiscInfo', 16: 'MemoryInfoList'}
for t in sorted(streams(buf)):
    for size, rva in streams(buf)[t]:
        print('stream %-4d %-14s size=%-10d rva=%-10d' % (t, NAMES.get(t, '?'), size, rva))
