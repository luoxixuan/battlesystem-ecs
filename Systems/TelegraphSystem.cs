using System;
using System.Collections.Generic;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

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
    public class TelegraphSystem
    {
        private readonly ComponentStore _store;
        private readonly IRenderer _logger;
        private readonly GameConfig _gameConfig;
        private readonly IEventBus _eventBus;

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

        public TelegraphSystem(ComponentStore store, IRenderer logger, GameConfig gameConfig, IEventBus eventBus = null)
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
                ApplyDamageToPlayer(enemyId, damage, playerId);
                return;
            }

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
            {
                _logger.Log($"[TELEGRAPH] WARNING: zone pool exhausted ({MAX_TELEGRAPH_ZONES}), dropping telegraph zone");
                // Fallback: apply damage instantly
                ApplyDamageToPlayer(enemyId, damage, playerId);
                return;
            }

            _zoneActive[zoneId] = true;
            _zoneX[zoneId] = x;
            _zoneY[zoneId] = y;
            _zoneRadius[zoneId] = radius;
            _zoneDuration[zoneId] = duration;
            _zoneRemaining[zoneId] = duration;
            _zoneDamage[zoneId] = damage;
            _zonePlayerId[zoneId] = playerId;
            _zoneSourceEnemyId[zoneId] = enemyId;
            _zoneShape[zoneId] = shape;
            _zoneConeAngle[zoneId] = coneAngle;
            _zoneConeDir[zoneId] = coneDir;
            _zoneColorHint[zoneId] = colorHint;
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
                    int enemyId = _zoneSourceEnemyId[zoneId];
                    int playerId = _zonePlayerId[zoneId];
                    ApplyDamageToPlayer(enemyId, damage, playerId);
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

        private void ApplyDamageToPlayer(int enemyId, float damage, int playerId)
        {
            if (damage <= 0f) return;
            _store.DecreasePlayerHealth(playerId, damage);
            float remaining = _store.GetPlayerCurrentHealth(playerId);

            _eventBus.Publish(GameEvents.PlayerDamaged, new PlayerDamagedEvent
            {
                Damage = damage,
                RemainingHealth = remaining,
                AttackerId = enemyId
            });

            _logger.Log($"[TELEGRAPH] AoE hits player for {damage:F1} damage (HP: {remaining:F1})");
        }

        private void DeactivateZone(int zoneId)
        {
            _zoneActive[zoneId] = false;
            _zoneRemaining[zoneId] = 0f;
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