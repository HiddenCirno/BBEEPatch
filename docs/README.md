# 苍翼：混沌效应 (BlazBlue Entropy Effect) 逆向分析报告

分析日期：2026-10-01
游戏版本：Unity 2022.3.62f2 / IL2CPP metadata v31 / 91Act

---

## 1. 总体架构

```
BlazblueEntropyEffect.exe
├── GameAssembly.dll            94 MB —— IL2CPP 编译产物 (24033 个类型)
├── UnityPlayer.dll
└── BlazblueEntropyEffect_Data/
    ├── il2cpp_data/Metadata/global-metadata.dat   24.7 MB, v31, 【未加密】
    ├── Plugins/x86_64/
    │   ├── puerts.dll          13 MB —— PuerTS (腾讯 JS/TS ↔ Unity 绑定)  = JS 桥接引擎
    │   ├── zf_cef.dll          94 MB —— 内置 Chromium (公告/内嵌浏览器)
    │   └── lz4.dll / lz4Managed  —— 存档压缩
    └── StreamingAssets/ab/
        ├── merge.json          60410 条索引 { r:逻辑路径, m:容器, s:偏移 }
        ├── m_1.m .. m_1030.m   约 9.6 GB —— 【多个标准 UnityFS bundle 顺序拼接】
        ├── gameconstconfig / manifest / shadervariantinfos
```

* **资源容器 `.m`**：不是自定义格式，就是若干未加密的 `UnityFS` bundle 首尾相接。
  `merge.json` 的 `s` 即 bundle 起始偏移，长度 = 下一条偏移 − 本条偏移。可直接用 AssetStudio/UnityPy 处理。
* **JS 桥接**：PuerTS。游戏逻辑以 TypeScript 编译后的 JS 存在 **1034 个 TextAsset** 里（`data/js/**.ab`）。
* **数据驱动**：`data/xlsx/*.ab`（192 个 Excel 配置）、`gameplay/{map,logic,heat}`（关卡/房间）。

---

## 2. NOAH 资产混淆 —— 已完全逆向 ✅

游戏自研框架 `NOAH.*` 对 TextAsset 与存档做了「混淆」（实为弱加密）。

### 2.1 调用链（源码位置已定位）

| 方法 | RVA |
|---|---|
| `NOAH.Asset.AssetManagerBase.GetBytes(TextAsset)` | `0x34F8090` |
| `NOAH.Asset.AssetManagerBase.DecodeBytes(byte[], uint)` | `0x34F71C0` |
| `NOAH.Asset.AssetManagerBase.EncodeBytes(byte[], uint)` | `0x34F75E0` |
| `NOAH.Asset.AssetManagerBase.ObfuscateBytes(ref byte[], uint, int, int)` | `0x34F9300` |
| `NOAH.Asset.AssetManagerBase.FastObfuscateBytes(...)` | `0x34F78B0` |
| `StringExtensions.CalculateHash(string)` | `0x3149320` |
| `NOAH.Core.IOUtil.MatchMagic / .cctor` | `0x34FFDE0` / `0x34FFFE0` |

```csharp
public byte[] GetBytes(TextAsset asset) {
    uint keyHash = StringExtensions.CalculateHash(asset.name);
    return DecodeBytes(asset.bytes, keyHash);
}
```

### 2.2 算法

**密钥派生**
```
h = 0x01234567
for each char c in name:  h = ((h ^ (byte)c) * 0x89ABCDEF) mod 2^32
keyHash = (h * 0x89ABCDEF) mod 2^32
```

**混淆（旋转式 4 字节 XOR，对合，加解密同函数）**
```
key   = keyHash 的小端 4 字节
accum = -1
for i in 0 .. n-1:
    if i % 4 == 0: accum += 1
    data[i] ^= key[(accum + i) % 4]        # 等价 key[(i + i//4) % 4]
```
> 关键点：密钥每 4 字节**轮转一位**，所以按 `i % 4` 分列看不到固定密钥——这也是最初误判为「非周期」的原因。

**容器**：`NOAH` (4 字节 magic) + 混淆后的正文。`keyHash == 0` 时不做任何处理。

工具：`tools/noah_codec.py`（`hash` / `dec` / `enc`）。

---

## 3. 存档格式 —— 已完全解析 ✅

### 3.1 主存档 `Save/<槽位>`（例：`Save/1`, 38056 B）

实为**标准 LZ4 帧**（`lz4` C# 库），魔数 `04 22 4D 18` = `0x184D2204` 小端。

| 偏移 | 内容 |
|---|---|
| 0x00 | LZ4 frame magic `04 22 4D 18` |
| 0x04 | FLG = `0x68`（版本 01、块独立、含 content size） |
| 0x05 | BD = `0x40`（最大块 64 KB） |
| 0x06 | uint64 content_size（解压后大小，实测 53199） |
| 0x0E | uint8 HC 头校验 |
| 0x0F | uint32 块长度 + LZ4 块数据 ... |
| 末尾 | `00 00 00 00` 结束标记 |

校验：`19 + blocksize + 4 == filesize` ✅

解压后是 **嵌套 DataSave protobuf**：
```
DataSave {
  1: "AutoSave"
  2: DataSave { 1: "ModelPlayer"
       2: DataSave { 1: "AutoSave", 2: <解锁/标记位>, 3: <场景坐标> }
       3: ... }
  3: DataSave { 1: "ModelBreedDungeon" ... }   // 本局肉鸽进度
  ...
}
```
已能逐字段读出（`PlotDialogue1` / `BUFA_30_QUEST` 等解锁标记、`SceneLobby` / `Test/Capital_F1_2` 场景名等）。
**无签名字段**：改动后只需重新 LZ4 压缩即可（content_size 与 HC 由 LZ4 帧自动生成）。

`ColdBackup/1/<时间戳>` 与 `Backup/1` 是同一格式的副本（22 份历史快照，可用于回滚）。

### 3.2 账号侧小文件（NOAH 包裹）

`PlayerData`(1090B) / `GameData`(65B) / `CloudData`(32B) / `GameVersionInfo`(69B) / `OAuthData`(358B)
均为 `NOAH + 混淆`。JS 侧保存流程见 `js_src/archive/archiveio.js`：

```js
static LoadFromFile(r){ let e = U.SafeRead(r);
    var a = csharp.lz4.DecompressWithRet(e);
    return PBDecode(pbdef.DataSave, JsUtil.ToArrayBuffer(a.Item1)); }
```

> ⚠️ 未完成项：这几个文件的 keyHash 用的**不是文件名**的 `CalculateHash`。已排除常见候选名。
> 主线不受影响——`Save/1` 已含全部游戏状态。可用 BepInEx hook `IOUtil.MatchMagic`/
> `AssetManagerBase.DecodeBytes` 在运行时直接打印 keyHash 与明文。

---

## 4. 反编译产物

| 产物 | 路径 | 说明 |
|---|---|---|
| C# 伪代码 | `dump/dump.cs` | 49 MB，128 万行，24033 个类型，含 RVA |
| 头文件 | `dump/il2cpp.h` | 70 MB 结构体定义 |
| 地址映射 | `dump/script.json` | 140 MB，可导入 IDA/Ghidra |
| 字符串字面量 | `dump/stringliteral.json` | 含地址 |
| **JS 全量源码** | `js_src/` | **1034/1034 模块，TypeScript 编译产物，可读可改** |
| 程序集清单 | `_Data/ScriptingAssemblies.json` | 含 PuerTS / NOAH / Lockstep / protobuf 等 |

### JS 源码树概览（`js_src/`）
```
archive/       存档读写 (ArchiveIO / ArchiveStrategy / ArchiveEnv)
manager/       各类 Manager (含 TsDataSaveManager)
server/module/ 数据模型 (potential.js, bless.js ...)
gameplay/
├── battle/           战斗流程 (battleprocess_*.js, battleexplore*.js)
│   ├── battleexplorerestroom / shoproom / bossroom / gambleroom ...
├── battleactor/      角色逻辑 (actorjs_base.js, actorfuncs.js, actorjsbridge.js)
├── battlebreedrun/   肉鸽流程 (选房间、商店、潜能房间)
└── buff/             增益
widget/         UI 组件 (widgetactorbless.js, potentialgroupslot.js ...)
window/         窗口 (battlechoosepotential.js, colorcontrolwindow.js ...)
core/          工具 (potentialutil.js, actorutil.js, util.js)
```

---

## 5. BepInEx 注入环境 —— 已安装 ✅

| 组件 | 版本 |
|---|---|
| BepInEx | `6.0.0-be.788+5b766a3` (IL2CPP, win-x64) |
| Unity Doorstop | 4.5.0 |
| Il2CppInterop | 1.5.3 |
| Cpp2IL / LibCpp2IL | 2022.1.0 |

* `winhttp.dll` 已与 `BlazblueEntropyEffect.exe` 同级，`doorstop_config.ini` → `enabled = true`
* 无头验证：Cpp2IL 成功解析 `GameAssembly.dll` + metadata，**24033 个类型**（与 Il2CppDumper 一致）
* **首次启动游戏**会生成 `BepInEx/interop/`（互操作程序集）与 `BepInEx/config/`，耗时数十秒
* 插件放入 `BepInEx/plugins/`

---

## 6. 工具清单 `tools/`

| 脚本 | 用途 |
|---|---|
| `m_extract.py` | `.m` 容器解包/回封（`list` / `get` / `unpack` / `verify` / `repack`） |
| `noah_codec.py` | NOAH 混淆编解码 + `CalculateHash`（`hash` / `dec` / `enc`） |
| `js_bulk.py` | 批量解包并解密全部 JS 模块 → `js_src/` |
| `pb_dump.py` | 通用 protobuf 结构转储（无需 .proto） |
| `save_header.py` | 存档头结构分析 |
| `disasm.py` | 按 RVA 反汇编 GameAssembly.dll（capstone） |
| `find_xref.py` | 扫描 rip-relative 引用（如字符串字面量） |
| `find_callers.py` | 扫描 `call rel32` 调用者，用于追踪调用链 |
| `noah_crypto.py` | 早期密码分析脚本（历史留存） |

依赖：`uv run --with numpy|capstone|UnityPy|lz4 python ...`

---

## 7. 常见陷阱

1. **别把 `.m` 当成加密格式** —— 它就是 bundle 拼接，直接按偏移切。
2. **存档不是 21 字节头 + protobuf** —— 那是 LZ4 帧的 magic/FLG/BD/content-size，别手工改 header，用 LZ4 帧重新压。
3. **NOAH 混淆密钥按资产名派生** —— 改 JS 回封时必须用**原名** `CalculateHash`，否则游戏读不出来。
4. **PuerTS 加载的是 TextAsset，不是外部 .js 文件** —— 想换 JS 要么改 bundle，要么 hook `AssetManagerBase.GetBytes`。
5. **IL2CPP GC**：Il2CppInterop 包装的原生对象需注意生命周期，跨帧持有要 `GC.KeepAlive` / 转托管副本。
6. Steam 云同步：改档前建议断网或先备份 `Save/`、`Backup/`、`ColdBackup/`。
