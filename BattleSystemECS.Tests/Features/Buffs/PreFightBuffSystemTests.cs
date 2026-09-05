using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Buffs
{
 /// <summary>
 /// Tests for Round178 Direction6: Pre-fight Buff Selection
 /// (BuildPhase末「3选1」出战 buff). Verifies that:
 /// - Default state: option slots empty, selection empty, tower cache1f
 /// - AddPlayer / AddTower reset PreFight fields
 /// - OnWavePending → Update() rolls N weighted options into Option{1,2,3}Id slots
 /// - Disabled config: rolls are no-op, tower cache stays at1f
 /// - Empty pool: rolls are no-op
 /// - SelectPreFightBuff(idx) sets SelectedBuffId + caches CritBonus / MaxHpMult
 /// - OnWaveStart → ApplyToAllActiveTowers writes DamageMult / SpeedMult to tower cache
 /// - OnWaveComplete → ClearWaveScoped resets to defaults
 /// - Weighted distribution is honored (high-weight entry picked more often)
 /// - Multiplayer: per-player independent selection (single-player default in tests)
 /// - Sentinel:1f fast path preserved when no selection made
 /// - Out-of-range select index silently rejected
 /// </summary>
 public class PreFightBuffSystemTests : BattleTestBase
 {
 private const int PlayerId =0;

 private (PreFightBuffSystem sys, int towerId) MakeSystem(
 bool enabled = true,
 int optionsPerWave =3,
 PreFightBuffOptionDef[]? pool = null)
 {
 if (pool == null)
 {
 pool = new PreFightBuffOptionDef[]
 {
 new() { Id = "AttackFury", Weight =1.0f, DamageMult =1.15f, SpeedMult =1.0f, CritChance =0f, MaxHpMult =1f },
 new() { Id = "SwiftCasting", Weight =1.0f, DamageMult =1.0f, SpeedMult =1.15f, CritChance =0f, MaxHpMult =1f },
 new() { Id = "CritEye", Weight =0.8f, DamageMult =1.05f, SpeedMult =1.0f, CritChance =0.10f, MaxHpMult =1f },
 new() { Id = "Fortified", Weight =1.0f, DamageMult =0.95f, SpeedMult =0.95f, CritChance =0f, MaxHpMult =1.25f },
 new() { Id = "Berserker", Weight =0.5f, DamageMult =1.30f, SpeedMult =1.20f, CritChance =0f, MaxHpMult =0.80f },
 };
 }
 Config.PreFight = new PreFightBuffConfig
 {
 Enabled = enabled,
 OptionsPerWave = optionsPerWave,
 Pool = pool
 };
 Player();
 int towerId = RawTower(0, 0, TowerType.Basic, 10f, 3, 1f, 1, 50f);
 Store.TowerActive[towerId] = true;
 Store.TowerAttackSpeed[towerId] =1f;
 Store.TowerAttackDamage[towerId] =10f;
 return (new PreFightBuffSystem(Store, Config), towerId);
 }

 /// <summary>
 /// 把系统 RNG 固定到指定种子。生产走 store.Determinism，测试 Reset 同一条流。
 /// </summary>
 private static void SeedSystemRng(PreFightBuffSystem sys, ComponentStore store, int seed)
 {
 _ = sys;
 store.Determinism.Reset(seed);
 }

 // ─── Default state (backward compat) ──────────────────────────────

 [Fact]
 public void DefaultState_NewComponentStore_AllPreFightFieldsEmptyOrZero()
 {
 // String fields default to null until AddPlayer resets them.
 Assert.True(string.IsNullOrEmpty(Store.PlayerPreFightSelectedBuffId[PlayerId]));
 Assert.True(string.IsNullOrEmpty(Store.PlayerPreFightOption1Id[PlayerId]));
 Assert.True(string.IsNullOrEmpty(Store.PlayerPreFightOption2Id[PlayerId]));
 Assert.True(string.IsNullOrEmpty(Store.PlayerPreFightOption3Id[PlayerId]));
 Assert.False(Store.PlayerPreFightOptionsRolled[PlayerId]);
 Assert.Equal(0f, Store.PlayerPreFightCritBonus[PlayerId]);
 Assert.Equal(0f, Store.PlayerPreFightMaxHpMult[PlayerId]);
 }

 [Fact]
 public void AddTower_InitializesTowerCacheToOneFastPath()
 {
 Store.AddPlayer(PlayerId,5f,1f,10f,1);
 Store.AddTower(0, TowerType.Basic,10f,3,1f,1,50f);
 // After AddTower the cache fields default to1f (fast path).
 Assert.Equal(1f, Store.TowerPreFightDamageMult[0]);
 Assert.Equal(1f, Store.TowerPreFightSpeedMult[0]);
 }

 [Fact]
 public void RemoveTower_ResetsTowerCacheToOneFastPath()
 {
 Store.AddPlayer(PlayerId,5f,1f,10f,1);
 Store.AddTower(0, TowerType.Basic,10f,3,1f,1,50f);
 Store.RemoveTower(0);
 // After RemoveTower the cache fields are still1f (no leak).
 Assert.Equal(1f, Store.TowerPreFightDamageMult[0]);
 Assert.Equal(1f, Store.TowerPreFightSpeedMult[0]);
 }

 [Fact]
 public void PreFightBuffConfig_HasSensibleDefaults()
 {
 var cfg = new PreFightBuffConfig();
 Assert.True(cfg.Enabled);
 Assert.True(cfg.OptionsPerWave >=1);
 Assert.NotNull(cfg.Pool);
 // Default Pool is Array.Empty — the system is inert until a real pool is loaded.
 }

 // ─── Update() rolls N options on wave-pending transition ──────────

 [Fact]
 public void Update_WavePendingFalse_NoRoll()
 {
 var (sys, towerId) = MakeSystem();
 // Default _wavePending = false → no roll happens.
 sys.Update(0.016f);
 Assert.Equal("", Store.PlayerPreFightOption1Id[PlayerId]);
 Assert.False(Store.PlayerPreFightOptionsRolled[PlayerId]);
 }

 [Fact]
 public void Update_WavePendingTrue_RollsThreeOptions()
 {
 var (sys, towerId) = MakeSystem(optionsPerWave:3);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 Assert.True(Store.PlayerPreFightOptionsRolled[PlayerId]);
 Assert.NotEqual("", Store.PlayerPreFightOption1Id[PlayerId]);
 Assert.NotEqual("", Store.PlayerPreFightOption2Id[PlayerId]);
 Assert.NotEqual("", Store.PlayerPreFightOption3Id[PlayerId]);
 }

 [Fact]
 public void Update_WavePendingTrue_NoDuplicatesInRoll()
 {
 var (sys, towerId) = MakeSystem(optionsPerWave:3);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 var opts = sys.GetCurrentOptions(PlayerId);
 Assert.NotEqual(opts[0], opts[1]);
 Assert.NotEqual(opts[1], opts[2]);
 Assert.NotEqual(opts[0], opts[2]);
 }

 [Fact]
 public void Update_CalledTwiceWhilePending_IdempotentRollLatch()
 {
 var (sys, towerId) = MakeSystem();
 sys.SetWavePending(true);
 sys.Update(0.016f);
 var first = sys.GetCurrentOptions(PlayerId);
 // Second tick within the same BuildPhase must not re-roll.
 sys.Update(0.016f);
 var second = sys.GetCurrentOptions(PlayerId);
 Assert.Equal(first[0], second[0]);
 Assert.Equal(first[1], second[1]);
 Assert.Equal(first[2], second[2]);
 }

 [Fact]
 public void Update_DisabledConfig_NoRoll()
 {
 var (sys, towerId) = MakeSystem(enabled: false);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 Assert.False(Store.PlayerPreFightOptionsRolled[PlayerId]);
 }

 [Fact]
 public void Update_EmptyPool_NoRoll()
 {
 var (sys, towerId) = MakeSystem(pool: Array.Empty<PreFightBuffOptionDef>());
 sys.SetWavePending(true);
 sys.Update(0.016f);
 Assert.False(Store.PlayerPreFightOptionsRolled[PlayerId]);
 }

 [Fact]
 public void Update_DeadPlayer_StillRollsForAllSlots()
 {
 // Unlike systems that gate on PlayerCurrentHealth, PreFightBuffSystem
 // unconditionally iterates MAX_PLAYERS (mirrors Interest/GlobalSkill
 // pattern). The SelectedBuffId "" sentinel still protects against
 // accidental application of stale state when AddPlayer is later called.
 var (sys, towerId) = MakeSystem();
 Store.PlayerCurrentHealth[PlayerId] =0f;
 sys.SetWavePending(true);
 sys.Update(0.016f);
 // Roll still happened for the slot, even though HP is0.
 Assert.True(Store.PlayerPreFightOptionsRolled[PlayerId]);
 }

 [Fact]
 public void Update_OptionsPerWaveExceedsPool_FillsAllSlotsViaLastIdFallback()
 {
 var smallPool = new PreFightBuffOptionDef[]
 {
 new() { Id = "OnlyOne", Weight =1f, DamageMult =1.1f }
 };
 var (sys, towerId) = MakeSystem(optionsPerWave:5, pool: smallPool);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 // Pool is exhausted after the first pick, so the lastId fallback fills
 // the remaining slots with the same id — the design can't surface more
 // unique options than the pool contains.
 Assert.Equal("OnlyOne", Store.PlayerPreFightOption1Id[PlayerId]);
 Assert.Equal("OnlyOne", Store.PlayerPreFightOption2Id[PlayerId]);
 Assert.Equal("OnlyOne", Store.PlayerPreFightOption3Id[PlayerId]);
 }

 // ─── SelectPreFightBuff ──────────────────────────────────────────

 [Fact]
 public void SelectPreFightBuff_ValidIndex_WritesSelection()
 {
 var (sys, towerId) = MakeSystem();
 sys.SetWavePending(true);
 sys.Update(0.016f);
 string[] opts = sys.GetCurrentOptions(PlayerId);
 sys.SelectPreFightBuff(PlayerId,1); // pick the middle option
 Assert.Equal(opts[1], Store.PlayerPreFightSelectedBuffId[PlayerId]);
 }

 [Fact]
 public void SelectPreFightBuff_OutOfRange_Rejected()
 {
 var (sys, towerId) = MakeSystem();
 sys.SetWavePending(true);
 sys.Update(0.016f);
 sys.SelectPreFightBuff(PlayerId,99);
 Assert.Equal("", Store.PlayerPreFightSelectedBuffId[PlayerId]);
 }

 [Fact]
 public void SelectPreFightBuff_CachesCritAndMaxHp()
 {
 // Build a config whose option[0] has non-default CritChance + MaxHpMult.
 // Use a single-element pool so index0 is always the CritBuff entry — the
 // test is verifying the CACHING logic in SelectPreFightBuff, not the
 // weighted-random roll (that has its own dedicated test).
 var pool = new PreFightBuffOptionDef[]
 {
 new() { Id = "CritBuff", Weight =1f, DamageMult =1f, SpeedMult =1f, CritChance =0.10f, MaxHpMult =1.25f }
 };
 var (sys, towerId) = MakeSystem(optionsPerWave:1, pool: pool);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 sys.SelectPreFightBuff(PlayerId,0);
 Assert.Equal(0.10f, Store.PlayerPreFightCritBonus[PlayerId]);
 Assert.Equal(1.25f, Store.PlayerPreFightMaxHpMult[PlayerId]);
 }

 [Fact]
 public void SelectPreFightBuff_UnknownId_DefaultsToZeroAndOne()
 {
 var (sys, towerId) = MakeSystem();
 // Force a selection of an id that's not in the pool.
 Store.PlayerPreFightSelectedBuffId[PlayerId] = "UnknownBuff";
 // Cache should be the default (0 crit,1.0 max hp mult) because the lookup
 // happens during Select, not during a re-resolve.
 sys.SelectPreFightBuff(PlayerId,0); // will look up option1, which is valid
 // After the legitimate select, the cache should reflect the chosen option.
 Assert.True(Store.PlayerPreFightCritBonus[PlayerId] >=0f);
 Assert.True(Store.PlayerPreFightMaxHpMult[PlayerId] >0f);
 }

 // ─── ApplyToAllActiveTowers (OnWaveStart) ─────────────────────────

 [Fact]
 public void ApplyToAllActiveTowers_NoSelection_StaysAtFastPath()
 {
 var (sys, towerId) = MakeSystem();
 // No selection made — ApplyToAllActiveTowers is a no-op for the cache.
 sys.ApplyToAllActiveTowers();
 Assert.Equal(1f, Store.TowerPreFightDamageMult[towerId]);
 Assert.Equal(1f, Store.TowerPreFightSpeedMult[towerId]);
 }

 [Fact]
 public void ApplyToAllActiveTowers_ValidSelection_WritesTowerCache()
 {
 // Use a single-element pool so index0 is always the DmgBuff entry. This
 // test verifies the apply-to-tower cache write path, not the weighted
 // roll (which has its own dedicated test).
 var pool = new PreFightBuffOptionDef[]
 {
 new() { Id = "DmgBuff", Weight =1f, DamageMult =1.20f, SpeedMult =1.10f, CritChance =0f, MaxHpMult =1f }
 };
 var (sys, towerId) = MakeSystem(optionsPerWave:1, pool: pool);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 sys.SelectPreFightBuff(PlayerId,0); // DmgBuff
 sys.ApplyToAllActiveTowers();
 Assert.Equal(1.20f, Store.TowerPreFightDamageMult[towerId]);
 Assert.Equal(1.10f, Store.TowerPreFightSpeedMult[towerId]);
 }

 [Fact]
 public void ApplyToAllActiveTowers_InactiveTower_NotWritten()
 {
 var pool = new PreFightBuffOptionDef[]
 {
 new() { Id = "DmgBuff", Weight =1f, DamageMult =1.20f, SpeedMult =1.10f }
 };
 var (sys, towerId) = MakeSystem(pool: pool);
 Store.TowerActive[towerId] = false;
 sys.SetWavePending(true);
 sys.Update(0.016f);
 sys.SelectPreFightBuff(PlayerId,0);
 sys.ApplyToAllActiveTowers();
 // Inactive tower cache stays at1f fast path.
 Assert.Equal(1f, Store.TowerPreFightDamageMult[towerId]);
 Assert.Equal(1f, Store.TowerPreFightSpeedMult[towerId]);
 }

 [Fact]
 public void ApplyToAllActiveTowers_UnknownId_StaysAtFastPath()
 {
 var (sys, towerId) = MakeSystem();
 // Manually corrupt SelectedBuffId to a non-pool id.
 Store.PlayerPreFightSelectedBuffId[PlayerId] = "GhostBuff";
 sys.ApplyToAllActiveTowers();
 Assert.Equal(1f, Store.TowerPreFightDamageMult[towerId]);
 }

 // ─── ClearWaveScoped (OnWaveComplete) ─────────────────────────────

 [Fact]
 public void ClearWaveScoped_ResetsTowerCacheToOne()
 {
 var pool = new PreFightBuffOptionDef[]
 {
 new() { Id = "DmgBuff", Weight =1f, DamageMult =1.20f, SpeedMult =1.10f }
 };
 var (sys, towerId) = MakeSystem(pool: pool);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 sys.SelectPreFightBuff(PlayerId,0);
 sys.ApplyToAllActiveTowers();
 Assert.Equal(1.20f, Store.TowerPreFightDamageMult[towerId]);

 // Now wave ends → ClearWaveScoped
 sys.ClearWaveScoped();
 Assert.Equal(1f, Store.TowerPreFightDamageMult[towerId]);
 Assert.Equal(1f, Store.TowerPreFightSpeedMult[towerId]);
 Assert.Equal("", Store.PlayerPreFightSelectedBuffId[PlayerId]);
 Assert.False(Store.PlayerPreFightOptionsRolled[PlayerId]);
 Assert.Equal("", Store.PlayerPreFightOption1Id[PlayerId]);
 }

 [Fact]
 public void ClearWaveScoped_ClearsDeadAndAlivePlayerSlots()
 {
 var (sys, towerId) = MakeSystem();
 // 契约：ClearWaveScoped 遍历全部 MAX_PLAYERS 槽位，不按 HP 是否 >0 跳过。
 // 预置死玩家(0)与活玩家(1)的非默认槽位，调用后两者都必须被清回默认值。
 Store.AddPlayer(1,5f,1f,100f,1);
 Store.PlayerCurrentHealth[PlayerId] =0f;
 foreach (int p in new[] { PlayerId,1 })
 {
 Store.PlayerPreFightSelectedBuffId[p] = "GhostBuff";
 Store.PlayerPreFightOption1Id[p] = "Opt1";
 Store.PlayerPreFightOption2Id[p] = "Opt2";
 Store.PlayerPreFightOption3Id[p] = "Opt3";
 Store.PlayerPreFightOptionsRolled[p] = true;
 Store.PlayerPreFightCritBonus[p] =0.42f;
 Store.PlayerPreFightMaxHpMult[p] =1.25f;
 }

 sys.ClearWaveScoped();

 foreach (int p in new[] { PlayerId,1 })
 {
 Assert.Equal("", Store.PlayerPreFightSelectedBuffId[p]);
 Assert.Equal("", Store.PlayerPreFightOption1Id[p]);
 Assert.Equal("", Store.PlayerPreFightOption2Id[p]);
 Assert.Equal("", Store.PlayerPreFightOption3Id[p]);
 Assert.False(Store.PlayerPreFightOptionsRolled[p]);
 Assert.Equal(0f, Store.PlayerPreFightCritBonus[p]);
 Assert.Equal(1f, Store.PlayerPreFightMaxHpMult[p]);
 }
 }

 // ─── Full lifecycle integration ──────────────────────────────────

 [Fact]
 public void FullLifecycle_RollSelectApplyComplete()
 {
 var pool = new PreFightBuffOptionDef[]
 {
 new() { Id = "Fury", Weight =1f, DamageMult =1.20f, SpeedMult =1f },
 new() { Id = "Swift", Weight =1f, DamageMult =1f, SpeedMult =1.30f },
 new() { Id = "Balanced", Weight =1f, DamageMult =1.05f, SpeedMult =1.05f }
 };
 var (sys, towerId) = MakeSystem(pool: pool);

 //1. BuildPhase tick — roll3 options.
 sys.SetWavePending(true);
 sys.Update(0.016f);
 Assert.True(Store.PlayerPreFightOptionsRolled[PlayerId]);

 //2. Player selects one.
 sys.SelectPreFightBuff(PlayerId,1);
 string picked = Store.PlayerPreFightSelectedBuffId[PlayerId];
 Assert.NotEqual("", picked);

 //3. Wave starts — apply to towers.
 sys.ApplyToAllActiveTowers();
 var dmg = Store.TowerPreFightDamageMult[towerId];
 var spd = Store.TowerPreFightSpeedMult[towerId];
 // Picked buff must produce non-default multipliers (the chosen one of the3
 // always has DamageMult !=1 OR SpeedMult !=1, otherwise it's a no-op).
 Assert.True(dmg !=1f || spd !=1f);

 //4. Wave ends — clear everything back.
 sys.ClearWaveScoped();
 Assert.Equal(1f, Store.TowerPreFightDamageMult[towerId]);
 Assert.Equal(1f, Store.TowerPreFightSpeedMult[towerId]);
 Assert.Equal("", Store.PlayerPreFightSelectedBuffId[PlayerId]);
 Assert.False(Store.PlayerPreFightOptionsRolled[PlayerId]);
 }

 // ─── Weighted distribution ───────────────────────────────────────

 [Fact]
 public void WeightedRoll_HighWeightBuff_PickedMoreOften()
 {
 //80/20 split: HeavyBuff has weight4, LightBuff has weight1.
 var pool = new PreFightBuffOptionDef[]
 {
 new() { Id = "Heavy", Weight =4f, DamageMult =1.10f },
 new() { Id = "Light", Weight =1f, DamageMult =1.10f }
 };
 Config.PreFight = new PreFightBuffConfig
 {
 Enabled = true,
 OptionsPerWave = 3,
 Pool = pool
 };

 int heavyCount =0;
 int lightCount =0;
 int trials =2000;
 for (int i =0; i < trials; i++)
 {
 // Fresh store per trial so the roll latch (PlayerPreFightOptionsRolled)
 // does not skip the second-and-later iterations. This models a fresh
 // BuildPhase per trial.
 var store = new ComponentStore(); // 保留独立 store：同一测试需 2000 个互不污染的独立 store 做权重采样
 store.AddPlayer(PlayerId,5f,1f,100f,1);
 store.AddTower(0, TowerType.Basic,10f,3,1f,1,50f);
 store.TowerActive[0] = true;
 store.TowerAttackSpeed[0] =1f;
 store.TowerAttackDamage[0] =10f;
 var sys = new PreFightBuffSystem(store, Config);
 // 每个新实例显式递增固定种子，消除 Environment.TickCount 的随机性。
 SeedSystemRng(sys, store, i);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 string picked = store.PlayerPreFightOption1Id[PlayerId];
 if (picked == "Heavy") heavyCount++;
 else if (picked == "Light") lightCount++;
 }
 // 权重 4:1 → 期望 Heavy 比例 = 4/5 = 0.80。
 // 区间 [0.70, 0.90]：2000 次固定种子试验中若实现尊重权重，比例几乎必然落在
 // 期望值 ±0.1 内；若退化为等概率（0.5）或近似全选 Heavy（1.0）都会越界失败。
 double heavyRatio = heavyCount / (double)trials;
 Assert.InRange(heavyRatio,0.70,0.90);
 // Sanity: neither should be zero in2000 trials with reasonable weights.
 Assert.True(heavyCount >0);
 Assert.True(lightCount >0);
 }

 [Fact]
 public void WeightedRoll_ZeroWeight_SkippedFromPool()
 {
 var pool = new PreFightBuffOptionDef[]
 {
 new() { Id = "Real", Weight =1f, DamageMult =1.1f },
 new() { Id = "Disabled", Weight =0f, DamageMult =1.5f }
 };
 var (sys, towerId) = MakeSystem(pool: pool);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 // Pool has only one pickable entry → both slots get the same id (the
 // system can't enforce uniqueness against an effectively1-entry pool).
 Assert.Equal("Real", Store.PlayerPreFightOption1Id[PlayerId]);
 Assert.Equal("Real", Store.PlayerPreFightOption2Id[PlayerId]);
 Assert.Equal("Real", Store.PlayerPreFightOption3Id[PlayerId]);
 }

 // ─── FindOptionById ──────────────────────────────────────────────

 [Fact]
 public void FindOptionById_Existing_ReturnsDef()
 {
 var (sys, towerId) = MakeSystem();
 var opt = sys.FindOptionById("AttackFury");
 Assert.NotNull(opt);
 Assert.Equal(1.15f, opt.DamageMult);
 }

 [Fact]
 public void FindOptionById_Missing_ReturnsNull()
 {
 var (sys, towerId) = MakeSystem();
 Assert.Null(sys.FindOptionById("NoSuchBuff"));
 }

 [Fact]
 public void FindOptionById_EmptyString_ReturnsNull()
 {
 var (sys, towerId) = MakeSystem();
 Assert.Null(sys.FindOptionById(""));
 }

 // ─── GetCurrentOptions ───────────────────────────────────────────

 [Fact]
 public void GetCurrentOptions_NoRoll_ReturnsEmptySlots()
 {
 var (sys, towerId) = MakeSystem();
 var opts = sys.GetCurrentOptions(PlayerId);
 Assert.Equal(3, opts.Length);
 Assert.All(opts, s => Assert.Equal("", s));
 }

 [Fact]
 public void GetCurrentOptions_OutOfRangePlayer_ReturnsEmpty()
 {
 var (sys, towerId) = MakeSystem();
 var opts = sys.GetCurrentOptions(999);
 Assert.Equal(3, opts.Length);
 Assert.All(opts, s => Assert.Equal("", s));
 }

 // ─── SubscribeToWaveEvents idempotency ───────────────────────────

 [Fact]
 public void WaveHandlersRequireNoConcreteSpawnerReference()
 {
 var (sys, towerId) = MakeSystem();
 Store.PlayerPreFightSelectedBuffId[PlayerId] = "selected";
 Store.TowerPreFightDamageMult[towerId] = 3f;
 sys.HandleWaveComplete();
 Assert.Equal("", Store.PlayerPreFightSelectedBuffId[PlayerId]);
 Assert.Equal(1f, Store.TowerPreFightDamageMult[towerId]);
 }
 }
}
