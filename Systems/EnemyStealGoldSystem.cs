using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 金币窃取系统 — 处理小偷敌人到达终点时窃取金币的逻辑。
    /// 小偷不扣血量，而是偷走金币后逃离。
    /// 如果玩家在小偷逃离后将其击杀，可获得 GoldOnReturn 奖励。
    /// </summary>
    public class EnemyStealGoldSystem
    {
        private ComponentStore store;
        private IRenderer renderer;
        private readonly int playerId;

        public EnemyStealGoldSystem(ComponentStore store, IRenderer renderer, int playerId = 0)
        {
            this.store = store;
            this.renderer = renderer;
            this.playerId = playerId;
        }

        public void SetTurn(int turn)
        {
            // Nothing to cache per-turn for this system
        }

        /// <summary>
        /// 每帧检查所有活跃敌人，检测金币窃取敌人是否到达终点。
        /// 小偷到达终点（PositionY <= 0）时：
        ///   1. 扣减玩家金币（LoseGold）
        ///   2. 标记为已逃跑（HasStolenGold）
        ///   3. 将其加入死亡队列（不奖励击杀金币）
        ///   4. 不扣减 BaseLives
        /// </summary>
        public void Update()
        {
            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            for (int i = 0; i < activeEnemyIds.Count; i++)
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId])
                    continue;

                // Check if thief reached the player base (PositionY <= 0)
                if (store.PositionY[enemyId] <= 0f && store.EnemyCanStealGold[enemyId])
                {
                    float stealAmount = store.EnemyStealAmount[enemyId];
                    float goldOnReturn = store.EnemyGoldOnReturn[enemyId];

                    // Steal gold from player
                    if (stealAmount > 0f)
                    {
                        store.LoseGold(playerId, stealAmount);
                        store.EnemyStolenGold[enemyId] = stealAmount;
                        renderer.Log($"[THIEF] Enemy {store.GetEntityName(enemyId)} stole {stealAmount} gold!");
                    }

                    // Mark as escaped (no gold reward on death)
                    store.EnemyHasStolenGold[enemyId] = true;

                    // Queue death (thief escapes, does NOT cost lives)
                    store.QueueEnemyDeath(enemyId, playerId);
                }
            }
        }
    }
}