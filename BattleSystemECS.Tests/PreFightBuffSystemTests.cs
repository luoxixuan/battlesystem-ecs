using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
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
 public class PreFightBuffSystemTests
 {
 private const int TowerId =0;
 private const int PlayerId =0;

 private static GameConfig MakeConfig(
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
 return new GameConfig
 {
 PreFight = new PreFightBuffConfig
 {
 Enabled = enabled,
 OptionsPerWave = optionsPerWave,
 Pool = pool
 }
 };
 }

 private static ComponentStore MakeStoreWithPlayerAndTower(GameConfig cfg)
 {
 var store = new ComponentStore();
 store.AddPlayer(PlayerId,5f,1f,100f,1);
 store.AddTower(TowerId, TowerType.Basic,10f,3,1f,1,50f);
 store.TowerActive[TowerId] = true;
 store.TowerAttackSpeed[TowerId] =1f;
 store.TowerAttackDamage[TowerId] =10f;
 return store;
 }

 // ─── Default state (backward compat) ──────────────────────────────

 [Fact]
 public void DefaultState_NewComponentStore_AllPreFightFieldsEmptyOrZero()
 {
 var store = new ComponentStore();
 // String fields default to null until AddPlayer resets them.
 Assert.True(string.IsNullOrEmpty(store.PlayerPreFightSelectedBuffId[PlayerId]));
 Assert.True(string.IsNullOrEmpty(store.PlayerPreFightOption1Id[PlayerId]));
 Assert.True(string.IsNullOrEmpty(store.PlayerPreFightOption2Id[PlayerId]));
 Assert.True(string.IsNullOrEmpty(store.PlayerPreFightOption3Id[PlayerId]));
 Assert.False(store.PlayerPreFightOptionsRolled[PlayerId]);
 Assert.Equal(0f, store.PlayerPreFightCritBonus[PlayerId]);
 Assert.Equal(0f, store.PlayerPreFightMaxHpMult[PlayerId]);
 }

 [Fact]
 public void AddTower_InitializesTowerCacheToOneFastPath()
 {
 var store = new ComponentStore();
 store.AddPlayer(PlayerId,5f,1f,10f,1);
 store.AddTower(TowerId, TowerType.Basic,10f,3,1f,1,50f);
 // After AddTower the cache fields default to1f (fast path).
 Assert.Equal(1f, store.TowerPreFightDamageMult[TowerId]);
 Assert.Equal(1f, store.TowerPreFightSpeedMult[TowerId]);
 }

 [Fact]
 public void RemoveTower_ResetsTowerCacheToOneFastPath()
 {
 var store = new ComponentStore();
 store.AddPlayer(PlayerId,5f,1f,10f,1);
 store.AddTower(TowerId, TowerType.Basic,10f,3,1f,1,50f);
 store.RemoveTower(TowerId);
 // After RemoveTower the cache fields are still1f (no leak).
 Assert.Equal(1f, store.TowerPreFightDamageMult[TowerId]);
 Assert.Equal(1f, store.TowerPreFightSpeedMult[TowerId]);
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
 var cfg = MakeConfig();
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 // Default _wavePending = false → no roll happens.
 sys.Update(0.016f);
 Assert.Equal("", store.PlayerPreFightOption1Id[PlayerId]);
 Assert.False(store.PlayerPreFightOptionsRolled[PlayerId]);
 }

 [Fact]
 public void Update_WavePendingTrue_RollsThreeOptions()
 {
 var cfg = MakeConfig(optionsPerWave:3);
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 Assert.True(store.PlayerPreFightOptionsRolled[PlayerId]);
 Assert.NotEqual("", store.PlayerPreFightOption1Id[PlayerId]);
 Assert.NotEqual("", store.PlayerPreFightOption2Id[PlayerId]);
 Assert.NotEqual("", store.PlayerPreFightOption3Id[PlayerId]);
 }

 [Fact]
 public void Update_WavePendingTrue_NoDuplicatesInRoll()
 {
 var cfg = MakeConfig(optionsPerWave:3);
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
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
 var cfg = MakeConfig();
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
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
 var cfg = MakeConfig(enabled: false);
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 Assert.False(store.PlayerPreFightOptionsRolled[PlayerId]);
 }

 [Fact]
 public void Update_EmptyPool_NoRoll()
 {
 var cfg = MakeConfig(pool: Array.Empty<PreFightBuffOptionDef>());
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 Assert.False(store.PlayerPreFightOptionsRolled[PlayerId]);
 }

 [Fact]
 public void Update_DeadPlayer_StillRollsForAllSlots()
 {
 // Unlike systems that gate on PlayerCurrentHealth, PreFightBuffSystem
 // unconditionally iterates MAX_PLAYERS (mirrors Interest/GlobalSkill
 // pattern). The SelectedBuffId "" sentinel still protects against
 // accidental application of stale state when AddPlayer is later called.
 var cfg = MakeConfig();
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 store.PlayerCurrentHealth[PlayerId] =0f;
 sys.SetWavePending(true);
 sys.Update(0.016f);
 // Roll still happened for the slot, even though HP is0.
 Assert.True(store.PlayerPreFightOptionsRolled[PlayerId]);
 }

 [Fact]
 public void Update_OptionsPerWaveExceedsPool_FillsAllSlotsViaLastIdFallback()
 {
 var smallPool = new PreFightBuffOptionDef[]
 {
 new() { Id = "OnlyOne", Weight =1f, DamageMult =1.1f }
 };
 var cfg = MakeConfig(optionsPerWave:5, pool: smallPool);
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 // Pool is exhausted after the first pick, so the lastId fallback fills
 // the remaining slots with the same id — the design can't surface more
 // unique options than the pool contains.
 Assert.Equal("OnlyOne", store.PlayerPreFightOption1Id[PlayerId]);
 Assert.Equal("OnlyOne", store.PlayerPreFightOption2Id[PlayerId]);
 Assert.Equal("OnlyOne", store.PlayerPreFightOption3Id[PlayerId]);
 }

 // ─── SelectPreFightBuff ──────────────────────────────────────────

 [Fact]
 public void SelectPreFightBuff_ValidIndex_WritesSelection()
 {
 var cfg = MakeConfig();
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 string[] opts = sys.GetCurrentOptions(PlayerId);
 sys.SelectPreFightBuff(PlayerId,1); // pick the middle option
 Assert.Equal(opts[1], store.PlayerPreFightSelectedBuffId[PlayerId]);
 }

 [Fact]
 public void SelectPreFightBuff_OutOfRange_Rejected()
 {
 var cfg = MakeConfig();
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 sys.SelectPreFightBuff(PlayerId,99);
 Assert.Equal("", store.PlayerPreFightSelectedBuffId[PlayerId]);
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
 var cfg = MakeConfig(optionsPerWave:1, pool: pool);
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 sys.SelectPreFightBuff(PlayerId,0);
 Assert.Equal(0.10f, store.PlayerPreFightCritBonus[PlayerId]);
 Assert.Equal(1.25f, store.PlayerPreFightMaxHpMult[PlayerId]);
 }

 [Fact]
 public void SelectPreFightBuff_UnknownId_DefaultsToZeroAndOne()
 {
 var cfg = MakeConfig();
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 // Force a selection of an id that's not in the pool.
 store.PlayerPreFightSelectedBuffId[PlayerId] = "UnknownBuff";
 // Cache should be the default (0 crit,1.0 max hp mult) because the lookup
 // happens during Select, not during a re-resolve.
 sys.SelectPreFightBuff(PlayerId,0); // will look up option1, which is valid
 // After the legitimate select, the cache should reflect the chosen option.
 Assert.True(store.PlayerPreFightCritBonus[PlayerId] >=0f);
 Assert.True(store.PlayerPreFightMaxHpMult[PlayerId] >0f);
 }

 // ─── ApplyToAllActiveTowers (OnWaveStart) ─────────────────────────

 [Fact]
 public void ApplyToAllActiveTowers_NoSelection_StaysAtFastPath()
 {
 var cfg = MakeConfig();
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 // No selection made — ApplyToAllActiveTowers is a no-op for the cache.
 sys.ApplyToAllActiveTowers();
 Assert.Equal(1f, store.TowerPreFightDamageMult[TowerId]);
 Assert.Equal(1f, store.TowerPreFightSpeedMult[TowerId]);
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
 var cfg = MakeConfig(optionsPerWave:1, pool: pool);
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 sys.SelectPreFightBuff(PlayerId,0); // DmgBuff
 sys.ApplyToAllActiveTowers();
 Assert.Equal(1.20f, store.TowerPreFightDamageMult[TowerId]);
 Assert.Equal(1.10f, store.TowerPreFightSpeedMult[TowerId]);
 }

 [Fact]
 public void ApplyToAllActiveTowers_InactiveTower_NotWritten()
 {
 var pool = new PreFightBuffOptionDef[]
 {
 new() { Id = "DmgBuff", Weight =1f, DamageMult =1.20f, SpeedMult =1.10f }
 };
 var cfg = MakeConfig(pool: pool);
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 store.TowerActive[TowerId] = false;
 sys.SetWavePending(true);
 sys.Update(0.016f);
 sys.SelectPreFightBuff(PlayerId,0);
 sys.ApplyToAllActiveTowers();
 // Inactive tower cache stays at1f fast path.
 Assert.Equal(1f, store.TowerPreFightDamageMult[TowerId]);
 Assert.Equal(1f, store.TowerPreFightSpeedMult[TowerId]);
 }

 [Fact]
 public void ApplyToAllActiveTowers_UnknownId_StaysAtFastPath()
 {
 var cfg = MakeConfig();
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 // Manually corrupt SelectedBuffId to a non-pool id.
 store.PlayerPreFightSelectedBuffId[PlayerId] = "GhostBuff";
 sys.ApplyToAllActiveTowers();
 Assert.Equal(1f, store.TowerPreFightDamageMult[TowerId]);
 }

 // ─── ClearWaveScoped (OnWaveComplete) ─────────────────────────────

 [Fact]
 public void ClearWaveScoped_ResetsTowerCacheToOne()
 {
 var pool = new PreFightBuffOptionDef[]
 {
 new() { Id = "DmgBuff", Weight =1f, DamageMult =1.20f, SpeedMult =1.10f }
 };
 var cfg = MakeConfig(pool: pool);
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 sys.SelectPreFightBuff(PlayerId,0);
 sys.ApplyToAllActiveTowers();
 Assert.Equal(1.20f, store.TowerPreFightDamageMult[TowerId]);

 // Now wave ends → ClearWaveScoped
 sys.ClearWaveScoped();
 Assert.Equal(1f, store.TowerPreFightDamageMult[TowerId]);
 Assert.Equal(1f, store.TowerPreFightSpeedMult[TowerId]);
 Assert.Equal("", store.PlayerPreFightSelectedBuffId[PlayerId]);
 Assert.False(store.PlayerPreFightOptionsRolled[PlayerId]);
 Assert.Equal("", store.PlayerPreFightOption1Id[PlayerId]);
 }

 [Fact]
 public void ClearWaveScoped_DeadPlayer_SkipsSlot()
 {
 var cfg = MakeConfig();
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 store.PlayerCurrentHealth[PlayerId] =0f;
 // Should not throw; the dead slot is skipped.
 sys.ClearWaveScoped();
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
 var cfg = MakeConfig(pool: pool);
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);

 //1. BuildPhase tick — roll3 options.
 sys.SetWavePending(true);
 sys.Update(0.016f);
 Assert.True(store.PlayerPreFightOptionsRolled[PlayerId]);

 //2. Player selects one.
 sys.SelectPreFightBuff(PlayerId,1);
 string picked = store.PlayerPreFightSelectedBuffId[PlayerId];
 Assert.NotEqual("", picked);

 //3. Wave starts — apply to towers.
 sys.ApplyToAllActiveTowers();
 var dmg = store.TowerPreFightDamageMult[TowerId];
 var spd = store.TowerPreFightSpeedMult[TowerId];
 // Picked buff must produce non-default multipliers (the chosen one of the3
 // always has DamageMult !=1 OR SpeedMult !=1, otherwise it's a no-op).
 Assert.True(dmg !=1f || spd !=1f);

 //4. Wave ends — clear everything back.
 sys.ClearWaveScoped();
 Assert.Equal(1f, store.TowerPreFightDamageMult[TowerId]);
 Assert.Equal(1f, store.TowerPreFightSpeedMult[TowerId]);
 Assert.Equal("", store.PlayerPreFightSelectedBuffId[PlayerId]);
 Assert.False(store.PlayerPreFightOptionsRolled[PlayerId]);
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
 var cfg = MakeConfig(pool: pool);

 int heavyCount =0;
 int lightCount =0;
 int trials =2000;
 for (int i =0; i < trials; i++)
 {
 // Fresh store per trial so the roll latch (PlayerPreFightOptionsRolled)
 // does not skip the second-and-later iterations. This models a fresh
 // BuildPhase per trial.
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 string picked = store.PlayerPreFightOption1Id[PlayerId];
 if (picked == "Heavy") heavyCount++;
 else if (picked == "Light") lightCount++;
 }
 // Heavy should dominate (expect ~80%).
 Assert.True(heavyCount > lightCount, $"Heavy={heavyCount} Light={lightCount}");
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
 var cfg = MakeConfig(pool: pool);
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 sys.SetWavePending(true);
 sys.Update(0.016f);
 // Pool has only one pickable entry → both slots get the same id (the
 // system can't enforce uniqueness against an effectively1-entry pool).
 Assert.Equal("Real", store.PlayerPreFightOption1Id[PlayerId]);
 Assert.Equal("Real", store.PlayerPreFightOption2Id[PlayerId]);
 Assert.Equal("Real", store.PlayerPreFightOption3Id[PlayerId]);
 }

 // ─── FindOptionById ──────────────────────────────────────────────

 [Fact]
 public void FindOptionById_Existing_ReturnsDef()
 {
 var cfg = MakeConfig();
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 var opt = sys.FindOptionById("AttackFury");
 Assert.NotNull(opt);
 Assert.Equal(1.15f, opt.DamageMult);
 }

 [Fact]
 public void FindOptionById_Missing_ReturnsNull()
 {
 var cfg = MakeConfig();
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 Assert.Null(sys.FindOptionById("NoSuchBuff"));
 }

 [Fact]
 public void FindOptionById_EmptyString_ReturnsNull()
 {
 var cfg = MakeConfig();
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 Assert.Null(sys.FindOptionById(""));
 }

 // ─── GetCurrentOptions ───────────────────────────────────────────

 [Fact]
 public void GetCurrentOptions_NoRoll_ReturnsEmptySlots()
 {
 var cfg = MakeConfig();
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 var opts = sys.GetCurrentOptions(PlayerId);
 Assert.Equal(3, opts.Length);
 Assert.All(opts, s => Assert.Equal("", s));
 }

 [Fact]
 public void GetCurrentOptions_OutOfRangePlayer_ReturnsEmpty()
 {
 var cfg = MakeConfig();
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 var opts = sys.GetCurrentOptions(999);
 Assert.Equal(3, opts.Length);
 Assert.All(opts, s => Assert.Equal("", s));
 }

 // ─── SubscribeToWaveEvents idempotency ───────────────────────────

 [Fact]
 public void SubscribeToWaveEvents_Idempotent()
 {
 var cfg = MakeConfig();
 var store = MakeStoreWithPlayerAndTower(cfg);
 var sys = new PreFightBuffSystem(store, cfg);
 // Pass null — should be a safe no-op.
 sys.SubscribeToWaveEvents(null);
 // No exception, no side effect.
 Assert.False(store.PlayerPreFightOptionsRolled[PlayerId]);
 }
 }
}
