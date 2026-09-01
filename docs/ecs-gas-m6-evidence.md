# Ability and Content Catalog Evidence

The production bootstrap now uses `GameConfigLoader.LoadStrictCatalog`, which
compiles and validates the canonical typed catalog before system construction.
The runtime combo rule derives its effect and trigger IDs from the compiled
catalog capacity and reads its threshold, stack cap, and modifier magnitude from
`ComboConfig`. Its modifier targets the player `AttackDamage` projection, which
is the attribute consumed by the player tower attack path. Computed attributes
are requested during registry construction and become visible at the scheduler
frame boundary.

The ownership boundary is explicit: typed `GasRuntime` owns compiled
definitions, active effects, trigger counters, ability-slot cooldown commits,
and typed damage/resource requests. Skill, Global, Hero, Tower active, Enemy,
and Auto entry points all cross the shared activation seam. `AutoSkillSystem`
is a facade over `SkillSystem`; it has no independent cooldown/effect rules.
The old `AreaShape` switch remains only as a stateless compatibility projection;
new activation dispatch uses the compiled Catalog and shape handler registry.
Global and Tower active systems now reject missing Catalogs in strict bootstrap;
their compatibility projections are reachable only from explicit non-strict
legacy/test configuration and are not production fallback paths.

Activation planning validates the complete effect, execution, target, cost, and
capacity plan before committing payloads or cooldown. Source entity, owning
player, and target entity are separate request fields; attribution, costs,
events, and death rewards use the owning player. Legacy multiplier definitions
are normalized to raw damage at the runtime boundary, while other damage stages
retain their declared semantics. Unsupported typed content is rejected without
resource, payload, cooldown, or event side effects.

All public activation entry points now delegate to one `ActivateCore` planning
and commit boundary. Single-target, deterministic multi-target, ECS slot, and
legacy cooldown-array adapters share the same phase, tag, targeting, cost,
capacity, payload-handler, cooldown, and activation-event rules. A supported
custom payload handler receives one read-only `CanCommit` call during planning
and one `Commit` call during commit. Later damage executions may observe only a
death queued by an earlier execution in the same activation plan.

Strict bootstrap now treats required auxiliary configuration as required input
and reports both its configured and resolved paths when loading fails. Enemy
ability type semantics come from one registry shared by catalog compilation,
strict reference validation, and runtime dispatch. Summon and stealth remain
typed `WorldAction` executions and cross the same activation/runtime adapter
boundary as other enemy abilities. Summon health and damage multipliers are
encoded in the compiled execution and must be positive at typed parsing,
Catalog compilation/validation, strict reference validation, activation
planning, and commit. Invalid or mismatched definitions reject before entity
allocation or cooldown commit.

The scheduler now publishes its phase context into the store before graph
execution, so every activation entry enforces the compiled `AllowedPhases`
contract at the shared runtime boundary. Required and blocked gameplay tags are
evaluated from active granted tags for both source and target. Global skill
configuration is compiled into the same immutable Catalog, and enemy damage to
players crosses the typed resource resolver, including shield absorption,
health, damage facts, and player-death facts. Requests rejected at a phase or
tag boundary do not commit payloads, costs, cooldowns, or activation events.

Hero bindings now use a strict structured parser and reject missing, duplicate,
or out-of-range slots before runtime initialization. Production-only payloads
share a composed handler chain with an explicit support table; planning calls
`CanCommit` read-only and commit dispatch occurs exactly once. Enemy ally buffs,
tower silence, and dispel compile as typed Catalog effects/executions, use the
same enemy ability registry as validation and dispatch, and execute through the
shared activation boundary. Compatibility helpers remain reachable only from
explicit non-strict fixtures.

Strict production configuration is parsed through `TypedGameConfigParser`
before Catalog compilation. The typed parser owns the three content sources,
their precedence and conflict diagnostics, and unknown-member rejection. The
legacy JSON adapter remains available only to explicit non-strict compatibility
fixtures; strict loading does not synthesize fallback/default content. Catalog
integration assertions derive abilities, payloads, and slots from the loaded
content instead of pinning production names, IDs, or slot numbers.

Strict and legacy loading are separate `IConfigurationParser` adapters. The
strict public entry references only the strict adapter, while the compatibility
entry references only the legacy adapter. An IL architecture test locks both
the outer adapter routing and the strict parser's complete project-assembly
call graph, including ordinary intermediate helpers: no reachable method may
enter `ParseLegacyGameConfig`, any `Extract*` helper, or any `DefGet*` helper.
Every strict adapter route (main, behavior, enemy, phase, and weather) must reach
its corresponding typed parser. The legacy route is a positive control, and a
synthetic two-hop helper probe proves the walker detects forbidden calls beyond
its roots. Method and member tokens are resolved with declaring generic type and
method context; unknown opcodes, truncated operands, and unresolved project call
tokens fail the test instead of dropping an edge. A generic MethodSpec two-hop
probe locks that behavior, and strict reachable code may not use reflection
dispatch APIs such as `MethodBase.Invoke`, `Delegate.CreateDelegate`, or
`Type.GetMethod`. This boundary is verified without matching source text.
`CatalogCompiler` applies
explicit property allowlists to curated skills, nested modifiers, and static
skill files, so misspelled properties report the source file and JSON node path.
Canonical, static, and player-bar sources retain explicit semantic-field
presence. Player aliases compare every supplied Catalog-consumed field,
including values equal to CLR defaults, while omitted fields remain compatible.
This covers mana
cost, damage multiplier, heal, shield, slow, freeze chance/duration, dot/effect,
targeting (including an explicit 60-degree cone), cooldown, and modifier payload
kind. Conflict diagnostics identify both the player node and winning compiled
ability name/ID.

Strict enemy telegraphs compile into typed `Telegraph` payloads with a
`QueueTelegraph` operation. Enemy activation enters `ActivateCore`, the shared
handler performs capacity planning, and commit only queues the zone. On expiry,
the telegraph submits a typed player-damage request through `ResourceResolver`;
the production-frame test verifies the configured duration/color, no immediate
damage, and exactly one expiry hit. Curated freeze definitions likewise compile
their configured duration and probability into a typed `Freeze` payload with an
`ApplyFreeze` operation. The shared runtime handler applies the configured
probability deterministically and commits freeze through the production
scheduler entry without branching on a skill name or ID.

Modifier normalization retains provenance in the typed Catalog. Each source
modifier records its original name/type/value/duration/stacking/tag together
with normalized payload, operation, magnitude, probability, and targeting.
Freeze crowd control therefore round-trips exactly to `Freeze/ApplyFreeze`
without reopening generic CrowdControl or Debuff payloads. Alias validation
compares the complete descriptor by position, and Catalog validation rejects a
descriptor whose normalized execution has drifted. The closure includes the
execution tag and, where the normalized payload has no effect object, an
explicit Freeze tag plus stacking/max-stack presence contract; effect-backed
payloads compare their effect and execution tags, duration, stacking, and
max-stacks as well. Freeze executions explicitly carry the canonical
normalized stacking and max-stack values, and validation compares them exactly;
another valid stacking enum or positive max-stack is still rejected. Direct
damage source descriptors and normalized executions explicitly use the absent
`None/0` stack contract because stacking is not applicable to that payload;
validator checks the source descriptor and execution in both directions.

Compatibility is intentionally narrow and field-specific:

| Source field | Strict status | Runtime status |
|---|---|---|
| `game_config.json Skills[].AutoCast` | accepted typed boolean | forced to `false`; legacy-inactive until an AutoCast behavior cutover is separately authorized |
| player `DotDuration`, `DotTickInterval`, `Modifiers`, explicit `ConeAngleDegrees` | preserved through typed parsing, including explicit values equal to CLR defaults | compared against the winning Catalog definition before activation; conflicts in values or modifier payload kinds fail startup |
| player `DotDamagePerTick`, polymorph fields, `SummonDefId` | accepted as explicit legacy compatibility data | normalized inactive because the compiled Catalog has no direct consumer for these player-bar aliases |
| `enemy_abilities.json CastTime` | accepted only by the explicit inactive compatibility allowlist | normalized to `0`; channeling remains inactive in strict production |
| `enemy_abilities.json Interruptible` | accepted only by the explicit inactive compatibility allowlist | retains the runtime default and has no effect while `CastTime` is inactive |
| `StunDuration`, `SlowFactor`, `SlowDuration` | typed, range-validated, and compiled | active; strict production tests verify exact configured control values through runtime commit |
| `MinionHealthMult`, `MinionDamageMult` | typed, compiled into the execution, and required to be greater than zero at parser/compiler/Catalog/strict-reference boundaries | activation planning and commit revalidate the exact pair; invalid input consumes neither entity ID nor cooldown, and there is no runtime `0 -> 0.3` fallback |
| `TelegraphDuration`, `TelegraphColor` | typed, validated, and compiled as `Telegraph/QueueTelegraph` | active through `ActivateCore`; expiry damage crosses `ResourceResolver` exactly once |
| `FreezeDuration`, `FreezeChance` | typed, range-validated, and compiled as `Freeze/ApplyFreeze` | active through the shared handler and production scheduler; configured probability and duration are preserved |

Unknown enemy fields outside that precise compatibility list fail fast. In
particular, real stun, slow, summon, and telegraph behavior fields are no longer
accepted and discarded as generic legacy data.

Validation evidence for this final parser/Catalog closure: Core and executable
builds have zero warnings/errors, the focused parser/Hero/strict-production
suite passes (`122/122`), the complete test suite passes (`1726/1726`), static
test rules report zero violations across 1560 parsed test methods, and
`git diff --check` passes. Performance validation is
deferred until the architecture migration is complete by explicit migration
policy; mode2, mode4, and mode5 were not rerun for this final integration and do
not block M6 closure. No performance claim is made for this final tree.

The current final-review artifact is stored outside the repository at
`C:\Users\Administrator\.codex\evidence\battlesystem-ecs\m6-typed-runtime-ninth-review-370bf09-20260902`.
Its `manifest.json` identifies the exact committed HEAD, dirty patch, untracked
evidence, DLLs, gate logs, protected stash objects, and complete linear
`master..HEAD` lineage with every hash, parent, and subject. The authoritative
manifest SHA-256 is recorded in the artifact's `manifest.sha256` sidecar.

The previous `m6-final-8a81bf8` artifact and manifest SHA-256
`A0B7FA3281D0219D56DBB8678FFA15BCD83AF4FB6DDA14620267FE47F46E3092`
are historical records superseded by the current artifact.
The earlier evidence snapshot
SHA-256 `54BA450D9B46211847893ECC0214C4276E6628995650474C177198C107463600`
and `C629795973C96139114B5A5514FF34A29FCE2F7069FFBD2650F1E4231C6D7AB0`,
along with all benchmark observations above, are historical snapshots
superseded by this final integration artifact.

The previous `m6-final-32d6321` artifact and its manifest SHA-256
`5BDDE639CD4DEBD7EA3E5FC2F651440BB00F376DF08645CD4BC2F00C7C6F8EF4`
are also superseded historical records.

Unity smoke status: `UNAVAILABLE/BLOCKED`. The documented Unity project path
`F:\AI\BattleSystem-ECS-Unity` does not exist on this host, no Unity process is
running, `127.0.0.1:8080` is unreachable, and no Unity MCP tool is available.
No Unity `BattleDriver` claim is made from this environment.

Combo ownership is now explicit: the compiled effect is the sole combat
contributor through the player `AttackDamage` computed projection. The legacy
`PlayerComboDamageMult` field remains a compatibility/UI projection and is no
longer multiplied by `PlayerTowerAttackSystem`, preventing double contribution.
