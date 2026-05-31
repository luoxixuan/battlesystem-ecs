#nullable enable
using System;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower Morph / Mode Switch System.
    /// 
    /// Allows towers to switch between two forms (e.g. "attack mode" vs "defense mode")
    /// with different stat profiles, without destroying/recreating the entity.
    /// 
    /// Design:
    /// - Each tower has N morph slots stored as stat snapshots (damage, speed, range).
    /// - Active morph index is stored in TowerCurrentMorph[].
    /// - Switch costs nothing (free toggle) but has a cooldown to prevent spam.
    /// - AttackSystem reads current morph stats directly from live stat fields.
    /// 
    /// Usage: call SwitchMorph(towerId) from player input (BuildPhase UI or hotkey).
    /// </summary>
    public class TowerMorphSystem
    {
        private readonly ComponentStore store;

        public TowerMorphSystem(ComponentStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Attempt to switch the given tower to its alternate morph.
        /// No-op if morph cooldown is active or tower has fewer than 2 morphs.
        /// </summary>
        public void SwitchMorph(int towerId)
        {
            if (!store.TowerActive[towerId]) return;
            if (store.TowerMorphCooldown[towerId] > 0f) return;

            int morphCount = store.TowerMorphCount[towerId];
            if (morphCount < 2) return; // nothing to switch between

            int current = store.TowerCurrentMorph[towerId];
            int next = (current + 1) % morphCount;

            store.TowerCurrentMorph[towerId] = next;

            // Apply the new morph's stat snapshot to live stats
            ApplyMorphStats(towerId, next);

            // Set cooldown (seconds)
            store.TowerMorphCooldown[towerId] = 5.0f; // 5-second cooldown between switches
        }

        /// <summary>
        /// Called each frame to tick down morph cooldowns.
        /// </summary>
        public void Update(float deltaTime)
        {
            var active = store.ActiveTowerIds;
            for (int i = 0; i < active.Count; i++)
            {
                int id = active[i];
                if (store.TowerMorphCooldown[id] > 0f)
                {
                    store.TowerMorphCooldown[id] = Math.Max(0f, store.TowerMorphCooldown[id] - deltaTime);
                }
            }
        }

        /// <summary>
        /// Initialize morph state for a newly placed tower.
        /// Call after AddTower when a tower with morphs is first placed.
        ///</summary>
        public void InitializeMorph(int towerId)
        {
            store.TowerCurrentMorph[towerId] = 0;
            store.TowerMorphCooldown[towerId] = 0f;
            // Apply initial morph 0 stats
            ApplyMorphStats(towerId, 0);
        }

        /// <summary>
        /// Register a tower's morph configuration (number of morphs).
        /// Call during tower placement/creation to define available morphs.
        /// </summary>
        public void RegisterMorphData(int towerId, int morphCount)
        {
            store.TowerMorphCount[towerId] = morphCount;
            store.TowerCurrentMorph[towerId] = 0;
            store.TowerMorphCooldown[towerId] = 0f;
        }

        /// <summary>
        /// Set a stat value for a specific morph of a tower.
        /// Call during tower creation/placement to define morph stat profiles.
        /// </summary>
        public void SetMorphStat(int towerId, int morphIndex, float damage, float attackSpeed, int range)
        {
            EnsureMorphArrays(morphIndex);
            store.TowerMorphDamage[morphIndex][towerId] = damage;
            store.TowerMorphAttackSpeed[morphIndex][towerId] = attackSpeed;
            store.TowerMorphRange[morphIndex][towerId] = range;
        }

        /// <summary>
        /// Lazy-init the jagged array slot for a morph index if still null.
        /// </summary>
        private void EnsureMorphArrays(int morphIndex)
        {
            if (store.TowerMorphDamage[morphIndex] == null)
                store.TowerMorphDamage[morphIndex] = new float[ComponentStore.MAX_ENTITIES];
            if (store.TowerMorphAttackSpeed[morphIndex] == null)
                store.TowerMorphAttackSpeed[morphIndex] = new float[ComponentStore.MAX_ENTITIES];
            if (store.TowerMorphRange[morphIndex] == null)
                store.TowerMorphRange[morphIndex] = new int[ComponentStore.MAX_ENTITIES];
        }

        // ─── Private helpers ───────────────────────────────────────────

        private void ApplyMorphStats(int towerId, int morphIndex)
        {
            store.TowerAttackDamage[towerId] = store.TowerMorphDamage[morphIndex][towerId];
            store.TowerAttackSpeed[towerId]  = store.TowerMorphAttackSpeed[morphIndex][towerId];
            store.TowerRange[towerId]        = store.TowerMorphRange[morphIndex][towerId];
        }
    }
}