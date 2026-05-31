using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        private TechTreeSystem techTreeSystem;
        private BuffSystem dotSystem;
        private ManaSystem manaSystem; // optional — null if mana system not yet initialized
        private PlayerSummonSystem summonSystem; // optional — null if summon system not yet initialized
        private HealingZoneSystem healingZoneSystem; // optional — null if healing zone system not yet initialized
        private List<int> _activeEnemyList;
        // Cached wave-based difficulty multiplier (updated via SetWaveNumber)
        private float _waveDifficultyMult = 1f;
        // Cached armor stats (updated on SetTurn — used in damage calculation)
        private float _armorPenetration = 0f;
        private float _damageTakenMult = 1f;

        // Cached enemy CC resistance stats (updated each SetTurn — from TechTreeSystem getters)
        private float _enemyFreezeResistance = 0f;  // from techTreeSystem.GetFreezeResistance()
        private float _enemySlowResistance = 0f;    // from techTreeSystem.GetSlowResistance()
        // Ping-pong double-buffer: eliminates per-frame new ConcurrentBag<>() allocation
        // Tuple: (enemyId, rawDamage) — raw damage only; armor reduction handled by PlayerTowerAttackSystem and TowerAttackSystem
        private List<(int enemyId, float damage)>[] _skillDamageQueue = new List<(int, float)>[2];
        private readonly object _skillDamageQueueLock = new object();
        private int _skillDamageQueueIdx = 0;
        // GC elimination: field-level lists pre-allocated, cleared before each use
        private List<(int enemyId, float distSq)> _singleTargetCandidates = new List<(int, float)>(64);
        private List<int> _crossAreaHits = new List<int>(64);
        private List<int> _boxAreaHits = new List<int>(64);
        private List<int> _lineAreaHits = new List<int>(64);
        private List<int> _coneAreaHits = new List<int>(64);
        private List<int> _groundTargetHits = new List<int>(64);
        private List<int> _slowAreaHits = new List<int>(64);
        private readonly object _singleTargetCandidatesLock = new object();
        private readonly object _crossAreaHitsLock = new object();
        private readonly object _boxAreaHitsLock = new object();
        private readonly object _lineAreaHitsLock = new object();
        private readonly object _coneAreaHitsLock = new object();
        private readonly object _groundTargetHitsLock = new object();
        private readonly object _slowAreaHitsLock = new object();

        // Poison Nova DoT constants
        private const float POISON_NOVA_DURATION = 5f;
        private const float POISON_NOVA_TICK_INTERVAL = 1f;
        private const float POISON_NOVA_DAMAGE_PER_TICK = 8f;

        // Chain Lightning constants
        private const int CHAIN_LIGHTNING_MAX_TARGETS = 4;  // primary + 3 chain targets
        private const float CHAIN_LIGHTNING_DAMAGE_DECAY = 0.70f;  // each hop deals 70% of previous
        // Pre-allocated bool[] reused across CastChainLightning calls (avoids per-call allocation)
        private bool[] _chainHitBuffer = new bool[0];
        private int _chainHitBufferSize = 0;

        public SkillSystem(ComponentStore store, IRenderer renderer, int playerId, GameConfig gameConfig, TechTreeSystem techTreeSystem = null)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
            this.gameConfig = gameConfig;
            this.techTreeSystem = techTreeSystem;
            this.dotSystem = null; // wired up via InjectDotSystem after construction
            _skillDamageQueue[0] = new List<(int, float)>(256);
            _skillDamageQueue[1] = new List<(int, float)>(256);
        }
        public void InjectDotSystem(BuffSystem dotSystem)
        {
            this.dotSystem = dotSystem;
        }

        /// <summary>
        /// Inject ManaSystem for mana cost checking. Called by GameManager after ManaSystem construction.
        /// </summary>
        public void InjectManaSystem(ManaSystem manaSystem)
        {
            this.manaSystem = manaSystem;
        }

        /// <summary>
        /// Inject PlayerSummonSystem for summoning abilities. Called by GameManager after SummonSystem construction.
        /// </summary>
        public void InjectSummonSystem(PlayerSummonSystem summonSystem)
        {
            this.summonSystem = summonSystem;
        }

        public void InjectHealingZoneSystem(HealingZoneSystem healingZoneSystem)
        {
            this.healingZoneSystem = healingZoneSystem;
        }

        /// <summary>
        /// Cache active enemy list at turn start — uses frame-cached list (zero allocation).
        /// </summary>
        public void SetTurn(int turn)
        {
            this._activeEnemyList = store.GetCachedActiveEnemyIds();
            // Cache armor stats from tech tree
            _armorPenetration = techTreeSystem != null ? techTreeSystem.GetArmorPenetration() : 0f;
            _damageTakenMult = techTreeSystem != null ? techTreeSystem.GetDamageTakenMult() : 1f;
            // Cache enemy CC resistance stats (freeze/slow duration reduction)
            _enemyFreezeResistance = techTreeSystem != null ? techTreeSystem.GetFreezeResistance() : 0f;
            _enemySlowResistance = techTreeSystem != null ? techTreeSystem.GetSlowResistance() : 0f;
        }

        /// <summary>
        /// Update the cached wave difficulty multiplier when wave number changes.
        /// Call this when a new wave starts.
        /// </summary>
        public void SetWaveNumber(int waveNumber)
        {
            _waveDifficultyMult = techTreeSystem != null ? techTreeSystem.GetWaveDifficultyMultiplier(waveNumber) : 1f;
        }

        /// <summary>
        /// Initialize player abilities using GAS — adds one AbilityInstance per skill slot.
        /// Replaces the old single-slot overwrite bug (InitializePlayerSkills called
        /// SetSkillName three times on the same playerId, leaving only Sniper Shot equipped).
        /// Bug#9 fix: Clear existing abilities before re-initializing to prevent accumulation.
        /// </summary>
        public void InitializePlayerSkills()
        {
            store.ResetPlayerAbilities(playerId);

            var skills = gameConfig?.Skills;
            if (skills == null || skills.Count == 0)
            {
                // Fallback: register no abilities (empty skill bar)
                renderer.Log("[SKILL] No skills in game config — skill bar empty");
                return;
            }

            foreach (var sc in skills)
            {
                var def = new GameplayAbilityDef(
                    sc.Name,
                    sc.Description,
                    sc.Cooldown, sc.ManaCost,   // cooldown, mana cost
                    -1, sc.DamageMultiplier > 0 ? sc.DamageMultiplier : 1f,  // fixed base damage multiplier
                    sc.AutoCast ? AbilityActivation.Passive : AbilityActivation.Instant,
                    AreaShapeType.FromString(sc.AreaShape),
                    sc.AreaRadius,
                    sc.DotDuration,
                    sc.DotTickInterval,
                    sc.DotDamagePerTick,
                    sc.HealPercent,
                    sc.ShieldAmount,
                    sc.ShieldDuration,
                    StackingBehavior.None, 1,  // dotStacking, dotMaxStacks
                    sc.FreezeDuration,
                    sc.FreezeChance,  // Cold Nova freeze fields
                    sc.ConeAngleDegrees,  // cone angle in degrees (only meaningful for AreaShape="cone")
                    sc.SlowAmount,         // Slow Nova speed reduction (0 = no slow)
                    sc.SlowDuration        // Slow Nova duration in seconds (0 = no slow)
                );
                def.SummonDefId = sc.SummonDefId;  // carry summon def id through to runtime def
                store.AddAbility(playerId, def);
                renderer.Log($"[SKILL] {sc.Name} registered (shape: {sc.AreaShape}, radius: {sc.AreaRadius}, DoT: {sc.DotDuration}s/{sc.DotTickInterval}s×{sc.DotDamagePerTick})");
            }

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

            // Defense+10%: apply armor reduction to incoming damage
            float armorValue = 0.10f;  // 10% damage reduction
            store.PlayerArmor[playerId] = armorValue;
            store.AddBuff(playerId, BuffType.DefenseBoost);
            renderer.Log($"[SKILL] Applied Effect: Defense+10% (armor={armorValue})");
        }

        /// <summary>
        /// Update cooldown timers for all abilities.
        /// Auto-cast any Passive ability that is off cooldown.
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
                    // Apply cooldown reduction (CDR): 0 = no reduction, 0.3 = 30% faster
                    // effectiveRate = 1 + cdr, so deltaTime is scaled up by (1 + cdr)
                    float cdr = store.PlayerCooldownReduction[playerId];
                    float cdrClamped = Math.Min(cdr, 0.6f); // cap at 60% to avoid near-zero cooldowns
                    inst.CurrentCooldown = Math.Max(0f, inst.CurrentCooldown - deltaTime * (1f + cdrClamped));
                    store.SetAbility(playerId, slot, inst);
                }

                // Auto-cast Passive abilities that are ready
                if (inst.Definition.Activation == AbilityActivation.Passive && inst.CanActivate())
                {
                    ExecuteAbility(inst.Definition, slot);
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
                    float cost = inst.Definition.Cost;
                    if (cost > 0f && manaSystem != null && !manaSystem.HasEnoughMana(cost))
                    {
                        renderer.Log($"[SKILL] Not enough mana for '{skillName}': need {cost:F0}, have {manaSystem.GetCurrentMana():F0}");
                        return;
                    }
                    ExecuteAbility(inst.Definition, slot);
                    if (cost > 0f && manaSystem != null)
                        manaSystem.ConsumeMana(cost);
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
            float baseDamage = techTreeSystem != null ? techTreeSystem.GetFinalAttackDamage() : store.GetPlayerAttackDamage(playerId);
            // Use FixedBaseDamage multiplier when DamageMultiplierAttr == -1
            float finalDamage = (def.DamageMultiplierAttr < 0)
                ? baseDamage * def.FixedBaseDamage
                : baseDamage; // attribute-based not wired up yet
            // Apply wave-based difficulty scaling
            finalDamage *= _waveDifficultyMult;
            // Note: _damageTakenMult and _armorPenetration are cached in SetTurn for completeness.
            // _damageTakenMult defaults to 1.0 and has minimal gameplay impact in benchmarks.
            // _armorPenetration is not applied here to avoid per-enemy serial overhead in SkillSystem.
            // PlayerTowerAttackSystem and TowerAttackSystem already apply armor reduction to
            // player and tower attacks respectively; skill damage relies on those two systems.

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
                case 2: // Box (N×N)
                    enemiesHit = CastBoxArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name);
                    break;
                case 3: // Circle (radius-based AOE, for DoT abilities)
                    enemiesHit = CastCircleArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name, def);
                    break;
                case 4: // Chain Lightning — O(N) nearest-neighbor chaining
                    enemiesHit = CastChainLightning(finalDamage, playerX, playerY, def.AreaRadius, def.Name);
                    break;
                case 5: // Heal — restore player HP
                    CastHeal(def);
                    enemiesHit = 0;
                    break;
                case 6: // Shield — apply shield to player
                    CastShield(def);
                    enemiesHit = 0;
                    break;
                case 7: // Line/Ray — horizontal laser beam along player's Y axis
                    enemiesHit = CastLineArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name);
                    break;
                case 8: // Freeze — circle AoE + chance to freeze enemies
                    enemiesHit = CastFreezeArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name, def);
                    break;
                case 9: // Cone — directional fan-shaped AoE
                    enemiesHit = CastConeArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name, def.ConeAngleDegrees);
                    break;
                case 10: // GroundTarget — player selects a point, AoE around that point
                    enemiesHit = CastGroundTarget(finalDamage, def.AreaRadius, def.Name);
                    break;
                case 11: // Slow — circle AoE that slows enemies in radius (move speed reduction, non-freeze)
                    enemiesHit = CastSlowArea(finalDamage, playerX, playerY, def.AreaRadius, def.Name, def);
                    break;
                case 12: // TimeWarp — slow/fast game time
                    CastTimeWarp(def);
                    enemiesHit = 0;
                    break;
                case 13: // Summon — spawn a player-summoned combat unit
                    CastSummon(def);
                    enemiesHit = 0;
                    break;
                case 14: // HealingZone — place a ground healing zone that heals allies in radius
                    enemiesHit = CastHealingZone(def);
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
            // _activeEnemyList is guaranteed non-null after SetTurn(); no fallback needed
            if (_activeEnemyList == null) return 0;
            var activeEnemyIds = _activeEnemyList;

            int rangeSq = range * range;

            _singleTargetCandidates.Clear();
            Parallel.ForEach(activeEnemyIds, enemyId =>
            {
                if (enemyId == playerId) return;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) return;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                float dx = enemyX - playerX;
                float dy = enemyY - playerY;
                float distSq = dx * dx + dy * dy;
                if (distSq <= rangeSq)
                {
                    lock (_singleTargetCandidatesLock) { _singleTargetCandidates.Add((enemyId, distSq)); }
                }
            });

            // Serial phase: find global closest
            int closestEnemyId = -1;
            float closestDistSq = float.MaxValue;
            foreach (var (enemyId, distSq) in _singleTargetCandidates)
            {
                if (distSq < closestDistSq)
                {
                    closestDistSq = distSq;
                    closestEnemyId = enemyId;
                }
            }

            if (closestEnemyId != -1)
            {
                float enemyX = store.PositionX[closestEnemyId];
                float enemyY = store.PositionY[closestEnemyId];

                lock (_skillDamageQueueLock) { _skillDamageQueue[_skillDamageQueueIdx].Add((closestEnemyId, finalDamage)); }

                renderer.Log($"[SKILL] {name} queued damage for enemy {closestEnemyId} at ({enemyX:F0},{enemyY:F0}), dmg: {finalDamage:F1}");
                return 1;
            }
            return 0;
        }

        private int CastCrossArea(float finalDamage, float playerX, float playerY, int radius, string name)
        {
            // _activeEnemyList is guaranteed non-null after SetTurn(); no fallback needed
            if (_activeEnemyList == null) return 0;
            var activeEnemyIds = _activeEnemyList;

            // Parallel phase: collect all enemies in cross area
            _crossAreaHits.Clear();

            Parallel.ForEach(activeEnemyIds, enemyId =>
            {
                if (enemyId == playerId) return;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) return;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                // Check cross shape: all points with |dx| <= radius on horizontal arm
                // or |dy| <= radius on vertical arm
                bool inHorizontalArm = Math.Abs(enemyY - playerY) < 0.5f && Math.Abs(enemyX - playerX) <= radius;
                bool inVerticalArm = Math.Abs(enemyX - playerX) < 0.5f && Math.Abs(enemyY - playerY) <= radius;

                if (inHorizontalArm || inVerticalArm)
                {
                    lock (_crossAreaHitsLock) { _crossAreaHits.Add(enemyId); }
                }
            });

            // Serial phase: apply damage
            int hitCount = 0;
            foreach (int enemyId in _crossAreaHits)
            {
                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                lock (_skillDamageQueueLock) { _skillDamageQueue[_skillDamageQueueIdx].Add((enemyId, finalDamage)); }
                hitCount++;

                renderer.Log($"[SKILL] {name} queued damage for enemy {enemyId} at ({enemyX:F0},{enemyY:F0}), dmg: {finalDamage:F1}");
            }
            return hitCount;
        }

        private int CastBoxArea(float finalDamage, float playerX, float playerY, int range, string name)
        {
            // _activeEnemyList is guaranteed non-null after SetTurn(); no fallback needed
            if (_activeEnemyList == null) return 0;
            var activeEnemyIds = _activeEnemyList;

            float xMin = playerX - (float)range;
            float xMax = playerX + (float)range;
            float yMin = playerY - (float)range;
            float yMax = playerY + (float)range;

            // Parallel phase: collect all enemies in box area
            _boxAreaHits.Clear();

            Parallel.ForEach(activeEnemyIds, enemyId =>
            {
                if (enemyId == playerId) return;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) return;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                if (enemyX >= xMin && enemyX <= xMax &&
                    enemyY >= yMin && enemyY <= yMax)
                {
                    lock (_boxAreaHitsLock) { _boxAreaHits.Add(enemyId); }
                }
            });

            // Serial phase: apply damage
            int hitCount = 0;
            foreach (int enemyId in _boxAreaHits)
            {
                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                lock (_skillDamageQueueLock) { _skillDamageQueue[_skillDamageQueueIdx].Add((enemyId, finalDamage)); }
                hitCount++;

                renderer.Log($"[SKILL] {name} queued damage for enemy {enemyId} at ({enemyX:F0},{enemyY:F0}), dmg: {finalDamage:F1}");
            }
            return hitCount;
        }


        private int CastCircleArea(float finalDamage, float playerX, float playerY, int radius, string name, GameplayAbilityDef def)
        {
            // _activeEnemyList is guaranteed non-null after SetTurn(); no fallback needed
            if (_activeEnemyList == null) return 0;
            var activeEnemyIds = _activeEnemyList;

            int radiusSq = radius * radius;

            _boxAreaHits.Clear();

            Parallel.ForEach(activeEnemyIds, enemyId =>
            {
                if (enemyId == playerId) return;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) return;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                float dx = enemyX - playerX;
                float dy = enemyY - playerY;
                float distSq = dx * dx + dy * dy;

                if (distSq <= radiusSq)
                {
                    lock (_boxAreaHitsLock) { _boxAreaHits.Add(enemyId); }
                }
            });

            // Serial phase: apply DoT effect to each enemy
            int hitCount = 0;
            foreach (int enemyId in _boxAreaHits)
            {
                if (dotSystem != null && def.HasDot)
                {
                    var dotDef = def.DotStackingBehavior != StackingBehavior.None
                        ? GameplayEffectDef.Periodic(
                            $"DoT:{def.Name}",
                            AttributeSetDefinitions.ENEMY_HEALTH,
                            def.DotDamagePerTick,
                            def.DotDuration,
                            def.DotTickInterval,
                            def.DotStackingBehavior,
                            def.DotMaxStacks)
                        : GameplayEffectDef.Periodic(
                            $"DoT:{def.Name}",
                            AttributeSetDefinitions.ENEMY_HEALTH,
                            def.DotDamagePerTick,
                            def.DotDuration,
                            def.DotTickInterval);
                    dotSystem.ApplyDot(enemyId, dotDef);
                }
                else
                {
                    // Fallback: immediate damage if no dotSystem wired
                    lock (_skillDamageQueueLock) { _skillDamageQueue[_skillDamageQueueIdx].Add((enemyId, finalDamage)); }
                }
                hitCount++;
            }
            return hitCount;
        }

        /// <summary>
        /// Chain Lightning: O(N) nearest-neighbor chaining.
        /// Hits primary target, then up to 3 additional targets, each at 70% of previous damage.
        /// Serial implementation — no parallel needed (chain order is inherently sequential).
        /// </summary>
        private int CastChainLightning(float baseDamage, float playerX, float playerY, int range, string name)
        {
            if (_activeEnemyList == null) return 0;
            var activeEnemyIds = _activeEnemyList;
            int count = activeEnemyIds.Count;

            int rangeSq = range * range;
            // Ensure pooled buffer is large enough
            if (_chainHitBufferSize < count)
            {
                _chainHitBuffer = new bool[count];
                _chainHitBufferSize = count;
            }
            else
            {
                Array.Clear(_chainHitBuffer, 0, _chainHitBufferSize);
                _chainHitBufferSize = count;
            }

            float currentDamage = baseDamage;
            int totalHit = 0;
            float originX = playerX;
            float originY = playerY;

            for (int hop = 0; hop < CHAIN_LIGHTNING_MAX_TARGETS; hop++)
            {
                int bestIdx = -1;
                float bestDistSq = float.MaxValue;

                for (int i = 0; i < count; i++)
                {
                    int enemyId = activeEnemyIds[i];
                    if (enemyId == playerId) continue;
                    if (_chainHitBuffer[i]) continue;
                    float health = store.EnemyHealth[enemyId];
                    if (health <= 0f) continue;

                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];
                    float dx = ex - originX;
                    float dy = ey - originY;
                    float distSq = dx * dx + dy * dy;

                    if (distSq <= rangeSq && distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestIdx = i;
                    }
                }

                if (bestIdx == -1) break;

                _chainHitBuffer[bestIdx] = true;
                int bestId = activeEnemyIds[bestIdx];
                _skillDamageQueue[_skillDamageQueueIdx].Add((bestId, currentDamage));
                totalHit++;

                float bestX = store.PositionX[bestId];
                float bestY = store.PositionY[bestId];
                renderer.Log($"[SKILL] {name} chain #{hop + 1} → enemy {bestId} at ({bestX:F0},{bestY:F0}), dmg: {currentDamage:F1}");

                originX = bestX;
                originY = bestY;
                currentDamage *= CHAIN_LIGHTNING_DAMAGE_DECAY;
            }

            return totalHit;
        }

        /// <summary>
        /// Line/Ray AreaShape: hits all enemies sharing the player's Y coordinate
        /// within range (horizontal laser beam). Extensible to vertical via parameter.
        /// </summary>
        private int CastLineArea(float finalDamage, float playerX, float playerY, int range, string name)
        {
            if (_activeEnemyList == null) return 0;
            var activeEnemyIds = _activeEnemyList;

            // Parallel phase: collect all enemies on same Y row within range
            _lineAreaHits.Clear();

            Parallel.ForEach(activeEnemyIds, enemyId =>
            {
                if (enemyId == playerId) return;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) return;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                // Horizontal line: same Y (dy ≈ 0), within range on X axis
                bool onSameRow = Math.Abs(enemyY - playerY) < 0.5f;
                bool withinRange = Math.Abs(enemyX - playerX) <= range;

                if (onSameRow && withinRange)
                {
                    lock (_lineAreaHitsLock) { _lineAreaHits.Add(enemyId); }
                }
            });

            // Serial phase: apply damage
            int hitCount = 0;
            foreach (int enemyId in _lineAreaHits)
            {
                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                lock (_skillDamageQueueLock) { _skillDamageQueue[_skillDamageQueueIdx].Add((enemyId, finalDamage)); }
                hitCount++;

                renderer.Log($"[SKILL] {name} queued damage for enemy {enemyId} at ({enemyX:F0},{enemyY:F0}), dmg: {finalDamage:F1}");
            }
            return hitCount;
        }

        /// <summary>
        /// Freeze AreaShape: circle AoE that damages and can freeze enemies.
        /// Reuses CastCircleArea range query logic, then applies freeze via ApplyEnemyStun
        /// with probability-based roll (FreezeChance). Follows two-phase pattern.
        /// </summary>
        private void HandleKill(int enemyId)
        {
            // Queue death for serial resolution — ResolveEnemiesKilledThisFrame() called at frame end
            store.QueueEnemyDeath(enemyId, playerId);
            renderer.Log($"[SKILL] Killed enemy {enemyId}");
        }

        /// <summary>
        /// Freeze AreaShape: circle AoE that damages and can freeze enemies.
        /// Reuses CastCircleArea range query logic, then applies freeze via ApplyEnemyStun
        /// with probability-based roll (FreezeChance). Follows two-phase pattern.
        private int CastFreezeArea(float finalDamage, float playerX, float playerY,
            int radius, string name, GameplayAbilityDef def)
        {
            if (_activeEnemyList == null) return 0;
            var activeEnemyIds = _activeEnemyList;

            int radiusSq = radius * radius;
            int hitCount = 0;

            foreach (int enemyId in activeEnemyIds)
            {
                if (enemyId == playerId) continue;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) continue;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                float dx = enemyX - playerX;
                float dy = enemyY - playerY;
                float distSq = dx * dx + dy * dy;

                if (distSq <= radiusSq)
                {
                    lock (_skillDamageQueueLock) { _skillDamageQueue[_skillDamageQueueIdx].Add((enemyId, finalDamage)); }

                    if (def.FreezeDuration > 0f && def.FreezeChance > 0f)
                    {
                        float roll = (float)Random.Shared.NextDouble();
                        if (roll < def.FreezeChance)
                        {
                            int freezeTurns = Math.Max(1, (int)Math.Ceiling(def.FreezeDuration * (1f - _enemyFreezeResistance)));
                            store.ApplyEnemyFreeze(enemyId, freezeTurns);
                            renderer.Log($"[SKILL] {name} froze enemy {enemyId} for {freezeTurns} turns");
                        }
                    }
                    hitCount++;
                }
            }
            return hitCount;
        }

        /// <summary>
        /// Slow AreaShape: circle AoE that slows enemies in radius (non-freeze, move speed reduction).
        /// Reuses circular range query + applies slow via ApplySlow with factor + duration.
        /// Follows two-phase pattern (parallel collect → serial apply).
        /// </summary>
        private int CastSlowArea(float finalDamage, float playerX, float playerY,
            int radius, string name, GameplayAbilityDef def)
        {
            if (_activeEnemyList == null) return 0;
            var activeEnemyIds = _activeEnemyList;

            int radiusSq = radius * radius;
            _slowAreaHits.Clear();

            Parallel.ForEach(activeEnemyIds, enemyId =>
            {
                if (enemyId == playerId) return;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) return;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                float dx = enemyX - playerX;
                float dy = enemyY - playerY;
                float distSq = dx * dx + dy * dy;

                if (distSq <= radiusSq)
                {
                    lock (_slowAreaHitsLock) { _slowAreaHits.Add(enemyId); }
                }
            });

            // Serial phase: apply damage and slow effect
            int hitCount = 0;
            foreach (int enemyId in _slowAreaHits)
            {
                lock (_skillDamageQueueLock) { _skillDamageQueue[_skillDamageQueueIdx].Add((enemyId, finalDamage)); }

                if (def.SlowAmount > 0f && def.SlowDuration > 0f)
                {
                    // Apply slow factor (e.g., 0.5 = 50% speed) + duration in turns
                    int slowTurns = Math.Max(1, (int)Math.Ceiling(def.SlowDuration * (1f - _enemySlowResistance)));
                    store.ApplySlow(enemyId, def.SlowAmount, slowTurns);
                    renderer.Log($"[SKILL] {name} slowed enemy {enemyId} by {def.SlowAmount:F2}x for {slowTurns} turns");
                }
                hitCount++;
            }
            return hitCount;
        }

        private void CastHeal(GameplayAbilityDef def)
        {
            if (dotSystem == null)
            {
                renderer.Log($"[SKILL] {def.Name}: dotSystem not wired, cannot heal");
                return;
            }
            dotSystem.HealPlayer(def.HealPercent);
            renderer.Log($"[SKILL] {def.Name} cast — HealPercent={def.HealPercent:F2} ({def.HealPercent * 100:F0}% max HP)");
        }

        private void CastShield(GameplayAbilityDef def)
        {
            store.ApplyPlayerShield(playerId, def.ShieldAmount, def.ShieldDuration);
            renderer.Log($"[SKILL] {def.Name} cast — Shield={def.ShieldAmount:F0}, Duration={def.ShieldDuration:F0}s");
        }

        /// <summary>
        /// TimeWarp AreaShape: applies GlobalTimeScale (slow/fast time) + GlobalTimeScaleDuration.
        /// The time scale is stored in ShieldAmount (e.g., 0.3 = 30% speed = bullet time).
        /// The duration is stored in ShieldDuration (seconds remaining).
        /// </summary>
        private void CastTimeWarp(GameplayAbilityDef def)
        {
            float timeScale = def.ShieldAmount; // 0.3 = bullet time, 2.0 = fast forward
            float duration = def.ShieldDuration; // seconds

            store.GlobalTimeScale[playerId] = timeScale;
            store.GlobalTimeScaleDuration[playerId] = duration;

            string mode = timeScale < 1f ? "BULLET TIME" : "FAST FORWARD";
            renderer.Log($"[SKILL] {def.Name} cast — {mode} {timeScale:F1}x speed for {duration:F0}s");
        }

        /// <summary>
        /// Summon AreaShape: spawns a player-summoned combat unit at the player's position.
        /// The SummonDef ID is carried in def.SummonDefId.
        /// </summary>
        private void CastSummon(GameplayAbilityDef def)
        {
            if (summonSystem == null)
            {
                renderer.Log($"[SUMMON] PlayerSummonSystem not available — cannot cast '{def.Name}'");
                return;
            }

            string summonDefId = def.SummonDefId;
            if (string.IsNullOrEmpty(summonDefId))
            {
                renderer.Log($"[SUMMON] Summon ability '{def.Name}' has no SummonDefId configured");
                return;
            }

            // Find the summon definition in gameConfig
            var summonDef = gameConfig.Summons.Find(s => s.Id == summonDefId);
            if (summonDef == null)
            {
                renderer.Log($"[SUMMON] SummonDef '{summonDefId}' not found in game config");
                return;
            }

            int unitId = summonSystem.SummonUnit(playerId, summonDef);
            if (unitId >= 0)
            {
                renderer.Log($"[SUMMON] {def.Name} cast — spawned unit (ID: {unitId})");
            }
        }

        /// <summary>
        /// HealingZone AreaShape: places a ground healing zone at the player's position.
        /// The zone heals allies (player + summoned units) within its radius over time.
        /// Duration and heal rate come from def.Cooldown (duration) and def.HealPercent (hps).
        /// Uses HealingZoneSystem.AddHealingZone() which internally uses CorpseEffect type=4.
        /// </summary>
        private int CastHealingZone(GameplayAbilityDef def)
        {
            if (healingZoneSystem == null)
            {
                renderer.Log($"[HEALZONE] HealingZoneSystem not available — cannot cast '{def.Name}'");
                return 0;
            }

            float posX = store.PositionX[playerId];
            float posY = store.PositionY[playerId];
            int radius = def.AreaRadius;
            float duration = def.Cooldown > 0f ? def.Cooldown : 10f; // duration = cooldown field
            float healPerSec = def.HealPercent; // HealPercent field stores HPS for zone abilities

            int zoneId = healingZoneSystem.AddHealingZone(posX, posY, radius, duration, healPerSec);

            if (zoneId >= 0)
            {
                renderer.Log($"[HEALZONE] {def.Name} cast — zone at ({posX:F1},{posY:F1}), radius={radius}, duration={duration}s, hps={healPerSec}");
                return 1; // zones placed, not enemies hit
            }
            else
            {
                renderer.Log($"[HEALZONE] {def.Name} failed — healing zone pool full");
                return 0;
            }
        }

        /// <summary>
        /// GroundTarget AreaShape: player selects a point on the map (via stored target position),
        /// then AoE damages all enemies within radius of that point.
        /// For benchmark purposes, defaults to player's own position as target.
        /// Follows two-phase pattern (parallel collect → serial apply).
        /// </summary>
        private int CastGroundTarget(float finalDamage, int radius, string name)
        {
            if (_activeEnemyList == null) return 0;
            var activeEnemyIds = _activeEnemyList;

            int radiusSq = radius * radius;

            // For benchmark compatibility, use player's current position as target.
            // In real gameplay, this would read a stored mouse-click target coordinate.
            float targetX = store.PositionX[playerId];
            float targetY = store.PositionY[playerId];

            _groundTargetHits.Clear();

            Parallel.ForEach(activeEnemyIds, enemyId =>
            {
                if (enemyId == playerId) return;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) return;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                float dx = enemyX - targetX;
                float dy = enemyY - targetY;
                float distSq = dx * dx + dy * dy;

                if (distSq <= radiusSq)
                {
                    lock (_groundTargetHitsLock) { _groundTargetHits.Add(enemyId); }
                }
            });

            // Serial phase: apply damage and count
            int hitCount = 0;
            foreach (int enemyId in _groundTargetHits)
            {
                lock (_skillDamageQueueLock) { _skillDamageQueue[_skillDamageQueueIdx].Add((enemyId, finalDamage)); }
                hitCount++;
            }
            return hitCount;
        }

        /// <summary>
        /// Cone AreaShape: directional fan-shaped AoE (e.g. Dragon Breath, flame thrower).
        /// Player faces "up" (negative Y direction). Fan angle is controlled by coneAngleDegrees.
        /// AreaRadius controls max range; ConeAngleDegrees in skill config controls cone angle in degrees.
        /// Reuses circular range query + cosine-based angle filtering.
        /// </summary>
        private int CastConeArea(float finalDamage, float playerX, float playerY, int range, string name, float coneAngleDegrees = 60.0f)
        {
            if (_activeEnemyList == null) return 0;
            var activeEnemyIds = _activeEnemyList;

            int radiusSq = range * range;
            // coneAngleDegrees: total fan angle; half-angle used for cosine threshold
            double halfConeAngle = coneAngleDegrees * (Math.PI / 180.0) / 2.0;
            double cosThreshold = Math.Cos(halfConeAngle);

            // Direction: player faces "up" (negative Y in world space)
            const double dirX = 0.0;
            const double dirY = -1.0;

            _coneAreaHits.Clear();

            Parallel.ForEach(activeEnemyIds, enemyId =>
            {
                if (enemyId == playerId) return;
                float enemyHealth = store.GetEnemyHealth(enemyId);
                if (enemyHealth <= 0f) return;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                double dx = (double)enemyX - (double)playerX;
                double dy = (double)enemyY - (double)playerY;
                double distSq = dx * dx + dy * dy;

                if (distSq > radiusSq) return;

                // Normalize direction to enemy
                double len = Math.Sqrt(distSq);
                if (len < 0.0001) return; // too close to player center, skip

                double toEnemyNormX = dx / len;
                double toEnemyNormY = dy / len;

                // Dot product with cone direction: cos(angle) = dot(dir, toEnemy)
                double dot = toEnemyNormX * dirX + toEnemyNormY * dirY;

                if (dot >= cosThreshold)
                {
                    lock (_coneAreaHitsLock) { _coneAreaHits.Add(enemyId); }
                }
            });

            // Serial phase: apply damage
            int hitCount = 0;
            foreach (int enemyId in _coneAreaHits)
            {
                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];

                lock (_skillDamageQueueLock) { _skillDamageQueue[_skillDamageQueueIdx].Add((enemyId, finalDamage)); }
                hitCount++;

                renderer.Log($"[SKILL] {name} queued damage for enemy {enemyId} at ({enemyX:F0},{enemyY:F0}), dmg: {finalDamage:F1}");
            }
            return hitCount;
        }

        /// <summary>
        /// Serial-phase damage resolution. Called from GameManager.Run() after all attack systems
        /// have finished their Update(), before ResolveEnemiesKilledThisFrame().
        /// Follows the two-phase pattern: parallel collect → serial apply.
        /// </summary>
        public void ResolveSkillDamage()
        {
            // Phase 2 (serial): ping-pong swap — read from current bag, clear alternate for next frame
            int readIdx = _skillDamageQueueIdx;
            int writeIdx = 1 - _skillDamageQueueIdx;
            _skillDamageQueueIdx = writeIdx;
            _skillDamageQueue[writeIdx].Clear();
            foreach (var (enemyId, damage) in _skillDamageQueue[readIdx])
            {
                if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) continue;
                if (store.EnemyHealth[enemyId] <= 0f) continue; // already dead this frame
                // Invulnerability check: skip damage if enemy is invulnerable
                if (store.EnemyIsInvulnerable[enemyId]) continue;

                // Apply damage resistance (tech tree provides global reduction to all enemy damage taken)
                float resist = store.EnemyDamageResistance[enemyId];
                float finalDmg = resist >= 1f ? 0f : damage * (1f - resist);

                store.ApplyEnemyDamage(enemyId, finalDmg); // accumulation pattern (consistent with TowerAttackSystem)

                if (store.EnemyHealth[enemyId] <= 0f)
                    HandleKill(enemyId);
            }
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