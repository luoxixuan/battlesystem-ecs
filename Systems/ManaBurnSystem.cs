using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Mana Burn System — enemies with mana-burn ability drain player mana.
    /// Runs in two phases:
    ///   Phase 1 (parallel): collect mana-burn events from enemies that have the ability
    ///   Phase 2 (serial): apply mana drain to player mana pool
    ///
    /// This creates a resource-denial dynamic: mana-burn enemies force players to
    /// manage their skill resource, balancing offensive casting vs defensive conservation.
    /// </summary>
    public class ManaBurnSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;

        // 并行段按活跃敌人索引独占写入，串行段按同一稳定顺序提交。
        private readonly ManaBurnEvent[] _manaBurnEvents = new ManaBurnEvent[ComponentStore.MAX_ENTITIES];
        private readonly bool[] _hasManaBurnEvent = new bool[ComponentStore.MAX_ENTITIES];
        private readonly Action<int> _collectBatch;
        private List<int> _activeEnemyIds;
        private int _activeEnemyCount;
        private int _preparedSpan;
        private const int BatchSize=256;

        private struct ManaBurnEvent
        {
            public int EnemyId;
            public float BurnAmount;
        }

        public ManaBurnSystem(ComponentStore store, int playerId)
        {
            this.store = store;
            this.playerId = playerId;
            _collectBatch=CollectBatch;
        }

        public void SetTurn(int turn)
        {
            // Nothing per-turn to cache for mana burn
        }

        public void Update()
        {
            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            int count = activeEnemyIds.Count;
            int clearCount=Math.Max(_preparedSpan,count);
            if(clearCount>0)Array.Clear(_hasManaBurnEvent,0,clearCount);
            _preparedSpan=count;
            if (count == 0) return;

            // Phase 1: parallel collection of mana burn events
            // Only enemies with EnemyManaBurnAmount > 0 can burn mana
            int parallelThreshold = 500;

            if (count < parallelThreshold)
            {
                // Sequential path — avoid Parallel.For overhead for small counts
                for (int i = 0; i < count; i++)
                {
                    int enemyId = activeEnemyIds[i];
                    if (!store.EnemyActive[enemyId]) continue;

                    float burnAmount = store.EnemyManaBurnAmount[enemyId];
                    if (burnAmount <= 0f) continue;

                    _manaBurnEvents[i].EnemyId = enemyId;
                    _manaBurnEvents[i].BurnAmount = burnAmount;
                    _hasManaBurnEvent[i] = true;
                }
            }
            else
            {
                // Parallel path — batch processing
                _activeEnemyIds=activeEnemyIds;
                _activeEnemyCount=count;
                int numBatches = (count + BatchSize - 1) / BatchSize;
                Parallel.For(0, numBatches, ParallelOptionsCache.Capped4, _collectBatch);
            }

            // Phase 2: serial execution — apply mana drain
            for (int i = 0; i < count; i++)
            {
                if (!_hasManaBurnEvent[i]) continue;
                ManaBurnEvent evt = _manaBurnEvents[i];
                ApplyManaBurn(evt.EnemyId, evt.BurnAmount);
            }
        }

        private void CollectBatch(int batchIdx)
        {
            int start=batchIdx*BatchSize;
            int end=Math.Min(start+BatchSize,_activeEnemyCount);
            for(int i=start;i<end;i++)
            {
                int enemyId=_activeEnemyIds[i];
                if(!store.EnemyActive[enemyId])continue;
                float burnAmount=store.EnemyManaBurnAmount[enemyId];
                if(burnAmount<=0f)continue;
                _manaBurnEvents[i].EnemyId=enemyId;
                _manaBurnEvents[i].BurnAmount=burnAmount;
                _hasManaBurnEvent[i]=true;
            }
        }

        private void ApplyManaBurn(int enemyId, float burnAmount)
        {
            if (!store.EnemyActive[enemyId]) return;
            if (!store.IsPlayerAlive(playerId)) return;

            float currentMana = store.GetPlayerMana(playerId);
            if (currentMana <= 0f) return; // No mana to burn

            int burnType = store.EnemyManaBurnType[enemyId];
            float drained;
            if (burnType == 1)
            {
                // Percent of current mana
                drained = currentMana * burnAmount;
            }
            else if (burnType == 2)
            {
                // Percent of max mana
                float maxMana = store.GetPlayerMaxMana(playerId);
                drained = maxMana * burnAmount;
            }
            else
            {
                // Flat amount (default)
                drained = Math.Min(currentMana, burnAmount);
            }

            store.DecreasePlayerMana(playerId, drained);
        }
    }
}
