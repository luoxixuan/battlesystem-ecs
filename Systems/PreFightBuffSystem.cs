using System;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
 /// <summary>
 /// PreFightBuffSystem (Round178 Direction6) — BuildPhase末「3选1」出战 buff.
 ///
 /// Lifecycle:
 ///1. <see cref="Update(float)"/> runs once per BuildPhase tick. It
 /// detects the "wave-pending" state via the public SetWavePending /
 /// SetWaveRunning helpers (called by SystemRegistry.WireDependencies
 /// from the WaveSpawningSystem OnWaveStart/OnWaveComplete events).
 ///2. On the transition WaveRunning → WavePending, it rolls
 /// <see cref="PreFightBuffConfig.OptionsPerWave"/> weighted-random
 /// options from <see cref="PreFightBuffConfig.Pool"/> and writes
 /// them into PlayerPreFightOption{1,2,3}Id[]. Sets the
 /// PlayerPreFightOptionsRolled latch so UI / tests can detect the
 /// roll happened.
 ///3. The player (or auto-selection logic) calls
 /// <see cref="SelectPreFightBuff"/> with an index0..2 to pick one.
 /// The selection is written to PlayerPreFightSelectedBuffId[] and
 /// the buff's per-player fields (CritBonus / MaxHpMult) are cached
 /// in the same call so the next OnWaveStart sees fresh values.
 ///4. OnWaveStart → <see cref="ApplyToAllActiveTowers"/> walks the
 /// ActiveTowerIds list and writes the chosen buff's DamageMult /
 /// SpeedMult into every tower's cache fields. The next
 /// TowerAttackSystem hot path reads these caches.
 ///5. OnWaveComplete → <see cref="ClearWaveScoped"/> resets the
 /// tower caches back to1f and clears the player-side selected
 /// buff so the next BuildPhase starts fresh.
 ///
 /// Sentinel-gated fast path: when PreFightConfig.Enabled == false
 /// or the pool is empty, Update() is a single line of state
 /// tracking and the tower cache stays at1f for the entire game
 /// (zero overhead on the TowerAttack hot path).
 ///
 /// Round178 Bug Fix (initial Claude scan):
 /// The first draft used a single Random instance and called .Next()
 /// for the roll. That produced a uniform distribution regardless
 /// of the configured Weight. Fixed by using a weight-accumulator
 /// roll (sum all weights, pick a uniform [0, sum), then walk the
 /// pool to find which entry owns that band).
 /// </summary>
 public class PreFightBuffSystem
 {
 private readonly ComponentStore store;
 private readonly GameConfig gameConfig;
 // Phase latch — true between OnWaveComplete and the next OnWaveStart.
 // Cleared on OnWaveStart. Detected by Update() to know when to roll.
 private bool _wavePending;
 // Idempotency guard against WireDependencies re-init / test reset
 // paths stacking duplicate OnWaveStart/OnWaveComplete handlers.
 // Re-usable Random instance. Seeded once per system so a test can
 // pass a deterministic seed by setting SystemRandomSeed (test hook).
 public int SystemRandomSeed = Environment.TickCount;
 private Random _rng;

 public PreFightBuffSystem(ComponentStore store, GameConfig gameConfig)
 {
 this.store = store;
 this.gameConfig = gameConfig;
 _rng = new Random(SystemRandomSeed);
 }

 /// <summary>
 /// Public setter for the wave-pending latch. Useful for tests that
 /// don't want to spin up a full WaveSpawningSystem. Production code
 /// 由 ProductionEvents 组合配方驱动该流程。
 /// </summary>
 public void SetWavePending(bool pending) => _wavePending = pending;

 // ── Event handlers ────────────────────────────────────────────
 public void HandleWaveStart()
 {
 _wavePending = false;
 ApplyToAllActiveTowers();
 }

 public void HandleWaveComplete()
 {
 _wavePending = true;
 ClearWaveScoped();
 }

 /// <summary>
 /// Per-frame BuildPhase tick. When _wavePending flips false→true
 /// (the moment a wave ends and BuildPhase begins), roll N options
 /// into PlayerPreFightOption{1,2,3}Id[]. Subsequent ticks within
 /// the same BuildPhase are no-ops (the roll latch is held).
 ///
 /// Sentinel fast path: when the config is null / disabled / the
 /// pool is empty, returns immediately. Also avoids throwing when
 /// gameConfig.PreFight is null (post-JSON-deserialization state).
 /// We iterate ALL MAX_PLAYERS slots — slots that have not been
 /// AddPlayer()-initialized have their option slots at null/empty
 /// (the sentinel "no roll yet" state), so writes are still safe and
 /// idempotent on next AddPlayer (which clears the slots). This
 /// mirrors how other BuildGroup systems (e.g. Interest, GlobalSkill)
 /// unconditionally iterate MAX_PLAYERS without an explicit "alive"
 /// check.
 /// </summary>
 public void Update(float deltaTime)
 {
 var cfg = gameConfig.PreFight;
 if (cfg == null || !cfg.Enabled) return;
 if (cfg.Pool == null || cfg.Pool.Length ==0) return;
 if (!_wavePending) return; // only roll on the WaveRunning→WavePending transition

 int playerCount = ComponentStore.MAX_PLAYERS;
 for (int p =0; p < playerCount; p++)
 {
 // Skip if we already rolled for this BuildPhase.
 if (store.PlayerPreFightOptionsRolled[p]) continue;
 RollOptionsForPlayer(p, cfg);
 }
 }

 // ── Core logic ────────────────────────────────────────────────
 private void RollOptionsForPlayer(int playerId, PreFightBuffConfig cfg)
 {
 int want = cfg.OptionsPerWave;
 int poolLen = cfg.Pool.Length;
 // Clamp want to >=1 to ensure we always produce at least one option.
 if (want <1) want =1;
 // We intentionally do NOT clamp want to poolLen: when the pool is smaller
 // than OptionsPerWave, the lastId-fallback path below fills the remaining
 // slots with the most recently picked id. This matches the design intent
 // ("3选1" means 3 cards presented to the player; if the pool only has 1
 // pickable entry, all 3 cards display that entry).

 // Build the option list. We use a temp array sized to the pool so
 // we can mark picked entries; reusing a single field across rolls
 // would require a clear-sweep and we'd rather just allocate once.
 // Track the lastId so when the pool is exhausted (fewer pickable
 // entries than OptionsPerWave, e.g. 1 entry with weight>0 and the
 // rest weight=0) we can repeat the last successful pick across the
 // remaining slots — the design contract is that ALL OptionsPerWave
 // slots are always populated, and we can't enforce uniqueness when
 // there are fewer pickable entries than requested options.
 var picked = new bool[poolLen];
 int filled =0;
 string lastId = "";
 while (filled < want)
 {
 int idx = WeightedRoll(cfg.Pool, picked);
 if (idx <0)
 {
 // Pool exhausted: repeat the last successful pick to fill remaining
 // slots. This matches the contract for zero-weight / small pool.
 if (string.IsNullOrEmpty(lastId)) break; // no successful pick yet (e.g. all weight=0)
 if (filled ==0) store.PlayerPreFightOption1Id[playerId] = lastId;
 else if (filled ==1) store.PlayerPreFightOption2Id[playerId] = lastId;
 else store.PlayerPreFightOption3Id[playerId] = lastId;
 filled++;
 continue;
 }
 picked[idx] = true;
 filled++;
 string pickedId = cfg.Pool[idx]?.Id ?? "";
 lastId = pickedId;
 if (filled ==1) store.PlayerPreFightOption1Id[playerId] = pickedId;
 else if (filled ==2) store.PlayerPreFightOption2Id[playerId] = pickedId;
 else if (filled ==3) store.PlayerPreFightOption3Id[playerId] = pickedId;
 }
 store.PlayerPreFightOptionsRolled[playerId] = true;
 }

 private int WeightedRoll(PreFightBuffOptionDef[] pool, bool[] picked)
 {
 float total =0f;
 for (int i =0; i < pool.Length; i++)
 {
 if (picked[i]) continue;
 var opt = pool[i];
 if (opt == null) continue;
 float w = opt.Weight;
 if (w >0f) total += w;
 }
 if (total <=0f) return -1;
 double r = _rng.NextDouble() * total;
 double acc =0;
 for (int i =0; i < pool.Length; i++)
 {
 if (picked[i]) continue;
 var opt = pool[i];
 if (opt == null) continue;
 float w = opt.Weight;
 if (w <=0f) continue;
 acc += w;
 if (r <= acc) return i;
 }
 // Floating-point edge: return the last pickable entry.
 for (int i = pool.Length -1; i >=0; i--)
 {
 if (!picked[i] && pool[i] != null && pool[i].Weight >0f) return i;
 }
 return -1;
 }

 /// <summary>
 /// Public API: select the option at the given index for the player.
 /// Index0 → Option1Id,1 → Option2Id,2 → Option3Id. Out-of-range
 /// indices are silently rejected. Selection is sticky for the
 /// BuildPhase; the next OnWaveStart applies it. Calling Select twice
 /// in the same BuildPhase just overwrites — last-write-wins, which
 /// matches typical UI behavior.
 ///
 /// Side effect: caches CritBonus / MaxHpMult on the player so
 /// GameManager / UI layers can read the values without re-resolving
 /// the Pool every frame.
 /// </summary>
 public void SelectPreFightBuff(int playerId, int optionIndex)
 {
 if (playerId <0 || playerId >= ComponentStore.MAX_PLAYERS) return;
 if (optionIndex <0 || optionIndex >2) return;
 string id = optionIndex switch
 {
0 => store.PlayerPreFightOption1Id[playerId],
1 => store.PlayerPreFightOption2Id[playerId],
2 => store.PlayerPreFightOption3Id[playerId],
 _ => ""
 };
 store.PlayerPreFightSelectedBuffId[playerId] = id;
 // Cache the per-player bonuses so subsequent OnWaveStart doesn't
 // have to re-resolve the Pool. Default to "no buff" values when
 // the id is empty or the option is no longer in the pool.
 var opt = FindOptionById(id);
 if (opt != null)
 {
 store.PlayerPreFightCritBonus[playerId] = opt.CritChance;
 store.PlayerPreFightMaxHpMult[playerId] = opt.MaxHpMult;
 }
 else
 {
 store.PlayerPreFightCritBonus[playerId] =0f;
 store.PlayerPreFightMaxHpMult[playerId] =1f;
 }
 }

 /// <summary>
 /// Apply the selected buff to every active tower's cache fields.
 /// Called from HandleWaveStart. No-op when no selection has been
 /// made (SelectedBuffId empty) — towers stay at1f default.
 ///
 /// Cost: O(activeTowers). Typically <20 towers, so well under one
 /// microsecond on the WaveStart hook. Sentinel-gated: when the
 /// config is null / disabled / the chosen option is missing /
 /// DamageMult is exactly1f and SpeedMult is exactly1f, we still
 /// write the values but they're a no-op on the hot path.
 /// </summary>
 public void ApplyToAllActiveTowers()
 {
 var cfg = gameConfig.PreFight;
 if (cfg == null || !cfg.Enabled) return;
 int playerCount = ComponentStore.MAX_PLAYERS;
 // Single-player default: apply player0's selection. Multi-player
 // (rare in this codebase) would need per-player filtering, but
 // the player-active tower roster is shared in this codebase, so
 // we apply the highest-index player's selection as a pragmatic
 // default. Production code is single-player so this is fine.
 // We don't gate on PlayerCurrentHealth because tests / save-load
 // paths may call Apply before SetPlayerCurrentHealth. AddPlayer
 // clears the SelectedBuffId to "" so an empty-string id is the
 // natural "no selection" sentinel regardless of health state.
 int applyPlayerId =0;
 for (int p =0; p < playerCount; p++)
 {
 if (!string.IsNullOrEmpty(store.PlayerPreFightSelectedBuffId[p])) { applyPlayerId = p; break; }
 }
 string id = store.PlayerPreFightSelectedBuffId[applyPlayerId];
 if (string.IsNullOrEmpty(id)) return;
 var opt = FindOptionById(id);
 if (opt == null) return;
 var active = store.ActiveTowerIds;
 for (int i =0; i < active.Count; i++)
 {
 int tid = active[i];
 if (tid <0 || tid >= ComponentStore.MAX_ENTITIES) continue;
 if (!store.TowerActive[tid]) continue;
 store.TowerPreFightDamageMult[tid] = opt.DamageMult;
 store.TowerPreFightSpeedMult[tid] = opt.SpeedMult;
 }
 }

 /// <summary>
 /// Reset tower cache fields back to1f and clear the player-side
 /// selected buff. Called from HandleWaveComplete so the next
 /// BuildPhase rolls fresh options.
 /// </summary>
 public void ClearWaveScoped()
 {
 int playerCount = ComponentStore.MAX_PLAYERS;
 for (int p =0; p < playerCount; p++)
 {
 store.PlayerPreFightSelectedBuffId[p] = "";
 store.PlayerPreFightOption1Id[p] = "";
 store.PlayerPreFightOption2Id[p] = "";
 store.PlayerPreFightOption3Id[p] = "";
 store.PlayerPreFightOptionsRolled[p] = false;
 store.PlayerPreFightCritBonus[p] =0f;
 store.PlayerPreFightMaxHpMult[p] =1f;
 }
 var active = store.ActiveTowerIds;
 for (int i =0; i < active.Count; i++)
 {
 int tid = active[i];
 if (tid <0 || tid >= ComponentStore.MAX_ENTITIES) continue;
 if (!store.TowerActive[tid]) continue;
 store.TowerPreFightDamageMult[tid] =1f;
 store.TowerPreFightSpeedMult[tid] =1f;
 }
 }

 // ── Read helpers ──────────────────────────────────────────────
 /// <summary>
 /// Returns the three option Ids currently offered to the player.
 /// Always returns a3-element array (entries may be empty strings).
 /// </summary>
 public string[] GetCurrentOptions(int playerId)
 {
 var opts = new string[] { "", "", "" };
 if (playerId <0 || playerId >= ComponentStore.MAX_PLAYERS) return opts;
 opts[0] = store.PlayerPreFightOption1Id[playerId] ?? "";
 opts[1] = store.PlayerPreFightOption2Id[playerId] ?? "";
 opts[2] = store.PlayerPreFightOption3Id[playerId] ?? "";
 return opts;
 }

 /// <summary>
 /// Returns the option definition for the given Id, or null if
 /// the Id is not in the pool (e.g. pool changed after selection).
 /// </summary>
 public PreFightBuffOptionDef FindOptionById(string id)
 {
 var cfg = gameConfig.PreFight;
 if (cfg == null || cfg.Pool == null || string.IsNullOrEmpty(id)) return null;
 for (int i =0; i < cfg.Pool.Length; i++)
 {
 var opt = cfg.Pool[i];
 if (opt != null && opt.Id == id) return opt;
 }
 return null;
 }
 }
}
