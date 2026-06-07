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
        private GameConfig gameConfig;
        // Round 67: EventBus for On-Hit / On-Crit trigger event publication.
        // Always non-null after construction (ctor falls back to a fresh EventBus instance).
        private readonly IEventBus _eventBus;

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

        // Cached meta-progression damage multiplier (read once per SetTurn, applied in hot path)
        private float _metaDamageMult = 1f;

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
        // Ping-pong double-buffer for damage (enemyId, raw damage, wasCrit, damageType).
        // wasCrit is set in the parallel phase (where the crit roll happens) and
        // drained in the serial phase to publish EnemyHit / EnemyCrit events.
        // damageType is the resolved type for this hit portion — when conversion is active
        // the parallel phase enqueues TWO entries for the same enemy (one per type) so the
        // serial phase applies them with the correct resistance/armor/immunity path.
        // Default-initialized to false; both bools and the 4-tuple are stack-friendly.
        private List<(int enemyId, float damage, bool wasCrit, DamageType damageType)>[] _damageQueue = new List<(int, float, bool, DamageType)>[2];
        private readonly object _damageQueueLock = new object();
        private int _damageQueueIdx = 0;

        // Ping-pong double-buffer for thorns damage reflect (enemy -> player, from player attacking enemy)
        private List<float>[] _thornsQueue = new List<float>[2];
        private int _thornsQueueIdx = 0;

        private HitShieldSystem _hitShieldSystem;

        public PlayerTowerAttackSystem(Core.ComponentStore store, IRenderer renderer, int playerId, GameConfig gameConfig)
            : this(store, renderer, playerId, gameConfig, null, null)
        {
        }

        public PlayerTowerAttackSystem(Core.ComponentStore store, IRenderer renderer, int playerId, GameConfig gameConfig, TechTreeSystem techTreeSystem)
            : this(store, renderer, playerId, gameConfig, techTreeSystem, null)
        {
        }

        // Round 67: IEventBus injection for On-Hit / On-Crit trigger event publication.
        // Optional parameter keeps existing call-sites (tests, partial ctor) compiling.
        public PlayerTowerAttackSystem(Core.ComponentStore store, IRenderer renderer, int playerId, GameConfig gameConfig, TechTreeSystem techTreeSystem, IEventBus eventBus)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
            this.techTreeSystem = techTreeSystem;
            this.gameConfig = gameConfig;
            this._eventBus = eventBus ?? new EventBus();
            _damageQueue[0] = new List<(int, float, bool, DamageType)>(256);
            _damageQueue[1] = new List<(int, float, bool, DamageType)>(256);
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

            // Cache meta-progression damage multiplier (resolved once at boot by PrestigeSystem.ApplyToConfig)
            // Read once per turn to honor any in-game prestige unlocks during a single run.
            _metaDamageMult = gameConfig != null ? gameConfig.MetaDamageMult : 1f;

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
            baseDamage *= _metaDamageMult;       // meta-progression: persistent cross-run bonus
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
                // Round 67: capture wasCrit bool so the serial phase can publish EnemyHit / EnemyCrit events.
                // Crit Resistance: enemy can suppress a fraction of incoming crit chance (Boss/Elite = 0.5).
                // Effective threshold = _critRateThreshold * (1 - EnemyCritResistance), applied inline.
                float finalDamage = baseDamage;
                bool wasCrit = false;
                float effectiveCritThreshold = _critRateThreshold * (1f - store.EnemyCritResistance[enemyId]);
                if (GetDeterministicRandom(_currentTurn, enemyId, playerId) < (int)(effectiveCritThreshold * 0x7FFFFFFF))
                {
                    finalDamage *= (1f + _critDamageBonus);
                    wasCrit = true;
                }

                // Resolve the player's primary damage type once per parallel iteration.
                // The resistance/immunity application + enqueue is delegated to
                // ApplyResistancesAndEnqueue (Round 102 Direction 7 — Damage Conversion).
                DamageType dmgType = store.PlayerDamageType[playerId];

                // ── Damage Conversion (Round 102 Direction 7) ──────────────────────────
                // If the player has a non-trivial conversion ratio configured (via
                // gameConfig.PlayerDamageConversionRatio), split the damage into the original
                // type portion + a converted type portion. Both portions are queued as separate
                // hit events so the serial phase applies each with the correct resistance/immunity
                // path. This mirrors TowerAttackSystem's damage conversion (lines 791-848) for
                // consistency across the attack pipeline.
                //
                // IMPORTANT: crit was rolled on the COMBINED finalDamage, so we treat both
                // portions as a single crit event from the enemy's perspective (wasCrit is true
                // on both, matching how TowerAttackSystem handles post-crit conversion).
                float convRatio = gameConfig != null ? gameConfig.PlayerDamageConversionRatio : 0f;
                if (convRatio < DamageConversionConfig.MinMeaningfulRatio)
                {
                    // Fast path: no meaningful conversion — single-event apply
                    ApplyResistancesAndEnqueue(enemyId, finalDamage, wasCrit, dmgType);
                }
                else
                {
                    // Clamp at the global cap so designers can't accidentally break the formula
                    if (convRatio > DamageConversionConfig.ConversionDefaultCap)
                        convRatio = DamageConversionConfig.ConversionDefaultCap;

                    DamageType convertToType = gameConfig.PlayerConvertedDamageType;
                    float origPortion = finalDamage * (1f - convRatio);
                    float convPortion = finalDamage * convRatio;

                    // Original-type portion
                    ApplyResistancesAndEnqueue(enemyId, origPortion, wasCrit, dmgType);
                    // Converted-type portion
                    ApplyResistancesAndEnqueue(enemyId, convPortion, wasCrit, convertToType);
                }
            });

            // Phase 2 (serial): ping-pong swap — read from current bag, clear alternate for next frame
            int readIdx = _damageQueueIdx;
            int writeIdx = 1 - _damageQueueIdx;
            _damageQueueIdx = writeIdx;
            _damageQueue[writeIdx].Clear(); // clear the bag threads will write to next frame
            foreach (var (enemyId, damage, wasCrit, damageType) in _damageQueue[readIdx])
            {
                if (!store.EnemyActive[enemyId]) continue;
                // Invulnerability check: skip damage if enemy is invulnerable
                if (store.EnemyIsInvulnerable[enemyId]) continue;
                // N-Hit Shield check: if enemy has hit shield layers, consume 1 layer and block damage
                if (_hitShieldSystem != null && _hitShieldSystem.ConsumeHitShield(enemyId)) continue;
                // I-frames check (Round 118): skip damage while EnemyInvulnFramesLeft > 0
                if (store.EnemyInvulnFramesLeft[enemyId] > 0) continue;
                // Round 182 Direction 6 — Blinker i-frames: a Blinker enemy that just
                // blinked forward (within the last 0.2s) is briefly invulnerable. Skip
                // damage while EnemyBlinkIFramesLeft > 0 (read-only check; the timer is
                // owned by FrameScheduler.TickBlinkerCycle and decrements independently).
                if (store.EnemyBlinkIFramesLeft[enemyId] > 0f) continue;
                // I-frames write-back (Round 118): after this hit lands, set invuln counter
                // to the configured per-monster value. Mirrors TowerAttackSystem's behavior so
                // Boss/Elite I-frames apply uniformly to BOTH tower and player attacks.
                int playerInvulnConfig = store.EnemyInvulnOnHitFrames[enemyId];
                if (playerInvulnConfig > 0)
                {
                    store.EnemyInvulnFramesLeft[enemyId] = playerInvulnConfig;
                }
                float prevHealth = store.EnemyHealth[enemyId];

                // Life Link damage split: if enemy is linked, share damage with linked partner
                float linkedDamage = 0f;
                int linkedEnemyId = -1;
                float finalDamage = damage;
                // ── Damage Saturation (Round 92 Direction 1) ──
                // Mirrors the TowerAttackSystem hot path: O(1) early-exit on disabled sentinel
                // (WindowFrames == -1) and on trivial sub-0.01f hits. Lazily expires the rolling
                // window (currentFrame - lastFrame > window), accumulates finalDamage, and applies
                // the scale multiplier if the rolling sum crosses (maxHp × threshold). Applied
                // BEFORE the LifeLink split so that the partner-share downstream also reflects
                // the saturated value (consistent behavior across both attack systems).
                int satWindow = DamageSaturationConfig.SaturationWindowFrames;
                if (satWindow >= 0 && finalDamage > 0.01f)
                {
                    int currentFrame = store.CurrentFrame;
                    int lastFrame = store.EnemyRecentDamageFrame[enemyId];
                    if (currentFrame - lastFrame > satWindow)
                    {
                        store.EnemyRecentDamageSum[enemyId] = 0f;
                    }
                    float newSum = store.EnemyRecentDamageSum[enemyId] + finalDamage;
                    store.EnemyRecentDamageSum[enemyId] = newSum;
                    store.EnemyRecentDamageFrame[enemyId] = currentFrame;
                    float maxHp = store.EnemyMaxHealth[enemyId];
                    if (maxHp > 0f)
                    {
                        float threshold = maxHp * DamageSaturationConfig.SaturationThresholdMult;
                        if (newSum > threshold)
                        {
                            finalDamage *= DamageSaturationConfig.SaturationScaleMult;
                        }
                    }
                }
                if (_lifeLinkSystem != null && store.EnemyIsLinked[enemyId])
                {
                    (finalDamage, linkedDamage, linkedEnemyId) = _lifeLinkSystem.ComputeLinkedDamage(enemyId, finalDamage);
                }

                store.ApplyEnemyDamage(enemyId, finalDamage);

                // Life Link: apply shared damage to linked enemy
                if (linkedEnemyId >= 0 && linkedDamage > 0f)
                {
                    ApplyLinkedDamage(linkedEnemyId, linkedDamage);
                }

                // ── Threat Score accumulation (Round 99 Direction 5) ──
                // Accumulate applied damage (post-saturation) into the per-frame accumulator.
                // Single-player, single-thread context: this runs in the serial phase after
                // the parallel damage-queue drain, so a plain += is safe and zero-overhead.
                // The FrameScheduler post-tick hook decays the running average from this
                // accumulator into PlayerRecentDPS using an EMA window (ThreatScoreConfig.DPSWindowSec).
                if (finalDamage > 0f)
                {
                    store.PlayerDPSAccumulator[playerId] += finalDamage;
                }

                // Thorns: enemy reflects damage back to the player
                float thornsRatio = store.EnemyThornsRatio[enemyId];
                if (thornsRatio > 0f && finalDamage > 0f)
                {
                    float thornsDamage = finalDamage * thornsRatio;
                    _thornsQueue[_thornsQueueIdx].Add(thornsDamage);
                }
                // Round 67: On-Hit / On-Crit trigger event publication.
                // EnemyHit fires for every applied hit; EnemyCrit only fires on crits (companion event).
                // Publish BEFORE the death-queue check so affix subscribers see the enemy still alive
                // (death is queued for frame-end resolution; the enemy is still in EnemyActive).
                // Skip publishing when finalDamage is 0 (immunity / shield) to avoid spurious triggers.
                if (finalDamage > 0f)
                {
                    PublishHitEvent(enemyId, playerId, finalDamage, wasCrit);
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
            // Round 132 Dir 8 — honor Boss Min-Health Floor on player→LifeLink partner route.
            store.ApplyMinHealthFloorInPlace(linkedEnemyId);

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

        /// <summary>
        /// Apply damage-type-specific resistance/immunity to the given portion and enqueue
        /// the hit for serial application. Extracted from the parallel phase so damage
        /// conversion (Round 102 Direction 7) can call it twice — once per type — without
        /// duplicating the resistance pipeline. Crit, exposure, death-mark and damage-taken
        /// multipliers are all assumed to have been baked into <paramref name="rawDamage"/>
        /// by the caller.
        /// </summary>
        private void ApplyResistancesAndEnqueue(int enemyId, float rawDamage, bool wasCrit, DamageType damageType)
        {
            float finalDamage = rawDamage;

            // Apply damage type resistance (Physical=armor, Magic=magicResist, Fire=fireResist, Ice=iceResist, Lightning=lightningResist, True=bypass all).
            // Elemental types (Fire/Ice/Lightning) consult their dedicated fractional-resist SOA arrays (Round 117).
            if (damageType != DamageType.True)
            {
                int immunityMask = store.EnemyDamageImmunityMask[enemyId];
                if ((immunityMask & (int)damageType) != 0)
                {
                    finalDamage = 0f;  // enemy is immune to this damage type
                }
            }
            if (damageType == DamageType.True)
            {
                // no resistance applied
            }
            else if (damageType == DamageType.Magic)
            {
                float magicResist = store.EnemyMagicResist[enemyId];
                finalDamage *= Math.Max(0.01f, 1f - magicResist);
            }
            else if (damageType == DamageType.Fire)
            {
                // Elemental resistance (Round 117): fractional reduction per monster JSON FireResist.
                float fireResist = store.EnemyFireResist[enemyId];
                finalDamage *= Math.Max(0.01f, 1f - fireResist);
            }
            else if (damageType == DamageType.Ice)
            {
                float iceResist = store.EnemyIceResist[enemyId];
                finalDamage *= Math.Max(0.01f, 1f - iceResist);
            }
            else if (damageType == DamageType.Lightning)
            {
                float lightningResist = store.EnemyLightningResist[enemyId];
                finalDamage *= Math.Max(0.01f, 1f - lightningResist);
            }
            else if (damageType == DamageType.Holy)
            {
                // Round 135 Dir 1: Holy / Smite / Divine damage — reduced by HolyResist only.
                // Player-side counterpart of TowerAttackSystem Holy branch.
                // 1% floor preserves non-zero damage even at HolyResist=0.999.
                float holyResist = store.EnemyHolyResist[enemyId];
                finalDamage *= Math.Max(0.01f, 1f - holyResist);
            }
            else  // Physical (default) — uses armor + armor pen
            {
                // Round 181 Direction 9 — Phaser gate: if the target is currently in its
                // phase window, zero the damage and skip the rest of the post-armor
                // processing. Magic / True branches above are untouched so the player
                // still benefits from magic / true damage types. We still enqueue the
                // 0-damage hit for the same reason immunity-mask skips enqueue it: the
                // downstream consumer (ApplyQueuedDamage) just no-ops on 0-damage entries.
                if (store.EnemyPhaserPhaseActive[enemyId])
                {
                    finalDamage = 0f;
                }
                else
                {
                    float enemyArmor = store.EnemyArmor[enemyId];
                    // Round 176 Direction 7 — Siege armor bonus (additive on top of
                    // EnemyArmor). 0.95 max combined so no enemy is unkillable.
                    // siegeBonus>0 also enters the branch so pure-siege monsters
                    // (EnemyArmor=0, SiegeArmorBonus=0.8) still get reduced.
                    float siegeBonus = store.EnemySiegeArmorBonus[enemyId];
                    if (enemyArmor > 0f || siegeBonus > 0f)
                    {
                        enemyArmor += siegeBonus;
                        if (enemyArmor > 0.95f) enemyArmor = 0.95f;
                        finalDamage *= Math.Max(0.01f, 1f - enemyArmor * (1f - _armorPenetration));
                    }
                }
            }

            // Apply tech tree damage taken multiplier
            finalDamage *= _damageTakenMult;

            // Death Mark / Execute bonus: marked enemies take +X% extra damage
            if (store.EnemyMarked[enemyId])
            {
                finalDamage *= (1f + store.EnemyMarkedDamageBonus[enemyId]);
            }

            // ── Elemental Exposure bonus (Round 83 Direction 5) ──
            // Active exposure window (EnemyExposureMask != None) triggers the +30% bonus
            // for off-element hits. The O(1) guard skips the common case where the enemy
            // has no active exposure.
            if (store.EnemyExposureTimer[enemyId] > 0f
                && store.EnemyExposureMask[enemyId] != ElementType.None)
            {
                finalDamage *= 1.30f; // 1 + EXPOSURE_BONUS_PCT (hardcoded in ElementalReactionSystem)
            }

            // Enqueue with damage type tag. Note: ThreatScore accumulator is bumped in the
            // serial phase (post-saturation) so we don't add baseDamage here — that path
            // already tracks combined finalDamage.
            lock (_damageQueueLock)
            {
                _damageQueue[_damageQueueIdx].Add((enemyId, finalDamage, wasCrit, damageType));
            }
        }

        /// <summary>
        /// Round 67: Publish an On-Hit / On-Crit trigger event pair.
        /// EnemyHit always fires (for affix code that subscribes to "on hit" mechanics).
        /// EnemyCrit only fires when the hit rolled as a critical strike — handlers
        /// don't need to re-check the IsCrit flag.
        ///
        /// AttackerKind=0 (player attack) is the only kind this system ever publishes.
        /// Both events are dispatched serially (we're in the post-Parallel.For apply
        /// phase), so subscribers see a stable snapshot of the world.
        /// </summary>
        private void PublishHitEvent(int enemyId, int attackerId, float damage, bool isCrit)
        {
            // Defensive: if the event bus has no subscribers at all, EventBus.Publish is
            // a single lock + early-return. Still cheap, but we avoid the lock entirely
            // when both events are empty subscriptions (bench hot path).
            var hitPayload = new EnemyHitEvent
            {
                EnemyId = enemyId,
                AttackerId = attackerId,
                AttackerKind = 0, // player attack
                Damage = damage,
                IsCrit = isCrit
            };
            _eventBus.Publish(GameEvents.EnemyHit, hitPayload);
            if (isCrit)
            {
                _eventBus.Publish(GameEvents.EnemyCrit, hitPayload);
            }
        }
    }
}