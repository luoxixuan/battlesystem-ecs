using System;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Mechanisms.Combat;

/// <summary>
/// Round 132 Direction 8 — Execute Immunity / Min-Health Floor tests.
/// 覆盖 ClampDamageToHealthFloor / ApplyMinHealthFloorInPlace 的真实存储层行为、
/// MarkSystem 的 ExecuteImmune 门、AddEnemy/DestroyEntity 重置，以及端到端 ApplyEnemyDamage。
/// 同构的“不触发钳制/触发钳制”分支合并为理论驱动。
/// </summary>
public class ExecuteImmunityTests : BattleTestBase
{
    private const int PlayerId = 0;

    private int MakeEnemy(float curHp, float maxHp, float floor = 0f, bool immune = false)
    {
        int enemyId = Store.AddEnemy(0, 0, 1f, curHp, maxHp, 5f, 10, 1, "TestEnemy");
        Store.EnemyMinHealthFloor[enemyId] = floor;
        Store.EnemyExecuteImmune[enemyId] = immune;
        return enemyId;
    }

    // ── ClampDamageToHealthFloor：不触发钳制的同构分支 ──────────────────

    [Theory]
    [InlineData(500f, 1000f, 0f, 200f, 200f)]   // 无 floor → 输入原样返回
    [InlineData(500f, 1000f, 0.05f, 0f, 0f)]    // 零伤害 → 0
    [InlineData(200f, 1000f, 0.05f, 30f, 30f)]  // 未触及 floor → 输入原样返回
    [InlineData(500f, 1000f, 0.05f, -10f, -10f)] // 负伤害 → 输入原样返回
    public void ClampDamageToHealthFloor_PassthroughCases_ReturnInput(
        float curHp, float maxHp, float floor, float inputDamage, float expected)
    {
        int eid = MakeEnemy(curHp, maxHp, floor);
        float damage = Store.ClampDamageToHealthFloor(eid, inputDamage);
        Assert.Equal(expected, damage);
        Assert.Equal(curHp, Store.EnemyHealth[eid]); // 该 API 只计算不写入
    }

    // ── ClampDamageToHealthFloor：触发钳制的同构分支 ────────────────────

    [Theory]
    [InlineData(60f, 1000f, 0.05f, 500f, 10f)]  // 60 - 50 = 10，过量伤害被钳到 floor 上方
    [InlineData(50f, 1000f, 0.05f, 100f, 0f)]   // 已在 floor → 0
    [InlineData(49f, 1000f, 0.05f, 100f, 0f)]   // 已在 floor 之下 → 0
    [InlineData(1000f, 1000f, 1.0f, 500f, 0f)]  // floor=100% → 完全禁止扣血
    public void ClampDamageToHealthFloor_ClampedCases_ReturnClampedValue(
        float curHp, float maxHp, float floor, float inputDamage, float expected)
    {
        int eid = MakeEnemy(curHp, maxHp, floor);
        float damage = Store.ClampDamageToHealthFloor(eid, inputDamage);
        Assert.Equal(expected, damage);
        Assert.Equal(curHp, Store.EnemyHealth[eid]); // 该 API 只计算不写入
    }

    // ── ApplyMinHealthFloorInPlace：写回路径 ────────────────────────────

    [Fact]
    public void ApplyMinHealthFloorInPlace_NoFloor_LeavesNegativeHpAlone()
    {
        int eid = MakeEnemy(curHp: -10f, maxHp: 1000f, floor: 0f);
        Store.ApplyMinHealthFloorInPlace(eid);
        Assert.Equal(-10f, Store.EnemyHealth[eid]);
    }

    [Theory]
    [InlineData(50f, 1000f, 0.05f, 10f, 50f)]   // 被直接 -= 推到 floor 之下 → 抬回 50
    [InlineData(500f, 1000f, 0.05f, 500f, 500f)] // 在 floor 之上 → 不动
    public void ApplyMinHealthFloorInPlace_FlooredCases_WriteBackCorrectly(
        float initialHp, float maxHp, float floor, float mutatedHp, float expectedHp)
    {
        int eid = MakeEnemy(initialHp, maxHp, floor);
        Store.EnemyHealth[eid] = mutatedHp; // 模拟绕过 ApplyEnemyDamage 的直接 -= 路径
        Store.ApplyMinHealthFloorInPlace(eid);
        Assert.Equal(expectedHp, Store.EnemyHealth[eid]);
    }

    [Fact]
    public void ApplyMinHealthFloorInPlace_ZeroMaxHealth_IsNoOp()
    {
        // MaxHealth=0 无法计算 floor，必须 no-op（避免 NaN / 除零）。

        int eid = Store.AddEnemy(0, 0, 1f, 0f, 0f, 5f, 10, 1, "Ghost");
        Store.EnemyMinHealthFloor[eid] = 0.05f;
        Store.EnemyHealth[eid] = -50f;
        Store.ApplyMinHealthFloorInPlace(eid);
        Assert.Equal(-50f, Store.EnemyHealth[eid]);
    }

    // ── ApplyEnemyDamage：端到端 floor 语义 ─────────────────────────────

    [Fact]
    public void ApplyEnemyDamage_RespectsFloor()
    {
        // 60 HP、5% floor=50：1000 点过量伤害只能打到 50。
        int eid = MakeEnemy(curHp: 60f, maxHp: 1000f, floor: 0.05f);
        Store.ApplyEnemyDamage(eid, 1000f);
        Assert.Equal(50f, Store.EnemyHealth[eid]);
    }

    [Fact]
    public void ApplyEnemyDamage_BelowFloor_FullyAbsorbed()
    {
        // 已在 floor 之下的敌人不会再被扣血（无“负血复活”）。
        int eid = MakeEnemy(curHp: 0f, maxHp: 1000f, floor: 0.05f);
        Store.ApplyEnemyDamage(eid, 1f);
        Assert.Equal(0f, Store.EnemyHealth[eid]);
    }

    // ── MarkSystem：ExecuteImmune 门（免疫/非免疫同构合并） ─────────────

    [Theory]
    [InlineData(true, 0)]  // 免疫 → AddMark 全部被吞
    [InlineData(false, 3)] // 非免疫 → 正常叠加
    public void MarkSystem_AddMark_RespectsExecuteImmune(bool immune, int expectedStacks)
    {
        int eid = MakeEnemy(curHp: 1000f, maxHp: 1000f, floor: 0.05f, immune: immune);
        Store.EnemyMarkMaxThreshold[eid] = 5;
        var mark = new MarkSystem(Store, playerId: PlayerId);
        mark.AddMark(eid, 3);
        Assert.Equal(expectedStacks, Store.EnemyMarkStacks[eid]);
    }

    // ── 槽位生命周期重置 ──────────────────────────────────────────────

    [Fact]
    public void AddEnemy_ResetsExecuteImmunityFieldsToDefaults()
    {
        // 新生成敌人：floor=0、immune=false（opt-out 默认）。

        int eid = Store.AddEnemy(0, 0, 1f, 500f, 1000f, 5f, 10, 1, "Peon");
        Assert.Equal(0f, Store.EnemyMinHealthFloor[eid]);
        Assert.False(Store.EnemyExecuteImmune[eid]);
    }

    [Fact]
    public void DestroyEntity_ResetsExecuteImmunityFields()
    {
        // ID 复用时 Boss 的 floor/immune 不得泄漏给后续小怪。

        int eid = Store.AddEnemy(0, 0, 1f, 500f, 1000f, 5f, 10, 1, "Boss");
        Store.EnemyMinHealthFloor[eid] = 0.05f;
        Store.EnemyExecuteImmune[eid] = true;
        Store.DestroyEntity(eid);
        Assert.Equal(0f, Store.EnemyMinHealthFloor[eid]);
        Assert.False(Store.EnemyExecuteImmune[eid]);
    }
}
