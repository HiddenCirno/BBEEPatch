# -*- coding: utf-8 -*-
"""
把 _modding 里【属于我们自己的东西】整理进 /tmp/bbeepatch 这个克隆好的仓库。

收录什么 / 不收什么 —— 判据是"这是不是我们的产出"：
  收录: plugin 源码 / tools 的 .py 工具链 / 分析文档(.md, .txt)
  不收: dump/(289M, 游戏 IL2CPP 转储)  logs/  extracted/(游戏资源)
        js_src/(14M, 提取出来的游戏 JS —— 公开别人的游戏代码不合适)
        tools/bin|decomp|_dl|_save(下载与反编译产物)  *_cabindex.json(生成的索引)
        plugin/**/bin|obj(构建产物)

用法: python _mkrepo.py <目标仓库目录>
"""
import io
import os
import shutil
import sys

SRC = os.path.dirname(os.path.abspath(__file__)) + os.sep + ".."
SRC = os.path.abspath(SRC)
DST = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\HiddenSummer\AppData\Local\Temp\bbeepatch"

SKIP_DIRS = {
    "dump", "logs", "extracted", "js_src", ".git",
    "bin", "obj", "_dl", "_save", "decomp", "node_modules",
}
SKIP_SUFFIX = (".json",)          # 只跳 _cabindex.json 那类生成的索引


def keep(full):
    name = os.path.basename(full)
    if name in SKIP_DIRS:
        return False
    if name.endswith(SKIP_SUFFIX) and name.startswith("_"):
        return False
    if name.endswith((".pdb", ".dll", ".nupkg", ".cache")):
        return False
    return True


copied = {"n": 0, "b": 0, "skip": 0}
for root, dirs, files in os.walk(SRC):
    dirs[:] = [d for d in dirs if keep(os.path.join(root, d))]
    rel = os.path.relpath(root, SRC)
    if rel == ".":
        out_dir = os.path.join(DST, "docs")
    else:
        out_dir = os.path.join(DST, rel)
    os.makedirs(out_dir, exist_ok=True)
    for f in files:
        src = os.path.join(root, f)
        if not keep(src):
            copied["skip"] += 1
            continue
        # 只收这些类型
        if not (f.endswith((".cs", ".csproj", ".py", ".md", ".txt"))):
            copied["skip"] += 1
            continue
        shutil.copy2(src, os.path.join(out_dir, f))
        copied["n"] += 1
        copied["b"] += os.path.getsize(src)

print("复制 %d 个文件, %.1f MB; 跳过 %d 个" % (copied["n"], copied["b"] / 1048576.0, copied["skip"]))

# .gitignore：防止以后误提交大件
gi = io.open(os.path.join(DST, ".gitignore"), "w", encoding="utf-8", newline="\n")
gi.write("\n".join([
    "# 构建产物",
    "bin/",
    "obj/",
    "*.dll",
    "*.pdb",
    "",
    "# 游戏派生物（不进版本库）",
    "dump/",
    "logs/",
    "extracted/",
    "js_src/",
    "tools/_dl/",
    "tools/_save/",
    "tools/decomp/",
    "tools/_cabindex.json",
    "",
    "# 本地临时",
    "*.orig",
    "*.bak",
    "",
]))
gi.close()
print("已写 .gitignore")
