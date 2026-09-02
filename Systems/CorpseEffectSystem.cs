using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Corpse Effect System — manages ground effects spawned when enemies die.
    ///
    /// Two-phase pattern:
    ///   - Phase 1 (ResolveEnemiesKilledThisFrame): subscribe to OnEnemyKilled, queue corpse effects
    ///   - Phase 2 (Update): tick durations, apply effects, expire zones
    ///
    /// Effect types:
    ///   0 = Poison (DoT), 1 = Slow, 2 = Ice (freeze), 3 = Fire (DoT), 4 = Healing, 5 = DamageBoost
    ///   6 = HallowedGround (positive DoT, holy smite — Round 168 Direction 3)
    ///   7 = ThornyBramble (DoT + slow combo — Round 169 Direction 10)
    ///   8 = BlightedGround (DoT + armor/speed debuff — Round 171 Direction 4)
    ///   9 = Smokescreen (tower miss chance + enemy speed boost — Round 175 Direction 9)
    ///  10 = ScorchedEarth (DoT + tower vision reduction — Round 183 Direction 8)
    ///
    /// Integration points:
    ///   - FrameScheduler.Tick() Phase 9.6 calls CorpseEffectSystem.Update()
    ///   - FrameScheduler registers CorpseEffectSystem via scheduler.CorpseEffect
    ///   - GameConfigLoader loads CorpseEffectDefs from Data/Configs/corpse_effects.json
    /// </summary>
    public class CorpseEffectSystem
    {
        private readonly ComponentStore _store;
        private readonly GameConfig _gameConfig;
        private readonly global::BattleSystemECS.Content.Contracts.IEffectCommandPort _buffSystem;
        private readonly IRenderer _logger;

        // Monster type name → CorpseEffectDef lookup (built at startup)
        private Dictionary<string, CorpseEffectDef> _monsterTypeToEffect = new Dictionary<string, CorpseEffectDef>();

        // CorpseEffectDef list (from config)
        private List<CorpseEffectDef> _corpseEffectDefs = new List<CorpseEffectDef>();

        public CorpseEffectSystem(ComponentStore store, GameConfig gameConfig, global::BattleSystemECS.Content.Contracts.IEffectCommandPort buffSystem, IRenderer logger = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            _buffSystem = buffSystem;
            _logger = logger;
        }

        /// <summary>
        /// Load corpse effect definitions from GameConfig.
        /// Must be called after GameConfig.CorpseEffectDefs is populated.
        /// </summary>
        public void LoadCorpseEffects()
        {
            _corpseEffectDefs.Clear();
            _monsterTypeToEffect.Clear();

            if (_gameConfig.CorpseEffectDefs == null || _gameConfig.CorpseEffectDefs.Count == 0)
            {
                _logger?.Log("[CORPSE] No corpse effect definitions found.");
                return;
            }

            foreach (var def in _gameConfig.CorpseEffectDefs)
            {
                _corpseEffectDefs.Add(def);
                if (def.MonsterTypes != null)
                {
                    foreach (var monsterType in def.MonsterTypes)
                    {
                        _monsterTypeToEffect[monsterType] = def;
                    }
                }
            }

            _logger?.Log($"[CORPSE] Loaded {_corpseEffectDefs.Count} corpse effect definitions covering {_monsterTypeToEffect.Count} monster types.");
        }

        /// <summary>
        /// Subscribe to OnEnemyKilled to spawn corpse effects on death.
        /// Called during GameManager bootstrap.
        /// </summary>
        public void SubscribeToOnEnemyKilled()
        {
            _store.OnEnemyKilled += HandleEnemyKilled;
        }

        private void HandleEnemyKilled(int enemyId, int playerId)
        {
            // Look up the monster type for this enemy
            string typeName = _store.EnemyTypeName[enemyId];
            if (string.IsNullOrEmpty(typeName)) return;

            if (!_monsterTypeToEffect.TryGetValue(typeName, out var effectDef))
                return;

            float x = _store.PositionX[enemyId];
            float y = _store.PositionY[enemyId];

            _store.AddCorpseEffect(
                x, y,
                effectDef.EffectType,
                effectDef.Radius,
                effectDef.Duration,
                effectDef.DamagePerTick,
                effectDef.SlowAmount,
                effectDef.TickInterval,
                // Round 171 Direction 4 — Blighted Ground debuffs (ignored for other effect types
                // because their ArmorReduction/SpeedReduction default to 0).
                effectDef.ArmorReduction,
                effectDef.SpeedReduction,
                // Round 175 Direction 9 — Smokescreen fields (ignored for other effect types
                // because their MissChance/EnemySpeedBoost default to 0/1f respectively).
                effectDef.MissChance,
                effectDef.EnemySpeedBoost
            );

            _logger?.Log($"[CORPSE] Spawned {effectDef.Name} at ({x:F1}, {y:F1}) for {effectDef.Duration:F1}s");
        }

        /// <summary>
        /// Update all active corpse effects — decrement duration, apply effects, expire.
        /// Called from FrameScheduler during Phase 9.6.
        /// </summary>
        public void Update(float deltaTime)
        {
            var activeIds = _store.GetCachedActiveCorpseEffectIds();

            for (int i = activeIds.Count - 1; i >= 0; i--)
            {
                int zoneId = activeIds[i];
                if (!_store.CorpseEffectActive[zoneId]) continue;

                // Tick duration
                _store.CorpseEffectDuration[zoneId] -= deltaTime;

                // Tick timer for DoT effects
                int curEffectType = _store.CorpseEffectType[zoneId];
                if (curEffectType == 0 || curEffectType == 3 || curEffectType == 6 || curEffectType == 7 || curEffectType == 8 || curEffectType == 10)
                {
                    // Poison (0), Fire (3), HallowedGround (6), ThornyBramble (7), BlightedGround (8),
                    // ScorchedEarth (10) — all DoT effects.
                    // NOTE: Smokescreen (9) is NOT a DoT — it has no DamagePerTick and is handled purely
                    // in ApplyContinuousEffect (per-frame miss + speed buff). Including it here would
                    // queue a 0-damage DoT pulse every tickInterval for no reason.
                    _store.CorpseEffectTickTimer[zoneId] += deltaTime;
                    float interval = _store.CorpseEffectTickInterval[zoneId];
                    if (interval <= 0f) interval = 1f; // fallback
                    if (_store.CorpseEffectTickTimer[zoneId] >= interval)
                    {
                        _store.CorpseEffectTickTimer[zoneId] -= interval;
                        ApplyDoTTick(zoneId);
                    }
                }

                // Check expiration
                if (_store.CorpseEffectDuration[zoneId] <= 0f)
                {
                    _store.RemoveCorpseEffect(zoneId);
                    continue;
                }

                // Apply per-frame effects (slow, ice freeze)
                ApplyContinuousEffect(zoneId);
            }
        }

        /// <summary>
        /// Apply a DoT tick to all enemies within range of a corpse effect zone.
        /// </summary>
        private void ApplyDoTTick(int zoneId)
        {
            float cx = _store.CorpseEffectX[zoneId];
            float cy = _store.CorpseEffectY[zoneId];
            float radius = _store.CorpseEffectRadius[zoneId];
            float damage = _store.CorpseEffectDamagePerTick[zoneId];
            int effectType = _store.CorpseEffectType[zoneId];

            var enemies = _store.GetCachedActiveEnemyIds();
            foreach (int enemyId in enemies)
            {
                if (!_store.EnemyActive[enemyId]) continue;

                float dx = _store.PositionX[enemyId] - cx;
                float dy = _store.PositionY[enemyId] - cy;
                float distSq = dx * dx + dy * dy;
                if (distSq <= radius * radius)
                {
                    // 通过 IEffectCommandPort 应用周期伤害。
                    // effectType 0 = Poison, 3 = Fire, 6 = HallowedGround, 7 = ThornyBramble, 8 = BlightedGround
                    if (_buffSystem != null)
                    {
                        _ = effectType; // keep switch-like intent explicit
                        // 通过 legacy snapshot 生成完整的周期规则，运行态计时由 global::BattleSystemECS.Content.Contracts.IEffectCommandPort 的 typed store 推进。
                        var dotDef = GameplayEffectDef.Periodic(
                            name: $"corpse_zone_tick_{effectType}",
                            attrIdx: AttributeSetDefinitions.ENEMY_HEALTH,
                            damagePerTick: damage,
                            totalDuration: 1f,             // 1s — one tick per zone pulse
                            tickInterval: 1f
                        );
                        _buffSystem.ApplyDot(enemyId, dotDef);
                    }
                }
            }
        }

        /// <summary>
        /// Apply continuous effects (slow, ice, smokescreen) to entities within range each frame.
        /// Ice (type 2) applies a brief stun/slow; Slow (type 1) reduces speed; Smokescreen (type 9)
        /// marks towers in range as "in smoke" (consumed by TowerAttackSystem) and boosts enemy speed.
        /// </summary>
        private void ApplyContinuousEffect(int zoneId)
        {
            float cx = _store.CorpseEffectX[zoneId];
            float cy = _store.CorpseEffectY[zoneId];
            float radius = _store.CorpseEffectRadius[zoneId];
            int effectType = _store.CorpseEffectType[zoneId];
            float slowAmount = _store.CorpseEffectSlowAmount[zoneId];

            // Slow (1), Ice (2), ThornyBramble (7), BlightedGround (8) need per-frame enemy pass.
            // Smokescreen (9) and ScorchedEarth (10) have their own dedicated passes; do not
            // enter the enemy-only loop.
            if (effectType != 1 && effectType != 2 && effectType != 7 && effectType != 8) {
                // Smokescreen has its own dedicated pass; do not enter the enemy-only loop
                if (effectType == 9)
                {
                    ApplySmokescreenEffects(zoneId);
                }
                else if (effectType == 10)
                {
                    ApplyScorchedEarthEffects(zoneId);
                }
                return;
            }

            var enemies = _store.GetCachedActiveEnemyIds();
            foreach (int enemyId in enemies)
            {
                if (!_store.EnemyActive[enemyId]) continue;

                float dx = _store.PositionX[enemyId] - cx;
                float dy = _store.PositionY[enemyId] - cy;
                float distSq = dx * dx + dy * dy;
                if (distSq > radius * radius) continue;

                if (effectType == 1) // Slow
                {
                    // Apply slow if stronger than existing
                    float existingSlow = _store.EnemyTerrainMoveSpeedMult[enemyId];
                    if (slowAmount < existingSlow)
                    {
                        _store.EnemyTerrainMoveSpeedMult[enemyId] = slowAmount;
                    }
                }
                else if (effectType == 2) // Ice — brief stun/slow
                {
                    // Ice applies a brief stun (handled via EnemyStunDurationLeft in the movement system)
                    // For simplicity, we just slow them significantly
                    float existingSlow = _store.EnemyTerrainMoveSpeedMult[enemyId];
                    float iceSlow = 0.2f; // 80% slow
                    if (iceSlow < existingSlow)
                    {
                        _store.EnemyTerrainMoveSpeedMult[enemyId] = iceSlow;
                    }
                }
                else if (effectType == 7) // ThornyBramble — DoT + slow combo
                {
                    // Same slow application rule as type 1 (Slow): use the slowAmount from JSON
                    float existingSlow = _store.EnemyTerrainMoveSpeedMult[enemyId];
                    if (slowAmount < existingSlow)
                    {
                        _store.EnemyTerrainMoveSpeedMult[enemyId] = slowAmount;
                    }
                }
                else if (effectType == 8) // BlightedGround — DoT + armor/speed debuff
                {
                    // Round 171 Direction 4 — applies both an armor debuff and a speed debuff to
                    // enemies standing in the zone. The values are read from JSON
                    // (armorReduction / speedReduction on the CorpseEffectDef) and accumulated
                    // additively into the existing EnemyCurse*Reduction fields set by
                    // CurseAuraSystem. The ComponentStore.BeginFrame() reset (added in this
                    // round) zeroes these fields at the start of each frame, so the +=
                    // accumulation here is well-defined: each frame starts at 0 and any
                    // overlapping zones / curse towers stack their contributions.
                    // Multiple BlightedGround zones can overlap and stack additively.
                    float zoneArmorRed = _store.CorpseEffectArmorReduction[zoneId];
                    float zoneSpeedRed = _store.CorpseEffectSpeedReduction[zoneId];
                    if (zoneArmorRed > 0f)
                    {
                        _store.EnemyCurseArmorReduction[enemyId] += zoneArmorRed;
                    }
                    if (zoneSpeedRed > 0f)
                    {
                        _store.EnemyCurseSpeedReduction[enemyId] += zoneSpeedRed;
                    }
                }
            }
        }

        /// <summary>
        /// Round 175 Direction 9 — Smokescreen per-frame application.
        /// - Towers in radius: write max(zone.MissChance, existing) into TowerSmokeMissChance[]. ComponentStore.BeginFrame()
        ///   zeroes this array at the start of every frame, so this write fully describes the
        ///   "this frame's miss chance" for the tower. Multiple overlapping smokescreens use max()
        ///   (not +=) so they don't stack multiplicatively into 100% miss. The TowerAttackSystem
        ///   miss roll is a single NextDouble() after the existing accuracy/evasion rolls.
        /// - Enemies in radius: multiply EnemyTerrainMoveSpeedMult[] by the configured speed boost
        ///   (e.g. 1.20 = +20% speed). Multiplicative with existing slow factors — uses max() with 1.0
        ///   floor so a 1.5x boost beats a 0.5x slow (net 0.75x).
        ///
        /// Bounds-checked: zone center is compared against ActiveEnemyIds / ActiveTowerIds
        /// (only iterates live entities, zero waste). Each per-tower and per-enemy write
        /// is O(1) with no allocations.
        /// </summary>
        private void ApplySmokescreenEffects(int zoneId)
        {
            float cx = _store.CorpseEffectX[zoneId];
            float cy = _store.CorpseEffectY[zoneId];
            float radius = _store.CorpseEffectRadius[zoneId];
            float missChance = _store.CorpseEffectMissChance[zoneId];
            float speedBoost = _store.CorpseEffectEnemySpeedBoost[zoneId];
            if (missChance <= 0f && speedBoost <= 1f) return; // inert zone — no-op fast path

            float radiusSq = radius * radius;

            // Tower pass: mark each tower in range with the smoke miss chance (max-merge).
            if (missChance > 0f)
            {
                var towers = _store.ActiveTowerIds;
                for (int i = 0; i < towers.Count; i++)
                {
                    int tid = towers[i];
                    if (!_store.TowerActive[tid]) continue;
                    // Towers use the shared PositionX/PositionY arrays (same as enemies/players).
                    float dx = _store.PositionX[tid] - cx;
                    float dy = _store.PositionY[tid] - cy;
                    if (dx * dx + dy * dy > radiusSq) continue;
                    // max-merge so overlapping smokescreens don't stack into 100% miss
                    if (missChance > _store.TowerSmokeMissChance[tid])
                    {
                        _store.TowerSmokeMissChance[tid] = missChance;
                    }
                }
            }

            // Enemy pass: apply multiplicative speed boost (1.0 = no boost, 1.2 = +20% speed).
            // We multiply into the existing per-frame EnemyTerrainMoveSpeedMult (which is set by
            // other zones in this same Update() loop and reset to 0/1f at the next BeginFrame).
            if (speedBoost > 1f)
            {
                var enemies = _store.GetCachedActiveEnemyIds();
                for (int i = 0; i < enemies.Count; i++)
                {
                    int eid = enemies[i];
                    if (!_store.EnemyActive[eid]) continue;
                    float dx = _store.PositionX[eid] - cx;
                    float dy = _store.PositionY[eid] - cy;
                    if (dx * dx + dy * dy > radiusSq) continue;
                    _store.EnemyTerrainMoveSpeedMult[eid] *= speedBoost;
                }
            }
        }

        /// <summary>
        /// Round 183 Direction 8 — Scorched Earth per-frame application.
        /// - Towers in radius: write max(zone.VisionReduction, existing) into
        ///   TowerVisionReduction[]. ComponentStore.BeginFrame() zeroes this array at the
        ///   start of every frame, so this write fully describes "this frame's range penalty"
        ///   for the tower. Multiple overlapping scorched-earth zones use max() (not +=) so
        ///   they don't compound into 100% blind. The TowerAttackSystem then multiplies the
        ///   tower's effectiveRange by (1 - TowerVisionReduction[tid]) before target selection.
        ///
        /// - Enemies in radius: the actual DoT damage is applied by ApplyDoTTick (the
        ///   CorpseEffectTickTimer-driven pulse), so this method only handles the tower-side
        ///   vision reduction. Bounds-checked: zone center is compared against ActiveTowerIds
        ///   (only iterates live towers, zero waste). Each per-tower write is O(1) with no
        ///   allocations.
        /// </summary>
        private void ApplyScorchedEarthEffects(int zoneId)
        {
            float cx = _store.CorpseEffectX[zoneId];
            float cy = _store.CorpseEffectY[zoneId];
            float radius = _store.CorpseEffectRadius[zoneId];
            float visionRed = _store.CorpseEffectVisionReduction[zoneId];
            if (visionRed <= 0f) return; // inert zone — no-op fast path

            float radiusSq = radius * radius;

            var towers = _store.ActiveTowerIds;
            for (int i = 0; i < towers.Count; i++)
            {
                int tid = towers[i];
                if (!_store.TowerActive[tid]) continue;
                // Towers use the shared PositionX/PositionY arrays (same as enemies/players).
                float dx = _store.PositionX[tid] - cx;
                float dy = _store.PositionY[tid] - cy;
                if (dx * dx + dy * dy > radiusSq) continue;
                // max-merge so overlapping scorched-earth zones don't compound into 100% blind
                if (visionRed > _store.TowerVisionReduction[tid])
                {
                    _store.TowerVisionReduction[tid] = visionRed;
                }
            }
        }

        /// <summary>
        /// Count of active corpse effects.
        /// </summary>
        public int ActiveCorpseEffectCount
        {
            get
            {
                var ids = _store.GetCachedActiveCorpseEffectIds();
                int count = 0;
                foreach (int id in ids)
                    if (_store.CorpseEffectActive[id]) count++;
                return count;
            }
        }
    }
}
