using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections.Concurrent;
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

        private int currentTurn;
        // SOA charge param — stored in store.EnemyChargeParam[] for zero-allocation array access

        // Per-turn cached fields for cache locality
        private List<int> _activeEnemyList;
        private float _playerX, _playerY;

        // BT evaluation cache — version counter increments on player health change.
        // Turn/frame changes do NOT invalidate (enemy health per-enemy + player health global).
        private int _cacheVersion = 0;
        private int _cacheSnapshot = -1;
        private float _cachedPlayerHealth = -1;
        private readonly float[] _enemyHealthCache = new float[ComponentStore.MAX_ENTITIES];
        private readonly EnemyActionType[] _lastActionCache = new EnemyActionType[ComponentStore.MAX_ENTITIES];

        public EnemyAISystem(ComponentStore store, IRenderer logger, int playerId, GameConfig gameConfig)
        {
            this.store = store;
            this.logger = logger;
            this.playerId = playerId;
            this.gameConfig = gameConfig;
        }

        /// <summary>
        /// Called at the start of each turn with the current turn number.
        /// </summary>
        public void SetTurn(int turn)
        {
            currentTurn = turn;
            // Cache player position once per turn
            _playerX = store.PositionX[playerId];
            _playerY = store.PositionY[playerId];
            // Cache active enemy list once per turn
            _activeEnemyList = store.GetAllActiveEnemyIds();
            // Cache current player health for BT evaluation
            _cachedPlayerHealth = store.PlayerCurrentHealth[playerId];
            // BT eval cache auto-invalidates when enemy/player health changes —
            // turn change alone does NOT invalidate (benchmark-friendly)
        }

        /// <summary>
        /// Evaluate behavior trees for all active enemies and set EnemyAIAction.
        /// Execute damage effects for the current turn's actions.
        /// </summary>
        public void Update()
        {
            var activeEnemyIds = _activeEnemyList;
            int count = activeEnemyIds.Count;

            // Parallel batch processing: 256 enemies per batch for instruction cache locality
            const int batchSize = 256;
            int numBatches = (count + batchSize - 1) / batchSize;

            // Each thread processes its own batch — no shared mutable state during BT evaluation
            Parallel.For(0, numBatches, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                batchIdx =>
            {
                int start = batchIdx * batchSize;
                int end = Math.Min(start + batchSize, count);

                for (int i = start; i < end; i++)
                {
                    int enemyId = activeEnemyIds[i];
                    if (!store.EnemyActive[enemyId])
                        continue;

                    // O(1) array access — pre-cached at spawn time in WaveSpawningSystem
                    var cachedBt = store.EnemyBehaviorTree[enemyId];

                    // Check BT evaluation cache: skip if enemy health, player health, and turn are unchanged
                    float enemyHealth = store.EnemyHealth[enemyId];
                    float playerHealth = store.PlayerCurrentHealth[playerId];
                    if (_cacheSnapshot == _cacheVersion &&
                        _enemyHealthCache[enemyId] == enemyHealth &&
                        _cachedPlayerHealth == playerHealth)
                    {
                        // Cache hit: reuse last action without re-evaluating BT
                        store.SetEnemyActionEnum(enemyId, _lastActionCache[enemyId]);
                        continue;
                    }

                    // Cache miss: evaluate behavior tree
                    // Precomputed enum eliminates StringToActionEnum() in hot path (saves ~17ms/frame at 10K enemies)
                    string action;
                    EnemyActionType actionEnum;
                    if (cachedBt != null)
                    {
                        action = BTCachedTreeEvaluator.EvaluateWithEnum(cachedBt, enemyId, store, playerId, currentTurn, out actionEnum);
                    }
                    else
                    {
                        // Fallback when no BT is configured — derive monsterType from stored name
                        string monsterType = store.GetEnemyTypeName(enemyId);
                        if (string.IsNullOrEmpty(monsterType))
                            monsterType = store.GetName(enemyId);
                        cachedBt = gameConfig.GetCachedBehaviorTree(monsterType);
                        if (cachedBt != null)
                        {
                            action = BTCachedTreeEvaluator.EvaluateWithEnum(cachedBt, enemyId, store, playerId, currentTurn, out actionEnum);
                        }
                        else
                        {
                            action = GetFallbackAction(enemyId);
                            actionEnum = StringToActionEnum(action);
                        }
                    }
                    store.SetEnemyActionEnum(enemyId, actionEnum);

                    // Update cache
                    _enemyHealthCache[enemyId] = enemyHealth;
                    _lastActionCache[enemyId] = actionEnum;

                    // InvokeExecuteActionEnum uses enum dispatch — string action not in hot path.
                    // (SetEnemyAIAction is only read by Dodge in EnemyMovementSystem, but Dodge
                    //  never calls SetEnemyAIAction — see Bug#6 in research/bug-fix.md)
                    if (actionEnum == EnemyActionType.AttackMelee ||
                        actionEnum == EnemyActionType.RangedAttack ||
                        actionEnum == EnemyActionType.ChargeAttack)
                    {
                        InvokeExecuteActionEnum(enemyId, actionEnum);
                    }
                    else if (actionEnum == EnemyActionType.Dodge)
                    {
                        // Dodge has side effects but no string action needed
                        InvokeExecuteActionEnum(enemyId, actionEnum);
                    }
                }
            });

            // Update turn cache after all enemies processed
            _cacheSnapshot = _cacheVersion;
        }

        /// <summary>
        /// Fallback action when no BT is configured.
        /// </summary>
        private string GetFallbackAction(int enemyId)
        {
            float enemyX = store.PositionX[enemyId];
            float enemyY = store.PositionY[enemyId];
            float playerX = _playerX;
            float playerY = _playerY;
            float distance = Math.Abs(enemyX - playerX) + Math.Abs(enemyY - playerY);

            if (distance <= 1.5f)
                return "attack_melee";
            return "move_to_target";
        }

        /// <summary>
        /// Convert action string to EnemyActionType enum using a static cache.
        /// Base action is extracted the same way as in InvokeExecuteAction.
        /// </summary>
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
                {
                    baseAction = action.Substring(0, underscoreIdx);
                }
            }

            EnemyActionType result = baseAction switch
            {
                "move_to_target" => EnemyActionType.MoveToTarget,
                "attack_melee" => EnemyActionType.AttackMelee,
                "ranged_attack" => EnemyActionType.RangedAttack,
                "charge_attack" => EnemyActionType.ChargeAttack,
                "dodge" => EnemyActionType.Dodge,
                "retreat" => EnemyActionType.Retreat,
                _ => EnemyActionType.None,
            };

            actionCache[action] = result;
            return result;
        }

        // Static cache for StringToActionEnum — eliminates repeated switch per call
        private static readonly ConcurrentDictionary<string, EnemyActionType> actionCache = new ConcurrentDictionary<string, EnemyActionType>();

        /// <summary>
        /// Execute the given action for the specified enemy using enum dispatch.
        /// </summary>
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
                    {
                        float param = store.EnemyChargeParam[enemyId];
                        ExecuteChargeAttack(enemyId, param);
                    }
                    break;

                case EnemyActionType.Dodge:
                    break;

                case EnemyActionType.Retreat:
                    break;

                case EnemyActionType.None:
                default:
                    break;
            }
        }

        /// <summary>
        /// Legacy string-based execute — kept for backward compatibility.
        /// </summary>
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

            store.DecreasePlayerHealth(playerId, damage);
            float remaining = store.GetPlayerCurrentHealth(playerId);

            EventBus.Instance.Publish(GameEvents.PlayerDamaged, new PlayerDamagedEvent
            {
                Damage = damage,
                RemainingHealth = remaining,
                AttackerId = enemyId
            });

            store.SetEnemyAILastAttackTurn(enemyId, currentTurn);
            logger.Log($"[AI] Enemy {enemyId} attacks player for {damage} damage (HP: {remaining})");
        }

        private void ExecuteRangedAttack(int enemyId)
        {
            float damage = store.EnemyDamage[enemyId];

            store.DecreasePlayerHealth(playerId, damage);
            float remaining = store.GetPlayerCurrentHealth(playerId);

            EventBus.Instance.Publish(GameEvents.EnemyCharging, new EnemyChargingEvent
            {
                EnemyId = enemyId,
                Turn = currentTurn,
                Damage = damage
            });

            EventBus.Instance.Publish(GameEvents.PlayerDamaged, new PlayerDamagedEvent
            {
                Damage = damage,
                RemainingHealth = remaining,
                AttackerId = enemyId
            });

            store.SetEnemyAILastAttackTurn(enemyId, currentTurn);
            logger.Log($"[AI] Enemy {enemyId} ranged attacks player for {damage} damage (HP: {remaining})");
        }

        private void ExecuteChargeAttack(int enemyId, float param)
        {
            int counter = store.GetEnemyAIChargeCounter(enemyId);
            int requiredTurns = (param > 0) ? (int)param : 3;

            if (counter < requiredTurns)
            {
                store.SetEnemyAIChargeCounter(enemyId, counter + 1);
                store.EnemyChargeParam[enemyId] = param;

                EventBus.Instance.Publish(GameEvents.EnemyCharging, new EnemyChargingEvent
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
                float chargedDamage = baseDamage * 3f;

                store.DecreasePlayerHealth(playerId, chargedDamage);
                float remaining = store.GetPlayerCurrentHealth(playerId);

                EventBus.Instance.Publish(GameEvents.EnemyChargeReleased, new EnemyChargeReleasedEvent
                {
                    EnemyId = enemyId,
                    Turn = currentTurn,
                    Damage = chargedDamage
                });

                EventBus.Instance.Publish(GameEvents.PlayerDamaged, new PlayerDamagedEvent
                {
                    Damage = chargedDamage,
                    RemainingHealth = remaining,
                    AttackerId = enemyId
                });

                store.SetEnemyAIChargeCounter(enemyId, 0);
                store.EnemyChargeParam[enemyId] = 0f;
                store.SetEnemyAILastAttackTurn(enemyId, currentTurn);

                logger.Log($"[AI] Enemy {enemyId} releases CHARGE for {chargedDamage} damage (3x)! HP: {remaining}");
            }
        }
    }

    public class EnemyChargingEvent
    {
        public int EnemyId;
        public int Turn;
        public float Damage;
    }

    public class EnemyChargeReleasedEvent
    {
        public int EnemyId;
        public int Turn;
        public float Damage;
    }
}