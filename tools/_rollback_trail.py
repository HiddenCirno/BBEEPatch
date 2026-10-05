# -*- coding: utf-8 -*-
"""回滚"残影路"(ActorTrail) 这一轮的所有代码, 并把换色总开关重新打开。

回滚范围（按用户要求"把现在的回滚了"）：
  · RecolorPipeline: HookTrails / TrailPostfix / ProxyPostfix / _wantProxies / OFF_PROXY_TRAIL + 注册行
  · TintBrush:       TintTrailRenderers / _trailDone
  · RecolorConfig:   TrailTint / TrailNameFilter 字段
  · Plugin.cs:       这两个配置项的绑定
  · cfg:             TrailTint / TrailNameFilter 两个键(以及它们的注释块)
并顺手做：MountRecolor = false -> true（用户为了测特效关掉了换色总开关）
"""
import io, os, re, sys
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))   # _modding
PLUG = os.path.join(ROOT, "plugin", "BlazblueJsPatch")
CFG = os.path.join(ROOT, "..", "BepInEx", "config", "ace.bbee.jspatch.cfg")


def edit(path, fn):
    p = os.path.join(PLUG, path)
    s = io.open(p, encoding="utf-8").read()
    out = fn(s)
    io.open(p, "w", encoding="utf-8").write(out)
    print("  改了", path, len(s), "->", len(out))


def cut_block(s, start_anchor, end_anchor, label):
    i = s.find(start_anchor)
    if i < 0:
        print("  !! 找不到起点:", label); return s
    j = s.find(end_anchor, i)
    if j < 0:
        print("  !! 找不到终点:", label); return s
    return s[:i] + s[j:]


# ---------------- RecolorPipeline ----------------
def fix_pipeline(s):
    s = s.replace("        n += HookTrails(harmony);\n", "", 1)
    s = cut_block(s,
                  "    // ---------------------------------------------------------------- ②c 残影(ActorTrail)",
                  "    private static int _bulletLogged;",
                  "残影路整段")
    return s


# ---------------- TintBrush ----------------
def fix_tintbrush(s):
    s = cut_block(s,
                  "    /// <summary>\n    /// ★ 残影路（窄口径）",
                  "    internal static int TintMaterials(GameObject go, RecolorConfig.Target t)",
                  "TintTrailRenderers")
    return s


# ---------------- RecolorConfig ----------------
def fix_config(s):
    s = re.sub(r"    /// <summary>★ 残影路\(ActorTrail\)：[\s\S]*?internal static ConfigEntry<string> TrailNameFilter;\n",
               "", s, count=1)
    return s


# ---------------- Plugin.cs ----------------
def fix_plugin(s):
    s = re.sub(r"            RecolorConfig\.TrailTint = Config\.Bind\([\s\S]*?TrailNameFilter = Config\.Bind\([\s\S]*?\);\n",
               "", s, count=1)
    return s


edit("Pipelines/Recolor/RecolorPipeline.cs", fix_pipeline)
edit("Pipelines/Recolor/TintBrush.cs", fix_tintbrush)
edit("Pipelines/Recolor/RecolorConfig.cs", fix_config)
edit("Plugin.cs", fix_plugin)

# ---------------- cfg ----------------
s = io.open(CFG, encoding="utf-8").read()
s = re.sub(r"## ★【残影路】[\s\S]*?TrailNameFilter = [^\n]*\n\n?", "", s, count=1)
s = re.sub(r"^TrailTint = .*$\n", "", s, flags=re.M)
s = re.sub(r"^TrailNameFilter = .*$\n", "", s, flags=re.M)
s = re.sub(r"^MountRecolor = .*$", "MountRecolor = true", s, flags=re.M)
io.open(CFG, "w", encoding="utf-8").write(s)
print("  改了 cfg（去掉残影路两个键 + MountRecolor=true）")
