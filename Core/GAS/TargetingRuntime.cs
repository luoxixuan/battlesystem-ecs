using System;
using System.Collections.Generic;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
    /// <summary>Deterministic typed target selection for player-originated abilities.</summary>
    public static class TargetingRuntime
    {
        private const int DerivedChainTargets = 4;
        private const float ChainMagnitudeDecay = 0.7f;
        private const float ChainHealMagnitudeDecay = 0.5f;

        public static bool TryCollectAllyTargets(ComponentStore store, int sourceEntityId,
            TargetingDefinition definition, List<int> targetIds, List<float> magnitudeScales)
        {
            if (store == null || targetIds == null || magnitudeScales == null ||
                !store.GetEntityHandle(sourceEntityId).IsValid) return false;
            targetIds.Clear();
            magnitudeScales.Clear();
            if (definition.Relation != RelationFilter.Allies || definition.Shape != TargetingShape.ChainHeal) return false;
            int limit = ResolveLimit(definition);
            float originX = store.PositionX[sourceEntityId];
            float originY = store.PositionY[sourceEntityId];
            float range = Range(definition);
            float scale = 1f;
            while (targetIds.Count < limit)
            {
                int best = -1;
                float bestDeficit = 0f;
                float bestDistanceSquared = float.MaxValue;
                for (int player = 0; player < ComponentStore.MAX_PLAYERS; player++)
                {
                    if (player == sourceEntityId || targetIds.Contains(player) ||
                        !store.GetEntityHandle(player).IsValid || !store.PositionActive[player] ||
                        !GameplayTagRuntime.Matches(store, player, definition.RequiredTags, definition.BlockedTags)) continue;
                    float current = store.PlayerCurrentHealth[player];
                    float maximum = store.PlayerMaxHealth[player];
                    float deficit = maximum - current;
                    if (current <= 0f || maximum <= 0f || deficit <= 0.001f) continue;
                    float dx = store.PositionX[player] - originX;
                    float dy = store.PositionY[player] - originY;
                    float distanceSquared = dx * dx + dy * dy;
                    if (distanceSquared > range * range) continue;
                    if (deficit > bestDeficit || deficit == bestDeficit &&
                        (distanceSquared < bestDistanceSquared || distanceSquared == bestDistanceSquared && player < best))
                    {
                        best = player;
                        bestDeficit = deficit;
                        bestDistanceSquared = distanceSquared;
                    }
                }
                if (best < 0) break;
                targetIds.Add(best);
                magnitudeScales.Add(scale);
                scale *= ChainHealMagnitudeDecay;
                originX = store.PositionX[best];
                originY = store.PositionY[best];
            }
            return true;
        }

        public static bool TryCollectEnemyTargets(ComponentStore store, int sourceEntityId,
            TargetingDefinition definition, List<int> targetIds, List<float> magnitudeScales)
        {
            if (store == null || targetIds == null || magnitudeScales == null ||
                !store.GetEntityHandle(sourceEntityId).IsValid)
                return false;
            targetIds.Clear();
            magnitudeScales.Clear();
            if (definition.Relation != RelationFilter.Any && definition.Relation != RelationFilter.Enemies)
                return false;

            float sourceX = store.PositionX[sourceEntityId];
            float sourceY = store.PositionY[sourceEntityId];
            int limit = ResolveLimit(definition);
            if (limit <= 0) return true;
            if (definition.Shape == TargetingShape.Chain)
                return CollectChain(store, sourceX, sourceY, definition, limit, targetIds, magnitudeScales);

            var enemies = store.ActiveEnemyIds;
            if (definition.Shape == TargetingShape.Single)
            {
                int nearest = FindNearest(store, enemies, sourceX, sourceY, Range(definition), targetIds, definition);
                if (nearest >= 0)
                {
                    targetIds.Add(nearest);
                    magnitudeScales.Add(1f);
                }
                return true;
            }

            for (int i = 0; i < enemies.Count && targetIds.Count < limit; i++)
            {
                int enemyId = enemies[i];
                if (!IsCandidate(store, enemyId, definition)) continue;
                float dx = store.PositionX[enemyId] - sourceX;
                float dy = store.PositionY[enemyId] - sourceY;
                if (!Contains(definition, dx, dy)) continue;
                targetIds.Add(enemyId);
                magnitudeScales.Add(1f);
            }
            return true;
        }

        private static bool CollectChain(ComponentStore store, float sourceX, float sourceY,
            TargetingDefinition definition, int limit, List<int> targetIds, List<float> magnitudeScales)
        {
            var enemies = store.ActiveEnemyIds;
            float x = sourceX;
            float y = sourceY;
            float magnitude = 1f;
            float range = Range(definition);
            while (targetIds.Count < limit)
            {
                int nearest = FindNearest(store, enemies, x, y, range, targetIds, definition);
                if (nearest < 0) break;
                targetIds.Add(nearest);
                magnitudeScales.Add(magnitude);
                magnitude *= ChainMagnitudeDecay;
                x = store.PositionX[nearest];
                y = store.PositionY[nearest];
            }
            return true;
        }

        private static int FindNearest(ComponentStore store, IReadOnlyList<int> enemies, float x, float y,
            float range, List<int> excluded, TargetingDefinition definition)
        {
            float maxDistanceSquared = range * range;
            float nearestDistanceSquared = float.MaxValue;
            int nearest = -1;
            for (int i = 0; i < enemies.Count; i++)
            {
                int enemyId = enemies[i];
                if (!IsCandidate(store, enemyId, definition) || excluded.Contains(enemyId)) continue;
                float dx = store.PositionX[enemyId] - x;
                float dy = store.PositionY[enemyId] - y;
                float distanceSquared = dx * dx + dy * dy;
                if (distanceSquared > maxDistanceSquared) continue;
                if (distanceSquared < nearestDistanceSquared ||
                    (distanceSquared == nearestDistanceSquared && enemyId < nearest))
                {
                    nearestDistanceSquared = distanceSquared;
                    nearest = enemyId;
                }
            }
            return nearest;
        }

        private static bool IsCandidate(ComponentStore store, int enemyId, TargetingDefinition definition) =>
            ComponentStore.IsValidEntity(enemyId) && store.EnemyActive[enemyId] && store.EnemyHealth[enemyId] > 0f &&
            GameplayTagRuntime.Matches(store, enemyId, definition.RequiredTags, definition.BlockedTags);

        private static bool Contains(TargetingDefinition definition, float dx, float dy)
        {
            float range = Range(definition);
            switch (definition.Shape)
            {
                case TargetingShape.Cross:
                    return (Math.Abs(dy) < 0.5f && Math.Abs(dx) <= range) ||
                           (Math.Abs(dx) < 0.5f && Math.Abs(dy) <= range);
                case TargetingShape.Box:
                    float halfWidth = definition.Width > 0 ? definition.Width * 0.5f : range;
                    float halfHeight = definition.Height > 0 ? definition.Height * 0.5f : range;
                    return Math.Abs(dx) <= halfWidth && Math.Abs(dy) <= halfHeight;
                case TargetingShape.Line:
                    return Math.Abs(dy) < 0.5f && Math.Abs(dx) <= range;
                case TargetingShape.Cone:
                    float distanceSquared = dx * dx + dy * dy;
                    if (distanceSquared <= 0f || distanceSquared > range * range) return false;
                    float halfAngle = (definition.Angle > 0f ? definition.Angle : 60f) * (float)Math.PI / 360f;
                    return -dy / (float)Math.Sqrt(distanceSquared) >= (float)Math.Cos(halfAngle);
                case TargetingShape.Circle:
                case TargetingShape.Freeze:
                case TargetingShape.GroundTarget:
                case TargetingShape.Slow:
                case TargetingShape.AoeStun:
                case TargetingShape.AoeRoot:
                case TargetingShape.AoeKnockback:
                    return dx * dx + dy * dy <= range * range;
                default:
                    return false;
            }
        }

        private static float Range(TargetingDefinition definition) =>
            definition.Radius > 0f ? definition.Radius : Math.Max(0, definition.Range);

        private static int ResolveLimit(TargetingDefinition definition)
        {
            if (definition.MaxTargetsMode == MaxTargetsPolicy.Unlimited) return int.MaxValue;
            if (definition.MaxTargetsMode == MaxTargetsPolicy.Fixed) return Math.Max(0, definition.MaxTargets);
            if (definition.Shape == TargetingShape.Single) return 1;
            if (definition.Shape == TargetingShape.Chain) return DerivedChainTargets;
            return int.MaxValue;
        }
    }
}
