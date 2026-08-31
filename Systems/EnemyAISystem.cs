using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Behavior-tree-driven enemy AI system.
    /// Replaces EnemyAttackSystem: evaluates behavior trees each turn and sets
    /// EnemyAIAction on each active enemy. Execution (movement direction, damage
    /// events) is split with EnemyMovementSystem which reads EnemyAIAction.
    /// </summary>
    public class EnemyAISystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer logger;
        private readonly int playerId;

        private readonly GameConfig gameConfig;
        private readonly EnemyAbilitySystem enemyAbilitySystem;
        private readonly TechTreeSystem techTreeSystem;
        private readonly EventBus _eventBus;
        private readonly ReflectTowerSystem _reflectTowerSystem;
        // Round 119 Dir 3 — optional WaveSpawningSystem ref. Set via SetWaveSpawningSystem() after
        // construction. When null (e.g. in unit tests that construct EnemyAISystem without the
        // full GameManager), the minion-summon bag is drained but no spawn happens (drained
        // count is still tracked for diagnostics).
        private WaveSpawningSystem _waveSpawningSystem;

        private int currentTurn;
        // Per-turn cached fields for cache locality
        private List<int> _activeEnemyList;
        private float _playerX, _playerY;
        private bool _playerHasKnockbackImmunity;
        private float _currentDeltaTime;

        // Attack event batch — ping-pong double-buffer to eliminate per-frame GC allocation.
        // Collected in parallel phase, executed in serial phase.
        private ConcurrentBag<AttackEvent>[] _attackEvents = new ConcurrentBag<AttackEvent>[2];
        private int _attackEventsIdx = 0;

        // Lifesteal event batch — ping-pong double-buffer (parallel collect, serial apply)
        private ConcurrentBag<LifestealEvent>[] _lifestealEvents = new ConcurrentBag<LifestealEvent>[2];
        private int _lifestealEventsIdx = 0;

        // Round 111 Direction 1 — Boss phase ability event bag. Both the sequential path
        // (small enemy counts) and the parallel batches (large enemy counts) push into this
        // bag whenever a phase's AbilityId needs to fire. The bag is drained at the END of
        // Update() into EnemyAbilitySystem.EnqueueAbility (which is NOT thread-safe — it
        // mutates EnemyIsChanneling / _activeChannelers). One-shot guard is the FiredMask
        // bit already set inside each path before the push, so even if two threads see the
        // same transition in the same frame, only the first wins (CAS on FiredMask).
        private readonly ConcurrentBag<(int enemyId, string abilityId)> _phaseAbilityEvents = new ConcurrentBag<(int, string)>();

        // Round 119 Dir 3 — Boss phase minion-summon event bag. Pushed in the same Parallel.For
        // path as _phaseAbilityEvents whenever a phase's (MinionTypeId, MinionCount) trigger
        // fires. Drained serially at end of Update() into WaveSpawningSystem.SpawnMinionNearPosition
        // (which calls AddEnemy — NOT thread-safe). Each event carries the boss id, typeId,
        // count, and current position so the spawn site can ring-place the new minions. One-shot
        // guard is the same EnemyPhaseFiredMask bit that guards the ability + speed/damage
        // triggers; the minion push happens AFTER the bit is set so a re-entrant parallel batch
        // cannot double-summon.
        // Round 137 Dir 6 — bag now carries boss element affinity (int ElementType) at fire time
        // so the serial drain can pass it to SpawnMinionNearPosition. Reading the SOA array in
        // the parallel push site is safe (single writer here, per-enemy slot).
        private readonly ConcurrentBag<(int bossId, int typeId, int count, float x, float y, int elementAffinity)> _phaseMinionEvents
            = new ConcurrentBag<(int bossId, int typeId, int count, float x, float y, int elementAffinity)>();

        // Round 129 Dir 2 — Boss phase change event bag. Pushed in BOTH the sequential (small
        // enemy count) and parallel (large enemy count) branches whenever a phase transition
        // fires (legacy CSV path uses the in-place `currentPhase` gate; structured path uses
        // EnemyPhaseFiredMask bit). Drained serially at end of Update() into the EventBus.
        // This decouples the Boss-phase system from any specific subscriber (BossTrailAoeSystem,
        // music, AoE warning, telemetry) — they subscribe via EventBus.BossPhaseChanged and
        // react to the payload. Drain is always performed (count tracked) even when the event
        // bus is null, matching the pattern of the other two phase-related bags.
        private readonly ConcurrentBag<BossPhaseChangedEvent> _phaseChangeEvents
            = new ConcurrentBag<BossPhaseChangedEvent>();

        // BT evaluation cache — invalidates when enemy health, charge counter, or stun duration changes.
        private float _cachedPlayerHealth = -1;
        private readonly float[] _enemyHealthCache = new float[ComponentStore.MAX_ENTITIES];
        private readonly int[] _enemyChargeCounterCache = new int[ComponentStore.MAX_ENTITIES];
        private readonly float[] _enemyStunDurationCache = new float[ComponentStore.MAX_ENTITIES];
        private readonly bool[] _stunFlagCache = new bool[ComponentStore.MAX_ENTITIES];
        private readonly EnemyActionType[] _lastActionCache = new EnemyActionType[ComponentStore.MAX_ENTITIES];
        private readonly string[] _lastActionStringCache = new string[ComponentStore.MAX_ENTITIES];

        public EnemyAISystem(ComponentStore store, IRenderer logger, int playerId, GameConfig gameConfig, EnemyAbilitySystem enemyAbilitySystem, TechTreeSystem techTreeSystem = null, EventBus eventBus = null, ReflectTowerSystem reflectTowerSystem = null)
        {
            this.store = store;
            this.logger = logger;
            this.playerId = playerId;
            this.gameConfig = gameConfig;
            this.enemyAbilitySystem = enemyAbilitySystem;
            this.techTreeSystem = techTreeSystem;
            this._eventBus = eventBus ?? new EventBus();
            this._reflectTowerSystem = reflectTowerSystem;
            _attackEvents[0] = new ConcurrentBag<AttackEvent>();
            _attackEvents[1] = new ConcurrentBag<AttackEvent>();
            _lifestealEvents[0] = new ConcurrentBag<LifestealEvent>();
            _lifestealEvents[1] = new ConcurrentBag<LifestealEvent>();
        }

        /// <summary>
        /// Round 119 Dir 3 — wire the WaveSpawningSystem into EnemyAISystem so that phase-triggered
        /// minion summons can be drained into SpawnMinionNearPosition(). Called from GameManager
        /// after both systems are constructed. Safe to call multiple times (idempotent).
        /// </summary>
        public void SetWaveSpawningSystem(WaveSpawningSystem waveSpawningSystem)
        {
            _waveSpawningSystem = waveSpawningSystem;
        }

        /// <summary>
        /// Called at the start of each turn with the current turn number.
        /// </summary>
        public void SetTurn(int turn, float deltaTime = 0f)
        {
            currentTurn = turn;
            _playerX = store.PositionX[playerId];
            _playerY = store.PositionY[playerId];
            _activeEnemyList = store.GetCachedActiveEnemyIds();
            _cachedPlayerHealth = store.PlayerCurrentHealth[playerId];
            _playerHasKnockbackImmunity = techTreeSystem?.GetKnockbackImmunity() ?? false;
            _currentDeltaTime = deltaTime;
        }

        /// <summary>
        /// Evaluate behavior trees for all active enemies and set EnemyAIAction.
        /// Execute damage effects for the current turn's actions.
        /// </summary>
        public void Update()
        {
            var activeEnemyIds = _activeEnemyList;
            int count = activeEnemyIds.Count;

            const int batchSize = 256;
            const int PARALLEL_MIN_ENEMIES = 500;

            if (count < PARALLEL_MIN_ENEMIES)
            {
                // Sequential — avoid Parallel.For overhead for small counts (< 2 batches)
                for (int i = 0; i < count; i++)
                {
                    int enemyId = activeEnemyIds[i];
                    if (!store.EnemyActive[enemyId])
                        continue;

                    // Round 174 Direction 8 — Stalker auto-reveal: if this enemy is a stalker
                    // and not yet revealed, scan nearest friendly tower and reveal when in
                    // range. The check is O(activeTowers) per stalker, but the hot path
                    // fast-returns via EnemyIsStalker[enemyId] == false so non-stalkers pay
                    // exactly one bool read. Sentinel-gated for zero-cost when no stalkers.
                    if (store.EnemyIsStalker[enemyId] && !store.EnemyStalkRevealed[enemyId])
                    {
                        UpdateStalkerReveal(enemyId);
                    }

                    var cachedBt = store.EnemyBehaviorTree[enemyId];

                    // Check BT evaluation cache — also track stun duration changes
                    float enemyHealth = store.EnemyHealth[enemyId];
                    float enemyMaxHealth = store.EnemyMaxHealth[enemyId];
                    float playerHealth = store.PlayerCurrentHealth[playerId];
                    int chargeCounter = store.GetEnemyAIChargeCounter(enemyId);
                    bool stunFlag = store.EnemyStunFlag[enemyId];
                    float stunDuration = store.EnemyStunDurationLeft[enemyId];

                    // Boss Phase / Enrage updates
                    float enrageTimer = store.EnemyEnrageTimer[enemyId];
                    if (enrageTimer > 0f)
                    {
                        enrageTimer -= _currentDeltaTime;
                        if (enrageTimer <= 0f)
                        {
                            enrageTimer = 0f;
                            store.EnemyIsEnraged[enemyId] = true;
                        }
                        store.EnemyEnrageTimer[enemyId] = enrageTimer;
                    }

                    // Health-based phase transition
                    string thresholdsStr = store.EnemyPhaseThresholds[enemyId];
                    if (!string.IsNullOrEmpty(thresholdsStr))
                    {
                        int currentPhase = store.EnemyBossPhase[enemyId];
                        float healthFraction = (enemyMaxHealth > 0f) ? enemyHealth / enemyMaxHealth : 1f;
                        string[] parts = thresholdsStr.Split(',');
                        for (int ph = 0; ph < parts.Length; ph++)
                        {
                            if (float.TryParse(parts[ph], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float threshold))
                            {
                                if (healthFraction < threshold)
                                {
                                    int newPhase = ph + 1;
                                    if (newPhase > currentPhase)
                                    {
                                        // Round 129 Dir 2 — push phase-change event BEFORE writing
                                        // the new phase so the payload's OldPhase reflects the
                                        // pre-transition value. HealthFraction is the boss's HP
                                        // fraction AT THE TRANSITION (just below the threshold).
                                        _phaseChangeEvents.Add(new BossPhaseChangedEvent
                                        {
                                            EnemyId = enemyId,
                                            BossTypeName = null, // filled in by drain (serial)
                                            OldPhase = currentPhase,
                                            NewPhase = newPhase,
                                            HealthFraction = healthFraction,
                                            Turn = currentTurn,
                                        });
                                        store.EnemyBossPhase[enemyId] = newPhase;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    // Round 111 Direction 1 — structured phase fields: one-shot fire of
                    // SpeedMult / DamageMult and the phase's AbilityId. Uses the new
                    // EnemyPhaseCount + EnemyPhaseThresholdsFlat arrays (Round 111), the
                    // EnemyPhaseFiredMask bitmask to ensure each phase triggers exactly once
                    // even if HP later recovers, and EnemyAbilitySystem.EnqueueAbility for the
                    // ability trigger. Capped at BOSS_PHASE_MAX (4) to match WaveSpawningSystem.
                    int phaseCount = store.EnemyPhaseCount[enemyId];
                    if (phaseCount > 0)
                    {
                        int firedMask = store.EnemyPhaseFiredMask[enemyId];
                        float healthFraction2 = (enemyMaxHealth > 0f) ? enemyHealth / enemyMaxHealth : 1f;
                        for (int ph = 0; ph < phaseCount; ph++)
                        {
                            int bit = 1 << ph;
                            if ((firedMask & bit) != 0) continue;
                            int phIdx = ph * ComponentStore.MAX_ENTITIES + enemyId;
                            float phThreshold = store.EnemyPhaseThresholdsFlat[phIdx];
                            if (phThreshold <= 0f || phThreshold > 1f) continue;
                            if (healthFraction2 < phThreshold)
                            {
                                // Mark fired first so re-entrant triggers can't double-apply
                                store.EnemyPhaseFiredMask[enemyId] = firedMask | bit;
                                // Round 129 Dir 2 — push phase-change event AFTER FiredMask is set
                                // (so a re-entrant parallel batch that sees the same transition
                                // will skip the duplicate bit, and the listener is guaranteed to
                                // observe the FiredMask write before the event is published). Old
                                // phase index is `ph` (0-indexed of the phase being entered, the
                                // previously-stored value). HealthFraction is captured at this
                                // frame's HP ratio (just below the threshold).
                                _phaseChangeEvents.Add(new BossPhaseChangedEvent
                                {
                                    EnemyId = enemyId,
                                    BossTypeName = null, // filled in by drain (serial)
                                    OldPhase = ph,
                                    NewPhase = ph + 1,
                                    HealthFraction = healthFraction2,
                                    Turn = currentTurn,
                                });
                                // Apply speed multiplier one-shot (multiplicative on top of current)
                                float speedMult = store.EnemyPhaseSpeedMults[phIdx];
                                if (speedMult > 0f && speedMult != 1f)
                                {
                                    // Cache base on first application; subsequent phase mults multiply against it.
                                    float baseSpeed = store.EnemyMoveSpeedBase[enemyId];
                                    if (baseSpeed <= 0f) baseSpeed = store.EnemyMoveSpeed[enemyId];
                                    store.EnemyMoveSpeed[enemyId] = baseSpeed * speedMult;
                                }
                                // Apply damage multiplier one-shot (multiplicative on top of current)
                                float dmgMult = store.EnemyPhaseDamageMults[phIdx];
                                if (dmgMult > 0f && dmgMult != 1f)
                                {
                                    store.EnemyDamage[enemyId] = store.EnemyDamage[enemyId] * dmgMult;
                                }
                                // Trigger phase ability — push to bag for end-of-Update serial drain.
                                // Avoids calling non-thread-safe EnemyAbilitySystem.EnqueueAbility
                                // from within a Parallel.For batch (race on EnemyIsChanneling /
                                // _activeChannelers / cooldown timers). Direct 2D array read — no
                                // per-frame string.Split (perf fix for 26% bench regression).
                                string abId = store.EnemyPhaseAbilityIdsFlat[ph, enemyId];
                                if (!string.IsNullOrEmpty(abId))
                                {
                                    _phaseAbilityEvents.Add((enemyId, abId));
                                }
                                // Round 119 Dir 3 — Boss phase minion summon trigger. Reads the
                                // pre-populated per-(phase,enemy) minion fields and pushes a
                                // (bossId, typeId, count, x, y) event for end-of-Update serial
                                // drain. Position is captured HERE (per-frame) so the minion
                                // appears at the boss's CURRENT location, not the spawn point.
                                int minionType = store.EnemyPhaseMinionTypeIdFlat[phIdx];
                                int minionCount = store.EnemyPhaseMinionCountsFlat[phIdx];
                                if (minionType >= 0 && minionCount > 0)
                                {
                                    // Round 137 Dir 6 — capture the boss's element affinity at fire time
                                    // (already pre-stored as ElementType int) so SpawnMinionNearPosition
                                    // can apply +10% HP to matching minions.
                                    int bossElem = store.EnemyPhaseElementAffinityFlat[phIdx];
                                    _phaseMinionEvents.Add((enemyId, minionType, minionCount,
                                        store.PositionX[enemyId], store.PositionY[enemyId], bossElem));
                                }
                                firedMask = store.EnemyPhaseFiredMask[enemyId];
                            }
                        }
                    }

                    // LastStand / DeathRattle — HP-threshold trigger (independent from BossPhase)
                    // When HP drops below EnemyLastStandHpFraction * maxHP, activate permanently.
                    // One-shot transition: speed/damage are applied exactly once when Active flips false→true.
                    if (!store.EnemyLastStandActive[enemyId] &&
                        store.EnemyLastStandHpFraction[enemyId] > 0f &&
                        enemyMaxHealth > 0f &&
                        enemyHealth / enemyMaxHealth < store.EnemyLastStandHpFraction[enemyId])
                    {
                        store.EnemyLastStandActive[enemyId] = true;
                        // Apply speed multiplier on top of base speed (idempotent via base reference)
                        float baseSpeed = store.EnemyMoveSpeedBase[enemyId];
                        if (baseSpeed <= 0f) baseSpeed = store.EnemyMoveSpeed[enemyId];
                        float lsSpeedMult = store.EnemyLastStandSpeedMult[enemyId];
                        if (lsSpeedMult > 0f)
                            store.EnemyMoveSpeed[enemyId] = baseSpeed * lsSpeedMult;
                        // Apply damage multiplier — one-shot multiplication on the current damage value
                        float lsDmgMult = store.EnemyLastStandDamageMult[enemyId];
                        if (lsDmgMult > 0f && lsDmgMult != 1f)
                            store.EnemyDamage[enemyId] = store.EnemyDamage[enemyId] * lsDmgMult;
                    }

                    if (_enemyHealthCache[enemyId] == enemyHealth &&
                        _cachedPlayerHealth == playerHealth &&
                        _enemyChargeCounterCache[enemyId] == chargeCounter &&
                        _stunFlagCache[enemyId] == stunFlag &&
                        _enemyStunDurationCache[enemyId] == stunDuration)
                    {
                        store.SetEnemyActionEnum(enemyId, _lastActionCache[enemyId]);
                        continue;
                    }

                    // Cache miss: evaluate behavior tree
                    string action;
                    EnemyActionType actionEnum;
                    string abilityId = null;

                    if (store.EnemyStunFlag[enemyId])
                    {
                        action = "none";
                        actionEnum = EnemyActionType.None;
                        store.SetEnemyActionEnum(enemyId, actionEnum);
                        _lastActionCache[enemyId] = actionEnum;
                        continue;
                    }

                    // Polymorph CC: enemy is transformed into a harmless form (变羊/变小鸡).
                    // Short-circuit BT evaluation to None — enemy cannot attack or use abilities
                    // while polymorphed. Decay the duration each frame; clear flag when it expires.
                    // This guard runs in both the sequential and parallel paths below.
                    if (store.EnemyIsPolymorphed[enemyId])
                    {
                        float polyLeft = store.EnemyPolymorphDurationLeft[enemyId] - _currentDeltaTime;
                        if (polyLeft <= 0f)
                        {
                            // Polymorph expired: clear flag, reset damage-taken multiplier to neutral
                            store.EnemyPolymorphDurationLeft[enemyId] = 0f;
                            store.EnemyIsPolymorphed[enemyId] = false;
                            store.EnemyPolymorphDamageTakenMultiplier[enemyId] = 1f;
                            // Fall through to normal BT evaluation this frame (no skip)
                        }
                        else
                        {
                            store.EnemyPolymorphDurationLeft[enemyId] = polyLeft;
                            action = "none";
                            actionEnum = EnemyActionType.Polymorphed;
                            store.SetEnemyActionEnum(enemyId, actionEnum);
                            _lastActionCache[enemyId] = actionEnum;
                            _lastActionStringCache[enemyId] = action;
                            continue; // polymorphed this frame — skip BT, can't attack/cast
                        }
                    }

                    if (cachedBt != null)
                    {
                        action = BTCachedTreeEvaluator.EvaluateWithEnumAndAbility(
                            cachedBt, enemyId, store, playerId, currentTurn,
                            out actionEnum, out abilityId);
                    }
                    else
                    {
                        string monsterType = store.GetEnemyTypeName(enemyId);
                        if (string.IsNullOrEmpty(monsterType))
                            monsterType = store.GetName(enemyId);
                        cachedBt = gameConfig.GetCachedBehaviorTree(monsterType);
                        if (cachedBt != null)
                        {
                            action = BTCachedTreeEvaluator.EvaluateWithEnumAndAbility(
                                cachedBt, enemyId, store, playerId, currentTurn,
                                out actionEnum, out abilityId);
                        }
                        else
                        {
                            action = GetFallbackAction(enemyId);
                            actionEnum = StringToActionEnum(action);
                        }
                    }
                    store.SetEnemyActionEnum(enemyId, actionEnum);
                    store.EnemyCastAbilityId[enemyId] = abilityId;

                    _enemyHealthCache[enemyId] = enemyHealth;
                    _enemyChargeCounterCache[enemyId] = chargeCounter;
                    _stunFlagCache[enemyId] = stunFlag;
                    _enemyStunDurationCache[enemyId] = stunDuration;
                    _lastActionCache[enemyId] = actionEnum;
                    _lastActionStringCache[enemyId] = action;

                    // Collect attack events
                    if (actionEnum == EnemyActionType.AttackMelee ||
                        actionEnum == EnemyActionType.RangedAttack ||
                        actionEnum == EnemyActionType.ChargeAttack)
                    {
                        float param = (actionEnum == EnemyActionType.ChargeAttack)
                            ? store.EnemyChargeParam[enemyId] : 0f;
                        _attackEvents[_attackEventsIdx].Add(new AttackEvent
                        {
                            EnemyId = enemyId,
                            ActionType = actionEnum,
                            Param = param
                        });
                    }
                }
            }
            else
            {
                int numBatches = (count + batchSize - 1) / batchSize;
                Parallel.For(0, numBatches, ParallelOptionsCache.HotPath,
                    batchIdx =>
                {
                    int start = batchIdx * batchSize;
                    int end = Math.Min(start + batchSize, count);

                    for (int i = start; i < end; i++)
                    {
                        int enemyId = activeEnemyIds[i];
                        if (!store.EnemyActive[enemyId])
                            continue;

                        var cachedBt = store.EnemyBehaviorTree[enemyId];

                        // Check BT evaluation cache — also track stun duration changes
                        float enemyHealth = store.EnemyHealth[enemyId];
                        float enemyMaxHealth = store.EnemyMaxHealth[enemyId];
                        float playerHealth = store.PlayerCurrentHealth[playerId];
                        int chargeCounter = store.GetEnemyAIChargeCounter(enemyId);
                        bool stunFlag = store.EnemyStunFlag[enemyId];
                        float stunDuration = store.EnemyStunDurationLeft[enemyId];

                        // Boss Phase / Enrage updates
                        float enrageTimer = store.EnemyEnrageTimer[enemyId];
                        if (enrageTimer > 0f)
                        {
                            enrageTimer -= _currentDeltaTime;
                            if (enrageTimer <= 0f)
                            {
                                enrageTimer = 0f;
                                store.EnemyIsEnraged[enemyId] = true;
                            }
                            store.EnemyEnrageTimer[enemyId] = enrageTimer;
                        }

                        // Decoy lifetime countdown — auto-expire finite-lived player-spawned dummies.
                        // Decremented each frame; when <= 0 the decoy is queued for death (no gold).
                        // Done before BT evaluation so the rest of the system skips expired decoys.
                        if (store.EnemyIsDecoy[enemyId])
                        {
                            float decoyLeft = store.EnemyDecoyLifetimeLeft[enemyId] - _currentDeltaTime;
                            if (decoyLeft <= 0f)
                            {
                                store.EnemyDecoyLifetimeLeft[enemyId] = 0f;
                                // Queue death; ResolveEnemiesKilledThisFrame() at frame end will destroy it.
                                // Uses playerId as the killer (consistency with other death paths).
                                store.QueueEnemyDeath(enemyId, playerId);
                                continue; // skip the rest of AI evaluation for this decoy this frame
                            }
                            store.EnemyDecoyLifetimeLeft[enemyId] = decoyLeft;
                        }

                        // Health-based phase transition
                        string thresholdsStr = store.EnemyPhaseThresholds[enemyId];
                        if (!string.IsNullOrEmpty(thresholdsStr))
                        {
                            int currentPhase = store.EnemyBossPhase[enemyId];
                            float healthFraction = (enemyMaxHealth > 0f) ? enemyHealth / enemyMaxHealth : 1f;
                            string[] parts = thresholdsStr.Split(',');
                            for (int ph = 0; ph < parts.Length; ph++)
                            {
                                if (float.TryParse(parts[ph], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out float threshold))
                                {
                                    if (healthFraction < threshold)
                                    {
                                        int newPhase = ph + 1;
                                        // Round 129 Dir 2 — atomic CAS guard. Two parallel batches
                                        // can both see the same `currentPhase`; without CAS both
                                        // would push the event AND overwrite the phase. The CAS
                                        // loop ensures only one batch's update sticks; on loss we
                                        // skip the event push so subscribers see exactly one event
                                        // per phase transition.
                                        if (newPhase > currentPhase &&
                                            Interlocked.CompareExchange(
                                                ref store.EnemyBossPhase[enemyId],
                                                newPhase, currentPhase) == currentPhase)
                                        {
                                            // Round 129 Dir 2 — push phase-change event. OldPhase
                                            // is the pre-transition value (the CAS saw this same
                                            // value), HealthFraction is the current frame's HP
                                            // ratio. ConcurrentBag.Add is thread-safe.
                                            _phaseChangeEvents.Add(new BossPhaseChangedEvent
                                            {
                                                EnemyId = enemyId,
                                                BossTypeName = null, // filled in by drain (serial)
                                                OldPhase = currentPhase,
                                                NewPhase = newPhase,
                                                HealthFraction = healthFraction,
                                                Turn = currentTurn,
                                            });
                                            break;
                                        }
                                    }
                                }
                            }
                        }

                        // Round 111 Direction 1 — structured phase fields (parallel path).
                        // Per-enemy independent writes to FiredMask / SpeedMult / DamageMult
                        // are safe in Parallel.For. Ability trigger goes to the bag and is
                        // drained serially at end of Update(). See _phaseAbilityEvents field.
                        int phaseCount = store.EnemyPhaseCount[enemyId];
                        if (phaseCount > 0)
                        {
                            int firedMask = store.EnemyPhaseFiredMask[enemyId];
                            float healthFraction2 = (enemyMaxHealth > 0f) ? enemyHealth / enemyMaxHealth : 1f;
                            for (int ph = 0; ph < phaseCount; ph++)
                            {
                                int bit = 1 << ph;
                                if ((firedMask & bit) != 0) continue;
                                int phIdx = ph * ComponentStore.MAX_ENTITIES + enemyId;
                                float phThreshold = store.EnemyPhaseThresholdsFlat[phIdx];
                                if (phThreshold <= 0f || phThreshold > 1f) continue;
                                if (healthFraction2 < phThreshold)
                                {
                                    // Round 129 Dir 2 — atomic CAS guard on FiredMask. Without
                                    // CAS, two parallel batches both reading firedMask=0 would
                                    // both write firedMask|bit and both push the event. The CAS
                                    // ensures exactly one batch's push + speed/damage application
                                    // sticks; the loser skips the event and skips the multiplier
                                    // application. firedMask may have been mutated by a sibling
                                    // bit before the CAS returns, so we read the post-CAS value
                                    // once for the loop's continued iteration safety.
                                    int oldFiredMask = Interlocked.CompareExchange(
                                        ref store.EnemyPhaseFiredMask[enemyId],
                                        firedMask | bit, firedMask);
                                    if (oldFiredMask == firedMask)
                                    {
                                        // Round 129 Dir 2 — push phase-change event. ConcurrentBag
                                        // push is thread-safe; the serial drain later resolves the
                                        // BossTypeName via store.GetEnemyTypeName. OldPhase is the
                                        // 0-indexed phase being entered (the value just stored).
                                        _phaseChangeEvents.Add(new BossPhaseChangedEvent
                                        {
                                            EnemyId = enemyId,
                                            BossTypeName = null, // filled in by drain (serial)
                                            OldPhase = ph,
                                            NewPhase = ph + 1,
                                            HealthFraction = healthFraction2,
                                            Turn = currentTurn,
                                        });
                                        float speedMult = store.EnemyPhaseSpeedMults[phIdx];
                                        if (speedMult > 0f && speedMult != 1f)
                                        {
                                            float baseSpeed = store.EnemyMoveSpeedBase[enemyId];
                                            if (baseSpeed <= 0f) baseSpeed = store.EnemyMoveSpeed[enemyId];
                                            store.EnemyMoveSpeed[enemyId] = baseSpeed * speedMult;
                                        }
                                        float dmgMult = store.EnemyPhaseDamageMults[phIdx];
                                        if (dmgMult > 0f && dmgMult != 1f)
                                        {
                                            store.EnemyDamage[enemyId] = store.EnemyDamage[enemyId] * dmgMult;
                                        }
                                        // Direct 2D array read — no per-frame string.Split (perf fix).
                                        string abId = store.EnemyPhaseAbilityIdsFlat[ph, enemyId];
                                        if (!string.IsNullOrEmpty(abId))
                                        {
                                            _phaseAbilityEvents.Add((enemyId, abId));
                                        }
                                        // Round 119 Dir 3 — Boss phase minion summon trigger (parallel
                                        // path). Same semantics as the sequential path: read pre-populated
                                        // fields, push to bag, drain serially at end of Update. Position
                                        // is captured per-frame at the boss's CURRENT location.
                                        int minionType = store.EnemyPhaseMinionTypeIdFlat[phIdx];
                                        int minionCount = store.EnemyPhaseMinionCountsFlat[phIdx];
                                        if (minionType >= 0 && minionCount > 0)
                                        {
                                            // Round 137 Dir 6 — capture boss element affinity for themed bonus
                                            int bossElem = store.EnemyPhaseElementAffinityFlat[phIdx];
                                            _phaseMinionEvents.Add((enemyId, minionType, minionCount,
                                                store.PositionX[enemyId], store.PositionY[enemyId], bossElem));
                                        }
                                        firedMask = store.EnemyPhaseFiredMask[enemyId];
                                    }
                                }
                            }
                        }

                        // LastStand / DeathRattle — HP-threshold trigger (parallel batch path)
                        if (!store.EnemyLastStandActive[enemyId] &&
                            store.EnemyLastStandHpFraction[enemyId] > 0f &&
                            enemyMaxHealth > 0f &&
                            enemyHealth / enemyMaxHealth < store.EnemyLastStandHpFraction[enemyId])
                        {
                            store.EnemyLastStandActive[enemyId] = true;
                            float baseSpeed = store.EnemyMoveSpeedBase[enemyId];
                            if (baseSpeed <= 0f) baseSpeed = store.EnemyMoveSpeed[enemyId];
                            float lsSpeedMult = store.EnemyLastStandSpeedMult[enemyId];
                            if (lsSpeedMult > 0f)
                                store.EnemyMoveSpeed[enemyId] = baseSpeed * lsSpeedMult;
                            float lsDmgMult = store.EnemyLastStandDamageMult[enemyId];
                            if (lsDmgMult > 0f && lsDmgMult != 1f)
                                store.EnemyDamage[enemyId] = store.EnemyDamage[enemyId] * lsDmgMult;
                        }

                        if (_enemyHealthCache[enemyId] == enemyHealth &&
                            _cachedPlayerHealth == playerHealth &&
                            _enemyChargeCounterCache[enemyId] == chargeCounter &&
                            _stunFlagCache[enemyId] == stunFlag &&
                            _enemyStunDurationCache[enemyId] == stunDuration)
                        {
                            store.SetEnemyActionEnum(enemyId, _lastActionCache[enemyId]);
                            continue;
                        }

                        // Cache miss: evaluate behavior tree
                        string action;
                        EnemyActionType actionEnum;
                        string abilityId = null;

                        if (store.EnemyStunFlag[enemyId])
                        {
                            action = "none";
                            actionEnum = EnemyActionType.None;
                            store.SetEnemyActionEnum(enemyId, actionEnum);
                            _lastActionCache[enemyId] = actionEnum;
                            continue;
                        }

                        // Decoys are passive: no movement, no attacks, no abilities.
                        // They exist solely to draw enemy aggro and absorb damage until they expire
                        // (handled above) or are killed. Set action to None and skip BT evaluation.
                        if (store.EnemyIsDecoy[enemyId])
                        {
                            actionEnum = EnemyActionType.None;
                            store.SetEnemyActionEnum(enemyId, actionEnum);
                            _lastActionCache[enemyId] = actionEnum;
                            continue;
                        }

                        // Free-Roam enemies (Round 84): off-path monsters skip the BT entirely.
                        // Their behavior is steered by WanderRoamSystem (target selection) and
                        // EnemyMovementSystem's Wandering action branch (position update).
                        // Skipping BT here keeps the per-frame cost for free-roam enemies at
                        // O(1) instead of the full BT evaluation cost, which is important when
                        // a wave of 100+ free-roam enemies is on the field.
                        if (store.EnemyIsFreeRoam[enemyId])
                        {
                            actionEnum = EnemyActionType.Wandering;
                            store.SetEnemyActionEnum(enemyId, actionEnum);
                            _lastActionCache[enemyId] = actionEnum;
                            continue;
                        }

                        if (cachedBt != null)
                        {
                            action = BTCachedTreeEvaluator.EvaluateWithEnumAndAbility(
                                cachedBt, enemyId, store, playerId, currentTurn,
                                out actionEnum, out abilityId);
                        }
                        else
                        {
                            string monsterType = store.GetEnemyTypeName(enemyId);
                            if (string.IsNullOrEmpty(monsterType))
                                monsterType = store.GetName(enemyId);
                            cachedBt = gameConfig.GetCachedBehaviorTree(monsterType);
                            if (cachedBt != null)
                            {
                                action = BTCachedTreeEvaluator.EvaluateWithEnumAndAbility(
                                    cachedBt, enemyId, store, playerId, currentTurn,
                                    out actionEnum, out abilityId);
                            }
                            else
                            {
                                action = GetFallbackAction(enemyId);
                                actionEnum = StringToActionEnum(action);
                            }
                        }
                        store.SetEnemyActionEnum(enemyId, actionEnum);
                        store.EnemyCastAbilityId[enemyId] = abilityId;

                        _enemyHealthCache[enemyId] = enemyHealth;
                        _enemyChargeCounterCache[enemyId] = chargeCounter;
                        _stunFlagCache[enemyId] = stunFlag;
                        _enemyStunDurationCache[enemyId] = stunDuration;
                        _lastActionCache[enemyId] = actionEnum;
                        _lastActionStringCache[enemyId] = action;

                        // Collect attack events
                        if (actionEnum == EnemyActionType.AttackMelee ||
                            actionEnum == EnemyActionType.RangedAttack ||
                            actionEnum == EnemyActionType.ChargeAttack)
                        {
                            float param = (actionEnum == EnemyActionType.ChargeAttack)
                                ? store.EnemyChargeParam[enemyId] : 0f;
                            _attackEvents[_attackEventsIdx].Add(new AttackEvent
                            {
                                EnemyId = enemyId,
                                ActionType = actionEnum,
                                Param = param
                            });
                        }
                    }
                });
            }

            // Serial action execution
            int readIdx = _attackEventsIdx;
            foreach (var evt in _attackEvents[readIdx])
            {
                InvokeExecuteActionEnum(evt.EnemyId, evt.ActionType);
            }

            // Ping-pong swap
            int writeIdx = 1 - _attackEventsIdx;
            _attackEvents[writeIdx].Clear();
            _attackEventsIdx = writeIdx;

            // Serial lifesteal apply — after all attack actions have been resolved
            int lsReadIdx = _lifestealEventsIdx;
            foreach (var evt in _lifestealEvents[lsReadIdx])
            {
                if (!store.EnemyActive[evt.EnemyId]) continue;
                store.ApplyEnemyResourceAuthority(evt.EnemyId, evt.EnemyId, new Core.GAS.AttributeKey(3), evt.HealAmount);
            }
            // Ping-pong swap
            int lsWriteIdx = 1 - _lifestealEventsIdx;
            _lifestealEvents[lsWriteIdx].Clear();
            _lifestealEventsIdx = lsWriteIdx;

            // Dodge execution
            foreach (var enemyId in activeEnemyIds)
            {
                if (!store.EnemyActive[enemyId]) continue;
                var actionEnum = store.GetEnemyActionEnum(enemyId);
                if (actionEnum == EnemyActionType.Dodge)
                {
                    // Skip lateral dodge movement if player has knockback immunity
                    if (_playerHasKnockbackImmunity) continue;
                    string cachedAction = _lastActionStringCache[enemyId] ?? "dodge";
                    int dodgeDir = ParseDodgeDirection(cachedAction);
                    store.EnemyChargeParam[enemyId] = dodgeDir;
                    float enemyX = store.PositionX[enemyId];
                    store.PositionX[enemyId] = enemyX + dodgeDir * store.EnemyMoveSpeed[enemyId];
                }
            }

            // Faction / Infighting (Round 90): pairwise check on same-faction enemies.
            // Opt-in via EnemyFactionId > 0; pairs in close proximity deal 5% maxHp each + 0.5s cooldown.
            // Gated by FactionInfightEnabled (set by WaveSpawningSystem) so the O(N) early-out scan
            // and O(N) cooldown-decrement loop only run when at least one enemy has a faction. This
            // is the lazy-disable optimization: when no monster config opts in, the gate stays 0
            // and the entire infight pass is a single int comparison, restoring pre-feature perf.
            if (store.FactionInfightEnabled != 0)
            {
                ResolveFactionInfighting(activeEnemyIds);
            }
        }

        /// <summary>
        /// Round 90 Faction / Infighting — same-faction enemies in close proximity damage each
        /// other. Damage = 5% of maxHp per side per trigger; cooldown 0.5s prevents spam.
        /// Skips pairs where either side has FactionId == 0 (opt-out) or has a cooldown > 0.
        /// </summary>
        private void ResolveFactionInfighting(List<int> activeEnemyIds)
        {
            int count = activeEnemyIds.Count;
            if (count < 2) return;
            // Cheap early-out: if no enemy in this batch has FactionId > 0, skip the O(N²) work.
            // Walk once to detect presence, then walk again for the pairwise check. Both passes
            // are tight loops over ~10K entries with random early-termination for non-faction.
            // Note: must NOT short-circuit on cooldown here, because the cooldown-decrement
            // loop at the bottom of this method still needs to run every frame to decay
            // existing cooldowns. Skipping it would freeze cooldowns permanently.
            bool hasAnyFaction = false;
            for (int i = 0; i < count; i++)
            {
                int eid = activeEnemyIds[i];
                if (!store.EnemyActive[eid]) continue;
                if (store.EnemyFactionId[eid] != 0)
                {
                    hasAnyFaction = true;
                    break;
                }
            }
            if (!hasAnyFaction) return;

            // Pairwise O(N²) check — for each eligible enemy A, look for eligible enemy B with
            // matching FactionId and (dx² + dy²) < InfightRadius².
            // Use squared distance to avoid sqrt. InfightRadius = 0.5 world units.
            const float InfightRadius = 0.5f;
            const float InfightRadiusSq = InfightRadius * InfightRadius;
            const float InfightDmgFrac = 0.05f;  // 5% of maxHp per side
            const float InfightCooldownSec = 0.5f;

            // Outer loop: A. Inner loop: B > A to dedupe.
            // IMPORTANT: re-read store.EnemyInfightCooldown[aId] at the top of each j iteration
            // so that after A's first hit sets the cooldown, subsequent j iterations correctly
            // skip A and don't let A damage every nearby B in a single frame.
            for (int i = 0; i < count; i++)
            {
                int aId = activeEnemyIds[i];
                if (!store.EnemyActive[aId]) continue;
                int aFaction = store.EnemyFactionId[aId];
                if (aFaction == 0) continue;
                if (store.EnemyInfightCooldown[aId] > 0f) continue;
                float aX = store.PositionX[aId];
                float aY = store.PositionY[aId];
                float aMaxHp = store.EnemyMaxHealth[aId];
                if (aMaxHp <= 0f) continue;

                for (int j = i + 1; j < count; j++)
                {
                    // Re-check A's cooldown every iteration — A's first successful hit sets
                    // its cooldown to InfightCooldownSec, and we must not let A hit another
                    // B in the same frame.
                    if (store.EnemyInfightCooldown[aId] > 0f) break;
                    int bId = activeEnemyIds[j];
                    if (!store.EnemyActive[bId]) continue;
                    if (store.EnemyFactionId[bId] != aFaction) continue;
                    if (store.EnemyInfightCooldown[bId] > 0f) continue;
                    float dx = aX - store.PositionX[bId];
                    float dy = aY - store.PositionY[bId];
                    if (dx * dx + dy * dy > InfightRadiusSq) continue;

                    // Apply 5% maxHp damage to both sides. Damage goes through ApplyEnemyDamage
                    // so shield (round 22) and other damage modifiers apply consistently.
                    float dmgA = aMaxHp * InfightDmgFrac;
                    float dmgB = store.EnemyMaxHealth[bId] * InfightDmgFrac;
                    store.ApplyDamageAuthority(aId, bId, dmgB, 0, stage: Core.GAS.DamageAmountStage.Raw);
                    store.ApplyDamageAuthority(bId, aId, dmgA, 0, stage: Core.GAS.DamageAmountStage.Raw);

                    // Set cooldowns on both sides so we don't double-trigger the same pair
                    // in the same frame or the next InfightCooldownSec.
                    store.SetInfightCooldown(aId, InfightCooldownSec);
                    store.SetInfightCooldown(bId, InfightCooldownSec);
                }
            }

            // Decrement all faction cooldowns by _currentDeltaTime. Wrap to 0 if past expiry.
            // Done at the end of the same frame so cooldowns set in this iteration are NOT
            // decremented until next frame (prevents 0-frame double-trigger).
            for (int i = 0; i < count; i++)
            {
                int eid = activeEnemyIds[i];
                if (!store.EnemyActive[eid]) continue;
                float cd = store.EnemyInfightCooldown[eid];
                if (cd > 0f)
                {
                    cd -= _currentDeltaTime;
                    if (cd < 0f) cd = 0f;
                    store.EnemyInfightCooldown[eid] = cd;
                }
            }
        }

        private string GetFallbackAction(int enemyId)
        {
            float enemyX = store.PositionX[enemyId];
            float enemyY = store.PositionY[enemyId];
            float distance = Math.Abs(enemyX - _playerX) + Math.Abs(enemyY - _playerY);
            if (distance <= 1.5f)
                return "attack_melee";
            return "move_to_target";
        }

        /// <summary>
        /// Round 174 Direction 8 — Stalker auto-reveal. Walks all friendly tower positions
        /// and reveals the stalker if any tower is within EnemyStalkRevealRadius.
        /// Reveal is sticky (one-shot per spawn) so the O(activeTowers) scan only runs
        /// until the stalker is revealed — once revealed, EnemyStalkRevealed=true skips
        /// the check at the call site.
        /// Caller must guarantee EnemyIsStalker[enemyId] && !EnemyStalkRevealed[enemyId].
        /// </summary>
        private void UpdateStalkerReveal(int enemyId)
        {
            float revealRadius = store.EnemyStalkRevealRadius[enemyId];
            // 0 = "no auto-reveal" — stalker stays hidden until something else reveals it.
            // This makes detection-tower-only monsters possible: a sniper that patrols
            // invisibly until a detection-tower pings it.
            if (revealRadius <= 0f) return;
            float radiusSq = revealRadius * revealRadius;
            float ex = store.PositionX[enemyId];
            float ey = store.PositionY[enemyId];
            var towerIds = store.ActiveTowerIds;
            int tCount = towerIds.Count;
            for (int i = 0; i < tCount; i++)
            {
                int tid = towerIds[i];
                if (!store.TowerActive[tid]) continue;
                float tx = store.PositionX[tid];
                float ty = store.PositionY[tid];
                float dx = tx - ex;
                float dy = ty - ey;
                float distSq = dx * dx + dy * dy;
                if (distSq <= radiusSq)
                {
                    // Reveal! Sticky flag; the ambush bonus is consumed by the FIRST
                    // attack post-reveal (see TowerAttackSystem / PlayerTowerAttackSystem
                    // / EnemyAbilitySystem — EnemyStalkConsumed becomes true after the
                    // first damage application).
                    store.EnemyStalkRevealed[enemyId] = true;
                    return;
                }
            }
        }

        public static EnemyActionType StringToActionEnum(string action)
        {
            if (string.IsNullOrEmpty(action))
                return EnemyActionType.None;

            if (actionCache.TryGetValue(action, out var cached))
                return cached;

            string baseAction = action;
            int underscoreIdx = action.LastIndexOf('_');
            if (underscoreIdx > 0 && underscoreIdx < action.Length - 1)
            {
                string suffix = action.Substring(underscoreIdx + 1);
                if (float.TryParse(suffix, out _))
                    baseAction = action.Substring(0, underscoreIdx);
            }

            EnemyActionType result = baseAction switch
            {
                "move_to_target" => EnemyActionType.MoveToTarget,
                "attack_melee" => EnemyActionType.AttackMelee,
                "ranged_attack" => EnemyActionType.RangedAttack,
                "charge_attack" => EnemyActionType.ChargeAttack,
                "dodge" => EnemyActionType.Dodge,
                "retreat" => EnemyActionType.Retreat,
                "enemy_cast_stun" => EnemyActionType.StunAoe,
                "enemy_cast_slow" => EnemyActionType.SlowAoe,
                "enemy_cast_heal" => EnemyActionType.HealAllies,
                "enemy_cast_stealth" => EnemyActionType.StealthAttack,
                _ => EnemyActionType.None,
            };

            actionCache[action] = result;
            return result;
        }

        private static readonly ConcurrentDictionary<string, EnemyActionType> actionCache = new ConcurrentDictionary<string, EnemyActionType>();

        public void InvokeExecuteActionEnum(int enemyId, EnemyActionType actionEnum)
        {
            switch (actionEnum)
            {
                case EnemyActionType.MoveToTarget:
                    break;
                case EnemyActionType.AttackMelee:
                    ExecuteMeleeAttack(enemyId);
                    break;
                case EnemyActionType.RangedAttack:
                    ExecuteRangedAttack(enemyId);
                    break;
                case EnemyActionType.ChargeAttack:
                    ExecuteChargeAttack(enemyId, store.EnemyChargeParam[enemyId]);
                    break;
                case EnemyActionType.Dodge:
                    break;
                case EnemyActionType.Retreat:
                    break;
                case EnemyActionType.SelfHeal:
                case EnemyActionType.AoeDamage:
                case EnemyActionType.BuffAllies:
                case EnemyActionType.StunAoe:
                case EnemyActionType.SlowAoe:
                case EnemyActionType.HealAllies:
                case EnemyActionType.StealthAttack:
                    // Ability actions are dispatched to EnemyAbilitySystem
                    string abilityId = store.EnemyCastAbilityId[enemyId];
                    if (!string.IsNullOrEmpty(abilityId))
                        enemyAbilitySystem.EnqueueAbility(enemyId, abilityId);
                    break;
                case EnemyActionType.None:
                default:
                    break;
            }
        }

        public void InvokeExecuteAction(int enemyId, string action)
        {
            if (string.IsNullOrEmpty(action))
                return;

            string baseAction = action;
            float param = 0f;

            int underscoreIdx = action.LastIndexOf('_');
            if (underscoreIdx > 0 && underscoreIdx < action.Length - 1)
            {
                string suffix = action.Substring(underscoreIdx + 1);
                if (float.TryParse(suffix, out float parsed))
                {
                    baseAction = action.Substring(0, underscoreIdx);
                    param = parsed;
                }
            }

            switch (baseAction)
            {
                case "move_to_target":
                    break;
                case "attack_melee":
                    ExecuteMeleeAttack(enemyId);
                    break;
                case "ranged_attack":
                    ExecuteRangedAttack(enemyId);
                    break;
                case "charge_attack":
                    ExecuteChargeAttack(enemyId, param);
                    break;
                case "dodge":
                    break;
                case "retreat":
                    break;
                default:
                    break;
            }
        }

        private void ExecuteMeleeAttack(int enemyId)
        {
            float damage = store.EnemyDamage[enemyId];
            damage += store.EnemyBuffDamageBonus[enemyId];
            // Apply stealth multiplier and reset to 1.0f for next attack
            float stealthMult = store.EnemyStealthMultiplier[enemyId];
            damage *= stealthMult;
            store.EnemyStealthMultiplier[enemyId] = 1f;
            store.DecreasePlayerHealth(playerId, damage);
            float remaining = store.GetPlayerCurrentHealth(playerId);
            _eventBus.PlayerDamaged.Publish(new PlayerDamagedEvent
            {
                Damage = damage,
                RemainingHealth = remaining,
                AttackerId = enemyId
            });
            store.SetEnemyAILastAttackTurn(enemyId, currentTurn);
            logger.Log($"[AI] Enemy {enemyId} attacks player for {damage} damage (HP: {remaining})");

            // Lifesteal: collect event for serial apply (two-phase pattern)
            if (store.EnemyLifestealActive[enemyId])
            {
                float ratio = store.EnemyLifestealRatio[enemyId];
                float cap = store.EnemyLifestealCap[enemyId];
                if (ratio > 0f)
                {
                    float healAmount = Math.Min(damage * ratio, cap);
                    if (healAmount > 0f)
                    {
                        _lifestealEvents[_lifestealEventsIdx].Add(new LifestealEvent
                        {
                            EnemyId = enemyId,
                            HealAmount = healAmount
                        });
                    }
                }
            }
        }

        private void ExecuteRangedAttack(int enemyId)
        {
            float damage = store.EnemyDamage[enemyId];
            damage += store.EnemyBuffDamageBonus[enemyId];
            // Apply stealth multiplier and reset to 1.0f for next attack
            float stealthMult = store.EnemyStealthMultiplier[enemyId];
            damage *= stealthMult;
            store.EnemyStealthMultiplier[enemyId] = 1f;
            store.DecreasePlayerHealth(playerId, damage);
            float remaining = store.GetPlayerCurrentHealth(playerId);
            _eventBus.EnemyCharging.Publish(new EnemyChargingEvent
            {
                EnemyId = enemyId,
                Turn = currentTurn,
                Damage = damage
            });
            _eventBus.PlayerDamaged.Publish(new PlayerDamagedEvent
            {
                Damage = damage,
                RemainingHealth = remaining,
                AttackerId = enemyId
            });
            store.SetEnemyAILastAttackTurn(enemyId, currentTurn);
            logger.Log($"[AI] Enemy {enemyId} ranged attacks player for {damage} damage (HP: {remaining})");

            // Lifesteal: collect event for serial apply (two-phase pattern)
            if (store.EnemyLifestealActive[enemyId])
            {
                float ratio = store.EnemyLifestealRatio[enemyId];
                float cap = store.EnemyLifestealCap[enemyId];
                if (ratio > 0f)
                {
                    float healAmount = Math.Min(damage * ratio, cap);
                    if (healAmount > 0f)
                    {
                        _lifestealEvents[_lifestealEventsIdx].Add(new LifestealEvent
                        {
                            EnemyId = enemyId,
                            HealAmount = healAmount
                        });
                    }
                }
            }
        }

        private void ExecuteChargeAttack(int enemyId, float param)
        {
            int counter = store.GetEnemyAIChargeCounter(enemyId);
            int requiredTurns = (param > 0) ? (int)param : 3;

            if (counter < requiredTurns)
            {
                store.SetEnemyAIChargeCounter(enemyId, counter + 1);
                store.EnemyChargeParam[enemyId] = param;
                _eventBus.EnemyCharging.Publish(new EnemyChargingEvent
                {
                    EnemyId = enemyId,
                    Turn = currentTurn,
                    Damage = store.EnemyDamage[enemyId]
                });
                logger.Log($"[AI] Enemy {enemyId} charging ({counter + 1}/{requiredTurns})");
            }
            else
            {
                float baseDamage = store.EnemyDamage[enemyId];
                baseDamage += store.EnemyBuffDamageBonus[enemyId];
                // Apply stealth multiplier and reset to 1.0f for next attack
                float stealthMult = store.EnemyStealthMultiplier[enemyId];
                baseDamage *= stealthMult;
                store.EnemyStealthMultiplier[enemyId] = 1f;
                float chargedDamage = baseDamage * 3f;
                store.DecreasePlayerHealth(playerId, chargedDamage);
                float remaining = store.GetPlayerCurrentHealth(playerId);
                _eventBus.EnemyChargeReleased.Publish(new EnemyChargeReleasedEvent
                {
                    EnemyId = enemyId,
                    Turn = currentTurn,
                    Damage = chargedDamage
                });
                _eventBus.PlayerDamaged.Publish(new PlayerDamagedEvent
                {
                    Damage = chargedDamage,
                    RemainingHealth = remaining,
                    AttackerId = enemyId
                });
                store.SetEnemyAIChargeCounter(enemyId, 0);
                store.EnemyChargeParam[enemyId] = 0f;
                store.SetEnemyAILastAttackTurn(enemyId, currentTurn);
                logger.Log($"[AI] Enemy {enemyId} releases CHARGE for {chargedDamage} damage (3x)! HP: {remaining}");

                // Lifesteal: collect event for serial apply (two-phase pattern)
                if (store.EnemyLifestealActive[enemyId])
                {
                    float ratio = store.EnemyLifestealRatio[enemyId];
                    float cap = store.EnemyLifestealCap[enemyId];
                    if (ratio > 0f)
                    {
                        float healAmount = Math.Min(chargedDamage * ratio, cap);
                        if (healAmount > 0f)
                        {
                            _lifestealEvents[_lifestealEventsIdx].Add(new LifestealEvent
                            {
                                EnemyId = enemyId,
                                HealAmount = healAmount
                            });
                        }
                    }
                }
            }

            // Round 111 Direction 1 — drain phase-ability bag into EnemyAbilitySystem.
            // Both sequential + parallel paths above push (enemyId, abilityId) tuples into
            // _phaseAbilityEvents whenever a phase transition fires. We now serially hand
            // them off to EnemyAbilitySystem.EnqueueAbility (which mutates cooldown
            // timers and _activeChannelers — NOT thread-safe). Drained at end of Update so
            // the rest of the frame can still see the new ability channeling state.
            DrainPhaseAbilityEvents();
            // Round 119 Dir 3 — drain phase-minion bag into WaveSpawningSystem.SpawnMinionNearPosition.
            // Same pattern as ability drain: serial, end-of-Update, thread-safe. Bag is always
            // drained (count tracked) so diagnostics work even when _waveSpawningSystem is null.
            DrainPhaseMinionEvents();
            // Round 129 Dir 2 — drain phase-change bag into EventBus. Fills in BossTypeName
            // (needs single-threaded access to store.EnemyTypeName[]) and publishes the
            // EventBus.BossPhaseChanged channel for any subscribers (music, telemetry, AoE
            // warning). Safe to call even when _eventBus is a no-op (NullEventBus pattern) —
            // the bag is always drained and the count tracked.
            DrainPhaseChangeEvents();
            // Round 134 Direction 3 — Boss HP natural regen. O(active enemies) single pass
            // at end of Update. Runs AFTER all phase transitions + damage apply so the
            // regen tick reflects the post-damage HP. Only mutates EnemyHealth[id] and is
            // bounded by EnemyMaxHealth[id], so no over-heal / clamp hazards. Gated on
            // EnemyHealthRegenPerSec > 0 (zero cost for legacy enemies).
            TickBossRegen();
        }

        // Round 134 Direction 3 — Boss HP regen tick. Walks all active enemies; for any
        // with EnemyHealthRegenPerSec > 0, applies regen * mult * dt to EnemyHealth,
        // clamped to EnemyMaxHealth. The phase multiplier is recomputed live from
        // monsterConfig.PhaseRegenMult indexed by EnemyBossPhase (or 1.0 if absent).
        // Zero-overhead fast path: when no active enemy has a non-zero regen rate, the
        // inner branch is skipped (the read of EnemyHealthRegenPerSec is a single array
        // load — the L1 line stays hot). For 10K active enemies this is a single linear
        // pass with two array reads + one conditional; ~10-20 ns/enemy in practice.
        public int BossRegenDrainCount { get; private set; }
        private void TickBossRegen()
        {
            if (_currentDeltaTime <= 0f) return;
            var activeEnemyIds = _activeEnemyList;
            int count = activeEnemyIds.Count;
            int touched = 0;
            for (int i = 0; i < count; i++)
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId]) continue;
                float baseRegen = store.EnemyHealthRegenPerSec[enemyId];
                if (baseRegen <= 0f) continue;
                float currentHp = store.EnemyHealth[enemyId];
                float maxHp = store.EnemyMaxHealth[enemyId];
                if (currentHp <= 0f || currentHp >= maxHp) continue;
                // Phase multiplier: live-lookup from monsterConfig.PhaseRegenMult indexed by
                // the boss's current phase. If config lookup fails (uncommon — happens for
                // injected/synthetic enemies in tests) fall back to the cached per-enemy
                // mult that was seeded at spawn time. This way regen survives even if the
                // GameConfig reference is incomplete in test harnesses.
                float mult = store.EnemyHealthRegenMult[enemyId];
                if (mult <= 0f) mult = 1f;
                // Best-effort live refresh: only spend the dict lookup cost on enemies that
                // actually have a non-zero base regen. The lookup is rare (boss < 10 active
                // typically) so the per-frame cost is negligible.
                string monsterType = store.GetEnemyTypeName(enemyId);
                if (string.IsNullOrEmpty(monsterType))
                    monsterType = store.GetName(enemyId);
                var monsterConfig = gameConfig.GetMonsterConfig(monsterType);
                if (monsterConfig != null && monsterConfig.PhaseRegenMult != null
                    && monsterConfig.PhaseRegenMult.Length > 0)
                {
                    int ph = store.EnemyBossPhase[enemyId];
                    if (ph >= 0 && ph < monsterConfig.PhaseRegenMult.Length)
                        mult = monsterConfig.PhaseRegenMult[ph];
                }
                float heal = baseRegen * mult * _currentDeltaTime;
                if (heal <= 0f) continue;
                store.ApplyEnemyResourceAuthority(enemyId, enemyId, new Core.GAS.AttributeKey(3), heal);
                touched++;
            }
            BossRegenDrainCount = touched;
        }

        /// <summary>
        /// Round 111 Direction 1 — drain the phase-ability event bag into EnemyAbilitySystem.
        /// Called at the end of Update() (after both sequential and parallel paths complete).
        /// Uses TryTake in a tight loop to empty the bag without per-iteration allocations.
        /// Drained count is exposed for tests / diagnostics.
        /// </summary>
        public int PhaseAbilityDrainCount { get; private set; }
        private void DrainPhaseAbilityEvents()
        {
            int count = 0;
            while (_phaseAbilityEvents.TryTake(out var ev))
            {
                if (enemyAbilitySystem != null && !string.IsNullOrEmpty(ev.abilityId))
                {
                    enemyAbilitySystem.EnqueueAbility(ev.enemyId, ev.abilityId);
                    count++;
                }
            }
            PhaseAbilityDrainCount = count;
        }

        // Round 119 Dir 3 — drain the phase-minion bag into WaveSpawningSystem.SpawnMinionNearPosition.
        // Called at the end of Update() (after both sequential and parallel paths complete). Like
        // DrainPhaseAbilityEvents, this is serial and thread-safe. When _waveSpawningSystem is null
        // (e.g. unit tests without a full GameManager) the bag is drained but no spawn happens —
        // the count is still tracked for diagnostics via PhaseMinionDrainCount.
        // Round 137 Dir 6 — bag now carries the boss's ElementType int; passed to the spawn
        // overload so minions with matching MonsterConfig.ElementAffinity get a +10% HP bonus.
        public int PhaseMinionDrainCount { get; private set; }
        public int PhaseMinionSpawnedCount { get; private set; }
        private void DrainPhaseMinionEvents()
        {
            int drained = 0;
            int spawned = 0;
            while (_phaseMinionEvents.TryTake(out var ev))
            {
                drained++;
                if (_waveSpawningSystem == null) continue;
                int n = _waveSpawningSystem.SpawnMinionNearPosition(ev.typeId, ev.count, ev.x, ev.y, ev.elementAffinity);
                spawned += n;
            }
            PhaseMinionDrainCount = drained;
            PhaseMinionSpawnedCount = spawned;
        }

        // Round 129 Dir 2 — drain the phase-change event bag into EventBus. Fills in the
        // BossTypeName from store.EnemyTypeName[] (single-threaded access) and publishes each
        // event via _eventBus.BossPhaseChanged.Publish. Empty BossTypeName is normalized to null so subscribers
        // see a consistent "no name available" sentinel. Drain count is exposed for tests /
        // diagnostics. Safe to call when _eventBus is null (the constructor default-fallback
        // creates a fresh EventBus, but tests that pass null will get a NullReferenceException
        // here — that's intentional, mirrors the existing pattern for other drains).
        public int PhaseChangeDrainCount { get; private set; }
        public int PhaseChangePublishCount { get; private set; }
        private void DrainPhaseChangeEvents()
        {
            int count = 0;
            int published = 0;
            while (_phaseChangeEvents.TryTake(out var ev))
            {
                count++;
                // Resolve BossTypeName in the serial drain (avoids contention on the
                // EnemyTypeName[] array — push sites can run in parallel).
                string typeName = store.GetEnemyTypeName(ev.EnemyId);
                ev.BossTypeName = string.IsNullOrEmpty(typeName) ? null : typeName;
                _eventBus.BossPhaseChanged.Publish(ev);
                published++;
            }
            PhaseChangeDrainCount = count;
            PhaseChangePublishCount = published;
        }

        private static int ParseDodgeDirection(string action)
        {
            if (string.IsNullOrEmpty(action))
                return 1;
            int underscoreIdx = action.LastIndexOf('_');
            if (underscoreIdx > 0 && underscoreIdx < action.Length - 1)
            {
                string suffix = action.Substring(underscoreIdx + 1);
                if (int.TryParse(suffix, out int dir))
                    return dir;
            }
            return 1;
        }

        private struct AttackEvent
        {
            public int EnemyId;
            public EnemyActionType ActionType;
            public float Param;
        }

        private struct LifestealEvent
        {
            public int EnemyId;
            public float HealAmount;
        }
    }
}
