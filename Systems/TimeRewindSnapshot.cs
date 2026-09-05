using System;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Time Rewind Snapshot System — Round 109 Direction 5.
    /// Maintains a per-player ring buffer of HP / Mana / Shield samples so the
    /// "Time Rewind" ability can restore them. Sampling is driven by the FrameScheduler
    /// in BuildPhase AND WavePhase: each tick, PlayerSnapshotTick accumulates deltaTime,
    /// and a new sample is appended when the accumulator exceeds SNAPSHOT_INTERVAL.
    ///
    /// Capacity: ComponentStore.MAX_SNAPSHOTS (20) × 0.25s sampling = 5s lookback.
    /// The Time Rewind skill uses RestoreFromSnapshot() to roll the player back to a
    /// sample ~3s in the past (configurable via SkillSystem.TimeRewindSeconds).
    ///
    /// Distinction from Save/Load:
    ///   - Snapshot is per-frame automatic, in-memory, ephemeral.
    ///   - Save/Load is full game state on disk — much heavier.
    ///
    /// Per-frame cost: O(1) per player (single tick compare + max-1 ring write).
    /// Players with no Time Rewind ability configured (the common case) still pay the
    /// sampling cost unless <c>Enabled = false</c>. We keep it always-on because
    /// the cost is two writes per 0.25s per player (negligible) and it keeps the
    /// system state-driven (any future "snapshot" skill works without code changes).
    /// </summary>
    public class TimeRewindSnapshotSystem : global::BattleSystemECS.Content.Contracts.ISnapshotRestorePort
    {
        private readonly ComponentStore store;

        // Tunables. Sampling interval matches ComponentStore.SNAPSHOT_INTERVAL.
        // RewindDuration = how far back a single Time Rewind cast rolls state (seconds).
        public float SamplingInterval = 0.25f;
        public float RewindDuration = 3.0f;

        // Diagnostic counters (read by tests / debug UI). Not gameplay-critical.
        public int TotalSamplesTaken { get; private set; }
        public int TotalRestores { get; private set; }

        public TimeRewindSnapshotSystem(ComponentStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Drive the snapshot sampler. Called by FrameScheduler once per tick (BuildPhase or WavePhase).
        /// When the per-player accumulator crosses SamplingInterval, a new sample is appended to the ring.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (deltaTime <= 0f) return;
            int maxPlayers = ComponentStore.MAX_PLAYERS;
            for (int pid = 0; pid < maxPlayers; pid++)
            {
                // Skip unused player slots cheaply. PlayerCurrentHealth defaults to 0; if a player has
                // never been added (no AddPlayer call), we treat the slot as inactive and skip sampling.
                if (store.PlayerMaxHealth[pid] <= 0f) continue;

                store.PlayerSnapshotTick[pid] += deltaTime;
                if (store.PlayerSnapshotTick[pid] < SamplingInterval) continue;
                store.PlayerSnapshotTick[pid] = 0f;

                AppendSnapshot(pid);
            }
        }

        /// <summary>
        /// Append a fresh sample to <paramref name="playerId"/>'s ring at the current head position,
        /// advancing head by 1 (mod MAX_SNAPSHOTS) and clamping Filled at MAX_SNAPSHOTS.
        /// Public for tests and manual forcing.
        /// </summary>
        public void AppendSnapshot(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return;
            int head = store.PlayerSnapshotHead[playerId];
            int slot = playerId * ComponentStore.MAX_SNAPSHOTS + head;
            store.PlayerSnapshotHP[slot] = store.PlayerCurrentHealth[playerId];
            store.PlayerSnapshotMana[slot] = store.PlayerMana[playerId];
            store.PlayerSnapshotShield[slot] = store.PlayerShield[playerId];

            store.PlayerSnapshotHead[playerId] = (head + 1) % ComponentStore.MAX_SNAPSHOTS;
            if (store.PlayerSnapshotFilled[playerId] < ComponentStore.MAX_SNAPSHOTS)
                store.PlayerSnapshotFilled[playerId]++;
            TotalSamplesTaken++;
        }

        /// <summary>
        /// How many valid samples are stored in the ring for this player.
        /// </summary>
        public int GetSampleCount(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return 0;
            return store.PlayerSnapshotFilled[playerId];
        }

        /// <summary>
        /// Compute the slot index of the sample closest to <paramref name="secondsBack"/> in the past.
        /// Returns the absolute ring index (playerId * MAX_SNAPSHOTS + slotInRing) or -1 if no data.
        /// Used by the Time Rewind skill to find which entry to restore from.
        /// </summary>
        public int FindSnapshotSlot(int playerId, float secondsBack)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return -1;
            int filled = store.PlayerSnapshotFilled[playerId];
            if (filled == 0) return -1;

            // Convert seconds back → samples back. Each sample is SamplingInterval apart.
            int samplesBack = (int)MathF.Round(secondsBack / SamplingInterval);
            if (samplesBack < 0) samplesBack = 0;
            if (samplesBack >= filled) samplesBack = filled - 1;

            // The newest sample sits at (head - 1 + MAX_SNAPSHOTS) mod MAX_SNAPSHOTS.
            int head = store.PlayerSnapshotHead[playerId];
            int newestSlot = (head - 1 + ComponentStore.MAX_SNAPSHOTS) % ComponentStore.MAX_SNAPSHOTS;
            int targetSlot = (newestSlot - samplesBack + ComponentStore.MAX_SNAPSHOTS) % ComponentStore.MAX_SNAPSHOTS;
            return playerId * ComponentStore.MAX_SNAPSHOTS + targetSlot;
        }

        /// <summary>
        /// Restore player HP / Mana / Shield from the snapshot at <paramref name="secondsBack"/> ago.
        /// Returns the actual seconds-back restored (clamped to available buffer) or -1 if no data.
        /// </summary>
        public float RestoreFromSnapshot(int playerId, float secondsBack)
        {
            return RestoreFromSnapshot(playerId, playerId, secondsBack);
        }

        public float RestoreFromSnapshot(int sourceEntityId, int playerId, float secondsBack)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return -1f;
            int filled = store.PlayerSnapshotFilled[playerId];
            if (filled == 0) return -1f;

            int samplesBack = (int)MathF.Round(secondsBack / SamplingInterval);
            if (samplesBack < 0) samplesBack = 0;
            if (samplesBack >= filled) samplesBack = filled - 1;
            float actualSeconds = samplesBack * SamplingInterval;

            int head = store.PlayerSnapshotHead[playerId];
            int newestSlot = (head - 1 + ComponentStore.MAX_SNAPSHOTS) % ComponentStore.MAX_SNAPSHOTS;
            int targetSlot = (newestSlot - samplesBack + ComponentStore.MAX_SNAPSHOTS) % ComponentStore.MAX_SNAPSHOTS;
            int absSlot = playerId * ComponentStore.MAX_SNAPSHOTS + targetSlot;

            // Cap restored HP at MaxHealth so a rewind never overheals.
            float maxHp = store.PlayerMaxHealth[playerId];
            float restoredHp = store.PlayerSnapshotHP[absSlot];
            if (restoredHp > maxHp) restoredHp = maxHp;
            if (restoredHp < 0f) restoredHp = 0f;
            if (!store.ResourceResolver.CanAccept(3, 3)) return -1f;
            if (!store.SetPlayerResourceAuthority(sourceEntityId, playerId, new Core.GAS.AttributeKey(3), restoredHp) ||
                !store.SetPlayerResourceAuthority(sourceEntityId, playerId, new Core.GAS.AttributeKey(7), store.PlayerSnapshotMana[absSlot]) ||
                !store.SetPlayerResourceAuthority(sourceEntityId, playerId, new Core.GAS.AttributeKey(9), store.PlayerSnapshotShield[absSlot]))
            {
                return -1f;
            }

            TotalRestores++;
            return actualSeconds;
        }

        /// <summary>
        /// Clear all snapshot data for a player. Used by AddPlayer and tests.
        /// </summary>
        public void Clear(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return;
            store.PlayerSnapshotHead[playerId] = 0;
            store.PlayerSnapshotFilled[playerId] = 0;
            store.PlayerSnapshotTick[playerId] = 0f;
        }
    }
}
