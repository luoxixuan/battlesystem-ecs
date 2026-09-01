using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using System;
using System.Collections.Generic;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Features.Skills;

/// <summary>
/// GlobalSkillSystem.ExecuteMeteorStrike —— 真实施放路径（TryActivateGlobalSkill）驱动。
///
/// 回归重点：陨石把敌人打到 0 血后必须 QueueEnemyDeath。此前它只夹血到 0 并
/// 自增本地 killed 计数，敌人却仍 EnemyActive —— 不给金币、不释放实体槽，且被
/// 下游所有 `EnemyHealth <= 0f` 守卫当死人跳过（僵尸实体）。
/// 全局没有"扫描 HP<=0"的兜底 sweeper，所以入队是唯一的死亡路径。
/// </summary>
public class GlobalSkillMeteorTests : BattleTestBase
{
    private const int PlayerId = 0;

    /// <summary>装一个 MeteorStrike 全局技能（0 费 0 冷却），返回驱动就绪的系统。</summary>
    private GlobalSkillSystem MakeSystem(float damagePct = 100f, float maxDamage = 10000f)
    {
        Config.GlobalSkills.Add(new GlobalSkillDef
        {
            Name = "Meteor Strike",
            SkillType = (int)GlobalSkillType.MeteorStrike,
            ManaCost = 0f,
            Cooldown = 0f,
            DamagePct = damagePct,
            MaxDamage = maxDamage
        });

        var sys = new GlobalSkillSystem(Store, Config, Renderer, PlayerId);
        sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
        sys.SetTurn(1); // 解锁技能槽（PlayerGlobalSkillUnlocked）
        return sys;
    }

    // ── 致死命中：必须入队 + Resolve 后真正死亡 ──────────────────────────

    [Fact]
    public void MeteorStrike_LethalHit_QueuesDeathAndResolves()
    {
        Player(p => { p.Health = 1000f; p.Gold = 0f; });
        var sys = MakeSystem();
        int eid = Enemy(e => { e.Health = 10f; e.MaxHealth = 10f; e.GoldReward = 25; });

        Assert.True(sys.TryActivateGlobalSkill(0));

        // 施放后：血已清零，但实体仍活着（死亡在帧末串行结算）
        Assert.Equal(0f, Store.EnemyHealth[eid]);
        Assert.True(Store.EnemyActive[eid]);

        Store.ResolveEnemiesKilledThisFrame();

        // 死亡结算真正跑过：实体失活 + 金币入账 + 击杀计数
        Assert.False(Store.EnemyActive[eid]);
        Assert.Equal(1, Store.TotalKills);
        Assert.True(Store.PlayerGold[PlayerId] >= 25f);
    }

    // ── 非致死命中：只掉血，不得入队 ──────────────────────────────────

    [Fact]
    public void MeteorStrike_NonLethalHit_DoesNotQueueDeath()
    {
        Player(p => { p.Health = 100f; p.Gold = 0f; });
        var sys = MakeSystem(damagePct: 10f, maxDamage: 10f); // 10 点伤害

        int eid = Enemy(e => { e.Health = 500f; e.MaxHealth = 500f; e.GoldReward = 25; });

        Assert.True(sys.TryActivateGlobalSkill(0));
        Store.ResolveEnemiesKilledThisFrame();

        Assert.True(Store.EnemyActive[eid]);
        Assert.Equal(490f, Store.EnemyHealth[eid]);
        Assert.Equal(0, Store.TotalKills);
        Assert.Equal(0f, Store.PlayerGold[PlayerId]);
    }

    // ── 实体槽必须被回收（僵尸实体会永久占位）────────────────────────

    [Fact]
    public void MeteorStrike_KilledEnemy_ReleasesEntitySlot()
    {
        Player(p => { p.Health = 1000f; });
        var sys = MakeSystem();
        int eid = Enemy(e => { e.Health = 10f; e.MaxHealth = 10f; });

        Assert.True(sys.TryActivateGlobalSkill(0));
        Store.ResolveEnemiesKilledThisFrame();

        // free-list 回收后，下一个 CreateEntity 必须能拿回同一个 id
        Assert.Equal(eid, Store.CreateEntity());
    }

    [Fact]
    public void StrictCatalogMeteorUsesGroundTargetSetInsteadOfPlayerEntity()
    {
        var config = new GameConfig { StrictCatalogReferences = true };
        config.GlobalSkills.Add(new GlobalSkillDef
        {
            Name = "typed-meteor", SkillType = (int)GlobalSkillType.MeteorStrike,
            ManaCost = 0f, Cooldown = 3f
        });
        var targeting = new TargetingDefinition(new TargetingId(0), TargetingShape.GroundTarget,
            3, 3, 3, 0, radius: 3f, relation: RelationFilter.Enemies,
            maxTargetsMode: MaxTargetsPolicy.Unlimited);
        var execution = new ExecutionDefinition(new ExecutionId(0), EffectPayloadKind.Damage, 5f,
            CatalogRegistries.SkillTag, operation: ExecutionOperation.ApplyDamage);
        var ability = new AbilityDefinition(new AbilityId(0), "typed-meteor", targeting, ClockId.Combat, 3f,
            GameplayPhaseMask.Wave, Array.Empty<EffectId>(), Array.Empty<ModifierDefinition>(),
            CatalogRegistries.SkillExecutor, CatalogRegistries.SkillConsumer, executions: new[] { execution.Id });
        config.CompiledCatalog = new GameplayCatalog(new[] { ability }, new[] { targeting },
            Array.Empty<GameplayEffectDefinition>(), new[] { execution }, Array.Empty<TriggerDefinition>(),
            Array.Empty<ModifierDefinition>(), new Dictionary<string, AbilityId> { [ability.Name] = ability.Id });
        Player(p => { p.X = 0f; p.Y = 0f; p.Health = 100f; });
        int inside = Enemy(e => { e.X = 1f; e.Y = 0f; e.Health = 1000f; e.MaxHealth = 1000f; });
        int outside = Enemy(e => { e.X = 50f; e.Y = 0f; e.Health = 1000f; e.MaxHealth = 1000f; });
        var system = new GlobalSkillSystem(Store, config, Renderer, PlayerId);
        system.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
        system.SetTurn(1);

        Assert.True(system.TryActivateGlobalSkill(0));
        Assert.Equal(995f, Store.EnemyHealth[inside]);
        Assert.Equal(1000f, Store.EnemyHealth[outside]);
    }

}
