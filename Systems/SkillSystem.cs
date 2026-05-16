using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Skill system refactored to use the GAS (Gameplay Ability System) architecture.
    /// Skills are stored as AbilityInstances in ComponentStore, one slot per ability.
    /// Casting is driven by the GameplayAbilityDef data (area shape, radius, etc.)
    /// instead of hard-coded string branching.
    /// </summary>
    public class SkillSystem
    {
        private ComponentStore store;
        private IRenderer renderer;
        private int playerId;
        private float deltaTime = 1f;
        private GameConfig gameConfig;
        private List<int> _activeEnemyList;
        // Two-phase pattern: collect damage in parallel phase, resolve in serial phase
        private ConcurrentBag<(int enemyId, float damage)> _skillDamageQueue = new();

        public SkillSystem(ComponentStore store, IRenderer renderer, int playerId, GameConfig gameConfig)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
            this.gameConfig = gameConfig;
        }

        /// <summary>
        /// Cache active enemy list at turn start — eliminates 3× GetAllActiveEnemyIds() allocations per skill cast.
        /// Consistent with PlayerTowerAttackSystem, EnemyAISystem, and EnemyMovementSystem SetTurn patterns.
        /// </summary>
        public void SetTurn(int turn)
        {
            this._activeEnemyList = store.GetAllActiveEnemyIds();
        }

        /// <summary>
        /// Initialize player abilities using GAS — adds one AbilityInstance per skill slot.
        /// Replaces the old single-slot overwrite bug (InitializePlayerSkills called
        /// SetSkillName three times on the same playerId, leaving only Sniper Shot equipped).
        /// Bug#9 fix: Clear existing abilities before re-initializing to prevent accumulation.
        /// </summary>
        public void InitializePlayerSkills()
        {
            // Bug#9: Reset abilities before re-init (game restart scenario)
            store.ResetPlayerAbilities(playerId);

            // Define 3 abilities using GAS structure
            var crossSlashDef = new GameplayAbilityDef(
                "Cross Slash", "400% damage in cross shape",
                5f, 0f,           // cooldown, cost
                -1, 4f,           // no attribute multiplier, fixed 4× base damage
                AbilityActivation.Instant,
                1, 3,             // area shape 1=cross, radius 3
                Array.Empty<int>()
            );
            store.AddAbility(playerId, crossSlashDef);
            renderer.Log("[SKILL] Cross Slash ability registered (cooldown: 5s, cross area radius 3)");

            var megaExplosionDef = new GameplayAbilityDef(
                "Mega Explosion", "3×3 area explosion",
                7f, 0f,
                -1, 3f,
                AbilityActivation.Instant,
                2, 1,             // area shape 2=box (3×3), radius 1
                Array.Empty<int>()
            );
            store.AddAbility(playerId, megaExplosionDef);
            renderer.Log("[SKILL] Mega Explosion ability registered (cooldown: 7s, 3×3 box area)");

            var sniperShotDef = new GameplayAbilityDef(
                "Sniper Shot", "Single target, 9-tile range",
                8f, 0f,
                -1, 6f,
                AbilityActivation.Instant,
                0, 9,             // area shape 0=single target, range 9
                Array.Empty<int>()
            );
            store.AddAbility(playerId, sniperShotDef);
            renderer.Log("[SKILL] Sniper Shot ability registered (cooldown: 8s, single target, range 9)");

            // Apply "Attack+10%" and "Crit Rate+5%" buffs via GameplayEffect
            var attackBoost = new GameplayEffectDef("Attack+10%", EffectType.Instant,
                AttributeSetDefinitions.ATTACK_DAMAGE, AttributeModifierOp.Multiply, 1.1f);
            store.AddEffect(playerId, new AppliedEffect(attackBoost, playerId));
            // Sync to bit flags for O(1) hot-path queries
            store.AddBuff(playerId, BuffType.AttackBoost);
            renderer.Log("[SKILL] Applied Effect: Attack+10% (instant, ×1.1)");

            var critBoost = new GameplayEffectDef("Crit Rate+5%", EffectType.Instant,
                AttributeSetDefinitions.CRIT_RATE, AttributeModifierOp.Add, 0.05f);
            store.AddEffect(playerId, new AppliedEffect(critBoost, playerId));
            // Sync to bit flags for O(1) hot-path queries
            store.AddBuff(playerId, BuffType.CritRateBoost);
            renderer.Log("[SKILL] Applied Effect: Crit Rate+5% (instant, +0.05)");
        }

        /// <summary>
        /// Update cooldown timers for all abilities.
        /// </summary>
        public void Update(float deltaTime)
        {
            this.deltaTime = deltaTime;
            int count = store.AbilityCount[playerId];
            for (int slot = 0; slot < count; slot++)
            {
                var inst = store.GetAbility(playerId, slot);
                if (inst.CurrentCooldown > 0f)
                {
                    inst.CurrentCooldown = Math.Max(0f, inst.CurrentCooldown - deltaTime);
                    store.SetAbility(playerId, slot, inst);
                }
            }
        }

        /// <summary>
        /// Cast a named ability.  Dispatches to the ability's area-shape handler
        /// so no string-based branching is needed per skill type.
        /// </summary>
        public void CastSkill(string skillName)
        {
            int count = store.AbilityCount[playerId];
            for (int slot = 0; slot < count; slot++)
            {
                var inst = store.GetAbility(playerId, slot);
                if (inst.Definition.Name == skillName)
                {
                    if (!inst.CanActivate())
                    {
                        renderer.Log($"[SKILL] '{skillName}' on cooldown: {inst.CurrentCooldown:F1}s remaining (epsilon-consistent via CanActivate())");
                        return;
                    }
                    ExecuteAbility(inst.Definition, slot);
                    return;
                }
            }
            renderer.Log($"[SKILL] Unknown ability: '{skillName}'");
        }

        /// <summary>
        /// Execute an ability by its definition data — area shape drives the damage pattern.
        /// </summary>
        private void ExecuteAbility(GameplayAbilityDef def, int slot)
        {
            float baseDamage = store.GetPlayerAttackDamage(playerId);
            // Use FixedBaseDamage multiplier when DamageMultiplierAttr == -1
            float finalDamage = (def.DamageMultiplierAttr < 0)
                ? baseDamage * def.FixedBaseDamage
                : baseDamage; // attribute-based not wired up yet

            float playerX = store.PositionX[playerId];
            float playerY = store.PositionY[playerId];

            int enemiesHit = 0;

            switch (def.AreaShape)
            {
                case 0: // Single target
                    enemiesHit = CastSingleTarget(finalDamage, playerX, playerY, def.AreaRadius, def.Name);
                    break;
                case 1: // Cross (+) shape
                    enemiesHit = CastCrossArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name);
                    break;
                case 2: // Box (3×3)
                    enemiesHit = CastBoxArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name);
                    break;
                default:
                    renderer.Log($"[SKILL] Unknown area shape {def.AreaShape} for ability '{def.Name}'");
                    return;
            }

            // Start cooldown
            var inst = store.GetAbility(playerId, slot);
            inst.CurrentCooldown = def.Cooldown;
            store.SetAbility(playerId, slot, inst);

            renderer.Log($"[SKILL] {def.Name} cast! Hit {enemiesHit} enemies, cooldown: {def.Cooldown}s");
        }

        private int CastSingleTarget(float finalDamage, float playerX, float playerY, int range, string name)
        {
            int hitCount = 0;
            float closestDistance = float.MaxValue;
            int closestEnemyId = -1;

            var activeEnemyIds = _activeEnemyList ?? store.GetAllActiveEnemyIds();
            if (activeEnemyIds == null) return 0;
            foreach (int enemyId in activeEnemyIds)
            {
                if (enemyId == playerId) continue;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) continue;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                // Bug fix: use correct Euclidean distance (squared, no sqrt needed for comparison)
                float dx = enemyX - playerX;
                float dy = enemyY - playerY;
                float distSq = dx * dx + dy * dy;
                if (distSq < closestDistance && distSq <= range * range)
                {
                    closestDistance = distSq;
                    closestEnemyId = enemyId;
                }
            }

            if (closestEnemyId != -1)
            {
                float enemyX = store.PositionX[closestEnemyId];
                float enemyY = store.PositionY[closestEnemyId];

                _skillDamageQueue.Add((closestEnemyId, finalDamage));
                hitCount = 1;

                renderer.Log($"[SKILL] {name} queued damage for enemy {closestEnemyId} at ({enemyX:F0},{enemyY:F0}), dmg: {finalDamage:F1}");
            }
            return hitCount;
        }

        private int CastCrossArea(float finalDamage, float playerX, float playerY, int radius, string name)
        {
            int hitCount = 0;
            var activeEnemyIds = _activeEnemyList ?? store.GetAllActiveEnemyIds();
            if (activeEnemyIds == null) return 0;

            foreach (int enemyId in activeEnemyIds)
            {
                if (enemyId == playerId) continue;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) continue;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                // Check cross shape: all points with |dx| <= radius on horizontal arm
                // or |dy| <= radius on vertical arm
                bool inHorizontalArm = Math.Abs(enemyY - playerY) < 0.5f && Math.Abs(enemyX - playerX) <= radius;
                bool inVerticalArm = Math.Abs(enemyX - playerX) < 0.5f && Math.Abs(enemyY - playerY) <= radius;

                if (inHorizontalArm || inVerticalArm)
                {
                    _skillDamageQueue.Add((enemyId, finalDamage));
                    hitCount++;

                    renderer.Log($"[SKILL] {name} queued damage for enemy {enemyId} at ({enemyX:F0},{enemyY:F0}), dmg: {finalDamage:F1}");
                }
            }
            return hitCount;
        }

        private int CastBoxArea(float finalDamage, float playerX, float playerY, int range, string name)
        {
            int hitCount = 0;
            var activeEnemyIds = _activeEnemyList ?? store.GetAllActiveEnemyIds();
            if (activeEnemyIds == null) return 0;

            foreach (int enemyId in activeEnemyIds)
            {
                if (enemyId == playerId) continue;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) continue;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                if (enemyX >= playerX - (float)range && enemyX <= playerX + (float)range &&
                    enemyY >= playerY - (float)range && enemyY <= playerY + (float)range)
                {
                    _skillDamageQueue.Add((enemyId, finalDamage));
                    hitCount++;

                    renderer.Log($"[SKILL] {name} queued damage for enemy {enemyId} at ({enemyX:F0},{enemyY:F0}), dmg: {finalDamage:F1}");
                }
            }
            return hitCount;
        }

        private void HandleKill(int enemyId)
        {
            // Queue death for serial resolution — ResolveEnemiesKilledThisFrame() called at frame end
            store.QueueEnemyDeath(enemyId, playerId);
            renderer.Log($"[SKILL] Killed enemy {enemyId}");
        }

        /// <summary>
        /// Serial-phase damage resolution. Called from GameManager.Run() after all attack systems
        /// have finished their Update(), before ResolveEnemiesKilledThisFrame().
        /// Follows the two-phase pattern: parallel collect → serial apply.
        /// </summary>
        public void ResolveSkillDamage()
        {
            foreach (var (enemyId, damage) in _skillDamageQueue)
            {
                if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) continue;
                float currentHealth = store.EnemyHealth[enemyId];
                if (currentHealth <= 0f) continue; // already dead this frame

                float newHealth = Math.Max(0f, currentHealth - damage);
                store.EnemyHealth[enemyId] -= damage; // accumulation pattern (consistent with TowerAttackSystem)

                if (newHealth <= 0f)
                    HandleKill(enemyId);
            }
            // Reset for next frame
            _skillDamageQueue = new ConcurrentBag<(int, float)>();
        }

        /// <summary>
        /// Auto-cast the first available ability (for benchmark compatibility).
        /// </summary>
        public void AutoCastBestSkill()
        {
            int count = store.AbilityCount[playerId];
            for (int slot = 0; slot < count; slot++)
            {
                var inst = store.GetAbility(playerId, slot);
                if (inst.CanActivate())
                {
                    ExecuteAbility(inst.Definition, slot);
                    return;
                }
            }
        }
    }
}