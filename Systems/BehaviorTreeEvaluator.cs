using System;
using System.Collections.Generic;
using System.Linq;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Components;

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
        public EnemyActionType PrecomputedActionEnum; // precomputed at build time; None if not Action
    }

    // ============================================================
    // Evaluator — stateless, no dictionary lookups in hot path
    // ============================================================

    public static class BTCachedTreeEvaluator
    {
        /// <summary>
        /// Evaluate behavior tree and return action string (backward-compatible, no out overhead).
        /// </summary>
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
                            return "";
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
                                continue;
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

        /// <summary>
        /// Evaluate behavior tree and return both action string and precomputed enum.
        /// Eliminates the need for StringToActionEnum() in the hot path.
        /// </summary>
        public static string EvaluateWithEnum(
            BTCachedTree bt,
            int enemyId,
            ComponentStore store,
            int playerId,
            int turn,
            out EnemyActionType precomputedEnum)
        {
            var root = bt?.Root;
            if (root == null)
            {
                precomputedEnum = EnemyActionType.None;
                return "";
            }
            return EvaluateNodeWithEnum(root, bt, enemyId, store, playerId, turn, out precomputedEnum);
        }

        private static string EvaluateNodeWithEnum(
            BTCachedNode node,
            BTCachedTree bt,
            int enemyId,
            ComponentStore store,
            int playerId,
            int turn,
            out EnemyActionType precomputedEnum)
        {
            switch (node.Type)
            {
                case BTNodeType.Sequence:
                    foreach (int childIdx in node.Children)
                    {
                        var child = bt.Nodes[childIdx];
                        if (child.Type == BTNodeType.Condition &&
                            !EvaluateCondition(child, enemyId, store, playerId))
                        {
                            precomputedEnum = EnemyActionType.None;
                            return "";
                        }
                        if (child.Type == BTNodeType.Action)
                        {
                            var a = child.Action ?? "";
                            if (!string.IsNullOrEmpty(a))
                            {
                                precomputedEnum = child.PrecomputedActionEnum;
                                return a;
                            }
                        }
                    }
                    precomputedEnum = EnemyActionType.None;
                    return "";

                case BTNodeType.Selector:
                    foreach (int childIdx in node.Children)
                    {
                        var child = bt.Nodes[childIdx];
                        if (child.Type == BTNodeType.Condition)
                        {
                            if (!EvaluateCondition(child, enemyId, store, playerId))
                                continue;
                        }
                        else if (child.Type == BTNodeType.Action)
                        {
                            var a = child.Action ?? "";
                            if (!string.IsNullOrEmpty(a))
                            {
                                precomputedEnum = child.PrecomputedActionEnum;
                                return a;
                            }
                        }
                        else if (child.Type == BTNodeType.Sequence)
                        {
                            var result = EvaluateNodeWithEnum(child, bt, enemyId, store, playerId, turn, out precomputedEnum);
                            if (!string.IsNullOrEmpty(result)) return result;
                        }
                    }
                    precomputedEnum = EnemyActionType.None;
                    return "";

                case BTNodeType.Action:
                    precomputedEnum = node.PrecomputedActionEnum;
                    return node.Action ?? "";

                default:
                    precomputedEnum = EnemyActionType.None;
                    return "";
            }
        }

        public static bool EvaluateCondition(
            BTCachedNode node,
            int enemyId,
            ComponentStore store,
            int playerId)
        {
            if (store == null) return false;
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
        /// <summary>
        /// Convert action string to EnemyActionType at build time.
        /// Same mapping as EnemyAISystem.StringToActionEnum — eliminates per-frame conversion.
        /// </summary>
        private static EnemyActionType MapActionToEnum(string action)
        {
            if (string.IsNullOrEmpty(action))
                return EnemyActionType.None;

            // Extract base action (strip parameter suffix like "(2)")
            int idx = action.IndexOf('(');
            string baseAction = idx >= 0 ? action.Substring(0, idx) : action;

            return baseAction switch
            {
                "move_to_target" => EnemyActionType.MoveToTarget,
                "attack_melee"   => EnemyActionType.AttackMelee,
                "ranged_attack"  => EnemyActionType.RangedAttack,
                "charge_attack"  => EnemyActionType.ChargeAttack,
                "dodge"          => EnemyActionType.Dodge,
                "retreat"        => EnemyActionType.Retreat,
                _                => EnemyActionType.None,
            };
        }

        public static BTCachedTree Build(BehaviorTreeDef bt)
        {
            if (bt == null || bt.Nodes == null || string.IsNullOrEmpty(bt.RootId))
                return null;

            var cached = new BTCachedTree { MonsterType = bt.MonsterType };

            // Index: nodeId → array index
            var nodeIds = new List<string>(bt.Nodes.Keys);
            var indexMap = new Dictionary<string, int>(nodeIds.Count);
            for (int i = 0; i < nodeIds.Count; i++)
                indexMap[nodeIds[i]] = i;

            // Flatten nodes into array — single pass over bt.Nodes
            cached.Nodes = new BTCachedNode[nodeIds.Count];
            foreach (var kvp in bt.Nodes)
            {
                int nodeIdx = indexMap[kvp.Key];
                var n = kvp.Value;
            int[] childIndices = (n.Children == null || n.Children.Length == 0)
                ? Array.Empty<int>()
                : n.Children
                    .Select(c => indexMap.TryGetValue(c, out var idx) ? idx : -1)
                    .ToArray();
            for (int ci = 0; ci < childIndices.Length; ci++)
            {
                if (childIndices[ci] < 0)
                    Console.WriteLine($"[BT-WARN] Node '{n.Id}' references unknown child ID '{n.Children[ci]}' — dropped from cached tree");
            }
            childIndices = childIndices.Where(idx => idx >= 0).ToArray();

                cached.Nodes[nodeIdx] = new BTCachedNode
                {
                    Id = n.Id,
                    Type = Enum.TryParse<BTNodeType>(n.Type, out var t) ? t : BTNodeType.Action,
                    Action = n.Action,
                    Condition = n.Condition,
                    Operator = n.Operator ?? "<=",
                    Value = n.Value,
                    Children = childIndices,
                    PrecomputedActionEnum = n.Type == "Action" ? MapActionToEnum(n.Action) : EnemyActionType.None
                };
            }

            if (indexMap.TryGetValue(bt.RootId, out var rootIdx))
                cached.Root = cached.Nodes[rootIdx];

            return cached;
        }
    }
}