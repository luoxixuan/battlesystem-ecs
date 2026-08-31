# Damage Source Inventory

This inventory records the current unified damage/resource migration boundary. It is a tracking and rollback document, not a claim that every writer has migrated.

| Method / writer | Domain | Current status | Resolver-owned fields | Rollback point |
|---|---|---|---|---|
| `ComponentStore.AddEnemy`, summon/morph/burrow initialization | Spawn | Unknown/intentional direct initialization | None | Keep spawn initializer |
| `ComponentStore.ApplyEnemyDamage` -> `DamageResolver.ApplyLegacy` | DamageCandidate | Migrated adapter | target safety, shield/HP rule, floor, death queue | Disable adapter and restore legacy body |
| `BuffSystem.ResolveDotDamage` | DamageCandidate | Migrated; first vertical slice | source/target handle, Poison element, raw stage, attribution, death | Stop resolver drain and restore DoT legacy drain |
| `WeatherSystem.ApplyWeatherDot` | DamageCandidate | Migrated; EarlyResolve request | source/target handle, weather element, raw stage, early boundary, death facts | 保留旧天气配置并通过 DamageResolver 提交 |
| `GlobalSkillSystem` meteor damage | DamageCandidate | Migrated adapter; scheduler golden exists | armor projection before resolver, raw damage/death attribution | Disable meteor adapter and restore legacy queue |
| `BleedSystem.ResolveBleedDamage` | DamageCandidate | Migrated request adapter + scheduler golden | resolver raw damage/death attribution/events | Bleed queue drain |
| `FrostbiteSystem.ResolveFrostbiteDamage` | DamageCandidate | Migrated request adapter; scheduler golden covered | resolver raw damage/death attribution/events | Frostbite queue drain |
| `ElementalReactionSystem.ResolveReactionDamage` | DamageCandidate | Migrated request adapter; scheduler golden covered | resolver raw damage/death attribution/events | Reaction queue drain |
| `ProjectileSystem`, `PlayerTowerAttackSystem`, `TowerAttackSystem` damage drains | DamageCandidate | Migrated; resolver requests | source/target generation, sequence, mitigation stage, reflect/transfer flags | 保留串行提交顺序 |
| `EnemyLifeLinkSystem`, `ProtectorSystem`, reflect/thorns/terrain/hero/obstacle paths | DamageCandidate | Resolver requests; Reflect/Transfer provenance retained with chain guard | authority source/target, owner, flags, parent sequence, death facts | Diagnose rejected provenance at caller |
| `ResourceResolver.TryApply` (`Heal`, `Shield`, `Mana`, `Gold`, `MaxHealth`) | Resource | Existing unique writer | resource validation, clamp, finite-value rejection | Resource adapter boundary |
| Direct player resource writes in economy/ability systems | Resource | Migrated; ResourceRequest authority | player entity handle, Add/Set operation, clamp, resource fact | 仅初始化/旧存档兼容便捷方法保留 |

## Current contract

`DamageResolver.TryApply` accepts the current slice contract: supported damage types, `DamageAmountStage.Raw`/`PostCrit`/`PostMitigation`, Reflect/Transfer provenance flags, and either declared commit boundary. `PostCrit` means crit has already been resolved and the resolver applies mitigation once without rolling another crit. Reflect/Transfer execute through the same resolver and retain source/target, parent sequence, `ProvenanceId`, and bounded `ProvenanceDepth <= MaxProvenanceDepth`; direct same-sequence loops are rejected with diagnostics. Deferred requests are bounded, lock-protected, deterministically ordered, and report `Deferred`, overflow, and unconsumed-request diagnostics. `ApplyLegacy` is a validated-target compatibility adapter and requires an explicit owner.

## Next migration order

1. Expand Reflect/Transfer business policies beyond the current bounded provenance and direct-loop guard.
2. Merge lifecycle reward ResourceChanged facts into the gameplay event commit stream.
3. Replace remaining compatibility direct resource projections and document initialization/save/benchmark exceptions.
4. Add BuildPhase ability-gate coverage for every combat producer.

Every source keeps its own cutover switch and rollback point until its scheduler, numeric, attribution, and event-order tests pass.
