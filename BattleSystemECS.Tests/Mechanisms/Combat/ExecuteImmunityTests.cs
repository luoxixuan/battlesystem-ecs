using System;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using Xunit;

namespace BattleSystemECS.Tests.Mechanisms.Combat;

/// <summary>
/// Round 132 Direction 8 — Execute Immunity / Min-Health Floor tests.
/// Verifies the floor-clamp helper, the in-place post-= helper, the MarkSystem.ExecuteImmune
/// guard, the AddEnemy/DestroyEntity reset, and the end-to-end ApplyEnemyDamage path.
/// </summary>
public class ExecuteImmunityTests
{
    private const int PlayerId = 0;

    private static (ComponentStore store, int enemyId) MakeEnemy(float curHp, float maxHp, float floor = 0f, bool immune = false)
    {
        var store = new ComponentStore();
        int enemyId = store.AddEnemy(0, 0, 1f, curHp, maxHp, 5f, 10, 1, "TestEnemy");
        store.EnemyMinHealthFloor[enemyId] = floor;
        store.EnemyExecuteImmune[enemyId] = immune;
        return (store, enemyId);
    }

    [Fact]
    public void ClampDamageToHealthFloor_NoFloor_ReturnsInputUnchanged()
    {
        var (store, eid) = MakeEnemy(curHp: 500f, maxHp: 1000f, floor: 0f);
        float dmg = store.ClampDamageToHealthFloor(eid, 200f);
        Assert.Equal(200f, dmg);
        Assert.Equal(500f, store.EnemyHealth[eid]); // unchanged
    }

    [Fact]
    public void ClampDamageToHealthFloor_ZeroDamage_ReturnsZero()
    {
        var (store, eid) = MakeEnemy(curHp: 500f, maxHp: 1000f, floor: 0.05f);
        float dmg = store.ClampDamageToHealthFloor(eid, 0f);
        Assert.Equal(0f, dmg);
    }

    [Fact]
    public void ClampDamageToHealthFloor_Overkill_ClampsToFloor()
    {
        // Boss: 1000 HP, 5% floor = 50 HP. Current 60 HP → only 10 damage allowed.
        var (store, eid) = MakeEnemy(curHp: 60f, maxHp: 1000f, floor: 0.05f);
        float dmg = store.ClampDamageToHealthFloor(eid, 500f); // would one-shot
        Assert.Equal(10f, dmg); // 60 - 50 = 10
    }

    [Fact]
    public void ClampDamageToHealthFloor_AtFloor_ReturnsZero()
    {
        // Boss at exactly the floor — no more damage allowed.
        var (store, eid) = MakeEnemy(curHp: 50f, maxHp: 1000f, floor: 0.05f);
        float dmg = store.ClampDamageToHealthFloor(eid, 100f);
        Assert.Equal(0f, dmg);
    }

    [Fact]
    public void ClampDamageToHealthFloor_UnderFloor_ReturnsZero()
    {
        // Boss slightly under floor — still no damage.
        var (store, eid) = MakeEnemy(curHp: 49f, maxHp: 1000f, floor: 0.05f);
        float dmg = store.ClampDamageToHealthFloor(eid, 100f);
        Assert.Equal(0f, dmg);
    }

    [Fact]
    public void ClampDamageToHealthFloor_UnderThreshold_ReturnsInput()
    {
        // Damage that doesn't reach the floor is unaffected.
        var (store, eid) = MakeEnemy(curHp: 200f, maxHp: 1000f, floor: 0.05f);
        float dmg = store.ClampDamageToHealthFloor(eid, 30f); // leaves 170, well above 50
        Assert.Equal(30f, dmg);
    }

    [Fact]
    public void ClampDamageToHealthFloor_FullFloor_ReturnsZero()
    {
        // floor=1.0 (100%) → enemy is functionally immortal to health damage.
        var (store, eid) = MakeEnemy(curHp: 1000f, maxHp: 1000f, floor: 1.0f);
        float dmg = store.ClampDamageToHealthFloor(eid, 500f);
        Assert.Equal(0f, dmg);
    }

    [Fact]
    public void ApplyMinHealthFloorInPlace_NoFloor_IsNoOp()
    {
        var (store, eid) = MakeEnemy(curHp: -10f, maxHp: 1000f, floor: 0f);
        store.ApplyMinHealthFloorInPlace(eid);
        Assert.Equal(-10f, store.EnemyHealth[eid]); // no floor, no clamp
    }

    [Fact]
    public void ApplyMinHealthFloorInPlace_BelowFloor_LiftsToFloor()
    {
        // Simulate a direct -= that pushed HP below the floor.
        var (store, eid) = MakeEnemy(curHp: 50f, maxHp: 1000f, floor: 0.05f);
        store.EnemyHealth[eid] = 10f; // direct mutation below floor
        store.ApplyMinHealthFloorInPlace(eid);
        Assert.Equal(50f, store.EnemyHealth[eid]);
    }

    [Fact]
    public void ApplyMinHealthFloorInPlace_AboveFloor_DoesNotClamp()
    {
        var (store, eid) = MakeEnemy(curHp: 500f, maxHp: 1000f, floor: 0.05f);
        store.ApplyMinHealthFloorInPlace(eid);
        Assert.Equal(500f, store.EnemyHealth[eid]);
    }

    [Fact]
    public void ApplyEnemyDamage_RespectsFloor()
    {
        // End-to-end: massive damage via canonical entry point clamps to floor.
        var (store, eid) = MakeEnemy(curHp: 60f, maxHp: 1000f, floor: 0.05f);
        store.ApplyEnemyDamage(eid, 1000f);
        Assert.Equal(50f, store.EnemyHealth[eid]); // clamped to floor
    }

    [Fact]
    public void MarkSystem_AddMark_RespectsExecuteImmune()
    {
        var (store, eid) = MakeEnemy(curHp: 1000f, maxHp: 1000f, floor: 0.05f, immune: true);
        store.EnemyMarkMaxThreshold[eid] = 5;
        var mark = new MarkSystem(store, playerId: PlayerId);
        for (int i = 0; i < 10; i++) mark.AddMark(eid, 1);
        Assert.Equal(0, store.EnemyMarkStacks[eid]); // immune → no stacks
    }

    [Fact]
    public void MarkSystem_AddMark_NotImmune_BeatsAsBefore()
    {
        // Regression: when EnemyExecuteImmune=false (default), AddMark works as before.
        var (store, eid) = MakeEnemy(curHp: 1000f, maxHp: 1000f, floor: 0.05f, immune: false);
        store.EnemyMarkMaxThreshold[eid] = 5;
        var mark = new MarkSystem(store, playerId: PlayerId);
        mark.AddMark(eid, 3);
        Assert.Equal(3, store.EnemyMarkStacks[eid]);
    }

    [Fact]
    public void AddEnemy_ResetsExecuteImmunityFieldsToDefaults()
    {
        // Opt-out defaults: floor=0, immune=false. Verified via a freshly-added enemy.
        var store = new ComponentStore();
        int eid = store.AddEnemy(0, 0, 1f, 500f, 1000f, 5f, 10, 1, "Peon");
        Assert.Equal(0f, store.EnemyMinHealthFloor[eid]);
        Assert.False(store.EnemyExecuteImmune[eid]);
    }

    [Fact]
    public void DestroyEntity_ResetsExecuteImmunityFields()
    {
        // After destroy, the slot must be reset so ID-reuse doesn't leak Boss flags to a peon.
        var store = new ComponentStore();
        int eid = store.AddEnemy(0, 0, 1f, 500f, 1000f, 5f, 10, 1, "Boss");
        store.EnemyMinHealthFloor[eid] = 0.05f;
        store.EnemyExecuteImmune[eid] = true;
        store.DestroyEntity(eid);
        Assert.Equal(0f, store.EnemyMinHealthFloor[eid]);
        Assert.False(store.EnemyExecuteImmune[eid]);
    }

    [Fact]
    public void ApplyMinHealthFloorInPlace_ZeroMaxHealth_IsNoOp()
    {
        // Defensive: MaxHealth=0 → can't compute floor → no-op (avoids NaN/divide-by-zero).
        var store = new ComponentStore();
        int eid = store.AddEnemy(0, 0, 1f, 0f, 0f, 5f, 10, 1, "Ghost");
        store.EnemyMinHealthFloor[eid] = 0.05f;
        store.EnemyHealth[eid] = -50f;
        store.ApplyMinHealthFloorInPlace(eid);
        Assert.Equal(-50f, store.EnemyHealth[eid]);
    }

    [Fact]
    public void ClampDamageToHealthFloor_NegativeDamage_ReturnsInput()
    {
        // Negative damage is a no-op anyway (ApplyEnemyDamage guards it upstream).
        var (store, eid) = MakeEnemy(curHp: 500f, maxHp: 1000f, floor: 0.05f);
        float dmg = store.ClampDamageToHealthFloor(eid, -10f);
        Assert.Equal(-10f, dmg);
    }

    [Fact]
    public void ApplyEnemyDamage_BelowFloor_FullyAbsorbed()
    {
        // Sanity: 1 HP damage to a 1000-HP Boss with 0-HP floor (current 0) is fully absorbed
        // (no damage applied, HP stays at 0). Equivalent to "boss already dead, no resurrection".
        var (store, eid) = MakeEnemy(curHp: 0f, maxHp: 1000f, floor: 0.05f);
        store.ApplyEnemyDamage(eid, 1f);
        Assert.Equal(0f, store.EnemyHealth[eid]);
    }
}
