# -*- coding: utf-8 -*-
"""修 Plugin.cs 里被 heredoc 毁掉的两个绑定（\\n 变成了真换行 → CS1010）。

教训（本项目第 N 次）：**含中文/转义符的脚本一律 Write 成 .py 再跑**，
不要用 `python - <<EOF` 内联 —— bash 会先把 `\\n` 变成 `\n`，python 再变成真换行。
"""
import io

PL = r"I:\SteamLibrary\steamapps\common\BlazblueEntropyEffect\_modding\plugin\BlazblueJsPatch\Plugin.cs"
lines = io.open(PL, encoding="utf-8").read().split("\n")

start = next(i for i, l in enumerate(lines) if "RecolorConfig.PrivateCopy = Config.Bind" in l)
end = next(i for i in range(start, len(lines)) if "只在调试时打开。\");" in lines[i])

NEW = [
    '            RecolorConfig.PrivateCopy = Config.Bind(esec, "PrivateCopy", true,',
    '                "【私有副本】把加载到的特效 prefab 复制一份, 只染副本, 再把副本当资产返回。\\n" +',
    '                "为什么必须: 材质是【全局共享】的 —— 直接改一份 Effect/Common 的材质,\\n" +',
    '                "所有用到它的特效(包括玩家真去装备的那个换色皮肤)都会跟着变, 等于污染游戏资产。\\n" +',
    '                "插值器对象同理(Instantiate 不深拷贝 SerializeReference 数组, 克隆里那份还指着原对象)。\\n" +',
    '                "复制失败会退回\\"直接染原资产\\"并在日志留痕(功能优先)。");',
    '            RecolorConfig.TintSharedMaterials = Config.Bind(esec, "TintSharedMaterials", false,',
    '                "直接写【共享】材质资产。默认关 —— 会外溢到所有使用者(污染游戏资产), " +',
    '                "副本路已经覆盖同样的效果。只在调试时打开。");',
]

lines[start:end + 1] = NEW
io.open(PL, "w", encoding="utf-8").write("\n".join(lines))
print("修好了 第 %d~%d 行 -> %d 行" % (start + 1, end + 1, len(NEW)))
