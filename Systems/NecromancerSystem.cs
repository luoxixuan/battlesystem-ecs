using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Necromancer System — resurrects nearby enemy corpses as reanimated minions.
    /// 
    /// Two-phase pattern:
    ///   - Phase 1 (SetTurn): cache simTime, scan for necromancers with expired cooldowns
    ///   - Phase 2 (Update): for each ready necromancer, find and resurrect a corpse
    /// 
    /// Corpse queue:
    ///   - Enemies are queued as corpses on death (in ComponentStore via NecromancerQueueCorpse).
    ///   - Each corpse stores position, monster type, death time, and owner.
    ///   - Corpse expires after MAX_CORPSE_AGE_SEC (30s) — configurable per monster.
    ///   - A corpse can only be resurrected once (CorpseReanimated flag).
    /// 
    /// Reanimated minions:
    ///   - Spawned as regular enemy entities via ComponentStore.AddEnemy().
    ///   - Stats scaled by ResurrectHpMult from the necromancer's config.
    ///   - Tagged with EnemyIsReanimated[] so they can be identified.
    ///   - Do NOT count toward nest spawner ActiveCount.
    /// 
    /// Integration points:
    ///   - FrameScheduler.Tick() Phase 2.5 calls Necromancer.SetTurn() + Update()
    ///   - GameManager bootstrap creates and injects NecromancerSystem instance
    ///   - FrameScheduler registers via scheduler.Necromancer
    /// </summary>
    public class NecromancerSystem
    {
        private readonly ComponentStore _store;
        private readonly GameConfig _gameConfig;
        private readonly IRenderer _logger;

        // Snapshot of the current turn from SetTurn
        private int _currentTurn;
        private float _currentSimTime;

        // Per-enemy reanimated minion count (keyed by necromancer entity ID)
        private Dictionary<int, int> _reanimatedCountByOwner = new Dictionary<int, int>();

        public NecromancerSystem(ComponentStore store, GameConfig gameConfig, IRenderer logger = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            _logger = logger;
        }

        /// <summary>
        /// SetTurn — cache current turn and simTime.
        /// Called from FrameScheduler before Update().
        /// </summary>
        public void SetTurn(int turn, float simTime)
        {
            _currentTurn = turn;
            _currentSimTime = simTime;
        }

        /// <summary>
        /// Update — process all necromancer enemies: cooldown ticking and resurrection.
        /// Called from FrameScheduler Phase 2.5.
        /// </summary>
        public void Update(float deltaTime)
        {
            var activeIds = _store.GetCachedActiveEnemyIds();

            for (int i = activeIds.Count - 1; i >= 0; i--)
            {
                int enemyId = activeIds[i];
                if (!_store.EnemyActive[enemyId]) continue;
                if (!_store.EnemyCanResurrect[enemyId]) continue;

                // Tick cooldown
                if (_store.EnemyResurrectCooldown[enemyId] > 0f)
                {
                    _store.EnemyResurrectCooldown[enemyId] -= deltaTime;
                }

                // Check if ready to resurrect
                float cooldown = _store.EnemyResurrectCooldownRef[enemyId];
                if (cooldown > 0f && _store.EnemyResurrectCooldown[enemyId] > 0f) continue;

                // Check max resurrect count
                int currentCount = 0;
                int maxCount = _store.EnemyMaxResurrectCount[enemyId];
                if (maxCount > 0)
                {
                    _reanimatedCountByOwner.TryGetValue(enemyId, out currentCount);
                    if (currentCount >= maxCount) continue;
                }

                // Attempt resurrection
                bool resurrected = TryResurrectCorpse(enemyId);
                if (resurrected)
                {
                    // Reset cooldown (use ref value, or -1 for one-time)
                    float refCooldown = _store.EnemyResurrectCooldownRef[enemyId];
                    if (refCooldown < 0f)
                    {
                        // One-time use: mark as exhausted
                        _store.EnemyResurrectCooldown[enemyId] = float.MaxValue;
                    }
                    else if (refCooldown > 0f)
                    {
                        _store.EnemyResurrectCooldown[enemyId] = refCooldown;
                    }

                    // Increment reanimated count
                    _reanimatedCountByOwner[enemyId] = currentCount + 1;
                }
            }
        }

        /// <summary>
        /// TryResurrectCorpse — find the nearest valid corpse within range and resurrect it.
        /// Returns true if a corpse was successfully resurrected.
        /// </summary>
        private bool TryResurrectCorpse(int necromancerId)
        {
            float range = _store.EnemyResurrectRange[necromancerId];
            if (range <= 0f) return false;

            float nx = _store.PositionX[necromancerId];
            float ny = _store.PositionY[necromancerId];
            float hpMult = _store.EnemyResurrectHpMult[necromancerId];
            float ageLimit = _store.EnemyResurrectCorpseAgeLimit[necromancerId];
            if (ageLimit <= 0f) ageLimit = ComponentStore.MAX_CORPSE_AGE_SEC;

            // Find the nearest valid, un-reanimated corpse within range
            int bestCorpseId = -1;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < ComponentStore.MAX_CORPSE_QUEUE; i++)
            {
                if (!_store.CorpseActive[i]) continue;
                if (_store.CorpseReanimated[i]) continue;
                if (_store.CorpseOwnerId[i] >= 0) continue; // already claimed

                // Check age limit
                float age = _currentSimTime - _store.CorpseDeathTime[i];
                if (age > ageLimit) continue;

                float dx = _store.CorpseX[i] - nx;
                float dy = _store.CorpseY[i] - ny;
                float distSq = dx * dx + dy * dy;
                if (distSq > range * range) continue;

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestCorpseId = i;
                }
            }

            if (bestCorpseId < 0) return false;

            // Claim the corpse
            _store.CorpseOwnerId[bestCorpseId] = necromancerId;
            _store.CorpseReanimated[bestCorpseId] = true;

            // Spawn the reanimated minion (refactored into shared helper so MassResurrect
            // can reuse it). The necromancer variant uses its own hpMult + position as
            // summon-circle anchor.
            SpawnReanimatedMinion(bestCorpseId, necromancerId, hpMult, nx, ny, range, isNecromancer: true);
            return true;
        }

        // ── Round 133 Dir 5 ─────────────────────────────────────────────────────
        // MassResurrect — player-triggered AOE revival. Scans the entire CorpseQueue
        // (bounded MAX_CORPSE_QUEUE = 256 by default — linear scan is cheap and avoids
        // needing a spatial-grid secondary structure for corpse positions, which are not
        // registered in SpatialGrid since SpatialGrid only tracks live enemies).
        //
        // Behavior:
        //   - Claim every active, un-reanimated corpse within `radius` of (centerX, centerY).
        //   - Age-gate by `MAX_CORPSE_AGE_SEC` (use CorpseReanimated flag to prevent double-revive).
        //   - Spawn reanimated minion at corpse position with `hpFraction` of max HP
        //     (lower than per-necromancer hpMult, since this is a one-shot divine spell).
        //   - Returns the count of corpses successfully resurrected (claimed).
        //
        // Integration:
        //   - Exposed as a public method (called by SkillSystem.ExecuteAbility case 18).
        //   - The caster position is passed separately so the SummonCircle anchor is the
        //     player (matches the typical "divine aura" flavor).
        //   - One-shot semantics: no cooldown, no per-frame state. The caller (SkillSystem)
        //     handles cooldown via AbilityInstance.CurrentCooldown.
        public int MassResurrect(int playerId, float centerX, float centerY, float radius, float hpFraction)
        {
            if (radius <= 0f) return 0;
            if (hpFraction <= 0f) hpFraction = 0.3f; // safety default (matches direction spec)
            float radiusSq = radius * radius;
            int revived = 0;

            for (int i = 0; i < ComponentStore.MAX_CORPSE_QUEUE; i++)
            {
                if (!_store.CorpseActive[i]) continue;
                if (_store.CorpseReanimated[i]) continue;
                if (_store.CorpseOwnerId[i] >= 0) continue; // already claimed by a necromancer

                // AOE gate (cheap squared distance, no sqrt)
                float dx = _store.CorpseX[i] - centerX;
                float dy = _store.CorpseY[i] - centerY;
                if (dx * dx + dy * dy > radiusSq) continue;

                // Age gate — corpses older than MAX_CORPSE_AGE_SEC are too decomposed
                float age = _currentSimTime - _store.CorpseDeathTime[i];
                if (age > ComponentStore.MAX_CORPSE_AGE_SEC) continue;

                // Claim & spawn
                _store.CorpseOwnerId[i] = playerId;
                _store.CorpseReanimated[i] = true;
                SpawnReanimatedMinion(i, playerId, hpFraction, centerX, centerY, radius, isNecromancer: false);
                revived++;
            }
            if (revived > 0)
            {
                _logger?.Log($"[MASS-RES] Player {playerId} mass-resurrected {revived} corpses within radius {radius:F1} (hpFraction={hpFraction:F2})");
            }
            return revived;
        }

        // ── Shared spawn helper (refactored out of TryResurrectCorpse, Round 133) ──
        // Spawns a reanimated minion at the corpse's recorded position. Used by both:
        //   - TryResurrectCorpse (necromancer-driven, per-corpse cooldown, hpMult from config)
        //   - MassResurrect (player-driven, AOE, fixed hpFraction)
        //
        // Behavior is identical to the original TryResurrectCorpse body:
        //   - look up monster stats from MonsterTypes config (string match on Type or Name)
        //   - AddEnemy at corpse position with hpMult * corpseHpPercent HP
        //   - mark EnemyIsReanimated + EnemyOwnerId
        //   - register SummonCircle at caster position with given radius
        //
        // Returns the spawned minion's entity id, or -1 if AddEnemy failed (pool full).
        // The "isNecromancer" flag exists for future divergence (e.g., player-raised
        // minions could be tagged as friendly/player-summoned); current behavior is
        // identical for both callers.
        private int SpawnReanimatedMinion(int corpseId, int ownerId, float hpMult, float ownerX, float ownerY, float ownerRange, bool isNecromancer)
        {
            string monsterType = _store.CorpseMonsterType[corpseId];
            float corpseHpPercent = _store.CorpseHealth[corpseId]; // 0.0-1.0 HP fraction from death state

            // Look up the base monster config for spawn parameters
            float baseMoveSpeed = 1f;
            float baseMaxHealth = 100f;
            float baseDamage = 10f;
            int baseGoldReward = 5;
            int waveNum = 1;
            float baseArmor = 0f;
            float baseShield = 0f;
            float baseMagicResist = 0f;

            if (_gameConfig.MonsterTypes != null)
            {
                foreach (var mc in _gameConfig.MonsterTypes)
                {
                    if (mc.Type == monsterType || mc.Name == monsterType)
                    {
                        baseMoveSpeed = mc.MoveSpeed;
                        baseMaxHealth = mc.Health;
                        baseDamage = mc.Damage;
                        baseGoldReward = mc.GoldReward;
                        baseArmor = mc.Armor;
                        baseShield = mc.Shield;
                        baseMagicResist = mc.MagicResist;
                        break;
                    }
                }
            }

            float spawnX = _store.CorpseX[corpseId];
            float spawnY = _store.CorpseY[corpseId];
            float spawnHealth = baseMaxHealth * corpseHpPercent * hpMult;

            int minionId = _store.AddEnemy(
                spawnX, spawnY,
                baseMoveSpeed * 0.8f,  // reanimated minions are slower
                spawnHealth,
                spawnHealth,           // maxHealth = currentHealth (no future regen)
                baseDamage * 0.5f,     // reanimated minions deal less damage
                baseGoldReward,
                waveNum,
                monsterType + "_reanimated",
                baseArmor * 0.5f,
                baseShield,
                baseMagicResist
            );

            if (minionId < 0) return -1; // Pool exhausted — corpse already claimed; minion won't spawn

            // Mark as reanimated (shared flag for both necromancer + mass-resurrect paths)
            _store.EnemyIsReanimated[minionId] = true;
            _store.EnemyOwnerId[minionId] = ownerId;

            // ── Summon Circle registration (Round 115 Direction 2) ──
            // Tag the minion with the caster's position as the summon-circle anchor.
            // For necromancers this is the necromancer's own position; for MassResurrect
            // this is the player-caster's position. Radius mirrors the source caster's
            // range (necromancer's per-corpse range OR player's mass-resurrect radius).
            _store.SetSummonCircle(minionId, ownerX, ownerY, ownerRange);

            _logger?.Log($"[NECRO] {(isNecromancer ? "Entity" : "Player")} {ownerId} resurrected corpse {corpseId} as minion {minionId} at ({spawnX:F1}, {spawnY:F1})");
            return minionId;
        }

        /// <summary>
        /// Queue a newly killed enemy as a corpse for potential necromancer resurrection.
        /// Called from ResolveEnemiesKilledThisFrame or a dedicated event handler.
        /// </summary>
        public void QueueCorpse(int enemyId, float hpPercentAtDeath)
        {
            // Don't queue necromancers themselves or reanimated minions
            if (_store.EnemyCanResurrect[enemyId] || _store.EnemyIsReanimated[enemyId]) return;

            string monsterType = _store.EnemyTypeName[enemyId];
            if (string.IsNullOrEmpty(monsterType)) return;

            float x = _store.PositionX[enemyId];
            float y = _store.PositionY[enemyId];

            int corpseId = _store.NecromancerQueueCorpse(enemyId, x, y, monsterType, hpPercentAtDeath, _currentSimTime);
            if (corpseId < 0)
            {
                _logger?.Log($"[NECRO] Corpse queue full — dropping corpse for enemy {enemyId}");
            }
        }

        /// <summary>
        /// Called by GameManager when an enemy is killed (before DestroyEntity).
        /// Queue the corpse for potential resurrection.
        /// </summary>
        public void OnEnemyKilled(int enemyId, int playerId)
        {
            // Calculate HP% at death (health is already reduced by final damage)
            float currentHp = _store.EnemyHealth[enemyId];
            float maxHp = _store.EnemyMaxHealth[enemyId];
            float hpPercent = (maxHp > 0f) ? (currentHp / maxHp) : 0f;
            QueueCorpse(enemyId, hpPercent);
        }

        /// <summary>
        /// Reset reanimated count for a necromancer (e.g., when it dies).
        /// </summary>
        public void ClearReanimatedCount(int necromancerId)
        {
            _reanimatedCountByOwner.Remove(necromancerId);
        }
    }
}