using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower Link Combo System — detects adjacent tower pairs and triggers combined attacks.
    /// 
    /// Unlike TowerSynergySystem (which operates on tower type counts), TowerLinkSystem detects
    /// specific tower type pairs placed within a grid distance range and fires a combo attack
    /// with behavior-level effects (new projectiles, debuffs, damage multipliers).
    /// 
    /// SetTurn: caches adjacent tower pairs that match link definitions.
    /// Update: fires combo attacks, applies effects, manages cooldowns.
    /// 
    /// Frame slot: runs after TowerSynergy in FrameScheduler.Tick (Phase 6).
    /// </summary>
    public class TowerLinkSystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer logger;

        // Link definitions loaded from Data/Towers/tower_links.json
        private List<TowerLinkDef> _linkDefs = new List<TowerLinkDef>();

        // Active link pairs: (towerIdA, towerIdB, linkDefId)
        // Cleared and rebuilt each SetTurn to stay in sync with tower placements.
        private List<(int towerIdA, int towerIdB, int linkDefId)> _activeLinkPairs =
            new List<(int, int, int)>();

        // Ping-pong double-buffer for link combo damage events
        private List<(int enemyId, float damage, int playerId)>[] _damageQueue =
            new List<(int, float, int)>[2];
        private readonly object _damageQueueLock = new object();
        private int _damageQueueIdx = 0;

        // Link combo type enum for inline effect routing
        private static readonly string LightningFrost = "lightning_frost_combo";
        private static readonly string FirewallLeech = "firewall_leech_heal";
        private static readonly string TeslaTesla = "tesla_tesla_arc";
        private static readonly string FrostCryo = "frost_cryo_wall";
        private static readonly string PlasmaNano = "plasma_nano_surge";

        public TowerLinkSystem(ComponentStore store, IRenderer logger)
        {
            this.store = store;
            this.logger = logger;
            _damageQueue[0] = new List<(int, float, int)>(64);
            _damageQueue[1] = new List<(int, float, int)>(64);
            LoadLinkDefs();
        }

        /// <summary>
        /// Load tower link definitions from tower_links.json at startup.
        /// </summary>
        private void LoadLinkDefs()
        {
            string configPath = Path.Combine("Data", "Towers", "tower_links.json");
            if (!File.Exists(configPath))
            {
                logger.Log("[TowerLink] Config not found, tower links disabled.");
                return;
            }

            try
            {
                string json = File.ReadAllText(configPath);
                var wrapper = JsonSerializer.Deserialize<TowerLinkWrapper>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (wrapper?.Links != null)
                {
                    _linkDefs = wrapper.Links;
                    logger.Log($"[TowerLink] Loaded {_linkDefs.Count} link definitions.");
                }
            }
            catch (Exception ex)
            {
                logger.Log($"[TowerLink] Failed to load config: {ex.Message}");
            }
        }

        private class TowerLinkWrapper
        {
            public List<TowerLinkDef> Links { get; set; } = new List<TowerLinkDef>();
        }

        /// <summary>
        /// SetTurn — scan all tower pairs, detect valid link combos, cache them.
        /// Called once per turn after spatial grid is rebuilt.
        /// </summary>
        public void SetTurn()
        {
            _activeLinkPairs.Clear();

            if (_linkDefs.Count == 0) return;

            var activeTowerIds = store.ActiveTowerIds;
            int count = activeTowerIds.Count;
            if (count < 2) return;

            // Decrement cooldowns for all towers with active link partners
            for (int i = 0; i < count; i++)
            {
                int towerId = activeTowerIds[i];
                float cd = store.GetTowerLinkCooldown(towerId);
                if (cd > 0f)
                {
                    store.SetTowerLinkCooldown(towerId, Math.Max(0f, cd - 1f));
                }
            }

            // O(n^2) pair scan — n is small (towers are limited by map grid)
            for (int i = 0; i < count; i++)
            {
                int towerIdA = activeTowerIds[i];
                TowerType typeA = store.TowerType[towerIdA];

                float xA = store.PositionX[towerIdA];
                float yA = store.PositionY[towerIdA];

                for (int j = i + 1; j < count; j++)
                {
                    int towerIdB = activeTowerIds[j];
                    TowerType typeB = store.TowerType[towerIdB];

                    // Check if this pair matches any link definition
                    for (int k = 0; k < _linkDefs.Count; k++)
                    {
                        var link = _linkDefs[k];
                        if (!IsPairMatch(typeA, typeB, link.RequiredTowerTypes))
                            continue;

                        // Check distance constraints
                        float xB = store.PositionX[towerIdB];
                        float yB = store.PositionY[towerIdB];
                        int dist = ComputeGridDistance(xA, yA, xB, yB);
                        if (dist < link.MinDistance || dist > link.MaxDistance)
                            continue;

                        // Cooldown check: neither tower must be on cooldown
                        float cdA = store.GetTowerLinkCooldown(towerIdA);
                        float cdB = store.GetTowerLinkCooldown(towerIdB);
                        if (cdA > 0f || cdB > 0f)
                            continue;

                        // Valid link pair found — register it
                        _activeLinkPairs.Add((towerIdA, towerIdB, k));

                        // Mark the partner IDs on both towers
                        store.SetTowerLinkPartnerId(towerIdA, towerIdB);
                        store.SetTowerLinkPartnerId(towerIdB, towerIdA);
                        store.TowerLinkComboType[towerIdA] = link.Id;
                        store.TowerLinkComboType[towerIdB] = link.Id;
                    }
                }
            }
        }

        private bool IsPairMatch(TowerType typeA, TowerType typeB, string[] requiredTypes)
        {
            if (requiredTypes.Length < 2) return false;
            // Bidirectional: (A, B) or (B, A) must match requiredTypes
            return (typeA.ToString() == requiredTypes[0] && typeB.ToString() == requiredTypes[1]) ||
                   (typeA.ToString() == requiredTypes[1] && typeB.ToString() == requiredTypes[0]);
        }

        private int ComputeGridDistance(float x1, float y1, float x2, float y2)
        {
            int dx = (int)Math.Abs(x2 - x1);
            int dy = (int)Math.Abs(y2 - y1);
            // Grid distance: max(dx, dy) for square grid adjacency
            return Math.Max(dx, dy);
        }

        /// <summary>
        /// Update — fire combo attacks for all active link pairs.
        /// Called once per turn in WavePhase after TowerSynergy.
        /// </summary>
        public void Update()
        {
            if (_activeLinkPairs.Count == 0) return;

            var activeEnemyIds = store.ActiveEnemyIds;

            foreach (var pair in _activeLinkPairs)
            {
                int towerIdA = pair.towerIdA;
                int towerIdB = pair.towerIdB;
                var link = _linkDefs[pair.linkDefId];
                var effect = link.ComboEffect;

                // ── Lightning + Frost: 霜雷射线 ───────────────────────────
                if (link.Id == LightningFrost && effect.DamagePerSecond > 0f)
                {
                    FireLightningFrostCombo(towerIdA, towerIdB, link, activeEnemyIds);
                }
                // ── Firewall + Leech: 燃魂汲取 ──────────────────────────
                else if (link.Id == FirewallLeech && effect.LifestealPercent > 0f)
                {
                    ApplyFirewallLeechCombo(towerIdA, towerIdB, link, activeEnemyIds);
                }
                // ── Tesla + Tesla: 双电弧 ────────────────────────────────
                else if (link.Id == TeslaTesla && effect.ChainCount > 0)
                {
                    ApplyTeslaTeslaCombo(towerIdA, towerIdB, link);
                }
                // ── Frost + Cryo: 冰墙封锁 ──────────────────────────────
                else if (link.Id == FrostCryo && effect.SlowAmount > 0f)
                {
                    ApplyFrostCryoCombo(towerIdA, towerIdB, link, activeEnemyIds);
                }
                // ── Plasma + Nano: 等离子冲击 ────────────────────────────
                else if (link.Id == PlasmaNano && effect.DamageVsHighHealthMult > 1f)
                {
                    ApplyPlasmaNanoCombo(towerIdA, towerIdB, link, activeEnemyIds);
                }
            }
        }

        private void FireLightningFrostCombo(int towerIdA, int towerIdB,
            TowerLinkDef link, IReadOnlyList<int> activeEnemyIds)
        {
            // Combo fires a lightning arc between the two towers, damaging enemies in the line.
            float xA = store.PositionX[towerIdA];
            float yA = store.PositionY[towerIdA];
            float xB = store.PositionX[towerIdB];
            float yB = store.PositionY[towerIdB];

            // Line-segment distance query from spatial grid
            float damage = link.ComboEffect.DamagePerSecond;
            int range = link.ComboEffect.ChainRange;
            int playerId = 0;

            // Collect enemies near the line segment between towers
            var currentDamageQueue = _damageQueue[_damageQueueIdx];
            currentDamageQueue.Clear();

            lock (_damageQueueLock)
            {
                foreach (int enemyId in activeEnemyIds)
                {
                    float ex = store.PositionX[enemyId];
                    float ey = store.PositionY[enemyId];
                    if (IsPointNearLine(xA, yA, xB, yB, ex, ey, range))
                    {
                        currentDamageQueue.Add((enemyId, damage, playerId));
                    }
                }
                SwapDamageQueue();
            }

            // Apply cooldown to both towers
            if (link.Cooldown > 0f)
            {
                store.SetTowerLinkCooldown(towerIdA, link.Cooldown);
                store.SetTowerLinkCooldown(towerIdB, link.Cooldown);
            }

            logger.Log($"[TowerLink] ⚡❄️ 霜雷射线 activated ({damage} dmg/s, range={range})");
        }

        private bool IsPointNearLine(float x1, float y1, float x2, float y2,
            float px, float py, float maxDist)
        {
            // Project point onto line segment, clamp to segment endpoints, measure distance
            float dx = x2 - x1;
            float dy = y2 - y1;
            float lenSq = dx * dx + dy * dy;
            if (lenSq < 0.0001f) return false;

            float t = Math.Max(0f, Math.Min(1f, ((px - x1) * dx + (py - y1) * dy) / lenSq));
            float nearX = x1 + t * dx;
            float nearY = y1 + t * dy;

            float distX = px - nearX;
            float distY = py - nearY;
            float distSq = distX * distX + distY * distY;
            return distSq <= maxDist * maxDist;
        }

        private void ApplyFirewallLeechCombo(int towerIdA, int towerIdB,
            TowerLinkDef link, IReadOnlyList<int> activeEnemyIds)
        {
            // Firewall+Leech: enemies near either tower take DoT, a fraction converts to player heal.
            float lifestealPct = link.ComboEffect.LifestealPercent;
            float dotBonus = link.ComboEffect.DotDamageBonus;

            // Find Firewall tower (A or B)
            TowerType typeA = store.TowerType[towerIdA];
            TowerType typeB = store.TowerType[towerIdB];
            int firewallId = typeA == TowerType.Firewall ? towerIdA : (typeB == TowerType.Firewall ? towerIdB : -1);
            if (firewallId == -1) return;

            // Buff the Firewall tower's DoT bonus (stored as link damage bonus)
            float existingBonus = store.GetTowerLinkDamageBonus(firewallId);
            store.SetTowerLinkDamageBonus(firewallId, existingBonus + dotBonus);

            logger.Log($"[TowerLink] 🔥💉 燃魂汲取 activated (DoT+{dotBonus:P0}, lifesteal {lifestealPct:P0})");
        }

        private void ApplyTeslaTeslaCombo(int towerIdA, int towerIdB, TowerLinkDef link)
        {
            // Tesla+Tesla: both towers get bonus chain count and damage multiplier.
            float dmgMult = link.ComboEffect.DamageMultiplier;
            int bonusChain = link.ComboEffect.ChainCount;

            foreach (int tid in new[] { towerIdA, towerIdB })
            {
                float existingBonus = store.GetTowerLinkDamageBonus(tid);
                store.SetTowerLinkDamageBonus(tid, existingBonus + (dmgMult - 1f));
                // Extra chain hops are stored via the synergy multiplier (shared mechanism)
                float existingSynergy = store.GetTowerSynergyMultiplier(tid);
                store.SetTowerSynergyMultiplier(tid, existingSynergy + bonusChain * 0.1f);
            }

            logger.Log($"[TowerLink] ⚡⚡ 双电弧 activated (+{bonusChain} chains, ×{dmgMult} dmg)");
        }

        private void ApplyFrostCryoCombo(int towerIdA, int towerIdB,
            TowerLinkDef link, IReadOnlyList<int> activeEnemyIds)
        {
            // Frost+Cryo: slow amount increases, applied to all enemies near both towers.
            float xA = store.PositionX[towerIdA];
            float yA = store.PositionY[towerIdA];
            float xB = store.PositionX[towerIdB];
            float yB = store.PositionY[towerIdB];
            float slowAmount = link.ComboEffect.SlowAmount;
            float slowDuration = link.ComboEffect.SlowDuration;
            int aoeRadius = link.ComboEffect.AoeRadius;

            // ApplySlow factor: 0.5 = 50% speed remaining (50% slow)
            float slowFactor = 1f - slowAmount;
            int duration = (int)slowDuration;

            foreach (int enemyId in activeEnemyIds)
            {
                float ex = store.PositionX[enemyId];
                float ey = store.PositionY[enemyId];
                float distA = (ex - xA) * (ex - xA) + (ey - yA) * (ey - yA);
                float distB = (ex - xB) * (ex - xB) + (ey - yB) * (ey - yB);
                float minDist = Math.Min(distA, distB);
                if (minDist <= aoeRadius * aoeRadius)
                {
                    // Apply enhanced slow (take the stronger one)
                    float existingSlowFactor = store.EnemySlowFactor[enemyId];
                    if (slowFactor < existingSlowFactor || existingSlowFactor <= 0f)
                    {
                        store.ApplySlow(enemyId, slowFactor, duration);
                    }
                }
            }

            logger.Log($"[TowerLink] ❄️🧊 冰墙封锁 activated (slow {slowAmount:P0}, {slowDuration}s)");
        }

        private void ApplyPlasmaNanoCombo(int towerIdA, int towerIdB,
            TowerLinkDef link, IReadOnlyList<int> activeEnemyIds)
        {
            // Plasma+Nano: +40% damage vs enemies above 50% HP.
            float dmgMult = link.ComboEffect.DamageVsHighHealthMult;
            float threshold = link.ComboEffect.HealthThreshold;

            // Store the bonus on both towers' link damage bonus
            foreach (int tid in new[] { towerIdA, towerIdB })
            {
                float existingBonus = store.GetTowerLinkDamageBonus(tid);
                store.SetTowerLinkDamageBonus(tid, existingBonus + (dmgMult - 1f));
            }

            logger.Log($"[TowerLink] 💥🔬 等离子冲击 activated (×{dmgMult} vs HP>{threshold:P0})");
        }

        /// <summary>
        /// Returns the current damage queue for external consumption (e.g., by a render system).
        /// </summary>
        public List<(int enemyId, float damage, int playerId)> GetDamageQueue()
        {
            return _damageQueue[1 - _damageQueueIdx];
        }

        private void SwapDamageQueue()
        {
            _damageQueueIdx = 1 - _damageQueueIdx;
        }
    }
}