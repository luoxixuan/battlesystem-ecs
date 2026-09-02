using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Random Mid-Wave Events System — triggers surprise events during wave gameplay.
    ///
    /// Event types:
    ///   1 = Ambush:     extra enemies spawn from map flanks
    ///   2 = SupplyDrop: timed chest spawns with gold/mana/buff
    ///   3 = Earthquake: all units (enemies + towers) take AoE damage, enemies slowed
    ///   4 = BossRush:   mini-boss spawns mid-wave
    ///   5 = Merchant:   temporary discount shop appears
    ///
    /// Integration:
    ///   - FrameScheduler.Tick() calls RandomEvent.Update(deltaTime, currentWave)
    ///   - 环境事件通过 IWaveSpawningPort 追加敌人
    ///   - 补给事件通过 IPickupCommandPort 生成掉落
    ///   - Earthquake applies damage via BuffSystem / direct ComponentStore
    ///   - 商人事件通过 IMerchantModifierPort 应用折扣
    ///   - OnEventTriggered event for UI notification
    /// </summary>
    public class RandomEventSystem
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;
        private readonly Random rng = new Random();

        // Per-player event state (indexed by playerId)
        private bool[] _eventApplied = new bool[ComponentStore.MAX_PLAYERS];

        // Events
        public event Action<int, string> OnEventTriggered; // playerId, eventName
        public event Action<int, string> OnEventEnded;     // playerId, eventName
        private const int MaxPendingCallbacks = ComponentStore.MAX_PLAYERS * 2;
        private readonly int[] _pendingCallbackPlayer = new int[MaxPendingCallbacks];
        private readonly byte[] _pendingCallbackKind = new byte[MaxPendingCallbacks];
        private readonly string[] _pendingCallbackName = new string[MaxPendingCallbacks];
        private int _pendingCallbackCount;

        // Reference to other systems (set by GameManager)
        private global::BattleSystemECS.Content.Contracts.IWaveSpawningPort waveSpawning;
        private global::BattleSystemECS.Content.Contracts.IPickupCommandPort pickup;
        private global::BattleSystemECS.Content.Contracts.IMerchantModifierPort interest;
        private global::BattleSystemECS.Content.Contracts.IGoldRewardPort gold;

        public RandomEventSystem(ComponentStore store, GameConfig gameConfig)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
        }

        public void SetWaveSpawning(global::BattleSystemECS.Content.Contracts.IWaveSpawningPort ws) => waveSpawning = ws;
        public void SetPickupSystem(global::BattleSystemECS.Content.Contracts.IPickupCommandPort ps) => pickup = ps;
        public void SetInterestSystem(global::BattleSystemECS.Content.Contracts.IMerchantModifierPort iss) => interest = iss;
        public void SetGoldSystem(global::BattleSystemECS.Content.Contracts.IGoldRewardPort gs) => gold = gs;

        /// <summary>
        /// Called each frame from FrameScheduler (WavePhase).
        /// Updates all event timers, handles cooldown tracking, and triggers new events.
        /// </summary>
        public void Update(float deltaTime, int currentWave, int currentLevel)
        {
            for (int playerId = 0; playerId < ComponentStore.MAX_PLAYERS; playerId++)
            {
                if (store.PlayerCurrentHealth[playerId] <= 0) continue;

                UpdatePlayerEvent(playerId, deltaTime, currentWave, currentLevel);
            }
        }

        private void UpdatePlayerEvent(int playerId, float deltaTime, int currentWave, int currentLevel)
        {
            int activeType = store.RandomEventActiveType[playerId];

            if (activeType != RandomEventConfig.None)
            {
                // ── Event is active — tick timer ────────────────────────────────
                float timer = store.RandomEventTimer[playerId];
                if (timer > 0f)
                {
                    timer -= deltaTime;
                    store.RandomEventTimer[playerId] = timer;
                }

                // Apply event effects once on first frame
                if (!_eventApplied[playerId])
                {
                    ApplyEvent(playerId, activeType);
                    _eventApplied[playerId] = true;
                }

                if (timer <= 0f && store.RandomEventTimer[playerId] <= 0f)
                {
                    // Event duration expired — end it
                    EndEvent(playerId, activeType);
                }
            }
            else
            {
                // ── No active event — check cooldown and maybe trigger one ────────
                float cooldown = store.RandomEventCooldown[playerId];
                if (cooldown > 0f)
                {
                    cooldown -= deltaTime;
                    store.RandomEventCooldown[playerId] = cooldown;
                }
                else if (cooldown <= 0f)
                {
                    // Cooldown ready — roll for a new event
                    TryTriggerEvent(playerId, currentWave, currentLevel);
                }
            }
        }

        private void TryTriggerEvent(int playerId, int currentWave, int currentLevel)
        {
            var config = gameConfig.RandomEvents;
            if (config == null || config.Events.Count == 0) return;

            // Global chance check
            if (rng.NextDouble() >= config.GlobalEventChance) return;

            // Collect eligible events (weight-based selection)
            float totalWeight = 0f;
            var eligible = new List<RandomEventDef>();

            foreach (var evt in config.Events)
            {
                if (evt.Weight <= 0f) continue;
                if (evt.MinWave > 0 && currentWave < evt.MinWave) continue;
                if (evt.MaxWave >= 0 && currentWave > evt.MaxWave) continue;
                // Per-type cooldown (tracked via event def id as a proxy)
                // Simple approach: only allow trigger if cooldown has passed
                // We approximate this by using the global cooldown per player
                eligible.Add(evt);
                totalWeight += evt.Weight;
            }

            if (eligible.Count == 0 || totalWeight <= 0f) return;

            // Weighted random selection
            float roll = (float)(rng.NextDouble() * totalWeight);
            float cumulative = 0f;
            RandomEventDef chosen = null;

            foreach (var evt in eligible)
            {
                cumulative += evt.Weight;
                if (roll <= cumulative)
                {
                    chosen = evt;
                    break;
                }
            }

            if (chosen == null) return;

            // Activate the event
            int eventType = chosen.EventType;
            store.RandomEventActiveType[playerId] = eventType;
            store.RandomEventTimer[playerId] = chosen.Duration;
            store.RandomEventParam[playerId] = chosen.Param;
            store.RandomEventParam2[playerId] = chosen.Param2;
            store.RandomEventCooldown[playerId] = chosen.Cooldown;
            _eventApplied[playerId] = false;

        }

        private void ApplyEvent(int playerId, int eventType)
        {
            float param = store.RandomEventParam[playerId];
            float param2 = store.RandomEventParam2[playerId];

            switch (eventType)
            {
                case RandomEventConfig.Ambush:
                    // Spawn extra enemies via WaveSpawning (extra batch injected mid-wave)
                    if (waveSpawning != null)
                    {
                        int extraCount = (int)(param > 0f ? param : 5f);
                        waveSpawning.InjectExtraEnemies(extraCount);
                    }
                    break;

                case RandomEventConfig.SupplyDrop:
                    // Directly grant gold/mana to player (simpler, more reliable than pickup system)
                    if (gold != null)
                    {
                        float goldAmount = param > 0f ? param : 50f;
                        float current = store.GetPlayerGold(playerId);
                        store.SetPlayerGold(playerId, current + goldAmount);
                    }
                    // 可选地通过 IPickupCommandPort 生成法力掉落。
                    if (pickup != null && param2 > 0f)
                    {
                        float manaAmount = param2; // param2 = mana amount if provided
                        int pickupType = 2; // ManaOrb
                        // Spawn near player base at y=1
                        pickup.SpawnPickup(pickupType, 5f, 1f, playerId, manaAmount);
                    }
                    break;

                case RandomEventConfig.Earthquake:
                    // Deal AoE damage to all enemies (param = damage, param2 = slow factor)
                    float dmg = param > 0f ? param : 20f;
                    float slow = param2 > 0f ? param2 : 0.5f;
                    ApplyEarthquakeDamage(playerId, dmg, slow);
                    break;

                case RandomEventConfig.BossRush:
                    // Spawn a mini-boss mid-wave via WaveSpawning
                    if (waveSpawning != null)
                    {
                        waveSpawning.InjectMiniBoss();
                    }
                    break;

                case RandomEventConfig.Merchant:
                    // Apply temporary gold discount to interest system
                    if (interest != null)
                    {
                        float discount = param > 0f ? param : 0.3f; // 30% discount default
                        interest.ApplyMerchantDiscount(playerId, discount);
                    }
                    break;

                case 6:
                    // TimeDilation: slow global time temporarily
                    if (param > 0f)
                    {
                        store.GlobalTimeScale[playerId] = param;
                        store.GlobalTimeScaleDuration[playerId] = param2 > 0f ? param2 : 10f;
                    }
                    break;

                case 7:
                    // HealWave: heal all active enemies on the map
                    if (param > 0f)
                    {
                        float healAmount = param;
                        var enemyIds = store.ActiveEnemyIds;
                        int count = enemyIds.Count;
                        for (int i = 0; i < count; i++)
                        {
                            int eid = enemyIds[i];
                            if (eid >= 0)
                            {
                                store.ApplyEnemyResourceAuthority(eid, eid, new Core.GAS.AttributeKey(3), healAmount);
                            }
                        }
                    }
                    break;
            }
            QueueCallback(playerId,GetEventName(eventType),1);
        }

        private void ApplyEarthquakeDamage(int playerId, float damage, float slowFactor)
        {
            // Apply flat damage to all active enemies (direct HP reduction)
            var enemyIds = store.ActiveEnemyIds;
            for (int i = 0; i < enemyIds.Count; i++)
            {
                int eid = enemyIds[i];
                if (!store.EnemyActive[eid]) continue;

                float currentHp = store.EnemyHealth[eid];
                if (currentHp > 0f)
                {
                    store.ApplyDamageAuthority(playerId, eid, damage, playerId, stage: Core.GAS.DamageAmountStage.Raw);
                }
            }

            // Apply slow to all enemies
            if (slowFactor < 1f)
            {
                for (int i = 0; i < enemyIds.Count; i++)
                {
                    int eid = enemyIds[i];
                    if (!store.EnemyActive[eid]) continue;
                    // Per-type CC immunity (Round 97): Slow bit or Unstoppable blocks this event-slow
                    if (store.IsCCImmuneTo(eid, CCImmunityConfig.Mask_Slow)) continue;
                    store.EnemySlowFactor[eid] = Math.Min(store.EnemySlowFactor[eid], slowFactor);
                    store.EnemySlowDurationLeft[eid] = Math.Max(store.EnemySlowDurationLeft[eid], 5f); // 5 turn slow
                }
            }
        }

        private void EndEvent(int playerId, int eventType)
        {
            var config = gameConfig.RandomEvents;
            string eventName = GetEventName(eventType);

            // Apply end-of-event rewards (bonus gold / research for surviving)
            if (config != null)
            {
                foreach (var evt in config.Events)
                {
                    if (evt.EventType == eventType)
                    {
                        if (evt.BonusGold > 0f && gold != null)
                        {
                            float current = store.GetPlayerGold(playerId);
                            store.SetPlayerGold(playerId, current + evt.BonusGold);
                        }
                        if (evt.BonusResearch > 0)
                        {
                            store.PlayerResearchPoints[playerId] += evt.BonusResearch;
                        }
                        break;
                    }
                }
            }

            // Clean up
            store.RandomEventActiveType[playerId] = RandomEventConfig.None;
            store.RandomEventTimer[playerId] = 0f;
            store.RandomEventParam[playerId] = 0f;
            store.RandomEventParam2[playerId] = 0f;
            _eventApplied[playerId] = false;

            // Reset Merchant discount if merchant event ended
            if (eventType == RandomEventConfig.Merchant && interest != null)
            {
                interest.ResetMerchantDiscount(playerId);
            }

            QueueCallback(playerId, eventName, 2);
        }

        private string GetEventName(int eventType)
        {
            var config=gameConfig.RandomEvents;
            if(config!=null)
            {
                foreach(var evt in config.Events)
                    if(evt.EventType==eventType)return evt.Name;
            }
            return "Unknown Event";
        }

        private void QueueCallback(int playerId, string eventName, byte kind)
        {
            if (_pendingCallbackCount >= MaxPendingCallbacks)
                throw new InvalidOperationException("Random event callback batch capacity exceeded before dispatch.");
            int index=_pendingCallbackCount++;
            _pendingCallbackPlayer[index]=playerId;
            _pendingCallbackKind[index]=kind;
            _pendingCallbackName[index]=eventName;
        }

        public void DispatchPendingCallbacks()
        {
            int count=_pendingCallbackCount;
            _pendingCallbackCount=0;
            for (int i = 0; i < count; i++)
            {
                int playerId=_pendingCallbackPlayer[i];
                byte kind = _pendingCallbackKind[i];
                string eventName = _pendingCallbackName[i];
                _pendingCallbackKind[i] = 0;
                _pendingCallbackName[i] = null;
                if (kind == 1) OnEventTriggered?.Invoke(playerId, eventName);
                else OnEventEnded?.Invoke(playerId, eventName);
            }
        }

        /// <summary>
        /// Force-trigger a specific event type (for testing or story beats).
        /// </summary>
        public void ForceEvent(int playerId, int eventType, float duration = 10f)
        {
            store.RandomEventActiveType[playerId] = eventType;
            store.RandomEventTimer[playerId] = duration;
            store.RandomEventCooldown[playerId] = 0f;
            _eventApplied[playerId] = false;
        }
    }
}
