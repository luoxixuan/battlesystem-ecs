using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Nest System — manages enemy spawner nest structures on the map.
    /// 
    /// Design:
    /// - Nests are static structures at fixed positions that periodically spawn minions.
    /// - Each nest has a spawn interval and spawns a monster type on each spawn event.
    /// - Nests have health and can be destroyed by towers.
    /// - Unlike WaveSpawning (time-based), nests produce continuously and are position-based.
    /// - NestSystem.Update() is called from FrameScheduler after WaveSpawning.
    /// </summary>
    public class NestSystem
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;
        private readonly IRenderer renderer;
        private readonly int playerId;

        // Nest definitions from game config (indexed by NestDef.Id)
        private NestDef[] nestDefs;

        // Active nest entity slots: tracks which entity IDs are used for nests
        private int[] nestEntitySlots;
        private int activeNestCount = 0;

        public NestSystem(ComponentStore store, GameConfig gameConfig, IRenderer renderer, int playerId = 0)
        {
            this.store = store;
            this.gameConfig = gameConfig;
            this.renderer = renderer;
            this.playerId = playerId;
            this.nestDefs = gameConfig.NestDefs;
            this.nestEntitySlots = new int[ComponentStore.MAX_ENTITIES];
            for (int i = 0; i < nestEntitySlots.Length; i++) nestEntitySlots[i] = -1;
        }

        /// <summary>
        /// Initialize nests from level config. Called once at game start.
        /// </summary>
        public void Initialize()
        {
            if (gameConfig.NestDefs == null || gameConfig.NestDefs.Length == 0)
                return;

            // Register each nest def as a spawn point (entities are created lazily on first spawn)
            for (int i = 0; i < gameConfig.NestDefs.Length; i++)
            {
                var def = gameConfig.NestDefs[i];
                if (def.MaxAlive > 0 && def.SpawnInterval > 0)
                {
                    // Pre-allocate entity slots for this nest
                    int nestEntityId = store.CreateEntity();
                    if (nestEntityId < 0) continue;

                    nestEntitySlots[nestEntityId] = nestEntityId;
                    store.PositionX[nestEntityId] = def.X;
                    store.PositionY[nestEntityId] = def.Y;
                    store.PositionActive[nestEntityId] = true;
                    store.NestHealth[nestEntityId] = def.MaxHealth;
                    store.NestMaxHealth[nestEntityId] = def.MaxHealth;
                    store.NestSpawnTimer[nestEntityId] = def.SpawnInterval; // spawn immediately
                    store.NestSpawnInterval[nestEntityId] = def.SpawnInterval;
                    // Store monster type as string (NestMonsterType string field)
                    store.NestMonsterTypeStr[nestEntityId] = def.MonsterType;
                    store.NestMaxAlive[nestEntityId] = def.MaxAlive;
                    store.NestActiveCount[nestEntityId] = 0;
                    store.NestDefId[nestEntityId] = i;
                    activeNestCount++;
                }
            }
        }

        /// <summary>
        /// Called at the start of each frame to cache per-turn state.
        /// </summary>
        public void SetTurn(int turn)
        {
            // No per-turn caching needed for nests
        }

        /// <summary>
        /// Update all nests: tick spawn timers and spawn minions when interval elapses.
        /// Also handles nest destruction when health reaches 0.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (activeNestCount == 0) return;

            // Iterate all potential nest entity IDs (sparse scan over activeEnemyIds as proxy)
            // Nests are stored in the enemy array but with NestDefId >= 0
            var activeEnemyIds = store.ActiveEnemyIds;
            for (int i = 0; i < activeEnemyIds.Count; i++)
            {
                int entityId = activeEnemyIds[i];
                if (!store.EnemyActive[entityId]) continue;
                if (store.NestDefId[entityId] < 0) continue; // not a nest

                // Tick spawn timer
                store.NestSpawnTimer[entityId] -= deltaTime;

                // Check if nest is destroyed (health <= 0) — destroy nest entity
                if (store.NestHealth[entityId] <= 0f)
                {
                    DestroyNest(entityId);
                    continue;
                }

                // Spawn minion if timer elapsed and under max alive limit
                if (store.NestSpawnTimer[entityId] <= 0f)
                {
                    bool spawned = TrySpawnMinion(entityId);
                    if (spawned)
                    {
                        store.NestSpawnTimer[entityId] = store.NestSpawnInterval[entityId];
                    }
                }
            }
        }

        /// <summary>
        /// Apply damage to a nest. Called by TowerAttackSystem when a tower attacks a nest.
        /// </summary>
        public void DamageNest(int nestEntityId, float damage)
        {
            if (!store.EnemyActive[nestEntityId]) return;
            if (store.NestDefId[nestEntityId] < 0) return; // not a nest

            store.NestHealth[nestEntityId] -= damage;
            if (store.NestHealth[nestEntityId] < 0f)
                store.NestHealth[nestEntityId] = 0f;
        }

        /// <summary>
        /// Get remaining health for a nest.
        /// </summary>
        public float GetNestHealth(int nestEntityId)
        {
            return store.NestHealth[nestEntityId];
        }

        /// <summary>
        /// Attempt to spawn a minion from the given nest entity.
        /// Returns true if a minion was successfully spawned.
        /// </summary>
        private bool TrySpawnMinion(int nestEntityId)
        {
            int defId = store.NestDefId[nestEntityId];
            if (defId < 0 || defId >= nestDefs.Length) return false;

            var def = nestDefs[defId];

            // Check alive minion count
            if (store.NestActiveCount[nestEntityId] >= def.MaxAlive)
                return false; // max spawned, wait for some to die

            // Look up monster config
            string monsterTypeStr = store.NestMonsterTypeStr[nestEntityId];
            var monsterDef = gameConfig.GetMonsterConfig(monsterTypeStr);
            if (monsterDef == null) return false;

            // Create minion entity
            int minionId = store.CreateEntity();
            if (minionId < 0) return false;

            store.EnemyHealth[minionId] = monsterDef.Health;
            store.EnemyMaxHealth[minionId] = monsterDef.Health;
            store.EnemyMoveSpeed[minionId] = monsterDef.MoveSpeed;
            store.EnemyDamage[minionId] = monsterDef.Damage;
            store.EnemyGoldReward[minionId] = Math.Max(1, monsterDef.GoldReward / 3);
            store.EnemyWaveNumber[minionId] = store.PlayerWaveIndex[playerId];
            store.EnemyActive[minionId] = true;
            store.EnemyTypeName[minionId] = monsterDef.Type;
            store.PositionX[minionId] = store.PositionX[nestEntityId];
            store.PositionY[minionId] = store.PositionY[nestEntityId];
            store.PositionActive[minionId] = true;
            store.SetEntityName(minionId, $"NestMinion_{minionId}");
            store.AddActiveEnemyId(minionId);

            // Increment active count for this nest
            store.NestActiveCount[nestEntityId]++;

            // Track nest origin so we can decrement count when minion dies
            store.NestOriginId[minionId] = nestEntityId;

            renderer.Log($"[NEST] Nest {nestEntityId} spawns minion {minionId} ({monsterDef.Type})");

            return true;
        }

        /// <summary>
        /// Called when a minion dies — decrements the parent nest's active count.
        /// </summary>
        public void OnMinionKilled(int minionId)
        {
            int nestId = store.NestOriginId[minionId];
            if (nestId >= 0 && store.EnemyActive[nestId])
            {
                store.NestActiveCount[nestId] = Math.Max(0, store.NestActiveCount[nestId] - 1);
            }
        }

        private void DestroyNest(int nestEntityId)
        {
            renderer.Log($"[NEST] Nest {nestEntityId} destroyed!");
            store.EnemyActive[nestEntityId] = false;
            store.PositionActive[nestEntityId] = false;
            activeNestCount--;
        }
    }
}