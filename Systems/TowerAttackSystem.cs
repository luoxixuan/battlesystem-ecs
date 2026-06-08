using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower attack system - handles tower target acquisition and enemy damage + debuffs.
    /// Two-phase: parallel collect, serial resolve (Bug#2 thread-safety fix).
    /// Tower type-specific mechanics (Tesla chain lightning, Leech lifesteal, Frost slow, Firewall DoT).
    /// </summary>
    public class TowerAttackSystem
    {
        private ComponentStore store;
        private IRenderer logger;
        private TechTreeSystem techTreeSystem;
        private BuffSystem buffSystem;
        private BleedSystem bleedSystem;
        private DeathMarkSystem deathMarkSystem; // injected for stack-based execute counter
        private TowerExperienceSystem towerExperienceSystem;
        private ProjectileSystem projectileSystem;
        private WeatherSystem _weatherSystem; // injected for weather effects
        private DayNightSystem _dayNightSystem; // injected for day/night cycle effects
        private HeatSystem _heatSystem; // injected for heat/overheat effects
        private TowerEnergySystem _energySystem; // injected for energy system effects
        private HitShieldSystem _hitShieldSystem; // injected for N-hit shield blocking
        private EnemyStrafeSystem _enemyStrafeSystem; // injected for enemy dodge/strafe
        private DesperationSystem _desperationSystem; // injected for last stand damage/speed bonuses
        // Round 143 Direction 1 — Tower-vs-Enemy type effectiveness matrix.
        // Optional injection; null disables the feature (multiplier = 1.0).
        // Lookups are O(1) Dictionary<string,float> with composite "<int>|<string>" key.
        private GameConfig _gameConfig;
        // Cached hot-path flag — bypass the lookup entirely when the matrix is empty
        // (the file was missing or had no entries). Saves the string-allocation per hit
        // in the common case where designers haven't configured effectiveness yet.
        private bool _hasEffectiveness;
        // Round 174 Direction 4 — Backstab master switch (cached at SetGameConfig time so
        // the hot path doesn't re-read the config object on every attack). When false, the
        // backstab block is fully skipped (zero overhead) even if a tower has a non-1.0
        // TowerBackstabDamageMult — designers get a single global kill-switch.
        private bool _backstabEnabled;
        // Cached desperation bonuses (updated each SetTurn from DesperationSystem)
        private float _desperationDmgBonus = 0f;
        private float _desperationSpeedBonus = 0f;
        // Round 128 Direction 5 — Fire Trail System. Optional injection; null when
        // not wired (in which case Firewall hits just apply their normal DoT and
        // do not leave a burning patch). Calling SpawnTrail with a null reference
        // is a no-op, so the hot path stays branch-free on the null case.
        private FireTrailSystem _fireTrailSystem;
        private List<int> _activeEnemyList;

        // GC elimination: per-tower reusable candidate arrays (zero-allocation — no List.Clear() version bump)
        private int[][] _towerCandidateBuffers = Array.Empty<int[]>();
        private int[] _towerCandidateCounts = Array.Empty<int>();

        // Ping-pong double-buffer: eliminates per-frame new ConcurrentBag<>() allocation
        private List<(int enemyId, float damage, int playerId, int towerId)>[] _damageQueue = new List<(int, float, int, int)>[2];
        private readonly object _damageQueueLock = new object();
        private int _damageQueueIdx = 0;

        // Round 67: On-Crit side-channel for the tower attack path.
        // The parallel phase marks tower+enemy pairs when a crit is rolled (line 902 area).
        // The serial apply phase looks up (enemyId, towerId) in this set to know whether
        // to publish EnemyCrit. Set is per-frame and cleared at end of Update().
        // Default size 32 covers all active towers in typical benches; Set grows on demand.
        private readonly HashSet<long> _critFiredThisFrame = new HashSet<long>(32);
        // Round 67: EventBus for On-Hit / On-Crit trigger publication.
        private readonly IEventBus _eventBus;

        // Ping-pong double-buffer for tower debuff events (collected parallel, applied serial)
        private List<(int enemyId, int towerId)>[] _debuffQueue = new List<(int, int)>[2];
        private readonly object _debuffQueueLock = new object();
        private int _debuffQueueIdx = 0;

        // Ping-pong double-buffer for tower type-specific events (Leech lifesteal heal, etc.)
        private List<(int playerId, float healAmount)>[] _healQueue = new List<(int, float)>[2];
        private readonly object _healQueueLock = new object();
        private int _healQueueIdx = 0;

        // Ping-pong double-buffer for thorns damage reflect (enemy -> player)
        private List<(int playerId, float damage)>[] _thornsQueue = new List<(int, float)>[2];
        private readonly object _thornsQueueLock = new object();
        private int _thornsQueueIdx = 0;

        // Cached player armor stats (updated each SetTurn)
        private float _armorPenetration = 0f;  // from TechTreeSystem
        private float _damageTakenMult = 1f;   // from TechTreeSystem

        // Cached enemy CC resistance stats (updated each SetTurn — from TechTreeSystem getters)
        private float _enemyStunResistance = 0f;  // from techTreeSystem.GetStunResistance()
        private float _enemySlowResistance = 0f;    // from techTreeSystem.GetSlowResistance()

        // Cached wave-based difficulty multiplier (updated each SetTurn)
        private float _waveDifficultyMult = 1f;

        // Cached crit bonuses from TechTreeSystem (updated each SetTurn)
        private float _critRateBonus = 0f;      // from techTreeSystem.GetCritRateBonus()
        private float _critDamageBonus = 1f;     // from techTreeSystem.GetCritDamageMult()
        // Cached armor shred per stack from TechTreeSystem (flat armor reduction per stack)
        private float _armorShredPerStack = 0f;

        // Shared random for debuff chance rolls — uses Random.Shared (.NET 6+ thread-safe)
        private static readonly Random _rand = Random.Shared;

        // Map width minus one (used for knockback bound clamping)
        private readonly float _mapWidthMinusOne;

        // Ping-pong double-buffer for Tesla chain lightning damage events
        // Tuple: (chainId, enemyId, damage, playerId, towerId)
        //   - chainId=-1: non-chain basic damage (from default case chain upgrade)
        //   - chainId=0: primary target (already in _damageQueue, queued here for chain logic)
        //   - chainId=1..3: chain hop damage (generated by ResolveTeslaChainLightning)
        private List<(int chainId, int enemyId, float damage, int playerId, int towerId)>[] _chainDamageQueue = new List<(int, int, float, int, int)>[2];
        private readonly object _chainDamageQueueLock = new object();
        private int _chainDamageQueueIdx = 0;

        // Cached weather multipliers (updated each SetTurn from WeatherSystem)
        private float _weatherRangeMult = 1f;
        private float _weatherDamageMult = 1f;

        // Cached day/night cycle multipliers (updated each SetTurn from DayNightSystem)
        private float _dayNightRangeMult = 1f;

        // Ping-pong double-buffer for splash damage events (from upgrade special abilities)
        private List<(int primaryEnemyId, float splashDamage, int playerId, int towerId)>[] _splashDamageQueue = new List<(int, float, int, int)>[2];
        private readonly object _splashDamageQueueLock = new object();
        private int _splashDamageQueueIdx = 0;

        // Ping-pong double-buffer for tower kill events (granted XP on kill)
        private List<(int enemyId, int playerId, int towerId)>[] _towerKillQueue = new List<(int, int, int)>[2];
        private readonly object _towerKillQueueLock = new object();
        private int _towerKillQueueIdx = 0;

        // Ping-pong double-buffer for bounce damage events
        // Tuple: (bounceLevel, enemyId, damage, playerId, towerId)
        //   bounceLevel=0: initial hit (already in _damageQueue)
        //   bounceLevel=1..N: bounce hop damage
        private List<(int bounceLevel, int enemyId, float damage, int playerId, int towerId)>[] _bounceDamageQueue = new List<(int, int, float, int, int)>[2];
        private readonly object _bounceDamageQueueLock = new object();
        private int _bounceDamageQueueIdx = 0;

        // Fragment projectile events: collected parallel, fired serial via ProjectileSystem
        // Tuple: (enemyId, damage, playerId, towerId, fragCount, fragRange)
        private List<(int enemyId, float damage, int playerId, int towerId, int fragCount, float fragRange)>[] _fragmentQueue = new List<(int, float, int, int, int, float)>[2];
        private readonly object _fragmentQueueLock = new object();
        private int _fragmentQueueIdx = 0;

        // Leech lifesteal rate: 30% of damage dealt is returned as player heal
        private const float LEECH_LIFESTEAL_RATE = 0.30f;
        // Healing suppression: each tower hit applies 30% healing reduction for 2 turns
        private const float HEALING_REDUCTION_AMOUNT = 0.30f;
        private const float HEALING_REDUCTION_DURATION = 2f;

        public TowerAttackSystem(ComponentStore store, IRenderer logger, TechTreeSystem techTreeSystem = null, int mapWidth = 10)
            : this(store, logger, techTreeSystem, mapWidth, null)
        {
        }

        // Round 67: IEventBus optional injection for On-Hit / On-Crit publication.
        public TowerAttackSystem(ComponentStore store, IRenderer logger, TechTreeSystem techTreeSystem, int mapWidth, IEventBus eventBus)
        {
            this.store = store;
            this.logger = logger;
            this.techTreeSystem = techTreeSystem;
            this._mapWidthMinusOne = mapWidth - 1f;
            this._eventBus = eventBus ?? new EventBus();
            _damageQueue[0] = new List<(int, float, int, int)>(256);
            _damageQueue[1] = new List<(int, float, int, int)>(256);
            _debuffQueue[0] = new List<(int, int)>(256);
            _debuffQueue[1] = new List<(int, int)>(256);
            _healQueue[0] = new List<(int, float)>(64);
            _healQueue[1] = new List<(int, float)>(64);
            _thornsQueue[0] = new List<(int, float)>(64);
            _thornsQueue[1] = new List<(int, float)>(64);
            _chainDamageQueue[0] = new List<(int, int, float, int, int)>(64);
            _chainDamageQueue[1] = new List<(int, int, float, int, int)>(64);
            _splashDamageQueue[0] = new List<(int, float, int, int)>(64);
            _splashDamageQueue[1] = new List<(int, float, int, int)>(64);
            _towerKillQueue[0] = new List<(int, int, int)>(64);
            _towerKillQueue[1] = new List<(int, int, int)>(64);
            _bounceDamageQueue[0] = new List<(int, int, float, int, int)>(64);
            _bounceDamageQueue[1] = new List<(int, int, float, int, int)>(64);
            _fragmentQueue[0] = new List<(int, float, int, int, int, float)>(64);
            _fragmentQueue[1] = new List<(int, float, int, int, int, float)>(64);
        }

        /// <summary>
        /// Inject EnemyStrafeSystem for event-driven dodge checks.
        /// </summary>
        public void SetEnemyStrafeSystem(EnemyStrafeSystem enemyStrafeSystem)
        {
            _enemyStrafeSystem = enemyStrafeSystem;
        }

        /// <summary>
        /// Inject BuffSystem reference for Leech lifesteal healing and Firewall DoT effects.
        /// </summary>
        public void SetBuffSystem(BuffSystem buffSystem)
        {
            this.buffSystem = buffSystem;
        }

        /// <summary>
        /// Inject BleedSystem reference for bleed application on tower hits.
        /// </summary>
        public void SetBleedSystem(BleedSystem bleedSystem)
        {
            this.bleedSystem = bleedSystem;
        }

        /// <summary>
        /// Round 200 Direction 5 — Inject DeathMarkSystem reference for stacking execute counter
        /// on tower hits. Late-bound like BleedSystem (no-op when null, which is the default for
        /// pre-existing test harnesses).
        /// </summary>
        public void SetDeathMarkSystem(DeathMarkSystem deathMarkSystem)
        {
            this.deathMarkSystem = deathMarkSystem;
        }

        /// <summary>
        /// Round 143 Direction 1 — Inject the GameConfig to read the
        /// tower-vs-enemy type effectiveness matrix. Late-bound so existing
        /// SystemRegistry wiring (which doesn't pass GameConfig) keeps compiling.
        /// When null or matrix is empty, effectiveness multiplier defaults to 1.0.
        /// </summary>
        public void SetGameConfig(GameConfig config)
        {
            _gameConfig = config;
            _hasEffectiveness = config != null
                && config.TowerEffectivenessMatrix != null
                && config.TowerEffectivenessMatrix.Count > 0;
            // Round 174 Direction 4 — cache the backstab master switch. When the
            // BackstabConfig is missing or disabled, the hot path skips the entire
            // backstab block, including the two float reads.
            _backstabEnabled = config != null
                && config.Backstab != null
                && config.Backstab.Enabled;
        }

        /// <summary>
        /// Round 143 Direction 1 — Compute the effectiveness multiplier for a tower attacking
        /// a given enemy. Returns 1.0 (no change) when the matrix is empty / entry is missing.
        /// Hot path: O(1) dictionary lookup, single string allocation per call (kept on the
        /// stack-path side via the string.Concat overload; no LINQ, no boxing).
        /// </summary>
        private float GetEffectivenessMultiplier(int towerTypeIndex, int enemyId)
        {
            if (!_hasEffectiveness || _gameConfig == null) return 1.0f;
            if ((uint)enemyId >= ComponentStore.MAX_ENTITIES) return 1.0f;
            string enemyType = store.GetEnemyTypeName(enemyId);
            if (string.IsNullOrEmpty(enemyType)) return 1.0f;
            // Build composite key without string.Format to avoid culture / boxing overhead.
            // We use the simple "+" concat — both operands are small (int + short string).
            string key = towerTypeIndex.ToString() + "|" + enemyType;
            if (_gameConfig.TowerEffectivenessMatrix.TryGetValue(key, out float mult)) return mult;
            return 1.0f;
        }

        /// <summary>
        /// Inject TowerExperienceSystem reference for XP grant on kills.
        /// </summary>
        public void SetTowerExperienceSystem(TowerExperienceSystem system)
        {
            this.towerExperienceSystem = system;
        }

        /// <summary>
        /// Inject ProjectileSystem reference for fragment (split) projectile spawning.
        /// </summary>
        public void SetProjectileSystem(ProjectileSystem projectileSystem)
        {
            this.projectileSystem = projectileSystem;
        }

        /// <summary>
        /// Inject WeatherSystem reference for dynamic weather effects on tower range and damage.
        /// </summary>
        public void SetWeatherSystem(WeatherSystem weather)
        {
            _weatherSystem = weather;
        }

        /// <summary>
        /// Inject DayNightSystem reference for day/night cycle effects on tower range.
        /// </summary>
        public void SetDayNightSystem(DayNightSystem dayNight)
        {
            _dayNightSystem = dayNight;
        }

        /// <summary>
        /// Inject HeatSystem reference for heat/overheat effects on tower attacks.
        /// </summary>
        public void SetHeatSystem(HeatSystem heatSystem)
        {
            _heatSystem = heatSystem;
        }

        /// <summary>
        /// Inject DesperationSystem reference for last stand damage/speed bonuses.
        /// </summary>
        public void SetDesperationSystem(DesperationSystem desperationSystem)
        {
            _desperationSystem = desperationSystem;
        }

        /// <summary>
        /// Round 128 Direction 5 — inject FireTrailSystem so the Firewall hit path
        /// can leave a brief burning patch at the enemy position. May be null
        /// (no-op in that case — the Firewall DoT still applies normally).
        /// </summary>
        public void SetFireTrailSystem(FireTrailSystem fireTrailSystem)
        {
            _fireTrailSystem = fireTrailSystem;
        }

        /// <summary>
        /// Inject TowerEnergySystem reference for energy consumption effects on tower attacks.
        /// </summary>
        public void SetEnergySystem(TowerEnergySystem energySystem)
        {
            _energySystem = energySystem;
        }

        /// <summary>
        /// Inject HitShieldSystem reference for N-hit shield blocking.
        /// </summary>
        public void SetHitShieldSystem(HitShieldSystem hitShieldSystem)
        {
            _hitShieldSystem = hitShieldSystem;
        }

        private TowerStealthSystem _towerStealthSystem;

        /// <summary>
        /// Inject TowerStealthSystem reference for stealth targeting filters and decloak-on-fire.
        /// </summary>
        public void SetTowerStealthSystem(TowerStealthSystem towerStealthSystem)
        {
            _towerStealthSystem = towerStealthSystem;
        }

        private EnemyLifeLinkSystem _lifeLinkSystem;

        /// <summary>
        /// Inject EnemyLifeLinkSystem reference for damage-sharing link computation.
        /// </summary>
        public void SetLifeLinkSystem(EnemyLifeLinkSystem lifeLinkSystem)
        {
            _lifeLinkSystem = lifeLinkSystem;
        }

        public void SetTurn(int turn)
        {
            _activeEnemyList = store.GetCachedActiveEnemyIds();  // zero allocation — frame cache

            // Cache weather multipliers (updated each turn)
            if (_weatherSystem != null)
            {
                _weatherRangeMult = _weatherSystem.GetTowerRangeMultiplier(0);
                _weatherDamageMult = _weatherSystem.GetTowerDamageMultiplier(0);
            }
            else
            {
                _weatherRangeMult = 1f;
                _weatherDamageMult = 1f;
            }

            // Cache day/night cycle range multiplier (updated each turn)
            if (_dayNightSystem != null)
                _dayNightRangeMult = _dayNightSystem.GetTowerRangeMultiplier(0);
            else
                _dayNightRangeMult = 1f;

            // Cache armor stats from tech tree
            _armorPenetration = techTreeSystem != null ? techTreeSystem.GetArmorPenetration() : 0f;
            _damageTakenMult = techTreeSystem != null ? techTreeSystem.GetDamageTakenMult() : 1f;

            // Cache enemy CC resistance stats from tech tree (stun/slow duration reduction)
            _enemyStunResistance = techTreeSystem != null ? techTreeSystem.GetStunResistance() : 0f;
            _enemySlowResistance = techTreeSystem != null ? techTreeSystem.GetSlowResistance() : 0f;

            // Cache crit bonuses from tech tree (avoid per-tower calls in hot path)
            _critRateBonus = techTreeSystem != null ? techTreeSystem.GetCritRateBonus() : 0f;
            _critDamageBonus = techTreeSystem != null ? techTreeSystem.GetCritDamageMult() : 1f;

            // Cache armor shred per stack from tech tree
            _armorShredPerStack = techTreeSystem != null ? techTreeSystem.GetArmorShredPerStack() : 0f;

            // Cache wave-based difficulty multiplier (default wave 1)
            _waveDifficultyMult = techTreeSystem != null ? techTreeSystem.GetWaveDifficultyMultiplier(1) : 1f;

            // Cache desperation bonuses from DesperationSystem
            if (_desperationSystem != null)
            {
                _desperationDmgBonus = _desperationSystem.DamageBonus;
                _desperationSpeedBonus = _desperationSystem.SpeedBonus;
            }
            else
            {
                _desperationDmgBonus = 0f;
                _desperationSpeedBonus = 0f;
            }

            // Ensure _towerCandidateBuffers is large enough; each slot is a reusable int[]
            var towerIds = store.ActiveTowerIds;
            if (_towerCandidateBuffers.Length < towerIds.Count)
            {
                var newBuffers = new int[towerIds.Count][];
                var newCounts = new int[towerIds.Count];
                Array.Copy(_towerCandidateBuffers, newBuffers, _towerCandidateBuffers.Length);
                Array.Copy(_towerCandidateCounts, newCounts, _towerCandidateCounts.Length);
                for (int i = _towerCandidateBuffers.Length; i < newBuffers.Length; i++)
                    newBuffers[i] = new int[ComponentStore.MAX_ENTITIES];
                _towerCandidateBuffers = newBuffers;
                _towerCandidateCounts = newCounts;
            }

            // ── Auto-link towers with TowerChainDmgRatio > 0 to nearest neighbor ──
            AutoLinkChainPartners();
        }

        /// <summary>
        /// Update the cached wave difficulty multiplier when wave number changes.
        /// Call this when a new wave starts.
        /// </summary>
        public void SetWaveNumber(int waveNumber)
        {
            _waveDifficultyMult = techTreeSystem != null ? techTreeSystem.GetWaveDifficultyMultiplier(waveNumber) : 1f;
        }

        public void Update(float deltaTime)
        {
            var activeTowerIds = store.ActiveTowerIds;

            // Defensive: ensure _towerCandidateBuffers covers all towers before parallel loop.
            if (_towerCandidateBuffers.Length < activeTowerIds.Count)
            {
                var newBuffers = new int[activeTowerIds.Count][];
                var newCounts = new int[activeTowerIds.Count];
                Array.Copy(_towerCandidateBuffers, newBuffers, _towerCandidateBuffers.Length);
                Array.Copy(_towerCandidateCounts, newCounts, _towerCandidateCounts.Length);
                for (int i = _towerCandidateBuffers.Length; i < newBuffers.Length; i++)
                    newBuffers[i] = new int[ComponentStore.MAX_ENTITIES];
                _towerCandidateBuffers = newBuffers;
                _towerCandidateCounts = newCounts;
            }

            // Phase 0: Spatial grid already rebuilt by GameManager before system chain.
            // Reuse instead of rebuilding — avoids O(enemies) waste per frame.

            // Phase 1 (parallel): collect damage events and debuff events — no structural mutations.
            var bag = _damageQueue[_damageQueueIdx];
            var debuffBag = _debuffQueue[_debuffQueueIdx];
            var chainBag = _chainDamageQueue[_chainDamageQueueIdx];
            var healBag = _healQueue[_healQueueIdx];
            var splashBag = _splashDamageQueue[_splashDamageQueueIdx];
            var bounceBag = _bounceDamageQueue[_bounceDamageQueueIdx];
            var fragmentBag = _fragmentQueue[_fragmentQueueIdx];
            var damageLock = _damageQueueLock;
            var debuffLock = _debuffQueueLock;
            var chainLock = _chainDamageQueueLock;
            var healLock = _healQueueLock;
            var splashLock = _splashDamageQueueLock;
            var bounceLock = _bounceDamageQueueLock;
            var fragmentLock = _fragmentQueueLock;

            Parallel.For(0, activeTowerIds.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, ti =>
            {
                int towerId = activeTowerIds[ti];

                store.TowerLastAttackTime[towerId] += deltaTime;

                // Round 186 Direction 2 — Sapper-destroyed towers skip combat. Legacy
                // indestructible towers have TowerMaxHp == 0, so the check is a no-op
                // for them; only towers with a non-zero MaxHp opt in to Sapper damage.
                if (store.TowerMaxHp[towerId] > 0f && store.TowerCurrentHp[towerId] <= 0f) return;

                // Round 180 Direction 5: Fortress atk-speed bonus (additive with HotZone + Desperation).
                // 0f when tower has no fortress cluster (zero overhead for isolated towers).
                // Round 186 Direction 2: TowerSapperSlowMult is the cumulative attack-speed slow
                // applied by all Sappers targeting this tower (re-derived each frame in
                // SapperSystem.RecomputeTowerSlows, then BeginFrame resets it to 0 — so we
                // apply the multiplier that was rolled up for THIS frame). 0f fast path for
                // non-targeted towers.
                // Round 187 Direction 4: TowerRallyAtkSpdBonus is the additive attack-speed
                // bonus contributed by any active Rally buffs on this tower. 0f fast path
                // when no rally is active. Layered additively with HotZone/Fortress/Desperation.
                float fortressAtkSpdBonus = store.GetTowerFortressAtkSpdBonus(towerId);
                float rallyAtkSpdBonus = store.TowerRallyAtkSpdBonus[towerId];
                float sapperAtkSpdMult = Math.Max(0f, 1f - store.TowerSapperSlowMult[towerId]);
                float attackInterval = 1.0f / Math.Max(0.1f, store.TowerAttackSpeed[towerId] * (1f + store.TowerHotZoneSpeedBonus[towerId] + _desperationSpeedBonus + fortressAtkSpdBonus + rallyAtkSpdBonus) * sapperAtkSpdMult);

                // ── Burst Fire / Salvo Mode check ─────────────────────────────────────
                int burstCount = store.TowerBurstCount[towerId];
                if (burstCount > 0)
                {
                    int shotsFired = store.TowerBurstShotsFired[towerId];
                    if (shotsFired >= burstCount)
                    {
                        // In cooldown phase: use burst cooldown (scaled by attack speed)
                        float burstCooldown = store.TowerBurstCooldown[towerId] / Math.Max(0.1f, store.TowerAttackSpeed[towerId] * (1f + store.TowerHotZoneSpeedBonus[towerId] + _desperationSpeedBonus + fortressAtkSpdBonus));
                        if (store.TowerLastAttackTime[towerId] < burstCooldown) return;
                        // Cooldown complete — reset burst counter
                        store.TowerBurstShotsFired[towerId] = 0;
                    }
                    else if (shotsFired > 0)
                    {
                        // In burst phase (not first shot): use burst interval between shots
                        if (store.TowerLastAttackTime[towerId] < store.TowerBurstInterval[towerId]) return;
                    }
                    // First shot of burst (shotsFired == 0): uses normal attackInterval (same as single-shot)
                }
                else if (store.TowerLastAttackTime[towerId] < attackInterval) return;

                // ── Round 98 — Windup / Pre-cast gate ─────────────────────────────
                // TowerWindupFrames > 0 means the tower enters a "charging" phase between
                // cooldown end and actual fire. WindupCountdown is set to WindupFrames on
                // the first frame cooldown completes; subsequent frames decrement it.
                // When WindupCountdown hits 0, the tower finally fires. CC (silence/stun/
                // disable/overheat/player-disabled) cancels the in-flight windup below
                // and resets LastAttackTime so the shot is fully lost.
                int windupFrames = store.TowerWindupFrames[towerId];
                if (windupFrames > 0)
                {
                    int countdown = store.TowerWindupCountdown[towerId];
                    if (countdown <= 0)
                    {
                        // First frame cooldown is done: enter charging state
                        store.TowerWindupCountdown[towerId] = windupFrames;
                        return; // skip fire this frame — start counting down next frame
                    }
                    // Already in windup: decrement
                    store.TowerWindupCountdown[towerId] = countdown - 1;
                    if (countdown - 1 > 0) return; // not yet at zero — keep charging
                    // WindupCountdown just hit 0: fall through to fire this frame
                }

                // Ammo check: skip targeting for towers that are reloading and empty
                if (store.TowerMaxAmmo[towerId] > 0 && store.TowerCurrentAmmo[towerId] <= 0) return;

                // Silence check: skip if tower is silenced by enemy ability
                // Round 98: silence also cancels any in-flight windup (resets LastAttackTime + Countdown)
                if (store.TowerIsSilenced[towerId])
                {
                    if (store.TowerWindupCountdown[towerId] > 0)
                    {
                        store.TowerWindupCountdown[towerId] = 0;
                        store.TowerLastAttackTime[towerId] = 0f; // full reset, must re-cooldown
                    }
                    return;
                }

                // Income tower check: skip attack logic for income-generating towers
                if (store.TowerIsIncomeTower[towerId]) return;

                // Beam tower check: beam towers are handled by BeamTowerSystem (not projectile-based)
                if (store.TowerIsBeam[towerId]) return;

                // Construction check: skip towers that are still under construction
                if (store.TowerIsConstructing[towerId]) return;

                // Disabled/sabotage check: skip towers that are disabled by enemy sabotage
                // Round 98: sabotage also cancels in-flight windup (full reset, must re-cooldown)
                if (store.TowerIsDisabled[towerId])
                {
                    if (store.TowerWindupCountdown[towerId] > 0)
                    {
                        store.TowerWindupCountdown[towerId] = 0;
                        store.TowerLastAttackTime[towerId] = 0f;
                    }
                    return;
                }

                // Player-disabled check: skip towers that the player has toggled off (Round 96)
                // Distinct from sabotage — both flags OR together: the tower stays inert
                // until BOTH clear. ToggleTower() in TowerPlacementSystem flips this flag.
                // Round 98: player toggle does NOT cancel in-flight windup (player intent: pause — shot resumes on enable).
                if (store.TowerPlayerDisabled[towerId]) return;

                // Overheat check: skip if tower is overheated (cannot fire)
                if (_heatSystem != null && _heatSystem.IsOverheated(towerId)) return;

                // Energy check: skip if tower doesn't have enough energy to fire
                if (_energySystem != null && !_energySystem.HasEnergy(towerId)) return;

                float tx = store.PositionX[towerId];
                float ty = store.PositionY[towerId];
                int range = store.TowerRange[towerId];

                // Hot zone range bonus — added to tower range for attack targeting
                float hotZoneRangeBonus = store.TowerHotZoneRangeBonus[towerId];
                if (hotZoneRangeBonus > 0f) range += (int)hotZoneRangeBonus;

                // Spatial grid: query O(cells) instead of O(enemies) — reuse pre-allocated array
                var candidates = _towerCandidateBuffers[ti];
                int candidateCount = 0;
                int effectiveRange = (int)(range * _weatherRangeMult * _dayNightRangeMult);
                // Round 183 Direction 8 — Scorched Earth vision reduction: a ScorchedEarth
                // corpse-effect zone (effectType=10) under this tower adds a multiplicative
                // range penalty. The CorpseEffectSystem writes max(zone.VisionReduction) into
                // TowerVisionReduction[towerId] each frame; ComponentStore.BeginFrame() zeroes
                // it at frame start so a tower that walks out of the fire regains full range.
                // Guard: only apply when > 0 (0 = no penalty fast path, JIT folds the multiply
                // to no-op). Applied as (1 - visionRed) so a 0.5 reduction = range × 0.5.
                float visionRed = store.TowerVisionReduction[towerId];
                if (visionRed > 0f)
                {
                    effectiveRange = (int)(effectiveRange * (1f - visionRed));
                }
                store.SpatialGrid.GetEnemiesInRange(store, tx, ty, effectiveRange, candidates, ref candidateCount);
                _towerCandidateCounts[ti] = candidateCount;

                // Read tower targeting mode
                TowerTargetingMode targetingMode = store.TowerTargetingMode[towerId];

                // ── Lock-On early-return: if this tower is a lock-on tower and its cached
                //   target is still alive + still in range, skip the entire targeting loop
                //   and reuse the cached enemy. This makes lock-on towers immune to target
                //   switches during CC (Fear/Stun) and lets snipers focus fire on a high-priority
                //   target through interrupts. Zero-overhead when TowerIsLockOn is false
                //   (single bool read + early-continue past the targeting loop). — Round 79
                bool isLockOn = store.TowerIsLockOn[towerId];
                int lockedId = store.TowerLockedTargetId[towerId];
                bool lockedValid = isLockOn && lockedId >= 0
                    && lockedId < ComponentStore.MAX_ENTITIES
                    && store.EnemyActive[lockedId]
                    && !store.EnemyIsBurrowed[lockedId];
                if (lockedValid)
                {
                    // Confirm in range (lock-on doesn't bypass range — only CC-driven switches)
                    float lex = store.PositionX[lockedId];
                    float ley = store.PositionY[lockedId];
                    float ldx = lex - tx;
                    float ldy = ley - ty;
                    if (ldx * ldx + ldy * ldy > (float)effectiveRange * effectiveRange)
                    {
                        lockedValid = false; // out of range → fall through to normal selection
                    }
                }
                // Best target is filled either by lock-on (cached) or by the normal selection loop below
                int bestTarget = -1;
                if (lockedValid)
                {
                    // Skip targeting loop entirely — reuse cached enemy ID
                    bestTarget = lockedId;
                }
                else
                {
                    // Clear stale lock when target died/left range so we re-pick on the next frame
                    if (isLockOn && lockedId != -1) store.TowerLockedTargetId[towerId] = -1;

                    // ── Normal targeting: select best candidate from spatial grid ──
                    float bestScore = 0f;

                    // Initialize bestScore based on targeting mode to ensure first candidate is always evaluated
                    switch (targetingMode)
                    {
                        case TowerTargetingMode.Furthest:
                            bestScore = float.MinValue;
                            break;
                        case TowerTargetingMode.LowestHealth:
                        case TowerTargetingMode.FirstSpawned:
                            bestScore = float.MaxValue; // minimize these scores
                            break;
                        case TowerTargetingMode.HighestHealth:
                        case TowerTargetingMode.LastSpawned:
                            bestScore = float.MinValue; // maximize these scores
                            break;
                        default: // Nearest — minimize distance
                            bestScore = float.MaxValue;
                            break;
                    }

                    for (int ci = 0; ci < candidateCount; ci++)
                    {
                        int enemyId = candidates[ci];
                        if (!store.EnemyActive[enemyId]) continue;

                        // Path-Hug filter: skip enemies that are off-path (no PathId assigned).
                        // Zero-overhead when TowerPathHugOnly[towerId] is false (the default).
                        if (store.TowerPathHugOnly[towerId] && store.EnemyPathId[enemyId] < 0) continue;

                        // Round 174 Direction 8 — Stalker filter: skip enemies that are stalkers
                        // AND not yet revealed. The bool read is one branch — non-stalkers
                        // (the 99% case) take the fast path with one extra bool comparison per
                        // candidate. Stalkers that are revealed (EnemyStalkRevealed=true) also
                        // take the fast path on subsequent frames. This implements the
                        // "invisible until close to a tower" tactical identity.
                        if (store.EnemyIsStalker[enemyId] && !store.EnemyStalkRevealed[enemyId]) continue;

                        // Burrow filter: skip enemies that are underground (cannot be targeted)
                        if (store.EnemyIsBurrowed[enemyId]) continue;

                        // Fog of War filter: skip enemies not visible to this tower
                        // TowerVisibilityByTower[towerId][enemyId] — only towers with VisionRadius > 0 have entries
                        // Towers without fog (VisionRadius=0) are not in the dictionary — treat as visible
                        bool isVisible = true;
                        if (store.TowerVisionRadius[towerId] > 0f)
                        {
                            if (store.TowerVisibilityByTower.TryGetValue(towerId, out bool[] visArray) && visArray != null && enemyId >= 0 && enemyId < visArray.Length)
                                isVisible = visArray[enemyId];
                            else
                                isVisible = false; // fog tower but enemy not in range
                        }
                        if (!isVisible) continue;

                        // Phase filter: skip phased enemies unless this tower is anti-phase (magic tower)
                        if (store.EnemyIsPhased[enemyId] && !store.TowerIsAntiPhase[towerId]) continue;

                        // Height-layer filter: skip enemies that this tower cannot hit
                        bool enemyFlying = store.EnemyIsFlying[enemyId];
                        bool canHitAir = store.TowerCanHitAir[towerId];
                        bool canHitGround = store.TowerCanHitGround[towerId];
                        if (enemyFlying && !canHitAir) continue;
                        if (!enemyFlying && !canHitGround) continue;

                        // LoS filter: opt-in towers (stealth/sniper) require unobstructed sight line.
                        // Default: TowerRequiresLOS[towerId] = false → LoS check skipped (backward compat).
                        // Phasing towers (TowerIsPhasing) ignore LoS — their shots phase through any
                        // TowerBlocksLOS obstacles, regardless of TowerRequiresLOS state.
                        if (store.TowerRequiresLOS[towerId] && !store.TowerIsPhasing[towerId])
                        {
                            float losTx = store.PositionX[enemyId];
                            float losTy = store.PositionY[enemyId];
                            if (!store.SpatialGrid.HasLineOfSight(store, towerId, tx, ty, losTx, losTy))
                                continue;
                        }

                        float ex = store.PositionX[enemyId];
                        float ey = store.PositionY[enemyId];

                        float dx = ex - tx;
                        float dy = ey - ty;

                        float distSq = dx * dx + dy * dy;

                        float score;
                        bool isBetter;
                        switch (targetingMode)
                        {
                            case TowerTargetingMode.Furthest:
                                score = distSq;
                                isBetter = score > bestScore;
                                break;
                            case TowerTargetingMode.LowestHealth:
                                score = store.EnemyHealth[enemyId];
                                isBetter = score < bestScore;
                                break;
                            case TowerTargetingMode.HighestHealth:
                                score = store.EnemyHealth[enemyId];
                                isBetter = score > bestScore;
                                break;
                            case TowerTargetingMode.FirstSpawned:
                                score = store.EnemySpawnFrame[enemyId];
                                isBetter = score < bestScore;
                                break;
                            case TowerTargetingMode.LastSpawned:
                                score = store.EnemySpawnFrame[enemyId];
                                isBetter = score > bestScore;
                                break;
                            default: // Nearest — minimize distance
                                score = distSq;
                                isBetter = distSq < bestScore;
                                break;
                        }

                        if (isBetter)
                        {
                            bestScore = score;
                            bestTarget = enemyId;
                        }
                    }

                    // Cache the selected target for lock-on towers (if Lock-On, only the
                    // first frame fills this — subsequent frames short-circuit on `lockedValid`).
                    if (isLockOn && bestTarget != -1)
                    {
                        store.TowerLockedTargetId[towerId] = bestTarget;
                    }
                }

                if (bestTarget != -1)
                {
                    // Tower rotation / aim check: if TurnRate > 0, tower gradually rotates toward target
                    float turnRate = store.TowerTurnRate[towerId];
                    if (turnRate > 0f)
                    {
                        float ex = store.PositionX[bestTarget];
                        float ey = store.PositionY[bestTarget];
                        float dx = ex - tx;
                        float dy = ey - ty;
                        float desiredAngle = (float)Math.Atan2(dy, dx);

                        // Normalize angle difference to [-PI, PI]
                        float currentAngle = store.TowerFacingAngle[towerId];
                        float angleDiff = desiredAngle - currentAngle;
                        while (angleDiff > Math.PI) angleDiff -= 2f * (float)Math.PI;
                        while (angleDiff < -Math.PI) angleDiff += 2f * (float)Math.PI;

                        // Rotate at most turnRate radians per second, skip attack while turning
                        float maxTurn = turnRate * deltaTime;
                        if (Math.Abs(angleDiff) > 0.1f)
                        {
                            float turn = Math.Clamp(angleDiff, -maxTurn, maxTurn);
                            store.TowerFacingAngle[towerId] = currentAngle + turn;
                            return;
                        }
                    }

                    // Accuracy check: if tower accuracy < 1.0, roll for miss
                    float towerAccuracy = store.TowerAccuracy[towerId];
                    if (towerAccuracy < 1f && _rand.NextDouble() >= towerAccuracy) return;

                    // Round 175 Direction 9 — Smokescreen miss check: a smokescreen zone
                    // (effectType=9 corpse effect) under this tower adds an additional miss
                    // chance. The CorpseEffectSystem writes max(zoneMissChance) into
                    // TowerSmokeMissChance[towerId] each frame; ComponentStore.BeginFrame()
                    // zeroes it at frame start so a tower that leaves the smoke is no longer
                    // affected. We roll AFTER the existing accuracy/evasion check so the
                    // miss is additive on top of base accuracy (e.g. 100% accuracy + 30%
                    // smoke = 70% effective hit rate).
                    float smokeMiss = store.TowerSmokeMissChance[towerId];
                    if (smokeMiss > 0f && _rand.NextDouble() < smokeMiss) return;

                    // Enemy evasion: if enemy has evasion > 0, roll for dodge (after accuracy check passes)
                    float enemyEvasion = store.EnemyEvasion[bestTarget];
                    if (enemyEvasion > 0f && _rand.NextDouble() < enemyEvasion) return;

                    // Enemy strafe/dodge: event-driven dodge triggered by this incoming attack.
                    // TryTriggerDodge returns true if dodge succeeds → skip this attack entirely.
                    if (_enemyStrafeSystem != null && _enemyStrafeSystem.TryTriggerDodge(bestTarget, towerId >= 0 ? 1 : -1)) return;

                    store.TowerLastAttackTime[towerId] = 0f;

                    // Burst fire: increment shot counter — resets to 0 in the burst cooldown check above
                    if (store.TowerBurstCount[towerId] > 0)
                    {
                        store.TowerBurstShotsFired[towerId]++;
                    }

                    // Consume ammo for towers with limited ammo (MaxAmmo > 0)
                    if (store.TowerMaxAmmo[towerId] > 0)
                    {
                        store.TowerCurrentAmmo[towerId]--;
                        // Start reload if empty (reload starts after last shot fired)
                        if (store.TowerCurrentAmmo[towerId] <= 0 && store.TowerReloadTime[towerId] > 0f)
                        {
                            store.TowerIsReloading[towerId] = true;
                            store.TowerReloadProgress[towerId] = 0f;
                        }
                    }

                    // Accumulate heat for towers that generate heat on each shot
                    if (_heatSystem != null)
                    {
                        _heatSystem.AccumulateHeat(towerId);
                    }

                    // Consume energy for towers that require energy to fire
                    if (_energySystem != null)
                    {
                        _energySystem.ConsumeEnergy(towerId);
                    }

                    float baseDmg = store.TowerAttackDamage[towerId];

                    // ── Random Damage Variance (Gambling / RNG Damage Range) ──────────────
                    float dmgVariance = store.TowerDamageVariance[towerId];
                    if (dmgVariance > 0f)
                        baseDmg *= (float)(1.0 - dmgVariance + _rand.NextDouble() * dmgVariance * 2.0);

                    // ── Desperation / Last Stand damage bonus ──────────────────────────
                    if (_desperationDmgBonus > 0f) baseDmg *= (1f + _desperationDmgBonus);

                    // ── Ramp-Up / Spool-Up Damage ──────────────────────────────────────────
                    // Each consecutive hit on the same target increases damage by RampUpRate,
                    // capped at RampUpMax. Target switch resets the multiplier to 1.0.
                    float rampUpRate = store.TowerRampUpRate[towerId];
                    if (rampUpRate > 0f)
                    {
                        int currentTarget = store.TowerRampUpTargetId[towerId];
                        if (currentTarget == bestTarget)
                        {
                            // Same target: accumulate ramp-up
                            float currentMult = store.TowerRampUpCurrent[towerId] + rampUpRate;
                            float maxMult = store.TowerRampUpMax[towerId];
                            if (currentMult > maxMult) currentMult = maxMult;
                            store.TowerRampUpCurrent[towerId] = currentMult;
                            baseDmg *= currentMult;
                        }
                        else if (store.TowerRampUpResetOnSwitch[towerId])
                        {
                            // Target switch with reset enabled: reset ramp-up
                            store.TowerRampUpCurrent[towerId] = 1f;
                            store.TowerRampUpTargetId[towerId] = bestTarget;
                            // First hit on new target: no multiplier bonus
                        }
                        else
                        {
                            // Target switch without reset: persist ramp-up, apply current multiplier
                            store.TowerRampUpTargetId[towerId] = bestTarget;
                            float currentMult = store.TowerRampUpCurrent[towerId];
                            if (currentMult > 1f)
                                baseDmg *= currentMult;
                        }
                    }

                    // ── Damage type resolution with conversion support ─────────────────
                    // Physical: reduced by armor (affected by armor penetration + shred)
                    // Magic: reduced by magic resist (no armor interaction)
                    // True: ignores armor and magic resist entirely
                    DamageType dmgType = store.TowerDamageType[towerId];
                    float conversionRatio = store.TowerDamageConversionRatio[towerId];
                    if (conversionRatio > 0f)
                    {
                        // Damage conversion: split damage into original type + converted type portions.
                        // This lets towers bypass enemy immunity to their primary damage type.
                        DamageType convertToType = store.TowerConvertedDamageType[towerId];
                        float origPortion = baseDmg * (1f - conversionRatio);
                        float convPortion = baseDmg * conversionRatio;
                        float finalDmg = 0f;

                        // Process original damage type portion
                        {
                            float d = origPortion;
                            if (dmgType != DamageType.True)
                            {
                                int mask = store.EnemyDamageImmunityMask[bestTarget];
                                if ((mask & (int)dmgType) != 0) d = 0f;
                            }
                            if (dmgType == DamageType.True)
                                d *= _damageTakenMult;
                            else if (dmgType == DamageType.Magic)
                                d *= Math.Max(0.01f, 1f - store.EnemyMagicResist[bestTarget]) * _damageTakenMult;
                            else
                            {
                                // Round 181 Direction 9 — Phaser gate (conversion path): if the
                                // target is currently in its phase window, zero the physical
                                // portion BEFORE armor math. True/Magic portions above are
                                // untouched so a tower with physical+true conversion still hits
                                // for the true portion.
                                if (store.EnemyPhaserPhaseActive[bestTarget]) d = 0f;
                                float ea = store.EnemyArmor[bestTarget] * (1f - _armorPenetration);
                                float shred = store.EnemyArmorShredStacks[bestTarget];
                                if (shred > 0f && _armorShredPerStack > 0f)
                                    ea = Math.Max(0f, ea - shred * _armorShredPerStack);
                                // Round 176 Direction 7 — Siege armor bonus (additive on top of
                                // EnemyArmor). 0.95 max combined so no enemy is unkillable.
                                ea += store.EnemySiegeArmorBonus[bestTarget];
                                if (ea > 0.95f) ea = 0.95f;
                                d *= Math.Max(0.01f, 1f - ea) * _damageTakenMult;
                            }
                            finalDmg += d;
                        }

                        // Process converted damage type portion
                        {
                            float d = convPortion;
                            if (convertToType != DamageType.True)
                            {
                                int mask = store.EnemyDamageImmunityMask[bestTarget];
                                if ((mask & (int)convertToType) != 0) d = 0f;
                            }
                            if (convertToType == DamageType.True)
                                d *= _damageTakenMult;
                            else if (convertToType == DamageType.Magic)
                                d *= Math.Max(0.01f, 1f - store.EnemyMagicResist[bestTarget]) * _damageTakenMult;
                            else
                            {
                                // Round 181 Direction 9 — Phaser gate (conversion path, mirror
                                // of the primary portion above).
                                if (store.EnemyPhaserPhaseActive[bestTarget]) d = 0f;
                                float ea = store.EnemyArmor[bestTarget] * (1f - _armorPenetration);
                                float shred = store.EnemyArmorShredStacks[bestTarget];
                                if (shred > 0f && _armorShredPerStack > 0f)
                                    ea = Math.Max(0f, ea - shred * _armorShredPerStack);
                                // Round 176 Direction 7 — Siege armor bonus (mirror of the
                                // primary damage branch above; same additive-on-EnemyArmor
                                // formula, same 0.95 clamp to keep the math symmetric).
                                ea += store.EnemySiegeArmorBonus[bestTarget];
                                if (ea > 0.95f) ea = 0.95f;
                                d *= Math.Max(0.01f, 1f - ea) * _damageTakenMult;
                            }
                            finalDmg += d;
                        }

                        baseDmg = finalDmg;
                    }
                    else
                    {
                        // ── Damage immunity check ───────────────────────────────────────────
                        // True damage bypasses immunity entirely. All other types check the mask.
                        if (dmgType != DamageType.True)
                        {
                            int immunityMask = store.EnemyDamageImmunityMask[bestTarget];
                            if ((immunityMask & (int)dmgType) != 0)
                            {
                                baseDmg = 0f;  // enemy is immune to this damage type
                            }
                        }
                        if (dmgType == DamageType.True)
                        {
                            baseDmg *= _damageTakenMult;
                        }
                        else if (dmgType == DamageType.Magic)
                        {
                            float magicResist = store.EnemyMagicResist[bestTarget];
                            baseDmg *= Math.Max(0.01f, 1f - magicResist) * _damageTakenMult;
                        }
                        else if (dmgType == DamageType.Fire)
                        {
                            // Elemental resistance (Round 117): fractional reduction per monster JSON FireResist.
                            // Distinct from EnemyDamageImmunityMask (binary) — here we allow 30% / 70% partial resist.
                            // Math.Max(0.01f, ...) preserves ≥1% damage floor so FireResist=0.999 still hits (not 0).
                            float fireResist = store.EnemyFireResist[bestTarget];
                            baseDmg *= Math.Max(0.01f, 1f - fireResist) * _damageTakenMult;
                        }
                        else if (dmgType == DamageType.Ice)
                        {
                            float iceResist = store.EnemyIceResist[bestTarget];
                            baseDmg *= Math.Max(0.01f, 1f - iceResist) * _damageTakenMult;
                        }
                        else if (dmgType == DamageType.Lightning)
                        {
                            float lightningResist = store.EnemyLightningResist[bestTarget];
                            baseDmg *= Math.Max(0.01f, 1f - lightningResist) * _damageTakenMult;
                        }
                        else if (dmgType == DamageType.Holy)
                        {
                            // Round 135 Dir 1: Holy / Smite / Divine damage — reduced by HolyResist only.
                            // Distinct from HolyVulnerable (TODO future): a ×2 multiplier for Undead.
                            // For now, Holy is a 4th elemental with same damage formula as Fire/Ice/Lightning.
                            // 1% floor preserves non-zero damage even at HolyResist=0.999.
                            float holyResist = store.EnemyHolyResist[bestTarget];
                            baseDmg *= Math.Max(0.01f, 1f - holyResist) * _damageTakenMult;
                        }
                        else  // Physical (default) — uses armor + armor shred + pen
                        {
                            // Round 181 Direction 9 — Phaser gate: if the target is currently in
                            // its phase window, zero out physical damage BEFORE armor math.
                            // Magic / True damage (handled in the branches above) bypass this
                            // gate entirely, so magic-heavy compositions shred phasers.
                            if (store.EnemyPhaserPhaseActive[bestTarget])
                            {
                                baseDmg = 0f;
                                return;  // skip the rest of the inner attack block (we're inside a Parallel.For lambda)
                            }
                            // Step 1: apply armor penetration (attacker's penetration ratio)
                            float effectiveArmor = store.EnemyArmor[bestTarget] * (1f - _armorPenetration);
                            // Step 2: apply flat armor shred stacks (debuff applied by attacker, e.g. AcidTower)
                            float armorShredStacks = store.EnemyArmorShredStacks[bestTarget];
                            if (armorShredStacks > 0f && _armorShredPerStack > 0f)
                                effectiveArmor = Math.Max(0f, effectiveArmor - armorShredStacks * _armorShredPerStack);
                            // Round 176 Direction 7 — Siege armor bonus (additive on top of
                            // EnemyArmor). 0.95 max combined so no enemy is unkillable.
                            effectiveArmor += store.EnemySiegeArmorBonus[bestTarget];
                            if (effectiveArmor > 0.95f) effectiveArmor = 0.95f;
                            // Step 3: apply effective armor to damage
                            baseDmg *= Math.Max(0.01f, 1f - effectiveArmor) * _damageTakenMult;
                        }
                    }
                    if (_waveDifficultyMult != 1.0f) baseDmg *= _waveDifficultyMult;

                    // Apply tower synergy multiplier (e.g. bonus damage when combo towers are placed together)
                    float synergyMult = store.GetTowerSynergyMultiplier(towerId);
                    if (synergyMult > 1.0f) baseDmg *= synergyMult;

                    // Round 180 Direction 5: Fortress damage bonus (cluster of same-type towers in range).
                    // Applied multiplicatively on baseDmg, after synergy multiplier, before weather/zone bonuses.
                    float fortressDmgBonus = store.GetTowerFortressDmgBonus(towerId);
                    if (fortressDmgBonus > 0f) baseDmg *= (1f + fortressDmgBonus);

                    // Apply weather damage multiplier (e.g. Storm gives towers +10% damage)
                    if (_weatherDamageMult != 1f) baseDmg *= _weatherDamageMult;

                    // Apply hot zone damage bonus (placement bonus from map terrain)
                    float hotZoneDmgBonus = store.TowerHotZoneDamageBonus[towerId];
                    if (hotZoneDmgBonus > 0f) baseDmg *= (1f + hotZoneDmgBonus);

                    // ── Range-based damage falloff ────────────────────────────────────────
                    int falloffType = store.TowerFalloffType[towerId];
                    if (falloffType != 0 && effectiveRange > 0)
                    {
                        float ex2 = store.PositionX[bestTarget];
                        float ey2 = store.PositionY[bestTarget];
                        float dx2 = ex2 - tx;
                        float dy2 = ey2 - ty;
                        float dist = (float)Math.Sqrt(dx2 * dx2 + dy2 * dy2);
                        float distRatio = dist / effectiveRange;

                        float startRatio = store.TowerFalloffStartRatio[towerId];
                        float minRatio = store.TowerFalloffMinRatio[towerId];

                        if (distRatio > startRatio && startRatio < 1f)
                        {
                            float t = (distRatio - startRatio) / Math.Max(0.001f, 1f - startRatio);
                            t = t > 1f ? 1f : (t < 0f ? 0f : t);

                            if (falloffType == 1) // Standard: closer = more damage
                                baseDmg *= (1f - t * (1f - minRatio));
                            else if (falloffType == 2) // Reverse (sniper): farther = more damage
                                baseDmg *= (minRatio + t * (1f - minRatio));
                        }
                    }

                    // ── Tower type-specific mechanics ─────────────────────────────────────
                    // ── Tower type-specific mechanics ─────────────────────────────────────
                    TowerType towerType = store.TowerType[towerId];

                    // ── I-frames guard (Round 118) ─────────────────────────────────────
                    // If bestTarget is currently invulnerable (EnemyInvulnFramesLeft > 0), skip
                    // damage application entirely. This throttles high-frequency DoT and multishot
                    // tower bursts on the same enemy within a single tick. The counter is decremented
                    // by FrameScheduler at the start of each WavePhase tick, so this check is
                    // O(1) and only fires when I-frames are actually active.
                    // Note: True damage is NOT exempted here — I-frames are a time-lock, not a damage
                    // type. Per the design doc, I-frames block ALL damage including True; if a future
                    // tower needs to bypass I-frames, expose EnemyInvulnOnHitFrames=0 on the target enemy.
                    // We're inside a Parallel.For lambda, so 'return' (not 'continue') skips the
                    // current iteration.
                    if (store.EnemyInvulnFramesLeft[bestTarget] > 0)
                    {
                        return;
                    }
                    // Round 182 Direction 6 — Blinker i-frames (post-blink invulnerability).
                    // A Blinker that just warped forward (within the last 0.2s) is briefly
                    // invulnerable. Skip damage while EnemyBlinkIFramesLeft > 0. Mirrors the
                    // existing I-frames check above; the Blinker timer is owned by
                    // FrameScheduler.TickBlinkerCycle and decrements independently each frame.
                    if (store.EnemyBlinkIFramesLeft[bestTarget] > 0f)
                    {
                        return;
                    }

                    // ── Round 143 Direction 1: Tower-vs-enemy type effectiveness multiplier ──
                    // Applied AFTER armor / resist / ramp / falloff / wave / synergy / weather
                    // math, so effectiveness stacks multiplicatively with those modifiers and
                    // doesn't get washed out by them. The composite-key dictionary lookup is
                    // O(1); the only allocation is the int→string + concat per hit. When the
                    // matrix is empty (_hasEffectiveness = false), the call short-circuits and
                    // returns 1.0 with no allocation — the common case for benchmarks / first-run.
                    if (_hasEffectiveness)
                    {
                        float effMult = GetEffectivenessMultiplier((int)towerType, bestTarget);
                        if (effMult != 1.0f) baseDmg *= effMult;
                    }

                    // ── Round 174 Direction 4: Backstab positional damage bonus ────────
                    // When the tower has a non-1.0 backstab multiplier (rogue / assassin
                    // archetype) and the target enemy has a non-zero movement direction
                    // (EnemyMoveDirX/Y, written by movement systems), check whether the
                    // tower sits in the enemy's rear hemisphere. If so, multiply damage
                    // by the configured multiplier. The check is two float reads, one
                    // dot product, and one float compare — zero overhead for non-rogue
                    // towers (1.0x mult) and zero overhead globally when the master
                    // BackstabConfig.Enabled switch is off.
                    //
                    // Math: enemy "facing" is its motion vector (dirX, dirY). The tower
                    // is "directly behind" the enemy when the (tower→enemy) direction
                    // points the same way the enemy is moving — i.e. dot((tower→enemy),
                    // facing) = 1. As the tower moves around to the side, dot → 0; in
                    // front of the enemy, dot → -1. A tower is inside the rear cone
                    // (half-angle θ) when deviation from "directly behind" is < θ, i.e.
                    // dot > cos(θ). For the default 90° cone, cos(90°)=0, so any tower
                    // with dot > 0 (anywhere in the rear hemisphere) gets the bonus.
                    if (_backstabEnabled)
                    {
                        float backstabMult = store.TowerBackstabDamageMult[towerId];
                        // Fast path: skip the math entirely for non-rogue towers (1.0x).
                        if (backstabMult > 1.0001f)
                        {
                            float enemyDirX = store.EnemyMoveDirX[bestTarget];
                            float enemyDirY = store.EnemyMoveDirY[bestTarget];
                            float dirLenSq = enemyDirX * enemyDirX + enemyDirY * enemyDirY;
                            if (dirLenSq > 0.0001f)
                            {
                                float angleDeg = store.TowerBackstabAngleDeg[towerId];
                                // cos(angleDeg) of the half-cone. The tower is in the
                                // rear cone when dot((tower→enemy unit), (enemy facing unit))
                                // > cos(angleDeg). For 0° (strict): cos=1, so dot>1 is
                                // impossible (test never triggers). For 180° (full rear
                                // hemisphere): cos=-1, so dot>-1 is always true for any
                                // non-antiparallel position. Both endpoints handled.
                                // MathF.Cos avoids the (float) cast Math.Cos requires.
                                float cosAngle = MathF.Cos(angleDeg * MathF.PI / 180f);
                                // Normalize the enemy direction vector to a unit (so dot
                                // is bounded by [-1, 1] regardless of motion-vector length).
                                float invDirLen = 1.0f / MathF.Sqrt(dirLenSq);
                                float unitDirX = enemyDirX * invDirLen;
                                float unitDirY = enemyDirY * invDirLen;
                                // tower→enemy direction (tx,ty) → (ex,ey)
                                float ex = store.PositionX[bestTarget];
                                float ey = store.PositionY[bestTarget];
                                float dx = ex - tx;
                                float dy = ey - ty;
                                float distSq = dx * dx + dy * dy;
                                if (distSq > 0.0001f)
                                {
                                    float invDist = 1.0f / MathF.Sqrt(distSq);
                                    float unitDx = dx * invDist;
                                    float unitDy = dy * invDist;
                                    // Dot of (tower→enemy unit) · (enemy facing unit).
                                    // dot=1 → tower is directly behind. dot=-1 → tower is
                                    // directly in front. Rear-cone test: dot > cos(angleDeg).
                                    float dot = unitDx * unitDirX + unitDy * unitDirY;
                                    if (dot > cosAngle)
                                    {
                                        baseDmg *= backstabMult;
                                    }
                                }
                            }
                        }
                    }

                    switch (towerType)
                    {
                        case TowerType.AOE:
                            // Damage + area splash (handled via upgrade special ability mechanism)
                            lock (damageLock) { bag.Add((bestTarget, baseDmg, store.PlayerEntityId, towerId)); }
                            // AOE towers naturally use splash — just mark the primary hit
                            // The splash resolution is handled in ResolveSplashDamage() if splash is set
                            break;

                        case TowerType.Sniper:
                            // High single-target damage + mark (bonus damage on next hit)
                            lock (damageLock) { bag.Add((bestTarget, baseDmg, store.PlayerEntityId, towerId)); }
                            // Apply mark debuff: next tower hit deals +20% damage
                            if (store.EnemyArmor[bestTarget] >= 0)
                            {
                                // Sniper mark: enemy takes +20% damage from next hit
                                // Use slowAmount field as mark damage amp for this tower
                                float markAmp = 0.20f;
                                if (markAmp > 0f)
                                    lock (debuffLock) { debuffBag.Add((bestTarget, towerId)); }
                            }
                            break;

                        case TowerType.Tesla:
                            // Chain lightning: collect primary target for serial chain resolution.
                            // ResolveTeslaChainLightning() finds nearest neighbors and chains up to 3 hops at 70% decay.
                            lock (chainLock) { chainBag.Add((0, bestTarget, baseDmg, store.PlayerEntityId, towerId)); }
                            break;

                        case TowerType.Leech:
                            // Damage + lifesteal (heal player)
                            lock (damageLock) { bag.Add((bestTarget, baseDmg, store.PlayerEntityId, towerId)); }
                            float healAmount = baseDmg * LEECH_LIFESTEAL_RATE;
                            if (healAmount > 0f)
                                lock (healLock) { healBag.Add((store.PlayerEntityId, healAmount)); }
                            break;

                        case TowerType.Frost:
                            // Damage + tower slow debuff (handled by debuff phase)
                            lock (damageLock) { bag.Add((bestTarget, baseDmg, store.PlayerEntityId, towerId)); }
                            lock (debuffLock) { debuffBag.Add((bestTarget, towerId)); }
                            break;

                        case TowerType.Stun:
                            // High-stun tower: damage + stun roll in debuff phase
                            lock (damageLock) { bag.Add((bestTarget, baseDmg, store.PlayerEntityId, towerId)); }
                            lock (debuffLock) { debuffBag.Add((bestTarget, towerId)); }
                            break;

                        case TowerType.EMP:
                            // EMP tower: damage + stun + slow (debuff phase)
                            lock (damageLock) { bag.Add((bestTarget, baseDmg, store.PlayerEntityId, towerId)); }
                            lock (debuffLock) { debuffBag.Add((bestTarget, towerId)); }
                            break;

                        case TowerType.Firewall:
                            // Damage + Firewall DoT (handled by debuff phase)
                            lock (damageLock) { bag.Add((bestTarget, baseDmg, store.PlayerEntityId, towerId)); }
                            lock (debuffLock) { debuffBag.Add((bestTarget, towerId)); }
                            break;

                        default:
                            // Basic / unknown: standard damage + standard debuff check
                            lock (damageLock) { bag.Add((bestTarget, baseDmg, store.PlayerEntityId, towerId)); }
                            // Special ability: armor pierce (reduces enemy armor effectiveness)
                            if (store.TowerArmorPierceRatio[towerId] > 0f)
                            {
                                float pierceEffectiveArmor = store.EnemyArmor[bestTarget] * (1f - store.TowerArmorPierceRatio[towerId]);
                                float pierceBonus = baseDmg * (1f - Math.Max(0.01f, 1f - pierceEffectiveArmor));
                                if (pierceBonus > 0f)
                                    lock (damageLock) { bag.Add((bestTarget, pierceBonus, store.PlayerEntityId, towerId)); }
                            }
                            // Armor shred debuff: apply stack on hit if tower has armor shred bonus
                            float armorShredBonus = store.TowerArmorShredBonus[towerId];
                            if (armorShredBonus > 0f && _armorShredPerStack > 0f)
                            {
                                store.EnemyArmorShredStacks[bestTarget] += armorShredBonus;
                                store.EnemyArmorShredDuration[bestTarget] = 5f; // 5-turn duration, refreshed on re-apply
                            }
                            // Special ability: splash damage (AOE)
                            if (store.TowerSplashRadius[towerId] > 0f)
                            {
                                // Collect splash targets for later processing
                                lock (splashLock) { splashBag.Add((bestTarget, baseDmg * 0.5f, store.PlayerEntityId, towerId)); }
                            }
                            // Special ability: critical strike
                            // Crit Resistance: enemy can suppress a fraction of incoming crit chance (Boss/Elite = 0.5)
                            float critRate = (store.TowerCritChance[towerId] + _critRateBonus) * (1f - store.EnemyCritResistance[bestTarget]);
                            if (critRate > 0f && _rand.NextDouble() < critRate)
                            {
                                float critBonus = baseDmg * (store.TowerCritMultiplier[towerId] * _critDamageBonus - 1f);
                                if (critBonus > 0f)
                                {
                                    // Round 67: mark (enemyId, towerId) in the On-Crit side-channel so the
                                    // serial apply phase can publish EnemyCrit exactly once for this attack.
                                    // Pack (enemyId, towerId) into a single long to avoid a struct-keyed HashSet alloc.
                                    // CRITICAL: HashSet<T>.Add is NOT thread-safe — must be guarded by damageLock
                                    // (the same lock guarding bag.Add). Without this, parallel threads can corrupt
                                    // the HashSet's internal buckets, throw IndexOutOfRangeException, or silently
                                    // drop crits so EnemyCrit never fires.
                                    long critKey = ((long)bestTarget << 32) | (uint)towerId;
                                    lock (damageLock)
                                    {
                                        _critFiredThisFrame.Add(critKey);
                                        bag.Add((bestTarget, critBonus, store.PlayerEntityId, towerId));
                                    }
                                }
                            }
                            // Scatter/multicast: if ProjectileCount > 1, fire additional projectiles at the target
                            int projCount = store.TowerProjectileCount[towerId];
                            if (projCount > 1)
                            {
                                // Shotgun mode: each pellet seeks its OWN target within a cone radius,
                                // simulating a cone-shaped spread of N independent projectiles.
                                // If no extra targets exist near primary, fall back to hitting primary.
                                float pelletMult = store.TowerPelletDamageMult[towerId];
                                if (pelletMult <= 0f) pelletMult = 1f;
                                float coneRadius = store.TowerPelletConeRadius[towerId];
                                if (coneRadius <= 0f) coneRadius = store.TowerRange[towerId];
                                if (coneRadius < 1f) coneRadius = 1f;

                                // For pellets 1..N-1, pick a unique nearby target within cone radius.
                                // We avoid the primary by tracking which enemyIds we already hit this attack.
                                // Cap pellets at 16 to bound per-frame cost (shotgun towers typically fire 5-8).
                                int pelletsToFire = projCount - 1;
                                if (pelletsToFire > 16) pelletsToFire = 16;

                                // Snapshot nearby enemy IDs once (reused per pellet)
                                // Use the existing per-tower candidate buffer populated earlier in Update()
                                // (size = range, but we filter to coneRadius since shotgun is short-range).
                                // Copy into a local scratch buffer so we can mark pellets-as-taken in place
                                // without mutating the shared per-tower candidate buffer.
                                // Allocation is fine here: shotgun path (projCount > 1) is rare in benchmarks.
                                int[] localBuf = _towerCandidateBuffers[ti];
                                int localCount = _towerCandidateCounts[ti];
                                int[] takenScratch = new int[localCount];
                                for (int k = 0; k < localCount; k++) takenScratch[k] = localBuf[k];
                                int scratchCount = localCount;

                                for (int sc = 0; sc < pelletsToFire; sc++)
                                {
                                    int pelletTarget = bestTarget; // default: same target
                                    // Pick the nearest UNUSED enemy in scratch that is not the primary and is alive
                                    int found = -1;
                                    float bestDist2 = float.MaxValue;
                                    float primaryX = store.PositionX[bestTarget];
                                    float primaryY = store.PositionY[bestTarget];
                                    for (int k = 0; k < scratchCount; k++)
                                    {
                                        int eid = takenScratch[k];
                                        if (eid < 0) continue; // already taken by an earlier pellet
                                        if (eid == bestTarget) continue; // skip primary
                                        if (!store.EnemyActive[eid]) continue;
                                        if (store.EnemyHealth[eid] <= 0f) continue;
                                        float ddx = store.PositionX[eid] - primaryX;
                                        float ddy = store.PositionY[eid] - primaryY;
                                        float d2 = ddx * ddx + ddy * ddy;
                                        if (d2 > coneRadius * coneRadius) continue;
                                        if (d2 < bestDist2) { bestDist2 = d2; found = eid; }
                                    }
                                    if (found >= 0)
                                    {
                                        // Mark this enemy as taken so the next pellet picks a different one
                                        for (int k = 0; k < scratchCount; k++)
                                        {
                                            if (takenScratch[k] == found) { takenScratch[k] = -1; break; }
                                        }
                                        pelletTarget = found;
                                    }
                                    float pelletDmg = baseDmg * pelletMult;
                                    if (pelletDmg > 0f)
                                        lock (damageLock) { bag.Add((pelletTarget, pelletDmg, store.PlayerEntityId, towerId)); }
                                }
                            }
                            // Special ability: chain lightning (from upgrade, not Tesla tower type)
                            if (store.TowerHasChainLightning[towerId])
                            {
                                lock (chainLock) { chainBag.Add((0, bestTarget, baseDmg, store.PlayerEntityId, towerId)); }
                            }
                            // Fragmentation/projectile split: if fragmentCount > 0, queue fragment spawning on impact
                            int fragCount = store.TowerProjectileFragmentCount[towerId];
                            if (fragCount > 0 && projectileSystem != null)
                            {
                                float fragRange = store.TowerProjectileFragmentRange[towerId];
                                float fragDmgMult = store.TowerProjectileFragmentDmgMult[towerId];
                                lock (fragmentLock) { fragmentBag.Add((bestTarget, baseDmg * fragDmgMult, store.PlayerEntityId, towerId, fragCount, fragRange)); }
                            }
                            // Bouncing projectile: if bounces > 0 and this isn't already a bounce hit
                            if (store.TowerBouncesRemaining[towerId] > 0 && store.TowerBounceHitsRemaining[towerId] == 0)
                            {
                                // Reset bounce counter for this attack — primary hit
                                store.TowerBounceHitsRemaining[towerId] = store.TowerBouncesRemaining[towerId];
                                // Queue primary target for bounce resolution
                                lock (bounceLock) { bounceBag.Add((0, bestTarget, baseDmg, store.PlayerEntityId, towerId)); }
                            }
                            // Multi-Strike (Round 201 Direction 1): if MultiStrikeCount > 0, hit N nearest extra
                            // enemies within MultiStrikeRange of the primary target. Distinct from Bounce (which
                            // chains with falloff) and Scatter (which fires N pellets at the same target).
                            // Each extra target takes (baseDmg * MultiStrikeDamageMult). Same-target damage
                            // event flows through _damageQueue so on-hit effects (lifesteal/cleave) fire per hit.
                            int multiStrikeCount = store.TowerMultiStrikeCount[towerId];
                            if (multiStrikeCount > 0)
                            {
                                float msRange = store.TowerMultiStrikeRange[towerId];
                                if (msRange <= 0f) msRange = store.TowerRange[towerId];
                                if (msRange < 1f) msRange = 1f;
                                float msMult = store.TowerMultiStrikeDamageMult[towerId];
                                if (msMult <= 0f) msMult = 1f;
                                // Cap multi-strike extras at 16 to bound per-frame cost (large AoE towers rarely exceed 8).
                                int extrasToHit = multiStrikeCount;
                                if (extrasToHit > 16) extrasToHit = 16;

                                // Reuse the per-tower candidate buffer populated earlier in Update() (range-filtered),
                                // but filter to msRange around the primary target and mark enemies as taken as we pick.
                                int[] localBuf = _towerCandidateBuffers[ti];
                                int localCount = _towerCandidateCounts[ti];
                                int[] takenScratch = new int[localCount];
                                for (int k = 0; k < localCount; k++) takenScratch[k] = localBuf[k];
                                int scratchCount = localCount;

                                float primaryX = store.PositionX[bestTarget];
                                float primaryY = store.PositionY[bestTarget];
                                float msRangeSq = msRange * msRange;

                                for (int sc = 0; sc < extrasToHit; sc++)
                                {
                                    int found = -1;
                                    float bestDist2 = float.MaxValue;
                                    for (int k = 0; k < scratchCount; k++)
                                    {
                                        int eid = takenScratch[k];
                                        if (eid < 0) continue; // already taken by an earlier extra hit
                                        if (eid == bestTarget) continue; // skip primary (already damaged)
                                        if (!store.EnemyActive[eid]) continue;
                                        if (store.EnemyHealth[eid] <= 0f) continue;
                                        float ddx = store.PositionX[eid] - primaryX;
                                        float ddy = store.PositionY[eid] - primaryY;
                                        float d2 = ddx * ddx + ddy * ddy;
                                        if (d2 > msRangeSq) continue;
                                        if (d2 < bestDist2) { bestDist2 = d2; found = eid; }
                                    }
                                    if (found < 0) break; // no more valid extra targets within range
                                    // Mark this enemy as taken so the next extra hit picks a different one
                                    for (int k = 0; k < scratchCount; k++)
                                    {
                                        if (takenScratch[k] == found) { takenScratch[k] = -1; break; }
                                    }
                                    float multiDmg = baseDmg * msMult;
                                    if (multiDmg > 0f)
                                        lock (damageLock) { bag.Add((found, multiDmg, store.PlayerEntityId, towerId)); }
                                }
                            }
                            // Special ability: freeze AOE (from upgrade)
                            if (store.TowerHasFreezeAoe[towerId])
                            {
                                lock (debuffLock) { debuffBag.Add((bestTarget, towerId)); }
                            }
                            // Standard stun/slow from tower debuff config
                            float stunChance = store.TowerStunChance[towerId];
                            float slowAmount = store.TowerSlowAmount[towerId];
                            if (stunChance > 0f || slowAmount > 0f)
                                lock (debuffLock) { debuffBag.Add((bestTarget, towerId)); }
                            break;
                    }

                    // ── I-frames write-back (Round 118) ─────────────────────────────────
                    // After all damage events for this tower→target hit have been queued, set the
                    // enemy's invuln-frames-left to the configured per-monster value. This causes the
                    // NEXT tower (within this frame or future frames) to skip this target until the
                    // counter expires. Default 0 = no I-frames; Boss/Elite monsters set 3-10.
                    // Cheap O(1) per tower hit, only writes when config > 0.
                    int invulnConfig = store.EnemyInvulnOnHitFrames[bestTarget];
                    if (invulnConfig > 0)
                    {
                        store.EnemyInvulnFramesLeft[bestTarget] = invulnConfig;
                    }

                    // ── Chain Attack: if this tower has a linked partner, queue partner's damage too ──
                    int chainPartnerId = store.TowerLinkPartnerId[towerId];
                    if (chainPartnerId != -1 && chainPartnerId < ComponentStore.MAX_ENTITIES
                        && store.TowerChainDmgRatio[towerId] > 0f && store.TowerActive[chainPartnerId])
                    {
                        float chainDmg = store.TowerAttackDamage[chainPartnerId] * store.TowerChainDmgRatio[towerId];
                        lock (damageLock) { bag.Add((bestTarget, chainDmg, store.PlayerEntityId, chainPartnerId)); }
                    }
                }
                else
                {
                    // ── Destructible targeting (Round 95 Direction 5) ─────────────────────
                    // No enemy in range → fall back to attacking the nearest destructible in range.
                    // Lower priority than enemies (fires only when bestTarget == -1). Zero overhead
                    // when ActiveObstacleIds is empty (the common case). Effect is resolved serially
                    // after the Parallel.For in the ResolveDestructibleDamage() pass below.
                    int dstrCount = store.ActiveObstacleIds.Count;
                    if (dstrCount > 0)
                    {
                        int bestDstr = -1;
                        float bestDstrDistSq = float.MaxValue;
                        for (int di = 0; di < dstrCount; di++)
                        {
                            int oid = store.ActiveObstacleIds[di];
                            if (!store.ObstacleActive[oid]) continue;
                            // Only attack obstacles with HP > 0 (i.e. destructibles that can be destroyed)
                            if (store.ObstacleHealth[oid] <= 0f) continue;
                            // Tower must be in range
                            float odx = store.ObstacleX[oid] - tx;
                            float ody = store.ObstacleY[oid] - ty;
                            float distSq = odx * odx + ody * ody;
                            float rangeSq = (float)effectiveRange * effectiveRange;
                            if (distSq > rangeSq) continue;
                            if (distSq < bestDstrDistSq)
                            {
                                bestDstrDistSq = distSq;
                                bestDstr = oid;
                            }
                        }
                        if (bestDstr != -1)
                        {
                            // Reset attack cooldown so this counts as a fired shot
                            store.TowerLastAttackTime[towerId] = 0f;
                            // Compute base damage to apply (use same TowerAttackDamage)
                            float dstrDmg = store.TowerAttackDamage[towerId];
                            if (dstrDmg < 0f) dstrDmg = 0f;
                            // Queue for serial resolution (same damage-lock pattern as enemy damage)
                            lock (damageLock)
                            {
                                // Reuse the same _damageQueue[readIdx] by encoding the destructible
                                // as a sentinel: enemyId < 0 means destructible. The serial phase
                                // checks enemyId < 0 and routes to the destructible handler instead.
                                // We use bitwise: enemyId = -(obstacleId + 1) to keep the int range.
                                bag.Add((-(bestDstr + 1), dstrDmg, store.PlayerEntityId, towerId));
                            }
                        }
                    }
                }
            });

            // Phase 2 (serial): apply damage
            int readIdx = _damageQueueIdx;
            int writeIdx = 1 - _damageQueueIdx;
            _damageQueueIdx = writeIdx;
            _damageQueue[writeIdx].Clear();
            foreach (var (enemyId, damage, playerId, towerId) in _damageQueue[readIdx])
            {
                // ── Destructible routing (Round 95 Direction 5) ──
                // Sentinel: enemyId < 0 means the queued damage is for an obstacle, not an enemy.
                // The obstacle id is encoded as -(enemyId + 1) to keep both signs valid in the int.
                // Handle destructible damage + on-destroy effect inline before falling into the
                // enemy damage path. This is opt-in: when no destructibles spawn, ActiveObstacleIds
                // is empty and the bag contains no negative enemyId → the check is a single int
                // comparison per queued entry and the destructible branch never runs.
                if (enemyId < 0)
                {
                    int obstacleId = -(enemyId + 1);
                    if (obstacleId >= 0 && obstacleId < ComponentStore.MAX_OBSTACLES
                        && store.ObstacleActive[obstacleId]
                        && store.ObstacleHealth[obstacleId] > 0f)
                    {
                        store.ObstacleHealth[obstacleId] -= damage;
                        if (store.ObstacleHealth[obstacleId] <= 0f)
                        {
                            // Destructible destroyed → trigger on-destroy effect before removal
                            int effect = store.ObstacleOnDestroyEffect[obstacleId];
                            float effectValue = store.ObstacleOnDestroyValue[obstacleId];
                            float ox = store.ObstacleX[obstacleId];
                            float oy = store.ObstacleY[obstacleId];
                            if (effect == 1)
                            {
                                // Gold: grant player gold (single-player game → PlayerEntityId).
                                // Uses the same single-player routing as the rest of the codebase —
                                // see ComponentStore.PlayerEntityId (default 1).
                                if (store.PlayerEntityId >= 0 && store.PlayerEntityId < ComponentStore.MAX_PLAYERS)
                                {
                                    store.PlayerGold[store.PlayerEntityId] += effectValue;
                                }
                            }
                            else if (effect == 2)
                            {
                                // Explosion: deal % of enemy max HP as damage to all enemies in radius
                                // Radius is hard-coded to 5f (documented default in DestructibleDef)
                                // to avoid an extra per-obstacle SOA field — see Direction 5 design.
                                const float radius = 5f;
                                float radiusSq = radius * radius;
                                var activeEnemies = store.GetCachedActiveEnemyIds();
                                float dmgRatio = effectValue; // 0-1, fraction of max HP
                                for (int ei = 0; ei < activeEnemies.Count; ei++)
                                {
                                    int eid = activeEnemies[ei];
                                    if (!store.EnemyActive[eid]) continue;
                                    float edx = store.PositionX[eid] - ox;
                                    float edy = store.PositionY[eid] - oy;
                                    if (edx * edx + edy * edy > radiusSq) continue;
                                    float maxHp = store.EnemyMaxHealth[eid];
                                    float explosionDmg = maxHp * dmgRatio;
                                    if (explosionDmg > 0f)
                                    {
                                        // Re-enter the enemy damage path via direct health application
                                        // (skipping damage queue to keep explosion atomic with the
                                        // destructible destruction and avoid recursion in the queue).
                                        store.EnemyHealth[eid] -= explosionDmg;
                                        // Round 132 Dir 8 — honor Boss Min-Health Floor (explosion route
                                        // bypasses ApplyEnemyDamage so we re-clamp here in-place).
                                        store.ApplyMinHealthFloorInPlace(eid);
                                        if (store.EnemyHealth[eid] <= 0f)
                                        {
                                            store.QueueEnemyDeath(eid, store.PlayerEntityId);
                                        }
                                    }
                                }
                            }
                            // Remove the destroyed destructible from the active list
                            store.RemoveObstacle(obstacleId);
                        }
                    }
                    continue; // Destructible entry fully handled — skip the enemy damage path
                }
                if (!store.EnemyActive[enemyId]) continue;
                // Invulnerability check: if enemy is invulnerable, skip damage
                if (store.EnemyIsInvulnerable[enemyId]) continue;
                // Round 174 Direction 8 — Stalker Ambush bonus: if this enemy is a revealed
                // stalker that has not yet had its ambush strike consumed, multiply incoming
                // damage by EnemyStalkAmbushMult (e.g. 3.0x) and flip the consumed flag so
                // the bonus fires EXACTLY ONCE per spawn. Sticky after consumption → subsequent
                // attacks deal base damage. Both flags are reset by AddEnemy / DestroyEntity
                // so the next spawn of this enemy id gets a fresh ambush opportunity.
                // Note: `damage` here is a foreach-iteration variable (deconstructed tuple
                // from the double-buffer queue) so it can't be reassigned in-place. We
                // compute the scaled value into a local and propagate it via the finalDmg
                // path below.
                float stalkerAmbushMult = 1f;
                if (store.EnemyIsStalker[enemyId] && store.EnemyStalkRevealed[enemyId] && !store.EnemyStalkConsumed[enemyId])
                {
                    stalkerAmbushMult = store.EnemyStalkAmbushMult[enemyId];
                    if (stalkerAmbushMult > 1f)
                    {
                        store.EnemyStalkConsumed[enemyId] = true;
                    }
                }
                // N-Hit Shield check: if enemy has hit shield layers, consume 1 layer and block damage
                if (_hitShieldSystem != null && _hitShieldSystem.ConsumeHitShield(enemyId)) continue;
                // I-frames check (Round 118): skip damage while EnemyInvulnFramesLeft > 0
                if (store.EnemyInvulnFramesLeft[enemyId] > 0) continue;
                // Round 182 Direction 6 — Blinker i-frames (post-blink invulnerability).
                // A Blinker that just warped forward (within the last 0.2s) is briefly
                // invulnerable. Skip damage while EnemyBlinkIFramesLeft > 0. Mirrors the
                // existing I-frames check above; the Blinker timer is owned by
                // FrameScheduler.TickBlinkerCycle and decrements independently each frame.
                if (store.EnemyBlinkIFramesLeft[enemyId] > 0f) continue;
                // Apply damage resistance (tech tree provides global reduction to all enemy damage taken)
                float resist = store.EnemyDamageResistance[enemyId];
                // Stalker ambush multiplier (Round 174 Dir 8) is applied AFTER damage resistance
                // so the bonus doesn't get partially eaten by armor. This preserves the "3x
                // ambush" feel even against high-resist bosses — which is exactly the design
                // intent (stalker bonus is meant to threaten armored/boss enemies).
                float finalDmg = resist >= 1f ? 0f : damage * (1f - resist);
                if (stalkerAmbushMult > 1f)
                {
                    finalDmg *= stalkerAmbushMult;
                }
                // ── Damage Saturation (Round 92 Direction 1) ──
                // O(1) guard: skip entirely when saturation is disabled (WindowFrames == -1 sentinel)
                // or when the per-hit damage is too small to ever matter (< 0.01f). When enabled,
                // lazily expire the rolling window (currentFrame - lastFrame > window), accumulate
                // finalDmg into the rolling sum, then apply the scale multiplier if the sum has
                // exceeded (maxHp × threshold). Saturated hits still register the sum update so
                // the window keeps expiring/resetting normally (no special-cased reset). The static
                // config fields are cached in local ints/floats before the inner block so the JIT
                // doesn't reload them on each iteration of the hot loop.
                int satWindow = DamageSaturationConfig.SaturationWindowFrames;
                if (satWindow >= 0 && finalDmg > 0.01f)
                {
                    int currentFrame = store.CurrentFrame;
                    int lastFrame = store.EnemyRecentDamageFrame[enemyId];
                    if (currentFrame - lastFrame > satWindow)
                    {
                        store.EnemyRecentDamageSum[enemyId] = 0f;
                    }
                    float newSum = store.EnemyRecentDamageSum[enemyId] + finalDmg;
                    store.EnemyRecentDamageSum[enemyId] = newSum;
                    store.EnemyRecentDamageFrame[enemyId] = currentFrame;
                    float maxHp = store.EnemyMaxHealth[enemyId];
                    if (maxHp > 0f)
                    {
                        float satThreshold = DamageSaturationConfig.SaturationThresholdMult;
                        if (newSum > maxHp * satThreshold)
                        {
                            finalDmg *= DamageSaturationConfig.SaturationScaleMult;
                        }
                    }
                }
                // ── Combo Chain bonus (Round 81) ──
                // O(1) guard: read playerId's chain buff timer; if > 0, multiply by (1 + bonus).
                // The check itself is one float read; the multiplier is applied only when the
                // buff is active (timer > 0), so the common case (no chain) adds a single
                // float load + branch — no GC, no allocation.
                if (store.PlayerChainKillBuffTimer[playerId] > 0f)
                {
                    finalDmg *= 1.25f; // 1 + ChainKillDamageBonusPct (hardcoded in ComboSystem)
                }
                // ── Elemental Affinity bonus (Round 68 Direction 7) ──
                // O(1) guard: skip entirely when the tower has no affinity configured.
                // towerId is always valid here (already validated upstream). ElementType is a [Flags]
                // bitmask, so the test is a single AND — zero GC, no allocations.
                int towerAff = store.TowerElementalAffinity[towerId];
                if (towerAff > 0)
                {
                    float affBonus = store.TowerElementalAffinityBonus[towerId];
                    if (affBonus > 0f)
                    {
                        ElementType enemyElems = store.EnemyElementStatus[enemyId];
                        if ((((ElementType)towerAff) & enemyElems) != 0)
                        {
                            finalDmg *= 1f + affBonus;
                        }
                    }
                }
                // ── Elemental Exposure bonus (Round 83 Direction 5) ──
                // O(1) guard: skip when exposure window is inactive (timer <= 0). Active towers
                // (towerAff > 0) with affinity bits disjoint from the enemy's current exposure
                // mask take the +30% off-element bonus. Physical-only towers (towerAff == 0) are
                // unaffected — this is a deliberate design choice to keep non-elemental towers
                // baseline-balanced and reserve the bonus for elemental-flux strategies.
                if (towerAff > 0 && store.EnemyExposureTimer[enemyId] > 0f)
                {
                    ElementType exposureMask = store.EnemyExposureMask[enemyId];
                    if (exposureMask != ElementType.None && (((ElementType)towerAff) & exposureMask) == 0)
                    {
                        finalDmg *= 1.30f; // 1 + EXPOSURE_BONUS_PCT (hardcoded in ElementalReactionSystem)
                    }
                }
                // ── Anti-Summon bonus (Round 115 Direction 2) ──
                // O(1) guard: skip entirely when the tower has no anti-summon config (multiplier
                // == 0, the common case for most towers). Anti-summon towers are specialized
                // counters that multiply damage against enemies tagged with an active summon
                // circle. The enemy is "in" the circle when the registered radius > 0 and the
                // enemy's current world position lies within radius of the circle anchor.
                // Edge case: anchor (X,Y) defaults to (0,0) and radius to 0 when no circle
                // exists, so the first guard (multiplier > 0) short-circuits before any
                // distance math — zero overhead for the common path.
                float antiSummonMult = store.TowerAntiSummonMultiplier[towerId];
                if (antiSummonMult > 0f && finalDmg > 0f)
                {
                    float circleR = store.EnemyInSummonCircleRadius[enemyId];
                    if (circleR > 0f)
                    {
                        float cx = store.EnemyInSummonCircleX[enemyId];
                        float cy = store.EnemyInSummonCircleY[enemyId];
                        float ex = store.PositionX[enemyId];
                        float ey = store.PositionY[enemyId];
                        float dx = ex - cx;
                        float dy = ey - cy;
                        if (dx * dx + dy * dy <= circleR * circleR)
                        {
                            finalDmg *= antiSummonMult;
                        }
                    }
                }
                // ── Tower Enchantment bonus (Round 116 Direction 3) ──
                // O(1) guard: when the tower has no active enchantment, this branch is
                // skipped entirely. When enchanted, two things happen on a successful hit:
                //   1) the matching ElementType is OR'd into EnemyElementStatus[enemyId]
                //      and the per-element EnemyElementTimer[] slot is refreshed to the
                //      configured duration, so the existing ElementalReactionSystem /
                //      DoT / freeze / shock systems trigger as if a separate spell applied
                //      the element (enables reactions, melts, freezes, etc.).
                //   2) a damage bonus (1 + bonus) is applied to finalDmg, modelling the
                //      "imbued strike" damage uplift of the enchanted weapon.
                // element lookup uses GetTowerEnchantedElement() which auto-expires by
                // comparing TowerEnchantExpiresAtTurn against store.CurrentFrame, so we
                // don't need a dedicated TickEnchant() loop.
                int enchantElem = store.GetTowerEnchantedElement(towerId);
                if (enchantElem > 0 && finalDmg > 0f)
                {
                    float enchantBonus = store.TowerEnchantBonus[towerId];
                    float enchantDur = store.TowerEnchantDuration[towerId];
                    if (enchantBonus > 0f) finalDmg *= 1f + enchantBonus;
                    if (enchantDur > 0f)
                    {
                        // Apply element to enemy — OR into status, refresh timer slot.
                        // ElementType ordinals match EnemyElementTimer layout (0=Fire..3=Poison).
                        ElementType elemBit;
                        int elemIdx;
                        switch (enchantElem)
                        {
                            case 1: elemBit = ElementType.Fire;       elemIdx = 0; break;
                            case 2: elemBit = ElementType.Ice;        elemIdx = 1; break;
                            case 3: elemBit = ElementType.Lightning; elemIdx = 2; break;
                            case 4: elemBit = ElementType.Poison;     elemIdx = 3; break;
                            default: elemBit = ElementType.None;      elemIdx = -1; break;
                        }
                        if (elemIdx >= 0)
                        {
                            store.EnemyElementStatus[enemyId] |= elemBit;
                            int timerSlot = enemyId * 4 + elemIdx;
                            // Refresh to max(currentTimer, enchantDur) so a longer-running
                            // element (e.g. an existing Fire from a prior attack) is not
                            // shortened by a shorter enchant duration.
                            if (store.EnemyElementTimer[timerSlot] < enchantDur)
                            {
                                store.EnemyElementTimer[timerSlot] = enchantDur;
                            }
                        }
                    }
                }
                // Vanguard damage transfer: if this enemy is protected by a vanguard, transfer a fraction to the vanguard
                float vanguardTransfer = store.EnemyVanguardDmgTransfer[enemyId];
                if (vanguardTransfer > 0f && finalDmg > 0f)
                {
                    ResolveVanguardDamageTransfer(enemyId, finalDmg, vanguardTransfer);
                }
                // Life Link damage split: if enemy is linked, share damage with linked partner
                float linkedDamage = 0f;
                int linkedEnemyId = -1;
                if (_lifeLinkSystem != null && store.EnemyIsLinked[enemyId])
                {
                    (finalDmg, linkedDamage, linkedEnemyId) = _lifeLinkSystem.ComputeLinkedDamage(enemyId, finalDmg);
                }
                store.EnemyHealth[enemyId] -= finalDmg;
                // Round 132 Dir 8 — honor Boss Min-Health Floor (TowerAttackSystem primary hot
                // path bypasses ApplyEnemyDamage's shield+floor route, so we re-clamp here).
                store.ApplyMinHealthFloorInPlace(enemyId);

                // ── Threat Score accumulation (Round 99 Direction 5) ──
                // Accumulate applied damage (post-saturation) into the per-frame accumulator.
                // This runs in the SERIAL phase (Phase 2 of Update), so a plain += to the
                // per-player accumulator is safe and zero-overhead. The FrameScheduler
                // post-tick hook decays PlayerRecentDPS using an EMA window.
                if (finalDmg > 0f)
                {
                    store.PlayerDPSAccumulator[playerId] += finalDmg;
                }
                // ── Mana Drain (Round 101 Direction 10) ──────────────────────────
                // Towers with ManaDrainPct > 0 drain a fraction of target enemy's current mana
                // and add it to the player mana pool. Only fires on hit (finalDmg > 0) — a
                // miss/dodge doesn't trigger drain. Zero-mana enemies (EnemyMaxMana == 0) silently
                // no-op so non-mana-wielders pay nothing in the hot path.
                if (finalDmg > 0f && store.TowerManaDrainPct[towerId] > 0f)
                {
                    float enemyMaxMana = store.EnemyMaxMana[enemyId];
                    if (enemyMaxMana > 0f)
                    {
                        float enemyCurMana = store.EnemyCurrentMana[enemyId];
                        if (enemyCurMana > 0f)
                        {
                            float drainPct = store.TowerManaDrainPct[towerId];
                            float towerCap = store.TowerManaDrainCap[towerId]; // 0 = use global cap
                            float cap = towerCap > 0f ? towerCap : ManaDrainConfig.ManaDrainCap;
                            float drain = Math.Min(cap, enemyCurMana * drainPct);
                            if (drain > 0f)
                            {
                                // Decrement enemy mana first (parallel-safe in serial phase)
                                store.EnemyCurrentMana[enemyId] = enemyCurMana - drain;
                                // Add to player mana pool, clamped to max
                                float playerCur = store.PlayerMana[playerId];
                                float playerMax = store.PlayerMaxMana[playerId];
                                store.PlayerMana[playerId] = Math.Min(playerMax, playerCur + drain);
                            }
                        }
                    }
                }
                // Stagger / Posture: heavy hits accumulate posture damage on the enemy.
                // Heuristic: damage >= 20% of max HP is a "heavy hit" worth 1 stagger point.
                // This works in the serial apply phase where we don't know if it was a crit.
                // (Crits naturally produce heavier damage and are caught by the same threshold.)
                if (finalDmg > 0f && store.EnemyStaggerMax[enemyId] > 0f)
                {
                    float maxHp = store.EnemyMaxHealth[enemyId];
                    if (maxHp > 0f && finalDmg >= 0.20f * maxHp)
                    {
                        store.AddStaggerDamage(enemyId, 1f, staggerDuration: 180, immuneSeconds: 10f);
                    }
                }
                // Life Link: apply shared damage to linked enemy
                if (linkedEnemyId >= 0 && linkedDamage > 0f)
                {
                    ApplyLinkedDamage(linkedEnemyId, linkedDamage, playerId);
                }
                // Tether damage share: if enemy is in a lock-chain, transfer a fraction of damage to partner.
                // Runs in the same serial pass as LifeLink (which only writes EnemyHealth directly).
                // Important: tether damage can also trigger a kill on the partner (stacks on top of primary).
                // O(1) guard: skip the call entirely when no tethered enemies exist (avoids per-enemy call overhead).
                if (store.ActiveTetheredCount > 0)
                    ApplyTetherDamageShare(enemyId, finalDmg, playerId);
                // Thorns: enemy reflects damage back to the player (tower attacker)
                float thornsRatio = store.EnemyThornsRatio[enemyId];
                if (thornsRatio > 0f && finalDmg > 0f)
                {
                    float thornsDamage = finalDmg * thornsRatio;
                    lock (_thornsQueueLock) { _thornsQueue[_thornsQueueIdx].Add((playerId, thornsDamage)); }
                }
                // Round 67: On-Hit / On-Crit trigger event publication (tower attack path).
                // EnemyHit fires for every applied hit. EnemyCrit fires only for damage entries
                // that match a (enemyId, towerId) pair the parallel phase flagged via _critFiredThisFrame.
                // We use a per-pair fire-once guard so a single crit attack doesn't publish EnemyCrit
                // multiple times (the same tower/enemy can have baseDmg + critBonus both flowing through).
                // We REMOVE the key from the set on first fire to avoid re-firing on later entries.
                if (finalDmg > 0f)
                {
                    long critKey = ((long)enemyId << 32) | (uint)towerId;
                    bool wasCrit = _critFiredThisFrame.Remove(critKey);
                    PublishTowerHitEvent(enemyId, towerId, finalDmg, wasCrit);
                }
                if (store.EnemyHealth[enemyId] <= 0f)
                {
                    // Overkill: if tower's damage exceeded enemy's pre-hit health, convert excess to splash
                    // excess = finalDmg - preDmgHealth (where preDmgHealth = EnemyHealth + finalDmg)
                    // Only apply if this is the killing blow (vanguard/lifelink may have been applied)
                    int okType = store.TowerOverkillType[towerId];
                    float okRatio = store.TowerOverkillRatio[towerId];
                    float okRadius = store.TowerOverkillRadius[towerId];
                    if (okType == 1 && okRatio > 0f && okRadius > 0f)
                    {
                        float preHitHealth = store.EnemyHealth[enemyId] + finalDmg; // health BEFORE subtraction we just did
                        float excess = finalDmg - preHitHealth;
                        if (excess > 0f)
                        {
                            float splashDmg = excess * okRatio;
                            // Queue as splash event; ResolveSplashDamage uses the killed enemy's
                            // position as the AoE center and skips the primary target via
                            // enemyId == primaryEnemyId check, so killed enemy is safely excluded.
                            lock (_splashDamageQueueLock) { _splashDamageQueue[_splashDamageQueueIdx].Add((enemyId, splashDmg, playerId, towerId)); }
                        }
                    }
                    // Queue both the enemy death and the tower kill for XP
                    store.QueueEnemyDeath(enemyId, playerId);
                    store.QueueTowerKill(enemyId, playerId, towerId);
                }
            }

            // Phase 2b (serial): resolve Tesla chain lightning (after basic damage to avoid double-hit on primary)
            ResolveTeslaChainLightning();

            // Phase 2c (serial): resolve splash damage from upgrade special ability
            ResolveSplashDamage();

            // Phase 2e (serial): resolve bouncing projectiles
            ResolveBounceDamage();

            // Phase 2d (serial): resolve Leech lifesteal heals
            ResolveLeechHealing();

            // Phase 2e (serial): resolve thorns damage reflect (enemy -> player)
            ResolveThornsDamage();

            System.Threading.Thread.MemoryBarrier(); // ensure drain completes

            // Phase 3 (serial): apply tower debuffs (stun/slow from Basic/EMP/Doom towers, Frost slow, Firewall DoT)
            int debuffReadIdx = _debuffQueueIdx;
            int debuffWriteIdx = 1 - _debuffQueueIdx;
            _debuffQueueIdx = debuffWriteIdx;
            _debuffQueue[debuffWriteIdx].Clear();
            foreach (var (enemyId, towerId) in _debuffQueue[debuffReadIdx])
            {
                if (!store.EnemyActive[enemyId]) continue;

                TowerType towerType = store.TowerType[towerId];
                float stunChance = store.TowerStunChance[towerId];
                float slowAmount = store.TowerSlowAmount[towerId];
                float slowDuration = store.TowerSlowDuration[towerId];
                // Round 124 — Disarm: independent of stun/slow. Read once per debuff application.
                float disarmChance = store.TowerDisarmChance[towerId];
                float disarmDuration = store.TowerDisarmDuration[towerId];

                // Apply knockback if tower has knockback force
                float kbForce = store.TowerKnockbackForce[towerId];
                if (kbForce > 0f)
                {
                    store.ApplyEnemyKnockback(enemyId, kbForce);
                }

                switch (towerType)
                {
                    case TowerType.Firewall:
                        // Firewall: apply burn DoT (continuous damage over time via BuffSystem)
                        if (buffSystem != null && slowAmount > 0f && slowDuration > 0f)
                        {
                            int actualDuration = (int)Math.Max(1, slowDuration * (1f - _enemySlowResistance));
                            buffSystem.ApplyDot(enemyId, slowAmount, actualDuration);
                        }
                        // Also roll stun
                        if (stunChance > 0f && _rand.NextDouble() < stunChance)
                        {
                            int stunTurns = Math.Max(1, (int)Math.Ceiling(1f * (1f - _enemyStunResistance)));
                            store.ApplyEnemyStun(enemyId, stunTurns);
                        }
                        // Round 128 Direction 5 — leave a brief fire trail at the hit
                        // position. SpawnTrail internally wraps AddCorpseEffect(type=3),
                        // so the CorpseEffectSystem.Update() that runs in PostDeathGroup
                        // will tick the DoT and eventually expire the zone. Multiple
                        // hits on the same spot stack additively (the AddCorpseEffect
                        // slot picker is round-robin, so a fresh hit always gets its
                        // own zone). Null _fireTrailSystem is a no-op (zero cost).
                        if (_fireTrailSystem != null)
                        {
                            _fireTrailSystem.SpawnTrail(
                                x: store.PositionX[enemyId],
                                y: store.PositionY[enemyId],
                                radius: 1.5f,
                                dps: 8.0f,
                                duration: 2.0f,
                                tickInterval: 0.5f);
                        }
                        break;

                    default:
                        // Basic / Frost / EMP / Doom: stun + slow
                        if (stunChance > 0f && _rand.NextDouble() < stunChance)
                        {
                            int stunTurns = Math.Max(1, (int)Math.Ceiling(1f * (1f - _enemyStunResistance)));
                            store.ApplyEnemyStun(enemyId, stunTurns);
                        }
                        if (slowAmount > 0f && slowDuration > 0f)
                        {
                            int actualDuration = (int)Math.Max(1, slowDuration * (1f - _enemySlowResistance));
                            store.ApplyEnemySlow(enemyId, slowAmount, actualDuration);
                        }
                        // Round 124 — Disarm: separate from stun, doesn't share stun-resistance.
                        // Applies after stun/slow so the enemy still has a chance to be stunned first.
                        if (disarmChance > 0f && disarmDuration > 0f && _rand.NextDouble() < disarmChance)
                        {
                            int disarmTurns = Math.Max(1, (int)Math.Ceiling(disarmDuration));
                            store.ApplyEnemyDisarm(enemyId, disarmTurns);
                        }
                        // Apply healing reduction debuff on every tower hit
                        store.EnemyHealingReduction[enemyId] = Math.Max(store.EnemyHealingReduction[enemyId], HEALING_REDUCTION_AMOUNT);
                        store.EnemyHealingReductionDuration[enemyId] = Math.Max(store.EnemyHealingReductionDuration[enemyId], HEALING_REDUCTION_DURATION);
                        break;
                }
            }

            // Phase 3b (serial): decay armor shred duration (1 turn per frame)
            DecayArmorShredStacks();

            // Phase 3b.5 (serial): decay healing reduction duration
            DecayHealingReduction();

            // Phase 3c (serial): apply bleed stacks from bleed towers (Slash/Pierce type)
            if (bleedSystem != null)
            {
                ApplyBleedStacks();
            }

            // Phase 3d (serial): resolve tower knockback — push enemies backward
            ResolveKnockback();

            // Phase 3d (serial): resolve fragmentation projectiles — spawn child projectiles
            ResolveFragmentProjectiles();

            // Reset bounce hit counter at end of attack resolution for all active towers
            foreach (var tid in store.ActiveTowerIds)
            {
                store.TowerBounceHitsRemaining[tid] = 0;
            }

            // Round 67: clear On-Crit side-channel for next frame. Any unmatched crits
            // (e.g. crit was rolled but target was invulnerable and skipped) are dropped —
            // their affix triggers are intentionally not delivered, matching the
            // "no EnemyHit when finalDamage=0" rule for the primary hit path.
            _critFiredThisFrame.Clear();

            System.Threading.Thread.MemoryBarrier();
        }

        /// <summary>
        /// Decay armor shred duration by 1 turn. When duration reaches 0, clear shred stacks.
        /// Called once per frame in the debuff phase.
        /// </summary>
        private void DecayArmorShredStacks()
        {
            var enemyIds = store.GetCachedActiveEnemyIds();
            int count = enemyIds.Count;
            for (int i = 0; i < count; i++)
            {
                int enemyId = enemyIds[i];
                float duration = store.EnemyArmorShredDuration[enemyId];
                if (duration > 0f)
                {
                    store.EnemyArmorShredDuration[enemyId] = duration - 1f;
                    if (store.EnemyArmorShredDuration[enemyId] <= 0f)
                    {
                        store.EnemyArmorShredStacks[enemyId] = 0f;
                        store.EnemyArmorShredDuration[enemyId] = 0f;
                    }
                }
            }
        }

        /// <summary>
        /// Decay healing reduction duration by 1 turn per frame. When duration reaches 0, clear reduction.
        /// Called once per frame in the debuff phase (Phase 3b.5), after armor shred decay.
        /// </summary>
        private void DecayHealingReduction()
        {
            var enemyIds = store.GetCachedActiveEnemyIds();
            int count = enemyIds.Count;
            for (int i = 0; i < count; i++)
            {
                int enemyId = enemyIds[i];
                float duration = store.EnemyHealingReductionDuration[enemyId];
                if (duration > 0f)
                {
                    store.EnemyHealingReductionDuration[enemyId] = duration - 1f;
                    if (store.EnemyHealingReductionDuration[enemyId] <= 0f)
                    {
                        store.EnemyHealingReduction[enemyId] = 0f;
                        store.EnemyHealingReductionDuration[enemyId] = 0f;
                    }
                }
            }
        }

        /// <summary>
        /// Apply bleed stacks from bleed towers to enemies in their attack range.
        /// Called during Phase 3c debuff resolution (after armor shred decay, before knockback).
        /// Only applies to towers with TowerIsBleedTower = true.
        /// </summary>
        private void ApplyBleedStacks()
        {
            foreach (int towerId in store.ActiveTowerIds)
            {
                if (!store.TowerIsBleedTower[towerId]) continue;

                float stacksPerHit = store.TowerBleedStacksPerHit[towerId];
                float dmgPct = store.TowerBleedDmgPct[towerId];
                float duration = store.TowerBleedDuration[towerId];
                if (stacksPerHit <= 0f || dmgPct <= 0f || duration <= 0f) continue;

                // Tower position for range check
                float tx = store.PositionX[towerId];
                float ty = store.PositionY[towerId];
                int range = store.TowerRange[towerId];
                int rangeSq = range * range;

                // Get all enemies in range (no need to check "bestTarget" — bleed applies to all in range)
                foreach (int enemyId in store.ActiveEnemyIds)
                {
                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];
                    float dx = ex - tx;
                    float dy = ey - ty;
                    if (dx * dx + dy * dy > rangeSq) continue;
                    if (!store.EnemyActive[enemyId]) continue;

                    // Apply bleed: dmgPerStack = dmgPct (already a fraction like 0.01 = 1% of max HP per stack)
                    bleedSystem.ApplyBleedFromTower(towerId, enemyId, stacksPerHit, dmgPct, duration);
                }
            }
        }

        /// <summary>
        /// Vanguard damage transfer: if the target enemy is protected by a vanguard,
        /// transfer a fraction of damage taken to the vanguard entity.
        /// O(enemies) scan for vanguard in front-line positions; vanguard must be alive and active.
        /// </summary>
        private void ResolveVanguardDamageTransfer(int protectedEnemyId, float damage, float transferRatio)
        {
            float transferredDamage = damage * transferRatio;
            if (transferredDamage <= 0f) return;

            var enemyIds = store.GetCachedActiveEnemyIds();
            int count = enemyIds.Count;
            float targetX = store.PositionX[protectedEnemyId];
            float targetY = store.PositionY[protectedEnemyId];

            // Vanguard must be in front (lower Y = closer to player base = front line)
            // Scan for a living vanguard ahead of the protected enemy
            for (int i = 0; i < count; i++)
            {
                int vanguardId = enemyIds[i];
                if (!store.EnemyActive[vanguardId]) continue;
                if (!store.EnemyIsVanguard[vanguardId]) continue;
                if (store.EnemyHealth[vanguardId] <= 0f) continue;

                float vx = store.PositionX[vanguardId];
                float vy = store.PositionY[vanguardId];

                // Vanguard must be ahead (lower Y = front) and within cover range
                if (vy >= targetY) continue; // not ahead

                float coverRange = store.EnemyVanguardCoverRange[vanguardId];
                float dy = targetY - vy;
                if (coverRange >= 0f && dy > coverRange) continue; // outside cover range

                // Found a protecting vanguard — apply transferred damage to it
                store.EnemyHealth[vanguardId] -= transferredDamage;
                // Round 132 Dir 8 — honor Boss Min-Health Floor on vanguard transfer route.
                store.ApplyMinHealthFloorInPlace(vanguardId);
                if (store.EnemyHealth[vanguardId] <= 0f)
                {
                    store.QueueEnemyDeath(vanguardId, store.PlayerEntityId);
                }
                return; // only one vanguard shields each enemy (first match wins)
            }
        }

        /// <summary>
        /// Apply life link shared damage to a linked enemy.
        /// The linked enemy takes full damage (no further splitting — links are not recursive).
        /// Break penalties are handled by EnemyLifeLinkSystem.ResolveBreakPenalties() post death.
        /// </summary>
        private void ApplyLinkedDamage(int linkedEnemyId, float linkedDamage, int playerId)
        {
            if (linkedEnemyId < 0 || linkedDamage <= 0f) return;
            if (!store.EnemyActive[linkedEnemyId]) return;

            // Apply damage resistance for the linked enemy
            float resist = store.EnemyDamageResistance[linkedEnemyId];
            float finalLinkedDmg = resist >= 1f ? 0f : linkedDamage * (1f - resist);

            store.EnemyHealth[linkedEnemyId] -= finalLinkedDmg;
            // Round 132 Dir 8 — honor Boss Min-Health Floor on LifeLink partner route.
            store.ApplyMinHealthFloorInPlace(linkedEnemyId);

            // Thorns on linked enemy (if any — rare but possible)
            float thornsRatio = store.EnemyThornsRatio[linkedEnemyId];
            if (thornsRatio > 0f && finalLinkedDmg > 0f)
            {
                float thornsDamage = finalLinkedDmg * thornsRatio;
                lock (_thornsQueueLock) { _thornsQueue[_thornsQueueIdx].Add((playerId, thornsDamage)); }
            }

            // Check if linked enemy dies from shared damage
            if (store.EnemyHealth[linkedEnemyId] <= 0f)
            {
                store.QueueEnemyDeath(linkedEnemyId, playerId);
                store.QueueTowerKill(linkedEnemyId, playerId, -1); // towerId=-1 (shared damage has no tower)
            }
        }

        /// <summary>
        /// Apply Tether lock-chain damage sharing: if `enemyId` is in a tether with
        /// `EnemyTetherDamageSharePct > 0`, transfer that fraction of `finalDmg`
        /// to the partner. The partner is also subject to its own damage resistance.
        /// Stun sharing is NOT done here (would require a separate stun queue + GameManager integration
        /// that crosses the 5-file limit); left as a future extension via EnemyTetherStunSharePct.
        ///
        /// Note: Tether damage share is NOT recursive — the partner takes the full share
        /// (not its own TetherDamageSharePct × share). This avoids infinite chains.
        /// </summary>
        private void ApplyTetherDamageShare(int enemyId, float finalDmg, int playerId)
        {
            if (enemyId < 0 || finalDmg <= 0f) return;
            // Bounds check on the array
            if (enemyId >= ComponentStore.MAX_ENTITIES) return;

            float sharePct = store.EnemyTetherDamageSharePct[enemyId];
            if (sharePct <= 0f) return;

            int partnerId = store.EnemyTetherPartnerId[enemyId];
            // partnerId == 0 is the default-int sentinel for "no partner configured".
            // We use `> 0` so we don't accidentally treat entity 0 as a valid partner
            // (and to avoid self-damage if entity 0 is configured to point at itself).
            if (partnerId <= 0 || partnerId == enemyId) return;
            if (partnerId >= ComponentStore.MAX_ENTITIES) return;
            if (!store.EnemyActive[partnerId]) return;
            // Partner must also be in a tether (defensive: ensure both sides are configured)
            if (store.EnemyTetherMaxLength[partnerId] <= 0f) return;

            float sharedDmg = finalDmg * sharePct;
            // Apply partner's damage resistance
            float partnerResist = store.EnemyDamageResistance[partnerId];
            float finalShared = partnerResist >= 1f ? 0f : sharedDmg * (1f - partnerResist);
            if (finalShared <= 0f) return;

            // Tether: avoid self-damage to invulnerable partner (e.g. immune to physical)
            if (store.EnemyIsInvulnerable[partnerId]) return;

            store.EnemyHealth[partnerId] -= finalShared;
            // Round 132 Dir 8 — honor Boss Min-Health Floor on Tether partner route.
            store.ApplyMinHealthFloorInPlace(partnerId);

            // Thorns on partner (if any)
            float partnerThorns = store.EnemyThornsRatio[partnerId];
            if (partnerThorns > 0f && finalShared > 0f)
            {
                float thornsDamage = finalShared * partnerThorns;
                lock (_thornsQueueLock) { _thornsQueue[_thornsQueueIdx].Add((playerId, thornsDamage)); }
            }

            // Tether shared damage can also kill the partner — queue death + kill
            if (store.EnemyHealth[partnerId] <= 0f)
            {
                store.QueueEnemyDeath(partnerId, playerId);
                store.QueueTowerKill(partnerId, playerId, -1); // towerId=-1 (tether has no source tower)
            }
        }

        private const int TESLA_MAX_CHAIN_HOPS = 3;        // primary + 3 hops
        private const float TESLA_CHAIN_DAMAGE_DECAY = 0.70f; // each hop deals 70% of previous
        private bool[] _teslaChainHitBuffer = new bool[0];
        private int _teslaChainHitBufferSize = 0;

        /// <summary>
        /// Resolve Tesla chain lightning: two-phase.
        /// Phase 1: for each chainId==0 entry, find nearest neighbors and add chain hops to the queue.
        /// Phase 2: apply all damage from the queue (both primary and chain hops).
        /// </summary>
        private void ResolveTeslaChainLightning()
        {
            int readIdx = _chainDamageQueueIdx;
            int writeIdx = 1 - _chainDamageQueueIdx;
            _chainDamageQueue[writeIdx].Clear();
            _chainDamageQueueIdx = writeIdx;

            foreach (var (chainId, enemyId, damage, playerId, towerId) in _chainDamageQueue[readIdx])
            {
                if (!store.EnemyActive[enemyId]) continue;

                if (chainId == 0)
                {
                    // Phase 1: find chain hops for this primary target and add to queue
                    ResolveChainHops(enemyId, damage, playerId, towerId);
                    // Primary damage already applied via _damageQueue; no double-apply needed here
                }
                else
                {
                    // Phase 2: apply chain hop damage
                    if (store.EnemyIsInvulnerable[enemyId]) continue;
                    store.EnemyHealth[enemyId] -= damage;
                    // Round 132 Dir 8 — honor Boss Min-Health Floor on chain hop route.
                    store.ApplyMinHealthFloorInPlace(enemyId);
                    if (store.EnemyHealth[enemyId] <= 0f)
                    {
                        store.QueueEnemyDeath(enemyId, playerId);
                    }
                }
            }
        }

        /// <summary>
        /// Find nearest neighbors from the primary target and add chain hops to _chainDamageQueue.
        /// O(N) scan for each hop, up to TESLA_MAX_CHAIN_HOPS hops at TESLA_CHAIN_DAMAGE_DECAY decay.
        /// </summary>
        private void ResolveChainHops(int primaryEnemyId, float primaryDamage, int playerId, int towerId)
        {
            if (_activeEnemyList == null || _activeEnemyList.Count == 0) return;
            var activeEnemyIds = _activeEnemyList;
            int count = activeEnemyIds.Count;

            // Ensure pooled hit buffer is large enough
            if (_teslaChainHitBufferSize < count)
            {
                _teslaChainHitBuffer = new bool[count];
                _teslaChainHitBufferSize = count;
            }
            else
            {
                Array.Clear(_teslaChainHitBuffer, 0, _teslaChainHitBufferSize);
                _teslaChainHitBufferSize = count;
            }

            // Mark primary as hit so it doesn't chain to itself
            int primaryIdx = -1;
            for (int i = 0; i < count; i++)
            {
                if (activeEnemyIds[i] == primaryEnemyId)
                {
                    primaryIdx = i;
                    break;
                }
            }
            if (primaryIdx >= 0) _teslaChainHitBuffer[primaryIdx] = true;

            float currentDamage = primaryDamage * TESLA_CHAIN_DAMAGE_DECAY;
            float originX = store.PositionX[primaryEnemyId];
            float originY = store.PositionY[primaryEnemyId];

            // Tower attack range as chain hop radius
            int chainRange = store.TowerRange[towerId];
            int rangeSq = chainRange * chainRange;

            for (int hop = 1; hop <= TESLA_MAX_CHAIN_HOPS; hop++)
            {
                int bestIdx = -1;
                float bestDistSq = float.MaxValue;

                for (int i = 0; i < count; i++)
                {
                    if (_teslaChainHitBuffer[i]) continue;
                    int enemyId = activeEnemyIds[i];
                    if (!store.EnemyActive[enemyId]) continue;
                    if (enemyId == playerId) continue;

                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];
                    float dx = ex - originX;
                    float dy = ey - originY;
                    float distSq = dx * dx + dy * dy;

                    if (distSq <= rangeSq && distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestIdx = i;
                    }
                }

                if (bestIdx == -1) break;

                _teslaChainHitBuffer[bestIdx] = true;
                int hopEnemyId = activeEnemyIds[bestIdx];

                // Queue chain hop damage: chainId > 0 means apply damage
                int writeIdx = _chainDamageQueueIdx;
                _chainDamageQueue[writeIdx].Add((hop, hopEnemyId, currentDamage, playerId, towerId));

                originX = store.PositionX[hopEnemyId];
                originY = store.PositionY[hopEnemyId];
                currentDamage *= TESLA_CHAIN_DAMAGE_DECAY;
            }
        }

        /// <summary>
        /// Resolve splash damage from tower upgrade special abilities.
        /// Deals reduced damage to enemies near the primary target.
        /// </summary>
        private void ResolveSplashDamage()
        {
            int readIdx = _splashDamageQueueIdx;
            int writeIdx = 1 - _splashDamageQueueIdx;
            _splashDamageQueueIdx = writeIdx;
            _splashDamageQueue[writeIdx].Clear();

            foreach (var (primaryEnemyId, splashDamage, playerId, towerId) in _splashDamageQueue[readIdx])
            {
                if (!store.EnemyActive[primaryEnemyId]) continue;
                if (store.EnemyIsInvulnerable[primaryEnemyId]) continue;
                // Effective splash radius: use TowerSplashRadius if set, else fall back to
                // TowerOverkillRadius (lets overkill-triggered splash work even on non-splash towers)
                float effectiveRadius = store.TowerSplashRadius[towerId] > 0f
                    ? store.TowerSplashRadius[towerId]
                    : store.TowerOverkillRadius[towerId];
                if (effectiveRadius <= 0f) continue;

                float px = store.PositionX[primaryEnemyId];
                float py = store.PositionY[primaryEnemyId];
                int splashRadius = (int)effectiveRadius;

                // Collect nearby enemies via spatial grid
                if (splashRadius > 0 && splashRadius <= 100)
                {
                    var candidates = _towerCandidateBuffers[0]; // reuse first slot
                    int splashCount = 0;
                    store.SpatialGrid.GetEnemiesInRange(store, px, py, splashRadius, candidates, ref splashCount);

                    for (int ci = 0; ci < splashCount; ci++)
                    {
                        int enemyId = candidates[ci];
                        if (!store.EnemyActive[enemyId] || enemyId == primaryEnemyId) continue;
                        if (store.EnemyIsInvulnerable[enemyId]) continue;

                        // Apply damage resistance per target (same formula as basic tower damage)
                        float resist = store.EnemyDamageResistance[enemyId];
                        float effectiveSplash = resist >= 1f ? 0f : splashDamage * (1f - resist);

                        // Calculate distance-based falloff: inner ring full damage, outer ring reduced
                        float falloffInnerRatio = store.TowerFalloffInnerRatio[towerId];
                        float falloffOuterMult = store.TowerFalloffOuterMult[towerId];

                        // Only apply falloff if it's different from the default (no falloff)
                        if (falloffInnerRatio < 1.0f || falloffOuterMult < 1.0f)
                        {
                            float ex = store.PositionX[enemyId];
                            float ey = store.PositionY[enemyId];
                            float dx = ex - px;
                            float dy = ey - py;
                            // Avoid sqrt: compare squared distance to squared inner radius
                            float distSq = dx * dx + dy * dy;
                            float innerRadiusSq = falloffInnerRatio * falloffInnerRatio * splashRadius * splashRadius;
                            if (distSq > innerRadiusSq)
                            {
                                store.EnemyHealth[enemyId] -= effectiveSplash * falloffOuterMult;
                                // Round 132 Dir 8 — honor Boss Min-Health Floor on splash outer falloff.
                                store.ApplyMinHealthFloorInPlace(enemyId);
                                if (store.EnemyHealth[enemyId] <= 0f)
                                    store.QueueEnemyDeath(enemyId, playerId);
                            }
                            else
                            {
                                store.EnemyHealth[enemyId] -= effectiveSplash;
                                // Round 132 Dir 8 — honor Boss Min-Health Floor on splash inner zone.
                                store.ApplyMinHealthFloorInPlace(enemyId);
                                if (store.EnemyHealth[enemyId] <= 0f)
                                    store.QueueEnemyDeath(enemyId, playerId);
                            }
                        }
                        else
                        {
                            store.EnemyHealth[enemyId] -= effectiveSplash;
                            // Round 132 Dir 8 — honor Boss Min-Health Floor on splash route (no falloff).
                            store.ApplyMinHealthFloorInPlace(enemyId);
                            if (store.EnemyHealth[enemyId] <= 0f)
                                store.QueueEnemyDeath(enemyId, playerId);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Resolve Leech lifesteal healing: apply player HP regen from Leech tower damage.
        /// </summary>
        private void ResolveLeechHealing()
        {
            int readIdx = _healQueueIdx;
            int writeIdx = 1 - _healQueueIdx;
            _healQueueIdx = writeIdx;
            _healQueue[writeIdx].Clear();

            foreach (var (playerId, healAmount) in _healQueue[readIdx])
            {
                if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) continue;
                float maxHealth = store.GetPlayerMaxHealth(playerId);
                float currentHealth = store.GetPlayerCurrentHealth(playerId);
                float newHealth = Math.Min(currentHealth + healAmount, maxHealth);
                store.SetPlayerCurrentHealth(playerId, newHealth);
            }
        }

        /// <summary>
        /// Phase 3d: resolve fragmentation projectiles — spawn child projectiles at impact position.
        /// Each fragment targets a nearby enemy within the configured fragment range.
        /// </summary>
        private void ResolveFragmentProjectiles()
        {
            if (projectileSystem == null) return;

            int readIdx = _fragmentQueueIdx;
            int writeIdx = 1 - _fragmentQueueIdx;
            _fragmentQueueIdx = writeIdx;
            _fragmentQueue[writeIdx].Clear();

            foreach (var (enemyId, damage, playerId, towerId, fragCount, fragRange) in _fragmentQueue[readIdx])
            {
                if (!store.EnemyActive[enemyId]) continue;
                if (fragCount <= 0) continue;

                float originX = store.PositionX[enemyId];
                float originY = store.PositionY[enemyId];

                var enemyIds = store.GetCachedActiveEnemyIds();
                var candidates = new System.Collections.Generic.List<(int eid, float distSq)>(fragCount * 2);
                float rangeSq = fragRange * fragRange;

                for (int i = 0; i < enemyIds.Count; i++)
                {
                    int eid = enemyIds[i];
                    if (eid == enemyId || !store.EnemyActive[eid]) continue;
                    float edx = store.PositionX[eid] - originX;
                    float edy = store.PositionY[eid] - originY;
                    float distSq = edx * edx + edy * edy;
                    if (distSq <= rangeSq)
                    {
                        candidates.Add((eid, distSq));
                    }
                }

                if (candidates.Count == 0) continue;

                candidates.Sort((a, b) => a.distSq.CompareTo(b.distSq));

                int toSpawn = Math.Min(fragCount, candidates.Count);
                float totalAngle = MathF.PI * 2f; // full circle fan spread
                for (int i = 0; i < toSpawn; i++)
                {
                    int eid = candidates[i].eid;
                    float angle = (totalAngle / toSpawn) * i;
                    float nx = MathF.Cos(angle);
                    float ny = MathF.Sin(angle);
                    float targetX = originX + nx * 0.5f;
                    float targetY = originY + ny * 0.5f;

                    // Retrieve tower properties for the fragment projectile
                    // Note: TowerProjectileSpeed uses a default of 10f if not configured
                    float speed = 10f;
                    bool isHoming = store.TowerProjectileHoming[towerId];
                    int pierceCount = store.TowerProjectilePierceCount[towerId];
                    float pierceFalloff = store.TowerProjectilePierceDmgFalloff[towerId];
                    int towerFragCount = store.TowerProjectileFragmentCount[towerId];
                    float towerFragRange = store.TowerProjectileFragmentRange[towerId];
                    float towerFragDmgMult = store.TowerProjectileFragmentDmgMult[towerId];
                    // Round 114 — Lead Aim: pass tower's leadAimFactor to fragment projectile fire path
                    // so predictive-aim towers pre-compensate the target's motion at fire time.
                    float leadAimFactor = store.TowerLeadAimFactor[towerId];

                    // Fire fragment projectile — uses homing to track the target enemy
                    projectileSystem.Fire(towerId, eid, damage, playerId, speed, isHoming,
                        pierceCount, pierceFalloff, towerFragCount, towerFragRange, towerFragDmgMult, leadAimFactor);
                }
            }
        }

        /// <summary>
        /// Phase 2e: resolve thorns damage — enemy reflects damage back to the attacking player.
        /// </summary>
        private void ResolveThornsDamage()
        {
            int readIdx = _thornsQueueIdx;
            int writeIdx = 1 - _thornsQueueIdx;
            _thornsQueueIdx = writeIdx;
            _thornsQueue[writeIdx].Clear();

            foreach (var (playerId, thornsDamage) in _thornsQueue[readIdx])
            {
                if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) continue;
                store.DecreasePlayerHealth(playerId, thornsDamage);
            }
        }

        /// <summary>
        /// Phase 2f: resolve tower knockback — push enemies backward along the path.
        /// Knockback moves the enemy toward y=max (retreat direction), opposite to normal movement.
        /// Resistance is applied so high-resistance enemies take reduced knockback.
        /// </summary>
        private void ResolveKnockback()
        {
            var enemyIds = store.GetCachedActiveEnemyIds();
            int count = enemyIds.Count;

            for (int i = 0; i < count; i++)
            {
                int enemyId = enemyIds[i];
                if (!store.EnemyActive[enemyId]) continue;

                float kbForce = store.EnemyKnockbackForceLeft[enemyId];
                if (kbForce <= 0f) continue;

                // Apply resistance: effectiveForce = force * (1 - resistance)
                float resist = store.EnemyKnockbackResistance[enemyId];
                float effectiveForce = kbForce * (1f - resist);
                if (effectiveForce <= 0f) continue;

                // Push enemy backward (positive y direction = retreat = away from player)
                float y = store.PositionY[enemyId];
                float maxY = _mapWidthMinusOne; // same bound as EnemyMovementSystem
                y += effectiveForce;
                if (y > maxY) y = maxY;
                store.PositionY[enemyId] = y;

                // Decay knockback force by 1 per frame (single frame duration by default)
                store.EnemyKnockbackForceLeft[enemyId] = 0f; // consume entire knockback this frame
            }
        }

        /// <summary>
        /// Resolve bouncing projectile chain: for each bounce entry, find the next nearest
        /// enemy within bounce range and queue bounce damage. All bounce damage flows through
        /// _damageQueue (same ping-pong queue as tower attack damage) for consistent deferred
        /// resolution. BounceLevel 0 = primary hit (already applied via _damageQueue before this
        /// call). BounceLevel >= 1 = bounce hop damage, applied via _damageQueue here.
        /// </summary>
        private void ResolveBounceDamage()
        {
            int readIdx = _bounceDamageQueueIdx;
            int writeIdx = 1 - _bounceDamageQueueIdx;
            _bounceDamageQueue[writeIdx].Clear();
            _bounceDamageQueueIdx = writeIdx;

            var enemyIds = store.GetCachedActiveEnemyIds();
            int enemyCount = enemyIds.Count;

            foreach (var (bounceLevel, enemyId, damage, playerId, tid) in _bounceDamageQueue[readIdx])
            {
                if (!store.EnemyActive[enemyId]) continue;
                if (store.EnemyIsInvulnerable[enemyId]) continue;
                if (bounceLevel < 0) continue;

                // Bounce search: only search if bounces remain for this tower
                int bouncesLeft = store.TowerBounceHitsRemaining[tid];
                if (bouncesLeft <= 0) continue;

                float bounceRange = store.TowerBounceRange[tid];
                float falloff = store.TowerBounceDamageFalloff[tid];
                if (bounceRange <= 0f) continue;

                // Find nearest enemy within bounceRange (excluding self and dead)
                int bestBounce = -1;
                float bestDistSq = float.MaxValue;
                float ex = store.PositionX[enemyId];
                float ey = store.PositionY[enemyId];
                float rangeSq = bounceRange * bounceRange;

                for (int i = 0; i < enemyCount; i++)
                {
                    int eid = enemyIds[i];
                    if (eid == enemyId || !store.EnemyActive[eid]) continue;
                    float dx = store.PositionX[eid] - ex;
                    float dy = store.PositionY[eid] - ey;
                    float dSq = dx * dx + dy * dy;
                    if (dSq <= rangeSq && dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestBounce = eid;
                    }
                }

                if (bestBounce != -1)
                {
                    float nextDmg = damage * falloff;
                    // Decrement bounce counter
                    store.TowerBounceHitsRemaining[tid]--;
                    // Queue next bounce for chain tracking (writeIdx = current frame's queue)
                    lock (_bounceDamageQueueLock) { _bounceDamageQueue[writeIdx].Add((bounceLevel + 1, bestBounce, nextDmg, playerId, tid)); }
                    // Apply bounce damage through _damageQueue (deferred, same pattern as chain/splash)
                    lock (_damageQueueLock) { _damageQueue[_damageQueueIdx].Add((bestBounce, nextDmg, playerId, tid)); }
                }
            }
        }

        /// <summary>
        /// Auto-link towers with TowerChainDmgRatio > 0 to their nearest active tower.
        /// Populates TowerLinkPartnerId for chain attack damage sharing.
        /// Called each SetTurn to stay in sync with tower placements/removals.
        /// </summary>
        private void AutoLinkChainPartners()
        {
            var towerIds = store.ActiveTowerIds;
            int count = towerIds.Count;
            if (count < 2) return;

            // Clear existing chain links
            for (int i = 0; i < count; i++)
            {
                int tid = towerIds[i];
                if (store.TowerChainDmgRatio[tid] > 0f && store.TowerLinkPartnerId[tid] != -1)
                {
                    // Clear partner's back-link
                    int oldPartner = store.TowerLinkPartnerId[tid];
                    if (oldPartner >= 0 && oldPartner < ComponentStore.MAX_ENTITIES)
                        store.SetTowerLinkPartnerId(oldPartner, -1);
                    store.SetTowerLinkPartnerId(tid, -1);
                }
            }

            // Pair towers with TowerChainDmgRatio > 0 to nearest active tower
            for (int i = 0; i < count; i++)
            {
                int tidA = towerIds[i];
                if (store.TowerChainDmgRatio[tidA] <= 0f) continue;
                if (store.TowerLinkPartnerId[tidA] != -1) continue; // already linked

                float xA = store.PositionX[tidA];
                float yA = store.PositionY[tidA];
                int bestPartner = -1;
                float bestDistSq = float.MaxValue;

                for (int j = 0; j < count; j++)
                {
                    if (i == j) continue;
                    int tidB = towerIds[j];
                    if (store.TowerLinkPartnerId[tidB] != -1) continue; // already linked

                    float dx = store.PositionX[tidB] - xA;
                    float dy = store.PositionY[tidB] - yA;
                    float distSq = dx * dx + dy * dy;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestPartner = tidB;
                    }
                }

                if (bestPartner != -1)
                {
                    store.SetTowerLinkPartnerId(tidA, bestPartner);
                    store.SetTowerLinkPartnerId(bestPartner, tidA);
                }
            }
        }

        /// <summary>
        /// Round 67: Publish an On-Hit / On-Crit trigger event pair for tower attacks.
        /// EnemyHit always fires (one per applied damage instance).
        /// EnemyCrit only fires when the parallel phase flagged this (enemyId, towerId) pair
        /// as a crit — the Remove() in the caller ensures we fire EnemyCrit exactly once
        /// per crit attack (not once per damage entry from the same attack).
        ///
        /// AttackerKind=1 (tower attack). Both events dispatched serially after Parallel.For
        /// completes, so subscribers see a stable snapshot of the world.
        /// </summary>
        private void PublishTowerHitEvent(int enemyId, int towerId, float damage, bool isCrit)
        {
            var hitPayload = new EnemyHitEvent
            {
                EnemyId = enemyId,
                AttackerId = towerId,
                AttackerKind = 1, // tower attack
                Damage = damage,
                IsCrit = isCrit
            };
            _eventBus.Publish(GameEvents.EnemyHit, hitPayload);
            if (isCrit)
            {
                _eventBus.Publish(GameEvents.EnemyCrit, hitPayload);
            }
        }
    }
}
