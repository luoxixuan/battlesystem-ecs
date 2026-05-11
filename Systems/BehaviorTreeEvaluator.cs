using System;
using System.Collections.Generic;
using System.Linq;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    // ============================================================
    // Cached (array-based) behavior tree types — built once, evaluated O(1) per enemy
    // ============================================================

    public enum BTNodeType { Sequence, Selector, Condition, Action }

    public class BTCachedTree
    {
        public string MonsterType;
        public BTCachedNode Root; // null → use fallback
        public BTCachedNode[] Nodes; // flat array for O(1) index access
    }

    public class BTCachedNode
    {
        public string Id;
        public BTNodeType Type;
        public string Action;       // non-null for Action nodes
        public string Condition;   // non-null for Condition nodes
        public string Operator;
        public float Value;
        public int[] Children;      // indices into BTCachedTree.Nodes
    }

    // ============================================================
    // Evaluator — stateless, no dictionary lookups in hot path
    // ============================================================

    public static class BTCachedTreeEvaluator
    {
        public static string Evaluate(
            BTCachedTree bt,
            int enemyId,
            ComponentStore store,
            int playerId,
            int turn)
        {
            var root = bt?.Root;
            if (root == null) return "";
            return EvaluateNode(root, bt, enemyId, store, playerId, turn);
        }

        private static string EvaluateNode(
            BTCachedNode node,
            BTCachedTree bt,
            int enemyId,
            ComponentStore store,
            int playerId,
            int turn)
        {
            switch (node.Type)
            {
                case BTNodeType.Sequence:
                    foreach (int childIdx in node.Children)
                    {
                        var child = bt.Nodes[childIdx];
                        if (child.Type == BTNodeType.Condition &&
                            !EvaluateCondition(child, enemyId, store, playerId))
                            return ""; // condition false → sequence fails
                        if (child.Type == BTNodeType.Action)
                        {
                            var a = child.Action ?? "";
                            if (!string.IsNullOrEmpty(a)) return a;
                        }
                    }
                    return "";

                case BTNodeType.Selector:
                    foreach (int childIdx in node.Children)
                    {
                        var child = bt.Nodes[childIdx];
                        if (child.Type == BTNodeType.Condition)
                        {
                            if (!EvaluateCondition(child, enemyId, store, playerId))
                                continue; // condition false → try next child
                        }
                        else if (child.Type == BTNodeType.Action)
                        {
                            var a = child.Action ?? "";
                            if (!string.IsNullOrEmpty(a)) return a;
                        }
                        else if (child.Type == BTNodeType.Sequence)
                        {
                            var result = EvaluateNode(child, bt, enemyId, store, playerId, turn);
                            if (!string.IsNullOrEmpty(result)) return result;
                        }
                    }
                    return "";

                case BTNodeType.Action:
                    return node.Action ?? "";

                default:
                    return "";
            }
        }

        public static bool EvaluateCondition(
            BTCachedNode node,
            int enemyId,
            ComponentStore store,
            int playerId)
        {
            switch (node.Condition)
            {
                case "target_in_range":
                {
                    float ex = store.PositionX[enemyId], ey = store.PositionY[enemyId];
                    float px = store.PositionX[playerId], py = store.PositionY[playerId];
                    float dist = Math.Abs(ex - px) + Math.Abs(ey - py);
                    return Compare(dist, node.Value, node.Operator);
                }
                case "health_below":
                {
                    float h = store.EnemyHealth[enemyId], mh = store.EnemyMaxHealth[enemyId];
                    if (mh <= 0f) return false;
                    return Compare(h / mh, node.Value, node.Operator);
                }
                case "can_attack":
                {
                    float ex = store.PositionX[enemyId], ey = store.PositionY[enemyId];
                    float px = store.PositionX[playerId], py = store.PositionY[playerId];
                    return Compare(Math.Abs(ex - px) + Math.Abs(ey - py), 1.5f, node.Operator);
                }
                case "is_charging":
                    return Compare(store.GetEnemyAIChargeCounter(enemyId), 0, ">");
                default:
                    return false;
            }
        }

        private static bool Compare(float lhs, float rhs, string op)
        {
            return op switch
            {
                ">"  => lhs > rhs,
                "<"  => lhs < rhs,
                ">=" => lhs >= rhs,
                "<=" => lhs <= rhs,
                "==" => lhs == rhs,
                "!=" => lhs != rhs,
                _    => lhs <= rhs,
            };
        }
    }

    // ============================================================
    // Builder — converts dictionary-based BehaviorTreeDef → flat array BTCachedTree
    // Called once at startup or first use, not in hot path
    // ============================================================

    public static class BTCachedTreeBuilder
    {
        public static BTCachedTree Build(BehaviorTreeDef bt)
        {
            if (bt == null || bt.Nodes == null || string.IsNullOrEmpty(bt.RootId))
                return null;

            var cached = new BTCachedTree { MonsterType = bt.MonsterType };

            // Index: nodeId → array index
            var nodeIds = new List<string>(bt.Nodes.Keys);
            var indexMap = new Dictionary<string, int>();
            for (int i = 0; i < nodeIds.Count; i++)
                indexMap[nodeIds[i]] = i;

            // Flatten nodes into array
            cached.Nodes = new BTCachedNode[nodeIds.Count];
            foreach (var kvp in bt.Nodes)
            {
                var n = kvp.Value;
                int[] childIndices = (n.Children == null || n.Children.Length == 0)
                    ? Array.Empty<int>()
                    : n.Children
                        .Select(c => indexMap.TryGetValue(c, out var idx) ? idx : -1)
                        .Where(idx => idx >= 0)
                        .ToArray();

                cached.Nodes[indexMap[kvp.Key]] = new BTCachedNode
                {
                    Id = n.Id,
                    Type = Enum.TryParse<BTNodeType>(n.Type, out var t) ? t : BTNodeType.Action,
                    Action = n.Action,
                    Condition = n.Condition,
                    Operator = n.Operator ?? "<=",
                    Value = n.Value,
                    Children = childIndices
                };
            }

            if (indexMap.TryGetValue(bt.RootId, out var rootIdx))
                cached.Root = cached.Nodes[rootIdx];

            return cached;
        }
    }
}