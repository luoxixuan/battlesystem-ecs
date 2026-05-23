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

        private int currentTurn;
        // Per-turn cached fields for cache locality
        private List<int> _activeEnemyList;
        private float _playerX, _playerY;
        private bool _playerHasKnockbackImmunity;

        // Attack event batch — ping-pong double-buffer to eliminate per-frame GC allocation.
        // Collected in parallel phase, executed in serial phase.
        private ConcurrentBag<AttackEvent>[] _attackEvents = new ConcurrentBag<AttackEvent>[2];
        private int _attackEventsIdx = 0;

        // BT evaluation cache — invalidates when enemy health, charge counter, or stun duration changes.
        private float _cachedPlayerHealth = -1;
        private readonly float[] _enemyHealthCache = new float[ComponentStore.MAX_ENTITIES];
        private readonly int[] _enemyChargeCounterCache = new int[ComponentStore.MAX_ENTITIES];
        private readonly float[] _enemyStunDurationCache = new float[ComponentStore.MAX_ENTITIES];
        private readonly bool[] _stunFlagCache = new bool[ComponentStore.MAX_ENTITIES];
        private readonly EnemyActionType[] _lastActionCache = new EnemyActionType[ComponentStore.MAX_ENTITIES];
        private readonly string[] _lastActionStringCache = new string[ComponentStore.MAX_ENTITIES];

        public EnemyAISystem(ComponentStore store, IRenderer logger, int playerId, GameConfig gameConfig, EnemyAbilitySystem enemyAbilitySystem, TechTreeSystem techTreeSystem = null)
        {
            this.store = store;
            this.logger = logger;
            this.playerId = playerId;
            this.gameConfig = gameConfig;
            this.enemyAbilitySystem = enemyAbilitySystem;
            this.techTreeSystem = techTreeSystem;
            _attackEvents[0] = new ConcurrentBag<AttackEvent>();
            _attackEvents[1] = new ConcurrentBag<AttackEvent>();
        }

        /// <summary>
        /// Called at the start of each turn with the current turn number.
        /// </summary>
        public void SetTurn(int turn)
        {
            currentTurn = turn;
            _playerX = store.PositionX[playerId];
            _playerY = store.PositionY[playerId];
            _activeEnemyList = store.GetCachedActiveEnemyIds();
            _cachedPlayerHealth = store.PlayerCurrentHealth[playerId];
            _playerHasKnockbackImmunity = techTreeSystem?.GetKnockbackImmunity() ?? false;
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
            int numBatches = (count + batchSize - 1) / batchSize;

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

                    var cachedBt = store.EnemyBehaviorTree[enemyId];

                    // Check BT evaluation cache — also track stun duration changes
                    float enemyHealth = store.EnemyHealth[enemyId];
                    float playerHealth = store.PlayerCurrentHealth[playerId];
                    int chargeCounter = store.GetEnemyAIChargeCounter(enemyId);
                    bool stunFlag = store.EnemyStunFlag[enemyId];
                    float stunDuration = store.EnemyStunDurationLeft[enemyId];
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

                    // If enemy is stunned, skip BT and force no action
                    if (store.EnemyStunFlag[enemyId])
                    {
                        action = "none";
                        actionEnum = EnemyActionType.None;
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
            damage += store.EnemyBuffDamageBonus[enemyId];
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
                baseDamage += store.EnemyBuffDamageBonus[enemyId];
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
    }
}
