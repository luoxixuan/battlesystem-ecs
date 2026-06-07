using BattleSystemECS.Components;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// Canonical event type constants used across the game.
    /// Only events that have at least one publisher are declared here.
    /// </summary>
    public static class GameEvents
    {
        // Actively published events (verified against all Subscribe/Publish call sites)
        public const string PlayerDamaged         = "player_damaged";
        public const string EnemyCharging         = "enemy_charging";
        public const string EnemyChargeReleased   = "enemy_charge_released";
        // On-Hit / On-Crit trigger infrastructure (Round 67)
        // Published in the serial damage-apply phase of PlayerTowerAttackSystem / TowerAttackSystem.
        // Payload fields: EnemyId, AttackerId (playerId or towerId; towerId<0 means player attack),
        // Damage, IsCrit. Subscribers can implement affix logic (heal on crit, slow on crit, etc).
        public const string EnemyHit              = "enemy_hit";
        public const string EnemyCrit             = "enemy_crit";
        // Boss phase transition event (Round 129 Direction 2). Published in the serial end-of-Update
        // drain phase of EnemyAISystem whenever a boss enemy crosses a phase threshold
        // (HP-fraction based; one-shot per phase, gated by EnemyPhaseFiredMask). Payload fields:
        // EnemyId, BossTypeName, OldPhase, NewPhase, HealthFraction, Turn. Subscribers can drive
        // boss mechanic changes (music swap, AoE warning, dialogue, telemetry, etc) without
        // tightly coupling to EnemyAISystem internals.
        public const string BossPhaseChanged = "boss_phase_changed";
 // Side quest completion event — Round201 Direction7. Published by ObjectiveSystem
 // whenever a side quest flips from in-progress to completed (one-shot per quest,
 // gated by PlayerSideQuestCompleted latch). Payload fields: PlayerId, QuestId,
 // Type (0..4, see SideQuestDef), GoldReward, SoulReward. Subscribers can drive
 // reward VFX, sound, achievement telemetry, etc without coupling to the system.
 public const string SideQuestCompleted = "side_quest_completed";
    }

    // ── Event Data Transfer Objects ──
    // Only DTOs that are actually instantiated in the codebase are kept.

    public class PlayerDamagedEvent
    {
        public float Damage;
        public float RemainingHealth;
        public int AttackerId;
    }

    public class EnemyChargingEvent
    {
        public int EnemyId;
        public int Turn;
        public float Damage;
    }

    public class EnemyChargeReleasedEvent
    {
        public int EnemyId;
        public int Turn;
        public float Damage;
    }

    public class ElementalReactionEvent
    {
        public int EnemyId;
        public int SourceEntityId;
        public ElementalReactionType ReactionType;
        public float Damage;
    }

    // On-Hit / On-Crit event DTOs (Round 67 — OnCrit/OnHitTaken trigger infrastructure)
    // EnemyHit fires for every successful damage instance applied to an enemy.
    // EnemyCrit is a *companion* event that ALSO fires when the hit was a crit
    // (handlers can subscribe to one or both). Keeping them as a pair lets
    // simple affix code subscribe to EnemyCrit only without filtering on IsCrit,
    // and lets OnHitTaken (e.g. "when struck, reflect X") use EnemyHit.
    //
    // AttackerKind: 0 = player attack, 1 = tower attack. TowerId is only valid
    // when AttackerKind == 1; PlayerId is only valid when AttackerKind == 0.
    public class EnemyHitEvent
    {
        public int EnemyId;
        public int AttackerId;       // playerId (AttackerKind=0) or towerId (AttackerKind=1)
        public byte AttackerKind;    // 0=player, 1=tower
        public float Damage;         // raw damage applied (post-resistance, pre-cap)
        public bool IsCrit;          // true if the hit rolled as a critical strike
    }

    // BossPhaseChanged event DTO (Round 129 Direction 2). Published by EnemyAISystem
    // whenever a boss enemy crosses a phase threshold. HealthFraction is the boss's HP
    // fraction AT THE TIME OF TRANSITION (i.e. just below the threshold). Turn is the
    // current frame/turn counter from EnemyAISystem.SetTurn.
    public class BossPhaseChangedEvent
 {
 public int EnemyId;
 public string BossTypeName; // monsterConfig.Type for the boss (e.g. "Dragon", "Lich")
 public int OldPhase; //0-indexed; phase1 =0
 public int NewPhase; //0-indexed; NewPhase > OldPhase
 public float HealthFraction; // enemyHealth / enemyMaxHealth at transition
 public int Turn; // game turn when transition fired
 }

 // SideQuestCompleted event DTO (Round201 Direction7). Published by ObjectiveSystem
 // on quest completion. QuestId is the string Id from SideQuestDef (e.g. "kill_30");
 // Type is the numeric quest type (0..4) for subscribers that key on type rather
 // than Id. GoldReward / SoulReward are the values granted to the player; subscribers
 // can replay them on UI but the system itself has already credited PlayerGold +
 // PlayerSoulCount at publish time.
 public class SideQuestCompletedEvent
 {
 public int PlayerId;
 public string QuestId;
 public int Type;
 public int GoldReward;
 public int SoulReward;
 }
}
