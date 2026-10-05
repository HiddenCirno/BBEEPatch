# -*- coding: utf-8 -*-
"""
把 RVA 翻译成方法名 —— 用 dump.cs 里的 `// RVA: 0x... Offset: 0x... VA: 0x...` 注释表。

⚠ 帧上的返回地址是**调用点之后**的几个字节, 所以不能用精确匹配:
   要找的是"RVA 最大的、且 ≤ 该地址"的那个方法 —— 也就是**包含**它的那个函数。
   (dump.cs 里的 RVA 是方法起点, 精确匹配只会全军覆没, 这一点上吃过亏。)

用法: _rvawho.py 0x4E1FE9 0x4E2546 ...
"""
import os, re, sys, bisect

DUMP = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'dump', 'dump.cs')
# 两种写法, 别搞混:
#   `\t// RVA: 0x7C5970 Offset: ...`   ← 【方法】表, 我们要的就是它
#   `\t|-RVA: 0x5A57E0 Offset: ...`    ← 【泛型实例化】表, 夹在 /* GenericInstMethod */ 块里
# 另外 `// RVA: -1 Offset: -1` 表示没有机器码(接口/抽象), 必须排除, 否则会把
# "第一个方法之前" 的判定带偏。
# ⚠ 低于方法表最小 RVA 的地址 = il2cpp **运行时/助手函数**区, 不是游戏方法 —— 解析不出来是正常的。
LINE = re.compile(r'^\s*//\s*RVA:\s*(0x[0-9A-Fa-f]+)\s')


def load():
    entries = []          # (rva, 名字, 类名)
    cur_class = ''
    last_name = ''
    with open(DUMP, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            s = line.strip()
            if s.startswith('// Namespace:') or s.startswith('.class') or s.startswith('public class') \
               or s.startswith('internal class') or s.startswith('public sealed class'):
                cur_class = s[:120]
            m = LINE.search(line)
            if m:
                rva = int(m.group(1), 16)
                if rva in (-1, 0xFFFFFFFF):
                    continue
                entries.append([rva, '(?)', cur_class, 0])
                continue
            # RVA 注释的下一行就是签名
            if entries and entries[-1][1] == '(?)' and s and not s.startswith('//'):
                entries[-1][1] = s[:160]
    entries.sort(key=lambda e: e[0])
    return entries


def main():
    entries = load()
    rvas = [e[0] for e in entries]
    print('dump.cs 方法数: %d' % len(entries))
    for arg in sys.argv[1:]:
        a = int(arg, 16)
        i = bisect.bisect_right(rvas, a) - 1
        if i < 0:
            print('0x%X -> (在第一个方法之前)' % a); continue
        rva, name, cls, _ = entries[i]
        print('\n0x%X -> %s  (方法起点 0x%X, 调用点偏移 +0x%X)' % (a, name, rva, a - rva))
        print('      所属: %s' % cls)


if __name__ == '__main__':
    main()
