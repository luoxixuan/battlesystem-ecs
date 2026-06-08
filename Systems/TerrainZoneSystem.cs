using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
 /// <summary>
 /// Round204 / Direction2 — Elemental Terrain Zone system.
 ///
 /// Player-spawned elemental terrain zones (Frozen Lake / Burning Ground / Toxic Swamp / Holy
 /// Sanctum). Distinct from map-baked HotZone (placement bonus) and tower-spawned HazardZone
 /// (single-effect DoT). Each zone has element type, per-stack slow + DoT, and stacks up on
 /// enemies that linger inside. Lifetime-decay removes the zone when its timer hits0.
 ///
 /// Behavior per frame:
 ///1. Reset per-enemy aggregate state (SlowTotal / DpsTotal / InTerrainZone) so this frame's
 /// ticks start from zero (prevents additive saturation + stale state).
 ///2. Decrement every active zone's lifetime. Expire any zone whose lifetime <=0 (Remove).
 ///3. Tick per-zone tickTimer; on each tick: scan active enemies in radius, add stack,
 /// accumulate aggregate slow + DPS into per-enemy arrays.
 ///4. (Optional) Expand-over-time grows radius from baseRadius toward maxRadius.
 ///
 /// Sentinel-gated fast path: when no zone is active (active list empty), Update() still calls
 /// ResetAllEnemyAggregateState so stale per-enemy state from a previous zone is cleared.
 ///
 /// Execution order: CombatGroup, runs after HotZone (map terrain bonuses) and before
 /// CorpseEffect (corpse ground effects). Damage is applied via store.ApplyEnemyDamage so
 /// shield rules + path terrain multipliers fire.
 /// </summary>
 public class TerrainZoneSystem
 {
 private readonly ComponentStore _store;
 private readonly GameConfig _config;
 private readonly int _playerId;
 private readonly BuffSystem _buffSystem;

 public TerrainZoneSystem(ComponentStore store, GameConfig config, int playerId, BuffSystem buffSystem = null)
 {
 _store = store ?? throw new ArgumentNullException(nameof(store));
 _config = config ?? throw new ArgumentNullException(nameof(config));
 _playerId = playerId;
 _buffSystem = buffSystem;
 }

 /// <summary>
 /// Per-turn hook (currently a no-op — the system is driven entirely by per-frame Update).
 /// Kept for parity with other systems in CombatSetupGroup so future per-turn bookkeeping
 /// (e.g. end-of-turn stack decay) can be added without changing the wiring.
 /// </summary>
 public void SetTurn(int turn)
 {
 // No-op: TerrainZoneSystem is fully driven by Update(deltaTime).
 }

 /// <summary>
 /// Spawn a player-spawned elemental terrain zone by Id from config. Returns zone id, or -1
 /// if the def is not found or the pool is full. Convenience wrapper for tests / SkillSystem.
 /// </summary>
 public int SpawnTerrainZone(string defId, float x, float y, int ownerPlayerId = -1)
 {
 if (_config == null) return -1;
 var def = _config.GetTerrainZoneDef(defId);
 if (def == null) return -1;
 return _store.AddTerrainZone(
 x, y, def.Radius, def.Element,
 def.BaseDps, def.SlowPerStack, def.MaxStacks,
 def.Lifetime, def.TickInterval, def.ExpandOverTime,
 ownerPlayerId >=0 ? ownerPlayerId : _playerId,
 def.Id);
 }

 /// <summary>
 /// Per-frame tick. Hot-path friendly: when no zone is active, still resets per-enemy
 /// aggregate state to clear any stale data from a previous frame's zone. When zones are
 /// active, O(zones × enemies) worst case (typically small — at most a few overlapping
 /// zones during late-game waves).
 ///
 /// Per-frame flow:
 /// 1. Reset per-enemy aggregate state (SlowTotal / DpsTotal / InTerrainZone = 0) so
 /// this frame's zones accumulate from zero.
 /// 2. For each zone: tick (with slowOnce = first-tick-of-frame flag) — sets
 /// InTerrainZone = 1 for enemies inside, accumulates slow + DPS, applies DoT damage.
 /// 3. After all zones have ticked, decay per-enemy stacks ONLY for enemies still
 /// flagged as out-of-zone (InTerrainZone == 0 from this frame's perspective). Running
 /// the decay AFTER the zone ticks fixes the "re-entry decay tax" bug where leaving
 /// for a single frame and re-entering cost the enemy a stack.
 /// </summary>
 public void Update(float deltaTime)
 {
 // Reset per-enemy aggregate state every frame so SlowTotal / DpsTotal / InTerrainZone
 // reflect THIS frame's zones only.
 ResetAllEnemyAggregateState();

 var activeZoneIds = _store.GetCachedActiveTerrainZoneIds();
 if (activeZoneIds.Count ==0)
 {
 // Even with no zones, decay stale stacks (so when ALL zones expire, stacks drain
 // rather than linger forever on enemies that were last inside them).
 DecayStacksForOutOfZoneEnemies();
 return;
 }

 // Decrement lifetime; collect expired zone ids.
 List<int> expired = null;
 for (int i =0; i < activeZoneIds.Count; i++)
 {
 int zoneId = activeZoneIds[i];
 if (!_store.TerrainZoneActive[zoneId]) continue;
 _store.TerrainZoneLifetime[zoneId] -= deltaTime;
 if (_store.TerrainZoneLifetime[zoneId] <=0f)
 {
 if (expired == null) expired = new List<int>();
 expired.Add(zoneId);
 }
 }
 if (expired != null)
 {
 for (int i =0; i < expired.Count; i++)
 {
 _store.RemoveTerrainZone(expired[i]);
 }
 }

 if (activeZoneIds.Count ==0)
 {
 DecayStacksForOutOfZoneEnemies();
 return;
 }

 // Per-zone tick (DoT + slow aggregation) + radius expansion.
 for (int i =0; i < activeZoneIds.Count; i++)
 {
 int zoneId = activeZoneIds[i];
 if (!_store.TerrainZoneActive[zoneId]) continue;

 // Expand-over-time: grow radius from baseRadius toward maxRadius (1.5x base).
 if (_store.TerrainZoneExpandOverTime[zoneId] && _store.TerrainZoneBaseRadius[zoneId] >0f)
 {
 float maxR = _store.TerrainZoneMaxRadius[zoneId];
 if (_store.TerrainZoneRadius[zoneId] < maxR)
 {
 float newR = _store.TerrainZoneRadius[zoneId] + deltaTime *0.5f;
 if (newR > maxR) newR = maxR;
 _store.TerrainZoneRadius[zoneId] = newR;
 }
 }

 _store.TerrainZoneTickTimer[zoneId] += deltaTime;
 float interval = _store.TerrainZoneTickInterval[zoneId];
 if (interval <=0f) interval =1f;
 // Use a while loop so long frames don't discard accumulated tick time.
 // Cap iterations to prevent runaway on extreme frames (e.g., debugger pause).
 int safetyCap =8;
 int ticksThisFrame =0;
 while (_store.TerrainZoneTickTimer[zoneId] >= interval && safetyCap >0)
 {
 _store.TerrainZoneTickTimer[zoneId] -= interval;
 // DoT damage fires per-tick (correct — should scale with elapsed time).
 // Slow accumulation is per-zone-per-frame (NOT per-tick) — passed via slowOnce.
 bool slowOnce = (ticksThisFrame ==0);
 ApplyZoneTickToEnemies(zoneId, slowOnce);
 ticksThisFrame++;
 safetyCap--;
 }
 if (_store.TerrainZoneTickTimer[zoneId] >= interval)
 {
 // Still over interval after cap — drop residual to prevent infinite buildup.
 _store.TerrainZoneTickTimer[zoneId] =0f;
 }
 }

 // After all zone ticks, decay stacks for enemies still flagged as out-of-zone
 // (InTerrainZone == 0 from this frame). This runs AFTER the zone ticks so a single-
 // frame re-entry doesn't double-charge the enemy.
 DecayStacksForOutOfZoneEnemies();
 }

 /// <summary>
 /// Reset per-enemy terrain zone aggregate state for every active enemy. Called once at the
 /// top of Update() so SlowTotal / DpsTotal / InTerrainZone are zero before per-zone ticks
 /// accumulate them. Without this reset, additive accumulation would saturate the slow clamp
 /// and stale state would persist after zones expire.
 /// </summary>
 private void ResetAllEnemyAggregateState()
 {
 var activeEnemies = _store.GetCachedActiveEnemyIds();
 for (int i =0; i < activeEnemies.Count; i++)
 {
 int enemyId = activeEnemies[i];
 if (!_store.EnemyActive[enemyId]) continue;
 _store.EnemyTerrainZoneSlowTotal[enemyId] =0f;
 _store.EnemyTerrainZoneDpsTotal[enemyId] =0f;
 _store.EnemyInTerrainZone[enemyId] =0;
 }
 }

 /// <summary>
 /// Apply one tick of zone effects to all enemies inside the zone radius. Adds a stack to
 /// the matching per-element counter (capped at MaxStacks), accumulates aggregate slow and
 /// DPS, and on tick deals DoT damage via ApplyEnemyDamage.
 ///
 /// `aggregateSlow` is true on the first tick of this frame for this zone; on subsequent
 /// ticks (when one long frame fired multiple ticks) we still deal DoT damage but skip
 /// the slow accumulator so a long frame doesn't double-count slow. DoT damage correctly
 /// scales with elapsed time and must fire per-tick.
 /// </summary>
 private void ApplyZoneTickToEnemies(int zoneId, bool aggregateSlow)
 {
 float zx = _store.TerrainZoneX[zoneId];
 float zy = _store.TerrainZoneY[zoneId];
 float zr = _store.TerrainZoneRadius[zoneId];
 float zrSq = zr * zr;
 int element = _store.TerrainZoneElement[zoneId];
 float baseDps = _store.TerrainZoneBaseDps[zoneId];
 float slowPerStack = _store.TerrainZoneSlowPerStack[zoneId];
 int maxStacks = _store.TerrainZoneMaxStacks[zoneId];
 int ownerPlayer = _store.TerrainZoneOwnerPlayerId[zoneId];
 float interval = _store.TerrainZoneTickInterval[zoneId];
 if (interval <=0f) interval =1f;

 var activeEnemies = _store.GetCachedActiveEnemyIds();
 for (int i =0; i < activeEnemies.Count; i++)
 {
 int enemyId = activeEnemies[i];
 if (!_store.EnemyActive[enemyId]) continue;

 float dx = _store.PositionX[enemyId] - zx;
 float dy = _store.PositionY[enemyId] - zy;
 if (dx * dx + dy * dy > zrSq) continue;

 // Mark as inside at least one zone
 _store.EnemyInTerrainZone[enemyId] =1;

 // Add stack to the matching element counter (clamped at maxStacks).
 // maxStacks <=0 means stacking is disabled (single-effect only) — we keep the
 // current stack count at0 so the slowAdd below uses *1.
 int currentStacks;
 if (maxStacks >0)
 {
 int existingStacks = GetElementStacks(enemyId, element);
 if (existingStacks < maxStacks)
 {
 int newStacks = existingStacks +1;
 SetElementStacks(enemyId, element, newStacks);
 currentStacks = newStacks;
 }
 else
 {
 currentStacks = existingStacks;
 }
 }
 else
 {
 // No-stacking mode: use1 as the multiplier for slow/DPS application.
 currentStacks =0;
 }

 // Accumulate slow (additive) — only on the first tick of this frame for this zone.
 // Clamp so total terrain slow never reduces speed below10%.
 if (aggregateSlow)
 {
 int slowMultiplier = currentStacks >0 ? currentStacks :1;
 float slowAdd = slowPerStack * slowMultiplier;
 float newSlow = _store.EnemyTerrainZoneSlowTotal[enemyId] + slowAdd;
 if (newSlow >0.9f) newSlow =0.9f;
 _store.EnemyTerrainZoneSlowTotal[enemyId] = newSlow;
 }

 // Accumulate DPS (per second, scaled to "per tick" for the damage call).
 // DpsTotal is per-frame; each zone adds its contribution. Damage below scales by interval.
 _store.EnemyTerrainZoneDpsTotal[enemyId] += baseDps;

 // Apply tick damage: baseDps * interval seconds = baseDps *1s (for default1s tick).
 int damageMultiplier = currentStacks >0 ? currentStacks :1;
 float tickDamage = baseDps * damageMultiplier * interval;
 if (tickDamage >0f && ownerPlayer >=0)
 {
 // Route damage through ApplyEnemyDamage so shield rules + path terrain
 // multipliers fire. Element routing (Fire/Ice/Toxic/Holy) lets the existing
 // shield + reaction system apply mitigation correctly.
 var attackElement = MapElementToElementType(element);
 _store.ApplyEnemyDamage(enemyId, tickDamage, attackElement);
 }
 }
 }

 /// <summary>Per-element stack counter accessor (centralized for cap + reset consistency).</summary>
 private int GetElementStacks(int enemyId, int element)
 {
 switch (element)
 {
 case 0: return _store.EnemyTerrainZoneFireStacks[enemyId];
 case 1: return _store.EnemyTerrainZoneIceStacks[enemyId];
 case 2: return _store.EnemyTerrainZoneToxicStacks[enemyId];
 case 3: return _store.EnemyTerrainZoneHolyStacks[enemyId];
 default: return 0;
 }
 }

 /// <summary>Per-element stack counter setter.0..3 element-only; ignores out-of-range elements.</summary>
 private void SetElementStacks(int enemyId, int element, int stacks)
 {
 switch (element)
 {
 case 0: _store.EnemyTerrainZoneFireStacks[enemyId] = stacks; break;
 case 1: _store.EnemyTerrainZoneIceStacks[enemyId] = stacks; break;
 case 2: _store.EnemyTerrainZoneToxicStacks[enemyId] = stacks; break;
 case 3: _store.EnemyTerrainZoneHolyStacks[enemyId] = stacks; break;
 }
 }

 /// <summary>Map a terrain element int to the existing ElementType flags enum so shield + reaction rules apply.</summary>
 private static Components.ElementType MapElementToElementType(int element)
 {
 // ElementType is a [Flags] enum. Map:0=Fire,1=Ice,2=Toxic -> Poison,3=Holy -> Lightning.
 // Holy doesn't have a native flag; reuse Lightning as a "magical/radiant" placeholder
 // until a dedicated Holy flag is added to ElementType.
 switch (element)
 {
 case 0: return Components.ElementType.Fire;
 case 1: return Components.ElementType.Ice;
 case 2: return Components.ElementType.Poison;
 case 3: return Components.ElementType.Lightning;
 default: return Components.ElementType.None;
 }
 }

 /// <summary>
 /// Decay stale per-enemy aggregate state when an enemy leaves all terrain zones. Reset all
 /// stack counters and aggregate slow/DPS to0, clear the InTerrainZone flag. Designers can
 /// tune decay rate via the optional tickInterval parameter.
 /// </summary>
 public void DecayStacksOnLeave(int enemyId)
 {
 if (enemyId <0) return;
 _store.EnemyTerrainZoneFireStacks[enemyId] =0;
 _store.EnemyTerrainZoneIceStacks[enemyId] =0;
 _store.EnemyTerrainZoneToxicStacks[enemyId] =0;
 _store.EnemyTerrainZoneHolyStacks[enemyId] =0;
 _store.EnemyTerrainZoneSlowTotal[enemyId] =0f;
 _store.EnemyTerrainZoneDpsTotal[enemyId] =0f;
 _store.EnemyInTerrainZone[enemyId] =0;
 }

 /// <summary>
 /// Per-frame stack decay: for every active enemy that is NOT currently in any terrain
 /// zone (InTerrainZone ==0 from the PREVIOUS frame, before ResetAllEnemyAggregateState
 /// is called), decrement each element's stack counter. When all stacks reach0 the slow
 /// aggregate follows. Uses a conservative decay rate:1 stack per element per call
 /// (callers can chain via Update() to accelerate). Stops at0 (no underflow).
 ///
 /// This is what prevents "leave-the-zone-but-keep-max-stacks-slow-forever". Without this,
 /// once an enemy reached max stacks (e.g.5 ice) inside a Frozen Lake, it would retain
 /// the -50% slow forever even after walking far away.
 /// </summary>
 private void DecayStacksForOutOfZoneEnemies()
 {
 var activeEnemies = _store.GetCachedActiveEnemyIds();
 for (int i =0; i < activeEnemies.Count; i++)
 {
 int enemyId = activeEnemies[i];
 if (!_store.EnemyActive[enemyId]) continue;
 if (_store.EnemyInTerrainZone[enemyId] !=0) continue; // still inside — no decay

 if (_store.EnemyTerrainZoneFireStacks[enemyId] >0)
 _store.EnemyTerrainZoneFireStacks[enemyId]--;
 if (_store.EnemyTerrainZoneIceStacks[enemyId] >0)
 _store.EnemyTerrainZoneIceStacks[enemyId]--;
 if (_store.EnemyTerrainZoneToxicStacks[enemyId] >0)
 _store.EnemyTerrainZoneToxicStacks[enemyId]--;
 if (_store.EnemyTerrainZoneHolyStacks[enemyId] >0)
 _store.EnemyTerrainZoneHolyStacks[enemyId]--;
 }
 }
 }
}
