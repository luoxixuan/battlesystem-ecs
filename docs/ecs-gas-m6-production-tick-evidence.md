# Production Catalog Tick Evidence

The production bootstrap derives the combo effect and trigger from the compiled
runtime catalog. `SystemRegistry.CreateAll` requests computed attributes, and a
sealed `FrameScheduler.Tick` applies the first player hit before the next frame
reads the updated `AttackDamage` projection.

`GameplayCatalogProductionFlowTests` verifies the effect's source and target are
the player entity, the modifier targets `AttackDamage`, and the next real player
attack deals more damage. The test clears target-owned post-hit i-frames after
the first frame so target lifecycle does not mask the projection assertion.

The observed next-frame damage includes both the new computed attribute modifier
and the existing `PlayerComboDamageMult` hot-path multiplier. This double
contribution is recorded as a residual semantic risk for a later architecture
review; this slice does not change that existing combo behavior.

Validation: Core and executable builds have zero warnings/errors, all 1593 tests
pass, static test rules report zero violations, and `git diff --check` passes.
Mode4 absolute performance remains deferred until the architecture migration is
complete, per the approved performance policy.
