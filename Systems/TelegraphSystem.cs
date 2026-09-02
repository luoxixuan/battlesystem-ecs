using System;
using System.Collections.Generic;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Telegraph system — manages warning zones for enemy/Boss AoE abilities.
    /// 
    /// Warning zones appear as glowing areas before an AoE attack lands, giving the player
    /// reaction time to move towers or activate skills.
    /// 
    /// Two-phase pattern:
    ///   - TelegraphSystem.Update() is called during combat resolution (after EnemyAbility execute)
    ///   - TelegraphSystem.Resolve() is called at end of frame to apply damage from expired zones
    /// 
    /// Integration points:
    ///   - FrameScheduler.Tick() calls Weather.Update(deltaTime) each turn
    ///   - EnemyAbilitySystem.ExecuteAoeDamage() queues telegraph zones instead of instant damage
    ///   - FrameScheduler.Phase 5.5 (before PointDefense) calls TelegraphSystem.Update()
    ///   - FrameScheduler.Phase 8 (before Death Resolve) calls TelegraphSystem.Resolve()
    ///   - ConsoleLogger renders telegraph zones as pulsing circles (ASCII art)
    /// </summary>
    public class TelegraphSystem : global::BattleSystemECS.Content.Contracts.ITelegraphCommandPort
    {
        private readonly ComponentStore _store;
        private readonly IRenderer _logger;
        private readonly GameConfig _gameConfig;
        private readonly EventBus _eventBus;

        public const int MAX_TELEGRAPH_ZONES = 1024;

        // Telegraph zone SOA arrays
        // Active state: if true, zone exists and is counting down
        private bool[] _zoneActive = new bool[MAX_TELEGRAPH_ZONES];
        // Zone center position (world coordinates in grid units)
        private float[] _zoneX = new float[MAX_TELEGRAPH_ZONES];
        private float[] _zoneY = new float[MAX_TELEGRAPH_ZONES];
        // Zone radius (in grid units)
        private float[] _zoneRadius = new float[MAX_TELEGRAPH_ZONES];
        // Warning duration in turns (0 = instant damage, no telegraph)
        private float[] _zoneDuration = new float[MAX_TELEGRAPH_ZONES];
        // Remaining duration counter
        private float[] _zoneRemaining = new float[MAX_TELEGRAPH_ZONES];
        // Damage to apply when zone expires
        private float[] _zoneDamage = new float[MAX_TELEGRAPH_ZONES];
        // Player ID (for damage application)
        private int[] _zonePlayerId = new int[MAX_TELEGRAPH_ZONES];
        // Source enemy ID (for telegraph visualization)
        private int[] _zoneSourceEnemyId = new int[MAX_TELEGRAPH_ZONES];
        private EntityHandle[] _zoneSources = new EntityHandle[MAX_TELEGRAPH_ZONES];
        private EntityHandle[] _zoneTargets = new EntityHandle[MAX_TELEGRAPH_ZONES];
        private AbilityId[] _zoneAbilities = new AbilityId[MAX_TELEGRAPH_ZONES];
        private int[] _zoneOwnerPlayerIds = new int[MAX_TELEGRAPH_ZONES];
        // Zone type: 0=circle (default), 1=box, 2=cone
        private int[] _zoneShape = new int[MAX_TELEGRAPH_ZONES];
        // Cone angle in degrees (only used when zoneShape=2)
        private float[] _zoneConeAngle = new float[MAX_TELEGRAPH_ZONES];
        // Cone direction in radians (only used when zoneShape=2)
        private float[] _zoneConeDir = new float[MAX_TELEGRAPH_ZONES];
        // Telegraph color hint: 0=red, 1=blue, 2=yellow (for renderer)
        private int[] _zoneColorHint = new int[MAX_TELEGRAPH_ZONES];

        // Tracking list for active zones
        private List<int> _activeZoneIds = new List<int>();
        private int _nextZoneId = 0;

        // Zone shape enum (mirrored from GameConfig.AreaShapeType for internal use)
        public const int SHAPE_CIRCLE = 0;
        public const int SHAPE_BOX = 1;
        public const int SHAPE_CONE = 2;
        public const int SHAPE_LINE = 3;
        public const int SHAPE_CHAIN = 4;

        public TelegraphSystem(ComponentStore store, IRenderer logger, GameConfig gameConfig, EventBus eventBus = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            _eventBus = eventBus ?? new EventBus();
        }

        /// <summary>
        /// Queue a telegraph zone. Called by EnemyAbilitySystem.ExecuteAoeDamage() when
        /// an ability has TelegraphDuration > 0.
        /// </summary>
        /// <param name="enemyId">Source enemy ID</param>
        /// <param name="x">Zone center X</param>
        /// <param name="y">Zone center Y</param>
        /// <param name="radius">Zone radius (tiles)</param>
        /// <param name="duration">Warning duration in turns</param>
        /// <param name="damage">Damage to apply when zone expires</param>
        /// <param name="playerId">Target player ID for damage</param>
        /// <param name="shape">Zone shape (0=circle, 1=box, 2=cone)</param>
        /// <param name="coneAngle">Cone angle in degrees (for cone shape)</param>
        /// <param name="coneDir">Cone direction in radians (for cone shape)</param>
        /// <param name="colorHint">Color hint for renderer: 0=red, 1=blue, 2=yellow</param>
        public void QueueTelegraphZone(int enemyId, float x, float y, float radius,
            float duration, float damage, int playerId,
            int shape = SHAPE_CIRCLE, float coneAngle = 60f, float coneDir = 0f,
            int colorHint = 0)
        {
            if (duration <= 0f)
            {
                // No telegraph — apply damage instantly
                ApplyDamageToPlayer(_store.GetEntityHandle(enemyId), _store.GetEntityHandle(playerId),
                    damage, default(AbilityId), playerId);
                return;
            }

            if (TryQueueTelegraphZone(_store.GetEntityHandle(enemyId), _store.GetEntityHandle(playerId),
                x, y, radius, duration, damage, default(AbilityId), playerId,
                shape, coneAngle, coneDir, colorHint)) return;

            _logger.Log($"[TELEGRAPH] WARNING: zone pool exhausted ({MAX_TELEGRAPH_ZONES}), applying compatibility damage immediately");
            ApplyDamageToPlayer(_store.GetEntityHandle(enemyId), _store.GetEntityHandle(playerId),
                damage, default(AbilityId), playerId);
        }

        public bool CanQueueTelegraphZone(float duration)
        {
            if (duration <= 0f || _activeZoneIds.Count >= MAX_TELEGRAPH_ZONES) return false;
            for (int i = 0; i < MAX_TELEGRAPH_ZONES; i++) if (!_zoneActive[i]) return true;
            return false;
        }

        public bool TryQueueTelegraphZone(EntityHandle source, EntityHandle target,
            float x, float y, float radius, float duration, float damage, AbilityId ability,
            int ownerPlayerId, int shape = SHAPE_CIRCLE, float coneAngle = 60f,
            float coneDir = 0f, int colorHint = 0)
        {
            if (!source.IsValid || !target.IsValid || damage <= 0f ||
                ownerPlayerId < 0 || ownerPlayerId >= ComponentStore.MAX_PLAYERS ||
                colorHint < 0 || colorHint > 2 || !CanQueueTelegraphZone(duration)) return false;

            // Find free slot
            int zoneId = -1;
            for (int i = 0; i < MAX_TELEGRAPH_ZONES; i++)
            {
                int idx = (_nextZoneId + i) % MAX_TELEGRAPH_ZONES;
                if (!_zoneActive[idx])
                {
                    zoneId = idx;
                    _nextZoneId = (idx + 1) % MAX_TELEGRAPH_ZONES;
                    break;
                }
            }
            if (zoneId < 0)
                return false;

            _zoneActive[zoneId] = true;
            _zoneX[zoneId] = x;
            _zoneY[zoneId] = y;
            _zoneRadius[zoneId] = radius;
            _zoneDuration[zoneId] = duration;
            _zoneRemaining[zoneId] = duration;
            _zoneDamage[zoneId] = damage;
            _zonePlayerId[zoneId] = target.Index;
            _zoneSourceEnemyId[zoneId] = source.Index;
            _zoneSources[zoneId] = source;
            _zoneTargets[zoneId] = target;
            _zoneAbilities[zoneId] = ability;
            _zoneOwnerPlayerIds[zoneId] = ownerPlayerId;
            _zoneShape[zoneId] = shape;
            _zoneConeAngle[zoneId] = coneAngle;
            _zoneConeDir[zoneId] = coneDir;
            _zoneColorHint[zoneId] = colorHint;
            _activeZoneIds.Add(zoneId);
            return true;
        }

        /// <summary>
        /// Update telegraph zones — decrement timers each turn.
        /// Called from FrameScheduler during Phase 5.5.
        /// </summary>
        public void Update(float deltaTime)
        {
            for (int i = _activeZoneIds.Count - 1; i >= 0; i--)
            {
                int zoneId = _activeZoneIds[i];
                if (!_zoneActive[zoneId]) continue;

                _zoneRemaining[zoneId] -= deltaTime;
                if (_zoneRemaining[zoneId] <= 0f)
                {
                    // Zone expired — apply damage and deactivate
                    float damage = _zoneDamage[zoneId];
                    ApplyDamageToPlayer(_zoneSources[zoneId], _zoneTargets[zoneId], damage,
                        _zoneAbilities[zoneId], _zoneOwnerPlayerIds[zoneId]);
                    DeactivateZone(zoneId);
                }
            }
        }

        /// <summary>
        /// Get all active telegraph zones for rendering.
        /// Returns an array of zone data for the renderer.
        /// </summary>
        public void GetActiveZones(List<int> outZoneIds, List<float> outX, List<float> outY,
            List<float> outRadius, List<float> outRemaining, List<float> outDuration,
            List<int> outShape, List<int> outColorHint)
        {
            outZoneIds.Clear();
            outX.Clear();
            outY.Clear();
            outRadius.Clear();
            outRemaining.Clear();
            outDuration.Clear();
            outShape.Clear();
            outColorHint.Clear();

            foreach (int zoneId in _activeZoneIds)
            {
                if (!_zoneActive[zoneId]) continue;
                outZoneIds.Add(zoneId);
                outX.Add(_zoneX[zoneId]);
                outY.Add(_zoneY[zoneId]);
                outRadius.Add(_zoneRadius[zoneId]);
                outRemaining.Add(_zoneRemaining[zoneId]);
                outDuration.Add(_zoneDuration[zoneId]);
                outShape.Add(_zoneShape[zoneId]);
                outColorHint.Add(_zoneColorHint[zoneId]);
            }
        }

        /// <summary>
        /// Count of active telegraph zones.
        /// </summary>
        public int ActiveZoneCount
        {
            get
            {
                int count = 0;
                foreach (int zoneId in _activeZoneIds)
                    if (_zoneActive[zoneId]) count++;
                return count;
            }
        }

        private void ApplyDamageToPlayer(EntityHandle source, EntityHandle target, float damage,
            AbilityId ability, int ownerPlayerId)
        {
            if (damage <= 0f) return;
            var result = _store.ResourceResolver.TryApply(new PlayerDamageRequest(source, target, damage,
                _store.AllocateGameplaySequence(target.Index), ability, ownerPlayerId));
            if (!result.Accepted) return;
            float remaining = _store.GetPlayerCurrentHealth(target.Index);

            _eventBus.PlayerDamaged.Publish(new PlayerDamagedEvent
            {
                Damage = result.Applied,
                RemainingHealth = remaining,
                AttackerId = source.Index
            });

            _logger.Log($"[TELEGRAPH] AoE hits player for {result.Applied:F1} damage (HP: {remaining:F1})");
        }

        private void DeactivateZone(int zoneId)
        {
            _zoneActive[zoneId] = false;
            _zoneRemaining[zoneId] = 0f;
            _zoneSources[zoneId] = default(EntityHandle);
            _zoneTargets[zoneId] = default(EntityHandle);
            _zoneAbilities[zoneId] = default(AbilityId);
            _zoneOwnerPlayerIds[zoneId] = -1;
            // Remove from active list
            for (int i = _activeZoneIds.Count - 1; i >= 0; i--)
            {
                if (_activeZoneIds[i] == zoneId)
                {
                    _activeZoneIds.RemoveAt(i);
                    break;
                }
            }
        }
    }
}
