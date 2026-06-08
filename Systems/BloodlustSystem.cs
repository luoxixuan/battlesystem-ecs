using System;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
 /// <summary>
 /// Bloodlust System (Round176 Direction2) — per-tower kill-stacking attack-speed / damage buff.
 ///
 /// Lifecycle per frame:
 ///1. Subscribe to store.OnTowerKill in SubscribeToEvents() (one-shot).
 ///2. HandleTowerKill(enemyId, playerId, towerId) increments the killing tower's
 /// TowerBloodlustStacks by1, capped at MaxStacks. Records the tower ID in a
 /// per-frame ledger so Update() can stamp that tower's LastKillTurn.
 ///3. Update(int turn) walks every active tower: if stacks >0 and the elapsed
 /// time since last kill exceeds DecayTurns, shed one stack per DecayTurns elapsed.
 /// Then re-derive the cached damage / speed mults from the live stack count.
 ///
 /// The system does NOT directly modify TowerAttackSystem's behavior — it just
 /// writes the cached TowerBloodlustDamageMult / TowerBloodlustSpeedMult arrays.
 /// TowerAttackSystem.Update reads them inline in the hot path. Sentinel-gated:
 /// when BloodlustConfig.Enabled = false, all mults are forced to0 and the
 /// HandleTowerKill handler is a no-op (so the damage hot path stays cheap).
 ///
 /// Round176 Bug Fix (Claude bug scan):
 /// The original design used a single bool `_anyKillThisFrame` and stamped ALL
 /// active towers' LastKillTurn whenever any tower killed. That meant an idle
 /// tower with stacks would never decay as long as any other tower on the field
 /// was killing — the decay clock got reset by sibling kills. Fixed by tracking
 /// the specific tower(s) that killed this frame in a per-instance List&lt;int&gt;.
 /// </summary>
 public class BloodlustSystem
 {
  private readonly ComponentStore store;
  private readonly GameConfig gameConfig;
  // Idempotency guard against WireDependencies re-init / test reset paths
  // stacking duplicate handlers.
  private bool _subscribed;
  // Per-frame kill ledger: which tower IDs scored kills between the previous
  // Update tick and this one. Drained (cleared) by Update(). Re-using a single
  // List instance keeps the per-kill path allocation-free and bounds the cost
  // to the number of towers that fired kills this frame (typically <5).
  private readonly System.Collections.Generic.List<int> _towerKillThisFrame
  = new System.Collections.Generic.List<int>(16);

  public BloodlustSystem(ComponentStore store, GameConfig gameConfig)
  {
   this.store = store;
   this.gameConfig = gameConfig;
   }

 /// <summary>
 /// Subscribe to OnTowerKill. Called once by SystemRegistry.WireDependencies().
 /// Idempotent: re-subscribing is a no-op so the WireDependencies
 /// reset-test path doesn't stack duplicate handlers.
 /// </summary>
  public void SubscribeToEvents()
  {
   if (_subscribed) return;
   _subscribed = true;
   store.OnTowerKill += HandleTowerKill;
   }

 /// <summary>
 /// OnTowerKill handler: increment the killing tower's stack count by1,
 /// cap at MaxStacks, and record the tower ID in the per-frame kill ledger.
 /// No-op when Enabled is false, config is null, or the tower is invalid.
 /// Runs in serial context (drained from _towerKillQueue inside ComponentStore),
 /// so direct SOA writes to TowerBloodlustStacks are safe.
 /// </summary>
  private void HandleTowerKill(int enemyId, int playerId, int towerId)
  {
   // Sentinel + null guard: cfg may be null after JSON deserialization
   // (property initializers don't run on the deserialization path).
   var cfg = gameConfig.Bloodlust;
   if (cfg == null || !cfg.Enabled || cfg.MaxStacks <=0) return;
   if (towerId <0 || towerId >= ComponentStore.MAX_ENTITIES) return;
   if (!store.TowerActive[towerId]) return;

   int stacks = store.TowerBloodlustStacks[towerId];
   if (stacks < cfg.MaxStacks)
   {
    store.TowerBloodlustStacks[towerId] = stacks +1;
    }
   // Record the killer so Update() stamps its LastKillTurn with the
   // current turn. We don't have the turn here — it's the Update tick
   // that anchors the decay math.
   _towerKillThisFrame.Add(towerId);
   }

 /// <summary>
 /// Per-frame tick. Walks every active tower, sheds decayed stacks, and
 /// re-derives the cached damage / speed multipliers. Sentinel-gated fast
 /// path: when Enabled = false or MaxStacks &lt;=0, only the mults are
 /// force-cleared (one pass through the active list) and we return.
 /// </summary>
  public void Update(int turn)
  {
   var cfg = gameConfig.Bloodlust;
   if (cfg == null || !cfg.Enabled || cfg.MaxStacks <=0)
   {
    // Disabled fast path: clear the cached mults so the TowerAttack
    // hot path sees0f and skips the bonus calculation. We walk
    // active towers only (so cost is O(active) and not O(MAX_ENTITIES)).
    var active = store.ActiveTowerIds;
    for (int i =0; i < active.Count; i++)
    {
     int id = active[i];
     if (id <0 || id >= ComponentStore.MAX_ENTITIES) continue;
     store.TowerBloodlustDamageMult[id] =0f;
     store.TowerBloodlustSpeedMult[id] =0f;
     }
    _towerKillThisFrame.Clear();
    return;
    }

   // Stamp LastKillTurn ONLY for towers that scored kills this frame.
   // (Round176 fix — previously we stamped every active tower with
   // stacks >0, which meant sibling kills reset the decay clock for
   // unrelated towers — making decay never fire as long as any tower
   // on the field was killing.)
   if (_towerKillThisFrame.Count >0)
   {
    for (int i =0; i < _towerKillThisFrame.Count; i++)
    {
     int id = _towerKillThisFrame[i];
     if (id <0 || id >= ComponentStore.MAX_ENTITIES) continue;
     if (!store.TowerActive[id]) continue;
     if (store.TowerBloodlustStacks[id] >0)
     {
      store.TowerBloodlustLastKillTurn[id] = turn;
      }
     }
    _towerKillThisFrame.Clear();
    }

   int decayTurns = cfg.DecayTurns;
   var activeList = store.ActiveTowerIds;
   for (int i =0; i < activeList.Count; i++)
   {
    int id = activeList[i];
    if (id <0 || id >= ComponentStore.MAX_ENTITIES) continue;
    if (!store.TowerActive[id]) continue;

    int stacks = store.TowerBloodlustStacks[id];
    if (stacks >0 && decayTurns >0)
    {
     int lastTurn = store.TowerBloodlustLastKillTurn[id];
     int elapsed = turn - lastTurn;
     // Shed one stack per DecayTurns window. Uses integer division
     // so299 elapsed frames with300 decay =0 stacks shed;600
     // elapsed =2 stacks shed. Clamped to stacks so we never go
     // negative.
     int shed = Math.Min(stacks, elapsed / decayTurns);
     if (shed >0)
     {
      stacks -= shed;
      store.TowerBloodlustStacks[id] = stacks;
      // Re-anchor last-kill-turn to the new "first stack was gained"
      // moment so the next shed happens DecayTurns from now.
      store.TowerBloodlustLastKillTurn[id] = turn;
      }
     }

    // Re-derive cached multipliers. Both are0 when stacks ==0, so
    // a tower that just decayed back to0 contributes0 to the hot
    // path next frame.
    int currentStacks = store.TowerBloodlustStacks[id];
    if (currentStacks >0)
    {
     store.TowerBloodlustDamageMult[id] = currentStacks * cfg.DamagePerStack;
     store.TowerBloodlustSpeedMult[id] = currentStacks * cfg.SpeedPerStack;
     }
    else
    {
     store.TowerBloodlustDamageMult[id] =0f;
     store.TowerBloodlustSpeedMult[id] =0f;
     }
    }
   }
  }
 }
