using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

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
        // Per-enemy charge param (Param value from the BT node definition)
        private readonly Dictionary<int, float> chargeParams = new Dictionary<int, float>();

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
        }

        /// <summary>
        /// Evaluate behavior trees for all active enemies and set EnemyAIAction.
        /// Execute damage effects for the current turn's actions.
        /// </summary>
        public void Update()
        {
            var activeEnemyIds = store.GetAllActiveEnemyIds();
            int evaluated = 0;

            foreach (int enemyId in activeEnemyIds)
            {
                if (!store.EnemyActive[enemyId])
                    continue;

                // Derive monster type from entity name, e.g. "NormalL1W1E0" -> "Normal"
                string fullName = store.GetName(enemyId);
                string monsterType = fullName;
                int sepIdx = fullName.IndexOf('L');
                if (sepIdx > 0)
                    monsterType = fullName.Substring(0, sepIdx);

                // Look up cached (array-based) behavior tree for this monster type
                var cachedBt = gameConfig.GetCachedBehaviorTree(monsterType);
                string action;
                if (cachedBt != null)
                {
                    action = BTCachedTreeEvaluator.Evaluate(cachedBt, enemyId, store, playerId, currentTurn);
                }
                else
                {
                    // Fallback when no BT is configured
                    action = GetFallbackAction(enemyId);
                }

                store.SetEnemyAIAction(enemyId, action);
                ExecuteAction(enemyId, action);
                evaluated++;
            }

            if (evaluated > 0)
            {
                logger.Log($"[AI] Evaluated {evaluated} enemies on turn {currentTurn}");
            }
        }

        /// <summary>
        /// Fallback action when no BT is configured.
        /// </summary>
        private string GetFallbackAction(int enemyId)
        {
            float enemyX = store.PositionX[enemyId];
            float enemyY = store.PositionY[enemyId];
            float playerX = store.PositionX[playerId];
            float playerY = store.PositionY[playerId];
            float distance = Math.Abs(enemyX - playerX) + Math.Abs(enemyY - playerY);

            if (distance <= 1.5f)
                return "attack_melee";
            return "move_to_target";
        }

        /// <summary>
        /// Execute the given action for the specified enemy.
        /// </summary>
        private void ExecuteAction(int enemyId, string action)
        {
            if (string.IsNullOrEmpty(action))
                return;

            // Parse action and optional numeric suffix (e.g. "dodge_1", "charge_attack_3")
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
                chargeParams[enemyId] = param;

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
                chargeParams.Remove(enemyId);
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