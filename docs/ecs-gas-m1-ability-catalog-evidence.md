# M1 Ability Catalog Evidence

日期：2026-08-30

本证据文件只覆盖能力目录、typed targeting、execution payload 和 M1 的解析/校验/确定性 contract；不宣称属性聚合、DamageResolver、Effect/Trigger runtime 或 FrameGraph 已切流。

## 20 项语义映射

| ID | Ability | 旧 SkillSystem 执行 | typed contract |
|---:|---|---|---|
| 0 | Cross Slash | `CastCrossArea(finalDamage)` | Damage multiplier 4 (`Multiplier/LegacyMultiplier`) + fixed 40 (`Constant/Raw`) |
| 1 | Mega Explosion | `CastBoxArea(finalDamage)` | Damage multiplier 3 + fixed 40 |
| 2 | Sniper Shot | `CastSingleTarget(finalDamage)` | Damage multiplier 6 + fixed 40 |
| 3 | Poison Nova | `CastCircleArea(finalDamage, def)` | Periodic Damage 8, duration 5, period 1 |
| 4 | Chain Lightning | `CastChainLightning(finalDamage)` | Damage multiplier 5 + fixed 40 |
| 5 | Guardian Heal | `CastHeal(def)` | Heal 0.3 (HealPercent, constant) |
| 6 | Chain Heal | `CastChainHealAbility` -> `CastChainHeal` | Heal 0.25 + Shield 15 for 3 seconds |
| 7 | Mass Resurrect | `CastMassResurrect` | Resurrect at HealPercent 0.3 |
| 8 | War Stomp | `CastAoeStun` | CrowdControl duration 2 |
| 9 | Earthroot | `CastAoeRoot` | CrowdControl duration 3 |
| 10 | Shockwave | `CastAoeKnockback` | CrowdControl force 80 |
| 11 | Energy Shield | `CastShield` | Shield 50 for 5 seconds |
| 12 | Laser Beam | `CastLineArea(finalDamage)` | Damage multiplier 3 + fixed 40 |
| 13 | Cold Nova | `CastFreezeArea(finalDamage, def)` | Damage multiplier 2 + Freeze effect duration 2 |
| 14 | Dragon Breath | `CastConeArea(finalDamage)` | Damage multiplier 3 + periodic Fire damage 5/3s/1s |
| 15 | Plasma Cannon | `CastConeArea(finalDamage)` | Damage multiplier 5 + fixed 60 |
| 16 | Artillery Strike | `CastGroundTarget(finalDamage)` | Damage multiplier 6 + fixed 50 |
| 17 | Meteor Strike | `CastGroundTarget(finalDamage)` | Damage multiplier 8 + fixed 70 + periodic Burn 4/3s/1s |
| 18 | Slow Nova | `CastSlowArea(finalDamage, def)` | Damage multiplier 2 + Slow 0.5 for 3 seconds |
| 19 | Time Rewind | `CastTimeRewind` -> `TimeRewindSnapshotSystem.RestoreFromSnapshot` | Resource payload 3 seconds, `ExecutionOperation.RestoreSnapshot` |

`ExecuteAbility` computes `baseDamage * FixedBaseDamage` when `DamageMultiplierAttr < 0`; the catalog therefore records the multiplier as `MagnitudeSource.Multiplier` and `DamageAmountStage.LegacyMultiplier`. Modifier `Value` is preserved separately as constant raw payload, including periodic DoT values.

## Contract checks

- `ParseTargeting` is the sole canonical shape parser. `timerwind` maps explicitly to `TargetingShape.TimeRewind`; unknown values fail fast.
- Definitions defensively copy arrays into read-only views. Catalog lookup is contiguous-index O(1); validator rejects duplicate/non-contiguous references and unregistered IDs.
- Determinism tests cover repeated compilation, reversed static input order, culture changes, and execution metadata fingerprinting.
- `CuratedAbilitySemanticTests` verifies all 20 names/IDs, targeting closure, payload magnitudes, source/stage, duration/period and composite payloads.

## Verification

- Catalog-focused tests: 25 passed.
- Core build: passed, 0 warnings / 0 errors.
- Explicit `BattleSystemECS.csproj` build: 0 errors; existing NETSDK1138 net6.0 lifecycle warnings remain.
- `tools/check-test-rules.ps1`: 0 violations.
- `git diff --check`: passed.
- mode 2: 13,606 FPS (硬门禁 >= 7,000).
- mode 4: 7,625 FPS (硬门禁 >= 3,000).
- mode 5: 7,755 FPS, 5/5 levels victory (硬门禁 >= 2,500).

Full repository tests and mode 2/4/5 benchmarks remain required before any phase exit or commit; this change does not alter their status.
