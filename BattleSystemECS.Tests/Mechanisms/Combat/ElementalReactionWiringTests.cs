using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Mechanisms.Combat;

/// <summary>
/// ElementalReactionSystem 的接线回归。
///
/// 背景：这个系统存在了两轮功能交付，却**从未被构造**（全库 `new ElementalReactionSystem(` 0 次、
/// registry 无字段、11 个 group 无属性、连测试都没有）。它独占三项职责，因此三项全是死的：
///   1. 元素计时器衰减（`EnemyElementStatus` / `EnemyElementTimer` 永不清除）；
///   2. `PendingShieldBreaks` 消费（队列整个会话只增不减）；
///   3. `EnemyExposureMask` / `EnemyExposureTimer` 写入 → `TowerAttackSystem:1838` 与
///      `PlayerTowerAttackSystem:596` 读的 +30% 元素易伤永不触发。
///
/// 本文件的关键点是**最后一个用例**：它走 `FrameScheduler` 的真实 WavePhase tick，
/// 证明"接线"本身生效，而不只是证明系统内部逻辑正确。只测系统逻辑会重演本项目
/// 已被咬过一次的假绿（`DeathMarkSystem.GetDamageMultiplier` 长期绿着但生产零调用）。
/// </summary>
public class ElementalReactionWiringTests : BattleTestBase
{
    private const int PlayerId = 0;

    /// <summary>装配真实 registry（与 GameManager 启动路径同源）。</summary>
    private (SystemRegistry registry, FrameScheduler scheduler) Assemble()
    {
        GameConfig config = GameConfigLoader.LoadConfig(Renderer);
        var stateMachine = new StateMachine();
        int playerId = Player();

        var registry = new SystemRegistry();
        registry.CreateAll(Store, config, Renderer, playerId, stateMachine);
        registry.WireDependencies(Store, playerId);
        var scheduler = new FrameScheduler(Store, config);
        registry.AssignToGroups(scheduler);
        return (registry, scheduler);
    }

    // ── 接线本身 ────────────────────────────────────────────────────────

    [Fact]
    public void Registry_ConstructsElementalReactionSystem()
    {
        var (registry, _) = Assemble();
        Assert.NotNull(registry.ElementalReaction);
    }

    [Fact]
    public void Registry_AssignsElementalReactionToSkillBuffGroup()
    {
        var (_, scheduler) = Assemble();
        Assert.NotNull(scheduler.SkillBuff.ElementalReaction);
    }

    // ── 端到端：接线生效的最强证据 ──────────────────────────────────────

    /// <summary>
    /// 走真实 WavePhase tick：元素计时器必须被衰减。接线断裂时元素位永不清除，
    /// 此用例即变红。这是本文件唯一能捕获"系统没接上"的用例。
    /// </summary>
    [Fact]
    public void WavePhaseTick_DecaysElementTimers_EndToEnd()
    {
        var (_, scheduler) = Assemble();
        scheduler.Phase = GameState.WavePhase;

        int eid = Store.AddEnemy(5f, 10f, 1f, 500f, 500f, 5f, 10, 1, "Elemental");
        Store.EnemyElementStatus[eid] = ElementType.Fire;
        Store.EnemyElementTimer[eid * 4] = 2f; // FIRE_IDX = 0

        scheduler.TickGameTurn(1f, 1);

        // 1s tick 后剩 ~1s：证明衰减真的跑了（未接线时仍是 2f）
        Assert.True(Store.EnemyElementTimer[eid * 4] < 2f,
            $"元素计时器未被衰减（仍为 {Store.EnemyElementTimer[eid * 4]}）—— ElementalReactionSystem 未接线？");
        Assert.Equal(ElementType.Fire, Store.EnemyElementStatus[eid]); // 未到期，位仍在
    }

    /// <summary>计时器归零后元素位必须被清除（真实 tick 驱动）。</summary>
    [Fact]
    public void WavePhaseTick_ClearsExpiredElementBit_EndToEnd()
    {
        var (_, scheduler) = Assemble();
        scheduler.Phase = GameState.WavePhase;

        int eid = Store.AddEnemy(5f, 10f, 1f, 500f, 500f, 5f, 10, 1, "Elemental");
        Store.EnemyElementStatus[eid] = ElementType.Fire;
        Store.EnemyElementTimer[eid * 4] = 0.5f; // 短于一帧

        scheduler.TickGameTurn(1f, 1);

        Assert.Equal(ElementType.None, Store.EnemyElementStatus[eid]);
        Assert.Equal(0f, Store.EnemyElementTimer[eid * 4]);
    }

    // ── 单系统行为（接线后这些才可达）────────────────────────────────

    /// <summary>破盾队列必须被系统消费（此前唯一消费者从未运行）。</summary>
    [Fact]
    public void Update_DrainsPendingShieldBreaks()
    {
        var sys = new ElementalReactionSystem(Store, PlayerId, Renderer);

        int eid = Store.AddEnemy(0f, 0f, 1f, 500f, 500f, 5f, 10, 1, "Shielded", 0f, shield: 20f);
        Store.EnemyShieldType[eid] = ElementType.Fire;
        Store.EnemyShieldBreakReaction[eid] = ElementType.Ice;
        Store.EnemyShieldBreakElementDuration[eid] = 2f;

        Store.ApplyEnemyDamage(eid, 50f, ElementType.Fire); // 破盾 → 入队
        Assert.Single(Store.PendingShieldBreaks);

        sys.Update(0.016f);

        Assert.Empty(Store.PendingShieldBreaks);
    }

    /// <summary>元素在身时曝光窗口必须被点亮 —— 这是 +30% 易伤的前置条件。</summary>
    [Fact]
    public void Update_ArmsExposureWindow_WhenElementPresent()
    {
        var sys = new ElementalReactionSystem(Store, PlayerId, Renderer);

        int eid = Store.AddEnemy(0f, 0f, 1f, 500f, 500f, 5f, 10, 1, "Elemental");
        Store.EnemyElementStatus[eid] = ElementType.Fire;
        Store.EnemyElementTimer[eid * 4] = 5f;

        Assert.Equal(ElementType.None, Store.EnemyExposureMask[eid]); // 接线前恒为 None
        Assert.Equal(0f, Store.EnemyExposureTimer[eid]);

        sys.Update(0.016f);

        Assert.Equal(ElementType.Fire, Store.EnemyExposureMask[eid]);
        Assert.True(Store.EnemyExposureTimer[eid] > 0f);
    }

    /// <summary>
    /// 曝光窗口点亮后，异元素/无元素攻击吃 +30%，同元素攻击不吃。
    /// 这是接线带来的**实际战斗数值变化**，不是 no-op。
    /// </summary>
    [Fact]
    public void ExposureMultiplier_BoostsOffElementHitsOnly()
    {
        var sys = new ElementalReactionSystem(Store, PlayerId, Renderer);

        int eid = Store.AddEnemy(0f, 0f, 1f, 500f, 500f, 5f, 10, 1, "Elemental");
        Store.EnemyElementStatus[eid] = ElementType.Fire;
        Store.EnemyElementTimer[eid * 4] = 5f;
        sys.Update(0.016f); // 点亮曝光窗口（mask = Fire）

        // 同元素（Fire）命中：无加成
        Assert.Equal(1.0f, sys.GetExposureDamageMultiplier(eid, ElementType.Fire), 3);
        // 异元素（Ice）命中：+30%
        Assert.Equal(1.30f, sys.GetExposureDamageMultiplier(eid, ElementType.Ice), 3);
        // 无元素（物理）命中：+30%
        Assert.Equal(1.30f, sys.GetExposureDamageMultiplier(eid, ElementType.None), 3);
    }

    /// <summary>无元素、无曝光的敌人必须拿到中性乘数（零开销快路径）。</summary>
    [Fact]
    public void ExposureMultiplier_IsNeutral_WithoutExposure()
    {
        var sys = new ElementalReactionSystem(Store, PlayerId, Renderer);
        int eid = Store.AddEnemy(0f, 0f, 1f, 500f, 500f, 5f, 10, 1, "Plain");

        Assert.Equal(1.0f, sys.GetExposureDamageMultiplier(eid, ElementType.None), 3);
        Assert.Equal(1.0f, sys.GetExposureDamageMultiplier(eid, ElementType.Fire), 3);
    }
}
