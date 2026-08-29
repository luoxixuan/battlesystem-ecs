using BattleSystemECS.Core.GAS;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Mechanisms.Combat;

/// <summary>
/// GAS 槽计数在实体销毁时的归零语义（DestroyEntity 共享段）。
///
/// 回归重点：实体 id 走 freeEntityIds 回收，而 AbilityCount / ActiveEffectCount
/// 此前只在 ResetPlayerAbilities 里清零 —— DestroyEntity / AddEnemy / CreateEntity
/// 都不碰。于是回收 id 的新敌人会继承前一个占用者的 DoT / 冻结 / 眩晕
/// （连 SourceEntityId 都指向旧攻击者），因为 BuffSystem 按 GetEffectCount 驱动 tick。
/// 归零 count 即足够：槽内容从不越过 count 读取。
/// </summary>
public class GasSlotRecycleTests : BattleTestBase
{
    private const int PlayerId = 0;

    private static AppliedEffect MakeDot(float dmgPerTick, float duration, int sourceId)
    {
        var def = GameplayEffectDef.Periodic("TestDot", AttributeSetDefinitions.ENEMY_HEALTH,
            dmgPerTick, duration, tickInterval: 1f);
        return new AppliedEffect(def, sourceId);
    }

    // ── 销毁即归零 ──────────────────────────────────────────────────────

    [Fact]
    public void DestroyEntity_ZeroesEffectAndAbilityCounts()
    {
        int eid = Enemy(e => { e.Health = 100f; e.MaxHealth = 100f; });
        Store.AddEffect(eid, MakeDot(5f, 3f, sourceId: 42));
        Store.AddAbility(eid, new GameplayAbilityDef("Probe", "", 1f, 0f, -1, 10f,
            AbilityActivation.Instant, AreaShapeType.Single, 1));

        Assert.Equal(1, Store.GetEffectCount(eid));
        Assert.Equal(1, Store.AbilityCount[eid]);

        Store.DestroyEntity(eid);

        Assert.Equal(0, Store.GetEffectCount(eid));
        Assert.Equal(0, Store.AbilityCount[eid]);
    }

    // ── id 回收后不得继承旧 DoT ─────────────────────────────────────────

    [Fact]
    public void RecycledEnemyId_DoesNotInheritPreviousDot()
    {
        int first = Enemy(e => { e.Health = 100f; e.MaxHealth = 100f; });
        Store.AddEffect(first, MakeDot(5f, 3f, sourceId: 42));
        Assert.Equal(1, Store.GetEffectCount(first));

        Store.DestroyEntity(first);

        // 同一个 id 被 free-list 回收给新敌人
        int second = Enemy(e => { e.Health = 200f; e.MaxHealth = 200f; });
        Assert.Equal(first, second);
        Assert.Equal(0, Store.GetEffectCount(second));
    }

    // ── 回收 id 的新敌人不得被上一任的 DoT 扣血 ────────────────────────

    [Fact]
    public void RecycledEnemyId_TakesNoInheritedDotDamage()
    {
        int first = Enemy(e => { e.Health = 100f; e.MaxHealth = 100f; });
        Store.AddEffect(first, MakeDot(5f, 3f, sourceId: 42));
        Store.DestroyEntity(first);

        int second = Enemy(e => { e.Health = 200f; e.MaxHealth = 200f; });
        Assert.Equal(first, second);

        var buffs = new BattleSystemECS.Systems.BuffSystem(Store, PlayerId, Renderer);
        buffs.Update(1f);          // tick effects → 若继承了 DoT 会入队伤害
        buffs.ResolveDotDamage();  // 结算队列 → 若入了队会扣血

        Assert.Equal(200f, Store.EnemyHealth[second]);
    }
}
