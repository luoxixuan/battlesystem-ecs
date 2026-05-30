using System;
using System.Collections.Concurrent;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Enemy Clone System — implements active mid-wave cloning for clone-capable enemies.
    ///
    /// Design:
    /// - Update() runs every frame during WavePhase (after BeginFrame, before combat resolution)
    /// - Checks each active enemy with a valid cloneDefId for trigger conditions
    /// - For HP_THRESHOLD: fires when EnemyHealth / EnemyMaxHealth <= TriggerValue
    /// - When triggered: spawns a functional clone of the enemy at a jittered nearby position
    /// - Clone cannot clone again (prevents exponential spawns)
    /// - Clone has lower tower targeting priority (TowerAttackSystem checks EnemyIsClone)
    ///
    /// Two-phase pattern:
    /// - Phase 1 (Update): scan all active enemies, identify triggers, collect clone events
    /// - Phase 2 (serial apply): spawn all clones serially — no parallel writes to ComponentStore
    ///
    /// Clone inheritance:
    /// - Scaled health (CloneHpMult from CloneDef)
    /// - Same damage, speed, armor, shield, path
    /// - Same behavior tree (BT is reference-copied)
    /// - Clone cooldown starts at CloneCooldown from CloneDef
    /// - Max clone count enforced (CloneMaxCount)
    /// - EnemyIsClone = true, EnemyCloneMasterId = masterId
    /// </summary>
    public class EnemyCloneSystem
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;
        private readonly IRenderer renderer;
        private readonly Random _spawnRandom = new Random();

        // Clone events: processed serially to avoid concurrent writes to ComponentStore
        private readonly ConcurrentBag<(int masterId, int playerId, float x, float y, GameConfig.CloneDef def)> _cloneQueue =
            new ConcurrentBag<(int, int, float, float, GameConfig.CloneDef)>();

        public EnemyCloneSystem(ComponentStore store, GameConfig gameConfig, IRenderer renderer)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            this.renderer = renderer;
        }

        /// <summary>
        /// Called once per frame during WavePhase.
        /// Scans all active enemies for clone trigger conditions and queues clone events.
        /// </summary>
        public void Update(float deltaTime)
        {
            var active = store.ActiveEnemyIds;
            int count = active.Count;

            for (int i = 0; i < count; i++)
            {
                int enemyId = active[i];
                if (!store.PositionActive[enemyId]) continue;

                // Clones cannot clone themselves — prevents exponential growth
                if (store.EnemyIsClone[enemyId]) continue;

                int cloneDefId = store.EnemyCloneDefId[enemyId];
                if (cloneDefId < 0) continue;

                // Bounds check
                if (cloneDefId >= gameConfig.CloneDefs.Length) continue;

                GameConfig.CloneDef def = gameConfig.CloneDefs[cloneDefId];
                if (def == null) continue;

                // Check cooldown: skip if clone ability is on cooldown
                float cooldown = store.EnemyCloneCooldown[enemyId];
                if (cooldown > 0f)
                {
                    // Tick down the cooldown
                    store.EnemyCloneCooldown[enemyId] = cooldown - deltaTime;
                    continue;
                }

                // Check max clone count: skip if already at max
                int currentClones = store.EnemyCloneCount[enemyId];
                if (currentClones >= def.MaxClones) continue;

                // Check trigger condition
                bool shouldClone = false;

                if (def.TriggerType == "HP_THRESHOLD")
                {
                    float currentHealth = store.EnemyHealth[enemyId];
                    float maxHealth = store.EnemyMaxHealth[enemyId];
                    if (maxHealth > 0f)
                    {
                        float healthFraction = currentHealth / maxHealth;
                        shouldClone = healthFraction <= def.TriggerValue;
                    }
                }
                else if (def.TriggerType == "TIME")
                {
                    // TIME trigger: fires after a certain number of seconds since spawn
                    // Requires EnemyAgeSeconds tracking — when added, uncomment:
                    // float age = store.EnemyAgeSeconds[enemyId];
                    // shouldClone = age >= def.TriggerValue;
                }

                if (shouldClone)
                {
                    float x = store.PositionX[enemyId];
                    float y = store.PositionY[enemyId];
                    int playerId = 0; // default player 0 (multiplayer out of scope for this feature)
                    _cloneQueue.Add((enemyId, playerId, x, y, def));
                }
            }

            // Apply all queued clones serially
            while (_cloneQueue.TryTake(out var cloneEvent))
            {
                ResolveClone(cloneEvent.masterId, cloneEvent.playerId, cloneEvent.x, cloneEvent.y, cloneEvent.def);
            }
        }

        /// <summary>
        /// Spawn a clone of the given enemy at a jittered nearby position.
        /// Called serially from Update() loop.
        /// </summary>
        private void ResolveClone(int masterId, int playerId, float x, float y, GameConfig.CloneDef def)
        {
            // Read master's stats
            float masterHealth = store.EnemyHealth[masterId];
            float masterMaxHealth = store.EnemyMaxHealth[masterId];
            float masterDamage = store.EnemyDamage[masterId];
            float masterSpeed = store.EnemyMoveSpeed[masterId];
            float masterArmor = store.EnemyArmor[masterId];
            float masterShield = store.EnemyShield[masterId];
            float masterMagicResist = store.EnemyMagicResist[masterId];
            int masterWave = store.EnemyWaveNumber[masterId];
            int masterGold = store.EnemyGoldReward[masterId];
            string masterTypeName = store.EnemyTypeName[masterId];

            // Apply HP multiplier to get clone's health
            float cloneHealth = Math.Max(1f, masterHealth * def.CloneHpMult);
            float cloneMaxHealth = Math.Max(1f, masterMaxHealth * def.CloneHpMult);

            // Jittered spawn position (avoid stacking perfectly)
            float jitterX = (float)(_spawnRandom.NextDouble() * 1.6 - 0.8); // ±0.8 tiles
            float jitterY = (float)(_spawnRandom.NextDouble() * 1.6 - 0.8);
            float spawnX = Math.Clamp(x + jitterX, 0f, 9f);
            float spawnY = y + jitterY; // no Y clamp needed (map extends vertically)

            int childId = store.AddEnemy(
                spawnX, spawnY,
                masterSpeed,
                cloneHealth,
                cloneMaxHealth,
                masterDamage,
                masterGold,
                masterWave,
                $"{masterTypeName}_CLONE",
                masterArmor,
                masterShield,
                masterMagicResist
            );

            if (childId < 0)
            {
                // Entity pool exhausted
                renderer.Log($"[CLONE] Entity pool exhausted, clone of {masterTypeName} (id={masterId}) aborted");
                return;
            }

            // Mark as clone (prevents further cloning)
            store.EnemyIsClone[childId] = true;
            store.EnemyCloneMasterId[childId] = masterId;

            // Inherit behavior tree from master
            store.EnemyBehaviorTree[childId] = store.EnemyBehaviorTree[masterId];

            // Inherit path
            store.EnemyPathId[childId] = store.EnemyPathId[masterId];
            store.EnemyPathNodeIndex[childId] = store.EnemyPathNodeIndex[masterId];

            // Clone does NOT inherit Elite flag — clones are regular enemies
            store.EnemyIsElite[childId] = false;

            // Clone does NOT inherit affixes (no affix flags)
            store.EnemyAffixFlags[childId] = BuffType.None;

            // Clone gets clone capability (so it CAN'T clone again — enforced by EnemyIsClone check)
            store.EnemyCloneDefId[childId] = store.EnemyCloneDefId[masterId];
            store.EnemyCloneCooldown[childId] = 0f; // fresh clone starts with no cooldown (but can't clone due to EnemyIsClone)
            store.EnemyCloneCount[childId] = 0;     // clone's own clone count
            store.EnemyCloneTimer[childId] = def.CloneDuration; // optional duration timer

            // Increment master's clone count
            int currentCount = store.EnemyCloneCount[masterId];
            store.EnemyCloneCount[masterId] = currentCount + 1;

            // Reset master's clone cooldown
            store.EnemyCloneCooldown[masterId] = def.CloneCooldown;

            // Set entity name for debugging
            store.SetEntityName(childId, $"{masterTypeName}_CLONE_M{masterId}");

            if (renderer != null)
            {
                renderer.Log($"[CLONE] {masterTypeName} (id={masterId}) → clone (id={childId}) at ({spawnX:F1}, {spawnY:F1}), HP {cloneHealth:F0}/{cloneMaxHealth:F0}");
            }
        }
    }
}