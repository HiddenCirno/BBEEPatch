# Input → Skill system analysis (static disassembly)

Game: Blazblue Entropy Effect (Unity IL2CPP, x64).
All RVAs are from `dump/dump.cs`, machine code from `GameAssembly.dll` via `tools/_disasm.py`.
Method labels printed by the tool can be wrong for tiny shared getters (RVA→name map uses setdefault);
every ownership claim below is grounded in the **offset** and the **call context**, not the label.

---

## 0. Per-frame call order (grounded)

`PlayerObj.UpdateLogic` (RVA 0x1BADC30) calls, in one frame:

- `PlayerInput::DoUpdate` (RVA 0x1B984A0)  — call @ `0x01badf30` (+0x300)
- `PlayerSkillMgr::DoUpdate` (RVA 0x1BD8780) — call @ `0x01badfa2` (+0x372)

`PlayerSkillMgr::DoUpdate` (0x1BD8780):
1. loop `m_SkChains` ([rdi+0x30]) calling `PlayerSkillChain::DoUpdate` (0x1BB1260) on each (call @0x181bd886e).
2. second loop calling `PlayerSkillChain::DoUpdateAndCheckInputSucc` (0x1BB10F0) on each (call @0x181bd8941, +0x1c1).

So the **input is read once per chain per frame** inside `DoUpdateAndCheckInputSucc`.

---

## 1. Where raw player input enters the skill system

### 1a. Platform layer → InputCmd + Fp2 dir

`InputCmd` enum (`dump.cs` line 239781, TypeDefIndex 3766):
`None=0, Attack=1, Skill=3, Ultra=4, Summon=5, Move=15, CustomUse1=21, CustomUse2=22,
SpecialAction1=31, SpecialAction2=32, SpecialAction3=33, SpecialAction4=34,
PvPSummonerSkill=41, PvPSummonerWrath=42, Dash=55, Dynamic=61, Jump=800`.

`GameInputManager` (SingletonBehaviour, TypeDefIndex 4336) wraps Rewired (`RewiredInputManager` InputManager @0xA8).

- `GameInputManager.Update` RVA 0x1C56BF0 — polls the active controller.
- `GameInputManager.ResolveActorAxis` RVA 0x1C54650 — reads the raw stick `Vector2` and calls
  `MathUtility.Digitize(ref Vector2 value, float xDeadZone, float yDeadZone)` (RVA 0x1A8E7B0) with
  `xDeadZone = [GameInputManager+0xC8]`, `yDeadZone = [GameInputManager+0xCC]`
  (`xAxisDeadZone` / `yAxisDeadZone`, `dump.cs` 258511+). Result is written to the per-player input cache
  and fed to `ResolveActorButton` for the `Move` command (edx=0xF).
- `GameInputManager.ResolveActorButton(playerId, InputCmd, bool pressed, Vector2 dir, bool ignoreLock)` RVA 0x1C54850
  → `NotifyCmdChange` (0x1C51670) / `NotifyDirChange` (0x1C519A0), which raise
  `EventActorCmdChanged` (`UnityAction<int,InputCmd,bool,Fp2>`, field @0x90) and
  `EventActorDirChanged` (`UnityAction<int,InputCmd,Fp2>`, field @0x98).

So the `InputCmd` is produced by the button/axis action mapping in `GameInputManager`, and the direction is a
**Digitize()d (8-way) + dead-zoned** `Vector2` converted to `Fp2`.

There is a second (touch/gesture) source: `CommandManager` (SingletonBehaviour, TypeDefIndex 2458)
`ResolveInput` RVA 0x153B130 → `NotifyPressChange` 0x153AEC0 / `NotifyDirChange` 0x153AE50 raising
`InputPressStateChanged`/`InputDirChanged` (`Vector2`). Callers: `OnScreenFreeStick`, `BattleSkillGestureOmni`.

### 1b. Game layer entry — PlayerInput

`PlayerInput::Init` (RVA 0x1B996B0) subscribes `this` to `GameInputManager.EventActorCmdChanged`
(call 0x181c571e0 @0x181b997d5) and `EventActorDirChanged` (call 0x181c57360 @0x181b9985d).
Handlers:

- `PlayerInput::OnInputCmdChange(int playerId, InputCmd cmd, bool pressed, Fp2 dir)` RVA 0x1B99950
- `PlayerInput::OnInputDirChange(int playerId, InputCmd cmd, Fp2 dir)` RVA 0x1B99C70

(`OnInputCmdChange` has no *static* call sites because it is invoked through the stored delegate; the
`tools --callers` scan only finds the JS/PVP wrappers, which is expected.)

`OnInputCmdChange` behaviour (offsets from `dump.cs` PlayerInput, TypeDefIndex 4015):
- `cmp ebx, [rsi+0x80]` → ignore if playerId != `m_PlayerId`.
- if pressed and `IsInputCmdBlock(cmd)` → ignore.
- fire `InputPressStateChanged` delegate ([rsi+0x10]) if set.
- `rbx = getInputCmd_raw(cmd)` (0x1B9B150); if null, create a new `InputCmdState`, set `[+0x10]=cmd`, add to `m_cmdState` ([rsi+0x28]).
- `[rbx+0x14] = pressed` (`InputCmdState.Pressing`), `[rbx+0x18] = dir` (`CurDir`, Fp2).
- released path: `[rbx+0x40] = [PlayerObj+0x200]` = owner.Time (`LastUnpressTimeStamp`) @0x181b99b3d.
- pressed path (0x181B99BB0): `[rbx+0x38] = owner.Time` (`LastPressTimeStamp`), `[rbx+0x28] = dir`
  (`LastStartPressingDir`), `[rbx+0x58] = 0` (`PressInputSatisfied`) @0x181b99bc2..0x181b99bcc.
- then `addCmdHistory(cmd, pressed, dir)`.

`owner.Time` = `ActorBase.Time` @0x200 (`dump.cs` ActorBase, TypeDefIndex 3720).

---

## 2. How input is routed to a chain (`PlayerSkillChain.m_Input`, 0x68)

- `PlayerSkillChain::autoGetInputType` RVA 0x1BB31B0 is the **only** writer of `+0x68`:
  - `mov dword ptr [rdi+0x68], 0` (reset) @0x181bb324e
  - iterate `SkillList` ([rdi+0x18]); for each `PlayerSkill`, if `[skill+0x44]` (`PlayerSkill.Input`) != 0 →
    `mov dword ptr [rdi+0x68], eax` @0x181bb32c3.
  - i.e. chain's `m_Input` = the first non-zero `PlayerSkill.Input` among the chain's skills.
- Called from `PlayerSkillChain::Init` RVA 0x1BB21E0 (call @0x181bb243a, +0x32e), which then sets
  Status ([+0x20]) and `NextSkillPredict` ([+0x38]).
- `PlayerSkillChain::get_Input` RVA 0x579520 returns `[rcx+0x68]`.

Routing at activation: `DoUpdateAndCheckInputSucc` (0x1BB10F0) passes `chain.m_Input` to the input object:

```
0x181bb1205  mov  r8,  [rcx+0x40]        ; rcx = PlayerObj ; PlayerObj.Input (ActorBase.IInput @0x40)
0x181bb1209  mov  ecx, 5                 ; il2cpp interface-vtable helper arg
0x181bb120e  mov  r9d, [rsi+0x68]        ; rsi = chain ; chain.m_Input
0x181bb1219  call 0x180002d10            ; virtual iface call -> IInput.GetCmdStatus(chain.m_Input)
0x181bb121e  test rax, rax
0x181bb1221  je   0x181bb1241            ; null -> false
0x181bb123c  jmp  0x181bb3320            ; -> findAndStartSkill_Imp(-1)
```

So **each chain group polls its own `InputCmd`** via `IInput.GetCmdStatus(cmd)`. The returned
`InputCmdState` is what the matcher inspects. The chain also records which skill matched via
`PlayerSkill.Input` ([skill+0x44], used in `findAndStartSkill_Imp` @0x181bb3430 and in the finders).

---

## 3. Is there an input BUFFER?  ← MOST IMPORTANT

**There is no queue / replay buffer in the skill system.** `PlayerSkillChain` and `PlayerSkillMgr` have no
pending-input field (field lists in `dump.cs` 249162 / 249418). The buffering is **implicit, in persistent
per-command state**: a press writes a timestamp that survives after the button is released, and the matcher
re-polls that state each frame and accepts it while it is still recent.

### 3a. The storage (exact type + offsets)

`PlayerInput` (TypeDefIndex 4015) holds:

- `m_cmdState` — `List<InputCmdState>` @ **PlayerInput+0x28** (one persistent state object per `InputCmd`).
- `m_histList` — `List<InputCmdHistory>` @ **PlayerInput+0x30**, cap `m_maxHist` @ **PlayerInput+0x38**.

`InputCmdState` (`dump.cs` 245344, TypeDefIndex 3971) fields:

| off | type | name |
|-----|------|------|
| 0x10 | InputCmd | Cmd |
| 0x14 | bool | Pressing |
| 0x18 | Fp2 | CurDir |
| 0x28 | Fp2 | LastStartPressingDir |
| **0x38** | **Fp** | **LastPressTimeStamp** |
| **0x40** | **Fp** | **LastUnpressTimeStamp** |
| 0x48 | VBtnSpecFlags | VBtnFlags |
| 0x4C | ESkillPotentialSlot | Slot |
| 0x50 | uint | SlotPotentialId |
| 0x54 | PlayerSkillGroup | SlotSkillGroup |
| **0x58** | **bool** | **PressInputSatisfied** |

`getInputCmd_raw` RVA 0x1B9B150 is a linear search of `m_cmdState` comparing `[state+0x10] == cmd`
(0x181b9b20d `cmp dword ptr [rax+0x10], esi`). The state object is created lazily on first press
(`OnInputCmdChange` @0x181b99ab2..0x181b99aef) and then **lives for the whole `PlayerInput` lifetime**.

On press (`OnInputCmdChange` pressed path, 0x181B99BB0):
- `mov rax, [owner+0x200]` ; `mov qword ptr [rbx+0x38], rax`  → **LastPressTimeStamp = owner.Time**
- `movups xmmword ptr [rbx+0x28], xmm0`                     → LastStartPressingDir = dir
- `mov byte ptr [rbx+0x58], 0`                              → PressInputSatisfied = 0

On release (unpress path, 0x181b99b36-0x181b99b3d):
- `mov rax,[owner+0x200]` ; `mov qword ptr [rbx+0x40], rax`  → **LastUnpressTimeStamp = owner.Time**
- `Pressing` ([+0x14]) = 0. `PressInputSatisfied` is **not** set on release (stays 0).

Cleared only by `PlayerInput::Clear` (0x1B982A0), `ClearSafe` (0x1B98080), `ClearOnlyKeyPresss` (0x1B97F10)
or `Release`. `ClearSafe` resets `m_histList`, `m_cmdState`, block map, teacher state.

### 3b. How long a stored input survives

The matcher does **not** replay anything. After the `ActdurStrict` gate opens, it polls every frame and
checks the **age of the last press**:

```
age = IInput.GetTimeEllaps( InputCmdState.LastPressTimeStamp )   ; = owner.Time - LastPressTimeStamp
accept iff age <= window
```

`GetTimeEllaps` RVA 0x1B995E0: `[rcx+0x78]` = m_Owner, `[owner+0x200]` = owner.Time,
then `Fp::op_Subtraction` → `now - stamp`.

The `window` is `Preinputtime` (Q4). So **a press is "live" for `window` seconds after the press instant**,
regardless of whether the button was released in between. That is the buffer. Its duration is the skill's
`Preinputtime` (or the default sticky value when `Preinputtime <= 0`).

### 3c. The second list (`m_histList`) is NOT the skill buffer

`PlayerInput::addCmdHistory` RVA 0x1B9A7E0 appends an `InputCmdHistory` `{ Cmd, Pressing, Dir, LastPressTimeStamp=owner.Time }`
(fields: Cmd 0x0, Pressing 0x4, Dir 0x8, LastPressTimeStamp 0x18 — `dump.cs` 245372) into `m_histList` ([rbx+0x30]).
When `count > m_maxHist` ([rbx+0x38]) it calls `sub_23154F0(list, 0, count/2)`
(0x181b9a9a9..0x181b9a9ae: `sar eax,1` → RemoveRange(0, count/2)) — i.e. it trims to half, not to a per-entry
lifetime.

Readers:
- `GetCmdHistory(Fp stickyTime, ref InputCmdHistory[])` RVA 0x1B985D0 — walks newest→oldest, keeps entries with
  `owner.Time - hist.LastPressTimeStamp <= stickyTime` (0x181b98713 `Fp::op_GreaterThan`).
- `GetLastestCmdHistory(bool pressed, Fp stickyTime)` RVA 0x1B98EE0 — newest entry with
  `hist.Pressing == pressed` and `owner.Time - hist.LastPressTimeStamp < stickyTime` (0x181b99049 `Fp::op_LessThan`),
  else static `s_EmptyCmdHistory`.

**Callers of both are only the JS binding wrapper** (`GamePlay_PlayerInput_Wrap`); no native skill code calls
them. So `m_histList` serves script/buff/UI, not core skill activation.

### 3d. Teacher-skill delay buffer (a real, separate delay buffer)

For teacher-skill combine only: `DelayTeacherSkillInputAndTryCombine` (+0x3C), `m_InputTeacherSkState` (+0x40),
`m_InputTeackerSkStateEllaps` (+0x44), `m_cmdStateCacheForSpecial` (+0x90), `m_TempMergeCmdState` (+0x98),
`m_DelayedCmdStateGen` (+0xA0)/count (+0xA8). Methods `delayedTeacherSkillInputEventProc` (0x1B9AD30),
`delayedTeacherSkillInputEventProc_SimulateKeyClick` (0x1B9AB30), `updateTeacherSkillInputEventProc` (0x1B9B5E0,
called from `PlayerInput::DoUpdate` @0x181b98522 when the flag is set). This delays/combines the
`CustomUse1/2` (0x15/0x16) presses; it is not the general player buffer.

---

## 4. How `Preinputtime` is consumed (decisive comparison)

`PlayerSkillChain::findAndStartSkill_Imp` RVA 0x1BB3320. The state object `r14` was obtained at the top of the
function via `IInput.GetCmdStatus(skill.Input)`:

```
0x181bb3430  mov  r9d, [rsi+0x44]        ; rsi = matched skill ; PlayerSkill.Input
0x181bb3439  mov  rdx, <RuntimeMethod>
0x181bb3440  mov  r8,  rdi               ; PlayerObj.Input
0x181bb3443  call 0x180002d10            ; virtual -> IInput.GetCmdStatus(skill.Input)
0x181bb3448  mov  r14, rax               ; r14 = InputCmdState

0x181bb34bb  call 0x181499750            ; SkillActivateFixedPointWrap::get_UseLongPress
0x181bb34c4  test al, al
0x181bb34c4  je   0x181bb3569            ; UseLongPress==false -> Preinputtime branch
```

Non-long-press branch (RVA 0x181BB3569..0x181BB3655):

```
0x181bb3569  cmp  byte ptr [r14+0x58], 0 ; InputCmdState.PressInputSatisfied
0x181bb356e  jne  0x181bb36f4            ; if PressInputSatisfied != 0 -> reject (return false)

0x181bb3574  mov  rcx, [rsi+0x20]        ; SkillActivateFixedPointWrap
0x181bb3583  call 0x1814995f0            ; get_Preinputtime  -> rax
0x181bb35a0  call Fp::op_Implicit(0)     ; rdx = 0
0x181bb35b2  call Fp::op_LessThanOrEqual ; Preinputtime <= 0 ?
0x181bb35b9  je   0x181bb35e5            ; else use Preinputtime as window
   ; Preinputtime <= 0 path: window = static Fp at static-fields offset 0x68
0x181bb35da  mov  rbx, [<class>+0xb8]    ; static_fields
0x181bb35e1  mov  rbx, [rbx+0x68]        ; <-- default window value

0x181bb35e5  call Fp::op_Implicit(0)
0x181bb360c  call Fp::op_GreaterThan     ; window > 0 ?
0x181bb3613  je   0x181bb365b            ; window <= 0 -> no window constraint (accept)

0x181bb3615  mov  r9,  [r14+0x38]        ; InputCmdState.LastPressTimeStamp
0x181bb3619  mov  ecx, 9                 ; il2cpp slot
0x181bb3628  call 0x180095550            ; virtual -> IInput.GetTimeEllaps(LastPressTimeStamp)
0x181bb3645  mov  rdx, rbx               ; rdx = window
0x181bb3648  mov  rcx, rdi               ; rcx = age
0x181bb364e  call Fp::op_GreaterThan     ; age > window ?
0x181bb3653  test al, al
0x181bb3655  jne  0x181bb36f4            ; if age > window -> reject (return false)
0x181bb365b  ...                          ; else -> ActionMgr::CheckCanChangeToAction ...
```

**Precise comparison:** `age = owner.Time - LastPressTimeStamp` (`IInput.GetTimeEllaps(LastPressTimeStamp)`),
compared with `Preinputtime` (or the default when `Preinputtime <= 0`):

```
accept  <=>  (now - LastPressTimeStamp) <= Preinputtime
```

It is measured **from the press instant**, not against the end of the action and not against remaining
duration / elapsed. A press older than `Preinputtime` is rejected; a press newer than that is accepted.
Because the whole check only runs once the `ActdurStrict` gate is satisfied, an early press is only honoured
if it is still within `Preinputtime` seconds at the frame the gate opens.

Default window note: the `Preinputtime <= 0` fallback is a **static `Fp` at static-fields offset 0x68** of a
static class (metadata slot VA 0x1852FB718). The owning class could **not** be resolved from the binary
(no matching field at 0x114/0x12C in `dump.cs`). Candidate by name/offset only: `ActorDefine.DefaultInputStickyTime`
(`dump.cs` ActorDefine, static `Fp` @0x68) — *not confirmed*.

Long-press branch (UseLongPress==true, 0x181BB34CA..0x181BB3655):
- requires `InputCmdState.Pressing` ([r14+0x14]) != 0 (0x181bb34ca),
- `pd = GetTimeEllaps(LastPressTimeStamp)` = hold duration,
- require `pd >= LongPressStart` (0x181bb3525 `Fp::op_LessThan` reject) and `pd <= LongPressEnd`
  (0x181bb364e `Fp::op_GreaterThan` reject).
(`get_LongPressStart` 0x1499570, `get_LongPressEnd` 0x1499510.)

---

## 5. `CheckPlayerSkillInputDir` — actual signature and behaviour

`dump.cs` (PlayerSkillUtility, TypeDefIndex 4068):

```
// RVA: 0x1BDDC10  public static bool CheckPlayerSkillInputDir(ActorBase actor, SkillInputDirType dirType)
```

**The premise "(PlayerObj, int triggerId)" is not correct.** It is `(ActorBase actor, SkillInputDirType dirType)`.
Callers pass the **skill's `InputDir`**, not a triggerId:
- `findNextSkillMatchPreOrderAndInputDir` RVA 0x1BB3700, call @0x181bb3a48:
  `rcx = [PlayerSkillMgr+0x10]` = PlayerObj, `edx = SkillActivateFixedPointWrap.get_InputDir()` (getter RVA 0x148B040, reads `data+0x48`).
- `findStartingSkillMatchInputDir` RVA 0x1BB3B20, call @0x181bb3d6c: same.

Behaviour:
- `rsi = [actor+0x40]` = `ActorBase.Input` (IInput).
- Interface virtual call resolves an IInput method that returns an `Fp2` (no args) →
  `IInput.GetMoveDirSimple()`. Result stored: `dir.X` at `[rsp+0x40]`, `dir.Y` at `[rsp+0x48]`.
- `dirType` (0..8) dispatched through a jump table at **RVA 0x1BDE16C** (base VA 0x180000000).
  `T` = static `Fp`(+0x114) + static `Fp`(+0x12C) of the static class at slot VA 0x1852FB718 (class not determined).

| dirType | SkillInputDirType | test |
|---|---|---|
| 0 | Any | `true` (0x1BDDDDB) |
| 1 | Up | `dir.Y >  T` (0x1BDDDEE) |
| 2 | Down | `dir.Y < -T` (0x1BDDE4A) |
| 3 | Front | `dir.X * actor.DirFp >  T` (0x1BDDEB0) |
| 4 | Back | `dir.X * actor.DirFp < -T` (0x1BDDF17) |
| 5 | NoDir | `dir.X == static(+0x54)` **and** `dir.Y == static(+0x54)` (0x1BDDF98) |
| 6 | Left | `dir.X < -T` (0x1BDE01F) |
| 7 | Right | `dir.X >  T` (0x1BDE085) |
| 8 | AnyX | `dir.X < -T` **or** `dir.X > T` (0x1BDE0D1) |

`actor.DirFp` = `ActorBase.get_DirFp` RVA 0x1B033D0.
(The separate `StrictMatchPlayerSkillInputDir(PlayerObj, Vector2 inputDir, SkillInputDirType)` RVA 0x1BE0660 was
already decoded and is not re-done here.)

---

## 6. Where the direction reaching the matcher comes from, and the deadzone

### Real activation path (used by `findAndStartSkill_Imp`)

`CheckPlayerSkillInputDir` → `IInput.GetMoveDirSimple()`:
- `PlayerInput::GetMoveDirSimple` RVA 0x1B99200 → zeroes out buffer then calls `PlayerInput::GetMoveDir` (0x1B99200 → 0x1B992A0).
- `PlayerInput::GetMoveDir` RVA 0x1B992A0:
  - if forced dir `m_MoveDirForced` (Fp2 [+0x50]) non-zero → returns it (0x181b995b6, `movups xmm0,[rsi+0x50]`).
  - else reads `getInputCmd_raw(this, 0xF)` (`InputCmd.Move`) and returns `[state+0x18]` = `InputCmdState.CurDir`
    (0x181b994dc..0x181b994f4 `movups xmm0,[rax+0x18]`). If no Move state, a config default is used.

`CurDir` is produced upstream in `GameInputManager.ResolveActorAxis` (Q1), which applies
`MathUtility.Digitize(ref dir, xAxisDeadZone[+0xC8], yAxisDeadZone[+0xCC])` (0x181c5471f). So:

- **Yes, a deadzone is applied before matching — upstream in `GameInputManager`** (fields 0xC8/0xCC), which also
  digitizes to 8 directions. `GetMoveDir` itself adds no deadzone; it returns the stored digitized dir.
- Additionally `CheckPlayerSkillInputDir` applies the match-time axis threshold `T = static(0x114) + static(0x12C)`
  (and equality to `static(0x54)` for NoDir).

### UI / prediction path

`StrictMatchPlayerSkillInputDir(PlayerObj, Vector2 inputDir, dirType)` RVA 0x1BE0660 receives `inputDir` from its
callers `FindByPreSkillOrderByInputDir` (0x1BB1C70) / `FindAStartingSkillForPredictByInputDir` (0x1BB1640),
which are reached from `PredictNextSkillByInputDir` (0x1BB2560). Its only callers are
`VirtualButtonInherit/Omni/Overload::UpdateStatus` (touch virtual-button drag direction) and the JS wrapper,
i.e. this path is **UI-only**; the `Vector2 inputDir` is the virtual button's drag vector. Whether a deadzone is
applied before this path was **not determined**.

---

## Summary of the two key answers

**3. Buffer:** No pending-input queue/replay exists in `PlayerSkillChain`/`PlayerSkillMgr`. Buffering is implicit
in the persistent `InputCmdState` stored in `PlayerInput.m_cmdState` (`List<InputCmdState>` @PlayerInput+0x28),
one per `InputCmd`, keyed by `Cmd`@0x10. On press, `LastPressTimeStamp` (Fp @0x38) is set to `owner.Time`; on
release `LastUnpressTimeStamp` (@0x40) is set, but `LastPressTimeStamp` is left intact and `PressInputSatisfied`
(@0x58) stays 0. The matcher re-polls this state every frame and accepts the press while
`owner.Time - LastPressTimeStamp <= window`. So a stored input survives **`window` seconds** (the skill's
`Preinputtime`; fallback default when `Preinputtime <= 0`), independent of the button being released.
The separate `m_histList` (PlayerInput+0x30, cap +0x38) is a trimmed event history read only by the JS wrapper —
not the skill buffer. A true explicit delay buffer exists only for teacher-skill combine.

**4. Preinputtime:** Decisive instructions in `findAndStartSkill_Imp` @0x181BB3569..0x181BB3655:
`age = IInput.GetTimeEllaps(InputCmdState.LastPressTimeStamp) = owner.Time - LastPressTimeStamp`;
`Fp::op_GreaterThan(age, window)` @0x181bb364e → reject if `age > window` (RVA 0x1BB36F4). `window = Preinputtime`
(getter 0x14995F0) unless `Preinputtime <= 0`, in which case the static Fp at static offset 0x68 is used. It is
**not** compared against remaining duration or elapsed action time — it is measured from the press timestamp.

### Files written
- `_modding/input_system_analysis.md` (this file)
- raw disassembly dumps used: `_modding/tools/_inp_out.txt`, `_inp_out2.txt`, `_inp_out3.txt`, `_inp_out4.txt`,
  `_inp_out5.txt`, `_inp_out6.txt`, `_inp_jt.txt`, `_inp_callers*.txt`
