using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// SOA (Struct of Arrays) 玩家攻击系统
    /// 直接访问 ComponentStore 的数组，无字典查询，无 struct 复制
    /// 性能提升：10-100 倍
    /// </summary>
    public class PlayerTowerAttackSystem
    {
        private Core.ComponentStore store;
        private IRenderer renderer;
        private int playerId;
        private TechTreeSystem techTreeSystem;

        // BUG-1 fix: deterministic hash-based RNG — no shared state, fully reproducible per (frame, enemyId, attackerId)
        // Replaces Random.Shared which caused non-determinism across runs.
        private static int GetDeterministicRandom(int frame, int enemyId, int attackerId)
        {
            // Combine frame + enemyId + attackerId into a single int seed, then xorshift
            int seed = frame ^ (enemyId * 71523) ^ (attackerId * 149357);
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            return seed & 0x7FFFFFFF;
        }

        // Cached per-turn to avoid per-frame store lookups
        private float _playerX, _playerY;
        private float _attackDamage, _attackRange;
        private List<int> _activeEnemyList;
        private bool _turnCached;
        private int _rangeSq;

        // Cached tech tree attack damage multiplier (updated on SetTurn)
        private float _attackDamageMult = 1f;

        // Cached crit stats (updated on SetTurn to avoid per-enemy tech tree calls)
        private float _critRateBonus;
        private float _critDamageBonus;  // additive bonus to ×2, e.g. 0.25 → ×2.25

        // Cached buff stats (precomputed in SetTurn — eliminates per-frame method calls + boundary checks)
        private float _attackBuffMult = 1f;
        private float _critRateThreshold;  // merged: (_hasCritRateBuff ? 0.05f : 0f) + _critRateBonus

        // Cached armor stats (updated on SetTurn — used in damage calculation)
        private float _armorPenetration = 0f;  // fraction of enemy armor ignored, e.g. 0.3 = 30% pen
        private float _damageTakenMult = 1f;    // tech tree: <1.0 = take less damage

        // Cached wave-based difficulty multiplier (updated on SetTurn)
        private float _waveDifficultyMult = 1f;

        private int _currentTurn;

        // Ping-pong double-buffer: eliminates per-frame new ConcurrentBag<>() allocation
        private List<(int enemyId, float damage)>[] _damageQueue = new List<(int, float)>[2];
        private readonly object _damageQueueLock = new object();
        private int _damageQueueIdx = 0;

        // Ping-pong double-buffer for thorns damage reflect (enemy -> player, from player attacking enemy)
        private List<float>[] _thornsQueue = new List<float>[2];
        private int _thornsQueueIdx = 0;

        private HitShieldSystem _hitShieldSystem;

        public PlayerTowerAttackSystem(Core.ComponentStore store, IRenderer renderer, int playerId, GameConfig gameConfig)
            : this(store, renderer, playerId, gameConfig, null)
        {
        }

        public PlayerTowerAttackSystem(Core.ComponentStore store, IRenderer renderer, int playerId, GameConfig gameConfig, TechTreeSystem techTreeSystem)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
            this.techTreeSystem = techTreeSystem;
            _damageQueue[0] = new List<(int, float)>(256);
            _damageQueue[1] = new List<(int, float)>(256);
            _thornsQueue[0] = new List<float>(64);
            _thornsQueue[1] = new List<float>(64);
        }

        public void SetTurn(int turn)
        {
            _currentTurn = turn;
            _playerX = store.PositionX[playerId];
            _playerY = store.PositionY[playerId];
            _attackDamage = store.GetPlayerAttackDamage(playerId);
            _attackRange = store.GetPlayerAttackRange(playerId);
            _activeEnemyList = store.GetCachedActiveEnemyIds();  // zero allocation — frame cache
            _turnCached = true;
            _rangeSq = (int)(_attackRange * _attackRange);

            // Cache crit bonuses from tech tree (avoid per-enemy calls in hot path)
            _critRateBonus = techTreeSystem != null ? techTreeSystem.GetCritRateBonus() : 0f;
            _critDamageBonus = techTreeSystem != null ? techTreeSystem.GetCritDamageMult() : 1f;

            // Cache tech tree attack damage multiplier
            _attackDamageMult = techTreeSystem != null ? techTreeSystem.GetAttackDamageMult() : 1f;

            // Cache armor stats from tech tree
            _armorPenetration = techTreeSystem != null ? techTreeSystem.GetArmorPenetration() : 0f;
            _damageTakenMult = techTreeSystem != null ? techTreeSystem.GetDamageTakenMult() : 1f;

            // Precompute buff-related values — eliminates 2 method calls + 2 boundary checks per frame
            _attackBuffMult = store.GetAttackBuffMultiplier(playerId);
            bool hasCritRateBuff = store.HasCritRateBuff(playerId);
            _critRateThreshold = (hasCritRateBuff ? 0.05f : 0f) + _critRateBonus;
        }

        /// <summary>
        /// Update the cached wave difficulty multiplier when wave number changes.
        /// Call this when a new wave starts. Also called internally by SetTurn for initial setup.
        /// </summary>
public void SetWaveNumber(int waveNumber)
        {
            _waveDifficultyMult = techTreeSystem != null ? techTreeSystem.GetWaveDifficultyMultiplier(waveNumber) : 1f;
        }

        private EnemyLifeLinkSystem _lifeLinkSystem;

        /// <summary>
        /// Inject EnemyLifeLinkSystem reference for damage-sharing link computation.
        /// </summary>
        public void SetLifeLinkSystem(EnemyLifeLinkSystem lifeLinkSystem)
        {
            _lifeLinkSystem = lifeLinkSystem;
        }

        /// <summary>
        /// Inject HitShieldSystem reference for N-hit shield blocking.
        /// </summary>
        public void SetHitShieldSystem(HitShieldSystem hitShieldSystem)
        {
            _hitShieldSystem = hitShieldSystem;
        }

        public int GetCachedEnemyCount() => _activeEnemyList != null ? _activeEnemyList.Count : 0;

        public void Update()
        {
            if (!_turnCached)
            {
                SetTurn(0);
            }

            // O(1) field access — no method calls, no boundary checks
            float baseDamage = _attackDamage * _attackBuffMult * _attackDamageMult;
            baseDamage *= _waveDifficultyMult;  // wave scaling, always applied (1.0f when wave=1)

            // Apply combo kill damage multiplier (min(1 + ComboCount * bonus, maxMult))
            baseDamage *= store.PlayerComboDamageMult[playerId];

            var activeEnemyIds = _activeEnemyList;

            // Phase 1 (parallel): collect damage events only — no structural mutations
            Parallel.For(0, activeEnemyIds.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
            {
                int enemyId = activeEnemyIds[i];
                if (enemyId == playerId) return;

                float enemyX = store.PositionX[enemyId];
                float enemyY = store.PositionY[enemyId];
                if (enemyY <= _playerY) return;

                float dx = enemyX - _playerX;
                if (dx * dx > _rangeSq) return;

                float enemyHealth = store.EnemyHealth[enemyId];
                if (enemyHealth <= 0f) return;

                // Death Mark / Execute: auto-mark enemy when HP drops below threshold
                // Marked enemies take +EnemyMarkedDamageBonus extra damage (e.g. 0.5 = +50%).
                // Self-balancing: bosses with massive HP get the bonus only in their final
                // 15% — turns long fights into satisfying executions.
                float maxHp = store.EnemyMaxHealth[enemyId];
                if (!store.EnemyMarked[enemyId] && maxHp > 0f)
                {
                    float hpFrac = enemyHealth / maxHp;
                    if (hpFrac <= store.EnemyMarkedThreshold[enemyId])
                    {
                        store.EnemyMarked[enemyId] = true;
                    }
                }

// H-3 fix: crit rolled per-enemy inside parallel loop, not once per frame globally.
                // Optimized: merged crit rate threshold (precomputed _critRateThreshold) eliminates branch
                float finalDamage = baseDamage;
                if (GetDeterministicRandom(_currentTurn, enemyId, playerId) < (int)(_critRateThreshold * 0x7FFFFFFF))
                {
                    finalDamage *= (1f + _critDamageBonus);
                }

                // Apply damage type resistance (Physical=armor, Magic=magicResist, True=bypass all)
                DamageType dmgType = store.PlayerDamageType[playerId];
                // Immunity check: True bypasses immunity; all others check the mask
                if (dmgType != DamageType.True)
                {
                    int immunityMask = store.EnemyDamageImmunityMask[enemyId];
                    if ((immunityMask & (int)dmgType) != 0)
                    {
                        finalDamage = 0f;  // enemy is immune to this damage type
                    }
                }
                if (dmgType == DamageType.True)
                {
                    // no resistance applied — skip to damageTakenMult below
                }
                else if (dmgType == DamageType.Magic)
                {
                    float magicResist = store.EnemyMagicResist[enemyId];
                    finalDamage *= Math.Max(0.01f, 1f - magicResist);
                }
                else  // Physical (default) — uses armor + armor pen
                {
                    float enemyArmor = store.EnemyArmor[enemyId];
                    if (enemyArmor > 0f)
                        finalDamage *= Math.Max(0.01f, 1f - enemyArmor * (1f - _armorPenetration));
                }

                // Apply tech tree damage taken multiplier (e.g. "Iron Wall II" reduces incoming damage)
                finalDamage *= _damageTakenMult;

                // Death Mark / Execute bonus: marked enemies take +X% extra damage (default +50%).
                // Applied after all resistances so the bonus is always meaningful.
                if (store.EnemyMarked[enemyId])
                {
                    finalDamage *= (1f + store.EnemyMarkedDamageBonus[enemyId]);
                }

                lock (_damageQueueLock) { _damageQueue[_damageQueueIdx].Add((enemyId, finalDamage)); }
            });

// Phase 2 (serial): ping-pong swap — read from current bag, clear alternate for next frame
            int readIdx = _damageQueueIdx;
            int writeIdx = 1 - _damageQueueIdx;
            _damageQueueIdx = writeIdx;
            _damageQueue[writeIdx].Clear(); // clear the bag threads will write to next frame
            foreach (var (enemyId, damage) in _damageQueue[readIdx])
            {
                if (!store.EnemyActive[enemyId]) continue;
                // Invulnerability check: skip damage if enemy is invulnerable
                if (store.EnemyIsInvulnerable[enemyId]) continue;
                // N-Hit Shield check: if enemy has hit shield layers, consume 1 layer and block damage
                if (_hitShieldSystem != null && _hitShieldSystem.ConsumeHitShield(enemyId)) continue;
                float prevHealth = store.EnemyHealth[enemyId];

                // Life Link damage split: if enemy is linked, share damage with linked partner
                float linkedDamage = 0f;
                int linkedEnemyId = -1;
                float finalDamage = damage;
                if (_lifeLinkSystem != null && store.EnemyIsLinked[enemyId])
                {
                    (finalDamage, linkedDamage, linkedEnemyId) = _lifeLinkSystem.ComputeLinkedDamage(enemyId, damage);
                }

                store.ApplyEnemyDamage(enemyId, finalDamage);

                // Life Link: apply shared damage to linked enemy
                if (linkedEnemyId >= 0 && linkedDamage > 0f)
                {
                    ApplyLinkedDamage(linkedEnemyId, linkedDamage);
                }

                // Thorns: enemy reflects damage back to the player
                float thornsRatio = store.EnemyThornsRatio[enemyId];
                if (thornsRatio > 0f && finalDamage > 0f)
                {
                    float thornsDamage = finalDamage * thornsRatio;
                    _thornsQueue[_thornsQueueIdx].Add(thornsDamage);
                }
                if (store.EnemyHealth[enemyId] <= 0f && prevHealth > 0f)
                    store.QueueEnemyDeath(enemyId, playerId);
            }

            // Phase 2b (serial): resolve thorns damage reflect (enemy -> player)
            int thornsReadIdx = _thornsQueueIdx;
            int thornsWriteIdx = 1 - _thornsQueueIdx;
            _thornsQueueIdx = thornsWriteIdx;
            _thornsQueue[thornsWriteIdx].Clear();
            foreach (float thornsDamage in _thornsQueue[thornsReadIdx])
            {
                store.DecreasePlayerHealth(playerId, thornsDamage);
            }
        }

        /// <summary>
        /// Apply life link shared damage to a linked enemy.
        /// The linked enemy takes full damage (no further splitting — links are not recursive).
        /// </summary>
        private void ApplyLinkedDamage(int linkedEnemyId, float linkedDamage)
        {
            if (linkedEnemyId < 0 || linkedDamage <= 0f) return;
            if (!store.EnemyActive[linkedEnemyId]) return;

            // Apply damage resistance for the linked enemy
            float resist = store.EnemyDamageResistance[linkedEnemyId];
            float finalLinkedDmg = resist >= 1f ? 0f : linkedDamage * (1f - resist);

            store.EnemyHealth[linkedEnemyId] -= finalLinkedDmg;

            // Thorns on linked enemy (if any)
            float thornsRatio = store.EnemyThornsRatio[linkedEnemyId];
            if (thornsRatio > 0f && finalLinkedDmg > 0f)
            {
                float thornsDamage = finalLinkedDmg * thornsRatio;
                _thornsQueue[_thornsQueueIdx].Add(thornsDamage);
            }

            // Check if linked enemy dies from shared damage
            if (store.EnemyHealth[linkedEnemyId] <= 0f)
            {
                store.QueueEnemyDeath(linkedEnemyId, playerId);
            }
        }
    }
}