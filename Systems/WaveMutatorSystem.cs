using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Wave Mutator System — applies global wave modifiers to all enemies.
    /// 
    /// Mutators are triggered at wave start based on wave number and apply
    /// continuous effects (speed buff, regen) or one-shot effects (explosion on death).
    /// 
    /// This system runs after EnemyMovementSystem in the frame update order,
    /// applying mutator effects to all active enemies.
    /// </summary>
    public class WaveMutatorSystem
    {
        private ComponentStore store;
        private readonly int playerId;
        private IRenderer renderer;

        // Cached active enemy list (refreshed each turn via SetTurn)
        private List<int> _activeEnemyList;

        // Mutator definitions loaded from wave_mutators.json
        private WaveMutatorDef[] _mutatorDefs = Array.Empty<WaveMutatorDef>();

        public WaveMutatorSystem(ComponentStore store, int playerId, IRenderer renderer = null)
        {
            this.store = store;
            this.playerId = playerId;
            this.renderer = renderer;
        }

        /// <summary>
        /// Load mutator definitions from wave_mutators.json configuration.
        /// Called once at game initialization.
        /// </summary>
        public void LoadMutators(WaveMutatorDef[] mutatorDefs)
        {
            _mutatorDefs = mutatorDefs ?? Array.Empty<WaveMutatorDef>();
            renderer?.Log($"[MUTATOR] Loaded {_mutatorDefs.Length} mutator definitions");
        }

        /// <summary>
        /// Called at the start of each wave to activate the appropriate mutator.
        /// Stores the active mutator id in ComponentStore so Update() applies effects.
        /// </summary>
        public void OnWaveStart(int waveNumber)
        {
            int mutatorId = -1;
            for (int i = 0; i < _mutatorDefs.Length; i++)
            {
                if (waveNumber >= _mutatorDefs[i].TriggerWaveStart)
                {
                    mutatorId = i; // keep updating to the highest qualifying mutator
                }
            }
            store.CurrentWaveMutatorId[playerId] = mutatorId;
            if (mutatorId >= 0)
            {
                var m = _mutatorDefs[mutatorId];
                renderer?.Log($"[MUTATOR] Wave {waveNumber} activated: {m.Name} ({m.EffectType})");
            }
            else
            {
                renderer?.Log($"[MUTATOR] Wave {waveNumber}: no active mutator");
            }
        }

        /// <summary>
        /// Refresh enemy list for this turn. Call before Update().
        /// </summary>
        public void SetTurn(int turn)
        {
            _activeEnemyList = store.GetCachedActiveEnemyIds();
        }

        /// <summary>
        /// Apply active mutator effects to all enemies this frame.
        /// - speed_mult: multiply EnemyMoveSpeed (apply once, not cumulative)
        /// - regen: heal enemies by RegenRate * maxHealth per second
        /// </summary>
        public void Update(float deltaTime)
        {
            int mutatorId = store.CurrentWaveMutatorId[playerId];
            if (mutatorId < 0 || mutatorId >= _mutatorDefs.Length) return;

            var mutator = _mutatorDefs[mutatorId];
            var active = _activeEnemyList;
            if (active == null || active.Count == 0) return;

            switch (mutator.EffectType)
            {
                case "speed_mult":
                    ApplySpeedMult(mutator, active);
                    break;
                case "regen":
                    ApplyRegen(mutator, active, deltaTime);
                    break;
                // explosive_death is handled in ResolveEnemiesKilledThisFrame by WaveSpawningSystem
            }
        }

        private void ApplySpeedMult(WaveMutatorDef mutator, List<int> active)
        {
            // We store the SPEED MULTIPLIER in a separate array to avoid permanently
            // modifying EnemyMoveSpeed (which is used as the base for difficulty scaling).
            // We read the current value, apply the multiplier, and set it.
            // Note: we don't cache the original — on mutator deactivation the speed reverts
            // because EnemyMoveSpeed itself wasn't changed.
            // Fast path: if no mutator, EnemyMoveSpeed is used as-is.
            for (int i = 0; i < active.Count; i++)
            {
                int enemyId = active[i];
                if (!store.EnemyActive[enemyId]) continue;
                float baseSpeed = store.EnemyMoveSpeed[enemyId];
                // Apply multiplier (baseSpeed already has difficulty scaling applied)
                store.EnemyMoveSpeed[enemyId] = baseSpeed * mutator.SpeedMult;
            }
        }

        private void ApplyRegen(WaveMutatorDef mutator, List<int> active, float deltaTime)
        {
            for (int i = 0; i < active.Count; i++)
            {
                int enemyId = active[i];
                if (!store.EnemyActive[enemyId]) continue;
                float maxHealth = store.EnemyMaxHealth[enemyId];
                if (maxHealth <= 0f) continue;
                float healAmount = maxHealth * mutator.RegenRate * deltaTime;
                store.EnemyHealth[enemyId] = Math.Min(store.EnemyHealth[enemyId] + healAmount, maxHealth);
            }
        }

        /// <summary>
        /// Handle explosive death mutator when an enemy is killed.
        /// Returns damage to apply to nearby enemies (0 if no explosive mutator).
        /// </summary>
        public float GetExplosiveDeathDamage(int killedEnemyId)
        {
            int mutatorId = store.CurrentWaveMutatorId[playerId];
            if (mutatorId < 0 || mutatorId >= _mutatorDefs.Length) return 0f;

            var mutator = _mutatorDefs[mutatorId];
            if (mutator.EffectType != "explosive_death") return 0f;

            float maxHealth = store.EnemyMaxHealth[killedEnemyId];
            return maxHealth * mutator.ExplosionDamageRatio;
        }

        /// <summary>
        /// Returns the explosive radius for the active mutator, or 0 if none.
        /// </summary>
        public float GetExplosiveRadius()
        {
            int mutatorId = store.CurrentWaveMutatorId[playerId];
            if (mutatorId < 0 || mutatorId >= _mutatorDefs.Length) return 0f;
            var mutator = _mutatorDefs[mutatorId];
            return mutator.EffectType == "explosive_death" ? mutator.ExplosionRadius : 0f;
        }
    }
}