using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 钻地/潜行敌人系统 — 管理敌人的钻地状态转换和地下移动逻辑。
    /// 
    /// 钻地敌人从地面消失（不可被选中）、在地下移动、然后破土而出造成 AoE 伤害。
    /// 
    /// 帧顺序：
    ///   1. Update() — 递减冷却、触发钻地/出土动作
    ///   2. ApplyBurrowEffects() — 串行应用出土 AoE 伤害
    /// </summary>
    public class EnemyBurrowSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;

        // 并行收集出土 AoE 事件，串行 apply
        private ConcurrentBag<(int enemyId, float dmg, float radius, int playerId)> _emergeAoeQueue =
            new ConcurrentBag<(int, float, float, int)>();

        public EnemyBurrowSystem(ComponentStore store, int playerId)
        {
            this.store = store;
            this.playerId = playerId;
        }

        public void SetTurn(int turn)
        {
            // No per-turn cache needed — direct array access
        }

        /// <summary>
        /// 每帧调用：递减钻地计时器、管理状态转换、收集出土 AoE 事件。
        /// </summary>
        public void Update()
        {
            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            int count = activeEnemyIds.Count;

            Parallel.For(0, count, ParallelOptionsCache.HotPath, i =>
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId])
                    return;

                // Skip non-burrowable enemies
                float cooldown = store.EnemyBurrowCooldown[enemyId];
                if (cooldown < 0f) // -1 means cannot burrow
                    return;

                bool isBurrowed = store.EnemyIsBurrowed[enemyId];
                float burrowTimer = store.EnemyBurrowTimer[enemyId];

                if (isBurrowed)
                {
                    // === 钻地状态：递减钻地计时器 ===
                    burrowTimer -= 1f;
                    store.EnemyBurrowTimer[enemyId] = burrowTimer;

                    if (burrowTimer <= 0f)
                    {
                        // 计时结束 → 触发出土
                        isBurrowed = false;
                        store.EnemyIsBurrowed[enemyId] = false;
                        store.EnemyBurrowTimer[enemyId] = 0f;
                        store.SetEnemyActionEnum(enemyId, EnemyActionType.Emerge);

                        // Reset cooldown to ref value (starts counting down next frame)
                        float cdRef = store.EnemyBurrowCooldownRef[enemyId];
                        store.EnemyBurrowCooldown[enemyId] = cdRef;

                        // 收集出土 AoE 事件（出土伤害非0时）
                        float emergeDmg = store.EnemyBurrowEmergeDamage[enemyId];
                        if (emergeDmg > 0f)
                        {
                            float radius = store.EnemyBurrowRadius[enemyId];
                            _emergeAoeQueue.Add((enemyId, emergeDmg, radius, playerId));
                        }
                    }
                    else
                    {
                        // 仍在钻地：保持 Burrow 动作（地下移动在下面处理）
                        store.SetEnemyActionEnum(enemyId, EnemyActionType.Burrow);
                    }
                }
                else
                {
                    // === 非钻地状态：检查是否需要触发钻地 ===
                    // 冷却递减（冷却 > 0 时不能钻地）
                    if (cooldown > 0f)
                    {
                        store.EnemyBurrowCooldown[enemyId] = cooldown - 1f;
                    }
                    else if (cooldown == 0f)
                    {
                        // cooldown == 0 且 CanBurrow == true → 可以钻地
                        // 出土后 cooldown 重置为某个大正数，此处已清零表示可以再次钻地
                        // 实际钻地触发由行为树/AI 系统决定，此处只处理冷却
                        // 什么都不做，等待 AI 设置动作
                    }
                }
            });
        }

        /// <summary>
        /// 串行应用出土 AoE 伤害 — 在 Update 之后由主循环调用。
        /// </summary>
        public void ApplyBurrowEffects()
        {
            foreach (var ev in _emergeAoeQueue)
            {
                int enemyId = ev.enemyId;
                float dmg = ev.dmg;
                float radius = ev.radius;
                int pid = ev.playerId;

                // 获取出土敌人位置作为 AoE 中心
                float cx = store.PositionX[enemyId];
                float cy = store.PositionY[enemyId];

                // 对范围内所有敌人造成 AoE 伤害（包括友军！这是设计意图）
                var activeEnemies = store.GetCachedActiveEnemyIds();
                for (int i = 0; i < activeEnemies.Count; i++)
                {
                    int targetId = activeEnemies[i];
                    if (!store.EnemyActive[targetId])
                        continue;
                    if (targetId == enemyId)
                        continue; // 不对自己造成伤害

                    float tx = store.PositionX[targetId];
                    float ty = store.PositionY[targetId];
                    float dx = tx - cx;
                    float dy = ty - cy;
                    if (dx * dx + dy * dy <= radius * radius)
                    {
                        // 对目标造成伤害（护甲计算）
                        float targetArmor = store.EnemyArmor[targetId];
                        float finalDmg = Math.Max(0f, dmg - targetArmor * 0.5f);
                        var source = store.GetEntityHandle(enemyId);
                        var target = store.GetEntityHandle(targetId);
                        if (source.IsValid)
                            store.DamageResolver.TryApply(new Core.GAS.DamageRequest(source, target, finalDmg, DamageType.True,
                                ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve,
                                store.AllocateGameplaySequence(targetId), ownerPlayerId: playerId));
                    }
                }
            }
            _emergeAoeQueue.Clear();
        }

        /// <summary>
        /// 触发敌人钻地（由 AI 系统调用）。
        /// 设置钻地计时器和出土冷却。
        /// </summary>
        public void TriggerBurrow(int enemyId, float duration, float emergeDamage, float emergeRadius, float cooldown)
        {
            if (!store.EnemyActive[enemyId])
                return;

            store.EnemyIsBurrowed[enemyId] = true;
            store.EnemyBurrowTimer[enemyId] = duration;
            store.EnemyBurrowEmergeDamage[enemyId] = emergeDamage;
            store.EnemyBurrowRadius[enemyId] = emergeRadius;
            store.EnemyBurrowCooldown[enemyId] = cooldown; // 正数表示冷却中，0 表示可再次钻地
            store.SetEnemyActionEnum(enemyId, EnemyActionType.Burrow);
        }
    }
}
