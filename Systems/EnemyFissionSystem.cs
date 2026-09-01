using System;
using System.Collections.Generic;
using System.Threading;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Enemy Fission System — implements split-on-death for fission-capable enemies.
    ///
    /// Design:
    /// - Hooks into store.OnEnemyKilled event (serial, safe)
    /// - Looks up FissionDef for the killed enemy
    /// - If generation < MaxGeneration, spawns N children at the death location
    /// - Children inherit fission capability (can fission again if generation allows)
    ///
    /// Two-phase pattern:
    /// - Phase 1 (OnEnemyKilled handler): collect fission events into a serial list
    /// - Phase 2 (Update): spawn all children serially — no parallel writes to ComponentStore
    ///
    /// Spawned children get:
    /// - Scaled health (from parent's current health × HealthScale)
    /// - Scaled damage × DamageScale
    /// - Scaled move speed × SpeedScale
    /// - Scaled gold reward × GoldScale
    /// - Same BT as parent
    /// - generation = parent.generation + 1
    /// </summary>
    public class EnemyFissionSystem
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;
        private readonly IRenderer renderer;
        private readonly Random _spawnRandom = new Random();

        // Fission events collected during OnEnemyKilled — processed serially in Update()
        private readonly List<(int parentId, int playerId, float deathX, float deathY, FissionDef def, int generation)> _fissionQueue =
            new List<(int, int, float, float, FissionDef, int)>();

        public EnemyFissionSystem(ComponentStore store, GameConfig gameConfig, IRenderer renderer)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            this.renderer = renderer;
            store.OnEnemyKilled += OnEnemyKilledHandler;
        }

        /// <summary>
        /// Called when any enemy is killed. Runs serially from ResolveEnemiesKilledThisFrame.
        /// Checks if the enemy can fission and queues the event.
        /// </summary>
        private void OnEnemyKilledHandler(int enemyId, int playerId)
        {
            int fissionDefId = store.EnemyFissionDefId[enemyId];
            if (fissionDefId < 0) return;

            // Bounds check
            if (fissionDefId >= gameConfig.FissionDefs.Length) return;

            FissionDef def = gameConfig.FissionDefs[fissionDefId];
            if (def == null) return;

            int generation = store.EnemyFissionGeneration[enemyId];
            if (generation >= def.MaxGeneration) return;

            // Queue the fission event for serial processing in Update()
            float deathX = store.PositionX[enemyId];
            float deathY = store.PositionY[enemyId];
            _fissionQueue.Add((enemyId, playerId, deathX, deathY, def, generation));
        }

        /// <summary>
        /// Called once per frame (WavePhase, after ResolveEnemiesKilledThisFrame).
        /// Processes all queued fission events — spawning children serially.
        /// </summary>
        public void Update()
        {
            for(int queueIndex=0;queueIndex<_fissionQueue.Count;queueIndex++)
            {
                var evt=_fissionQueue[queueIndex];
                var (parentId, playerId, deathX, deathY, def, generation) = evt;
                ResolveFission(parentId, playerId, deathX, deathY, def, generation);
            }
            _fissionQueue.Clear();
        }

        /// <summary>
        /// Spawns N children at the death location with scaled stats.
        /// </summary>
        private void ResolveFission(int parentId, int playerId, float deathX, float deathY, FissionDef def, int parentGeneration)
        {
            // Parent's current health (what it had when it died, already reduced by damage)
            float parentHealth = store.EnemyHealth[parentId];
            float parentMaxHealth = store.EnemyMaxHealth[parentId];
            float parentDamage = store.EnemyDamage[parentId];
            float parentSpeed = store.EnemyMoveSpeed[parentId];
            int parentWave = store.EnemyWaveNumber[parentId];
            int parentGold = store.EnemyGoldReward[parentId];
            int parentFissionDefId = store.EnemyFissionDefId[parentId];
            string parentTypeName = store.EnemyTypeName[parentId];

            int newGeneration = parentGeneration + 1;

            for (int i = 0; i < def.ChildrenCount; i++)
            {
                // Compute scaled stats
                float childHealth = Math.Max(1f, parentHealth * def.HealthScale);
                float childMaxHealth = Math.Max(1f, parentMaxHealth * def.HealthScale);
                float childDamage = parentDamage * def.DamageScale;
                float childSpeed = parentSpeed * def.SpeedScale;
                int childGold = Math.Max(1, (int)(parentGold * def.GoldScale));

                // Jittered spawn position (avoid stacking perfectly)
                float jitterX = (float)(_spawnRandom.NextDouble() * 1.6 - 0.8); // ±0.8 tiles
                float jitterY = (float)(_spawnRandom.NextDouble() * 1.6 - 0.8);
                float spawnX = deathX + jitterX;
                float spawnY = deathY + jitterY;

                // Clamp to map bounds (0-9 for X, reasonable Y range)
                spawnX = Math.Clamp(spawnX, 0f, 9f);

                int childId = store.AddEnemy(
                    spawnX, spawnY,
                    childSpeed,
                    childHealth,
                    childMaxHealth,
                    childDamage,
                    childGold,
                    parentWave,
                    $"{def.ChildMonsterType}_F{newGeneration}",
                    0f, // armor (children don't inherit armor scaling)
                    0f, // shield
                    0f  // magic resist
                );

                if (childId < 0)
                {
                    // Entity pool exhausted — skip remaining children
                    renderer.Log($"[FISSION] Entity pool exhausted, spawning {def.ChildrenCount - i} children aborted");
                    break;
                }

                // Inherit behavior tree from parent
                store.EnemyBehaviorTree[childId] = store.EnemyBehaviorTree[parentId];

                // Set fission capability
                store.EnemyFissionDefId[childId] = parentFissionDefId;
                store.EnemyFissionGeneration[childId] = newGeneration;

                // Inherit Elite flag (children are not elite even if parent was)
                store.EnemyIsElite[childId] = false;

                // Inherit path
                store.EnemyPathId[childId] = store.EnemyPathId[parentId];
                store.EnemyPathNodeIndex[childId] = store.EnemyPathNodeIndex[parentId];

                // Inherit affixes? (optional — for now, children have no affixes)
                store.EnemyAffixFlags[childId] = BuffType.None;

                // Set entity name for debugging
                store.SetEntityName(childId, $"{def.ChildMonsterType}_F{newGeneration}G{newGeneration}");
            }

            if (renderer != null)
            {
                renderer.Log($"[FISSION] {def.SourceMonsterType} (gen {parentGeneration}) → {def.ChildrenCount}x {def.ChildMonsterType} (gen {newGeneration}) at ({deathX:F1}, {deathY:F1})");
            }
        }
    }
}
