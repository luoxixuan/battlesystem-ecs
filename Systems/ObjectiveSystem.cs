using System;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Objective System — manages alternate win/lose conditions per level.
    /// 
    /// Supports:
    ///   - KillAll (default): win when all waves cleared
    ///   - Escort: protect an NPC that moves toward the exit; lose if NPC dies
    ///   - Survival: survive N waves (no kill requirement)
    ///   - Timed: eliminate all enemies within a time limit
    ///   - Endless: survive as many waves as possible; score = waves cleared
    /// 
    /// Phase gates:
    ///   BuildPhase: update escort NPC movement, timer ticking
    ///   WavePhase:  update objective progress, check win/lose conditions
    /// </summary>
    public class ObjectiveSystem
 {
 private readonly ComponentStore _store;
 private readonly int _playerId;
 // Round110 Direction10 — cached LevelConfig so CheckObjective can
 // compute DoomClock final score (needs DoomClockWaveScore / TimeBonusPerSec
 // / HealthBonusPerPercent tunables) without a separate parameter.
 private Config.LevelConfig _currentLevel;
 // Round201 Direction7 — optional EventBus for SideQuestCompleted publish.
 // Null is OK (no subscribers → no publish cost).
 private readonly Core.EventBus _eventBus;

 public ObjectiveSystem(ComponentStore store, int playerId =0, Core.EventBus eventBus = null)
 {
 _store = store ?? throw new ArgumentNullException(nameof(store));
 _playerId = playerId;
 _eventBus = eventBus;
 }

        /// <summary>
        /// Initialize objective state from level config. Called once per level load.
        /// Round 110 Direction 10: also seeds DoomClockSystem state when the
        /// DoomClock objective is active. DoomClock initialization is idempotent —
        /// repeated calls reset the timer / counters rather than accumulating.
        /// </summary>
        public void InitializeFromLevel(Config.LevelConfig level, int mapHeight)
        {
            // Round 110 Direction 10 — cache the level so CheckObjective can read
            // DoomClock scoring tunables when the run wins (no extra parameter).
            _currentLevel = level;
            _store.CurrentObjectiveType[_playerId] = level.ObjectiveType;

            if (level.ObjectiveType == (int)ObjectiveType.Escort)
            {
                // Spawn escort NPC at start position (x=0, halfway up the map)
                _store.EscortNpcX[_playerId] = 0f;
                _store.EscortNpcY[_playerId] = mapHeight / 2f;
                _store.EscortNpcMaxHealth[_playerId] = level.EscortNpcMaxHealth;
                _store.EscortNpcHealth[_playerId] = level.EscortNpcMaxHealth;
                _store.EscortNpcSpeed[_playerId] = level.EscortNpcSpeed;
                _store.EscortNpcActive[_playerId] = true;
            }
            else
            {
                _store.EscortNpcActive[_playerId] = false;
            }

            if (level.ObjectiveType == (int)ObjectiveType.Timed)
            {
                _store.ObjectiveTimer[_playerId] = level.ObjectiveTimeLimit;
                _store.ObjectiveTimeLimit[_playerId] = level.ObjectiveTimeLimit;
            }
            else
            {
                _store.ObjectiveTimer[_playerId] = 0f;
            }

            if (level.ObjectiveType == (int)ObjectiveType.Survival)
            {
                _store.ObjectiveWavesRemaining[_playerId] = level.SurvivalWaveCount;
            }
            else
            {
                _store.ObjectiveWavesRemaining[_playerId] = 0;
            }

            // Reset score tracking
            _store.ObjectiveWaveScore[_playerId] = 0;
            _store.ObjectiveHealthScore[_playerId] = 0f;

            // Round 110 Direction 10 — DoomClock state seed/reset.
            // DoomClockSystem reuses the wave spawn flow (it just adds a
            // countdown and a final score), so the regular Objectives init
            // handles wave data; the DoomClock-specific fields live on the
            // DoomClockSystem helper. We invalidate stale final scores here
            // so a level reload doesn't carry over the previous run's number.
            if (level.ObjectiveType == (int)ObjectiveType.DoomClock)
            {
                _store.DoomClockActive[_playerId] = true;
                _store.DoomClockTimer[_playerId] = level.DoomClockDuration;
                _store.DoomClockDuration[_playerId] = level.DoomClockDuration;
                _store.DoomClockWavesCleared[_playerId] = 0;
                _store.DoomClockCycleCount[_playerId] = 0;
                _store.DoomClockFinalScore[_playerId] = 0;
            }
            else
 {
 _store.DoomClockActive[_playerId] = false;
 _store.DoomClockFinalScore[_playerId] =0;
 }

 // Round201 Direction7 — subscribe to kill events for side quest kill-counting.
 // The handler is stored as a field so it can be unsubscribed on the next
 // InitializeFromLevel call (defensive: level reload shouldn't double-subscribe).
 _store.OnEnemyKilled -= OnEnemyKilledForSideQuest;
 _store.OnEnemyKilled += OnEnemyKilledForSideQuest;
 }

        /// <summary>
        /// Per-frame tick — update escort movement and objective timers.
        /// Called every frame regardless of phase (movement is real-time).
        /// Round 110 Direction 10: also ticks the DoomClock countdown during
        /// WavePhase. The DoomClock timer stops at 0 — the objective check
        /// is what actually fires the win condition (timer=0 && player alive).
        /// </summary>
        public void Update(float deltaTime, GameState phase)
 {
 var objType = (ObjectiveType)_store.CurrentObjectiveType[_playerId];

 if (objType == ObjectiveType.Escort)
 {
 UpdateEscort(deltaTime);
 }

 if (objType == ObjectiveType.Timed && phase == GameState.WavePhase)
 {
 UpdateTimed(deltaTime);
 }

 if (objType == ObjectiveType.DoomClock && phase == GameState.WavePhase)
 {
 UpdateDoomClock(deltaTime);
 }

 // Round201 Direction7 — accumulate run-elapsed time during WavePhase so the
 // Speed side quest can read it at run end. Only counts wave-active time so
 // build phases don't pad the clock (matches the player-visible "objective
 // clock" intuition).
 if (phase == GameState.WavePhase)
 {
 _store.PlayerRunElapsedTime[_playerId] += deltaTime;
 }
 }

        /// <summary>
        /// WavePhase-specific update — check conditions and advance progress.
        /// </summary>
        public void UpdateWavePhase(float deltaTime, int currentWave, int totalWaves)
        {
            var objType = (ObjectiveType)_store.CurrentObjectiveType[_playerId];

            if (objType == ObjectiveType.Survival)
            {
                // Survival: don't care about kills, just survive the required wave count.
                // Wave complete notification comes from WaveSpawningSystem.
            }

            if (objType == ObjectiveType.Endless)
            {
                // Track current wave for scoring
                _store.ObjectiveWaveScore[_playerId] = currentWave;
            }
        }

        /// <summary>
        /// Called when a wave is completed (all enemies killed or timeout).
        /// Returns true if the game should continue, false if objective is satisfied.
        /// Round 110 Direction 10: for DoomClock, every wave completion increments
        /// the cleared counter (used for final score). Game continues regardless
        /// (the timer is the only win condition).
        /// </summary>
        public bool OnWaveCompleted(int wavesRemaining)
        {
            var objType = (ObjectiveType)_store.CurrentObjectiveType[_playerId];

            if (objType == ObjectiveType.Survival)
            {
                _store.ObjectiveWavesRemaining[_playerId] = wavesRemaining;
                if (wavesRemaining <= 0)
                {
                    // Player survived all required waves — objective complete!
                    return false;
                }
            }

            if (objType == ObjectiveType.DoomClock && _store.DoomClockActive[_playerId])
            {
                // Increment cleared-wave counter. DoomClock continues regardless.
                _store.DoomClockWavesCleared[_playerId]++;
            }

            return true; // continue
        }

        /// <summary>
        /// Called when an escort NPC takes damage.
        /// </summary>
        public void DamageEscortNpc(float damage)
        {
            if (!_store.EscortNpcActive[_playerId]) return;

            _store.EscortNpcHealth[_playerId] -= damage;
            if (_store.EscortNpcHealth[_playerId] <= 0f)
            {
                _store.EscortNpcHealth[_playerId] = 0f;
                _store.EscortNpcActive[_playerId] = false;
            }
        }

        /// <summary>
        /// Check if the current objective has been won or lost.
        /// Returns: 0 = ongoing, 1 = won, -1 = lost
        /// </summary>
        public int CheckObjective(int activeEnemyCount, int currentWave, int totalWaves)
        {
            var objType = (ObjectiveType)_store.CurrentObjectiveType[_playerId];

            switch (objType)
 {
 case ObjectiveType.KillAll:
 // Win when all waves done and no enemies remain
 if (currentWave > totalWaves && activeEnemyCount ==0)
 {
 EvaluateSideQuestsOnRunEnd();
 return 1;
 }
 break;

 case ObjectiveType.Escort:
 // Lose if NPC dies
 if (!_store.EscortNpcActive[_playerId])
 {
 EvaluateSideQuestsOnRunEnd();
 return -1;
 }
 // Win if NPC reaches far side of map (x >= map width)
 // We don't have map width here — check health is >0 and wave complete
 if (_store.EscortNpcActive[_playerId] && currentWave > totalWaves && activeEnemyCount ==0)
 {
 EvaluateSideQuestsOnRunEnd();
 return 1;
 }
 break;

 case ObjectiveType.Survival:
 // Win when all survival waves cleared (no kill requirement)
 if (currentWave > totalWaves && activeEnemyCount ==0)
 {
 EvaluateSideQuestsOnRunEnd();
 return 1;
 }
 break;

 case ObjectiveType.Timed:
 // Win when timer expires with enemies remaining
 // Lose if all enemies killed before timer expires
 if (activeEnemyCount ==0)
 {
 EvaluateSideQuestsOnRunEnd();
 return 1; // cleared early = win
 }
 if (_store.ObjectiveTimer[_playerId] <=0f && activeEnemyCount >0)
 {
 EvaluateSideQuestsOnRunEnd();
 return -1; // time ran out with enemies still alive
 }
 break;

 case ObjectiveType.Endless:
 // No win condition — runs until player loses (lives =0 or escort dies)
 // Score is tracked in ObjectiveWaveScore
 break;

 case ObjectiveType.DoomClock:
 // Round110 Direction10:
 // - Win when the countdown hits0 AND no enemies remain on the field.
 // (We require the wave to be cleared, not just the timer to expire,
 // so the player actually finishes the current fight cleanly.)
 // - If the timer hits0 but enemies are still alive, we keep waiting
 // (return 0;) — the wave is still winnable. The clock already expired
 // and the score bonus for time-remaining will simply be0.
 // - Lose is reported by the game-over path (lives =0) before this
 // function is reached, so we don't need to handle a -1 here.
 if (!_store.DoomClockActive[_playerId]) break; // run already ended
 if (_store.DoomClockTimer[_playerId] <=0f && activeEnemyCount ==0)
 {
 // Round110 Direction10 — compute and persist final score
 // before returning win. Uses cached _currentLevel tunables;
 // playerHealthFraction is read from the store. EndRun also
 // flips DoomClockActive=false so subsequent calls no-op.
 float maxHp = _store.PlayerMaxHealth[_playerId];
 float curHp = _store.PlayerCurrentHealth[_playerId];
 float frac = maxHp >0f ? (curHp / maxHp) :0f;
 if (frac <0f) frac =0f;
 if (frac >1f) frac =1f;
 int waveBonus = _store.DoomClockWavesCleared[_playerId] * (_currentLevel?.DoomClockWaveScore ??100);
 int timeBonus = (int)(_store.DoomClockTimer[_playerId] * (_currentLevel?.DoomClockTimeBonusPerSec ??10));
 int healthBonus = (int)(frac *100f) * (_currentLevel?.DoomClockHealthBonusPerPercent ??5);
 _store.DoomClockFinalScore[_playerId] = waveBonus + timeBonus + healthBonus;
 _store.DoomClockActive[_playerId] = false;
 EvaluateSideQuestsOnRunEnd();
 return 1; // survived the clock + cleared the wave = win
 }
 break;
 }

 return 0; // ongoing
 }

        /// <summary>
        /// Get current objective progress as a formatted string for UI.
        /// </summary>
        public string GetObjectiveStatus()
        {
            var objType = (ObjectiveType)_store.CurrentObjectiveType[_playerId];

            switch (objType)
            {
                case ObjectiveType.Escort:
                    var hp = _store.EscortNpcHealth[_playerId];
                    var maxHp = _store.EscortNpcMaxHealth[_playerId];
                    var active = _store.EscortNpcActive[_playerId] ? "ALIVE" : "DEAD";
                    return $"[ESCORT] NPC HP: {hp:F0}/{maxHp:F0} ({active})";

                case ObjectiveType.Survival:
                    var waves = _store.ObjectiveWavesRemaining[_playerId];
                    return $"[SURVIVAL] Waves remaining: {waves}";

                case ObjectiveType.Timed:
                    var timer = _store.ObjectiveTimer[_playerId];
                    return $"[TIMED] Time remaining: {timer:F1}s";

                case ObjectiveType.Endless:
                    var score = _store.ObjectiveWaveScore[_playerId];
                    return $"[ENDLESS] Waves survived: {score}";

                case ObjectiveType.DoomClock:
                    // Round 110 Direction 10: HUD readout combines the countdown,
                    // the cleared-wave count (used for final score) and the cycle
                    // number (0 = first pass through the wave pool, 1+ = wrapped).
                    if (!_store.DoomClockActive[_playerId])
                    {
                        int final = _store.DoomClockFinalScore[_playerId];
                        return final > 0
                            ? $"[DOOM CLOCK] FINAL SCORE: {final}"
                            : "[DOOM CLOCK] (ended)";
                    }
                    var dcTimer = _store.DoomClockTimer[_playerId];
                    var dcWaves = _store.DoomClockWavesCleared[_playerId];
                    var dcCycle = _store.DoomClockCycleCount[_playerId];
                    return $"[DOOM CLOCK] Time: {dcTimer:F1}s | Waves: {dcWaves} | Cycle: {dcCycle}";

                default:
                    return "[KILLALL] Defeat all enemies";
            }
        }

        // ── Private helpers ─────────────────────────────────────────────────────

        private void UpdateEscort(float deltaTime)
        {
            if (!_store.EscortNpcActive[_playerId]) return;

            // Move NPC rightward at constant speed
            // Map width is not stored in store — we approximate with a large constant
            // The escort is treated as having reached the goal when the wave is cleared
            _store.EscortNpcX[_playerId] += _store.EscortNpcSpeed[_playerId] * deltaTime;
        }

        private void UpdateTimed(float deltaTime)
 {
 if (_store.ObjectiveTimer[_playerId] >0f)
 {
 _store.ObjectiveTimer[_playerId] -= deltaTime;
 if (_store.ObjectiveTimer[_playerId] <0f)
 _store.ObjectiveTimer[_playerId] =0f;
 }
 }

 // ── Side Quest evaluation (Round201 Direction7) ─────────────────────
 // Called once when the main objective transitions to a terminal state (won/lost).
 // Walks the level's SideQuests list and checks each quest against current state.
 // Completes any quest that crosses its threshold, latches it, credits rewards,
 // and publishes SideQuestCompleted via EventBus. Main objective outcome does not
 // affect side quest eligibility — e.g. a player who loses (lives=0) still gets
 // their kill-count or no-heal quest if they crossed the threshold.
 private void EvaluateSideQuestsOnRunEnd()
 {
 var level = _currentLevel;
 if (level == null) return;
 var quests = level.SideQuests;
 if (quests == null || quests.Count ==0) return; // fast path

 int lives = _store.PlayerBaseLives != null && _playerId < _store.PlayerBaseLives.Length
 ? _store.PlayerBaseLives[_playerId]
 :0;
 int towersPlaced = _store.PlayerTowerCount != null && _playerId < _store.PlayerTowerCount.Length
 ? _store.PlayerTowerCount[_playerId]
 :0;
 float elapsed = _store.PlayerRunElapsedTime[_playerId];

 for (int i =0; i < quests.Count && i < ComponentStore.MAX_SIDE_QUESTS; i++)
 {
 if (_store.PlayerSideQuestCompleted[_playerId * ComponentStore.MAX_SIDE_QUESTS + i])
 continue; // already done — latch prevents re-fire

 var quest = quests[i];
 bool done = false;
 switch (quest.Type)
 {
  case 0: // KillCount
  done = _store.PlayerSideQuestProgress[_playerId * ComponentStore.MAX_SIDE_QUESTS + i] >= quest.Threshold;
  break;
  case 1: // NoDeath — Threshold >=1 means ≥1 life remaining
  done = lives >= Math.Max(1, quest.Threshold);
  break;
  case 2: // Speed — finished within TimeLimit seconds
  done = elapsed <= quest.TimeLimit && quest.TimeLimit >0f;
  break;
  case 3: // MinimalTowers — at most Threshold towers placed
  done = towersPlaced <= quest.Threshold;
  break;
  case 4: // NoHeal — placeholder (healing spending tracker TBD; treat as no-op for now)
  done = false;
  break;
 }

 if (!done) continue;
 _store.PlayerSideQuestCompleted[_playerId * ComponentStore.MAX_SIDE_QUESTS + i] = true;
 if (quest.GoldReward >0)
 _store.ApplyPlayerResourceAuthority(_playerId, _playerId, new Core.GAS.AttributeKey(4), quest.GoldReward);
 if (quest.SoulReward >0 && _store.PlayerSoulCount != null && _playerId < _store.PlayerSoulCount.Length)
 _store.PlayerSoulCount[_playerId] += quest.SoulReward;

 _eventBus?.SideQuestCompleted.Publish(new SideQuestCompletedEvent
 {
 PlayerId = _playerId,
 QuestId = quest.Id,
 Type = quest.Type,
 GoldReward = quest.GoldReward,
 SoulReward = quest.SoulReward,
 });
 }
 }

 /// <summary>
 /// Side-quest kill-event handler (Round201 Direction7). Increments the per-slot
 /// progress counter for any KillCount (Type=0) quest, then evaluates completion.
 /// PlayerId filter: only advance progress for our player to avoid cross-player
 /// quest pollution in multi-player tests.
 /// </summary>
 private void OnEnemyKilledForSideQuest(int enemyId, int playerId)
 {
 if (playerId != _playerId) return;
 var level = _currentLevel;
 if (level == null) return;
 var quests = level.SideQuests;
 if (quests == null || quests.Count ==0) return;

 for (int i =0; i < quests.Count && i < ComponentStore.MAX_SIDE_QUESTS; i++)
 {
 if (quests[i].Type !=0) continue; // only KillCount tracks via per-kill hook
 if (_store.PlayerSideQuestCompleted[_playerId * ComponentStore.MAX_SIDE_QUESTS + i]) continue;
 // Cap progress at int.MaxValue-1 to mirror PlayerWaveKillCount overflow safety.
 int slot = _playerId * ComponentStore.MAX_SIDE_QUESTS + i;
 int cur = _store.PlayerSideQuestProgress[slot];
 if (cur < int.MaxValue -1)
 _store.PlayerSideQuestProgress[slot] = cur +1;
 // Don't check threshold here — let the next OnWaveCompleted / CheckObjective
 // call handle the completion (publishes the event in a single canonical place).
 }
 }

 /// <summary>
 /// Get the completion progress for a side quest slot (0..count-1).
 /// Returns (current, threshold, completed) for HUD display.
 /// </summary>
 public (int current, int threshold, bool completed) GetSideQuestProgress(int questIndex)
 {
 var level = _currentLevel;
 if (level == null || questIndex <0 || questIndex >= level.SideQuests.Count)
 return (0,0, false);
 int slot = _playerId * ComponentStore.MAX_SIDE_QUESTS + questIndex;
 return (
 _store.PlayerSideQuestProgress[slot],
 level.SideQuests[questIndex].Threshold,
 _store.PlayerSideQuestCompleted[slot]
 );
 }

 /// <summary>
 /// Public hook to evaluate side quests at any time (Round201 Direction7).
 /// CheckObjective calls this internally when the main objective transitions
 /// to a terminal state; tests can call it directly to evaluate without
 /// driving a full wave-completion flow. Idempotent — completed quests are
 /// latched so a second call is a no-op.
 /// </summary>
 public void EvaluateSideQuestsNow() => EvaluateSideQuestsOnRunEnd();

/// <summary>
/// Unsubscribe from ComponentStore.OnEnemyKilled — Round201 Direction7.
/// Call before discarding an ObjectiveSystem instance to prevent the event
/// delegate from holding a stale reference (memory leak / ghost handler).
/// </summary>
public void Unsubscribe()
{
 _store.OnEnemyKilled -= OnEnemyKilledForSideQuest;
}

 /// <summary>
 /// DoomClock countdown — Round110 Direction10. Decrements the global
 /// timer each frame during WavePhase. Stops at0 (the win check in
 /// CheckObjective is what actually fires the win). Mirror of UpdateTimed
 /// but on the DoomClock-specific timer field, with an `Active` guard so
 /// the loop short-circuits once the run is over.
 /// </summary>
 private void UpdateDoomClock(float deltaTime)
        {
            if (!_store.DoomClockActive[_playerId]) return;
            float t = _store.DoomClockTimer[_playerId];
            if (t <= 0f) return;
            t -= deltaTime;
            if (t < 0f) t = 0f;
            _store.DoomClockTimer[_playerId] = t;
        }
    }
}
