# Production Catalog Tick Evidence

The production bootstrap derives the combo effect and trigger from the compiled
runtime catalog. `SystemRegistry.CreateAll` requests computed attributes, and a
sealed `FrameScheduler.Tick` applies the first player hit before the next frame
reads the updated `AttackDamage` projection.

`GameplayCatalogProductionFlowTests` verifies the effect's source and target are
the player entity, the modifier targets `AttackDamage`, and the next real player
attack deals more damage. The test clears target-owned post-hit i-frames after
the first frame so target lifecycle does not mask the projection assertion.

Validation: Core and executable builds have zero warnings/errors, all 1613 tests
pass, static test rules report zero violations, and `git diff --check` passes.
Mode4 absolute performance remains deferred until the architecture migration is
complete, per the approved performance policy.
