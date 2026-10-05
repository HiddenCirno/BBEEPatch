# -*- coding: utf-8 -*-
"""生成 ES 动作表文档 + 全角色原始表。

产物:
  _modding/es_action_table.md         —— 整理后的 ES 动作表(带说明与分析)
  _modding/tools/_skillactivate_all.txt —— 全部 2438 条的原始 dump (各角色)
"""
import io, os, struct
from collections import defaultdict, Counter

BASE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
P = os.path.join(BASE, "extracted", "skillactivate.ab.bin")
OUT_MD = os.path.join(BASE, "es_action_table.md")
OUT_ALL = os.path.join(BASE, "tools", "_skillactivate_all.txt")

d = open(P, "rb").read()


def rv(b, i):
    r = s = 0
    while i < len(b):
        c = b[i]; i += 1
        r |= (c & 0x7F) << s
        if not (c & 0x80):
            break
        s += 7
    return r, i


def fields(b):
    out, i = [], 0
    while i < len(b):
        try:
            k, i = rv(b, i)
        except Exception:
            break
        fn, wt = k >> 3, k & 7
        if wt == 0:
            v, i = rv(b, i); out.append((fn, 'v', v))
        elif wt == 2:
            ln, i = rv(b, i); v = b[i:i + ln]; i += ln
            try:
                s = v.decode('utf-8')
                s = s if all(32 <= ord(c) < 127 for c in s) and s else None
            except UnicodeDecodeError:
                s = None
            out.append((fn, 's', s if s else v))
        elif wt == 5:
            out.append((fn, 'f', struct.unpack_from('<f', b, i)[0])); i += 4
        elif wt == 1:
            out.append((fn, 'f', 0.0)); i += 8
        else:
            break
    return out


rows, i, n = [], 0, len(d)
while i + 4 <= n:
    ln = struct.unpack_from('<I', d, i)[0]
    if ln == 0 or i + 4 + ln > n:
        break
    rows.append(fields(d[i + 4:i + 4 + ln]))
    i += 4 + ln


def G(r):
    g = {}
    for fn, wt, v in r:
        g.setdefault(fn, v)
    return g


gs = [G(r) for r in rows]

# ---------- 全角色原始表 ----------
A = [f"skillactivate 全表 dump  共 {len(gs)} 条", ""]
byactor = defaultdict(list)
for g in gs:
    byactor[g.get(2, '?')].append(g)
A.append(f"涉及角色 id {len(byactor)} 个: " + ", ".join(f"{k}({len(v)})" for k, v in sorted(byactor.items(), key=lambda x: -len(x[1]))[:30]))
for aid in sorted(byactor, key=lambda x: (not isinstance(x, int), x)):
    A.append(f"\n########## actorId={aid}  ({len(byactor[aid])} 条) ##########")
    for g in sorted(byactor[aid], key=lambda x: (x.get(3, 0), x.get(4, 0))):
        nm = g.get(5)
        if isinstance(nm, bytes):
            nm = nm.hex()
        A.append(f"  slot={g.get(3)} idx={g.get(4)} 输入={g.get(8)} \"{nm}\""
                 f"  start={g.get(6)} end={g.get(7)} f9={g.get(9)} f10={g.get(10)}"
                 f" 段={g.get(15)} 帧={g.get(24)}")
io.open(OUT_ALL, "w", encoding="utf-8").write("\n".join(A))

# ---------- ES 整理表 ----------
es = [g for g in gs if g.get(2) == 103401]
byslot = defaultdict(list)
for g in es:
    byslot[g.get(3, '?')].append(g)

SLOT_NAME = {
    1: "普攻系（Attack 输入）",
    2: "技能系（Skill 输入）",
    3: "？（1 条，动作名为空）",
    4: "空中 / 落地 / 受身系",
    5: "冲刺系（Dash 输入）",
    6: "突进（rush）",
    7: "跳跃",
    8: "蓄力系（长按）",
    11: "召唤系（Summon 输入）",
}

M = []
M.append("# ES（103401）原生动作表")
M.append("")
M.append("## 来源")
M.append("")
M.append("从 `extracted/skillactivate.ab` 的 TextAsset 静态解析得到（342,244 字节，全表 2438 条，其中 ES 97 条）。")
M.append("格式是连续 `(uint32 len, protobuf)`。不必进游戏，随时可重新生成：`py tools/_gen_action_table.py`。")
M.append("")
M.append("### 字段含义（推断 + 已交叉验证）")
M.append("")
M.append("| 字段 | 含义 |")
M.append("|---|---|")
M.append("| f2 | 角色 id（103401 = ES） |")
M.append("| f3 | **槽位**（见下表） |")
M.append("| f4 | 槽位内序号 |")
M.append("| f5 | **内部动作名**（就是 `ActionMgr` 里那个名字） |")
M.append("| f6 / f7 | 起手 / 收招标记（`Start_N` / `End_N`） |")
M.append("| f8 | **输入类型**：`Attack` / `Dash` / `Skill` / `Summon` / 空 |")
M.append("| f9 | 标志位 |")
M.append("| f10 | 名称 id（varint，如 11234 / 11239 / 11258）—— **在这些前缀里查不到本地化条目，用途待定** |")
M.append("| f15 | 段数 |")
M.append("| f24 | 帧数据 JSON，形如 `\"all\":0.7,\"5\":0.01` |")
M.append("| f33 | UI 图标路径（`IconSkill_103401`） |")
M.append("")
M.append("## 槽位")
M.append("")
for k in sorted(byslot):
    M.append(f"- **slot={k}** — {SLOT_NAME.get(k, '?')}（{len(byslot[k])} 条）")
M.append("")
M.append("## 全部条目")
M.append("")
for k in sorted(byslot):
    M.append(f"### slot={k} — {SLOT_NAME.get(k, '?')}")
    M.append("")
    M.append("| idx | 输入 | 动作名 | 段 | 帧数据 |")
    M.append("|---|---|---|---|---|")
    for g in sorted(byslot[k], key=lambda x: x.get(4, 0)):
        nm = g.get(5)
        if isinstance(nm, bytes):
            nm = f"`{nm.hex()}`"
        fr = g.get(24)
        if isinstance(fr, bytes):
            fr = "(二进制)"
        inp = g.get(8) or ""
        seg = g.get(15) if isinstance(g.get(15), int) else ""
        M.append(f"| {g.get(4)} | {inp} | `{nm}` | {seg} | {fr} |")
    M.append("")

M.append("""## 实测交叉验证

用 `ActionJournal`（记录 `ActionMgr.ChangeAction`）在训练场实跑一遍，观察到的动作名与上表**完全吻合**，并补充了表里没有的运行时名：

```
普攻      attack1 → attack2 → attack3 → attack4
下+攻击   attackD1 → attackD2 → attackD3
上挑      AttackUp
空中      atkAirX / atkAir12 / atkAir3
冲刺      dash → dashend → rushUp / dash2 / dashAir / dashAir2
          dashAtk / dashAtk0 / dashAtkG / dashAirAtk / dashAirAtk0 / dashAirAtkG
冲刺攻击  dashAAendEX
蓄力技    attackholdDashEX → attackhold_hit → attackhold_hit2
纹章解放  holdEX
技能      attackA / attackAEX / attackB / attackC
          attackA_AirEX / attackB_Air / attackC_Air
Ultra     UltraDashEX → UDdrop → dropend / UltraDashAirEX → UltraDAend
召唤      summon
下落系    fall / fallup / fallupd / fallup2 / fallup22 / fallup2d / fallup3 /
          fallm / fallmdown / fallmEX0 / fallmdownEX0 / fallmdownendEX / fallmdownendEX2
```

## ★ 结构结论（会影响改动的设计方式）

**`attackA` / `attackB` / `attackC` 是「技能槽」，不是具体的圆桌骑士。**

崔斯坦、布鲁诺、莫德雷德、高文、加拉哈德、佩利诺尔 **共用同一批动作名**，
往 A 槽装谁，`attackA` 就解析成谁的动作组。

也就是说：**潜能不是在替换动作，而是在切换整个输入组的动作映射。**
（这也是为什么 `summon` 是**独立的一种输入类型** —— 召唤/切换走的是单独通道。）

### 对那五条设计的直接影响

| 设计目标 | 能不能靠动作名匹配 |
|---|---|
| ① 冲刺打断冲刺 | ✅ 可以（`dash`/`dash2`/`dashend`，看 `GameActionLogic.Interrupt`） |
| ② 高文 → 崔斯坦一/二/三段纹章 | ❌ **跨技能**，动作名分不出来 |
| ③ 崔斯坦三段 → 布鲁诺三段剑气 | ❌ 跨技能 |
| ④ 布鲁诺剑气 → ±30° 两道 | ⚠ 要先认出"布鲁诺的剑气"是哪个弹幕 |
| ⑤ 莫德雷德上挑 → 移动纹章 / 布鲁诺剑气 | ❌ 跨技能 |

**②③⑤ 必须改从「产物」侧识别**：不认"这是崔斯坦的动作"，而认"它生成了哪个纹章/剑气"
（`bullet_id` + `bullet_action`）。`BulletProbe` 已经能打出这两项。

## 下一步

把三个技能槽分别装上 **崔斯坦 / 布鲁诺 / 莫德雷德** 各按一次，
用 `BulletProbe` 记录「**技能 → 弹幕 id + action**」——
**那张表才是②③⑤ 的真正地基**，有了它这几条就是纯配置。

## 其他

- 全角色原始 dump：`tools/_skillactivate_all.txt`（2438 条）
- 本地化招式名（`ActorActionName_<id>`）：纹章解放=340061、崔斯坦=340081、布鲁诺=340281、
  莫德雷德=340321、高文=340391、加拉哈德=340441、贝德维尔=340501
""")

io.open(OUT_MD, "w", encoding="utf-8").write("\n".join(M))
print("done ->", OUT_MD)
print("done ->", OUT_ALL)
