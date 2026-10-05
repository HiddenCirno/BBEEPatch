# BBEEPatch

《苍翼：混沌效应》(BlazBlue Entropy Effect / 91Act) 的运行时补丁与逆向分析工程。

- 目标环境：Unity 2022.3.62f2 / IL2CPP v31
- Mod 框架：BepInEx 6.0.0-be.788 + Il2CppInterop 1.5.3 + HarmonyX（.NET 6.0.7 CoreCLR）
- 全部改动都在**运行时**完成，不改磁盘上的游戏资源

## 目录

| 路径 | 内容 |
|---|---|
| `plugin/BlazblueJsPatch/` | BepInEx 插件本体（C#，`dotnet build -c Release`） |
| `tools/` | 逆向分析工具链（Python，基于 UnityPy / 自写的 IL2CPP 转储解析） |
| `docs/` | 分析文档。**`docs/PROJECT_STATE.md` 是主文档**，接手前先读它 |

## 插件结构

模块通过一张表装配（`Core/ModuleTable.cs`），想加删管线只改那里。分区：

| 分区 | 内容 |
|---|---|
| 战斗规则 | 冲刺无敌、完美闪避、跳跃⟷冲刺互重置、技能无耗 MP |
| 特效换色 | 特效换色管线、特效裁剪 |
| ES 机体性能 | 动作变速、连段模组（重排普攻）、纹章解放 |
| 诊断 | 动作结构转储、动作记录、弹幕探针、冲刺解剖、时停/现场捕获、现场检查器、叠色探针 |

### 面板热键

| 键 | 功能 |
|---|---|
| F5 | 慢放循环（1 → 0.1 → 0.02 → 0） |
| F6 | 时停 / 恢复（`Time.timeScale = 0`） |
| F7 | 捕获：把场上渲染器的名字画在屏幕对应位置 |
| F8 | 配置面板 |
| F9 | 弹幕实验台 |
| F10 | 现场检查器（在冻结的世界里开关/挪物体） |
| F11 | 叠色探针（读引擎自己的 `ReferenceCounterMap`） |

## 分析工具链（`tools/`）

```bash
python _disasm.py <方法名>            # 带 call 目标符号标注的反汇编
python _disasm.py --callers <方法名>  # 反向找调用点
python _tinterdump.py <特效名>        # 摊开一个 prefab 的全部插值器（原色 vs 各套皮肤）
python _fxfull.py <特效名> --dump     # 一个 prefab 的完整对象清单
python _skindiff_all.py               # 官方换色皮肤的全量差异
python _bulletscan.py                 # 扫日志：动作 → 弹幕
```

## 说明

- `dump/`（IL2CPP 转储）、`logs/`、`extracted/`、`js_src/` 等**游戏派生物不进版本库**（见 `.gitignore`）。
- 本项目只用于本机运行时的分析与模组开发。
