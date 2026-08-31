using BattleSystemECS.Config;
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

}
