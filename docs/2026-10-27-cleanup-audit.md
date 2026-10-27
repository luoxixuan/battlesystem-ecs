# BattleSystem-ECS — Dead-Code / Legacy-Path Cleanup Audit

> Status: **read-only audit**. No source, configuration, test, generated artifact, Unity file or external service was changed by this audit. This report is the only file the audit adds to the repository.
> Nothing in this report is **ready to commit**: required gates mode 2/4/5 are DEFERRED and the Unity smoke is UNAVAILABLE on this host (see §1.4).
> Classification vocabulary: **V** = verified cleanup candidate (reachability evidence complete; blocking conditions listed), **R** = must retain, **E** = insufficient evidence, **F** = behavior-changing proposal / functional finding (not a cleanup).

---

## 0. Summary

| Area | Finding | Class |
|---|---|---|
| 34 `Systems/` classes (6,009 LOC) are never constructed by any production path; 18 of them are recorded `feature-disabled` in the registration spec, 2 more are reviewed-disabled FrameGraph nodes without spec entries | §3 V-05, App. S | V (product decision required) |
| 88 SOA arrays are referenced only by those never-constructed systems — **≥ 21.8 MB per `ComponentStore`** (static estimate, lower bound) | §3 V-05, App. D | V |
| `FearSystem` and `SuicideBombSystem` are registered, run every Wave frame, and can never have an effect (inputs have no writer, no config field, no JSON key). `FearSystem` does a 100,000-slot serial scan per frame | §3 V-03/V-04 | V — **DONE in B3 (2026-10-27): retired** |
| 6 SOA arrays with zero indexed access anywhere (≈1.0 MB/store) | §3 V-02 | V |
| Internal zero-reference members (4 symbols) | §3 V-01 | V |
| Misleading comments/docs that contradict code or the Unity consumer | §3 V-06 | V (docs only) |
| `SystemRegistry.CreateAll/WireDependencies/AssignToGroups` are **live** (called by `ProductionSystemInstaller`) | §4 R-01 | R |
| Legacy config parser is the loader for **every benchmark, `Program.cs` and Unity** | §4 R-02 | R |
| `Multiply`/`LegacyEffectAdapter` exit condition unmet; naive replacement is **not bit-identical** | §4 R-06 | R |
| 247 public/internal methods with zero references (incl. Unity), 102 test-only methods | App. A1/A2 | E (public surface) |
| 114 write-only / never-written SOA arrays | App. B | E (per-field confirmation) |
| 43 config properties never read; strict parser uses `UnmappedMemberHandling.Disallow` | App. C | E / F (schema change) |
| 11 `Data/Configs` files, 238 `Data/Monsters` files and 174 `Data/Towers` files have no loader | §5 E-09 | E (provenance) |
| `SaveSystem.SaveCheckpoint` writes a file on every wave completion that nothing loads | §5 E-08 | E |
| Unity plugin DLL predates the Engine split (no Engine DLL present) | §6 F-04 | F / gap |

---

## 1. Audit baseline

### 1.1 Repository state (captured before the audit started and re-captured after the RecordKill task finished)

| Item | Value |
|---|---|
| Repository | `F:\AI\BattleSystem-ECS` (working directory confirmed by the user) |
| HEAD | `3b10ebc6c7e2eda257ab91e2be41fa5d85f12ec6` (`refactor: trim dead and misleading GameplayTriggerRuntime API`) |
| Branch | `master` |
| Index tree | `e0a4a6a88ebcfa2ae6823d042fa6095753e65117` |
| Working tree | **not clean** — 20 status entries (19 tracked changes + 1 untracked test) from earlier work in this session (see §1.3). `git diff HEAD --binary` sha256 prefix `41e5824c9d1eeb92`; porcelain status sha256 prefix `b1c90bdbea4e50c4`; captured 2026-10-27T00:20:56-07:00 |
| Audit target | **Committed HEAD**, inspected through a detached, read-only `git worktree` at HEAD in the session scratchpad, so pending uncommitted edits do not skew reachability results |

### 1.2 Method

- Static reachability: per-symbol reference scans across `BattleSystemECS.Engine`, `Core/`, `Core/GAS/`, `Systems/`, `Program.cs`, `BattleSystemECS.Tests/`, `tools/`, JSON data, and the Unity project `F:\AI\BattleSystem-ECS-Unity\Assets` (scripts, tests, StreamingAssets listing). Scans recorded as TSV in the scratchpad and reproduced as appendices.
- Construction tracing: `new X(`, target-typed `new()`, registry properties, typed recipes (`Core/SystemRegistrationRecipes.cs`), schema v3 spec (`tools/system-registration-spec.json`), generated manifest (`Core/SystemRegistrationManifest.generated.cs`), `FrameBindingFacts`, `FrameSystemGraph.ReviewedDisabledReason`, group slots (`Core/*Group.cs`).
- Reflection/dynamic paths checked: no `Activator`/`Type.GetType`/`GetFields` over `ComponentStore` in production code; reflection exists only in tests (`GameplayStorageProfileTests`, `EngineContractBoundaryTests`, `EffectRuntimeStateTests`, `TypedConfigParserTests`, `Infrastructure/IlMetadataWalker`). Config loading: explicit file constants plus two directory enumerations (`Data/Skills/*.json`, `skill_*.json`).
- Data-flow for SOA arrays: indexed write (`X[i] =`, compound, `++/--`) vs indexed read, excluding whole-array lifecycle statements in `ComponentStore*.cs`. Known blind spots: `ref` locals, `Volatile/Interlocked(ref X[i])`, `Array.Copy/Fill`. Every promoted SOA item was rechecked for these forms.
- Migration exit conditions: `docs/plan/ecs-gas-migration-plan.md`, `…-foundation.md`, `…-combat.md`, `…-orchestration.md`, `…-lumio-contract-alignment.md`, `…-gap-remediation-plan.md`, `docs/ecs-gas-m8-evidence.md`.

### 1.3 Pending working-tree changes (not part of HEAD)

The F: working tree already contains uncommitted edits made earlier in this session. They are **outside** the audited HEAD and are classified here under this audit's stricter rules:

| Pending change | Audit classification |
|---|---|
| Delete `ApplyEnemyDamageLegacy`, `FrameScheduler.ClearFrameBindings`, `DamageResolver.MarkBoundary`, `Registrar.AddWave/AddWaveAt` | V-01 (internal; no contract) |
| Delete `WindSystem` + wind SOA/methods, `TowerMergeSystem`, `FileLogger`, `ElementalReactionEvent`, `CorpseEffectTypeEnum`, `BTCachedTreeEvaluator.EvaluateWithEnum`, string `EnemyAISystem.InvokeExecuteAction`, `CommandBuffer.ResetOverflowCount`, `TimeContext.EffectDeltaFor` | **Public surface** removals: zero consumers in repo and Unity source, but external-consumer sign-off is required (see V-05 / E-01). Should not share a commit with V-01 |
| Adaptive-difficulty fixes (`SetAdaptiveDifficulty` wiring, `RecordKill` subscription, leak subtraction, `GameManager` leak count, `_adaptivePlayerId`) + `AdaptiveDifficultyProductionWiringTests` | **F — behavior-changing**: mode 5 outcome changes Victory/457 → Victory/445. Must be its own commit with its own evidence |

### 1.4 Environment limitations and evidence gaps

| Gap | Effect on conclusions |
|---|---|
| Benchmark modes 2/4/5 are DEFERRED for this audit (AGENTS.md §7.1 current round) | No CPU benefit is claimed as measured. FrameGraph node timings (mode 4) were not collected. Any mode-5 numbers from earlier in this session belong to the adaptive-difficulty task, not to this audit |
| Unity smoke UNAVAILABLE (Editor not run). Unity **source** was read-only inspected | Unity compile/run compatibility of any removal is unverified; only static source references are evidence |
| Unity plugin `BattleSystemECS.Core.dll` is dated 2026-06-10 and no `BattleSystemECS.Engine.dll` is present | Unity has not consumed a post-Engine-split build; current API compatibility with Unity scripts is unproven (F-04) |
| `F:\AI\battlesystem-ecs-gate-logs\…` evidence directories cited by `docs/plan/ecs-gas-migration-orchestration.md`, `docs/ecs-gas-m0-baseline.md`, `docs/ecs-gas-m7-evidence.md` and `docs/ecs-gas-m8-evidence.md` are not present | M7/M8 evidence (incl. the 723-array storage profile, 72.4/109.2/17.3 MB figures) is cited from documents only and is not re-verifiable here |
| Build/test gates were not run on HEAD by this audit (read-only). Separately, in the `F:\AI\BattleSystem-ECS` checkout the test host is terminated (exit −1, no dump, no Application event-log entry, ~49 GB commit free) — reproduced **at committed HEAD with all working-tree changes stashed**, via Git Bash and PowerShell, sandboxed and not. The same code passed 1953/1953 in clean checkouts elsewhere on the same machine earlier the same day. The stderr line `[StateMachine] Invalid transition: Init -> WavePhase` reported as the "reason" is an ordinary `Console.Error` warning (`Core/GameState.cs:64`), not the cause. **Cause (confirmed by the user):** a memory guard running in another session on the same machine terminates large test hosts — it reported stopping a ~30 GB `BattleSystem-ECS` testhost. Not a code defect. Per the user, the full suite (~30 GB) is not run on this machine; verification uses builds and focused (`--filter`) test runs, and gate 4 is reported as not run | Test passes are not used as behavioral-equivalence evidence anywhere in this report. AGENTS.md gate 4 cannot currently be satisfied in this checkout |
| No `CODEOWNERS`/maintainer file. Commit authors include an automation identity (`Hermes Cron`) | Owners are listed as **unknown** |
| Historical/external consumers of the public Core API beyond Unity are unknown; no NuGet/packaging scripts exist in the repo | Every public-surface removal carries an external-consumer blocking condition |

---

## 2. Coverage matrix

| Module | Files | Entry points inspected | Registration / config sources | Consumers checked | Remaining gaps |
|---|---|---|---|---|---|
| `BattleSystemECS.Engine` | 1 (`Contracts.cs`, 22 lines) | All exported types | — | Core, Tests (`EngineContractBoundaryTests`), Unity | 9 interfaces have no implementer (E-05) |
| `Core/` | 64 | `GameManager`, `FrameScheduler.Tick`, `ProductionSystemInstaller`, `SystemRegistry` facades, `ComponentStore*` (999 arrays), `GameConfigLoader` (strict/legacy), `SaveSystem`, event buses | spec v3 (130 registrations: 112 enabled, 18 `feature-disabled`), recipes, generated manifest/ledger, `FrameBindingFacts`, `FrameSystemGraph` reviewed-disabled list | Systems, Tests, Unity | Benchmarks not run; Unity compile not run |
| `Core/GAS/` | 30 | Resolvers, runtimes, `LegacyEffectAdapter`, `ActiveAbilityStore`, `GameplayObservation` | Catalog compiler / validator | Systems, Tests, Unity | — |
| `Systems/` | 146 | Every class: production construction, group slot, spec entry, frame node | spec, recipes, groups | Core, Tests, Unity | Per-system semantic tracing done only for Fear, SuicideBomb, Hero (partial), Sabotage (partial) |
| `Program.cs` | 1 | Menu modes 1–5 | `LoadConfig` (legacy parser) | — | Modes not executed |
| `BattleSystemECS.Tests` | 159 | References to candidates, reflection inventories, RNG guard file lists | — | — | Test redundancy not analysed beyond tests bound to never-constructed systems |
| JSON data | `game_config.json`, 52 `Data/Configs`, 238 `Data/Monsters`, 150 `Data/Skills`, 176 `Data/Towers`, 6 `Data/Levels` | Loader constants and enumerations | Strict parser (`Disallow`), legacy parser | Core, Unity scripts, Unity StreamingAssets | Provenance/sync of Unity StreamingAssets unknown |
| `tools/` + CI | 7 + `.github/workflows/ci.yml` | Each script's references and hard-coded paths | — | docs, CI | Scripts not executed |
| Unity (`F:\AI\BattleSystem-ECS-Unity`) | 30 scripts | `BattleDriver`, `UnityEventBus`, UI scripts, PlayMode tests | `StreamingAssets` (473 data files) | — | Editor/compile/smoke not run; plugin DLL stale |

---

## 3. Verified cleanup candidates

Common to all V items: **none is ready to commit** (mode 2/4/5 DEFERRED, Unity UNAVAILABLE). CPU benefit **not measured**. Owner **unknown**. Rollback: revert the batch commit (no save-format or config-schema changes are involved unless stated).

### V-01 Internal zero-reference members

| Field | Content |
|---|---|
| Symbols / locations | `ComponentStore.ApplyEnemyDamageLegacy` (`Core/ComponentStore_Enemy.cs:2141`, `internal`); `FrameScheduler.ClearFrameBindings` (`Core/FrameScheduler.cs:40`, `internal`); `DamageResolver.MarkBoundary` (`Core/GAS/DamageResolver.cs:149`, `internal`); `FrameSystemGraph.Registrar.AddWave` / `AddWaveAt` (`Core/FrameSystemGraph.cs:441-442`, members of a `private sealed class` inside `internal static class FrameSystemGraph`) |
| Current behavior | Never invoked. `ApplyEnemyDamageLegacy` additionally ignores its `ignoreShield` parameter |
| Callsites | None in Core/Systems/Program/Tests (InternalsVisibleTo Tests only)/Unity |
| Counterexamples ruled out | No reflection or string dispatch to these names; `RegisterFrameBinding`/`RemoveFrameBinding`/`AddWaveBinding` are separate live members |
| Change boundary | Delete the members only. No generated sources involved |
| Risk | None functional; no public contract |
| Benefit | Maintenance only |
| Required checks | Gates 1–6; gates 7–9 DEFERRED |
| Status | Already implemented in the pending working tree (§1.3) |

### V-02 SOA arrays with no indexed access anywhere

> **Done (B2, 2026-10-27)**: all 6 arrays removed (declaration, `Dispose` null-out, comments). Kept list: none.

| Array | Declaration | Size |
|---|---|---|
| `EnemyHealerHealTargetPriority` | `Core/ComponentStore_Enemy.cs:885` `int[MAX_ENTITIES]` | 400,000 B |
| `EnemyTrampleKnockback` | `Core/ComponentStore_Enemy.cs:1242` `bool[MAX_ENTITIES]` | 100,000 B |
| `TowerCooldownReduction` | `Core/ComponentStore_Tower.cs:204` `float[MAX_ENTITIES]` | 400,000 B |
| `TowerWasStealthedLastFrame` | `Core/ComponentStore_Tower.cs:864` `bool[MAX_ENTITIES]` | 100,000 B |
| `HeroTypeId` | `Core/ComponentStore_Player.cs:1159` `int[MAX_HEROES]` | small |
| `PlayerGlobalSkillHotkey` | `Core/ComponentStore_Player.cs:216` `string[MAX_PLAYERS*8]` | small |

- Evidence: the only occurrences are the declaration, an adjacent comment, and (for two of them) the `Dispose` null-out in `Core/ComponentStore.cs` (lines 1657, 1762). Zero hits in Tests and Unity. No production reflection over `ComponentStore` fields; `GameplayStorageProfileTests` reflects but asserts no array count.
- Benefit: ≈1.0 MB per store (static: element size × length). Not measured.
- Blocking: public fields — external-consumer sign-off; Unity source verified 0 references.
- Change boundary: declaration + `Dispose` null-out line only.

### V-03 `FearSystem` — registered per-frame work with no possible effect — DONE (B3)

| Field | Content |
|---|---|
| Location | `Systems/FearSystem.cs` (`Update` :39); registration `tools/system-registration-spec.json:2545` (`Fear`, frame nodes `ai.fear.prepare` SerialPrepare, `ai.fear.update` ParallelDisjointWrite) |
| Effective behavior | Each Wave frame: (1) `Parallel.For` over active enemies decrementing `EnemyFearDurationLeft`; (2) `ApplyTowerFearAuras` — serial scan of all `MAX_ENTITIES` (100,000) slots at :95 looking for `TowerFearRadius > 0`, then early return; (3) `Parallel.For` clearing `EnemyIsFeared` |
| Why it can never act | `TowerFearRadius/Duration/Chance` have no writer anywhere (App. B), no `GameConfig` field, no JSON key. `EnemyFearDurationLeft` is written only inside the unreachable aura branch (:168) and by lifecycle reset. `EnemyActionType.Fear` is set only by `FearSystem`. `EnemyIsFeared` is only ever written `false` and has no reader |
| Counterexamples ruled out | No RNG draw in the file (DeterminismContext order unaffected); no other writer of the fear inputs in Core/Systems/Unity; behavior trees / config contain no `fear` action |
| External consumers | Unity: 0 references |
| Proposed boundary | Either **retire** (spec entry → regenerate manifest/ledger; recipe; `SystemRegistry` property; `AIGroup` slot; `FrameBindingFacts` nodes; tower-fear SOA arrays) or **wire** the inputs (feature work, F-01). Retirement changes FrameGraph topology (node count) and needs topology-hash re-approval; relative order of remaining nodes is unchanged |
| Benefit | Static: removes a 100k-iteration serial loop and two parallel dispatches per Wave frame. **Not measured** (mode 4 node timing DEFERRED) |
| Blocking | Product decision (wire vs retire); topology-hash approval; mode 4/5 evidence |

### V-04 `SuicideBombSystem` — registered per-frame work with no possible effect — DONE (B3)

| Field | Content |
|---|---|
| Location | `Systems/SuicideBombSystem.cs` (`Parallel.For` :75, gate :82); frame node `combat.suicide-bomb.update` (InternalParallelCollectSerialCommit); group slot `Core/CombatGroup.cs:52` |
| Effective behavior | Each frame: `Array.Clear` + `Parallel.For` over active enemies that returns at `!EnemyIsSuicide[enemyId]`. The two `MAX_ENTITIES` tower loops (:102, :179) sit behind that gate |
| Why it can never act | `EnemyIsSuicide`, `EnemySuicideTriggerRange/DmgRadius/DmgAmount` have no production writer, no config field, no JSON key. Only tests write them (1 test file) |
| Counterexamples ruled out | No RNG; no Unity writer |
| Proposed boundary / blocking | As V-03. Retirement also retires the test that exercises the feature (verification capability for an unreachable feature — owner decision) |
| Benefit | Static: one parallel dispatch + clear per frame. Not measured |

### V-05 Never-constructed system classes (34 classes, 6,009 LOC) and their exclusive SOA arrays

Full table: **Appendix S**. Exclusive arrays: **Appendix D** (88 arrays, ≥ 21.8 MB per store, static; arrays sized by named constants such as `MAX_WIND_SOURCES` counted as 0, so this is a lower bound).

Evidence: no `new X(` (or target-typed `new()`) in Core/Systems/Program; no `SystemRegistry` property; no recipe; no Unity reference; spec entries, where present, are `feature-disabled` with recipes `null`. `FrameSystemGraph.ReviewedDisabledReason` (`Core/FrameSystemGraph.cs:506-531`) records most as “intentionally absent from Registry composition; reviewed disabled” — this is a **recorded decision to keep them disabled**, not a decision to retire them.

Corrections to Appendix S (reason strings use different class names): S8 `EnemyStealGoldSystem` → node `movement.steal-gold.update`; S11 `FogOfWarSystem` → `spatial.fog.prepare/update`; S16 `PlayerSummonSystem` → `movement.summon.prepare/update`.

Sub-groups by coupling (they determine batch ownership):

| Sub-group | Members | Shared surfaces touched |
|---|---|---|
| **S-a standalone** | `HitTriggerSystem`, `TowerMergeSystem`, `EnemyCloneSystem`, `EnemyTeleportSystem` + `PortalSystem` (reference each other) | Systems files; `Framework/SimulationRngGuardTests.cs` file-name lists (lines ~49–63) |
| **S-b spec/graph-registered disabled** | 18 `feature-disabled` spec ids + `BeamTowerSystem`, `TowerEnergySystem` (reviewed nodes without spec ids) | spec → generated manifest + ledger (regenerate, never hand-edit), `FrameBindingFacts`, `FrameSystemGraph` (registrar calls + reviewed list), `*Group.cs` slots, `TowerAttackSystem.cs:672` `TowerIsBeam` guard (never true; only written `false` at `ComponentStore_Tower.cs:1452/1798`), tests `FrameGraphTests.cs:598`, `CombatGoldenReplayTests.cs:440` |
| **S-c config-coupled** | `CraftingSystem` (`crafting_recipes.json`, `items.json` loaders + `GameConfig` properties) | Config schema → **F (behavior/config-changing)** if the loader/properties are removed; class-only removal leaves config loading intact |
| **S-d store-coupled** | `TotemSystem`, `ReforgeSystem`, `WaveSkipSystem`, `TowerFormSwitchSystem`, `ObstacleSystem`, `WindSystem` | `ComponentStore_*` public methods/arrays used only by these classes |

Risks: public types in the shipped DLL; tests bound to these classes (`CraftingSystemTests`, `TowerFormSwitchSystemTests`, `ParallelCollectDeterminismTests` (Protector), `PlayerDamageAuthorityTests` (EnemyProjectile), RNG guard lists) would be retired with the features — this removes verification of unreachable features only; no assertion on reachable behavior is weakened.
Blocking: product owner decision **retire vs. keep-disabled** per feature; external-consumer sign-off; topology-hash re-approval for S-b; Unity compile check with a refreshed DLL.

### V-06 Misleading documentation and comments (doc-only)

| Location | Problem | Evidence |
|---|---|---|
| `Core/GAS/ActiveAbilityStore.cs:5` | Says Unity still reads the `AbilityInstances` projection | Unity source: 0 references to `AbilityInstances` |
| `Systems/TowerAttackSystem.cs:671-672` | Says beam towers are handled by `BeamTowerSystem` | `BeamTowerSystem` is never constructed; `TowerIsBeam` is never set true |
| `AGENTS.md` §2.1/§3.1 | `Data/Monsters` “200 种怪物静态定义”, `Data/Towers` “150 种塔静态定义”, `Data/Levels` “5 个关卡配置” | 238 monster files and 174 of 176 tower files have no loader; levels are loaded from `game_config.json` (Core) and from Unity's own `StreamingAssets/level_1.json` |
| `AGENTS.md` §4.2 / docs claiming benchmarks use the production composition | Benchmarks use `GameConfigLoader.LoadConfig` (legacy parser), production uses `LoadStrictCatalog` | `Systems/BenchmarkSystem.cs:336,538,631,726`; `Core/GameManager.cs:130` |
| `README.md`, `docs/ecs-gas-m0-baseline.md`, `docs/ecs-gas-m7-evidence.md`, `docs/ecs-gas-m8-evidence.md`, `docs/plan/ecs-gas-migration-orchestration.md`, `docs/ability-phase-boundary-evidence.md`, `tools/capture-m7/m8-fresh-evidence.ps1` | Cited evidence directories under a wrong root (`C:\WorkSpace\AI\…`) | Fixed: paths now use the working root `F:\AI\…`. The `F:\AI\battlesystem-ecs-gate-logs` folder itself is not present |

Risk: none functional. Required checks: `git diff --check`; build for the two `.cs` comment edits.

---

## 4. Must retain

| ID | Item | Location | Rationale (evidence) |
|---|---|---|---|
| R-01 | `SystemRegistry.CreateAll / WireDependencies / AssignToGroups` | `Core/SystemRegistry.cs:304, 353, 441` | Invoked by `ProductionSystemInstaller` stages (`Core/SystemInstaller.cs:199, 201, 203`) — they are the production path, not dead facades. Also called by 14 test files |
| R-02 | `LegacyConfigurationParser`, `GameConfigLoader.LoadConfig` | `Core/GameConfigLoader.cs:34, 64` | Sole loader for `Program.cs:84`, all four benchmark modes (`BenchmarkSystem.cs:336/538/631/726`), **Unity** `BattleDriver.cs:88`, and 23 test call sites. Strict path is used only by `GameManager.cs:130` |
| R-03 | `IBattleEventBus.OnPositionChanged` | `Core/IBattleEventBus.cs` | 0 production calls, but Unity's `UnityEventBus` implements the interface; removal breaks Unity compilation |
| R-04 | Config properties present in shipped JSON (App. C rows with JSON files > 0) | `Core/GameConfig.cs` | Strict parser sets `UnmappedMemberHandling.Disallow` (`Core/TypedGameConfigParser.cs:350`): removing a property that JSON contains makes strict loading throw |
| R-05 | `ComponentStore.ApplyEnemyDamage(...)` compat entry + `DamageResolver.ApplyLegacy` + `LegacyApplyCount` | `Core/ComponentStore_Enemy.cs:2114, 2128`; `Core/GAS/DamageResolver.cs:391`; `GameplayObservation.cs:113` | No production caller, but migration docs keep global legacy entries until M7/M8 closure (`ecs-gas-migration-combat.md` §1.10, §2.9); M8 has **not** exited (`ecs-gas-migration-plan.md` §10). 18 test uses; `LegacyApplyCount` is an observation diagnostic |
| R-06 | `LegacyEffectAdapter`, `ActiveEffects` projection, `AttributeModifierOp.Multiply` | `Core/GAS/LegacyEffectAdapter.cs`; `Core/ComponentStore_World.cs:1033-1153`; `Systems/SkillSystem.cs:334` | Exit condition (`ecs-gas-lumio-contract-alignment.md` §8.6) unmet: `SkillSystem.cs:334` still creates `Multiply(1.1f)`; `BuffSystem.cs:222` and `ElementalReactionSystem.cs:199/216` use the adapter. **Trap:** replacing with `Percent(0.1f)` is not bit-identical (`1.1f − 1f` = 0.100000024f); a behavior-preserving rewrite must use `Percent(1.1f - 1f)` |
| R-07 | `GameplayEffectDef` | 99 production references in 18 files | Live type |
| R-08 | `DamageImmunityFlags` members, `BuffType` members (App. E) | `Core/DamageType.cs:51-56`, `Core/BuffType.cs` | Frozen bit semantics (`ecs-gas-migration-foundation.md` §M1); `BuffType.cs` is hash-guarded by evidence scans; values reached by value/JSON, not by name |
| R-09 | `Data/Levels/*.json` | `Data/Levels/` | Unity reads `level_1.json` directly (`BattleDriver.cs:560, 622`; `NextWavePreview.cs:163`; `WaveProgressOverlay.cs:159`) |
| R-10 | `FrameBindingFacts` / `ReviewedDisabledReason` entries for disabled nodes | `Core/FrameBindingFacts.cs`, `Core/FrameSystemGraph.cs:506-531` | Seal validation contract: required while the nodes are declared. Remove only together with the node (V-05 S-b) |
| R-11 | Generated `Core/SystemRegistrationManifest.generated.cs`, `docs/ecs-gas-m7-nullable-ledger.md` | — | Generated from `tools/system-registration-spec.json` + `Core/SystemRegistry.cs` by `tools/generate-system-registry-ledger.ps1`; change only through the spec and regenerate |
| R-12 | Polyfills, `NullEventBus`, bounds/capacity/generation checks, idempotency guards, Debug validation, `GameplayObservation` | various | Safety and compatibility infrastructure (explicitly out of scope for removal) |
| R-14 | `TowerKillCount` (looks write-only from Core) | `Core/ComponentStore_Tower.cs` | Unity reads it: `TowerInfoPanel.cs:223`, `Tests/PlayMode/TowerInfoPanelLifecycleTests.cs:187` |
| R-15 | `TowerSabotageSystem` | `Systems/TowerSabotageSystem.cs` | Drain path (`ApplyDrainEffects` :128) is live: `MonsterConfig.DrainRatio` → `WaveSpawningSystem.cs:994`. Only its sabotage sub-path has no input (F-01) |
| R-16 | `EnemyAbilityDef.DispelDuration`, `WaveConfig.Rhythm`, `GameConfig.PhaseBehaviors/BehaviorTrees/UpgradeBuffs` | `Core/GameConfig.cs` | Read in-class or validated (`TypedGameConfigParser.cs:135` NonNegative); removed from the App. C candidate set |
| R-13 | `tools/check-test-rules.ps1`, `generate-system-registry-ledger.ps1`, `ab-bench.ps1`, `capture-m8-fresh-evidence.ps1`, `inventory-ecs-gas-migration.ps1` | `tools/` | CI gate / documented evidence or generation tooling |

---

## 5. Insufficient evidence

| ID | Item | Evidence so far | Evidence required to qualify |
|---|---|---|---|
| E-01 | 247 public/internal methods with zero references anywhere incl. Unity (App. A1) | Name-occurrence scan; interface implementations and overrides excluded by construction (≥2 occurrences) | External-consumer confirmation for the public DLL; product intent for UI-facing actions (`DeployHero`, `PlaceObstacle`, `Deposit`/`Withdraw`, `ActivateStealth`, `PlaceBlock`, …), which look like unwired features rather than leftovers |
| E-02 | 102 methods used only by tests (App. A2) | Production-only occurrence scan | Whether each test verifies reachable behavior; otherwise move with the owning feature decision |
| E-03 | 114 write-only / never-written SOA arrays (App. B) | Indexed read/write scan; spot checks found no `ref`/bulk writes and no config fields for Suicide/Tether/Trample/Fear/TrueSight/Variance | Per-field recheck for `ref`/`Volatile`/`Interlocked`/`Array.Copy`; for never-written-but-read fields a product decision (wire = F-01, or constant-fold) |
| E-04 | 43 config properties never read (App. C) | Not read outside declaration/loaders/in-class; strict parser disallows unmapped members | Owner decision on schema change + survey of external/user configs; rows with JSON > 0 are R-04 |
| E-05 | 9 Engine interfaces without implementers (`IWorldView`, `IAttributeView`, `ISpatialQuery`, `ICatalog`, `ICommandSink<T>`, `IDamageResolver`, `IResourceResolver`, `IFrameGraph`, `IDiagnostics`) | `BattleSystemECS.Engine/Contracts.cs:9-20`; introduced in `4bebc43`; principle 9 in `docs/ecs-gas-final-architecture.md:714` says content systems depend on Engine contracts | Architecture owner decision: placeholders for the target state vs. obsolete |
| E-06 | `AbilityInstances` dense projection (`MAX_ENTITIES × MAX_ABILITIES_PER_ENTITY`) | Stated retention reason (Unity reads it) is false per Unity source; store methods and `SkillSystem` still read it; M8 profile lists Ability candidates at 72.4 MB (document figure, evidence dir inaccessible) | Migrate remaining readers to `ActiveAbilityStore`; external-consumer sign-off; M8 storage decision |
| E-07 | Legacy Effect projection arrays (109.2 MB per M8 document) | Coupled to R-06 | R-06 exit condition + external sign-off |
| E-08 | `SaveSystem.SaveCheckpoint` on every wave completion | `Core/SystemRegistrationRecipes.cs:976`; writes `Data/Saves/checkpoint.json` relative to CWD (`Core/SaveSystem.cs:24`); `LoadCheckpoint`, `HasCheckpoint`, `Save/LoadMetaProgression` have no callers (repo or Unity). File I/O runs inside wave-callback dispatch, including tests and mode 5 | Whether players/tools consume the file; if not, it is a write with no reader (removal changes an observable artifact → product decision) |
| E-09 | Data with no loader: 11 `Data/Configs` files (`auto_skill`, `backstab`, `beacon_towers`, `damage_conversion`, `path_terrain`, `player`, `projectile_deflection`, `shop_reroll`, `shrine_towers`, `wave_gold_decay`, `wave_skip`), 238 `Data/Monsters`, 174 of 176 `Data/Towers` (only `tower_links.json` — loaded by the never-constructed `TowerLinkSystem` — and `tower_synergy.json` are read) | No loader constant, no enumeration; Unity code does not read them although `StreamingAssets` holds copies; `BattleSystemECS.csproj` copies `wave_gold_decay.json` and `path_terrain.json` to output | Provenance: are these authoring sources for `game_config.json` or for a Unity sync process outside the repo? |
| E-10 | `tools/capture-m7-fresh-evidence.ps1` | Hard-coded M7 evidence root, deletes `attempt-failures` there; no doc/CI reference; superseded in function by `capture-m8-fresh-evidence.ps1` | Whether M7 evidence must remain reproducible |
| E-11 | Possibly no-op registered systems: `TowerSabotageSystem`, `TowerStealthSystem`, `HeroSystem` | **Resolved in §9.1**: Sabotage must retain (drain path is live via config), Stealth is a verified no-op (V-07), Hero/HeroSkill are an unwired UI entry (F-07) | — |
| E-12 | Typed `EventBus` channels without production subscribers: `PlayerDamaged` (legacy channel; tests subscribe), `BossPhaseChanged` (tests), `EnemyCharging`, `EnemyChargeReleased`, `SideQuestCompleted` (no subscriber anywhere) | `Core/EventBus.cs:70-76`; Unity uses only `IBattleEventBus` | Event migration ledger status (LegacyOnly/Bridge/GameplayOnly) and external-consumer sign-off; benefit is negligible |
| E-13 | Unreferenced enum members other than R-08 (App. E) | Name scan | Ordinal/serialization stability check per enum |

---

## 6. Behavior-changing proposals and functional findings (not cleanup)

| ID | Finding | Evidence |
|---|---|---|
| F-01 | Features whose inputs have no supported write path at all: suicide bombers, enemy tethers, trample, tower fear auras, true sight, tower damage variance, breather bonuses, hero stats | App. B `NEVER_WRITTEN`; no `GameConfig` field or JSON key. Same failure class as the `RecordKill` gap fixed in the pending tree |
| F-02 | Never-assigned dependency setters (inputs silently null): `TowerAttackSystem.SetHeatSystem/SetEnergySystem`, `RandomEventSystem.SetGoldSystem/SetPickupSystem/SetInterestSystem/SetWaveSpawning`, `SkillSystem.InjectSummonSystem`, `PointDefenseSystem.SetEnemyProjectileSystem` | No call sites at HEAD; `git log -S "<Setter>("` over `Core/` returns no commit for any of them (never wired) |
| F-03 | Benchmarks and Unity load configuration through the legacy parser; interactive production uses the strict catalog | R-02 |
| F-04 | Unity plugin predates the Engine split; a Core DLL refresh alone will not load (missing `BattleSystemECS.Engine.dll`) | `Assets/Plugins` listing |
| F-05 | Checkpoint files written relative to CWD during tests and benchmarks (e.g. repo-root `Data/Saves/checkpoint.json` after `dotnet run -- 5`) | E-08 |
| F-06 | Adaptive difficulty wiring/kill/leak/player-id fixes (pending tree) change mode 5 outcome 457 → 445 frames | §1.3 |

---

## 7. Batches, workstreams and verification

### 7.1 Batches (ordered by risk, then benefit)

| Batch | Contents | Risk | Benefit | Blocking conditions | Verification |
|---|---|---|---|---|---|
| **B0** Split pending tree | Three commits: (a) V-01 internal removals; (b) public-surface removals from §1.3; (c) F-06 adaptive fixes + test + CHANGELOG | (a) none; (b) external API; (c) gameplay | Clarity of history | (b) external sign-off; (c) mode 5 outcome change accepted by owner | Gates 1–6 per commit; 7–9 DEFERRED |
| **B1** Docs | V-06 | None | Maintenance | — | `git diff --check`; build for `.cs` comment edits |
| **B2** Dead SOA | V-02 | Low (public fields) | ≈1.0 MB/store | External sign-off | Gates 1–6; `GameplayStorageProfileTests`; determinism soak digests unchanged |
| **B3 — DONE (2026-10-27)** No-op registered systems | V-03, V-04 (and E-11 once traced) | Medium (FrameGraph topology, public types) | Per-frame CPU (not measured) | Product decision; topology-hash approval; mode 4 node timing + mode 5 A/B when un-deferred | Spec edit → regenerate manifest/ledger → Seal validation; `FrameGraphProductionFlowTests`, `CombatGoldenReplayTests`, `DeterminismSoakTests` (state and event-sequence digests must be identical) |
| **B4** Never-constructed systems | V-05 S-a, then S-d, then S-b; S-c only as an F proposal | Medium (public types, tests retired with features) | 6,009 LOC; ≥21.8 MB/store memory | Per-feature retire decision; external sign-off; Unity compile with refreshed Core **and** Engine DLLs | As B3; RNG guard file lists updated to remove deleted files (not weakened) |
| **B5** Data provenance | E-09, E-10 | Unknown until provenance known | Repository size / confusion | Owner confirmation | Strict + legacy load tests; Unity StreamingAssets sync check |

### 7.2 Workstreams and write scopes

| Workstream | Batches | Write scope | Can run in parallel with |
|---|---|---|---|
| WS-1 Documentation | B1 | `AGENTS.md`, `README.md`, `docs/**`, comment-only lines in `Core/GAS/ActiveAbilityStore.cs`, `Systems/TowerAttackSystem.cs` | All, except WS-2 edits to `TowerAttackSystem.cs` (B4 S-b touches line 672) — sequence those |
| WS-2 Registry / FrameGraph / ComponentStore (single owner) | B0(a), B2, B3, B4 S-b, B4 S-d | `tools/system-registration-spec.json`, regenerated `Core/SystemRegistrationManifest.generated.cs` + `docs/ecs-gas-m7-nullable-ledger.md`, `Core/SystemRegistrationRecipes.cs`, `Core/SystemRegistry.cs`, `Core/FrameBindingFacts.cs`, `Core/FrameSystemGraph.cs`, `Core/*Group.cs`, `Core/ComponentStore*.cs`, owning `Systems/*.cs`, bound tests | **Not** parallelizable internally: all batches share the spec, generated manifest, FrameGraph and ComponentStore files |
| WS-3 Standalone systems | B4 S-a | `Systems/{HitTrigger,TowerMerge,EnemyClone,EnemyTeleport,Portal}System.cs`, `BattleSystemECS.Tests/Framework/SimulationRngGuardTests.cs` | WS-1, WS-4. Conflicts with WS-2 only if WS-2 also edits `SimulationRngGuardTests.cs` (B4 S-d) — serialize that file |
| WS-4 Data provenance (read-only until decided) | B5 | `Data/**`, `BattleSystemECS.csproj` copy items, `tools/capture-m7-fresh-evidence.ps1` | All |

### 7.3 Verification plan (applies to every batch)

1. AGENTS.md §8 gates 1–6 (builds 0/0, full tests, `check-test-rules.ps1`, `git diff --check`).
2. Behavioral equivalence: existing golden/replay and soak tests must produce identical `StateDigest` and Gameplay event-sequence digests (`GameplayObservation` harness, tests only — no observation added to production Tick).
3. Registration changes: regenerate via `tools/generate-system-registry-ledger.ps1`; Seal validation and IL/manifest tests must pass; topology hash re-approved by its owner.
4. Gates 7–9 (mode 2/4/5) **DEFERRED** — candidates remain “not ready to commit” until run; CPU benefit claims require mode 4 node timing.
5. Unity: refresh `Assets/Plugins` with Core **and** Engine DLLs and compile — currently **UNAVAILABLE/BLOCKED**.
6. Rollback: one commit per batch; `git revert` of that commit. No batch changes save files or config schema (S-c excluded by design).

---

## 8. Proven vs inferred

- **Proven (static evidence in this repository and Unity source):** zero-reference symbols; never-constructed classes; arrays without indexed access; parser usage; facade call path; absence of config fields/JSON keys for F-01 inputs; checkpoint write without loader; Unity DLL date and missing Engine DLL.
- **Inferred:** product intent of disabled features and UI-facing methods; provenance of unreferenced data; per-frame CPU benefit of V-03/V-04; that the M8 document figures still hold at HEAD (evidence directories not accessible).
- **Not used as evidence:** passing tests as proof of behavioral equivalence; historical benchmark numbers.

---

## 9. Follow-up pass (2026-10-27, working tree)

Scope: read-only against the `F:\AI\BattleSystem-ECS` working tree (HEAD `3b10ebc` + the pending changes in §1.3). No worktree or directory was created.

### 9.1 E-11 resolution — registered systems traced end to end

| System | Result | Evidence |
|---|---|---|
| `TowerSabotageSystem` | **Must retain (R-15)** | Three phases (`Update` :35): disable-timer decay, sabotage (`EnemyCanSabotage` gate :74 — no writer, no config field), drain (`EnemyDrainRatio` gate in `ApplyDrainEffects` :128 — written from `MonsterConfig.DrainRatio` at `WaveSpawningSystem.cs:994`). Drain is reachable for any config that sets `DrainRatio` even though shipped JSON does not — "not currently triggered" ≠ unnecessary |
| `TowerStealthSystem` | **V-07 verified no-op — DONE (B3, retired)** | Every effect is gated on `TowerStealthType != 0` / `TowerIsStealthed`. `TowerStealthType` has no writer anywhere (store, systems, Unity) and no config field; `TowerIsStealthed` is set true only by `ActivateStealth`, which has no caller (repo, Unity). Its only consumer, `CanTargetTower`, is called only by `SuicideBombSystem` (itself V-04). No RNG. Retirement shares the B3 boundary (spec/FrameGraph/group slot) |
| `HeroSystem`, `HeroSkillSystem` | **F-07 unwired UI entry** | All work is gated on `HeroIsDeployed` (`HeroSystem` move/attack loops; `HeroSkillSystem.cs:150, 192`), set only by `DeployHero` (`HeroSystem.cs:216`) whose doc comment claims it is "Called from TowerPlacementSystem" — there is no caller anywhere (repo, `GameManager`, Unity). Cost per frame: a `Parallel.For` over `MAX_HEROES`=5 slots. Decision: wire `DeployHero` to input, or retire |

### 9.2 SOA candidate confirmation (App. B)

All 112 remaining App. B arrays (2 wind arrays are already removed in the working tree) were re-scanned for the blind-spot access forms (`ref` locals, `Volatile/Interlocked(ref …)`, `Array.Copy/Fill/Clear/Resize`, `AsSpan/CopyTo`, `Span<T>` aliases): **0 hits for every array**. Unity cross-check moved `TowerKillCount` to R-14.

| Class | Arrays | Static size (lower bound) | Meaning | Classification |
|---|---|---|---|---|
| RESET_ONLY | 29 | ≥ 7.34 MB / store | Written only by `ComponentStore*` lifecycle/store code, never read anywhere | **V-08** (App. B2). Blocking: public fields → external sign-off; 4 arrays have test references that must be updated with the removal |
| COMPUTED_UNREAD | 44 (excl. R-14) | ≥ 12.30 MB / store | A system computes and stores a value nobody reads | E-03 — removing the array also removes the computation; each needs a check that the computation has no other side effect |
| NEVER_WRITTEN | 33 | ≥ 8.68 MB / store | Read, but always the default value | F-01 / E-03 — either wire an input or constant-fold the readers (behavior-identical only if every reader is rewritten to use the default) |
| NONE | 6 | ≈ 0.95 MB / store | No indexed access | V-02 (unchanged) |

### 9.3 E-06 refinement — `AbilityInstances` is a read fallback, not a pure mirror

`ComponentStore.GetAbility` (`Core/ComponentStore_World.cs:822`) returns the sparse pool value when the slot handle is valid and otherwise **returns the dense `AbilityInstances[idx]`**. `SetAbility` writes the dense slot only after the pool accepts the value; `ReleaseEntityAbilities` clears it to `default`; `TryTickAbilityCooldown` (`:857`) writes the dense `Cooldown` on every cooldown tick. `GetAbility` has 15 production and 50 test call sites, 0 in Unity.
Evidence required before E-06 can become a candidate: proof that every path leaving a slot with an invalid handle also leaves the dense slot at `default` (including pool-rejection in `SetAbility` and `TryAddAbility`), or an explicit decision to change the fallback semantics (F).

### 9.4 Config property refinement (App. C)

The 16 properties that App. C excluded because of in-class mentions were inspected line by line:

- **Comment-only mentions → add to the unread set (E-04):** `MonsterConfig.BurrowDuration` (:133), `TowerConfig.FormSwitchCooldown` (:362), `TowerConfig.PierceCount` (:364), `TowerConfig.PierceDmgFalloff` (:366), `TowerConfig.FalloffType/FalloffStartRatio/FalloffMinRatio` (:502–507), `EnemyAbilityDef.DispelImmunityDuration` (:989), `DispelEnemyDef.DispelDuration` (:1302).
- **Read only through uncalled accessors, never populated by any loader:** `GameConfig.ThiefDefs` (:1918, `GetThiefDef` 0 callers), `GameConfig.HealerDefs` (:1921, `GetHealerDef` 0 callers) → E-04.
- **Live → R-16:** `WaveConfig.Rhythm`, `GameConfig.PhaseBehaviors`, `GameConfig.BehaviorTrees`, `GameConfig.UpgradeBuffs`, `EnemyAbilityDef.DispelDuration`.

### 9.5 Additional functional findings

| ID | Finding | Evidence |
|---|---|---|
| F-07 | Hero deployment is unreachable; `DeployHero` doc comment is misleading | §9.1 |
| F-08 | `TowerSabotageSystem` sabotage sub-feature and `TowerStealthSystem` stealth types have no input path | §9.1 |

### 9.6 Batch impact

- **B2 — DONE (2026-10-27, `refactor: remove dead ComponentStore SOA arrays (B2)`)**: V-02 + V-08, all 35 arrays removed (≈ 8.7 MB / store, static). Re-verified across Core/Systems/Tests/Program/tools and Unity `Assets` before removal; **kept list: none** (`TowerKillCount` is R-14 and was never in the candidate set). Tests touching removed fields had only those assertion lines deleted (`SiegeEnemyTests`, `TerrainZoneSystemTests`, `CorpseEffectSystemTests`, `CatalogEntryBehaviorMatrixTests`, `FrameSchedulerTests`). Originally: B2 grows to V-02 + V-08 (35 arrays, ≥ 8.3 MB / store, static). Same blocking conditions; owned by WS-2 (ComponentStore files).
- **B3 — DONE (2026-10-27, `refactor: retire no-op Fear/SuicideBomb/TowerStealth systems (B3)`)**: V-03, V-04, V-07 retired (3 systems, 659 LOC, 15 SOA arrays ≈ 4.8 MB/store static, 4 frame nodes; graph nodes 198 → 194, topology and review-root hashes re-pinned, golden/replay/determinism digests unchanged). Follow-up: `EnemyActionType.Fear` and its `EnemyMovementSystem` branch are now unreachable. Original note: B3 adds V-07 (`TowerStealthSystem`); retiring it must happen together with or after V-04 (`SuicideBombSystem` is its only consumer).
- **B4a — DONE (2026-10-27, `refactor: remove never-constructed standalone systems (B4a)`)**: S-a retired: `HitTriggerSystem`, `EnemyCloneSystem`, `EnemyTeleportSystem`, `PortalSystem` (641 LOC; `TowerMergeSystem` was already removed in `6621ab3`). RNG guard file-name list lost the 3 deleted entries (not weakened). Their exclusive SOA arrays (`EnemyClone*`, `EnemyIsClone`, `EnemyTeleportCooldown` etc.) were intentionally left for a store cleanup follow-up. No Unity references; Unity csc check still baseline 3 errors.
- **B4b — DONE (2026-10-27, `refactor: remove never-constructed store-coupled systems (B4b)`)**: S-d retired: `TotemSystem`, `ReforgeSystem`, `WaveSkipSystem`, `TowerFormSwitchSystem`, `ObstacleSystem` (1,169 LOC; `WindSystem` was already removed in `6621ab3`) plus `TowerFormSwitchSystemTests`. Store: 22 exclusive SOA arrays / cooldown columns removed (Totem 9, wave-skip 2, tower form 5, tower affix/reforge 5, `ObstacleType` 1; ≈ 3.2 MB/store for the `MAX_ENTITIES` ones, static) and their accessor methods; `MechanismAbilityIds.TotemPulse/TotemSummon` removed. `WaveSkipConfig` kept (moved into `Core/GameConfig.cs`, same namespace) because `GameConfig.WaveSkip` is schema. Obstacle store arrays other than `ObstacleType` stay (read by `TowerAttackSystem`/`PathBlockSystem`). Orphaned config: `GameConfig.WaveSkip`, `GameConfig.Reforge`/`TowerAffixes`, tower `forms` config (applied via the removed `SetTowerForms`), `GameConfig.ObstacleDefs`.
- **B4c — DONE (2026-10-27, `refactor: retire spec-disabled unwired systems (B4c)`)**: S-b retired (decision rule: disabled reason / feature policy says "not enabled by production composition" / "intentionally absent" with no milestone tie): `TowerIncomeSystem`, `TowerRelocateSystem`, `TowerConstructionSystem`, `EnemyAffixSystem`, `EnemyWoundSystem`, `EnemyHealerSystem`, `EnemyStealGoldSystem`, `PlayerSummonSystem`, `TowerOverchargeSystem`, `TowerLinkSystem`, `PatrolTowerSystem`, `FogOfWarSystem`, `PointDefenseSystem`, `HeatSystem`, `TowerDemolishSystem`, `TowerSilenceSystem`, `TowerDispelSystem`, `EnemyProjectileSystem`, `BeamTowerSystem`, `TowerEnergySystem` (20 files, 3,233 LOC); all 18 `feature-disabled` spec entries (spec now has none), 30 reviewed-disabled frame nodes with their `ReviewedDisabledReason` / `FrameAccessReviewCatalog` / `FrameAdapterBindingCatalog` entries, group slots, `TowerAttackSystem` heat/energy/beam seams, `ITowerHeatPort` / `ITowerEnergyPort` / `IEnemyProjectilePort`. Store: 58 exclusive SOA arrays (incl. the B4a orphans `EnemyClone*`, `EnemyIsClone`, `EnemyTeleport*`) ≈ 19.6 MB/store (static) plus 8 accessor methods. Tests retired: wound bullet-time test, `EnemyProjectile_HitsPlayerEntityIdNotHardcodedSlotOne`, beam/energy graph assertions. **Kept** (reason): `EnemyLifestealSystem` + nodes `ai.lifesteal.prepare/update` (source comment reserves the slot for a future aura-based passive lifesteal), node `post-death.life-link.resolve` (DisabledUnsafe: destroyed-source policy undefined, owner decision), `IEnemyAffixDecorator` ctor parameter of `WaveSpawningSystem` and `ISummonCommandPort`/`SkillSystem.CastSummon` (behavioral seams of the skill/spawn features, owner decision). S-c `CraftingSystem` untouched.
- **B1-doc follow-up:** `HeroSystem.DeployHero` comment ("Called from TowerPlacementSystem") is misleading (F-07).

## 10. Unity consumer audit (2026-10-27, read-only)

Scope: `F:\AI\BattleSystem-ECS-Unity` (own git repo, HEAD `14bb123`), read-only. The Unity Editor was not opened (another session was running Unity tests/builds). No file in the Unity project was changed. Core under test: `master` at `485cd29`.

### 10.1 Static compile check of Unity runtime scripts against current Core

Method: the 30 scripts under `Assets/Scripts` (assembly `BattleSystemECS.Unity`) were compiled with the .NET SDK's Roslyn `csc` against Unity's own reference set (`Editor/Data/NetStandard/ref/2.1.0/netstandard.dll`, the 87 `Managed/UnityEngine/*.dll` modules, and `Library/ScriptAssemblies/{UnityEngine.UI, Unity.RenderPipelines.Core.Runtime, Unity.RenderPipelines.Universal.Runtime}.dll`), defines `UNITY_2022_3_OR_NEWER;UNITY_STANDALONE;UNITY_STANDALONE_WIN`.

| Run | Core reference | Result |
|---|---|---|
| Control | `Assets/Plugins/BattleSystemECS.Core.dll` (2026-06-10) | **0 errors** — the harness reproduces the Unity runtime build |
| Current | `BattleSystemECS.Core.dll` + `BattleSystemECS.Engine.dll` built from `master` | **3 errors**, all `CS1061 'FrameScheduler' does not contain a definition for 'Spawning'` at `Assets/Scripts/BattleDriver.cs:246, 473, 484` |

- `FrameScheduler.Spawning` became `internal` in `4bebc43`. Unity uses it to detach/re-attach `WaveSpawning` between waves (its wave-pacing takeover).
- **No other Core API used by Unity runtime scripts is missing**; in particular none of the removals in `0a7f21a`/`6621ab3` and none of the V-candidates in this report is referenced by Unity runtime code.
- Not covered: `Assets/Tests/PlayMode` (21 files; needs NUnit/Test Framework references) and `Assets/Editor` (needs UnityEditor). Their Core usage was checked by identifier only (e.g. `TowerInfoPanelLifecycleTests.cs:187` writes `TowerKillCount`).

### 10.2 Runtime compatibility (static reading)

| Finding | Evidence | Consequence |
|---|---|---|
| Unity composes the game through the registry facades, not the installer | `BattleDriver.cs:96-107`: `new SystemRegistry()`, `CreateAll(…, 1, …)`, `WireDependencies(_store, 1)`, `new FrameScheduler(_store, _config, _eventBus)`, `AssignToGroups(_scheduler)`; then `_scheduler.Tick` at :215 | With current Core the first `Tick` throws `InvalidOperationException("Frame graph composition must be built, validated, and sealed before the first Tick.")` (`FrameScheduler.RequireFrameGraph`). `SealGraphComposition` is `internal`; only `ProductionSystemInstaller.Install` seals. Unity must move to `new ProductionSystemInstaller().Install(...)` |
| Plugin assembly reference list | `Assets/Scripts/BattleSystemECS.Unity.asmdef`: `overrideReferences: true`, `precompiledReferences: ["BattleSystemECS.Core.dll"]` | A post-split Core needs `BattleSystemECS.Engine.dll` copied to `Assets/Plugins` **and** added to `precompiledReferences` |
| Reflection on Core field/property names | `UnityEventBus.cs:118-…` looks up `ComponentStore` fields `EnemyTypeName`, `EnemyType`, `EnemyMonsterType`, `EnemyClass`, `EnemyName`; `BattleDriver.cs:537-548` looks up `GameConfig.Levels`, `LevelConfig.WaveCount`, `LevelConfig.Waves` | These names are part of the Unity contract even though no compiler checks them → R-17 |
| Enum surfaced in UI | `TowerBuildMenu.cs:147` builds the tower menu from `Enum.GetValues(typeof(TowerType))` | Every `TowerType` member is user-visible in Unity; removing or reordering members changes the UI → R-18 |
| Config loading path | `BattleDriver.cs:88` `GameConfigLoader.LoadConfig` (legacy parser) | Paths are resolved against the process working directory (Unity project root in the Editor), see 10.3 |

### 10.3 Data distribution chain

| Location (Unity repo) | Content | Relation to this repository |
|---|---|---|
| `game_config.json` (project root) | 169,800 bytes, mtime 2026-06-20 | **Stale** copy of this repo's `game_config.json` (269,625 bytes). This is what Core's loader reads in the Editor |
| `Data/Configs/` | 473 files | Flattened copy of this repo's whole `Data/` tree: 51 files identical to `Data/Configs`, `skills.json` differs, 421 files are Unity-only names that correspond to this repo's `Data/Monsters`, `Data/Towers`, `Data/Levels` and `game_config.json`. There is no `Data/Skills` directory |
| `Data/Saves/checkpoint.json` | Written by `SaveSystem` (E-08) in the Editor working directory | Confirms the checkpoint write also happens in Unity |
| `Assets/StreamingAssets/` | 473 data files (+ .meta) | Read directly by Unity scripts: `level_1.json` (`BattleDriver.cs:560, 622`, `NextWavePreview.cs:163`, `WaveProgressOverlay.cs:159`) and `game_config.json` MonsterTypes (`SyncMonsterTypesFromJson`; Unity's own AGENTS.md notes 763 bytes of trailing garbage in that file) |

No sync script exists in either repository (`Tools/` contains only `check_braces.py`); the DLL and data copies are manual and stale. This resolves part of E-09: `Data/Monsters`/`Data/Towers` files reach Unity only as flattened copies that **no Unity script and no Core loader reads**; their authoring purpose (if any) is still unknown.

### 10.4 Effect on classifications

| ID | Change |
|---|---|
| R-01 | Strengthened: the registry facades are also Unity's (current) composition path |
| R-17 (new) | Keep the reflected names `ComponentStore.EnemyTypeName` and `GameConfig.Levels` / `LevelConfig.WaveCount` / `LevelConfig.Waves` |
| R-18 (new) | Keep `TowerType` members and order (Unity tower menu) |
| F-04 | Expanded: Unity is not runnable on current Core — 3 compile errors (`FrameScheduler.Spawning`) and an unsealed-graph exception on the first `Tick`; needs Engine DLL + asmdef change, `ProductionSystemInstaller.Install`, and a supported API for pausing wave spawning |
| E-01, V-02, V-05, V-08 | Unity runtime-script consumer check is now compile-verified (not just identifier search); external consumers other than Unity remain unknown |
| E-09 | Partially resolved (10.3) |
| E-14 (new) | Legacy execution path: `FrameSchedulerExecutionMode.Legacy` → `FrameScheduler.TickLegacy` (`Core/FrameScheduler.cs:281`, 79 lines) plus the 11 `*Group.Execute` methods. Production and Unity default to `Graph`; only `FrameGraphTests.cs:332-364` select `Legacy`, as graph-vs-legacy equivalence checks (M5 verification tooling). Exit condition (M5 declared complete) unmet → insufficient evidence |

## Appendices

Appendix tables are generated from the scan outputs at HEAD `3b10ebc`; Appendix B2 from the working tree. File paths are relative to the repository root.

### Appendix S — Never-constructed system classes (34)

| # | Class | File (LOC) | Spec entry | Reviewed-disabled frame node(s) | Group slot | Tests constructing | Test files naming it | Unity refs |
|---|---|---|---|---|---|---|---|---|
| S1 | `BeamTowerSystem` | `Systems/BeamTowerSystem.cs` (267) | none | combat.beam.update | CombatGroup.cs  | 0 | 1 | 0 |
| S2 | `CraftingSystem` | `Systems/CraftingSystem.cs` (375) | none | — | — | 2 | 2 | 0 |
| S3 | `EnemyAffixSystem` | `Systems/EnemyAffixSystem.cs` (214) | EnemyAffix | — | AIGroup.cs  | 0 | 1 | 0 |
| S4 | `EnemyCloneSystem` | `Systems/EnemyCloneSystem.cs` (216) | none | — | — | 0 | 1 | 0 |
| S5 | `EnemyHealerSystem` | `Systems/EnemyHealerSystem.cs` (111) | EnemyHealer | movement.enemy-healer.prepare, movement.enemy-healer.update | MovementGroup.cs  | 0 | 0 | 0 |
| S6 | `EnemyLifestealSystem` | `Systems/EnemyLifestealSystem.cs` (33) | none | — | AIGroup.cs  | 0 | 0 | 0 |
| S7 | `EnemyProjectileSystem` | `Systems/EnemyProjectileSystem.cs` (239) | EnemyProjectile | combat.enemy-projectile.update | CombatGroup.cs  | 1 | 1 | 0 |
| S8 | `EnemyStealGoldSystem` | `Systems/EnemyStealGoldSystem.cs` (70) | StealGold | — | MovementGroup.cs  | 0 | 0 | 0 |
| S9 | `EnemyTeleportSystem` | `Systems/EnemyTeleportSystem.cs` (165) | none | — | — | 0 | 1 | 0 |
| S10 | `EnemyWoundSystem` | `Systems/EnemyWoundSystem.cs` (75) | Wound | movement.wound.prepare, movement.wound.update | MovementGroup.cs  | 0 | 1 | 0 |
| S11 | `FogOfWarSystem` | `Systems/FogOfWarSystem.cs` (133) | Fog | — | SpatialGroup.cs  | 0 | 0 | 0 |
| S12 | `HeatSystem` | `Systems/HeatSystem.cs` (146) | Heat | combat-setup.heat.prepare, combat.heat.update | CombatGroup.cs CombatSetupGroup.cs  | 0 | 0 | 0 |
| S13 | `HitTriggerSystem` | `Systems/HitTriggerSystem.cs` (149) | none | — | — | 0 | 0 | 0 |
| S14 | `ObstacleSystem` | `Systems/ObstacleSystem.cs` (157) | none | — | MovementGroup.cs  | 0 | 0 | 0 |
| S15 | `PatrolTowerSystem` | `Systems/PatrolTowerSystem.cs` (181) | PatrolTower | spatial.patrol.prepare, spatial.patrol.update | SpatialGroup.cs  | 0 | 0 | 0 |
| S16 | `PlayerSummonSystem` | `Systems/PlayerSummonSystem.cs` (331) | Summon | — | MovementGroup.cs  | 0 | 0 | 0 |
| S17 | `PointDefenseSystem` | `Systems/PointDefenseSystem.cs` (136) | PointDefense | spatial.point-defense.prepare, spatial.point-defense.update | SpatialGroup.cs  | 0 | 1 | 0 |
| S18 | `PortalSystem` | `Systems/PortalSystem.cs` (111) | none | — | — | 0 | 1 | 0 |
| S19 | `ProtectorSystem` | `Systems/ProtectorSystem.cs` (226) | none | — | — | 1 | 1 | 0 |
| S20 | `ReforgeSystem` | `Systems/ReforgeSystem.cs` (337) | none | — | — | 0 | 1 | 0 |
| S21 | `TotemSystem` | `Systems/TotemSystem.cs` (409) | none | — | — | 0 | 0 | 0 |
| S22 | `TowerConstructionSystem` | `Systems/TowerConstructionSystem.cs` (103) | Construction | pregame.construction.update | PreGameGroup.cs  | 0 | 0 | 0 |
| S23 | `TowerDemolishSystem` | `Systems/TowerDemolishSystem.cs` (186) | Demolish | combat.demolish.update | CombatGroup.cs  | 0 | 0 | 0 |
| S24 | `TowerDispelSystem` | `Systems/TowerDispelSystem.cs` (63) | Dispel | combat.dispel.update | CombatGroup.cs  | 0 | 0 | 0 |
| S25 | `TowerEnergySystem` | `Systems/TowerEnergySystem.cs` (161) | none | combat.energy.update | CombatGroup.cs  | 0 | 0 | 0 |
| S26 | `TowerFormSwitchSystem` | `Systems/TowerFormSwitchSystem.cs` (111) | none | — | — | 1 | 1 | 0 |
| S27 | `TowerIncomeSystem` | `Systems/TowerIncomeSystem.cs` (94) | TowerIncome | build.tower-income.update, post-death.tower-income.update | PostDeathGroup.cs  | 0 | 0 | 0 |
| S28 | `TowerLinkSystem` | `Systems/TowerLinkSystem.cs` (391) | TowerLink | combat-setup.link.prepare, combat.link.update | CombatGroup.cs CombatSetupGroup.cs  | 0 | 0 | 0 |
| S29 | `TowerMergeSystem` | `Systems/TowerMergeSystem.cs` (93) | none | — | — | 0 | 0 | 0 |
| S30 | `TowerOverchargeSystem` | `Systems/TowerOverchargeSystem.cs` (104) | TowerOvercharge | combat-setup.overcharge.prepare, combat.overcharge.update | CombatGroup.cs CombatSetupGroup.cs  | 0 | 0 | 0 |
| S31 | `TowerRelocateSystem` | `Systems/TowerRelocateSystem.cs` (164) | TowerRelocate | build.tower-relocate.update | — | 0 | 0 | 0 |
| S32 | `TowerSilenceSystem` | `Systems/TowerSilenceSystem.cs` (64) | TowerSilence | combat.silence.update | CombatGroup.cs  | 0 | 0 | 0 |
| S33 | `WaveSkipSystem` | `Systems/WaveSkipSystem.cs` (154) | none | — | — | 0 | 0 | 0 |
| S34 | `WindSystem` | `Systems/WindSystem.cs` (240) | none | — | — | 0 | 0 | 0 |

### Appendix D — SOA arrays referenced only by never-constructed systems (88; static size estimate at MAX_ENTITIES = 100,000; named-constant lengths counted as 0)

| Array | Store file | Type[len] | Est. bytes (100k slots) | Referencing never-constructed system(s) |
|---|---|---|---|---|
| `TowerBeamChainCount` | `ComponentStore_Tower.cs` | `int[MAX_ENTITIES]` | 400000 bytes | BeamTowerSystem |
| `TowerBeamChainDecay` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | BeamTowerSystem |
| `TowerBeamDPS` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | BeamTowerSystem |
| `TowerBeamMaxRange` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | BeamTowerSystem |
| `EnemyTeleportCooldown` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | EnemyAffixSystem,EnemyTeleportSystem |
| `EnemyCloneCooldown` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | EnemyCloneSystem |
| `EnemyCloneCount` | `ComponentStore_Enemy.cs` | `int[MAX_ENTITIES]` | 400000 bytes | EnemyCloneSystem |
| `EnemyCloneDefId` | `ComponentStore_Enemy.cs` | `int[MAX_ENTITIES]` | 400000 bytes | EnemyCloneSystem |
| `EnemyCloneMasterId` | `ComponentStore_Enemy.cs` | `int[MAX_ENTITIES]` | 400000 bytes | EnemyCloneSystem |
| `EnemyCloneTimer` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | EnemyCloneSystem |
| `EnemyIsClone` | `ComponentStore_Enemy.cs` | `bool[MAX_ENTITIES]` | 100000 bytes | EnemyCloneSystem |
| `EnemyHealerHealAmount` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | EnemyHealerSystem |
| `EnemyHealerHealInterval` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | EnemyHealerSystem |
| `EnemyHasStolenGold` | `ComponentStore_Enemy.cs` | `bool[MAX_ENTITIES]` | 100000 bytes | EnemyStealGoldSystem |
| `EnemyStolenGold` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | EnemyStealGoldSystem |
| `EnemyTeleportDestinationX` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | EnemyTeleportSystem |
| `EnemyTeleportDestinationY` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | EnemyTeleportSystem |
| `EnemyTeleportType` | `ComponentStore_Enemy.cs` | `int[MAX_ENTITIES]` | 400000 bytes | EnemyTeleportSystem |
| `EnemyIsWounded` | `ComponentStore_Enemy.cs` | `bool[MAX_ENTITIES]` | 100000 bytes | EnemyWoundSystem |
| `EnemyWoundSlowRatio` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | EnemyWoundSystem |
| `EnemyWoundThreshold` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | EnemyWoundSystem |
| `GlobalFogDensity` | `ComponentStore_World.cs` | `float[MAX_PLAYERS]` | 0 bytes | FogOfWarSystem |
| `TowerCanOverheat` | `ComponentStore_Tower.cs` | `bool[MAX_ENTITIES]` | 100000 bytes | HeatSystem |
| `TowerHeat` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | HeatSystem |
| `TowerHeatCooldownRate` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | HeatSystem |
| `TowerHeatPerShot` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | HeatSystem |
| `TowerIsOverheated` | `ComponentStore_Tower.cs` | `bool[MAX_ENTITIES]` | 100000 bytes | HeatSystem |
| `TowerMaxHeat` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | HeatSystem |
| `TowerOverheatBonus` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | HeatSystem |
| `TowerOverheatPenalty` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | HeatSystem |
| `TowerOverheatTimer` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | HeatSystem |
| `ObstacleType` | `ComponentStore_World.cs` | `int[MAX_OBSTACLES]` | 0 bytes | ObstacleSystem |
| `SummonedUnitAttackRange` | `ComponentStore_Enemy.cs` | `int[MAX_ENTITIES]` | 400000 bytes | PlayerSummonSystem |
| `SummonedUnitAttackSpeed` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | PlayerSummonSystem |
| `SummonedUnitAttackTimer` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | PlayerSummonSystem |
| `SummonedUnitDamage` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | PlayerSummonSystem |
| `SummonedUnitDuration` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | PlayerSummonSystem |
| `SummonedUnitGoldReward` | `ComponentStore_Enemy.cs` | `int[MAX_ENTITIES]` | 400000 bytes | PlayerSummonSystem |
| `SummonedUnitMoveSpeed` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | PlayerSummonSystem |
| `SummonedUnitOwnerId` | `ComponentStore_Enemy.cs` | `int[MAX_ENTITIES]` | 400000 bytes | PlayerSummonSystem |
| `SummonedUnitTargetId` | `ComponentStore_Enemy.cs` | `int[MAX_ENTITIES]` | 400000 bytes | PlayerSummonSystem |
| `SummonedUnitType` | `ComponentStore_Enemy.cs` | `int[MAX_ENTITIES]` | 400000 bytes | PlayerSummonSystem |
| `TowerInterceptRate` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | PointDefenseSystem |
| `EnemyIsProtector` | `ComponentStore_Enemy.cs` | `bool[MAX_ENTITIES]` | 100000 bytes | ProtectorSystem |
| `EnemyProtectDamageTransfer` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | ProtectorSystem |
| `EnemyProtectMaxTargets` | `ComponentStore_Enemy.cs` | `int[MAX_ENTITIES]` | 400000 bytes | ProtectorSystem |
| `EnemyProtectRadius` | `ComponentStore_Enemy.cs` | `float[MAX_ENTITIES]` | 400000 bytes | ProtectorSystem |
| `TowerAffixLockMask` | `ComponentStore_Tower.cs` | `int[MAX_ENTITIES]` | 400000 bytes | ReforgeSystem |
| `TowerAffixSlotCount` | `ComponentStore_Tower.cs` | `int[MAX_ENTITIES]` | 400000 bytes | ReforgeSystem |
| `TowerReforgeCount` | `ComponentStore_Tower.cs` | `int[MAX_ENTITIES]` | 400000 bytes | ReforgeSystem |
| `TotemActive` | `ComponentStore_Player.cs` | `bool[MAX_TOTEMS]` | 0 bytes | TotemSystem |
| `TotemChargesLeft` | `ComponentStore_Player.cs` | `int[MAX_TOTEMS]` | 0 bytes | TotemSystem |
| `TotemDurationLeft` | `ComponentStore_Player.cs` | `float[MAX_TOTEMS]` | 0 bytes | TotemSystem |
| `TotemOwnerId` | `ComponentStore_Player.cs` | `int[MAX_TOTEMS]` | 0 bytes | TotemSystem |
| `TotemPosX` | `ComponentStore_Player.cs` | `float[MAX_TOTEMS]` | 0 bytes | TotemSystem |
| `TotemPosY` | `ComponentStore_Player.cs` | `float[MAX_TOTEMS]` | 0 bytes | TotemSystem |
| `TotemType` | `ComponentStore_Player.cs` | `int[MAX_TOTEMS]` | 0 bytes | TotemSystem |
| `TowerDispelImmunityTimer` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | TowerDispelSystem |
| `TowerDispelTimer` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | TowerDispelSystem |
| `TowerEnergy` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | TowerEnergySystem |
| `TowerEnergyPerShot` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | TowerEnergySystem |
| `TowerEnergyRegen` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | TowerEnergySystem |
| `TowerEnergyRegenRadius` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | TowerEnergySystem |
| `TowerIsEnergyTower` | `ComponentStore_Tower.cs` | `bool[MAX_ENTITIES]` | 100000 bytes | TowerEnergySystem |
| `TowerMaxEnergy` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | TowerEnergySystem |
| `TowerActiveForm` | `ComponentStore_Tower.cs` | `int[MAX_ENTITIES]` | 400000 bytes | TowerFormSwitchSystem |
| `TowerFormCount` | `ComponentStore_Tower.cs` | `int[MAX_ENTITIES]` | 400000 bytes | TowerFormSwitchSystem |
| `TowerLinkComboType` | `ComponentStore_Tower.cs` | `string[MAX_ENTITIES]` | 800000 bytes | TowerLinkSystem |
| `TowerCanOvercharge` | `ComponentStore_Tower.cs` | `bool[MAX_ENTITIES]` | 100000 bytes | TowerOverchargeSystem |
| `TowerIsOvercharged` | `ComponentStore_Tower.cs` | `bool[MAX_ENTITIES]` | 100000 bytes | TowerOverchargeSystem |
| `TowerOverchargeCooldown` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | TowerOverchargeSystem |
| `TowerOverchargeDuration` | `ComponentStore_Tower.cs` | `float[MAX_ENTITIES]` | 400000 bytes | TowerOverchargeSystem |
| `PlayerSkipBonusDamagePct` | `ComponentStore_Player.cs` | `float[MAX_PLAYERS]` | 0 bytes | WaveSkipSystem |
| `PlayerWaveSkipsUsed` | `ComponentStore_Player.cs` | `int[MAX_PLAYERS]` | 0 bytes | WaveSkipSystem |
| `GlobalWindActive` | `ComponentStore_World.cs` | `bool[MAX_PLAYERS]` | 0 bytes | WindSystem |
| `GlobalWindDirection` | `ComponentStore_World.cs` | `float[MAX_PLAYERS]` | 0 bytes | WindSystem |
| `GlobalWindDuration` | `ComponentStore_World.cs` | `float[MAX_PLAYERS]` | 0 bytes | WindSystem |
| `GlobalWindGustInterval` | `ComponentStore_World.cs` | `float[MAX_PLAYERS]` | 0 bytes | WindSystem |
| `GlobalWindGustStrength` | `ComponentStore_World.cs` | `float[MAX_PLAYERS]` | 0 bytes | WindSystem |
| `GlobalWindGustTimer` | `ComponentStore_World.cs` | `float[MAX_PLAYERS]` | 0 bytes | WindSystem |
| `GlobalWindStrength` | `ComponentStore_World.cs` | `float[MAX_PLAYERS]` | 0 bytes | WindSystem |
| `WindSourceActive` | `ComponentStore_World.cs` | `bool[MAX_WIND_SOURCES]` | 0 bytes | WindSystem |
| `WindSourceDirection` | `ComponentStore_World.cs` | `float[MAX_WIND_SOURCES]` | 0 bytes | WindSystem |
| `WindSourceDuration` | `ComponentStore_World.cs` | `float[MAX_WIND_SOURCES]` | 0 bytes | WindSystem |
| `WindSourceRadius` | `ComponentStore_World.cs` | `float[MAX_WIND_SOURCES]` | 0 bytes | WindSystem |
| `WindSourceStrength` | `ComponentStore_World.cs` | `float[MAX_WIND_SOURCES]` | 0 bytes | WindSystem |
| `WindSourceX` | `ComponentStore_World.cs` | `float[MAX_WIND_SOURCES]` | 0 bytes | WindSystem |
| `WindSourceY` | `ComponentStore_World.cs` | `float[MAX_WIND_SOURCES]` | 0 bytes | WindSystem |

### Appendix A1 — Public/internal methods with zero references outside their declaration (Core, Systems, Engine, Program, Tests, tools, Unity) (247)

| # | Symbol | Location (HEAD) |
|---|---|---|
| A1-1 | `IsEnemyActive` | `Core/ComponentStore.cs:1573` |
| A1-2 | `IsPlayer` | `Core/ComponentStore.cs:1579` |
| A1-3 | `GetEnemyMaxHealth` | `Core/ComponentStore_Enemy.cs:1712` |
| A1-4 | `GetEnemyArmor` | `Core/ComponentStore_Enemy.cs:1718` |
| A1-5 | `SetEnemyArmor` | `Core/ComponentStore_Enemy.cs:1724` |
| A1-6 | `GetEnemySoulValue` | `Core/ComponentStore_Enemy.cs:1903` |
| A1-7 | `ApplyEnemyDamageLegacy` | `Core/ComponentStore_Enemy.cs:2141` |
| A1-8 | `GetEnemyMoveSpeed` | `Core/ComponentStore_Enemy.cs:2159` |
| A1-9 | `GetEnemyDamage` | `Core/ComponentStore_Enemy.cs:2165` |
| A1-10 | `GetEnemyGoldReward` | `Core/ComponentStore_Enemy.cs:2171` |
| A1-11 | `ClearStagger` | `Core/ComponentStore_Enemy.cs:2413` |
| A1-12 | `ClearSlow` | `Core/ComponentStore_Enemy.cs:2424` |
| A1-13 | `DecrementEnemySlowDurations` | `Core/ComponentStore_Enemy.cs:2580` |
| A1-14 | `GetEnemyAIAction` | `Core/ComponentStore_Enemy.cs:2600` |
| A1-15 | `GetEnemyAILastAttackTurn` | `Core/ComponentStore_Enemy.cs:2630` |
| A1-16 | `IsWithinAnyPathModifier` | `Core/ComponentStore_Enemy.cs:2715` |
| A1-17 | `GetPathModifierTargetPathId` | `Core/ComponentStore_Enemy.cs:2734` |
| A1-18 | `GetPathModifierIdAt` | `Core/ComponentStore_Enemy.cs:2752` |
| A1-19 | `SetPlayerMaxMana` | `Core/ComponentStore_Player.cs:1033` |
| A1-20 | `GetPlayerManaRegen` | `Core/ComponentStore_Player.cs:1057` |
| A1-21 | `GetWeatherIntensity` | `Core/ComponentStore_Player.cs:1076` |
| A1-22 | `GetWeatherTimer` | `Core/ComponentStore_Player.cs:1088` |
| A1-23 | `GetDayNightCycleCount` | `Core/ComponentStore_Player.cs:1125` |
| A1-24 | `GetPlayerAttackSpeed` | `Core/ComponentStore_Player.cs:643` |
| A1-25 | `GetPlayerTotalGold` | `Core/ComponentStore_Player.cs:700` |
| A1-26 | `HasBuff` | `Core/ComponentStore_Player.cs:754` |
| A1-27 | `IsPlayerStunned` | `Core/ComponentStore_Player.cs:786` |
| A1-28 | `IsPlayerSlowed` | `Core/ComponentStore_Player.cs:793` |
| A1-29 | `GetPlayerShield` | `Core/ComponentStore_Player.cs:838` |
| A1-30 | `SetPlayerBaseLives` | `Core/ComponentStore_Player.cs:890` |
| A1-31 | `GetPlayerMinHealthFloor` | `Core/ComponentStore_Player.cs:910` |
| A1-32 | `SetPlayerMinHealthFloor` | `Core/ComponentStore_Player.cs:916` |
| A1-33 | `GetPlayerReincarnationCharges` | `Core/ComponentStore_Player.cs:951` |
| A1-34 | `GetPlayerHasReincarnated` | `Core/ComponentStore_Player.cs:957` |
| A1-35 | `DeselectAllTowers` | `Core/ComponentStore_Tower.cs:1942` |
| A1-36 | `GetTowerSynergyId` | `Core/ComponentStore_Tower.cs:1972` |
| A1-37 | `SetTowerSynergyId` | `Core/ComponentStore_Tower.cs:1979` |
| A1-38 | `GetTowerBuffShareMask` | `Core/ComponentStore_Tower.cs:2015` |
| A1-39 | `GetTowerTargetingMode` | `Core/ComponentStore_Tower.cs:2030` |
| A1-40 | `GetTowerLinkPartnerId` | `Core/ComponentStore_Tower.cs:2151` |
| A1-41 | `SetTowerAffixSlotCount` | `Core/ComponentStore_Tower.cs:2213` |
| A1-42 | `GetTowerAffixId` | `Core/ComponentStore_Tower.cs:2225` |
| A1-43 | `GetTowerAffixStackCount` | `Core/ComponentStore_Tower.cs:2243` |
| A1-44 | `ClearTowerAffixes` | `Core/ComponentStore_Tower.cs:2266` |
| A1-45 | `GetTowerAffixLockMask` | `Core/ComponentStore_Tower.cs:2283` |
| A1-46 | `SetTowerAffixLockMask` | `Core/ComponentStore_Tower.cs:2290` |
| A1-47 | `SetTowerReforgeCount` | `Core/ComponentStore_Tower.cs:2323` |
| A1-48 | `TickTowerActiveCooldown` | `Core/ComponentStore_Tower.cs:2389` |
| A1-49 | `HasTowerForms` | `Core/ComponentStore_Tower.cs:2516` |
| A1-50 | `TrySetEffect` | `Core/ComponentStore_World.cs:1094` |
| A1-51 | `SetEffectCount` | `Core/ComponentStore_World.cs:1190` |
| A1-52 | `IsWindSourceActive` | `Core/ComponentStore_World.cs:1252` |
| A1-53 | `IsPullSourceActive` | `Core/ComponentStore_World.cs:1337` |
| A1-54 | `GetUnlockedTechs` | `Core/ComponentStore_World.cs:1393` |
| A1-55 | `RemoveHazardZone` | `Core/ComponentStore_World.cs:476` |
| A1-56 | `GetCachedActiveHazardZoneIds` | `Core/ComponentStore_World.cs:493` |
| A1-57 | `ExpireCorpse` | `Core/ComponentStore_World.cs:753` |
| A1-58 | `GetSkillName` | `Core/ComponentStore_World.cs:769` |
| A1-59 | `GetSkillDamageMultiplier` | `Core/ComponentStore_World.cs:781` |
| A1-60 | `SetSkillDamageMultiplier` | `Core/ComponentStore_World.cs:787` |
| A1-61 | `GetSkillAreaWidth` | `Core/ComponentStore_World.cs:793` |
| A1-62 | `SetSkillAreaWidth` | `Core/ComponentStore_World.cs:799` |
| A1-63 | `GetSkillAreaHeight` | `Core/ComponentStore_World.cs:805` |
| A1-64 | `SetSkillAreaHeight` | `Core/ComponentStore_World.cs:811` |
| A1-65 | `GetSkillAttackRange` | `Core/ComponentStore_World.cs:817` |
| A1-66 | `SetSkillAttackRange` | `Core/ComponentStore_World.cs:823` |
| A1-67 | `GetSkillCooldown` | `Core/ComponentStore_World.cs:829` |
| A1-68 | `SetSkillCooldown` | `Core/ComponentStore_World.cs:835` |
| A1-69 | `GetSkillCurrentCooldown` | `Core/ComponentStore_World.cs:841` |
| A1-70 | `SetSkillCurrentCooldown` | `Core/ComponentStore_World.cs:847` |
| A1-71 | `TryCompleteTimedAbility` | `Core/ComponentStore_World.cs:933` |
| A1-72 | `TryCancelTimedAbility` | `Core/ComponentStore_World.cs:943` |
| A1-73 | `TryPrepareTimedAbility` | `Core/ComponentStore_World.cs:953` |
| A1-74 | `LogHotPath` | `Core/FileLogger.cs:13` |
| A1-75 | `ClearLog` | `Core/FileLogger.cs:75` |
| A1-76 | `EffectDeltaFor` | `Core/FrameGraph.cs:66` |
| A1-77 | `ClearFrameBindings` | `Core/FrameScheduler.cs:40` |
| A1-78 | `AddWave` | `Core/FrameSystemGraph.cs:441` |
| A1-79 | `AddWaveAt` | `Core/FrameSystemGraph.cs:442` |
| A1-80 | `MarkDirty` | `Core/GAS/Attributes.cs:165` |
| A1-81 | `ApplyModifier` | `Core/GAS/Attributes.cs:231` |
| A1-82 | `ResetToBase` | `Core/GAS/Attributes.cs:233` |
| A1-83 | `PlayerAttributeName` | `Core/GAS/Attributes.cs:258` |
| A1-84 | `MarkBoundary` | `Core/GAS/DamageResolver.cs:149` |
| A1-85 | `ResetOverflowCount` | `Core/GAS/GameplayCommandBuffer.cs:90` |
| A1-86 | `GetLifeLinkDef` | `Core/GameConfig.cs:1899` |
| A1-87 | `GetLifeLinkDefIdBySourceType` | `Core/GameConfig.cs:1905` |
| A1-88 | `GetHealerDef` | `Core/GameConfig.cs:1924` |
| A1-89 | `GetTowerAffixDef` | `Core/GameConfig.cs:2022` |
| A1-90 | `GetTowerAffixIndex` | `Core/GameConfig.cs:2028` |
| A1-91 | `GetFissionDef` | `Core/GameConfig.cs:2034` |
| A1-92 | `GetFissionDefBySourceType` | `Core/GameConfig.cs:2040` |
| A1-93 | `GetThiefDef` | `Core/GameConfig.cs:2052` |
| A1-94 | `GetCloneDefIdBySourceType` | `Core/GameConfig.cs:2097` |
| A1-95 | `GetPrestigeNode` | `Core/GameConfig.cs:2272` |
| A1-96 | `GetSkillConfig` | `Core/GameConfig.cs:2676` |
| A1-97 | `GetAllMonsterTypes` | `Core/GameConfig.cs:752` |
| A1-98 | `GetTurn` | `Core/GameManager.cs:619` |
| A1-99 | `IsGameRunning` | `Core/GameManager.cs:627` |
| A1-100 | `HasCheckpoint` | `Core/SaveSystem.cs:134` |
| A1-101 | `SaveMetaProgression` | `Core/SaveSystem.cs:143` |
| A1-102 | `LoadMetaProgression` | `Core/SaveSystem.cs:172` |
| A1-103 | `LoadCheckpoint` | `Core/SaveSystem.cs:99` |
| A1-104 | `UpdateEnemies` | `Core/SpatialGrid.cs:75` |
| A1-105 | `RecordDamageTaken` | `Systems/AdaptiveDifficultySystem.cs:82` |
| A1-106 | `GetScoreMultiplier` | `Systems/AscensionSystem.cs:130` |
| A1-107 | `GetGoldEarnedMultiplier` | `Systems/AscensionSystem.cs:135` |
| A1-108 | `GetTowerAttackSpeedMultiplier` | `Systems/AscensionSystem.cs:145` |
| A1-109 | `GetTowerRangePenalty` | `Systems/AscensionSystem.cs:150` |
| A1-110 | `GetEnemyHpMultiplier` | `Systems/AscensionSystem.cs:155` |
| A1-111 | `GetEnemyGoldBonus` | `Systems/AscensionSystem.cs:165` |
| A1-112 | `GetPlayerStartGold` | `Systems/AscensionSystem.cs:170` |
| A1-113 | `GetPlayerStartLives` | `Systems/AscensionSystem.cs:175` |
| A1-114 | `DeselectModifier` | `Systems/AscensionSystem.cs:216` |
| A1-115 | `GetActiveModifierIds` | `Systems/AscensionSystem.cs:234` |
| A1-116 | `GetModifierDescription` | `Systems/AscensionSystem.cs:242` |
| A1-117 | `IsBeamTower` | `Systems/BeamTowerSystem.cs:84` |
| A1-118 | `EvaluateWithEnum` | `Systems/BehaviorTreeEvaluator.cs:117` |
| A1-119 | `GetPathProgress` | `Systems/BossTrailAoeSystem.cs:186` |
| A1-120 | `GetChainKillDamageMultiplier` | `Systems/ComboSystem.cs:134` |
| A1-121 | `GetComboGoldMultiplier` | `Systems/ComboSystem.cs:154` |
| A1-122 | `GetComboCount` | `Systems/ComboSystem.cs:164` |
| A1-123 | `GetComboKillStreak` | `Systems/ComboSystem.cs:173` |
| A1-124 | `ResetAllCombos` | `Systems/ComboSystem.cs:198` |
| A1-125 | `GetEnemyDamageMultiplier` | `Systems/DayNightSystem.cs:127` |
| A1-126 | `GetPhase` | `Systems/DayNightSystem.cs:135` |
| A1-127 | `IsNight` | `Systems/DayNightSystem.cs:143` |
| A1-128 | `ForcePhase` | `Systems/DayNightSystem.cs:177` |
| A1-129 | `TriggerShatter` | `Systems/ElementalReactionSystem.cs:158` |
| A1-130 | `GetMeltDamageBonus` | `Systems/ElementalReactionSystem.cs:178` |
| A1-131 | `InvokeExecuteAction` | `Systems/EnemyAISystem.cs:1024` |
| A1-132 | `GetActiveCount` | `Systems/EnemyProjectileSystem.cs:219` |
| A1-133 | `QueueTeleport` | `Systems/EnemyTeleportSystem.cs:146` |
| A1-134 | `CanTeleportNow` | `Systems/EnemyTeleportSystem.cs:159` |
| A1-135 | `ExecuteTeleport` | `Systems/EnemyTeleportSystem.cs:71` |
| A1-136 | `IsEnemyVisibleToTower` | `Systems/FogOfWarSystem.cs:118` |
| A1-137 | `AwardGoldForWave` | `Systems/GoldSystem.cs:156` |
| A1-138 | `SpendGold` | `Systems/GoldSystem.cs:177` |
| A1-139 | `QueueGroundTargetHeal` | `Systems/HealingZoneSystem.cs:213` |
| A1-140 | `GetOverheatAttackSpeedMultiplier` | `Systems/HeatSystem.cs:120` |
| A1-141 | `GetOverheatDamageMultiplier` | `Systems/HeatSystem.cs:131` |
| A1-142 | `DeployHero` | `Systems/HeroSystem.cs:216` |
| A1-143 | `RecallHero` | `Systems/HeroSystem.cs:231` |
| A1-144 | `SetupHitShield` | `Systems/HitShieldSystem.cs:121` |
| A1-145 | `GetHitsThisFrame` | `Systems/HitTriggerSystem.cs:138` |
| A1-146 | `GetCritsThisFrame` | `Systems/HitTriggerSystem.cs:144` |
| A1-147 | `Deposit` | `Systems/InterestSystem.cs:112` |
| A1-148 | `Withdraw` | `Systems/InterestSystem.cs:133` |
| A1-149 | `SetInterestRate` | `Systems/InterestSystem.cs:150` |
| A1-150 | `GetBankBalance` | `Systems/InterestSystem.cs:158` |
| A1-151 | `GetInterestRate` | `Systems/InterestSystem.cs:166` |
| A1-152 | `GetUsedTotal` | `Systems/InventorySystem.cs:202` |
| A1-153 | `GetActiveZoneCount` | `Systems/MagnetizeSystem.cs:129` |
| A1-154 | `GetCurrentShield` | `Systems/ManaShieldSystem.cs:145` |
| A1-155 | `GetShieldCap` | `Systems/ManaShieldSystem.cs:151` |
| A1-156 | `HasEnoughMana` | `Systems/ManaSystem.cs:104` |
| A1-157 | `ConsumeMana` | `Systems/ManaSystem.cs:115` |
| A1-158 | `GetMaxMana` | `Systems/ManaSystem.cs:142` |
| A1-159 | `GetManaRegen` | `Systems/ManaSystem.cs:150` |
| A1-160 | `ClearReanimatedCount` | `Systems/NecromancerSystem.cs:369` |
| A1-161 | `DamageNest` | `Systems/NestSystem.cs:129` |
| A1-162 | `GetNestHealth` | `Systems/NestSystem.cs:142` |
| A1-163 | `OnMinionKilled` | `Systems/NestSystem.cs:199` |
| A1-164 | `UpdateWavePhase` | `Systems/ObjectiveSystem.cs:157` |
| A1-165 | `DamageEscortNpc` | `Systems/ObjectiveSystem.cs:207` |
| A1-166 | `GetObjectiveStatus` | `Systems/ObjectiveSystem.cs:322` |
| A1-167 | `EvaluateSideQuestsNow` | `Systems/ObjectiveSystem.cs:507` |
| A1-168 | `PlaceObstacle` | `Systems/ObstacleSystem.cs:37` |
| A1-169 | `PlaceBlock` | `Systems/PathBlockSystem.cs:36` |
| A1-170 | `RemoveBlock` | `Systems/PathBlockSystem.cs:58` |
| A1-171 | `IsBlocked` | `Systems/PathBlockSystem.cs:67` |
| A1-172 | `GetCurrentWaypoint` | `Systems/PathfindingSystem.cs:196` |
| A1-173 | `AssignPath` | `Systems/PathfindingSystem.cs:217` |
| A1-174 | `DeactivatePhase` | `Systems/PhaseSystem.cs:105` |
| A1-175 | `IsPhased` | `Systems/PhaseSystem.cs:115` |
| A1-176 | `CanDamagePhased` | `Systems/PhaseSystem.cs:124` |
| A1-177 | `ActivatePhase` | `Systems/PhaseSystem.cs:92` |
| A1-178 | `OnUnitKilled` | `Systems/PlayerSummonSystem.cs:323` |
| A1-179 | `SetEnemyProjectileSystem` | `Systems/PointDefenseSystem.cs:38` |
| A1-180 | `LoadPortals` | `Systems/PortalSystem.cs:32` |
| A1-181 | `CreateTowerPull` | `Systems/PullSystem.cs:188` |
| A1-182 | `CreateGlobalGravityWell` | `Systems/PullSystem.cs:196` |
| A1-183 | `GetGlobalPullEffectiveStrength` | `Systems/PullSystem.cs:212` |
| A1-184 | `ClearPullFlags` | `Systems/PullTowerSystem.cs:111` |
| A1-185 | `SetWaveSpawning` | `Systems/RandomEventSystem.cs:55` |
| A1-186 | `SetPickupSystem` | `Systems/RandomEventSystem.cs:56` |
| A1-187 | `SetInterestSystem` | `Systems/RandomEventSystem.cs:57` |
| A1-188 | `SetGoldSystem` | `Systems/RandomEventSystem.cs:58` |
| A1-189 | `GetReforgeCount` | `Systems/ReforgeSystem.cs:104` |
| A1-190 | `IsSlotLocked` | `Systems/ReforgeSystem.cs:148` |
| A1-191 | `RerollAllUnlocked` | `Systems/ReforgeSystem.cs:218` |
| A1-192 | `GetLockSlotCost` | `Systems/ReforgeSystem.cs:95` |
| A1-193 | `GetMaxRerollsPerTower` | `Systems/ReforgeSystem.cs:98` |
| A1-194 | `BeginRecording` | `Systems/ReplaySystem.cs:82` |
| A1-195 | `OnTowerRemoved` | `Systems/ResourceNodeSystem.cs:176` |
| A1-196 | `GetNodeStatus` | `Systems/ResourceNodeSystem.cs:243` |
| A1-197 | `InjectSummonSystem` | `Systems/SkillSystem.cs:226` |
| A1-198 | `GetAvailableNodes` | `Systems/TechTreeSystem.cs:155` |
| A1-199 | `GetConfig` | `Systems/TechTreeSystem.cs:173` |
| A1-200 | `GetPointsPerWave` | `Systems/TechTreeSystem.cs:178` |
| A1-201 | `GetAttackSpeedMult` | `Systems/TechTreeSystem.cs:287` |
| A1-202 | `GetArmor` | `Systems/TechTreeSystem.cs:292` |
| A1-203 | `GetExperiencePerKill` | `Systems/TechTreeSystem.cs:322` |
| A1-204 | `GetDamageResistance` | `Systems/TechTreeSystem.cs:385` |
| A1-205 | `GetCooldownReduction` | `Systems/TechTreeSystem.cs:406` |
| A1-206 | `GetTowerSlotBonus` | `Systems/TechTreeSystem.cs:411` |
| A1-207 | `GetActiveTotemCount` | `Systems/TotemSystem.cs:154` |
| A1-208 | `SetHeatSystem` | `Systems/TowerAttackSystem.cs:402` |
| A1-209 | `SetEnergySystem` | `Systems/TowerAttackSystem.cs:426` |
| A1-210 | `DamageConstruction` | `Systems/TowerConstructionSystem.cs:84` |
| A1-211 | `HasEnergy` | `Systems/TowerEnergySystem.cs:135` |
| A1-212 | `GetEnergyFraction` | `Systems/TowerEnergySystem.cs:151` |
| A1-213 | `ConsumeEnergy` | `Systems/TowerEnergySystem.cs:41` |
| A1-214 | `GetMasteryDamageMultiplier` | `Systems/TowerExperienceSystem.cs:59` |
| A1-215 | `GetMasteryAttackSpeedBonus` | `Systems/TowerExperienceSystem.cs:72` |
| A1-216 | `GetMasteryRangeBonus` | `Systems/TowerExperienceSystem.cs:85` |
| A1-217 | `GetCooldownRemaining` | `Systems/TowerFormSwitchSystem.cs:107` |
| A1-218 | `CanSwitch` | `Systems/TowerFormSwitchSystem.cs:90` |
| A1-219 | `GetFormCount` | `Systems/TowerFormSwitchSystem.cs:98` |
| A1-220 | `GetTotalIncomePerSecond` | `Systems/TowerIncomeSystem.cs:75` |
| A1-221 | `GetDamageQueue` | `Systems/TowerLinkSystem.cs:382` |
| A1-222 | `MergeTowers` | `Systems/TowerMergeSystem.cs:35` |
| A1-223 | `GetModifierId` | `Systems/TowerModifierSystem.cs:128` |
| A1-224 | `InitializeMorph` | `Systems/TowerMorphSystem.cs:74` |
| A1-225 | `RegisterMorphData` | `Systems/TowerMorphSystem.cs:86` |
| A1-226 | `SetMorphStat` | `Systems/TowerMorphSystem.cs:97` |
| A1-227 | `TryActivateOvercharge` | `Systems/TowerOverchargeSystem.cs:48` |
| A1-228 | `SellSelectedTowers` | `Systems/TowerPlacementSystem.cs:1154` |
| A1-229 | `RepairTower` | `Systems/TowerSabotageSystem.cs:276` |
| A1-230 | `ApplySilence` | `Systems/TowerSilenceSystem.cs:57` |
| A1-231 | `ActivateStealth` | `Systems/TowerStealthSystem.cs:130` |
| A1-232 | `DeclaimTower` | `Systems/TowerStealthSystem.cs:146` |
| A1-233 | `OnTowerAttacked` | `Systems/TowerStealthSystem.cs:74` |
| A1-234 | `SwitchUpgradePath` | `Systems/TowerUpgradeSystem.cs:99` |
| A1-235 | `GetSelectedBranchOption` | `Systems/WaveBranchSystem.cs:128` |
| A1-236 | `GetExplosiveDeathDamage` | `Systems/WaveMutatorSystem.cs:141` |
| A1-237 | `GetExplosiveRadius` | `Systems/WaveMutatorSystem.cs:156` |
| A1-238 | `GetRemainingSkips` | `Systems/WaveSkipSystem.cs:116` |
| A1-239 | `GetCumulativeDamageBonusPct` | `Systems/WaveSkipSystem.cs:124` |
| A1-240 | `GetSkipCost` | `Systems/WaveSkipSystem.cs:130` |
| A1-241 | `SetAdaptiveDifficulty` | `Systems/WaveSpawningSystem.cs:166` |
| A1-242 | `GetCurrentWaveConfig` | `Systems/WaveSpawningSystem.cs:586` |
| A1-243 | `CreateTowerWind` | `Systems/WindSystem.cs:210` |
| A1-244 | `CreateGlobalStorm` | `Systems/WindSystem.cs:218` |
| A1-245 | `GetGlobalWindEffectiveStrength` | `Systems/WindSystem.cs:234` |
| A1-246 | `GetActiveWispCount` | `Systems/WispSystem.cs:115` |
| A1-247 | `GetActiveCCZoneIds` | `Systems/ZoneControlSystem.cs:142` |

### Appendix A2 — Methods referenced only by tests (102)

| # | Symbol | Location (HEAD) |
|---|---|---|
| A2-1 | `ActivateBulletTime` | `Core/ComponentStore_Player.cs:943` |
| A2-2 | `ActivatePathModifier` | `Core/ComponentStore_Enemy.cs:2680` |
| A2-3 | `AddActiveTowerId` | `Core/ComponentStore.cs:261` |
| A2-4 | `AddEffect` | `Core/ComponentStore_World.cs:1059` |
| A2-5 | `AddJunction` | `Systems/PathfindingSystem.cs:275` |
| A2-6 | `ApplyFrostbite` | `Systems/FrostbiteSystem.cs:43` |
| A2-7 | `ApplyLifecycleGold` | `Core/GAS/ResourceResolver.cs:418` |
| A2-8 | `ApplyLifecycleMana` | `Core/GAS/ResourceResolver.cs:443` |
| A2-9 | `ApplyMinHealthFloorInPlace` | `Core/ComponentStore_Enemy.cs:2101` |
| A2-10 | `BindCraftingSystem` | `Systems/InventorySystem.cs:74` |
| A2-11 | `CastChainHealPublic` | `Systems/SkillSystem.cs:1029` |
| A2-12 | `ClampDamageToHealthFloor` | `Core/ComponentStore_Enemy.cs:2079` |
| A2-13 | `ClearBuildQueue` | `Systems/TowerPlacementSystem.cs:924` |
| A2-14 | `ClearCCImmuneBit` | `Core/ComponentStore_Enemy.cs:2511` |
| A2-15 | `ClearDeathMark` | `Systems/DeathMarkSystem.cs:222` |
| A2-16 | `ClearJunctions` | `Systems/PathfindingSystem.cs:286` |
| A2-17 | `ClearLink` | `Systems/EnemyLifeLinkSystem.cs:325` |
| A2-18 | `ClearMark` | `Systems/MarkSystem.cs:179` |
| A2-19 | `ClearSummonCircle` | `Core/ComponentStore_Enemy.cs:2058` |
| A2-20 | `ComputeBonusGold` | `Systems/CullingSystem.cs:262` |
| A2-21 | `DamageNode` | `Systems/ResourceNodeSystem.cs:193` |
| A2-22 | `DecayStacksOnLeave` | `Systems/TerrainZoneSystem.cs:332` |
| A2-23 | `EnableDigests` | `Core/GAS/GameplayObservation.cs:230` |
| A2-24 | `EnqueuePhaseChangeForDiagnostics` | `Systems/EnemyAISystem.cs:1374` |
| A2-25 | `ExpectedArtifactNodeCount` | `Core/FrameAccessReviewCatalog.cs:427` |
| A2-26 | `FindSnapshotSlot` | `Systems/TimeRewindSnapshot.cs:101` |
| A2-27 | `FireWithArc` | `Systems/ProjectileSystem.cs:204` |
| A2-28 | `ForceEvent` | `Systems/RandomEventSystem.cs:373` |
| A2-29 | `ForceWeather` | `Systems/WeatherSystem.cs:241` |
| A2-30 | `GetActiveForm` | `Systems/TowerFormSwitchSystem.cs:81` |
| A2-31 | `GetActiveZones` | `Systems/TelegraphSystem.cs:201` |
| A2-32 | `GetBuildQueueCount` | `Systems/TowerPlacementSystem.cs:940` |
| A2-33 | `GetCachedEnemyCount` | `Systems/PlayerTowerAttackSystem.cs:185` |
| A2-34 | `GetCachedManaRegen` | `Systems/TowerShrineSystem.cs:176` |
| A2-35 | `GetCurrentOptions` | `Systems/PreFightBuffSystem.cs:314` |
| A2-36 | `GetCurrentWeather` | `Core/ComponentStore_Player.cs:1064` |
| A2-37 | `GetCycleScalingMultiplier` | `Systems/DoomClockSystem.cs:122` |
| A2-38 | `GetDamageMultiplier` | `Systems/DeathMarkSystem.cs:201` |
| A2-39 | `GetEffect` | `Core/ComponentStore_World.cs:1031` |
| A2-40 | `GetElementResist` | `Core/ComponentStore_Enemy.cs:1765` |
| A2-41 | `GetEnemyDotPct` | `Systems/WeatherSystem.cs:230` |
| A2-42 | `GetExposureDamageMultiplier` | `Systems/ElementalReactionSystem.cs:324` |
| A2-43 | `GetHeroSkillCooldownMax` | `Systems/HeroSkillSystem.cs:273` |
| A2-44 | `GetHeroSkillId` | `Systems/HeroSkillSystem.cs:255` |
| A2-45 | `GetInventoryUsed` | `Core/ComponentStore_Player.cs:1262` |
| A2-46 | `GetItemName` | `Systems/InventorySystem.cs:194` |
| A2-47 | `GetMinRarity` | `Systems/TowerModifierSystem.cs:67` |
| A2-48 | `GetMineDef` | `Systems/MineSystem.cs:93` |
| A2-49 | `GetModifierRarity` | `Systems/TowerModifierSystem.cs:134` |
| A2-50 | `GetPlayerStacks` | `Systems/CullingSystem.cs:269` |
| A2-51 | `GetRecipe` | `Systems/CraftingSystem.cs:369` |
| A2-52 | `GetRecipeCount` | `Systems/CraftingSystem.cs:367` |
| A2-53 | `GetSoulCap` | `Systems/SoulHarvestSystem.cs:211` |
| A2-54 | `GetStatus` | `Systems/DoomClockSystem.cs:177` |
| A2-55 | `GetTotalEnemiesSpawned` | `Systems/WaveSpawningSystem.cs:291` |
| A2-56 | `GetTowerBuffShareRadius` | `Core/ComponentStore_Tower.cs:2001` |
| A2-57 | `GetTowerEnchantBonus` | `Core/ComponentStore_Tower.cs:2125` |
| A2-58 | `GetTowerEnchantDuration` | `Core/ComponentStore_Tower.cs:2134` |
| A2-59 | `GetTowerFortressNeighborCount` | `Core/ComponentStore_Tower.cs:2450` |
| A2-60 | `GetTowerModifierIndex` | `Core/GameConfig.cs:1998` |
| A2-61 | `HasAnyConfiguredSkill` | `Systems/HeroSkillSystem.cs:282` |
| A2-62 | `HasEnoughSouls` | `Systems/SoulHarvestSystem.cs:217` |
| A2-63 | `HasModifier` | `Systems/TowerModifierSystem.cs:157` |
| A2-64 | `ImportAliases` | `Core/GAS/CatalogCompiler.cs:985` |
| A2-65 | `InventoryIndex` | `Core/ComponentStore_Player.cs:1224` |
| A2-66 | `IsEnemyFrozen` | `Core/ComponentStore_Enemy.cs:2477` |
| A2-67 | `IsFullFired` | `Systems/DeathMarkSystem.cs:237` |
| A2-68 | `IsMarked` | `Systems/DeathMarkSystem.cs:212` |
| A2-69 | `IsThresholdFired` | `Systems/MarkSystem.cs:194` |
| A2-70 | `OnExit` | `Core/GameState.cs:93` |
| A2-71 | `Producer` | `Core/GAS/GameplayIdsAndHandles.cs:216` |
| A2-72 | `QueryEntityTombstone` | `Core/ComponentStore.cs:167` |
| A2-73 | `RebuildSchedule` | `Core/GAS/GameplayRuntime.cs:42` |
| A2-74 | `RecordKill` | `Systems/AdaptiveDifficultySystem.cs:73` |
| A2-75 | `RemoveFrameBinding` | `Core/FrameScheduler.cs:41` |
| A2-76 | `RequestCatalogAbility` | `Systems/SkillSystem.cs:68` |
| A2-77 | `RequestHeroSkill` | `Systems/HeroSkillSystem.cs:171` |
| A2-78 | `RequestTowerActive` | `Systems/TowerActiveSkillSystem.cs:44` |
| A2-79 | `RunCompositionHarness` | `Systems/BenchmarkSystem.cs:168` |
| A2-80 | `RunFixedPopulationSample` | `Systems/BenchmarkSystem.cs:301` |
| A2-81 | `ScheduleListCount` | `Core/GAS/GameplayRuntime.cs:43` |
| A2-82 | `SetCCImmuneMask` | `Core/ComponentStore_Enemy.cs:2497` |
| A2-83 | `SetEffect` | `Core/ComponentStore_World.cs:1039` |
| A2-84 | `SetEnemyHealth` | `Core/ComponentStore_Enemy.cs:1706` |
| A2-85 | `SetTowerActiveOnCooldown` | `Core/ComponentStore_Tower.cs:2381` |
| A2-86 | `SetTowerAntiSummonMultiplier` | `Core/ComponentStore_Tower.cs:2060` |
| A2-87 | `SetTowerBuffShareMask` | `Core/ComponentStore_Tower.cs:2022` |
| A2-88 | `SetTowerBuffShareRadius` | `Core/ComponentStore_Tower.cs:2008` |
| A2-89 | `SetTowerEnchantment` | `Core/ComponentStore_Tower.cs:2094` |
| A2-90 | `SpawnTerrainZone` | `Systems/TerrainZoneSystem.cs:58` |
| A2-91 | `StateEntryCount` | `Systems/BenchmarkSystem.cs:71` |
| A2-92 | `Submit` | `Core/GAS/GameplayCommandBuffer.cs:104` |
| A2-93 | `ToRuntime` | `Core/GAS/LegacyEffectAdapter.cs:69` |
| A2-94 | `TriggerBurrow` | `Systems/EnemyBurrowSystem.cs:175` |
| A2-95 | `TryBeginTimedAbility` | `Core/ComponentStore_World.cs:916` |
| A2-96 | `TryGetFrameNodeContract` | `Core/FrameScheduler.cs:76` |
| A2-97 | `TryGetParent` | `Core/GAS/GameplayTagVocabulary.cs:122` |
| A2-98 | `TryInhibit` | `Core/GAS/GameplayRuntime.cs:498` |
| A2-99 | `TryRestack` | `Core/GAS/GameplayRuntime.cs:276` |
| A2-100 | `TryUninhibit` | `Core/GAS/GameplayRuntime.cs:510` |
| A2-101 | `TryUnlock` | `Systems/TechTreeSystem.cs:133` |
| A2-102 | `WithCause` | `Core/GAS/GameplayIdsAndHandles.cs:184` |

### Appendix B — SOA arrays: write-only (production), never-written, or no indexed access (114)

Writes/reads count indexed accesses in Core, Core/GAS, Systems and Program, excluding whole-array lifecycle statements in ComponentStore*.cs. Lifecycle reset writes are counted as writes. Blind spots: ref locals, Volatile/Interlocked(ref …), Array.Copy/Fill.

| Class | Field | File | Prod writes | Prod reads | Unity refs | Test refs | Writer files | Reader files |
|---|---|---|---|---|---|---|---|---|
| NEVER_WRITTEN | `EnemyCanSabotage` | `ComponentStore_Enemy.cs` | 0 | 2 | 0 | 0 |  | TowerSabotageSystem |
| NEVER_WRITTEN | `EnemyDebuffResistMult` | `ComponentStore_Enemy.cs` | 0 | 1 | 0 | 9 |  | ComponentStore_Enemy |
| NEVER_WRITTEN | `EnemyDisarmResistance` | `ComponentStore_Enemy.cs` | 0 | 2 | 0 | 4 |  | ComponentStore_Enemy |
| NEVER_WRITTEN | `EnemyHasTrueSight` | `ComponentStore_Enemy.cs` | 0 | 2 | 0 | 0 |  | TowerStealthSystem |
| NEVER_WRITTEN | `EnemyHealerHealAmount` | `ComponentStore_Enemy.cs` | 0 | 2 | 0 | 0 |  | EnemyHealerSystem |
| NEVER_WRITTEN | `EnemyHealerHealInterval` | `ComponentStore_Enemy.cs` | 0 | 1 | 0 | 0 |  | EnemyHealerSystem |
| NEVER_WRITTEN | `EnemyIsSuicide` | `ComponentStore_Enemy.cs` | 0 | 1 | 0 | 1 |  | SuicideBombSystem |
| NEVER_WRITTEN | `EnemySabotageCooldown` | `ComponentStore_Enemy.cs` | 0 | 1 | 0 | 0 |  | TowerSabotageSystem |
| NEVER_WRITTEN | `EnemySabotageDuration` | `ComponentStore_Enemy.cs` | 0 | 1 | 0 | 0 |  | TowerSabotageSystem |
| NEVER_WRITTEN | `EnemySabotageRadius` | `ComponentStore_Enemy.cs` | 0 | 1 | 0 | 0 |  | TowerSabotageSystem |
| NEVER_WRITTEN | `EnemySuicideDmgAmount` | `ComponentStore_Enemy.cs` | 0 | 1 | 0 | 1 |  | SuicideBombSystem |
| NEVER_WRITTEN | `EnemySuicideDmgRadius` | `ComponentStore_Enemy.cs` | 0 | 1 | 0 | 1 |  | SuicideBombSystem |
| NEVER_WRITTEN | `EnemySuicideTriggerRange` | `ComponentStore_Enemy.cs` | 0 | 1 | 0 | 1 |  | SuicideBombSystem |
| NEVER_WRITTEN | `EnemyTetherDamageSharePct` | `ComponentStore_Enemy.cs` | 0 | 2 | 0 | 0 |  | TowerAttackSystem |
| NEVER_WRITTEN | `EnemyTetherMaxLength` | `ComponentStore_Enemy.cs` | 0 | 8 | 0 | 0 |  | ComponentStore,EnemyMovementSystem,TowerAttackSystem |
| NEVER_WRITTEN | `EnemyTetherPartnerId` | `ComponentStore_Enemy.cs` | 0 | 2 | 0 | 0 |  | EnemyMovementSystem,TowerAttackSystem |
| NEVER_WRITTEN | `EnemyTetherStunSharePct` | `ComponentStore_Enemy.cs` | 0 | 1 | 0 | 0 |  | TowerAttackSystem |
| NEVER_WRITTEN | `EnemyTrampleDamagePerStep` | `ComponentStore_Enemy.cs` | 0 | 2 | 0 | 1 |  | ComponentStore,EnemyMovementSystem |
| NEVER_WRITTEN | `EnemyTrampleRadius` | `ComponentStore_Enemy.cs` | 0 | 4 | 0 | 1 |  | ComponentStore,EnemyMovementSystem |
| NEVER_WRITTEN | `EnemyWoundSlowRatio` | `ComponentStore_Enemy.cs` | 0 | 1 | 0 | 1 |  | EnemyWoundSystem |
| NEVER_WRITTEN | `EnemyWoundThreshold` | `ComponentStore_Enemy.cs` | 0 | 1 | 0 | 1 |  | EnemyWoundSystem |
| NEVER_WRITTEN | `HeroAttackRange` | `ComponentStore_Player.cs` | 0 | 2 | 0 | 1 |  | HeroSystem |
| NEVER_WRITTEN | `HeroAttackSpeed` | `ComponentStore_Player.cs` | 0 | 2 | 0 | 1 |  | HeroSystem |
| NEVER_WRITTEN | `HeroDamage` | `ComponentStore_Player.cs` | 0 | 2 | 0 | 1 |  | HeroSystem |
| NEVER_WRITTEN | `HeroMoveSpeed` | `ComponentStore_Player.cs` | 0 | 2 | 0 | 0 |  | HeroSystem |
| NEVER_WRITTEN | `PlayerBreatherGoldBonus` | `ComponentStore_Player.cs` | 0 | 2 | 0 | 0 |  | GoldSystem |
| NEVER_WRITTEN | `PlayerCooldownReduceOnBreather` | `ComponentStore_Player.cs` | 0 | 2 | 0 | 0 |  | GoldSystem |
| NEVER_WRITTEN | `PlayerHealOnBreatherWave` | `ComponentStore_Player.cs` | 0 | 3 | 0 | 0 |  | GoldSystem |
| NEVER_WRITTEN | `PlayerWavePreviewLevel` | `ComponentStore_Player.cs` | 0 | 2 | 0 | 0 |  | WavePreviewSystem |
| NEVER_WRITTEN | `TowerDamageVariance` | `ComponentStore_Tower.cs` | 0 | 1 | 0 | 5 |  | TowerAttackSystem |
| NEVER_WRITTEN | `TowerFearChance` | `ComponentStore_Tower.cs` | 0 | 1 | 0 | 0 |  | FearSystem |
| NEVER_WRITTEN | `TowerFearDuration` | `ComponentStore_Tower.cs` | 0 | 2 | 0 | 0 |  | FearSystem |
| NEVER_WRITTEN | `TowerFearRadius` | `ComponentStore_Tower.cs` | 0 | 4 | 0 | 0 |  | FearSystem |
| NONE | `EnemyHealerHealTargetPriority` | `ComponentStore_Enemy.cs` | 0 | 0 | 0 | 0 |  |  |
| NONE | `EnemyTrampleKnockback` | `ComponentStore_Enemy.cs` | 0 | 0 | 0 | 0 |  |  |
| NONE | `HeroTypeId` | `ComponentStore_Player.cs` | 0 | 0 | 0 | 0 |  |  |
| NONE | `PlayerGlobalSkillHotkey` | `ComponentStore_Player.cs` | 0 | 0 | 0 | 0 |  |  |
| NONE | `TowerCooldownReduction` | `ComponentStore_Tower.cs` | 0 | 0 | 0 | 0 |  |  |
| NONE | `TowerWasStealthedLastFrame` | `ComponentStore_Tower.cs` | 0 | 0 | 0 | 0 |  |  |
| WRITE_ONLY | `AdaptiveDifficultyScore` | `ComponentStore_World.cs` | 3 | 0 | 0 | 0 | ComponentStore,AdaptiveDifficultySystem |  |
| WRITE_ONLY | `CorpseEffectDamageType` | `ComponentStore_World.cs` | 2 | 0 | 0 | 1 | ComponentStore_World |  |
| WRITE_ONLY | `EnemyBanishOriginalX` | `ComponentStore_Enemy.cs` | 2 | 0 | 0 | 0 | ComponentStore,ComponentStore_Enemy |  |
| WRITE_ONLY | `EnemyBanishOriginalY` | `ComponentStore_Enemy.cs` | 2 | 0 | 0 | 0 | ComponentStore,ComponentStore_Enemy |  |
| WRITE_ONLY | `EnemyBuffDamageBonus` | `ComponentStore_Enemy.cs` | 1 | 0 | 0 | 2 | ComponentStore |  |
| WRITE_ONLY | `EnemyBuffDurationLeft` | `ComponentStore_Enemy.cs` | 1 | 0 | 0 | 0 | ComponentStore |  |
| WRITE_ONLY | `EnemyBurrowSpeedMult` | `ComponentStore_Enemy.cs` | 2 | 0 | 0 | 0 | ComponentStore,WaveSpawningSystem |  |
| WRITE_ONLY | `EnemyCanLand` | `ComponentStore_Enemy.cs` | 3 | 0 | 0 | 0 | ComponentStore,WaveSpawningSystem |  |
| WRITE_ONLY | `EnemyCharmDurationLeft` | `ComponentStore_Enemy.cs` | 1 | 0 | 0 | 0 | ComponentStore |  |
| WRITE_ONLY | `EnemyCloneTimer` | `ComponentStore_Enemy.cs` | 1 | 0 | 0 | 0 | EnemyCloneSystem |  |
| WRITE_ONLY | `EnemyCrestRegenPerSec` | `ComponentStore_Enemy.cs` | 3 | 0 | 0 | 15 | ComponentStore_Enemy,CrestSystem |  |
| WRITE_ONLY | `EnemyCurseDmgReduction` | `ComponentStore_Enemy.cs` | 3 | 0 | 0 | 0 | ComponentStore,CurseAuraSystem |  |
| WRITE_ONLY | `EnemyCurseDmgTakenIncrease` | `ComponentStore_Enemy.cs` | 3 | 0 | 0 | 0 | ComponentStore,CurseAuraSystem |  |
| WRITE_ONLY | `EnemyDecoyLifetime` | `ComponentStore_Enemy.cs` | 2 | 0 | 0 | 0 | ComponentStore,ComponentStore_Enemy |  |
| WRITE_ONLY | `EnemyFlightHeight` | `ComponentStore_Enemy.cs` | 3 | 0 | 0 | 0 | ComponentStore,WaveSpawningSystem |  |
| WRITE_ONLY | `EnemyInvulnerablePhaseName` | `ComponentStore_Enemy.cs` | 1 | 0 | 0 | 0 | ComponentStore |  |
| WRITE_ONLY | `EnemyIsEnraged` | `ComponentStore_Enemy.cs` | 3 | 0 | 0 | 0 | ComponentStore,EnemyAISystem |  |
| WRITE_ONLY | `EnemyIsSiege` | `ComponentStore_Enemy.cs` | 3 | 0 | 0 | 5 | ComponentStore,ComponentStore_Enemy |  |
| WRITE_ONLY | `EnemyLeashReturnX` | `ComponentStore_Enemy.cs` | 2 | 0 | 0 | 0 | ComponentStore_Enemy,EnemyMovementSystem |  |
| WRITE_ONLY | `EnemyLeashReturnY` | `ComponentStore_Enemy.cs` | 2 | 0 | 0 | 0 | ComponentStore_Enemy,EnemyMovementSystem |  |
| WRITE_ONLY | `EnemyManaBurnCooldown` | `ComponentStore_Enemy.cs` | 1 | 0 | 0 | 0 | ComponentStore_Enemy |  |
| WRITE_ONLY | `EnemyManaBurnRadius` | `ComponentStore_Enemy.cs` | 1 | 0 | 0 | 0 | ComponentStore_Enemy |  |
| WRITE_ONLY | `EnemyMorphTriggered` | `ComponentStore_Enemy.cs` | 3 | 0 | 0 | 0 | EnemyMorphSystem,WaveSpawningSystem |  |
| WRITE_ONLY | `EnemyPathTerrainDmgMult` | `ComponentStore_Enemy.cs` | 2 | 0 | 0 | 0 | ComponentStore_Enemy,EnemyMovementSystem |  |
| WRITE_ONLY | `EnemyPhaseDuration` | `ComponentStore_Enemy.cs` | 2 | 0 | 0 | 0 | ComponentStore,PhaseSystem |  |
| WRITE_ONLY | `EnemySapperTargetTowerId` | `ComponentStore_Enemy.cs` | 3 | 0 | 0 | 0 | ComponentStore,ComponentStore_Enemy |  |
| WRITE_ONLY | `EnemyStackCount` | `ComponentStore_Enemy.cs` | 2 | 0 | 0 | 2 | ComponentStore_Enemy,EnemyMovementSystem |  |
| WRITE_ONLY | `EnemyStolenGold` | `ComponentStore_Enemy.cs` | 2 | 0 | 0 | 0 | ComponentStore_Enemy,EnemyStealGoldSystem |  |
| WRITE_ONLY | `EnemyTauntTargetId` | `ComponentStore_Enemy.cs` | 1 | 0 | 0 | 0 | ComponentStore |  |
| WRITE_ONLY | `EnemyTerrainZoneDpsTotal` | `ComponentStore_Enemy.cs` | 4 | 0 | 0 | 3 | ComponentStore,TerrainZoneSystem |  |
| WRITE_ONLY | `EnemyVanguardCoverCount` | `ComponentStore_Enemy.cs` | 1 | 0 | 0 | 0 | ComponentStore_Enemy |  |
| WRITE_ONLY | `EscortNpcX` | `ComponentStore_World.cs` | 2 | 0 | 0 | 0 | ObjectiveSystem |  |
| WRITE_ONLY | `EscortNpcY` | `ComponentStore_World.cs` | 1 | 0 | 0 | 0 | ObjectiveSystem |  |
| WRITE_ONLY | `HazardZoneMaxRadius` | `ComponentStore_World.cs` | 3 | 0 | 0 | 0 | ComponentStore_World,ZoneControlSystem |  |
| WRITE_ONLY | `HazardZoneOwnerTowerId` | `ComponentStore_World.cs` | 3 | 0 | 0 | 0 | ComponentStore_World,ZoneControlSystem |  |
| WRITE_ONLY | `NestMaxAlive` | `ComponentStore_Enemy.cs` | 2 | 0 | 0 | 0 | ComponentStore,NestSystem |  |
| WRITE_ONLY | `NestMaxHealth` | `ComponentStore_Enemy.cs` | 2 | 0 | 0 | 0 | ComponentStore,NestSystem |  |
| WRITE_ONLY | `ObjectiveHealthScore` | `ComponentStore_World.cs` | 1 | 0 | 0 | 0 | ObjectiveSystem |  |
| WRITE_ONLY | `ObstacleMaxHealth` | `ComponentStore_World.cs` | 2 | 0 | 0 | 0 | ComponentStore_World |  |
| WRITE_ONLY | `PathModifierOwnerId` | `ComponentStore_Enemy.cs` | 1 | 0 | 0 | 0 | ComponentStore_Enemy |  |
| WRITE_ONLY | `PlayerCrestActiveId` | `ComponentStore_Player.cs` | 3 | 0 | 0 | 10 | ComponentStore_Player,CrestSystem |  |
| WRITE_ONLY | `PlayerIsWaveActive` | `ComponentStore_World.cs` | 1 | 0 | 0 | 1 | ComponentStore |  |
| WRITE_ONLY | `PlayerMaxBaseLives` | `ComponentStore_Player.cs` | 1 | 0 | 0 | 0 | ComponentStore_Player |  |
| WRITE_ONLY | `PlayerMomentumCurrentTier` | `ComponentStore_Player.cs` | 3 | 0 | 0 | 0 | ComponentStore_Player,MomentumSystem |  |
| WRITE_ONLY | `PlayerPreFightMaxHpMult` | `ComponentStore_Player.cs` | 4 | 0 | 0 | 5 | ComponentStore_Player,PreFightBuffSystem |  |
| WRITE_ONLY | `PlayerThornsRatio` | `ComponentStore_Player.cs` | 1 | 0 | 0 | 0 | ComponentStore |  |
| WRITE_ONLY | `PlayerWaveTimer` | `ComponentStore_World.cs` | 1 | 0 | 0 | 0 | ComponentStore |  |
| WRITE_ONLY | `PullSourceOwnerPlayer` | `ComponentStore_World.cs` | 2 | 0 | 0 | 0 | ComponentStore_World |  |
| WRITE_ONLY | `PullSourceTowerId` | `ComponentStore_World.cs` | 2 | 0 | 0 | 0 | ComponentStore_World |  |
| WRITE_ONLY | `SummonedUnitGoldReward` | `ComponentStore_Enemy.cs` | 1 | 0 | 0 | 0 | PlayerSummonSystem |  |
| WRITE_ONLY | `TerrainZoneId` | `ComponentStore_World.cs` | 2 | 0 | 0 | 1 | ComponentStore_World |  |
| WRITE_ONLY | `TerrainZoneTotalLifetime` | `ComponentStore_World.cs` | 2 | 0 | 0 | 0 | ComponentStore_World |  |
| WRITE_ONLY | `TowerBleedMaxStacks` | `ComponentStore_Tower.cs` | 3 | 0 | 0 | 0 | ComponentStore_Tower,TowerPlacementSystem |  |
| WRITE_ONLY | `TowerBleedTickInterval` | `ComponentStore_Tower.cs` | 3 | 0 | 0 | 0 | ComponentStore_Tower,TowerPlacementSystem |  |
| WRITE_ONLY | `TowerBurstTimer` | `ComponentStore_Tower.cs` | 3 | 0 | 0 | 0 | ComponentStore_Tower,TowerPlacementSystem |  |
| WRITE_ONLY | `TowerDeathMarkChance` | `ComponentStore_Tower.cs` | 3 | 0 | 0 | 0 | ComponentStore_Tower,TowerPlacementSystem |  |
| WRITE_ONLY | `TowerDemolishStunDuration` | `ComponentStore_Tower.cs` | 1 | 0 | 0 | 0 | TowerPlacementSystem |  |
| WRITE_ONLY | `TowerIsDeathMarkTower` | `ComponentStore_Tower.cs` | 3 | 0 | 0 | 0 | ComponentStore_Tower,TowerPlacementSystem |  |
| WRITE_ONLY | `TowerIsReloading` | `ComponentStore_Tower.cs` | 5 | 0 | 0 | 0 | ComponentStore,ComponentStore_Tower,TowerAttackSystem,TowerPlacementSystem |  |
| WRITE_ONLY | `TowerKillCount` | `ComponentStore_Tower.cs` | 1 | 0 | 2 | 0 | TowerExperienceSystem |  |
| WRITE_ONLY | `TowerKnockbackRadius` | `ComponentStore_Tower.cs` | 1 | 0 | 0 | 0 | ComponentStore_Tower |  |
| WRITE_ONLY | `TowerLinkComboType` | `ComponentStore_Tower.cs` | 2 | 0 | 0 | 0 | TowerLinkSystem |  |
| WRITE_ONLY | `TowerPierceHitsRemaining` | `ComponentStore_Tower.cs` | 1 | 0 | 0 | 0 | ComponentStore_Tower |  |
| WRITE_ONLY | `TowerProjectileArcPeakHeight` | `ComponentStore_Tower.cs` | 2 | 0 | 0 | 0 | ComponentStore_Tower |  |
| WRITE_ONLY | `TowerProjectileArcType` | `ComponentStore_Tower.cs` | 2 | 0 | 0 | 0 | ComponentStore_Tower |  |
| WRITE_ONLY | `TowerProjectileGravityScale` | `ComponentStore_Tower.cs` | 2 | 0 | 0 | 0 | ComponentStore_Tower |  |
| WRITE_ONLY | `TowerReloadProgress` | `ComponentStore_Tower.cs` | 4 | 0 | 0 | 0 | ComponentStore,ComponentStore_Tower,TowerAttackSystem |  |
| WRITE_ONLY | `TowerSilenceSourceId` | `ComponentStore_Tower.cs` | 4 | 0 | 0 | 0 | ComponentStore_Tower,EnemyAbilitySystem,TowerSilenceSystem |  |
| WRITE_ONLY | `TowerSpecialAbilityDamageMult` | `ComponentStore_Tower.cs` | 1 | 0 | 0 | 0 | TowerPlacementSystem |  |
| WRITE_ONLY | `TowerSpecialAbilityDotDamage` | `ComponentStore_Tower.cs` | 1 | 0 | 0 | 0 | TowerPlacementSystem |  |
| WRITE_ONLY | `TowerSpecialAbilityDotInterval` | `ComponentStore_Tower.cs` | 1 | 0 | 0 | 0 | TowerPlacementSystem |  |
| WRITE_ONLY | `TowerSpecialAbilityRadius` | `ComponentStore_Tower.cs` | 1 | 0 | 0 | 0 | TowerPlacementSystem |  |
| WRITE_ONLY | `TowerStealthDuration` | `ComponentStore_Tower.cs` | 1 | 0 | 0 | 0 | TowerStealthSystem |  |
| WRITE_ONLY | `WindSourceOwnerPlayer` | `ComponentStore_World.cs` | 2 | 0 | 0 | 0 | ComponentStore_World |  |
| WRITE_ONLY | `WindSourceTowerId` | `ComponentStore_World.cs` | 2 | 0 | 0 | 0 | ComponentStore_World |  |

### Appendix C — Config properties never read outside their declaration (43)

Strict parsing uses JsonUnmappedMemberHandling.Disallow: rows with JSON files > 0 are must-retain (R-04).

| Property | Declared | Loader mentions | JSON files with key | Test files |
|---|---|---|---|---|
| `TowerFormDef.BounceCount` | `Core/GameConfig.cs:673` | 0 | 2 | 0 |
| `TowerFormDef.ElementTag` | `Core/GameConfig.cs:681` | 0 | 2 | 0 |
| `WaveConfig.AppliedBranchOption` | `Core/GameConfig.cs:833` | 0 | 0 | 0 |
| `LevelConfig.DoomClockWaveScaling` | `Core/GameConfig.cs:893` | 1 | 1 | 1 |
| `EnemyAbilityDef.CooldownRemaining` | `Core/GameConfig.cs:958` | 3 | 1 | 0 |
| `EnemyAbilityDef.BuffStat` | `Core/GameConfig.cs:962` | 3 | 1 | 0 |
| `BossPhaseDef.NewBehaviorTree` | `Core/GameConfig.cs:1019` | 0 | 0 | 1 |
| `PathModifierDef.TargetPathId` | `Core/GameConfig.cs:1156` | 0 | 0 | 0 |
| `MorphDef.MorphId` | `Core/GameConfig.cs:1170` | 1 | 0 | 0 |
| `JunctionDef.BossTypeTags` | `Core/GameConfig.cs:1214` | 0 | 1 | 1 |
| `HealerDef.HealInterval` | `Core/GameConfig.cs:1270` | 0 | 0 | 0 |
| `HealerDef.HealRange` | `Core/GameConfig.cs:1272` | 0 | 0 | 0 |
| `DispelEnemyDef.ImmunityDuration` | `Core/GameConfig.cs:1304` | 0 | 0 | 0 |
| `ObstacleDef.CanBeAttacked` | `Core/GameConfig.cs:1320` | 0 | 0 | 0 |
| `PhaseBehaviorDef.AutoAdvance` | `Core/GameConfig.cs:1513` | 1 | 0 | 0 |
| `PhaseBehaviorDef.UnlockTowers` | `Core/GameConfig.cs:1514` | 2 | 0 | 0 |
| `PhaseBehaviorDef.UnlockAbilities` | `Core/GameConfig.cs:1515` | 3 | 0 | 0 |
| `PhaseBehaviorDef.IntermissionDelayMs` | `Core/GameConfig.cs:1516` | 2 | 0 | 0 |
| `PhaseBehaviorDef.TurnIntervalMs` | `Core/GameConfig.cs:1518` | 2 | 0 | 0 |
| `PhaseBehaviorDef.NextWaveMessage` | `Core/GameConfig.cs:1519` | 1 | 0 | 0 |
| `PhaseBehaviorDef.AutoAdvanceToBuild` | `Core/GameConfig.cs:1520` | 1 | 0 | 0 |
| `PhaseBehaviorDef.AdvanceDelayMs` | `Core/GameConfig.cs:1521` | 1 | 0 | 0 |
| `PhaseBehaviorDef.ShowStats` | `Core/GameConfig.cs:1522` | 1 | 0 | 0 |
| `AutoSkillConfig.MinCooldownToConsider` | `Core/GameConfig.cs:1604` | 0 | 1 | 0 |
| `TowerModifierDef.TowerEffectivenessEntryCount` | `Core/GameConfig.cs:2019` | 1 | 0 | 1 |
| `CloneDef.CloneId` | `Core/GameConfig.cs:2073` | 0 | 0 | 0 |
| `TerrainZoneDef.PathModifiers` | `Core/GameConfig.cs:2230` | 0 | 0 | 0 |
| `TerrainZoneDef.DailyModifierPool` | `Core/GameConfig.cs:2238` | 4 | 0 | 1 |
| `TerrainZoneDef.DailyModifierCount` | `Core/GameConfig.cs:2241` | 2 | 0 | 1 |
| `TerrainZoneDef.MetaBonusStartingGold` | `Core/GameConfig.cs:2267` | 0 | 0 | 0 |
| `GlobalSkillDef.GoldMultiplier` | `Core/GameConfig.cs:3326` | 0 | 0 | 0 |
| `WeatherConfig.GlobalEnemySpeedMult` | `Core/GameConfig.cs:3345` | 3 | 0 | 0 |
| `WeatherConfig.GlobalTowerRangeMult` | `Core/GameConfig.cs:3346` | 3 | 0 | 0 |
| `WeatherConfig.GlobalTowerDamageMult` | `Core/GameConfig.cs:3347` | 3 | 0 | 0 |
| `DayNightConfig.SentinelNightRangeBonus` | `Core/GameConfig.cs:3397` | 0 | 0 | 0 |
| `DayNightConfig.RepeatCycles` | `Core/GameConfig.cs:3400` | 0 | 0 | 0 |
| `TerrainTypeConfig.TowerRangeBonus` | `Core/GameConfig.cs:3411` | 1 | 0 | 0 |
| `TowerOverchargeConfig.RangeMultiplier` | `Core/GameConfig.cs:3425` | 1 | 1 | 1 |
| `PickupDef.Color` | `Core/GameConfig.cs:3465` | 1 | 1 | 0 |
| `PickupDef.Fx` | `Core/GameConfig.cs:3466` | 1 | 1 | 0 |
| `ResourceNodeDef.CaptureRadius` | `Core/GameConfig.cs:3580` | 0 | 1 | 0 |
| `RandomEventDef.DifficultyMult` | `Core/GameConfig.cs:3736` | 1 | 0 | 0 |
| `RandomEventConfig.MinEventGap` | `Core/GameConfig.cs:3763` | 1 | 0 | 0 |

### Appendix E — Enum members never referenced by name (33)

CorpseEffectTypeEnum members are integer codes documented by an enum that is itself unreferenced; DamageImmunityFlags and BuffType are must-retain (R-08).

| Enum member | Declared | JSON hits |
|---|---|---|
| `AttributeUnit.Key` | `Core/GAS/Attributes.cs:30` | 0 |
| `AttributeUnit.Minimum` | `Core/GAS/Attributes.cs:31` | 0 |
| `AttributeUnit.PercentFloor` | `Core/GAS/Attributes.cs:32` | 0 |
| `BuffType.Slow` | `Core/BuffType.cs:13` | 0 |
| `BuffType.Stun` | `Core/BuffType.cs:12` | 6 |
| `CorpseEffectTypeEnum.BlightedGround` | `Core/GameConfig.cs:3812` | 0 |
| `CorpseEffectTypeEnum.DamageBoost` | `Core/GameConfig.cs:3800` | 2 |
| `CorpseEffectTypeEnum.Fire` | `Core/GameConfig.cs:3798` | 13 |
| `CorpseEffectTypeEnum.HallowedGround` | `Core/GameConfig.cs:3803` | 0 |
| `CorpseEffectTypeEnum.Healing` | `Core/GameConfig.cs:3799` | 0 |
| `CorpseEffectTypeEnum.Ice` | `Core/GameConfig.cs:3797` | 2 |
| `CorpseEffectTypeEnum.Poison` | `Core/GameConfig.cs:3795` | 2 |
| `CorpseEffectTypeEnum.Slow` | `Core/GameConfig.cs:3796` | 0 |
| `CorpseEffectTypeEnum.ThornyBramble` | `Core/GameConfig.cs:3805` | 0 |
| `DamageImmunityFlags.Fire` | `Core/DamageType.cs:54` | 13 |
| `DamageImmunityFlags.Ice` | `Core/DamageType.cs:55` | 2 |
| `DamageImmunityFlags.Lightning` | `Core/DamageType.cs:56` | 2 |
| `DamageImmunityFlags.Magic` | `Core/DamageType.cs:53` | 1 |
| `DamageImmunityFlags.None` | `Core/DamageType.cs:51` | 1 |
| `DamageImmunityFlags.Physical` | `Core/DamageType.cs:52` | 1 |
| `ElementalReactionType.Melt` | `Core/ElementType.cs:31` | 0 |
| `EnemyAbilityKind.Unknown` | `Core/GAS/EnemyAbilityTypeRegistry.cs:8` | 0 |
| `EnemyActionType.Banished` | `Core/EnemyActionType.cs:33` | 0 |
| `EnemyActionType.Clone` | `Core/EnemyActionType.cs:32` | 11 |
| `EnemyActionType.EstablishLink` | `Core/EnemyActionType.cs:31` | 0 |
| `EnemyActionType.Land` | `Core/EnemyActionType.cs:27` | 0 |
| `EnemyActionType.Resurrect` | `Core/EnemyActionType.cs:30` | 0 |
| `EnemyActionType.Staggered` | `Core/EnemyActionType.cs:34` | 0 |
| `EnemyActionType.Tethered` | `Core/EnemyActionType.cs:35` | 0 |
| `FrameAccessReviewDisposition.JustifiedException` | `Core/FrameAccessProfile.cs:50` | 0 |
| `TargetMagnitudeMode.ForbidEffects` | `Core/GAS/GameplayAbilityRuntime.cs:199` | 0 |
| `TargetMagnitudeMode.RequireEnemy` | `Core/GAS/GameplayAbilityRuntime.cs:197` | 0 |
| `TargetMagnitudeMode.RequireHealExecutions` | `Core/GAS/GameplayAbilityRuntime.cs:198` | 0 |

### Appendix B2 — RESET_ONLY SOA arrays (V-08; 29) — all removed 2026-10-27 (B2); kept: none

| Array | Location (working tree) | Type[len] | Est. bytes | Test files | Writers |
|---|---|---|---|---|---|
| `CorpseEffectDamageType` | `Core/ComponentStore_World.cs:259` | `int[MAX_CORPSE_EFFECTS]` | named-constant length | 1 | ComponentStore_World.cs |
| `EnemyBanishOriginalX` | `Core/ComponentStore_Enemy.cs:1214` | `float[MAX_ENTITIES]` | 400000 | 0 | ComponentStore.cs,ComponentStore_Enemy.cs |
| `EnemyBanishOriginalY` | `Core/ComponentStore_Enemy.cs:1215` | `float[MAX_ENTITIES]` | 400000 | 0 | ComponentStore.cs,ComponentStore_Enemy.cs |
| `EnemyBuffDamageBonus` | `Core/ComponentStore_Enemy.cs:48` | `float[MAX_ENTITIES]` | 400000 | 1 | ComponentStore.cs |
| `EnemyBuffDurationLeft` | `Core/ComponentStore_Enemy.cs:50` | `float[MAX_ENTITIES]` | 400000 | 0 | ComponentStore.cs |
| `EnemyCharmDurationLeft` | `Core/ComponentStore_Enemy.cs:744` | `float[MAX_ENTITIES]` | 400000 | 0 | ComponentStore.cs |
| `EnemyDecoyLifetime` | `Core/ComponentStore_Enemy.cs:1145` | `float[MAX_ENTITIES]` | 400000 | 0 | ComponentStore.cs,ComponentStore_Enemy.cs |
| `EnemyInvulnerablePhaseName` | `Core/ComponentStore_Enemy.cs:647` | `string[MAX_ENTITIES]` | 800000 | 0 | ComponentStore.cs |
| `EnemyIsSiege` | `Core/ComponentStore_Enemy.cs:149` | `bool[MAX_ENTITIES]` | 100000 | 1 | ComponentStore.cs,ComponentStore_Enemy.cs |
| `EnemyManaBurnCooldown` | `Core/ComponentStore_Enemy.cs:905` | `float[MAX_ENTITIES]` | 400000 | 0 | ComponentStore_Enemy.cs |
| `EnemyManaBurnRadius` | `Core/ComponentStore_Enemy.cs:903` | `float[MAX_ENTITIES]` | 400000 | 0 | ComponentStore_Enemy.cs |
| `EnemySapperTargetTowerId` | `Core/ComponentStore_Enemy.cs:415` | `int[MAX_ENTITIES]` | 400000 | 0 | ComponentStore.cs,ComponentStore_Enemy.cs |
| `EnemyTauntTargetId` | `Core/ComponentStore_Enemy.cs:742` | `int[MAX_ENTITIES]` | 400000 | 0 | ComponentStore.cs |
| `EnemyVanguardCoverCount` | `Core/ComponentStore_Enemy.cs:877` | `int[MAX_ENTITIES]` | 400000 | 0 | ComponentStore_Enemy.cs |
| `ObstacleMaxHealth` | `Core/ComponentStore_World.cs:207` | `float[MAX_OBSTACLES]` | named-constant length | 0 | ComponentStore_World.cs |
| `PathModifierOwnerId` | `Core/ComponentStore_Enemy.cs:23` | `int[MAX_ENTITIES]` | 400000 | 0 | ComponentStore_Enemy.cs |
| `PlayerIsWaveActive` | `Core/ComponentStore_World.cs:193` | `bool[MAX_PLAYERS]` | named-constant length | 1 | ComponentStore.cs |
| `PlayerMaxBaseLives` | `Core/ComponentStore_Player.cs:253` | `int[MAX_PLAYERS]` | named-constant length | 0 | ComponentStore_Player.cs |
| `PlayerThornsRatio` | `Core/ComponentStore_Player.cs:42` | `float[MAX_PLAYERS]` | named-constant length | 0 | ComponentStore.cs |
| `PlayerWaveTimer` | `Core/ComponentStore_World.cs:195` | `float[MAX_PLAYERS]` | named-constant length | 0 | ComponentStore.cs |
| `PullSourceOwnerPlayer` | `Core/ComponentStore_World.cs:160` | `int[MAX_PULL_SOURCES]` | named-constant length | 0 | ComponentStore_World.cs |
| `PullSourceTowerId` | `Core/ComponentStore_World.cs:161` | `int[MAX_PULL_SOURCES]` | named-constant length | 0 | ComponentStore_World.cs |
| `TerrainZoneId` | `Core/ComponentStore_World.cs:290` | `string[MAX_TERRAIN_ZONES]` | named-constant length | 1 | ComponentStore_World.cs |
| `TerrainZoneTotalLifetime` | `Core/ComponentStore_World.cs:281` | `float[MAX_TERRAIN_ZONES]` | named-constant length | 0 | ComponentStore_World.cs |
| `TowerKnockbackRadius` | `Core/ComponentStore_Tower.cs:348` | `float[MAX_ENTITIES]` | 400000 | 0 | ComponentStore_Tower.cs |
| `TowerPierceHitsRemaining` | `Core/ComponentStore_Tower.cs:407` | `int[MAX_ENTITIES]` | 400000 | 0 | ComponentStore_Tower.cs |
| `TowerProjectileArcPeakHeight` | `Core/ComponentStore_Tower.cs:897` | `float[MAX_ENTITIES]` | 400000 | 0 | ComponentStore_Tower.cs |
| `TowerProjectileArcType` | `Core/ComponentStore_Tower.cs:894` | `int[MAX_ENTITIES]` | 400000 | 0 | ComponentStore_Tower.cs |
| `TowerProjectileGravityScale` | `Core/ComponentStore_Tower.cs:899` | `float[MAX_ENTITIES]` | 400000 | 0 | ComponentStore_Tower.cs |
