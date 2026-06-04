using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Resource Node System — manages map resource nodes (gold mines, mana springs, tech relics).
    ///
    /// Each node:
    ///   - Produces resources per second while owned by a player
    ///   - Can be captured by enemies approaching it (capture progress mechanic)
    ///   - Can be destroyed if it has health (enemies attack the node)
    ///   - When tower is placed on a node, it generates additional bonus production
    ///
    /// Phase gates:
    ///   BuildPhase: nodes produce resources (accumulated)
    ///   WavePhase:  nodes produce + enemy capture/damage updates
    /// </summary>
    public class ResourceNodeSystem
    {
        private readonly ComponentStore _store;
        private readonly int _playerId;
        private readonly IRenderer _logger;

        // Enemy IDs currently within capture range of each node
        // Simple approach: we scan enemies each frame to find those near nodes
        private readonly float _captureRangeSq = 4f * 4f; // 2 tile radius squared

        public ResourceNodeSystem(ComponentStore store, IRenderer logger, int playerId = 0)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(store));
            _playerId = playerId;
        }

        /// <summary>
        /// Initialize resource nodes from level config.
        /// </summary>
        public void InitializeFromLevel(LevelConfig level)
        {
            if (level.ResourceNodes == null || level.ResourceNodes.Count == 0)
            {
                _store.ActiveResourceNodeCount = 0;
                return;
            }

            int count = Math.Min(level.ResourceNodes.Count, ComponentStore.MAX_RESOURCE_NODES);
            _store.ActiveResourceNodeCount = count;

            for (int i = 0; i < count; i++)
            {
                var node = level.ResourceNodes[i];
                _store.ResourceNodeX[i] = node.X;
                _store.ResourceNodeY[i] = node.Y;
                _store.ResourceNodeOwner[i] = node.InitialOwner;
                _store.ResourceNodeType[i] = node.Type;
                _store.ResourceNodeActive[i] = true;
                _store.ResourceNodeProductionRate[i] = node.ProductionRate;
                _store.ResourceNodeMaxHealth[i] = node.MaxHealth;
                _store.ResourceNodeHealth[i] = node.MaxHealth;
                _store.ResourceNodeAccumulated[i] = 0f;
                // CaptureProgress: -1 = fully owned (not being captured)
                _store.ResourceNodeCaptureProgress[i] = node.InitialOwner >= 0 ? -1f : 0f;
                _store.ResourceNodeTowerId[i] = 0; // 0 = no tower
                // Round 108 Direction 4: Resource Node Regen
                // RegenDelay > 0 enables respawn after destruction. 0/negative = legacy no-regen.
                _store.ResourceNodeRegenDelay[i] = node.RegenDelay;
                _store.ResourceNodeRegenTimer[i] = 0f;
                _store.ResourceNodeDepleted[i] = false;
            }
        }

        /// <summary>
        /// Per-frame tick — accumulate resources and handle enemy capture.
        /// Called every frame for both BuildPhase and WavePhase.
        /// </summary>
        public void Update(float deltaTime, GameState phase)
        {
            if (_store.ActiveResourceNodeCount == 0) return;

            for (int i = 0; i < _store.ActiveResourceNodeCount; i++)
            {
                // ── Phase: Regen — depleted nodes count down to respawn ─────────
                if (_store.ResourceNodeDepleted[i])
                {
                    if (_store.ResourceNodeRegenDelay[i] > 0f)
                    {
                        _store.ResourceNodeRegenTimer[i] -= deltaTime;
                        if (_store.ResourceNodeRegenTimer[i] <= 0f)
                        {
                            RespawnNode(i);
                        }
                    }
                    // While depleted: skip production / capture work
                    continue;
                }

                if (!_store.ResourceNodeActive[i]) continue;

                // ── Phase: Resource production ───────────────────────────────────
                int owner = _store.ResourceNodeOwner[i];
                if (owner >= 0)
                {
                    // Produce resources: gold → gold, mana spring → mana, tech relic → research
                    float rate = _store.ResourceNodeProductionRate[i];

                    // Bonus if a tower is placed on this node
                    int towerId = _store.ResourceNodeTowerId[i];
                    if (towerId > 0 && _store.TowerActive[towerId])
                    {
                        rate *= 2f; // double production when towered
                    }

                    float produced = rate * deltaTime;
                    _store.ResourceNodeAccumulated[i] += produced;

                    // Auto-collect: transfer accumulated resources to player
                    // GoldMine: add to PlayerGold
                    // ManaSpring: add to PlayerMana
                    // TechRelic: add to PlayerResearchPoints
                    var nodeType = (ResourceNodeTypeEnum)_store.ResourceNodeType[i];
                    switch (nodeType)
                    {
                        case ResourceNodeTypeEnum.GoldMine:
                            _store.PlayerGold[owner] += produced;
                            break;
                        case ResourceNodeTypeEnum.ManaSpring:
                            _store.PlayerMana[owner] = Math.Min(
                                _store.PlayerMana[owner] + produced,
                                _store.PlayerMaxMana[owner]);
                            break;
                        case ResourceNodeTypeEnum.TechRelic:
                            // Research is discrete points — accumulate until >= 1
                            if (_store.ResourceNodeAccumulated[i] >= 1f)
                            {
                                int rp = (int)_store.ResourceNodeAccumulated[i];
                                _store.AddResearchPoints(owner, rp);
                                _store.ResourceNodeAccumulated[i] -= rp;
                            }
                            break;
                    }
                }

                // ── Phase: Enemy capture (WavePhase only) ───────────────────────
                if (phase == GameState.WavePhase && owner >= 0)
                {
                    UpdateEnemyCapture(i);
                }
            }
        }

        /// <summary>
        /// Check if a tower is placed on a resource node. Call after TowerPlacement.
        /// Returns true if a node was found and the tower ID was registered.
        /// </summary>
        public void OnTowerPlaced(int towerId, float towerX, float towerY)
        {
            if (_store.ActiveResourceNodeCount == 0) return;

            for (int i = 0; i < _store.ActiveResourceNodeCount; i++)
            {
                if (!_store.ResourceNodeActive[i]) continue;
                float dx = _store.ResourceNodeX[i] - towerX;
                float dy = _store.ResourceNodeY[i] - towerY;
                float distSq = dx * dx + dy * dy;
                if (distSq < 0.5f * 0.5f) // within half a tile
                {
                    _store.ResourceNodeTowerId[i] = towerId;
                    break;
                }
            }
        }

        /// <summary>
        /// Called when a tower is sold or destroyed.
        /// </summary>
        public void OnTowerRemoved(int towerId)
        {
            if (_store.ActiveResourceNodeCount == 0) return;

            for (int i = 0; i < _store.ActiveResourceNodeCount; i++)
            {
                if (_store.ResourceNodeTowerId[i] == towerId)
                {
                    _store.ResourceNodeTowerId[i] = 0;
                    break;
                }
            }
        }

        /// <summary>
        /// Damage a resource node (called when enemies attack the node).
        /// </summary>
        public void DamageNode(int nodeIndex, float damage)
        {
            if (nodeIndex < 0 || nodeIndex >= _store.ActiveResourceNodeCount) return;
            if (!_store.ResourceNodeActive[nodeIndex]) return;
            if (_store.ResourceNodeMaxHealth[nodeIndex] <= 0f) return; // indestructible

            _store.ResourceNodeHealth[nodeIndex] -= damage;
            if (_store.ResourceNodeHealth[nodeIndex] <= 0f)
            {
                _store.ResourceNodeHealth[nodeIndex] = 0f;
                _store.ResourceNodeActive[nodeIndex] = false;
                // Node destroyed — becomes neutral and inactive until rebuilt (future feature)
                _store.ResourceNodeOwner[nodeIndex] = -1;
                _store.ResourceNodeTowerId[nodeIndex] = 0;

                // Round 108 Direction 4: schedule respawn if RegenDelay > 0
                if (_store.ResourceNodeRegenDelay[nodeIndex] > 0f)
                {
                    _store.ResourceNodeDepleted[nodeIndex] = true;
                    _store.ResourceNodeRegenTimer[nodeIndex] = _store.ResourceNodeRegenDelay[nodeIndex];
                }
            }
        }

        /// <summary>
        /// Restore a depleted node to full HP and re-claim initial ownership.
        /// Called by Update() when the regen timer expires. No-op for non-depleted nodes.
        /// </summary>
        private void RespawnNode(int nodeIndex)
        {
            if (!_store.ResourceNodeDepleted[nodeIndex]) return;
            // The owner field is mutated by capture to -1; for respawn we need to remember
            // the initial owner. ResourceNodeOwner was set to -1 on destruction, but
            // the live data we have is the configured initial owner. We restore by reading
            // the production type and assuming player ownership for respawn-able nodes.
            // For Round 108 we use owner=0 (default player) as a simple respawn policy.
            // The configured initial owner is preserved in the level config and could be
            // surfaced as a new field if per-node initial-owner respawn is needed later.
            _store.ResourceNodeHealth[nodeIndex] = _store.ResourceNodeMaxHealth[nodeIndex];
            _store.ResourceNodeActive[nodeIndex] = true;
            _store.ResourceNodeDepleted[nodeIndex] = false;
            _store.ResourceNodeRegenTimer[nodeIndex] = 0f;
            _store.ResourceNodeCaptureProgress[nodeIndex] = -1f; // fully owned
            // Default to player 0; advanced respawn policies can be added later
            _store.ResourceNodeOwner[nodeIndex] = 0;
        }

        /// <summary>
        /// Get a human-readable status for all active nodes.
        /// </summary>
        public string GetNodeStatus()
        {
            if (_store.ActiveResourceNodeCount == 0) return "[RESOURCE NODES] None on map";

            var lines = new System.Text.StringBuilder();
            lines.Append("[RESOURCE NODES]\n");
            for (int i = 0; i < _store.ActiveResourceNodeCount; i++)
            {
                if (!_store.ResourceNodeActive[i])
                {
                    lines.Append($"  [{i}] DESTROYED\n");
                    continue;
                }
                var nodeType = (ResourceNodeTypeEnum)_store.ResourceNodeType[i];
                int owner = _store.ResourceNodeOwner[i];
                string ownerStr = owner < 0 ? "NEUTRAL" : $"PLAYER_{owner}";
                float acc = _store.ResourceNodeAccumulated[i];
                float hp = _store.ResourceNodeHealth[i];
                float maxHp = _store.ResourceNodeMaxHealth[i];
                string hpStr = maxHp > 0 ? $" HP:{hp:F0}/{maxHp:F0}" : "";
                lines.Append($"  [{i}] {nodeType} @({_store.ResourceNodeX[i]:F1},{_store.ResourceNodeY[i]:F1}) Owner:{ownerStr} Accum:{acc:F2}{hpStr}\n");
            }
            return lines.ToString();
        }

        // ── Private helpers ─────────────────────────────────────────────────────

        private void UpdateEnemyCapture(int nodeIndex)
        {
            float nodeX = _store.ResourceNodeX[nodeIndex];
            float nodeY = _store.ResourceNodeY[nodeIndex];
            float captureRadius = _store.ResourceNodeCaptureProgress[nodeIndex];

            // Count active enemies within capture radius of this node
            int nearbyCount = 0;
                for (int e = 0; e < _store.GetActiveEnemyCount(); e++)
            {
                int enemyId = _store.ActiveEnemyIds[e];
                if (!_store.EnemyActive[enemyId]) continue;
                float dx = _store.PositionX[enemyId] - nodeX;
                float dy = _store.PositionY[enemyId] - nodeY;
                float distSq = dx * dx + dy * dy;
                if (distSq <= _captureRangeSq)
                {
                    nearbyCount++;
                }
            }

            if (nearbyCount > 0)
            {
                // Enemies are capturing — move ownership toward neutral (-1)
                // Progress: 0 = player owns, 1 = fully neutral (being captured)
                // We use a simple linear approach
                float captureSpeed = 0.1f; // progress per second per enemy
                float delta = nearbyCount * captureSpeed * (1f / 60f); // per frame at 60fps baseline
                _store.ResourceNodeCaptureProgress[nodeIndex] = Math.Min(1f,
                    _store.ResourceNodeCaptureProgress[nodeIndex] + delta);

                // When capture progress reaches 1, ownership flips to -1
                if (_store.ResourceNodeCaptureProgress[nodeIndex] >= 1f)
                {
                    _store.ResourceNodeOwner[nodeIndex] = -1;
                    _store.ResourceNodeCaptureProgress[nodeIndex] = 0f;
                }
            }
            else
            {
                // No enemies nearby — restore ownership
                // Progress moves back toward -1 (fully owned)
                if (_store.ResourceNodeCaptureProgress[nodeIndex] > -1f)
                {
                    float restoreSpeed = 0.2f * (1f / 60f);
                    _store.ResourceNodeCaptureProgress[nodeIndex] = Math.Max(-1f,
                        _store.ResourceNodeCaptureProgress[nodeIndex] - restoreSpeed);

                    if (_store.ResourceNodeCaptureProgress[nodeIndex] <= -1f)
                    {
                        // Fully restored — owner already correct
                        _store.ResourceNodeCaptureProgress[nodeIndex] = -1f;
                    }
                }
            }
        }
    }
}
