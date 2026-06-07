using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BattleSystemECS.Components;
using BattleSystemECS.Systems;
using BattleSystemECS.Core;

namespace BattleSystemECS.Config
{
    public class PlayerConfig
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public float AttackRange { get; set; }
        public float AttackSpeed { get; set; }
        public float AttackInterval { get; set; }
        public float AttackDamage { get; set; }
        public float MaxHealth { get; set; }
        public int StartingLives { get; set; } = 10;  // 初始基地生命数（漏怪次数上限）
        public int CurrentLevel { get; set; }
        public float UpgradeThreshold { get; set; }
        public List<string> StartingSkills { get; set; } = new List<string>();
        // ReincarnationCharges: number of one-time auto-revives on player death (default 0 = disabled).
        // 1 = classic "one-time save". Each revive restores HP to ReincarnationHealFraction * MaxHP.
        public int ReincarnationCharges { get; set; } = 0;
        // ReincarnationHealFraction: HP fraction (0-1) restored on reincarnation. Default 0.5 (50% MaxHP).
        public float ReincarnationHealFraction { get; set; } = 0.5f;
    }

    public class MonsterConfig
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public float Health { get; set; }
        public float MaxHealth { get; set; }
        public float Damage { get; set; }
        public float MoveSpeed { get; set; }
        public float AttackRange { get; set; }
        public float AttackInterval { get; set; }
        public int GoldReward { get; set; }
        public List<string> Skills { get; set; } = new List<string>();
        // Armor: reduces incoming damage. Tank/Elite/Boss types get high armor (5-15),
        // Normal/Fast types get low armor (0-2). Affected by attacker's armor penetration.
        public float Armor { get; set; } = 0f;
        // MagicResist: reduces incoming Magic damage (0.0-1.0 fraction reduction).
        // Separate from armor. Physical ignores magic resist, Magic ignores armor.
        public float MagicResist { get; set; } = 0f;
        // Elemental Resistance (Round 117): fractional reduction for Fire / Ice / Lightning damage (0.0-1.0).
        // Distinct from DamageImmunities (binary 0% or 100%) — here we allow partial resists like 30% / 70%.
        // Default 0 = no resist (take full damage). Negative values are clamped to 0; values >1 are clamped to 1.
        // True damage bypasses all four; Physical and Magic damage ignore these arrays.
        public float FireResist { get; set; } = 0f;
        public float IceResist { get; set; } = 0f;
        public float LightningResist { get; set; } = 0f;
        // Holy Resistance (Round 135 Direction 1): fractional reduction for Holy / Smite / Divine damage (0.0-1.0).
        // Same semantics as FireResist/IceResist/LightningResist. Demons get high HolyResist (e.g. 0.5);
        // Undead typically have 0 (vulnerable to Holy). Default 0 = no resist (take full Holy damage).
        public float HolyResist { get; set; } = 0f;
        // Round 137 Dir 6 — Themed Boss Summon. ElementAffinity declares this monster's element
        // ("Fire" / "Ice" / "Lightning" / "Poison" / ""). When a boss with a matching
        // BossElementAffinity summons this minion, the minion gets +10% HP (themed resonance).
        // Empty string (default) = no affinity = no bonus. Compared case-insensitively.
        // Distinct from FireResist/IceResist (those are damage reduction; this is summon synergy).
        public string ElementAffinity { get; set; } = "";
        // DamageImmunities: list of damage type names this enemy is fully immune to
        // (binary, not percentage). Valid entries: "Physical", "Magic", "Fire", "Ice", "Lightning", "Holy".
        // True damage bypasses immunity. Empty/null = no immunities.
        public List<string> DamageImmunities { get; set; } = new List<string>();
        /// <summary>
        /// Computes the bit mask of damage immunities from the DamageImmunities list.
        /// Returns 0 if list is null/empty. Unknown names are silently ignored.
        /// </summary>
        public int ComputeDamageImmunityMask()
        {
            if (DamageImmunities == null || DamageImmunities.Count == 0) return 0;
            int mask = 0;
            for (int i = 0; i < DamageImmunities.Count; i++)
            {
                string name = DamageImmunities[i];
                if (string.IsNullOrEmpty(name)) continue;
                switch (name)
                {
                    case "Physical":  mask |= (int)DamageType.Physical;  break;
                    case "Magic":     mask |= (int)DamageType.Magic;     break;
                    case "Fire":      mask |= (int)DamageType.Fire;      break;
                    case "Ice":       mask |= (int)DamageType.Ice;       break;
                    case "Lightning": mask |= (int)DamageType.Lightning; break;
                    case "Holy":      mask |= (int)DamageType.Holy;      break;  // Round 135 Dir 1
                    // True damage is never immuned — intentionally omitted.
                }
            }
            return mask;
        }
        // Shield: absorbs incoming damage before health. Boss/Elite types can have shield.
        public float Shield { get; set; } = 0f;
        // ShieldElement: which element this shield is weak to ("Fire"/"Ice"/"Lightning"/"Poison"/"" or null).
        // Empty/null = no elemental interaction (plain numeric shield, same as Shield field).
        public string ShieldElement { get; set; }
        // ShieldWeakMult: damage multiplier to shield when hit by matching element (default 2.0).
        public float ShieldWeakMult { get; set; } = 0f; // 0 = use default 2x
        // ShieldResistMult: damage multiplier to shield when hit by non-matching element (default 0.5).
        public float ShieldResistMult { get; set; } = 0f; // 0 = use default 0.5x
        // ShieldBreakReaction: which element is applied to the enemy on shield break.
        public string ShieldBreakReaction { get; set; }
        // ShieldBreakElementDuration: how long the break-reaction element lasts in seconds.
        public float ShieldBreakElementDuration { get; set; } = 0f; // 0 = use default 2s
        // HitShieldCount: number of N-hit shield layers (0 = none). Blocks that many attacks completely.
        public float HitShieldCount { get; set; } = 0f;
        // HitShieldRegenInterval: seconds between layer regen ticks (0 = no regen).
        public float HitShieldRegenInterval { get; set; } = 0f;
        // HealthRegenPerSec: HP/sec natural regen rate (0 = no regen). Designed for boss / elite
        // enemies that need a "breathing window" mechanic — e.g. a boss entering Phase 2 heals
        // for X% of maxHP per second for N seconds, forcing the player to land damage inside a
        // narrow DPS window. Default 0 keeps every existing monster config at zero overhead.
        public float HealthRegenPerSec { get; set; } = 0f;
        // PhaseRegenMult: per-phase multiplier on HealthRegenPerSec, indexed by phase index
        // (0 = phase 1, 1 = phase 2, ...). E.g. { 1.0, 1.5, 2.5 } → phase 1 regen ×1, phase 2 ×1.5,
        // phase 3 ×2.5. Empty array or phase index out of range → fallback 1.0 (no scaling).
        public float[] PhaseRegenMult { get; set; } = System.Array.Empty<float>();
        // IsFlying: true if this is an airborne enemy (ignores obstacles, terrain effects)
        public bool IsFlying { get; set; } = false;
        // FlightHeight: flight altitude level (1=low, 2=high) — only meaningful if IsFlying=true
        public float FlightHeight { get; set; } = 0f;
        // CanLand: true if this flying enemy can land mid-flight and become a ground unit
        public bool CanLand { get; set; } = false;
        // CanBurrow: true if this enemy can dive underground and become untargetable
        public bool CanBurrow { get; set; } = false;
        // BurrowDuration: how many turns the enemy stays underground
        public float BurrowDuration { get; set; } = 0f;
        // BurrowSpeedMult: movement speed multiplier while underground
        public float BurrowSpeedMult { get; set; } = 1f;
        // BurrowEmergeDamage: AoE damage dealt when emerging from ground (0 = no damage)
        public float BurrowEmergeDamage { get; set; } = 0f;
        // BurrowRadius: AoE radius for emerge damage
        public float BurrowRadius { get; set; } = 0f;
        // BurrowCooldown: turns between burrow uses (-1 = one-time, 0 = can always burrow)
        public float BurrowCooldown { get; set; } = -1f;
        // IsNecromancer: true if this enemy is a necromancer that resurrects nearby corpses
        public bool IsNecromancer { get; set; } = false;
        // ResurrectRange: radius in world units for scanning nearby corpses
        public float ResurrectRange { get; set; } = 0f;
        // ResurrectCooldown: turns between resurrection uses (-1 = one-time, 0 = can always)
        public float ResurrectCooldown { get; set; } = 0f;
        // ResurrectHpMult: HP multiplier applied to reanimated minions (0.0-1.0)
        public float ResurrectHpMult { get; set; } = 0f;
        // MaxResurrectCount: max number of simultaneous reanimated minions per necromancer
        public int MaxResurrectCount { get; set; } = 0;
        // ResurrectCorpseAgeLimit: max age of corpses in seconds (older corpses can't be resurrected)
        public float ResurrectCorpseAgeLimit { get; set; } = 0f;
        // Boss: true if this monster type is a boss (participates in phase/enrage system).
        public bool IsBoss { get; set; } = false;
        // IsThief: true if this enemy steals gold instead of damaging base (GoldStealing direction)
        public bool IsThief { get; set; } = false;
        // StealAmount: gold stolen when thief reaches player base
        public float StealAmount { get; set; } = 0f;
        // PathDeviationType: 0=none (default deterministic Y-axis), 1=sine, 2=random per turn
        public int PathDeviationType { get; set; } = 0;
        // PathDeviationAmplitude: max lateral X offset in world units (e.g. 0.3 = ±0.3 cells)
        public float PathDeviationAmplitude { get; set; } = 0f;
        // GoldOnReturn: bonus gold awarded when player kills thief after it escapes
        public float GoldOnReturn { get; set; } = 0f;
        // Round 179 Direction 3 — Bounty Enemy Marker ────────────────────────
        // IsBounty: true if this enemy pays EnemyBountyGoldMult × gold on death (high-value
        // high-risk target). When true, WaveSpawningSystem calls SetEnemyBounty() to wire
        // the multiplier into the ComponentStore. Default false = inert fast path.
        public bool IsBounty { get; set; } = false;
        // BountyGoldMult: gold reward multiplier on death (e.g. 5.0 = 5× reward). Wired to
        // EnemyBountyGoldMult via SetEnemyBounty(). Clamped to [1.0, 20.0] at the store
        // level so malformed JSON can't spike the economy. 1.0 = no bonus (inert).
        public float BountyGoldMult { get; set; } = 1f;
        // DrainRatio: max fraction of tower damage this enemy can drain (0-1, 0 = no drain).
        // Example: 0.5 = can reduce a nearby tower's damage by up to 50%.
        public float DrainRatio { get; set; } = 0f;
        // DrainRadius: world-unit radius within which this enemy can drain a tower.
        public float DrainRadius { get; set; } = 0f;
        // DrainRate: fraction of base tower damage drained per second until DrainRatio cap is reached.
        // Example: 0.1 = 10% of base damage stolen per second.
        public float DrainRate { get; set; } = 0f;
        // Phases: ordered list of boss phase definitions (by threshold, descending).
        // Example: [{"threshold": 0.75, "abilityId": "phase2_buff"}, {"threshold": 0.50, "abilityId": "enrage"}]
        public List<BossPhaseDef> Phases { get; set; } = new List<BossPhaseDef>();
        // Round 124 Dir 1 — Boss Path Trail AoE: when set on a boss, leaves a damaging
        // AoE trail along the path as the boss advances. All fields default to 0 = no trail
        // (zero-overhead on the hot path). When BossTrailProgressInterval > 0 AND
        // BossTrailRadius > 0 AND BossTrailDamage > 0, the boss drops one trail AoE per
        // "Interval" worth of path progress (0.1 = every 10% of the waypoints).
        // BossTrailSlow is applied to nearby enemies (0.5 = 50% slow for 1 frame).
        public float BossTrailProgressInterval { get; set; } = 0f;
        public float BossTrailRadius { get; set; } = 0f;
        public float BossTrailDamage { get; set; } = 0f;
        public float BossTrailSlow { get; set; } = 0f;
        // Enrage: enrage configuration (timer-based). Null = no enrage.
        public BossEnrageConfig Enrage { get; set; }
        // LastStand: HP-threshold-based death rattle. Null = no LastStand.
        // Typical use: boss below 10% HP goes into dramatic enrage (faster + harder hitting).
        public BossLastStandConfig LastStand { get; set; }
        // PierceResistance: 0-1, fraction of piercing damage ignored (0 = full damage, 0.75 = 75% blocked).
        // PierceImmune: binary flag, true = piercing projectiles deal 0 damage.
        public float PierceResist { get; set; } = 0f;
        public bool PierceImmune { get; set; } = false;
        // CritResistance: 0-1, fraction of incoming crit chance suppressed (0 = full crit, 0.5 = halved, 1.0 = immune).
        // Wired to EnemyCritResistance via SetCritResistance() in WaveSpawningSystem.
        public float CritResist { get; set; } = 0f;
        // DeflectChance: 0-1, probability that the enemy deflects an incoming projectile (0 = never, 0.2 = boss-tier).
        // Wired to EnemyDeflectChance via SetDeflectChance() in WaveSpawningSystem. Default 0.
        public float DeflectChance { get; set; } = 0f;
        // FactionId: 0 = no faction (immune to infighting), >0 = opt-in to "挤死小怪" mechanic.
        // Enemies sharing a non-zero FactionId will damage each other in close proximity
        // (configured via infight_cooldown.json). Default 0 for non-swarm archetypes.
        public int FactionId { get; set; } = 0;
    }

    public class TowerConfig
    {
        public string Name { get; set; }
        public TowerType Type { get; set; }
        public float Damage { get; set; }
        public int Range { get; set; }
        public float AttackSpeed { get; set; }
        public float Cost { get; set; }
        public float UpgradeCost { get; set; }
        // Tower debuff fields (0 = no debuff)
        public float StunChance { get; set; } = 0f;   // probability per hit (0-1)
        public float SlowAmount { get; set; } = 0f;   // speed multiplier (e.g. 0.5 = 50% speed)
        public float SlowDuration { get; set; } = 0f; // duration in turns
        // Round 124 — Disarm: probability per hit (0-1) and duration in turns (0 = no disarm)
        public float DisarmChance { get; set; } = 0f;
        public float DisarmDuration { get; set; } = 0f;
        // Targeting mode: which enemy the tower prefers to attack
        public TowerTargetingMode TargetingMode { get; set; } = TowerTargetingMode.Nearest;
        // Tower special ability fields (null = no special ability)
        public TowerSpecialAbility SpecialAbility { get; set; }
        // Tower upgrade path: "standard" (default), "fast", or "tank"
        // Determines which upgrade curve the tower follows.
        // When null/empty, defaults to "standard".
        public string UpgradePath { get; set; }
        // Ammo system: 0 = unlimited ammo (no reload needed)
        public int MaxAmmo { get; set; } = 0;
        // Reload time in seconds (0 = instant/unlimited)
        public float ReloadTime { get; set; } = 0f;
        // Homing projectile: if true, projectile tracks target and turns mid-flight
        public bool ProjectileHoming { get; set; } = false;
        // Lead-aim factor: 0 = no lead (default, straight aim). > 0 = projectile is fired at the
        // target's predicted future position based on its current movement direction + speed.
        // 1.0 = perfect lead (compensates target's motion for the full flight time), 0.5 = half
        // lead. Capped at 2.0 (over-lead). Only applied to ProjectileSystem-fired projectiles
        // (fragment / homing variants); instant-hit tower attacks ignore it.
        public float LeadAimFactor { get; set; } = 0f;
        // Turn rate: maximum angular change per second in radians (e.g. PI = 180°/sec, 0 = instant/snap to target)
        // Default 0 means instant rotation (existing behavior unchanged)
        public float TurnRate { get; set; } = 0f;
        // Damage type: determines which resistance the target uses for mitigation.
        public DamageType DamageType { get; set; } = DamageType.Physical;
        // InterceptRate: for PointDefense towers (TargetingMode=6), probability of intercepting enemy projectiles (0.0-1.0)
        public float InterceptRate { get; set; } = 0.5f;
        // Bouncing projectile: number of bounces after initial hit (0 = no bounce)
        public int Bounces { get; set; } = 0;
        // BounceRange: search radius in tiles for finding next bounce target
        public float BounceRange { get; set; } = 0f;
        // BounceDamageFalloff: damage multiplier per bounce (1.0 = full damage, 0.7 = 70%)
        public float BounceDamageFalloff { get; set; } = 1f;
        // Piercing projectile: number of enemies the projectile can pierce through (0 = no pierce)
        public int PierceCount { get; set; } = 0;
        // PierceDmgFalloff: damage multiplier after each pierce (1.0 = full damage, 0.7 = 70%)
        public float PierceDmgFalloff { get; set; } = 1f;
        // Tower demolish (sacrifice): if non-null, tower can be detonated for AoE damage
        public TowerDemolishConfig Demolish { get; set; }
        // Income tower: if true, tower generates gold passively instead of attacking
        public bool IsIncomeTower { get; set; } = false;
        // GoldPerSecond: gold generated per second (only meaningful if IsIncomeTower = true)
        public float GoldPerSecond { get; set; } = 0f;
        // Curse tower: if true, tower applies curse aura debuff to nearby enemies
        public bool IsCurseTower { get; set; } = false;
        // CurseRadius: radius within which the curse effect applies (in grid units)
        public float CurseRadius { get; set; } = 0f;
        // CurseDmgReduction: damage reduction applied to cursed enemies (0.2 = -20% damage)
        public float CurseDmgReduction { get; set; } = 0f;
        // CurseSpeedReduction: move speed reduction applied to cursed enemies (0.3 = -30% speed)
        public float CurseSpeedReduction { get; set; } = 0f;
        // CurseArmorReduction: armor reduction applied to cursed enemies (0.15 = -15% armor)
        public float CurseArmorReduction { get; set; } = 0f;
        // CurseDmgTakenIncrease: additional damage taken bonus applied to cursed enemies (0.25 = +25% damage taken)
        public float CurseDmgTakenIncrease { get; set; } = 0f;
        // Heal aura tower (Round 122 Direction 2): if HealAuraRadius > 0 + HealAuraAmount > 0,
        //   this tower passively heals friendly Palisade towers in radius every HealAuraInterval
        //   seconds. Default 0/0/0 = no heal aura. Designers size this as a maintenance mechanic
        //   (small per-tick HP), not a hard invulnerability.
        public float HealAuraRadius { get; set; } = 0f;
        public float HealAuraAmount { get; set; } = 0f;
        public float HealAuraInterval { get; set; } = 0f;
        // Round 126 Direction 4 — Thorns Aura Tower. If IsThornsTower=true with non-zero
        //   ThornsRadius and ThornsDps, this tower passively applies ThornsDps damage per
        //   second (or per ThornsInterval seconds when interval > 0) to every enemy in
        //   range. Default false/0/0/0 = no thorns aura. Designers size DPS to be small
        //   (1-15 per tick) so the system is a maintenance pressure mechanic, not a
        //   kill-the-whole-wave button. Distinct from on-hit reflect: this is a
        //   constant passive aura, like a poison cloud centered on the tower.
        public bool IsThornsTower { get; set; } = false;
        public float ThornsRadius { get; set; } = 0f;
        public float ThornsDps { get; set; } = 0f;
        public float ThornsInterval { get; set; } = 0f;
        // Taunt tower: if true, tower forces nearby enemies to retarget it (dual of Aggro/Leash —
        //   Aggro = enemy chases player; Taunt = tower forces enemy to attack itself)
        public bool IsTauntTower { get; set; } = false;
        // TauntRadius: world-units radius within which enemies are forced to retarget this tower
        //   (only meaningful if IsTauntTower = true; 0 = inert even if IsTauntTower=true)
        public float TauntRadius { get; set; } = 0f;
        // Pull tower: if true, tower applies gravitational pull to enemies in range
        public bool IsPullTower { get; set; } = false;
        // PullStrength: force magnitude pulling enemies toward the tower per second
        public float PullStrength { get; set; } = 0f;
        // PullRadius: radius within which enemies are pulled toward the tower
        public float PullRadius { get; set; } = 0f;
        // PullCooldown: seconds between pull pulses (0 = continuous/always-on pull)
        public float PullCooldown { get; set; } = 0f;
        // Bleed tower: if true, tower applies stacking bleed on hit (Slash/Pierce type)
        public bool IsBleedTower { get; set; } = false;
        // BleedStacksPerHit: number of bleed stacks applied per successful hit
        public float BleedStacksPerHit { get; set; } = 0f;
        // BleedDmgPct: each stack deals BleedDmgPct * target's EnemyMaxHealth as damage per tick
        public float BleedDmgPct { get; set; } = 0f;
        // BleedTickInterval: seconds between bleed damage ticks
        public float BleedTickInterval { get; set; } = 1f;
        // BleedMaxStacks: maximum stacks that can be applied by this tower (0 = no cap)
        public float BleedMaxStacks { get; set; } = 0f;
        // BleedDuration: total duration in seconds for bleed effect
        public float BleedDuration { get; set; } = 0f;
        // Chrono tower: if true, tower creates a time dilation field that slows enemies within radius
        public bool IsChronoTower { get; set; } = false;
        // TimeFieldRadius: radius of the time dilation field (in grid units)
        public float TimeFieldRadius { get; set; } = 0f;
        // TimeScale: time scale applied to enemies in the field (e.g. 0.5 = 50% speed)
        public float TimeScale { get; set; } = 1f;
        // Construction: time in seconds for this tower to complete construction (0 = instant, no construction phase)
        public float ConstructionTime { get; set; } = 0f;
        // ConstructionHP: maximum HP during construction phase (tower takes damage from enemies during build)
        public float ConstructionHP { get; set; } = 0f;
        // IsVulnerableDuringConstruction: if true, enemies can attack this tower while it's under construction
        public bool IsVulnerableDuringConstruction { get; set; } = false;
        // VisionRadius: fog of war vision radius in grid units (0 = no fog, can see all enemies)
        // Tower can only target enemies within this radius. Affects FogOfWarSystem.
        public float VisionRadius { get; set; } = 0f;
        // IsMobile: if true, this tower moves along a patrol path during combat
        public bool IsMobile { get; set; } = false;
        // MoveSpeed: movement speed in grid units per second (only meaningful if IsMobile = true)
        public float MoveSpeed { get; set; } = 0f;
        // PatrolPathId: ID of the patrol path this tower follows (patrol_paths.json)
        // -1 or missing = no default path (uses path 0 at runtime)
        public int PatrolPathId { get; set; } = -1;
        // PatrolDirection: +1 = forward (ping-pong), -1 = backward, 0 = one-way/stop at end
        public int PatrolDirection { get; set; } = 1;
        // PatrolAttackSpeedPenalty: attack speed multiplier while moving (e.g. 0.75 = 25% slower)
        public float PatrolAttackSpeedPenalty { get; set; } = 0.75f;
        // ── Deployable Trap Tower ───────────────────────────────────────────────
        // IsTrap: if true, this tower is a passive trap (does not actively attack).
        // Triggers an effect (stun/damage/slow) on enemies that walk into its trigger
        // radius. Each trigger consumes 1 charge. Charges = 0 = trap destroyed.
        public bool IsTrap { get; set; } = false;
        // TrapTriggerRadius: in grid units, the radius within which enemies trigger the trap
        public float TrapTriggerRadius { get; set; } = 0f;
        // TrapCharges: total trigger count before the trap is destroyed (-1 = unlimited)
        public int TrapCharges { get; set; } = 0;
        // TrapEffectType: 1=stun (duration in sec), 2=damage (flat HP), 3=slow (factor 0-1)
        public int TrapEffectType { get; set; } = 0;
        // TrapEffectValue: magnitude of the effect (stun seconds / damage HP / slow factor)
        public float TrapEffectValue { get; set; } = 0f;
        // ── Burst Fire / Salvo Mode ────────────────────────────────────────────────
        // BurstCount: number of shots fired per burst cycle (0 = no burst fire, standard single-shot)
        public int BurstCount { get; set; } = 0;
        // BurstInterval: time in seconds between shots within a burst (e.g. 0.1 = 10 shots/sec)
        public float BurstInterval { get; set; } = 0f;
        // BurstCooldown: total cooldown time in seconds for one full burst cycle
        public float BurstCooldown { get; set; } = 0f;
        // ── Range-Based Damage Falloff ──────────────────────────────────────────────
        // FalloffType: 0=None (no falloff), 1=Standard (closer=more dmg), 2=Reverse (sniper: farther=more dmg)
        public int FalloffType { get; set; } = 0;
        // FalloffStartRatio: fraction of max range where falloff begins (0 = starts at tower, 1 = never)
        public float FalloffStartRatio { get; set; } = 1f;
        // FalloffMinRatio: minimum damage multiplier at max range (only for Standard falloff)
        // For Reverse falloff, this is the minimum damage at min range
        public float FalloffMinRatio { get; set; } = 1f;
        // ── Ramp-Up / Spool-Up Damage ───────────────────────────────────────────────
        // RampUpRate: damage increase per consecutive hit on same target (0 = no ramp-up)
        // E.g. 0.05 = +5% damage per hit, up to RampUpMax cap
        public float RampUpRate { get; set; } = 0f;
        // RampUpMax: maximum damage multiplier cap (e.g. 2.0 = 200% max)
        // Default 1.0 = no ramp-up (no increase)
        public float RampUpMax { get; set; } = 1f;
        // RampUpResetOnSwitch: if true, ramp-up resets when target switches (default: true)
        // If false, ramp-up persists even when switching targets (decays gradually instead)
        public bool RampUpResetOnSwitch { get; set; } = true;
        // ── Damage Type Conversion (Phys↔Magic) ─────────────────────────────────────────
        // DamageConversionRatio: fraction of damage converted to ConvertedDamageType (0 = no conversion)
        // E.g. 0.5 = 50% of the tower's damage is converted to the target type
        public float DamageConversionRatio { get; set; } = 0f;
        // ConvertedDamageType: the damage type to convert to (e.g. Magic to bypass Physical immunity)
        public DamageType ConvertedDamageType { get; set; } = DamageType.Physical;
        // ── Mana Drain (Round 101 Direction 10) ─────────────────────────────
        // ManaDrainPct: fraction of target enemy's current mana drained on a successful attack hit.
        // Drained mana is added to the player mana pool (not the tower — towers are mana-less).
        // Default 0 = no drain. E.g. 0.1 = 10% of target's current mana converted to player mana per hit.
        public float ManaDrainPct { get; set; } = 0f;
        // ManaDrainCap: maximum amount of mana that can be drained from a single enemy per hit
        // (prevents one-shot drain of mega-mana boss enemies from instantly filling player pool).
        public float ManaDrainCap { get; set; } = 50f;
        // ── Overkill / Excess Damage ─────────────────────────────────────────────
        // OverkillType: 0=None (no effect), 1=Splash (excess damage splashes to nearby enemies in radius)
        // Default 0 = no overkill effect (backward compatible)
        public int OverkillType { get; set; } = 0;
        // OverkillRatio: fraction of excess damage that becomes splash/secondary effect (0-1)
        // E.g. 0.6 = 60% of overkill distributed to nearby enemies, 40% is wasted
        public float OverkillRatio { get; set; } = 0f;
        // OverkillRadius: search radius in tiles for finding overkill splash targets
        // 0 = no radius (effect disabled, even if type is non-zero)
        public float OverkillRadius { get; set; } = 0f;
        // ── Kill-Triggered Player Sustain (HealOnKill / ManaOnKill) ───────────
        // HealOnKillAmount: HP restored to the owning player whenever this tower scores a kill.
        // 0 = no heal (backward compatible). Recommended range: 0.5 – 5.0 HP per kill.
        public float HealOnKillAmount { get; set; } = 0f;
        // ManaOnKillAmount: mana restored to the owning player whenever this tower scores a kill.
        // 0 = no mana restore. Capped at PlayerMaxMana inside AddPlayerMana.
        public float ManaOnKillAmount { get; set; } = 0f;
        // ── Elemental Affinity (same-element bonus damage) ───────────────────────
        // ElementalAffinity: -1 = no affinity (zero-overhead path). 0..3 = Fire/Ice/Lightning/Poison.
        // When set to a value matching an enemy's element (EnemyElementStatus), the tower's damage
        // is multiplied by (1 + ElementalAffinityBonus). Bench/profile: default -1 keeps the cost
        // identical to legacy behavior on all 150 stock towers.
        public int ElementalAffinity { get; set; } = -1;
        // ElementalAffinityBonus: damage multiplier added when affinity matches enemy element.
        // 0.30 = +30% damage. 0 = inactive even if ElementalAffinity >= 0.
        public float ElementalAffinityBonus { get; set; } = 0f;
        // ── On-Hit Lifesteal (Vampire / Spell-Vamp style tower) ──────────────────
        // LifestealFraction: fraction of raw damage converted to player HP per hit.
        // 0.20 = 20% vamp. 0 = inactive (zero-overhead path). Recommended: 0.10 – 0.40.
        public float LifestealFraction { get; set; } = 0f;
        // LifestealMaxPerFrame: hard ceiling on per-frame heal per single hit (NOT per-frame
        // sum — that would need a per-tower accumulator). 0 = uncapped. Use this to prevent
        // a 10K-enemy burst from overhealing past PlayerMaxHealth in a single frame.
        public float LifestealMaxPerFrame { get; set; } = 0f;

        // Round 138 — Per-Tower Active Skill (manual cast by the player, e.g. press a hotkey
        //   to trigger a powerful ability tied to this specific tower). ActiveSkillId = -1
        //   (default) means this tower has no active skill — the system is fully inert for
        //   it. When ≥ 0, the id refers to the shared SkillDefs[] table (the same one used by
        //   players), so any existing player skill can be repurposed as a tower active.
        //   ActiveCooldown is the configured max cooldown in seconds between casts. Effect
        //   dispatch is the responsibility of TowerActiveSkillSystem on TriggerTowerActive() input.
        public int ActiveSkillId { get; set; } = -1;
        public float ActiveCooldown { get; set; } = 0f;
    }

    /// <summary>
    /// Tower special ability definition — allows towers to have active skills
    /// that are triggered manually (or auto) with area-of-effect effects.
    /// Mirrors the AreaShape pattern from SkillSystem for consistency.
    /// </summary>
    public class TowerSpecialAbility
    {
        /// <summary>Ability identifier, e.g. "aoe_burn", "freeze_stun", "chain_lightning"</summary>
        public string AbilityType { get; set; }
        /// <summary>Cooldown in seconds between activations</summary>
        public float Cooldown { get; set; } = 0f;
        /// <summary>Area shape: circle, box, cross, line, chain. Maps to AreaShapeType enum.</summary>
        public string AreaShape { get; set; }
        /// <summary>Radius in tiles for circle/chain shapes, or half-size for box shapes</summary>
        public int Radius { get; set; } = 0;
        /// <summary>Damage dealt by the ability (multiplied by tower damage)</summary>
        public float DamageMultiplier { get; set; } = 1f;
        /// <summary>Duration in seconds for effects like burn DoT</summary>
        public float Duration { get; set; } = 0f;
        /// <summary>DoT damage per tick (0 = no DoT)</summary>
        public float DotDamagePerTick { get; set; } = 0f;
        /// <summary>DoT tick interval in seconds</summary>
        public float DotTickInterval { get; set; } = 1f;
        /// <summary>Stun duration in turns (0 = no stun)</summary>
        public int StunDuration { get; set; } = 0;
        /// <summary>Slow factor (0.5 = 50% speed, 0 = no slow)</summary>
        public float SlowFactor { get; set; } = 0f;
        /// <summary>Slow duration in turns</summary>
        public int SlowDuration { get; set; } = 0;
        /// <summary>AOE falloff inner radius ratio (0.5 = inner 50% at full damage, default 1.0)</summary>
        public float FalloffInnerRatio { get; set; } = 1.0f;
/// <summary>AOE falloff outer damage multiplier (0.5 = outer 50% damage, default 1.0)</summary>
        public float FalloffOuterMult { get; set; } = 1f;
    }

    /// <summary>
    /// Tower demolish (sacrifice) configuration — when triggered, the tower
    /// detonates with a powerful AoE effect and is permanently destroyed.
    /// </summary>
    public class TowerDemolishConfig
    {
        /// <summary>Radius of the demolish AoE explosion in tiles.</summary>
        public float DemolishRadius { get; set; } = 0f;
        /// <summary>Raw damage dealt to all enemies in the AoE radius.</summary>
        public float DemolishDamage { get; set; } = 0f;
        /// <summary>
        /// Effect type: 0=None, 1=Fire, 2=Ice, 3=Lightning, 4=Poison, 5=Arcane.
        /// Fire applies burning DoT, Ice applies freeze stun, Lightning applies stun,
        /// Poison applies poison DoT, Arcane applies no extra CC.
        /// </summary>
        public int DemolishEffectType { get; set; } = 0;
        /// <summary>DoT damage per tick for fire/poison demolish effects.</summary>
        public float DemolishDotDamagePerTick { get; set; } = 0f;
        /// <summary>Total duration of the DoT effect in seconds.</summary>
        public float DemolishDotDuration { get; set; } = 0f;
        /// <summary>DoT tick interval in seconds.</summary>
        public float DemolishDotInterval { get; set; } = 1f;
        /// <summary>Stun duration in turns for ice/lightning demolish effects (0 = no stun).</summary>
        public int DemolishStunDuration { get; set; } = 0;
    }

    public class EnemyTypeEntry
    {
        public string MonsterType { get; set; }
        public int Count { get; set; } = 0;
    }

    /// <summary>
    /// Wave rhythm tag — controls how the wave is paced within a level.
    /// Affects enemy count and stat scaling at spawn time (see WaveSpawningSystem).
    /// </summary>
    public enum WaveRhythm
    {
        Normal = 0,    // Default — no scaling modifier (×1.0)
        Breather = 1,  // "Rest" wave — fewer and weaker enemies, gives player breathing room
        Surge = 2,     // "Push" wave — more and stronger enemies, compensation after a Breather
        Climax = 3     // "Finale" wave — last wave of a level, harder than Surge
    }

    public class WaveConfig
    {
        public int WaveNumber { get; set; }
        public string MonsterType { get; set; }
        public int EnemyCount { get; set; }
        // Multi-type support: if EnemyTypes is non-empty, use it instead of MonsterType
        public List<EnemyTypeEntry> EnemyTypes { get; set; } = new List<EnemyTypeEntry>();
        // Wave rhythm tag — controls spawn-time scaling (count + stats). Defaults to Normal.
        // Normalized to enum in WaveSpawningSystem at spawn time; missing/invalid values are treated as Normal.
        public string Rhythm { get; set; } = "Normal";
        // Round 120 Dir 3 — Adaptive Spawn Count baseline. Number of kills the player is
        // expected to land during this wave; AdaptiveDifficultySystem uses this to compute
        // the rubber-band multiplier for the NEXT wave. 0 (default) DISABLES the scaling
        // for this wave (backward-compatible — old JSON files stay at multiplier = 1.0).
        // When a wave is multi-type, this is the SUM of expected kills across all types;
        // designers can keep it per-type by setting per-type thresholds in their content.
        public int ExpectedKillCount { get; set; } = 0;

        /// <summary>
        /// Returns how many enemies of a given monster type should spawn this wave.
        /// Uses EnemyTypes[] if populated, otherwise falls back to MonsterType + EnemyCount.
        /// Applies rhythm modifiers: Breather ×0.6, Surge ×1.3, Climax ×1.5, Normal ×1.0.
        /// Floor of 1 to avoid zero-count waves.
        /// </summary>
        public int GetEnemyCountForType(string monsterType)
        {
            int baseCount;
            if (EnemyTypes != null && EnemyTypes.Count > 0)
            {
                int found = 0;
                foreach (var entry in EnemyTypes)
                {
                    if (!string.IsNullOrEmpty(entry.MonsterType) && entry.MonsterType == monsterType)
                    {
                        found = entry.Count;
                        break;
                    }
                }
                baseCount = found;
            }
            else
            {
                baseCount = !string.IsNullOrEmpty(MonsterType) ? EnemyCount : 0;
            }
            return ApplyRhythmCountScale(baseCount);
        }

        /// <summary>
        /// Returns all monster types configured for this wave, in order.
        /// </summary>
        public List<string> GetAllMonsterTypes()
        {
            if (EnemyTypes != null && EnemyTypes.Count > 0)
            {
                var result = new List<string>();
                foreach (var entry in EnemyTypes)
                {
                    if (!string.IsNullOrEmpty(entry.MonsterType) && entry.Count > 0)
                        result.Add(entry.MonsterType);
                }
                return result;
            }
            return !string.IsNullOrEmpty(MonsterType) ? new List<string> { MonsterType } : new List<string>();
        }

        /// <summary>
        /// Returns total enemy count for this wave (rhythm-scaled).
        /// For multi-type waves, this is the sum of per-type scaled counts so the
        /// total always matches <see cref="GetEnemyCountForType"/>'s per-type math.
        /// </summary>
        public int GetTotalEnemyCount()
        {
            if (EnemyTypes != null && EnemyTypes.Count > 0)
            {
                int total = 0;
                foreach (var entry in EnemyTypes)
                    total += ApplyRhythmCountScale(entry.Count);
                return total;
            }
            return ApplyRhythmCountScale(EnemyCount);
        }

        /// <summary>
        /// Normalized enum form of <see cref="Rhythm"/>. Falls back to Normal on missing/invalid input.
        /// </summary>
        public WaveRhythm GetRhythmEnum()
        {
            if (string.IsNullOrEmpty(Rhythm)) return WaveRhythm.Normal;
            if (Enum.TryParse<WaveRhythm>(Rhythm, ignoreCase: true, out var parsed))
                return parsed;
            return WaveRhythm.Normal;
        }

        /// <summary>
        /// Apply rhythm-scaled count multiplier with a minimum of 1 enemy.
        /// Breather ×0.6, Surge ×1.3, Climax ×1.5, Normal ×1.0.
        /// </summary>
        private int ApplyRhythmCountScale(int baseCount)
        {
            if (baseCount <= 0) return 0;
            float mult = GetRhythmEnum() switch
            {
                WaveRhythm.Breather => 0.6f,
                WaveRhythm.Surge => 1.3f,
                WaveRhythm.Climax => 1.5f,
                _ => 1.0f
            };
            int scaled = (int)(baseCount * mult);
            return scaled < 1 ? 1 : scaled;
        }

        /// <summary>
        /// Apply rhythm-scaled stat multiplier (health / damage / armor / speed).
        /// Breather ×0.7, Surge ×1.2, Climax ×1.4, Normal ×1.0.
        /// </summary>
        public float GetRhythmStatMult()
        {
            return GetRhythmEnum() switch
            {
                WaveRhythm.Breather => 0.7f,
                WaveRhythm.Surge => 1.2f,
                WaveRhythm.Climax => 1.4f,
                _ => 1.0f
            };
        }

        // ── Wave Branching: if non-empty, player chooses which path to take next ──
        // When WaveBranches.Count > 0, the wave is a branch point and these options are shown.
        public List<WaveBranchOption> WaveBranches { get; set; } = new List<WaveBranchOption>();
        // [Internal] Set by WaveBranchSystem when player selects a branch option.
        // WaveSpawningSystem reads this on SetLevel to override the wave's enemy composition.
        public WaveBranchOption AppliedBranchOption { get; set; }
    }

    /// <summary>
    /// Wave branch option — one possible next wave the player can choose from.
    /// Shown at branch points during Intermission before the next wave starts.
    /// </summary>
    public class WaveBranchOption
    {
        // Display name shown to the player (e.g., "Swarm Wave", "Elite Rush", "Boss Wave")
        public string Name { get; set; } = "";
        // Monster type for this branch option
        public string MonsterType { get; set; } = "";
        // Number of enemies for this branch option
        public int EnemyCount { get; set; } = 10;
        // Gold bonus awarded when this option is chosen
        public float GoldBonus { get; set; } = 0f;
        // Research points bonus awarded when this option is chosen
        public int ResearchBonus { get; set; } = 0;
        // Difficulty hint shown to the player: "Easy" / "Medium" / "Hard" / "Extreme"
        public string Difficulty { get; set; } = "Medium";
        // Optional: multi-type enemy composition
        public List<EnemyTypeEntry> EnemyTypes { get; set; } = new List<EnemyTypeEntry>();
    }

    public class LevelConfig
    {
        public int LevelNumber { get; set; }
        public int WaveCount { get; set; }
        public List<WaveConfig> Waves { get; set; } = new List<WaveConfig>();
        // Objective type for this level — defaults to KillAll if omitted.
        // KillAll=0, Escort=1, Survival=2, Timed=3, Endless=4
        public int ObjectiveType { get; set; } = 0;
        // For Escort mode: escort NPC max health
        public float EscortNpcMaxHealth { get; set; } = 100f;
        // For Escort mode: escort NPC movement speed (tiles/sec)
        public float EscortNpcSpeed { get; set; } = 0.5f;
        // For Timed mode: time limit in seconds
        public float ObjectiveTimeLimit { get; set; } = 120f;
        // For Survival mode: number of waves to survive
        public int SurvivalWaveCount { get; set; } = 10;
        // Resource nodes on this map — populated from level JSON or resource_nodes.json
        public List<ResourceNodeDef> ResourceNodes { get; set; } = new List<ResourceNodeDef>();
        // Destructible objects (crates, oil barrels) on this level. Round 95 Direction 5.
        // Each entry references a DestructibleDef by DefId and is placed at (X, Y).
        public List<DestructiblePlacement> Destructibles { get; set; } = new List<DestructiblePlacement>();
        // ── DoomClock objective (Round 110 Direction 10) ─────────────────────────
        // For DoomClock mode: total countdown duration in seconds (e.g. 180 = 3 min).
        // Default 180s. Win when timer hits 0 with player alive; lose if player dies first.
        public float DoomClockDuration { get; set; } = 180f;
        // For DoomClock mode: bonus points awarded per cleared wave (default 100).
        public int DoomClockWaveScore { get; set; } = 100;
        // For DoomClock mode: bonus points awarded per second of remaining time.
        public int DoomClockTimeBonusPerSec { get; set; } = 10;
        // For DoomClock mode: bonus points awarded per 1% of remaining HP at game end.
        public int DoomClockHealthBonusPerPercent { get; set; } = 5;
        // For DoomClock mode: enemy stat multiplier per wave cycle (1.1 = +10% per cycle).
        // Cycle = one full pass through DoomClockInitialWaves. Default 1.10f.
        public float DoomClockWaveScaling { get; set; } = 1.10f;
        // For DoomClock mode: initial wave templates (re-used in cycle when waves exhausted).
        // Each entry is a (MonsterType, EnemyCount) pair. If empty, falls back to level.Waves.
        public List<DoomClockWaveTemplate> DoomClockInitialWaves { get; set; } = new List<DoomClockWaveTemplate>();
    }

    /// <summary>
    /// Wave template used by DoomClock mode (Round 110 Direction 10).
    /// After the initial pool is exhausted, DoomClockSystem cycles back to wave 0 with
    /// stat scaling applied. Each template defines a monster type and a spawn count.
    /// </summary>
    public class DoomClockWaveTemplate
    {
        public string MonsterType { get; set; } = "Normal";
        public int EnemyCount { get; set; } = 10;
    }

    /// <summary>
    /// Placement entry for a destructible object on a level map. Round 95 Direction 5.
    /// The DefId references a DestructibleDef.Id in the destructibles.json config.
    /// </summary>
    public class DestructiblePlacement
    {
        public string DefId { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
    }

    /// <summary>
    /// Enemy ability definition — loaded from enemy_abilities.json.
    /// </summary>
    public class EnemyAbilityDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string AbilityType { get; set; } // "self_heal", "aoe_damage", "buff_allies"
        public float Cooldown { get; set; }
        public float CooldownRemaining { get; set; }
        public int AoeRadius { get; set; }
        public float DamageMultiplier { get; set; }
        public float HealAmount { get; set; }
        public string BuffStat { get; set; }
        public int BuffDuration { get; set; }
        public int StunDuration { get; set; }   // turns to stun (for stun_aoe abilities)
        public float SlowFactor { get; set; }   // speed multiplier for slow (0.5 = 50%)
        public int SlowDuration { get; set; }  // turns for slow
        // summon_minion ability fields
        public float MinionHealthMult { get; set; } // health multiplier for summoned minion
        public float MinionDamageMult { get; set; } // damage multiplier for summoned minion

        // Telegraph/warning zone fields (for aoe_damage, stun_aoe, slow_aoe abilities)
        // TelegraphDuration: warning turns before AoE lands (0 = instant damage, no telegraph)
        public float TelegraphDuration { get; set; }
        // TelegraphColor: 0=red, 1=blue, 2=yellow (for renderer)
        public int TelegraphColor { get; set; }

        // Silence tower fields (for silence_tower abilities)
        // SilenceRadius: radius within which towers are silenced (-1 = not a silence ability)
        public float SilenceRadius { get; set; }
        // SilenceDuration: how many turns silenced towers cannot attack
        public float SilenceDuration { get; set; }

        // Dispel tower fields (for dispel_tower abilities)
        // DispelRadius: radius within which tower aura/synergy buffs are removed (-1 = not a dispel ability)
        public float DispelRadius { get; set; }
        // DispelDuration: how many turns dispelled towers cannot receive new buffs
        public float DispelDuration { get; set; }
        // DispelImmunityDuration: turns of immunity after dispel expires
        public float DispelImmunityDuration { get; set; }

        // Interruptible Channeling fields (for high-threat enemy abilities).
        // CastTime: turns the enemy spends channeling the ability before it resolves (0 = instant).
        //   While casting, the enemy is frozen in place (skip Movement/AI) and is interruptible by
        //   silence/stun/damage. When the cast completes, ExecuteAbility fires normally.
        //   On interrupt, 50% of Cooldown is refunded (prevents perma-stun exploit).
        // Interruptible: whether the cast can be cancelled externally (true = default for most,
        //   false = must complete, used for boss ultimate abilities).
        public float CastTime { get; set; }
        public bool Interruptible { get; set; } = true;
    }

    /// <summary>
    /// Boss phase definition — loaded from monster JSON (phases[] field).
    /// Each phase specifies a health threshold and the ability to trigger on phase entry.
    /// </summary>
    public class BossPhaseDef
    {
        // Health fraction (0-1) at which this phase activates. E.g., 0.5 = 50% max HP.
        public float Threshold { get; set; }
        // Ability ID to trigger when phase activates (e.g., "boss_summon", "boss_enrage").
        // Empty = no ability triggered on phase entry.
        public string AbilityId { get; set; }
        // Speed multiplier applied when this phase activates (1.0 = no change, 1.5 = +50%).
        public float SpeedMult { get; set; } = 1.0f;
        // Damage multiplier applied when this phase activates (1.0 = no change, 1.25 = +25%).
        public float DamageMult { get; set; } = 1.0f;
        // New behavior tree subtree to use during this phase.
        // Empty = continue using current BT.
        public string NewBehaviorTree { get; set; }
        // Round 119 Dir 3 — optional minion summon trigger. When the phase fires, the boss
        // spawns MinionCount copies of the MonsterTypes[MinionTypeId] entry near its current
        // position. Both fields are 0 by default (= no summon). MinionTypeId uses the index
        // into GameConfig.MonsterTypes (NOT the string Type), so it is data-driven without
        // re-running the JSON loader at runtime. Designers can leave both fields at 0 to
        // keep the phase behaving like Round 111 (speed/damage/ability only).
        public int MinionTypeId { get; set; } = 0;
        public int MinionCount { get; set; } = 0;
        // Round 137 Dir 6 — Themed Boss Summon. BossElementAffinity declares this boss's
        // element ("Fire" / "Ice" / "Lightning" / "Poison" / ""). When the phase fires, spawned
        // minions whose MonsterConfig.ElementAffinity matches this string get a +10% HP
        // bonus (and the same element-based bonus damage to the player via the existing
        // ElementalResistanceSystem). Empty string (default) = no affinity = no bonus.
        // Compared case-insensitively against MonsterConfig.ElementAffinity at spawn time.
        public string BossElementAffinity { get; set; } = "";
    }

    /// <summary>
    /// Boss enrage configuration — loaded from monster JSON (enrage{} field).
    /// </summary>
    public class BossEnrageConfig
    {
        // Time in seconds after spawn before enrage activates (0 = no timer-based enrage).
        public float EnrageAfterSeconds { get; set; }
        // Speed multiplier when enrage activates (1.0 = no change).
        public float SpeedMult { get; set; } = 1.0f;
        // Damage multiplier when enrage activates (1.0 = no change).
        public float DamageMult { get; set; } = 1.0f;
    }

    /// <summary>
    /// Boss LastStand / DeathRattle configuration — loaded from monster JSON (lastStand{} field).
    /// HP-threshold trigger (in contrast to Enrage's timer-based trigger): activates when
    /// currentHP < hpFraction * maxHP. Typical use: boss enters enrage below 10% HP for dramatic finale.
    /// </summary>
    public class BossLastStandConfig
    {
        // HP fraction (0-1) below which LastStand activates. 0 = disabled.
        // Example: 0.1 = activate when HP drops below 10% of max.
        public float HpFraction { get; set; } = 0f;
        // Speed multiplier when LastStand activates (1.0 = no change, 1.5 = +50% speed).
        public float SpeedMult { get; set; } = 1.0f;
        // Damage multiplier when LastStand activates (1.0 = no change, 2.0 = double damage).
        public float DamageMult { get; set; } = 1.0f;
    }

    /// <summary>
    /// Enemy fission (split on death) definition — loaded from enemy_fission.json.
    /// When a monster with fission dies, it spawns N child enemies at the death location.
    /// </summary>
    public class FissionDef
    {
        public string FissionId { get; set; } = "";
        // Source monster type that triggers fission (e.g., "Slime")
        public string SourceMonsterType { get; set; } = "";
        // Child monster type spawned on fission
        public string ChildMonsterType { get; set; } = "";
        // Number of children spawned
        public int ChildrenCount { get; set; } = 2;
        // Health scale of children relative to parent (0.4 = 40% of parent's current health)
        public float HealthScale { get; set; } = 0.4f;
        // Damage scale of children
        public float DamageScale { get; set; } = 0.3f;
        // Speed scale of children
        public float SpeedScale { get; set; } = 1.2f;
        // Gold reward scale of children
        public float GoldScale { get; set; } = 0.5f;
        // Maximum fission generations (parent + children + grandchildren...)
        public int MaxGeneration { get; set; } = 2;
    }

    /// <summary>
    /// Enemy Life Link definition — loaded from life_link.json.
    /// When a monster with life link dies, it establishes a bidirectional damage-sharing
    /// link with a nearby ally. Linked enemies split incoming damage.
    /// </summary>
    public class LifeLinkDef
    {
        public string LifeLinkId { get; set; } = "";
        // Source monster type that can establish life links (e.g., "SoulBinder")
        public string SourceMonsterType { get; set; } = "";
        // Maximum number of links this enemy can have at once (0 = no linking)
        public int MaxLinks { get; set; } = 1;
        // Damage sharing ratio (0.0-1.0): fraction of incoming damage shared with linked enemy
        // e.g. 0.5 = 50% of damage goes to linked enemy, 50% stays on target
        // e.g. 0.3 = 30% of damage shared, 70% stays on target
        public float DamageShareRatio { get; set; } = 0.5f;
        // Link range in world units: only enemies within this range can be linked
        public float LinkRange { get; set; } = 3f;
        // Cooldown in turns between link attempts (0 = no cooldown, always tries to link)
        public float LinkCooldown { get; set; } = 5f;
        // If true, when link master dies, linked enemy also takes damage (break penalty)
        public bool BreakPenalty { get; set; } = true;
        // Damage fraction applied to linked enemy when master dies (e.g. 0.25 = 25% of master's current HP)
        public float BreakPenaltyDamageFraction { get; set; } = 0.25f;
    }

    /// <summary>
    /// Enemy spawner nest structure definition — loaded from nests.json (referenced in level configs).
    /// Nests are static structures that periodically spawn minions at their location.
    /// Unlike WaveSpawning (time-based), nests produce continuously and can be destroyed.
    /// </summary>
    public class NestDef
    {
        public string Id { get; set; } = "";
        // X/Y position on the map (in tiles)
        public float X { get; set; }
        public float Y { get; set; }
        // Monster type to spawn (must match a MonsterConfig.Type)
        public string MonsterType { get; set; } = "";
        // Spawn interval in seconds
        public float SpawnInterval { get; set; } = 5f;
        // Maximum alive minions from this nest at any time
        public int MaxAlive { get; set; } = 3;
        // Nest health (can be destroyed by towers)
        public float MaxHealth { get; set; } = 500f;
        // Armor (reduces incoming damage from attacks)
        public float Armor { get; set; } = 5f;
    }

    /// <summary>
    /// Path modifier definition — represents a path modification node placed by the player.
    /// When an enemy enters the modifier's influence zone, it is forced to follow the
    /// target path instead of its default path.
    /// </summary>
    public class PathModifierDef
    {
        public string Id { get; set; } = "";
        // Name displayed to the player
        public string Name { get; set; } = "";
        // Cost in gold to place this modifier
        public float Cost { get; set; } = 50f;
        // Radius of influence in grid tiles
        public float Radius { get; set; } = 2f;
        // Target path ID to assign when enemy is inside influence zone
        // 0 = default, 1 = fork_left, 2 = fork_right, 3 = ring
        public int TargetPathId { get; set; } = 1;
        // Duration in turns (0 = permanent)
        public float Duration { get; set; } = 0f;
        // Description for UI
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// Enemy morph (transform mid-wave) definition — loaded from enemy_morphs.json.
    /// When a monster's health drops below a threshold (or time trigger fires), it transforms
    /// into a different monster type with scaled stats.
    /// </summary>
    public class MorphDef
    {
        public string MorphId { get; set; } = "";
        // Source monster type that triggers morph (e.g., "Wolf")
        public string SourceMonsterType { get; set; } = "";
        // Target monster type after morph (e.g., "DireWolf")
        public string TargetMonsterType { get; set; } = "";
        // Trigger type: "HP_THRESHOLD" | "TIME"
        public string TriggerType { get; set; } = "HP_THRESHOLD";
        // For HP_THRESHOLD: morph when health drops below this fraction (0.0-1.0)
        // For TIME: morph after this many seconds since spawn
        public float TriggerValue { get; set; } = 0.5f;
        public string Description { get; set; } = "";
        // Stat multipliers applied on morph
        public float SpeedMultOnMorph { get; set; } = 1.0f;
        public float DamageMultOnMorph { get; set; } = 1.0f;
        public float HealthMultOnMorph { get; set; } = 1.0f;
        // Morph duration in seconds (0 = instant, permanent)
        public float Duration { get; set; } = 0f;
    }

    /// <summary>
    /// Runtime path branching junction definition (Round 121 — Direction 1).
    /// Defines a single decision point on a path where the enemy dynamically chooses
    /// which downstream path to follow, based on a JunctionPolicy. Loaded from
    /// `path_junctions.json` (no JSON loader integration yet — wired directly in tests/code).
    /// </summary>
    public class JunctionDef
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        // Path ID where this junction lives (0 = default, 1 = fork_left, 2 = fork_right, 3 = ring).
        public int SourcePathId { get; set; } = 0;
        // Waypoint index within the source path that is the decision point.
        public int NodeIndex { get; set; } = 0;
        // Policy used to choose the downstream path. 0 = HpBased, 1 = TowerDensityBased, 2 = TypeBased.
        // (String also accepted via ToString/Parse; integer form is the wire form.)
        public JunctionPolicy Policy { get; set; } = JunctionPolicy.HpBased;
        // For HpBased: HP fraction threshold (0-1). Enemies with HP/maxHP > threshold take the
        // "long" branch; others take the "short" branch.
        public float HpLongPathThreshold { get; set; } = 0.75f;
        // For TowerDensityBased: tower count within TowerDensityRadius. If count > threshold,
        // take the "short" branch (avoid heavy defenses); else take the "long" branch.
        public float TowerDensityRadius { get; set; } = 4f;
        public int TowerDensityShortPathThreshold { get; set; } = 2;
        // For TypeBased: monster types (by tag) that take the "boss" branch (direct path).
        public List<string> BossTypeTags { get; set; } = new List<string>();
        // Path IDs to assign based on policy result.
        // Index 0 = "short"/"direct" path; Index 1 = "long"/"boss" path. Default to existing paths.
        public int ShortPathId { get; set; } = 0;
        public int LongPathId { get; set; } = 1;
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// Policy used by JunctionDef to decide which downstream path an enemy takes.
    /// 0 = HpBased (high HP → long path), 1 = TowerDensityBased (high tower count → short path),
    /// 2 = TypeBased (boss tag → direct path).
    /// </summary>
    public enum JunctionPolicy
    {
        HpBased = 0,
        TowerDensityBased = 1,
        TypeBased = 2,
    }

    /// <summary>
    /// Wave mutator definition — loaded from wave_mutators.json.
    /// A mutator applies a global modifier to all enemies during a specific wave.
    /// </summary>
    public class WaveMutatorDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        // "speed_mult" | "regen" | "explosive_death" | "dense_spawn"
        public string EffectType { get; set; }
        // For speed_mult
        public float SpeedMult { get; set; } = 1.0f;
        // For regen
        public float RegenRate { get; set; } = 0f;
        // For explosive_death
        public float ExplosionDamageRatio { get; set; } = 0f;
        public float ExplosionRadius { get; set; } = 0f;
        // For dense_spawn
        public int SpawnBatchSize { get; set; } = 5;
        // Wave number (1-indexed) at which this mutator activates
        public int TriggerWaveStart { get; set; } = 0;
    }

    /// <summary>
    /// Defines a heal-over-time enemy healer unit type.
    /// Healers restore HP to nearby wounded allies every heal interval.
    /// </summary>
    public class HealerDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        // Heal amount per tick (flat HP)
        public float HealAmount { get; set; }
        // Cooldown/interval between heal ticks (in seconds)
        public float HealInterval { get; set; }
        // Range within which the healer can target allies (in tiles)
        public float HealRange { get; set; }
    }

    /// <summary>
    /// Defines a gold-stealing thief enemy type.
    /// Thieves skip base damage and instead steal gold when reaching the end.
    /// </summary>
    public class ThiefDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        // Gold amount stolen when thief reaches the player's base
        public float StealAmount { get; set; }
        // Bonus gold awarded when player kills the thief after it escapes
        public float GoldOnReturn { get; set; }
    }

    /// <summary>
    /// Defines an enemy that can dispel (remove) tower aura/synergy buffs.
    /// Dispel enemies release a purification wave that clears all tower增益 within radius.
    /// </summary>
    public class DispelEnemyDef
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        /// Radius within which tower buffs are removed (in tiles).
        public float DispelRadius { get; set; } = 0f;
        /// Duration in turns that dispelled towers cannot receive new buffs.
        public float DispelDuration { get; set; } = 0f;
        /// Immunity duration in turns after dispel expires.
        public float ImmunityDuration { get; set; } = 0f;
    }

    /// <summary>
    /// Defines a placeable obstacle type (wooden barricade, ice wall, spike trap).
    /// </summary>
    public class ObstacleDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        // Gold cost to place
        public float Cost { get; set; }
        // Max health of the obstacle
        public float MaxHealth { get; set; }
        // Whether enemies attack this obstacle when in range
        public bool CanBeAttacked { get; set; } = true;
        // Damage dealt to enemies when they walk over (spike trap)
        public float TrapDamage { get; set; } = 0f;
    }

    /// <summary>
    /// Defines a destructible object type (wooden crate, oil barrel, altar). Round 95 Direction 5.
    /// Unlike ObstacleDef, destructibles are static loot/utility objects placed on the level
    /// map at level load time (not built by players) and can be destroyed by tower attacks.
    /// </summary>
    public class DestructibleDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        // Max health of the destructible
        public float MaxHealth { get; set; }
        // On-destroy effect: 0=None, 1=Gold (drop gold to player), 2=Explosion (AoE damage)
        public int OnDestroyEffect { get; set; } = 0;
        // Magnitude: Gold=gold amount, Explosion=% of enemy max HP as damage (0.0-1.0)
        public float OnDestroyValue { get; set; } = 0f;
        // Explosion radius in tiles (only used when OnDestroyEffect=2)
        public float ExplosionRadius { get; set; } = 5f;
    }

    /// <summary>
    /// Defines a player-summoned combat unit (summoned via skill).
    /// </summary>
    public class SummonDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        // Unit type: 0=Melee, 1=Ranged, 2=Bomber
        public int UnitType { get; set; }
        // Max health
        public float Health { get; set; }
        // Attack damage per hit
        public float Damage { get; set; }
        // Movement speed (tiles/sec)
        public float MoveSpeed { get; set; }
        // Attack range (tiles)
        public int AttackRange { get; set; }
        // Attack speed (attacks/sec)
        public float AttackSpeed { get; set; }
        // Gold cost to summon
        public float Cost { get; set; }
        // Mana cost to summon
        public float ManaCost { get; set; }
        // Duration in seconds (0 = permanent until killed)
        public float Duration { get; set; }
        // Cooldown in seconds
        public float Cooldown { get; set; }
    }

    public class SkillConfig
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public float DamageMultiplier { get; set; }
        public int AreaWidth { get; set; }
        public int AreaHeight { get; set; }
        public int AttackRange { get; set; }
        public float Cooldown { get; set; }
        public bool AutoCast { get; set; }
        public string Hotkey { get; set; }
        // Area shape string maps to AreaShapeType via FromString()
        public string AreaShape { get; set; }
        // Effect radius (tiles). Box uses this as half-size → 3×3 box → AreaRadius=1
        public int AreaRadius { get; set; }
        // DoT fields (0 = no DoT)
        public float DotDuration { get; set; }
        public float DotTickInterval { get; set; }
        public float DotDamagePerTick { get; set; }
        // Heal/Shield fields (0 = no heal/shield)
        public float HealPercent { get; set; }
        public float ShieldAmount { get; set; }
        public float ShieldDuration { get; set; }
        // Freeze fields (Cold Nova)
        public float FreezeDuration { get; set; }
        public float FreezeChance { get; set; }
        /// <summary>Cone angle in degrees for AreaShape="cone". Controls fan spread. Default: 60.</summary>
        public float ConeAngleDegrees { get; set; } = 60.0f;
        // Slow fields (Slow Nova — move speed reduction AoE)
        public float SlowAmount { get; set; }
        public float SlowDuration { get; set; }
        // Polymorph fields (变羊/变小鸡 — turns enemies into a harmless form)
        // 0 / 0f = no polymorph applied. Duration in turns; multiplier increases damage taken.
        public float PolymorphDuration { get; set; }
        public float PolymorphDamageTakenMultiplier { get; set; } = 1f;
        // Mana cost for casting this skill (0 = free)
        public float ManaCost { get; set; }
        // Summon definition ID (for summon_unit ability type) — null/empty = not a summon skill
        public string SummonDefId { get; set; }
        // Round 136 Direction 2 — AOE CC group control fields
        public float AoeStunDuration { get; set; }     // turns (used by AreaShape=aoestun)
        public float AoeRootDuration { get; set; }     // turns (used by AreaShape=aoeroot)
        public float AoeKnockbackForce { get; set; }   // radial push impulse (used by AreaShape=aoeknockback)
    }

    public class BehaviorTreeDef
    {
        public string MonsterType;
        public string RootId;
        public Dictionary<string, BTNodeDef> Nodes;
    }

    public class BTNodeDef
    {
        public string Id;
        public string Type;
        public string Action;
        public string Condition;
        public string Operator;
        public float Value;
        public float Param;
        public string[] Children;
        // Ability ID for enemy_cast_* action nodes
        public string AbilityId;
    }

    /// <summary>
    /// Special ability granted by a tower upgrade level.
    /// </summary>
    public enum TowerUpgradeAbility
    {
        None = 0,
        ArmorPierce,     // Ignore part of enemy armor
        SplashDamage,    // Deal damage to nearby enemies
        CriticalStrike,  // Chance to deal bonus damage
        ChainLightning,  // Chain to nearby enemies (uses existing Tesla logic)
        FreezeAoe        // Slow nearby enemies on hit
    }

    /// <summary>
    /// Per-level upgrade multipliers for a tower upgrade path.
    /// Keys are upgrade levels (1-based). Level 1 = first upgrade from base.
    /// </summary>
    public class TowerUpgradeLevelConfig
    {
        public float DamageMultiplier { get; set; } = 1.2f;
        public float RangeAdd { get; set; } = 1f;
        public float AttackSpeedMultiplier { get; set; } = 1.0f;
        public float CostMultiplier { get; set; } = 1.5f;
        /// <summary>Special ability granted by this upgrade level (e.g., "armor_pierce", "splash_damage").</summary>
        public TowerUpgradeAbility SpecialAbility { get; set; } = TowerUpgradeAbility.None;
        /// <summary>Parameter for special ability (e.g., armor pierce ratio, splash radius, crit chance).</summary>
        public float SpecialAbilityParam { get; set; } = 0f;
    }

    /// <summary>
    /// A named tower upgrade path (e.g., "standard", "fast", "tank").
    /// Maps upgrade levels to per-level multipliers.
    /// </summary>
    public class TowerUpgradePathConfig
    {
        public string Id { get; set; }
        public string Description { get; set; }
        /// <summary>Keys are level numbers (1, 2, 3, ...). If a level is missing, fall back to the highest defined level.</summary>
        public Dictionary<int, TowerUpgradeLevelConfig> Levels { get; set; } = new Dictionary<int, TowerUpgradeLevelConfig>();
    }

    // ── Phase Behavior Config ────────────────────────────────────────────────

    /// <summary>
    /// Per-phase behavior settings loaded from phase_behavior.json.
    /// </summary>
    public class PhaseBehaviorDef
    {
        public string Description { get; set; }
        public string EnterMessage { get; set; }
        public bool AutoAdvance { get; set; }
        public List<string> UnlockTowers { get; set; } = new List<string>();
        public List<string> UnlockAbilities { get; set; } = new List<string>();
        public int IntermissionDelayMs { get; set; }
        public string WaveStartMessage { get; set; }
        public int TurnIntervalMs { get; set; }
        public string NextWaveMessage { get; set; }
        public bool AutoAdvanceToBuild { get; set; }
        public int AdvanceDelayMs { get; set; }
        public bool ShowStats { get; set; }
    }

    /// <summary>
    /// Combo Kill system configuration — controls combo window, damage/gold scaling, and cap.
    /// </summary>
    public class ComboConfig
    {
        /// <summary>Seconds since last kill before combo resets to 0. Default: 3.0f</summary>
        public float ComboWindowSeconds { get; set; } = 3.0f;
        /// <summary>Damage bonus per combo kill. 0.05f = +5% per kill. Default: 0.05f</summary>
        public float ComboDamageBonusPerKill { get; set; } = 0.05f;
        /// <summary>Gold bonus per combo kill. 0.10f = +10% per kill. Default: 0.10f</summary>
        public float ComboGoldBonusPerKill { get; set; } = 0.10f;
        /// <summary>Maximum combo damage multiplier. Default: 3.0f (reached at 40 kills)</summary>
        public float ComboMaxMultiplier { get; set; } = 3.0f;
    }

    /// <summary>
    /// Enemy Tile Stacking Penalty configuration — when N enemies occupy the same cell,
    /// each enemy gets a move-speed slow proportional to its stack count.
    /// PenaltyPerStack is applied per additional enemy (so 3 enemies in one cell ⇒
    /// each has 2 × PenaltyPerStack slow, clamped to [MaxStackSlow, 1.0]).
    /// 0 = no penalty (default). Players must use knockback/displacement towers to break up
    /// clumps, otherwise stacked enemies become a slow but dense "wall" for AoE towers.
    /// </summary>
    public class StackingConfig
    {
        /// <summary>Move-speed slow per stacked enemy (0.02 = -2% per stack). Default: 0.02f</summary>
        public float PenaltyPerStack { get; set; } = 0.02f;
        /// <summary>Maximum cumulative slow from stacking (0.5 = at most 50% slow). Default: 0.5f</summary>
        public float MaxStackSlow { get; set; } = 0.5f;
    }

    /// <summary>
    /// Replay / Recording System configuration — controls per-frame telemetry capture.
    /// When Enabled=false, ReplaySystem is constructed but no I/O occurs (zero hot-path cost).
    /// </summary>
    public class ReplayConfig
    {
        /// <summary>Master switch for recording. Default: false (no recording, zero overhead)</summary>
        public bool Enabled { get; set; } = false;
        /// <summary>Flush to disk every N frames to bound data loss on crash. Default: 60</summary>
        public int FlushInterval { get; set; } = 60;
        /// <summary>Maximum frames to record per session (0 = unlimited). Default: 0</summary>
        public int MaxFrames { get; set; } = 0;
    }

    /// <summary>
    /// Auto Skill System configuration — controls which strategy is used to auto-cast skills
    /// during BuildPhase, how many skills can fire per phase, and cooldown protection.
    /// </summary>
    public class AutoSkillConfig
    {
        /// <summary>Enable auto skill casting during BuildPhase. Default: true</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>Maximum number of skills to cast per BuildPhase tick. Default: 2</summary>
        public int MaxSkillsPerPhase { get; set; } = 2;
        /// <summary>Minimum cooldown (seconds) a skill must have to be considered for auto-cast. Default: 0s</summary>
        public float MinCooldownToConsider { get; set; } = 0f;
        /// <summary>Selection strategy for choosing which ready skill to cast. Default: CoolestFirst</summary>
        public AutoSkillStrategy SelectionStrategy { get; set; } = AutoSkillStrategy.CoolestFirst;
    }

    /// <summary>
    /// Shop Reroll configuration — controls how the BuildPhase offer pool can be refreshed.
    /// Inspired by Slay-the-Spire / Monster Train: spend gold to re-roll the 3 offer slots,
    /// with a cost curve and per-phase cap.
    /// </summary>
    public class ShopRerollConfig
    {
        /// <summary>Master switch for the shop reroll system. Default: true</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>Number of offer slots in the shop (Common + Rare + Epic). Default: 3</summary>
        public int OfferSlotCount { get; set; } = 3;
        /// <summary>Cost curve per reroll within a single BuildPhase (index 0 = first reroll). Default: [5, 10, 20, 40]</summary>
        public float[] CostCurve { get; set; } = new float[] { 5f, 10f, 20f, 40f };
        /// <summary>Hard cap on rerolls per BuildPhase. Default: 3</summary>
        public int MaxRerollsPerPhase { get; set; } = 3;
        /// <summary>Pity counter: if no Epic (RarityTier=2) appears in this many consecutive offers, force one in. Default: 10</summary>
        public int PityEpicThreshold { get; set; } = 10;
        /// <summary>Pity counter: if no Rare (RarityTier>=1) appears in this many consecutive offers, force one in. Default: 5</summary>
        public int PityRareThreshold { get; set; } = 5;
        /// <summary>Probability weights for picking a rarity tier per offer (Common, Rare, Epic). Default: [70, 25, 5]</summary>
        public float[] RarityWeights { get; set; } = new float[] { 70f, 25f, 5f };
    }

    /// <summary>
    /// Reforge configuration — controls tower affix reroll cost, lock-slot cost, and rarity weights.
    /// Inspired by Diablo / ARPG Reforge: spend gold to re-roll a tower's affix slot, with
    /// escalating cost and an optional per-slot lock (paying extra gold to preserve the current affix).
    /// Hot-path impact: zero (BuildPhase only).
    /// </summary>
    public class ReforgeConfig
    {
        /// <summary>Master switch for the Reforge system. Default: true</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>Base gold cost of the first reroll on a tower. Default: 50</summary>
        public float BaseCost { get; set; } = 50f;
        /// <summary>Cost increment per subsequent reroll on the same tower (cost = base + count * increment). Default: 50</summary>
        public float IncrementPerReroll { get; set; } = 50f;
        /// <summary>Hard cap on rerolls per tower. Default: 10</summary>
        public int MaxRerollsPerTower { get; set; } = 10;
        /// <summary>Flat gold cost to lock (or unlock) a single slot. Default: 25</summary>
        public float LockSlotCost { get; set; } = 25f;
        /// <summary>Rarity weights for affix picks (Common, Uncommon, Rare, Epic, Legendary). Default: [60, 25, 10, 4, 1]</summary>
        public float[] RarityWeights { get; set; } = new float[] { 60f, 25f, 10f, 4f, 1f };
    }

    /// <summary>
    /// Tower Mastery Level definition — XP threshold and bonuses granted at each level.
    /// </summary>
    public class TowerMasteryLevelConfig
    {
        /// <summary>Mastery level (1 = fresh, 2 = 2nd tier, etc.)</summary>
        public int Level { get; set; }
        /// <summary>Total XP required to reach this level (cumulative threshold)</summary>
        public float XPThreshold { get; set; }
        /// <summary>Damage multiplier bonus granted at this level (e.g. 0.1 = +10% damage)</summary>
        public float DamageBonus { get; set; }
        /// <summary>Attack speed multiplier bonus granted at this level (e.g. 0.05 = +5% attack speed)</summary>
        public float AttackSpeedBonus { get; set; }
        /// <summary>Range bonus granted at this level (flat tiles, e.g. 1 = +1 tile range)</summary>
        public int RangeBonus { get; set; }
    }

    /// <summary>
    /// Tower Mastery System configuration — level progression table and XP sources.
    /// </summary>
    public class TowerMasteryConfig
    {
        /// <summary>XP granted per enemy kill (scaled by enemy gold reward multiplier). Default: 10</summary>
        public float XPPerKill { get; set; } = 10f;
        /// <summary>Elite kill bonus XP (flat add on top of XPPerKill). Default: 50</summary>
        public float XPPerEliteKill { get; set; } = 50f;
        /// <summary>Mastery level definitions sorted by level ascending.</summary>
        public List<TowerMasteryLevelConfig> Levels { get; set; } = new List<TowerMasteryLevelConfig>
        {
            new TowerMasteryLevelConfig { Level = 1, XPThreshold = 0f,    DamageBonus = 0f,    AttackSpeedBonus = 0f, RangeBonus = 0 },
            new TowerMasteryLevelConfig { Level = 2, XPThreshold = 100f,   DamageBonus = 0.05f, AttackSpeedBonus = 0.02f, RangeBonus = 0 },
            new TowerMasteryLevelConfig { Level = 3, XPThreshold = 300f,   DamageBonus = 0.10f, AttackSpeedBonus = 0.05f, RangeBonus = 1 },
            new TowerMasteryLevelConfig { Level = 4, XPThreshold = 600f,  DamageBonus = 0.15f, AttackSpeedBonus = 0.08f, RangeBonus = 1 },
            new TowerMasteryLevelConfig { Level = 5, XPThreshold = 1000f, DamageBonus = 0.20f, AttackSpeedBonus = 0.10f, RangeBonus = 2 },
            new TowerMasteryLevelConfig { Level = 6, XPThreshold = 1500f, DamageBonus = 0.25f, AttackSpeedBonus = 0.12f, RangeBonus = 2 },
            new TowerMasteryLevelConfig { Level = 7, XPThreshold = 2200f, DamageBonus = 0.30f, AttackSpeedBonus = 0.15f, RangeBonus = 3 },
            new TowerMasteryLevelConfig { Level = 8, XPThreshold = 3000f, DamageBonus = 0.35f, AttackSpeedBonus = 0.18f, RangeBonus = 3 },
        };
    }

    /// <summary>
    /// Auto-skill selection strategy when multiple skills are ready.
    /// </summary>
    public enum AutoSkillStrategy
    {
        /// <summary>Best score = large AoE radius / short cooldown. Default.</summary>
        CoolestFirst = 0,
        /// <summary>Shortest cooldown first (most frequent re-use).</summary>
        CooldownShortest = 1,
        /// <summary>Highest damage multiplier first.</summary>
        DamageHighest = 2,
        /// <summary>Largest AoE radius first.</summary>
        AoeLargest = 3,
        /// <summary>Random selection.</summary>
        Random = 4
    }

    /// <summary>
    /// Ascension/Difficulty Modifier definition — loaded from ascension_modifiers.json.
    /// A modifier applies a persistent positive or negative challenge to the player after
    /// completing a run (Slay the Spire-style). Players opt-in to modifiers to earn
    /// higher scores or unlock rewards.
    /// </summary>
    public class AscensionModifierDef
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        // Category: "enemy" | "tower" | "player" | "economy"
        public string Category { get; set; }
        // Stacks: whether this modifier can be applied multiple times (e.g. +1 each level)
        public bool CanStack { get; set; }
        // Max stack count (only meaningful if CanStack=true)
        public int MaxStack { get; set; } = 1;

        // Enemy modifiers
        // HP multiplier applied when enemies spawn (1.3 = +30% HP)
        public float EnemyHpMult { get; set; } = 1.0f;
        // Damage multiplier applied to all enemy attacks
        public float EnemyDamageMult { get; set; } = 1.0f;
        // Speed multiplier applied to all enemy movement
        public float EnemySpeedMult { get; set; } = 1.0f;
        // Flat gold bonus per enemy kill (can be negative to reduce rewards)
        public float EnemyGoldBonus { get; set; } = 0f;
        // Flat HP regeneration per second to all enemies
        public float EnemyRegenRate { get; set; } = 0f;

        // Tower modifiers
        // Damage multiplier applied to all tower attacks (0.8 = -20% tower damage)
        public float TowerDamageMult { get; set; } = 1.0f;
        // Attack speed multiplier for all towers
        public float TowerAttackSpeedMult { get; set; } = 1.0f;
        // Range penalty in tiles (negative = shorter range)
        public int TowerRangePenalty { get; set; } = 0;

        // Player modifiers
        // Starting gold when a new run begins
        public float PlayerStartGold { get; set; } = -1f; // -1 means "use default"
        // Starting lives
        public int PlayerStartLives { get; set; } = -1;  // -1 means "use default"
        // Gold earned multiplier (0.9 = -10% gold from kills)
        public float GoldEarnedMult { get; set; } = 1.0f;

        // Scoring multiplier — rewards score multiplied by this when modifier is active
        public float ScoreMultiplier { get; set; } = 1.0f;
    }

    public class GameConfig
    {
        public PlayerConfig Player { get; set; } = new PlayerConfig();
        public List<SkillConfig> Skills { get; set; } = new List<SkillConfig>();
        public List<MonsterConfig> MonsterTypes { get; set; } = new List<MonsterConfig>();
        public List<TowerConfig> TowerTypes { get; set; } = new List<TowerConfig>();
        public List<SummonDef> Summons { get; set; } = new List<SummonDef>();
        public List<LevelConfig> Levels { get; set; } = new List<LevelConfig>();
        public LevelConfig CurrentLevel { get; set; }

        // Phase behavior keyed by GameState name
        public Dictionary<string, PhaseBehaviorDef> PhaseBehaviors { get; set; } = new Dictionary<string, PhaseBehaviorDef>();

        // Ascension/Difficulty modifier definitions (loaded from ascension_modifiers.json)
        public AscensionModifierDef[] AscensionModifiers { get; set; } = Array.Empty<AscensionModifierDef>();

        // Tower upgrade paths (config-driven upgrade curves)
        public Dictionary<string, TowerUpgradePathConfig> TowerUpgradePaths { get; set; } = new Dictionary<string, TowerUpgradePathConfig>();

        // Wave-based difficulty scaling
        public float DifficultyGrowthPerWave { get; set; } = 0.05f;
        public float PlayerDamageScalingPerWave { get; set; } = 0.05f;

        // ── Player Damage Conversion (Round 102 Direction 7) ─────────────────────
        // Fraction of the player's base attack damage split into the converted type.
        // 0 = no conversion (default, backward compatible). 0.3 = 30% of damage applied
        // as PlayerConvertedDamageType, the rest stays in PlayerDamageType.
        // Clamped at DamageConversionConfig.ConversionDefaultCap inside PlayerTowerAttackSystem
        // to keep damage formulas sane.
        public float PlayerDamageConversionRatio { get; set; } = 0f;
        // The damage type the converted portion is applied as (e.g. Magic to bypass Physical
        // immunity). Default Physical = inert when PlayerDamageConversionRatio = 0.
        public DamageType PlayerConvertedDamageType { get; set; } = DamageType.Physical;

        // Behavior tree definitions keyed by monster type
        public Dictionary<string, BehaviorTreeDef> BehaviorTrees { get; set; } = new Dictionary<string, BehaviorTreeDef>();
        private Dictionary<string, BehaviorTreeDef> _btCache = new Dictionary<string, BehaviorTreeDef>();
        private Dictionary<string, BattleSystemECS.Systems.BTCachedTree> _cachedBtCache = new Dictionary<string, BattleSystemECS.Systems.BTCachedTree>();
        private Dictionary<string, MonsterConfig> _monsterCache = new Dictionary<string, MonsterConfig>();

        // Enemy abilities keyed by ability id
        public List<EnemyAbilityDef> EnemyAbilities { get; set; } = new List<EnemyAbilityDef>();

        // Buff definitions for UpgradeSystem (Bug#31 fix: was hardcoded strings)
        public List<string> UpgradeBuffs { get; set; } = new List<string> { "Attack+10%", "Crit Rate+5%", "Defense+10%" };

        // Combo Kill configuration
        public ComboConfig Combo { get; set; } = new ComboConfig();

        // Enemy tile-stacking penalty configuration (move-speed slow when N enemies share a cell)
        public StackingConfig Stacking { get; set; } = new StackingConfig();

        // Replay / Recording configuration (per-frame telemetry capture)
        public ReplayConfig Replay { get; set; } = new ReplayConfig();

        // Auto Skill configuration (BuildPhase auto-casting)
        public AutoSkillConfig AutoSkill { get; set; } = new AutoSkillConfig();

        // Shop Reroll configuration (BuildPhase offer pool refresh)
        public ShopRerollConfig ShopReroll { get; set; } = new ShopRerollConfig();

        // Reforge configuration (tower affix reroll — Split B)
        public ReforgeConfig Reforge { get; set; } = new ReforgeConfig();

        // Wave Skip Reward configuration (BuildPhase "skip wave → gain dmg bonus" option)
        public WaveSkipConfig WaveSkip { get; set; } = new WaveSkipConfig();

        // Tower Overcharge configuration
        public TowerOverchargeConfig TowerOvercharge { get; set; } = new TowerOverchargeConfig();

        // Tower Mastery / XP system configuration
        public TowerMasteryConfig TowerMastery { get; set; } = new TowerMasteryConfig();

        // Wave mutator definitions (loaded from wave_mutators.json)
        public WaveMutatorDef[] WaveMutatorDefs { get; set; } = Array.Empty<WaveMutatorDef>();

        // Enemy fission definitions (loaded from enemy_fission.json)
        public FissionDef[] FissionDefs { get; set; } = Array.Empty<FissionDef>();

        // Enemy life link definitions (loaded from life_link.json)
        public LifeLinkDef[] LifeLinkDefs { get; set; } = Array.Empty<LifeLinkDef>();

        // Look up a life link def by its LifeLinkId
        public LifeLinkDef GetLifeLinkDef(string lifeLinkId)
        {
            return LifeLinkDefs.FirstOrDefault(l => l.LifeLinkId == lifeLinkId);
        }

        // Get life link def index by source monster type (returns -1 if none)
        public int GetLifeLinkDefIdBySourceType(string monsterType)
        {
            for (int i = 0; i < LifeLinkDefs.Length; i++)
            {
                if (LifeLinkDefs[i].SourceMonsterType == monsterType) return i;
            }
            return -1;
        }

        // Nest / spawner structure definitions (loaded from nests.json)
        public NestDef[] NestDefs { get; set; } = Array.Empty<NestDef>();

        // Gold-stealing thief definitions (inline with monster configs)
        public ThiefDef[] ThiefDefs { get; set; } = Array.Empty<ThiefDef>();

        // Heal-over-time healer unit definitions
        public HealerDef[] HealerDefs { get; set; } = Array.Empty<HealerDef>();

        // Look up a healer def by its Id
        public HealerDef GetHealerDef(string healerId)
        {
            return HealerDefs.FirstOrDefault(h => h.Id == healerId);
        }

        // ==================== 塔词缀系统 (Tower Affix — Reforge Split A) ====================
        // Defines a single tower affix (prefix/suffix mod) usable in the Reforge system.
        // Each affix modifies a single stat by a multiplier (Multiplicative) or flat add (Additive).
        // Loaded from Data/Configs/tower_affixes.json; the Reforge system will pick from this pool
        // (Round 35, Reforge — Split B).
        public class TowerAffixDef
        {
            // Unique affix identifier (e.g., "damage_pct", "crit_chance", "chain_targets").
            public string AffixId { get; set; } = "";
            // Human-readable name for UI display (e.g., "Sharpened", "Piercing", "Vampiric").
            public string Name { get; set; } = "";
            // Stat this affix modifies: "Damage" | "Range" | "AttackSpeed" | "CritChance" |
            // "CritMultiplier" | "ChainTargets" | "PierceCount" | "ArmorPenetration" |
            // "KnockbackChance" | "ExecuteThreshold" | "LifeOnKill" | "ManaOnHit" | "GoldOnHit" |
            // "CooldownReduction" | "MultishotCount" | "SplashRadius" | "Accuracy".
            public string Stat { get; set; } = "";
            // Magnitude: percent (0.15 = +15%) for multiplicative stats, or flat add for additive stats.
            // Sign convention: positive = buff, negative = debuff (e.g., "-0.1" CooldownReduction is invalid).
            public float Magnitude { get; set; } = 0f;
            // Rarity tier: 0=Common, 1=Uncommon, 2=Rare, 3=Epic, 4=Legendary. Drives weighted random in Reforge.
            public int Rarity { get; set; } = 0;
            // Min tower level required for this affix to roll (0 = any level).
            public int MinLevel { get; set; } = 0;
            // Max stack count on a single slot (1 = no stacking, N = up to N of this affix on one slot).
            public int MaxStack { get; set; } = 1;
            // Optional description for tooltips/UI.
            public string Description { get; set; } = "";
        }

        // Tower affix pool (loaded from Data/Configs/tower_affixes.json).
        public TowerAffixDef[] TowerAffixes { get; set; } = Array.Empty<TowerAffixDef>();

        // ==================== 塔类型专精 (Tower Modifier — Round 145 方向3) ====================
        // Per-tower passive modifier — distinct from the stackable affix system.
        // Each tower rolls ONE modifier from the weighted pool at placement time.
        // Modifiers are read-only stat / effect descriptors consumed by combat systems
        // (combat-side integration is left to a follow-up round; Round 145 establishes
        // the config + roll + storage + read API surface only).
        //
        // ModifierId  — index into GameConfig.TowerModifiers[] (-1 = no modifier)
        // Magnitude   — designer-tuned scalar the consuming system applies to its effect
        // Rarity      — 0=Common, 1=Uncommon, 2=Rare, 3=Epic, 4=Legendary (mirrors affix)
        // Tier        — placement-time weighted random bracket (heavier bias toward Common)
        public class TowerModifierDef
        {
            public string ModifierId { get; set; } = "";
            public string Name { get; set; } = "";
            // Stat this modifier applies to: "CritChance" | "CritMultiplier" |
            // "LifeOnKill" | "GoldOnKill" | "ManaOnHit" | "AttackSpeed" |
            // "CooldownReduction" | "DamageVsFullHp" | "DamageVsLowHp" | "ExecuteThreshold".
            // Consumed by combat systems — Round 145 only stores / surfaces the value.
            public string Stat { get; set; } = "";
            public float Magnitude { get; set; } = 0f;
            public int Rarity { get; set; } = 0;
            public int Weight { get; set; } = 1;
            public string Description { get; set; } = "";
        }

        // Tower modifier pool (loaded from Data/Configs/tower_modifiers.json).
        public TowerModifierDef[] TowerModifiers { get; set; } = Array.Empty<TowerModifierDef>();

        // Look up a tower modifier def by its ModifierId (returns null if not found).
        public TowerModifierDef? GetTowerModifierDef(string modifierId)
        {
            if (string.IsNullOrEmpty(modifierId)) return null;
            return Array.Find(TowerModifiers, m => m.ModifierId == modifierId);
        }

        // Get the index of a tower modifier in TowerModifiers[] by ModifierId (returns -1 if not found).
        public int GetTowerModifierIndex(string modifierId)
        {
            if (string.IsNullOrEmpty(modifierId)) return -1;
            return Array.FindIndex(TowerModifiers, m => m.ModifierId == modifierId);
        }

        // Look up a tower modifier def by its index in TowerModifiers[] (returns null if not found).
        public TowerModifierDef? GetTowerModifierDef(int index)
        {
            if (index < 0 || index >= TowerModifiers.Length) return null;
            return TowerModifiers[index];
        }

        // Round 143 Direction 1 — Tower-vs-Enemy Type Effectiveness Matrix
        // Loaded from Data/Configs/tower_effectiveness.json. Keyed by
        // (TowerType enum index, enemy Type string). Missing entries default to 1.0.
        // The dictionary key is built as "<towerTypeIndex>|<enemyType>" to avoid
        // composite-key allocation on the hot path. Stored as a flat dictionary for
        // O(1) lookups.
        public Dictionary<string, float> TowerEffectivenessMatrix { get; set; } = new Dictionary<string, float>();
        // Total entries loaded (informational; for log messages and tests).
        public int TowerEffectivenessEntryCount { get; set; } = 0;

        // Look up a tower affix def by its AffixId (returns null if not found).
        public TowerAffixDef GetTowerAffixDef(string affixId)
        {
            return Array.Find(TowerAffixes, a => a.AffixId == affixId);
        }

        // Get the index of a tower affix in TowerAffixes[] by AffixId (returns -1 if not found).
        public int GetTowerAffixIndex(string affixId)
        {
            return Array.FindIndex(TowerAffixes, a => a.AffixId == affixId);
        }

        // Look up a fission def by its FissionId
        public FissionDef GetFissionDef(string fissionId)
        {
            return FissionDefs.FirstOrDefault(f => f.FissionId == fissionId);
        }

        // Look up a fission def by source monster type
        public FissionDef GetFissionDefBySourceType(string monsterType)
        {
            return FissionDefs.FirstOrDefault(f => f.SourceMonsterType == monsterType);
        }

        // GetFissionDefId: returns index into FissionDefs[] for a monster type, -1 if none
        public int GetFissionDefIdBySourceType(string monsterType)
        {
            return Array.FindIndex(FissionDefs, f => f.SourceMonsterType == monsterType);
        }

        // Look up a thief def by its Id
        public ThiefDef GetThiefDef(string thiefId)
        {
            return ThiefDefs.FirstOrDefault(t => t.Id == thiefId);
        }

        // Enemy morph definitions (loaded from enemy_morphs.json)
        public MorphDef[] MorphDefs { get; set; } = Array.Empty<MorphDef>();

        // GetMorphDefId: returns index into MorphDefs[] for a monster type, -1 if none
        public int GetMorphDefIdBySourceType(string monsterType)
        {
            return Array.FindIndex(MorphDefs, m => m.SourceMonsterType == monsterType);
        }

        /// <summary>
        /// Enemy clone (duplicate mid-wave) definition — loaded from enemy_clones.json.
        /// When a monster with cloning capability meets its trigger condition, it spawns
        /// a functional clone that shares its stats and behavior.
        /// </summary>
        public class CloneDef
        {
            public string CloneId { get; set; } = "";
            // Source monster type that triggers cloning (e.g., "Doppelganger")
            public string SourceMonsterType { get; set; } = "";
            // Maximum number of active clones this enemy can have at once
            public int MaxClones { get; set; } = 2;
            // Health multiplier for clones relative to master's current health
            public float CloneHpMult { get; set; } = 0.6f;
            // Clone cooldown in seconds between clone attempts
            public float CloneCooldown { get; set; } = 8f;
            // Clone duration in seconds (0 = permanent clone, -1 = no duration tracking)
            // When duration > 0, clone is killed when timer expires
            public float CloneDuration { get; set; } = 0f;
            // Trigger type: "HP_THRESHOLD" | "TIME"
            public string TriggerType { get; set; } = "HP_THRESHOLD";
            // For HP_THRESHOLD: clone when health drops below this fraction (0.0-1.0)
            // For TIME: clone after this many seconds since spawn (requires age tracking)
            public float TriggerValue { get; set; } = 0.3f;
            public string Description { get; set; } = "";
        }

        // Enemy clone definitions (loaded from enemy_clones.json)
        public CloneDef[] CloneDefs { get; set; } = Array.Empty<CloneDef>();

        // GetCloneDefId: returns index into CloneDefs[] for a monster type, -1 if none
        public int GetCloneDefIdBySourceType(string monsterType)
        {
            return Array.FindIndex(CloneDefs, c => c.SourceMonsterType == monsterType);
        }

        /// <summary>
        /// Hot zone definition — a pre-defined map region that grants placement bonuses to towers.
        /// </summary>
        public class HotZoneDef
        {
            /// <summary>Unique identifier for this hot zone.</summary>
            public string Id { get; set; } = "";
            /// <summary>Center X position in grid cells.</summary>
            public int CenterX { get; set; }
            /// <summary>Center Y position in grid cells.</summary>
            public int CenterY { get; set; }
            /// <summary>Radius in grid cells (circular region).</summary>
            public int Radius { get; set; }
            /// <summary>Damage multiplier bonus (e.g. 0.15 = +15% damage).</summary>
            public float DamageBonus { get; set; }
            /// <summary>Range bonus in cells (added to TowerRange during attack).</summary>
            public float RangeBonus { get; set; }
            /// <summary>Attack speed multiplier bonus (e.g. 0.1 = +10% attack speed).</summary>
            public float SpeedBonus { get; set; }
            /// <summary>Description for UI display.</summary>
            public string Name { get; set; } = "";
        }

        /// <summary>
        /// Hot zone definitions for map terrain bonuses.
        /// Default empty — loaded from Data/Configs/hot_zones.json by HotZoneSystem.
        /// </summary>
        public List<HotZoneDef> HotZoneDefs { get; set; } = new List<HotZoneDef>();

        // Pickup item definitions (loaded from pickup_defs.json)
        public PickupDef[] PickupDefs { get; set; } = Array.Empty<PickupDef>();

        // Pickup rarity roll configuration (loaded from pickup_defs.json → "PickupRarity" object)
        public PickupRarityConfig PickupRarity { get; set; } = new PickupRarityConfig();

        // Round 130 — Inventory item definitions (loaded from items.json).
        // Used by InventorySystem to define consumable items (potions / grenades / scrolls / shield sigils).
        // Each item has ItemType (heal/mana/shield/speedBoost/damageBoost/aoeBurst/summon/cleanse)
        // and Value (magnitude); InventorySystem dispatches by ItemType.
        public ItemDef[] ItemDefs { get; set; } = Array.Empty<ItemDef>();

        // Obstacle definitions (loaded from obstacles.json)
        public ObstacleDef[] ObstacleDefs { get; set; } = Array.Empty<ObstacleDef>();

        // Destructible object definitions (loaded from destructibles.json). Round 95 Direction 5.
        // Indexed by type id; level JSON references destructible defs by their Id string and
        // is converted to a type id by GameManager at level-load time.
        public DestructibleDef[] DestructibleDefs { get; set; } = Array.Empty<DestructibleDef>();

        // Map dimensions (Bug#30 fix: magic numbers 10 and 20 in GameManager/EnemyMovementSystem)
        public int MapWidth { get; set; } = 10;
        public int MapHeight { get; set; } = 20;

        // Weather system configuration
        public WeatherConfig Weather { get; set; } = new WeatherConfig();

        // Day/Night cycle system configuration
        public DayNightConfig DayNight { get; set; } = new DayNightConfig();

        // Terrain system configuration (direction 2)
        public List<TerrainTypeConfig> TerrainTypes { get; set; } = new List<TerrainTypeConfig>();
        public int[][] MapTerrainGrid { get; set; } = Array.Empty<int[]>();

        // Corpse ground effect definitions (direction 9)
        public List<CorpseEffectDef> CorpseEffectDefs { get; set; } = new List<CorpseEffectDef>();

        // Path modifier tower definitions (direction 7)
        public List<PathModifierDef> PathModifiers { get; set; } = new List<PathModifierDef>();

        // Random mid-wave event system configuration (direction 9)
        public RandomEventConfig RandomEvents { get; set; } = new RandomEventConfig();

        // ── Daily Challenge / Rotating Seed (Round 105 Direction 9) ────────────
        // The pool of available daily modifiers (loaded from Data/Configs/daily_modifiers.json).
        // When empty, the daily system is a no-op and stock values are used.
        public List<DailyModifierDef> DailyModifierPool { get; set; } = new List<DailyModifierDef>();
        // Number of modifiers to pick per day (default 3). Configurable so designers
        // can ramp difficulty curve per season.
        public int DailyModifierCount { get; set; } = 3;
        // Resolved daily challenge for the current run. Filled in by
        // DailyChallengeSystem.ApplyToConfig — null when the system is disabled
        // (no JSON pool, or pool is empty).
        public DailyChallengeResult? DailyLastResult { get; set; } = null;
        // Daily multiplicative damage modifier (default 1.0 = inert).
        public float DailyDamageMult { get; set; } = 1.0f;
        // Daily multiplicative gold modifier (default 1.0 = inert).
        public float DailyGoldMult { get; set; } = 1.0f;
        // Daily multiplicative enemy HP modifier (default 1.0 = inert).
        public float DailyEnemyHpMult { get; set; } = 1.0f;
        // Daily additive starting-gold bonus (default 0 = inert).
        public float DailyStartingGoldBonus { get; set; } = 0f;

        // ── Meta Progression / Prestige (cross-run unlocks) ───────────────
        // Definitions of all available prestige nodes (loaded from Data/Configs/meta_progression.json)
        public List<MetaProgressionNode> PrestigeNodes { get; set; } = new List<MetaProgressionNode>();
        // Resolved multipliers from active prestige nodes (computed once at boot).
        // All default to 1.0f / 0f so that systems without any prestige reads work normally.
        public float MetaDamageMult { get; set; } = 1.0f;        // applied to player base damage
        public float MetaGoldEarnedMult { get; set; } = 1.0f;    // applied to gold earned
        public float MetaStartingGoldBonus { get; set; } = 0f;   // additive bonus to starting gold
        public int MetaStartingLivesBonus { get; set; } = 0;     // additive bonus to starting lives
        public float MetaCritRateBonus { get; set; } = 0f;       // additive bonus to crit rate (0-1)
        public float MetaAttackSpeedMult { get; set; } = 1.0f;   // applied to attack speed
        public int MetaFreeTechLevels { get; set; } = 0;         // number of free tech-tree levels
        public int MetaBonusStartingGold { get; set; } = 0;      // flat starting gold bonus (alternative to bonus above)

        /// <summary>
        /// Look up a prestige node by its id. Returns null if not found.
        /// </summary>
        public MetaProgressionNode GetPrestigeNode(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || PrestigeNodes == null) return null;
            for (int i = 0; i < PrestigeNodes.Count; i++)
            {
                if (PrestigeNodes[i].Id == nodeId) return PrestigeNodes[i];
            }
            return null;
        }

        // Bank / Interest system configuration (direction 2)
        public BankConfig Bank { get; set; } = new BankConfig();
        // Mana/Energy pool system (direction 5)
        public ManaConfig Mana { get; set; } = new ManaConfig();
        // Player global skills / ultimates (direction 5)
        public List<GlobalSkillDef> GlobalSkills { get; set; } = new List<GlobalSkillDef>();

        public GameConfig()
        {
            InitializeDefaultConfig();
        }

        private void InitializeDefaultConfig()
        {
            // Default skills
            Skills.Add(new SkillConfig
            {
                Name = "Cross Slash",
                Description = "十字范围伤害 - 400% 伤害倍率，3x3 十字形范围",
                DamageMultiplier = 4f,
                AreaWidth = 3,
                AreaHeight = 3,
                AttackRange = 3,
                Cooldown = 5f,
                AutoCast = false,
                Hotkey = "1"
            });

            Skills.Add(new SkillConfig
            {
                Name = "Mega Explosion",
                Description = "3x3 范围伤害 - 400% 伤害倍率，9 格范围",
                DamageMultiplier = 4f,
                AreaWidth = 3,
                AreaHeight = 3,
                AttackRange = 5,
                Cooldown = 10f,
                AutoCast = false,
                Hotkey = "2"
            });

            Skills.Add(new SkillConfig
            {
                Name = "Sniper Shot",
                Description = "超远距离单体攻击 - 400% 伤害倍率，9 格攻击距离",
                DamageMultiplier = 4f,
                AreaWidth = 1,
                AreaHeight = 1,
                AttackRange = 9,
                Cooldown = 8f,
                AutoCast = false,
                Hotkey = "3"
            });

            // Default towers — now with debuff fields so TowerPlacementSystem.GetTowerConfig() finds them
            TowerTypes.Add(new TowerConfig
            {
                Name = "Basic Tower",
                Type = TowerType.Basic,
                Damage = 10f,
                Range = 3,
                AttackSpeed = 1f,
                Cost = 50f,
                UpgradeCost = 30f,
                StunChance = 0.10f,   // 10% stun on hit
                SlowAmount = 0f,
                SlowDuration = 0f
            });

            TowerTypes.Add(new TowerConfig
            {
                Name = "Sniper Tower",
                Type = TowerType.Sniper,
                Damage = 25f,
                Range = 8,
                AttackSpeed = 0.5f,
                Cost = 100f,
                UpgradeCost = 60f,
                StunChance = 0.05f,   // 5% stun — precision shot can stun briefly
                SlowAmount = 0f,
                SlowDuration = 0f
            });

            TowerTypes.Add(new TowerConfig
            {
                Name = "AOE Tower",
                Type = TowerType.AOE,
                Damage = 8f,
                Range = 2,
                AttackSpeed = 1.5f,
                Cost = 75f,
                UpgradeCost = 45f,
                StunChance = 0f,
                SlowAmount = 0.30f,   // 30% slow on hit (area of effect)
                SlowDuration = 1f
            });

            // Frost Tower — dedicated cryo tower, applies heavy slow
            TowerTypes.Add(new TowerConfig
            {
                Name = "Frost Tower",
                Type = TowerType.Frost,
                Damage = 6f,
                Range = 3,
                AttackSpeed = 1.2f,
                Cost = 80f,
                UpgradeCost = 48f,
                StunChance = 0f,
                SlowAmount = 0.50f,   // 50% slow on hit
                SlowDuration = 2f
            });

            // Stun Tower — dedicated stun tower, high stun chance
            TowerTypes.Add(new TowerConfig
            {
                Name = "Stun Tower",
                Type = TowerType.Stun,
                Damage = 8f,
                Range = 3,
                AttackSpeed = 0.8f,
                Cost = 90f,
                UpgradeCost = 54f,
                StunChance = 0.35f,   // 35% stun on hit
                SlowAmount = 0f,
                SlowDuration = 0f
            });

            // EMP Tower — silence/disable tower (future: enemy ability suppression)
            TowerTypes.Add(new TowerConfig
            {
                Name = "EMP Tower",
                Type = TowerType.EMP,
                Damage = 10f,
                Range = 4,
                AttackSpeed = 0.6f,
                Cost = 100f,
                UpgradeCost = 60f,
                StunChance = 0.15f,   // 15% stun on hit
                SlowAmount = 0.20f,   // 20% slow
                SlowDuration = 1f
            });

            // Tesla Tower — chain lightning tower with built-in SpecialAbility
            TowerTypes.Add(new TowerConfig
            {
                Name = "Tesla Coil",
                Type = TowerType.Tesla,
                Damage = 8f,
                Range = 4,
                AttackSpeed = 1.5f,
                Cost = 70f,
                UpgradeCost = 40f,
                StunChance = 0f,
                SlowAmount = 0f,
                SlowDuration = 0f,
                SpecialAbility = new TowerSpecialAbility
                {
                    AbilityType = "chain_lightning",
                    Radius = 3
                }
            });

            // Default monsters
            MonsterTypes.Add(new MonsterConfig
            {
                Name = "Normal Slime",
                Type = "Normal",
                Health = 20f,
                MaxHealth = 20f,
                Damage = 5f,
                MoveSpeed = 1f,
                AttackRange = 1f,
                AttackInterval = 1.5f,
                GoldReward = 10,
                Skills = new List<string> { "Normal Attack" }
            });

            MonsterTypes.Add(new MonsterConfig
            {
                Name = "Fast Slime",
                Type = "Fast",
                Health = 15f,
                MaxHealth = 15f,
                Damage = 3f,
                MoveSpeed = 2f,
                AttackRange = 1f,
                AttackInterval = 1f,
                GoldReward = 15,
                Skills = new List<string> { "Normal Attack", "Quick Dash" },
                // Path deviation: sine wave lateral drift (Fast Slime skitters sideways).
                PathDeviationType = 1,
                PathDeviationAmplitude = 0.4f
            });

            MonsterTypes.Add(new MonsterConfig
            {
                Name = "Strong Slime",
                Type = "Strong",
                Health = 30f,
                MaxHealth = 30f,
                Damage = 8f,
                MoveSpeed = 0.5f,
                AttackRange = 2f,
                AttackInterval = 2f,
                GoldReward = 20,
                Skills = new List<string> { "Normal Attack", "Heavy Strike" }
            });

            MonsterTypes.Add(new MonsterConfig
            {
                Name = "Ranged Slime",
                Type = "Ranged",
                Health = 15f,
                MaxHealth = 15f,
                Damage = 6f,
                MoveSpeed = 0.8f,
                AttackRange = 5f,
                AttackInterval = 1.2f,
                GoldReward = 25,
                Skills = new List<string> { "Normal Attack", "Ranged Shot" }
            });

            // Stat Drainer: a specialist that drains nearby tower damage up to 50% cap,
            // at 10% per second within 3.5 tiles radius. When the drainer dies, the
            // tower's full damage is restored. This forces the player to kill or push
            // back the drainer before its presence permanently cripples a tower.
            MonsterTypes.Add(new MonsterConfig
            {
                Name = "Stat Drainer",
                Type = "Drainer",
                Health = 40f,
                MaxHealth = 40f,
                Damage = 2f,
                MoveSpeed = 0.6f,
                AttackRange = 1f,
                AttackInterval = 1.5f,
                GoldReward = 30,
                Skills = new List<string> { "Normal Attack", "Stat Drain" },
                DrainRatio = 0.5f,
                DrainRadius = 3.5f,
                DrainRate = 0.1f
            });

            // Default levels
            var level1 = new LevelConfig { LevelNumber = 1, WaveCount = 3 };
            for (int i = 1; i <= 3; i++)
            {
                level1.Waves.Add(new WaveConfig { WaveNumber = i, MonsterType = "Normal", EnemyCount = 100 });
            }
            Levels.Add(level1);

            // Default tower upgrade paths (replaces hardcoded +20%/+1/+1.5x in TowerUpgradeSystem)
            // "standard" — matches the original hardcoded curve
            // Note: undefined levels fall back to the highest defined level
            TowerUpgradePaths["standard"] = new TowerUpgradePathConfig
            {
                Id = "standard",
                Description = "Standard upgrade path: +20% damage, +1 range, +1.5x cost per level",
                Levels = new Dictionary<int, TowerUpgradeLevelConfig>
                {
                    { 1, new TowerUpgradeLevelConfig { DamageMultiplier = 1.2f, RangeAdd = 1f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.5f } },
                    { 2, new TowerUpgradeLevelConfig { DamageMultiplier = 1.2f, RangeAdd = 0f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.5f, SpecialAbility = TowerUpgradeAbility.SplashDamage, SpecialAbilityParam = 1f } },
                    { 3, new TowerUpgradeLevelConfig { DamageMultiplier = 1.2f, RangeAdd = 0f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.5f, SpecialAbility = TowerUpgradeAbility.ChainLightning, SpecialAbilityParam = 0f } },
                    { 4, new TowerUpgradeLevelConfig { DamageMultiplier = 1.2f, RangeAdd = 0f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.5f, SpecialAbility = TowerUpgradeAbility.FreezeAoe, SpecialAbilityParam = 0f } },
                }
            };

            // "fast" — prioritizes attack speed, minimal range growth (suitable for Weapon/Fast towers)
            TowerUpgradePaths["fast"] = new TowerUpgradePathConfig
            {
                Id = "fast",
                Description = "Fast upgrade path: +15% damage, +0.5 range, +25% attack speed, +1.6x cost",
                Levels = new Dictionary<int, TowerUpgradeLevelConfig>
                {
                    { 1, new TowerUpgradeLevelConfig { DamageMultiplier = 1.15f, RangeAdd = 0.5f, AttackSpeedMultiplier = 1.25f, CostMultiplier = 1.6f } },
                    { 2, new TowerUpgradeLevelConfig { DamageMultiplier = 1.15f, RangeAdd = 0f, AttackSpeedMultiplier = 1.10f, CostMultiplier = 1.6f, SpecialAbility = TowerUpgradeAbility.CriticalStrike, SpecialAbilityParam = 0.25f } },
                    { 3, new TowerUpgradeLevelConfig { DamageMultiplier = 1.15f, RangeAdd = 0f, AttackSpeedMultiplier = 1.05f, CostMultiplier = 1.6f, SpecialAbility = TowerUpgradeAbility.SplashDamage, SpecialAbilityParam = 1f } },
                    { 4, new TowerUpgradeLevelConfig { DamageMultiplier = 1.15f, RangeAdd = 0f, AttackSpeedMultiplier = 1.05f, CostMultiplier = 1.6f, SpecialAbility = TowerUpgradeAbility.ChainLightning, SpecialAbilityParam = 0f } },
                }
            };

            // "tank" — prioritizes damage and range (suitable for Defense/Special towers)
            TowerUpgradePaths["tank"] = new TowerUpgradePathConfig
            {
                Id = "tank",
                Description = "Tank upgrade path: +30% damage, +2 range, +1.4x cost",
                Levels = new Dictionary<int, TowerUpgradeLevelConfig>
                {
                    { 1, new TowerUpgradeLevelConfig { DamageMultiplier = 1.3f, RangeAdd = 2f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.4f } },
                    { 2, new TowerUpgradeLevelConfig { DamageMultiplier = 1.3f, RangeAdd = 0f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.4f, SpecialAbility = TowerUpgradeAbility.ArmorPierce, SpecialAbilityParam = 0.5f } },
                    { 3, new TowerUpgradeLevelConfig { DamageMultiplier = 1.3f, RangeAdd = 0f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.4f, SpecialAbility = TowerUpgradeAbility.CriticalStrike, SpecialAbilityParam = 0.35f } },
                    { 4, new TowerUpgradeLevelConfig { DamageMultiplier = 1.3f, RangeAdd = 0f, AttackSpeedMultiplier = 1.0f, CostMultiplier = 1.4f, SpecialAbility = TowerUpgradeAbility.FreezeAoe, SpecialAbilityParam = 0f } },
                }
            };

            // Load tower upgrade paths from JSON config (overrides C# defaults where specified)
            LoadTowerUpgradePathsFromJson();

            // Default player
            Player = new PlayerConfig
            {
                Name = "Player",
                Type = "Tower",
                AttackRange = 3f,
                AttackInterval = 1f,
                AttackDamage = 10f,
                MaxHealth = 200f,
                CurrentLevel = 1,
                UpgradeThreshold = 1000f,
                StartingSkills = new List<string> { "Cross Slash", "Mega Explosion", "Sniper Shot" }
            };

            // Default upgrade buffs (Bug#31 fix: moved from UpgradeSystem hardcoded strings)
            // Field initializer provides the canonical 3 buffs: Attack+10%, Crit Rate+5%, Defense+10%
            // These match the buff names consumed by PlayerTowerAttackSystem.cs

            if (Levels.Count > 0)
            {
                CurrentLevel = Levels[0];
            }
        }

        public MonsterConfig GetMonsterConfig(string type)
        {
            if (_monsterCache.TryGetValue(type, out var cached))
                return cached;
            var found = MonsterTypes.Find(m => m.Type == type);
            if (found != null)
                _monsterCache[type] = found;
            return found;
        }

        // Round 119 Dir 3 — typeId-based lookup used by Boss Phase minion summon. typeId is the
        // index into MonsterTypes (NOT the string Type). Returns null for out-of-range or
        // negative typeId so callers can early-out without an exception. The cache-by-string
        // path above is unused here because minions are spawned infrequently (1-2 per phase
        // transition, max ~32 per boss) — a per-call linear scan is fine.
        public MonsterConfig GetMonsterConfigByTypeId(int typeId)
        {
            if (typeId < 0 || typeId >= MonsterTypes.Count) return null;
            return MonsterTypes[typeId];
        }

        public LevelConfig GetLevelConfig(int levelNumber)
        {
            return Levels.Find(l => l.LevelNumber == levelNumber);
        }

        public SkillConfig GetSkillConfig(string skillName)
        {
            return Skills.Find(s => s.Name == skillName);
        }

        public TowerConfig GetTowerConfig(string type)
        {
            return TowerTypes.Find(t => t.Type.ToString() == type);
        }

        // ── Build Queue (BuildPhase 预排多塔位) ────────────────────────────────
        // BuildQueueInterval: seconds between automatic PlaceTower calls when draining
        // the build queue. 0.2f = 5 placements/sec (smooth visual + WavePhase pacing).
        private float _buildQueueInterval = 0.2f;
        public float BuildQueueInterval { get => _buildQueueInterval; set => _buildQueueInterval = value; }

        public BehaviorTreeDef GetBehaviorTree(string monsterType)
        {
            if (string.IsNullOrEmpty(monsterType)) return null;
            if (_btCache.TryGetValue(monsterType, out var cached))
                return cached;
            if (BehaviorTrees.TryGetValue(monsterType, out var bt))
            {
                _btCache[monsterType] = bt;
                return bt;
            }
            return null;
        }

        /// <summary>
        /// Returns the pre-built O(1) cached behavior tree for this monster type.
        /// Builds the cache on first call; subsequent calls are O(1) dictionary hit.
        /// </summary>
        public BattleSystemECS.Systems.BTCachedTree GetCachedBehaviorTree(string monsterType)
        {
            if (string.IsNullOrEmpty(monsterType)) return null;
            if (_cachedBtCache.TryGetValue(monsterType, out var cached))
                return cached;
            // Bug#35 fix: query BehaviorTrees directly instead of via GetBehaviorTree()
            // to avoid the double dictionary lookup (BehaviorTrees.TryGetValue + _btCache.TryGetValue).
            // The _btCache still works as a side effect for GetBehaviorTree() callers.
            if (!BehaviorTrees.TryGetValue(monsterType, out var bt)) return null;
            var cachedBt = BattleSystemECS.Systems.BTCachedTreeBuilder.Build(bt);
            _cachedBtCache[monsterType] = cachedBt;
            return cachedBt;
        }

        /// <summary>
        /// Returns upgrade buff options (Bug#31 fix: was hardcoded in UpgradeSystem).
        /// </summary>
        public IReadOnlyList<string> GetUpgradeBuffs() => UpgradeBuffs;

        /// <summary>
        /// Returns the upgrade path config for the given pathId, or null if not found.
        /// </summary>
        public TowerUpgradePathConfig GetUpgradePath(string pathId)
        {
            if (string.IsNullOrEmpty(pathId)) return null;
            TowerUpgradePaths.TryGetValue(pathId, out var path);
            return path;
        }

        /// <summary>
        /// Returns the per-level upgrade config for the given path and level.
        /// Falls back to the highest defined level if the exact level is not defined.
        /// Returns null if the path is not found.
        /// </summary>
        public TowerUpgradeLevelConfig GetUpgradeLevelConfig(string pathId, int level)
        {
            var path = GetUpgradePath(pathId);
            if (path == null || path.Levels == null || path.Levels.Count == 0) return null;

            if (path.Levels.TryGetValue(level, out var levelCfg))
                return levelCfg;

            // Fall back to the highest defined level
            int highestLevel = path.Levels.Keys.Max();
            return path.Levels[highestLevel];
        }

        /// <summary>
        /// Returns phase behavior settings for the given GameState name.
        /// Returns null if not configured.
        /// </summary>
        public PhaseBehaviorDef GetPhaseBehavior(string stateName)
        {
            if (string.IsNullOrEmpty(stateName)) return null;
            PhaseBehaviors.TryGetValue(stateName, out var def);
            return def;
        }

        /// <summary>
        /// Loads tower upgrade path definitions from an external JSON file.
        /// Supported file: Data/Configs/tower_upgrade_paths.json
        /// Overrides only the path IDs present in the file; C# defaults remain for all others.
        /// If the file does not exist, C# defaults are used unchanged (safe fallback).
        /// </summary>
        private void LoadTowerUpgradePathsFromJson()
        {
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string jsonPath = Path.Combine(basePath, "Data", "Configs", "tower_upgrade_paths.json");
            if (!File.Exists(jsonPath)) return;

            try
            {
                string json = File.ReadAllText(jsonPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("paths", out var pathsElement) && pathsElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var pathElem in pathsElement.EnumerateArray())
                    {
                        string pathId = pathElem.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                        if (string.IsNullOrEmpty(pathId)) continue;

                        var cfg = new TowerUpgradePathConfig { Id = pathId };
                        if (pathElem.TryGetProperty("description", out var descProp))
                            cfg.Description = descProp.GetString();

                        cfg.Levels = new Dictionary<int, TowerUpgradeLevelConfig>();
                        if (pathElem.TryGetProperty("levels", out var levelsElem) && levelsElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var levelElem in levelsElem.EnumerateArray())
                            {
                                int level = levelElem.TryGetProperty("level", out var lvProp) ? lvProp.GetInt32() : 0;
                                if (level <= 0) continue;

                                var lc = new TowerUpgradeLevelConfig();
                                if (levelElem.TryGetProperty("damageMultiplier", out var dm)) lc.DamageMultiplier = dm.GetSingle();
                                if (levelElem.TryGetProperty("rangeAdd", out var ra)) lc.RangeAdd = ra.GetSingle();
                                if (levelElem.TryGetProperty("attackSpeedMultiplier", out var asm)) lc.AttackSpeedMultiplier = asm.GetSingle();
                                if (levelElem.TryGetProperty("costMultiplier", out var cm)) lc.CostMultiplier = cm.GetSingle();
                                if (levelElem.TryGetProperty("specialAbility", out var sa))
                                {
                                    lc.SpecialAbility = ParseUpgradeAbility(sa.GetString());
                                    if (levelElem.TryGetProperty("specialAbilityParam", out var sap))
                                        lc.SpecialAbilityParam = sap.GetSingle();
                                }
                                cfg.Levels[level] = lc;
                            }
                        }

                        // Merge: only override/add levels in the JSON; keep existing levels not in JSON
                        if (!TowerUpgradePaths.TryGetValue(pathId, out var existing))
                            TowerUpgradePaths[pathId] = cfg;
                        else
                            foreach (var kvp in cfg.Levels)
                                existing.Levels[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GameConfig] Warning: failed to load tower_upgrade_paths.json: {ex.Message}");
            }
        }

        private static TowerUpgradeAbility ParseUpgradeAbility(string s)
        {
            return s?.ToLowerInvariant() switch
            {
                "splashdamage" or "splash" => TowerUpgradeAbility.SplashDamage,
                "chainlightning" or "chain" => TowerUpgradeAbility.ChainLightning,
                "freezepct" or "freeze" => TowerUpgradeAbility.FreezeAoe,
                "armorpierce" or "armor" => TowerUpgradeAbility.ArmorPierce,
                "criticalstrike" or "critical" or "crit" => TowerUpgradeAbility.CriticalStrike,
                _ => TowerUpgradeAbility.None,
            };
        }
    }

    /// <summary>
    /// Bank / Interest system configuration.
    /// </summary>
    public class BankConfig
    {
        // Base interest rate per wave (0.05f = 5% of banked gold)
        public float InterestRateBase { get; set; } = 0.05f;
        // Maximum interest rate cap (even with bonuses, rate cannot exceed this)
        public float InterestRateCap { get; set; } = 0.20f;
        // Maximum gold that can be stored in the bank
        public float BankGoldCap { get; set; } = 100000f;
    }

    /// <summary>
    /// Mana/Energy pool system configuration — defines mana regeneration and costs.
    /// </summary>
    public class ManaConfig
    {
        // Base mana points at game start
        public float BaseMana { get; set; } = 100f;
        // Maximum mana cap
        public float MaxManaBase { get; set; } = 100f;
        // Mana regeneration per second (in active combat)
        public float ManaRegenPerSec { get; set; } = 5f;
        // Mana regen in BuildPhase (typically higher for preparation)
        public float ManaRegenBuildPhase { get; set; } = 10f;
        // Multiplier on all mana costs (buff/debuff from tech tree)
        public float ManaCostMultiplier { get; set; } = 1f;
    }

    /// <summary>
    /// Skill type IDs for player global skills (ultimates).
    /// </summary>
    public enum GlobalSkillType
    {
        MeteorStrike = 0,   // Full-screen AoE damage
        TimeStop = 1,      // Freeze all enemies temporarily
        EmergencyHeal = 2, // Restore HP to all towers
        GoldBurst = 3      // Instant gold + temporary income boost
    }

    /// <summary>
    /// Player global skill / ultimate ability definition.
    /// Stored in gameConfig.GlobalSkills and referenced by GlobalSkillSystem.
    /// </summary>
    public class GlobalSkillDef
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        // Skill type ID (maps to GlobalSkillType enum)
        public int SkillType { get; set; }
        // Mana cost to activate
        public float ManaCost { get; set; } = 0f;
        // Cooldown in seconds (cross-wave)
        public float Cooldown { get; set; } = 0f;
        // For MeteorStrike: damage as % of player HP
        public float DamagePct { get; set; } = 0f;
        // Cap on meteor damage so one skill can't one-shot bosses
        public float MaxDamage { get; set; } = 0f;
        // For TimeStop: duration in seconds
        public float Duration { get; set; } = 0f;
        // For EmergencyHeal: heal as % of max HP
        public float HealPct { get; set; } = 0f;
        // For GoldBurst: flat gold awarded
        public float GoldAmount { get; set; } = 0f;
        // For GoldBurst: income multiplier for Duration seconds
        public float GoldMultiplier { get; set; } = 1f;
        // Hotkey string for UI display (e.g. "Q", "R")
        public string Hotkey { get; set; } = "";
    }

    /// <summary>
    /// Weather system configuration — defines weather types and their effects.
    /// </summary>
    public class WeatherConfig
    {
        // Weather type IDs (used as array indices)
        public const int Clear = 0;
        public const int Rain = 1;
        public const int Fog = 2;
        public const int Storm = 3;

        // Global config
        public float GlobalEnemySpeedMult { get; set; } = 1.0f;
        public float GlobalTowerRangeMult { get; set; } = 1.0f;
        public float GlobalTowerDamageMult { get; set; } = 1.0f;
        // Per-type overrides
        public Dictionary<string, WeatherTypeConfig> Types { get; set; } = new();
    }

    public class WeatherTypeConfig
    {
        public string Name { get; set; } = "";
        // Multiplier applied to enemy move speed while this weather is active
        public float EnemySpeedMult { get; set; } = 1.0f;
        // Multiplier applied to tower attack range
        public float TowerRangeMult { get; set; } = 1.0f;
        // Multiplier applied to all tower damage
        public float TowerDamageMult { get; set; } = 1.0f;
        // Default duration in turns (-1 = permanent until changed)
        public float DefaultDuration { get; set; } = -1f;
        // Intensity range for random selection
        public float MinIntensity { get; set; } = 0.5f;
        public float MaxIntensity { get; set; } = 1.0f;
    }

    /// <summary>
    /// Day/Night cycle configuration — global environmental phase that alternates
    /// between Day (buffs towers) and Night (buffs enemies).
    /// Loaded from day_night.json and applied via DayNightSystem.
    /// </summary>
    public class DayNightConfig
    {
        // Phase IDs
        public const int Day = 0;
        public const int Night = 1;

        // Duration of each phase in seconds (-1 = no day/night cycles)
        public float DayDuration { get; set; } = 60f;
        public float NightDuration { get; set; } = 45f;

        // Day bonuses to towers
        public float DayTowerRangeBonus { get; set; } = 0.20f;  // +20% tower range during day
        public float DayEnemySpeedBonus { get; set; } = 0.10f;  // +10% enemy speed during day

        // Night bonuses to enemies (and penalties to towers)
        public float NightTowerRangePenalty { get; set; } = -0.30f;  // -30% tower range during night
        public float NightEnemySpeedBonus { get; set; } = 0.0f;      // no speed bonus by default
        public float NightEnemyDamageBonus { get; set; } = 0.15f;    // +15% enemy damage during night

        // Sentinel tower: special night-active tower that gains bonus range at night
        // (stored as a bonus multiplier, not a separate tower type)
        public float SentinelNightRangeBonus { get; set; } = 0.50f;  // +50% range at night

        // Whether cycles repeat (true = infinite day/night, false = single day then stuck)
        public bool RepeatCycles { get; set; } = true;
    }

    public class TerrainTypeConfig
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public float MoveSpeedMult { get; set; } = 1.0f;
        public float DotDamagePerTick { get; set; }
        public int DotDuration { get; set; }
        public float TowerRangeBonus { get; set; }
    }

    /// <summary>
    /// Tower overcharge (overdrive/boost) configuration.
    /// Allows players to temporarily boost tower damage/attack speed at the cost of mana.
    /// </summary>
    public class TowerOverchargeConfig
    {
        /// <summary>Damage multiplier while overcharge is active (e.g. 2.0 = double damage)</summary>
        public float DamageMultiplier { get; set; } = 2.0f;
        /// <summary>Attack speed multiplier while overcharge is active (e.g. 1.5 = 50% faster)</summary>
        public float AttackSpeedMultiplier { get; set; } = 1.5f;
        /// <summary>Range bonus multiplier while overcharge is active (e.g. 1.2 = +20% range)</summary>
        public float RangeMultiplier { get; set; } = 1.2f;
        /// <summary>Duration of the overcharge boost in seconds</summary>
        public float Duration { get; set; } = 5.0f;
        /// <summary>Cooldown before the same tower can be overcharged again (seconds)</summary>
        public float Cooldown { get; set; } = 30.0f;
        /// <summary>Mana cost to activate overcharge per tower</summary>
        public float ManaCost { get; set; } = 20f;
        /// <summary>Player must have at least this much mana to activate overcharge</summary>
        public float MinManaRequired { get; set; } = 10f;
    }

    /// <summary>
    /// Pickup item definition — loaded from pickup_defs.json.
    /// </summary>
    public class PickupDef
    {
        public string Type { get; set; } = "";
        public float Value { get; set; }
        public float CollectRadius { get; set; } = 1.5f;
        public float LifetimeSeconds { get; set; } = 30f;
        public string Color { get; set; } = "White";
        public string Fx { get; set; } = "None";
        // Rarity tier for this pickup: 0=Common, 1=Uncommon, 2=Rare, 3=Epic, 4=Legendary.
        // Default 0 (Common) for backward compat. PickupSystem uses this to filter weighted random rolls.
        public int Rarity { get; set; } = 0;
    }

    /// <summary>
    /// Inventory item type (Round 130) — semantic category used by InventorySystem.UseItem to dispatch effect.
    /// String from JSON, parsed to enum at load time. Keep alphabetical for stable order.
    /// </summary>
    public enum InventoryItemType
    {
        Unknown = 0,
        Heal = 1,         // +Value HP to player
        Mana = 2,         // +Value mana to player
        Shield = 3,       // +Value shield to player
        SpeedBoost = 4,   // +50% speed for fixed duration
        DamageBoost = 5,  // +X% attack damage for fixed duration
        AoEBurst = 6,     // damage nearby enemies (Value = damage, 0-arg radius from Radius field)
        Summon = 7,       // spawn Value allied units
        Cleanse = 8,      // remove all CC/DoT flags
    }

    /// <summary>
    /// Inventory item definition (Round 130) — loaded from items.json.
    /// Each item is a consumable stored in a per-player inventory slot.
    /// InventorySystem dispatches on ItemType; Value/BuffDuration/Radius are typed meaning.
    /// </summary>
    public class ItemDef
    {
        public string Type { get; set; } = "";          // unique id (e.g. "healing_potion")
        public string Name { get; set; } = "";          // display name (e.g. "Healing Potion")
        public InventoryItemType ItemType { get; set; } = InventoryItemType.Unknown;
        public float Value { get; set; } = 0f;          // magnitude (heal amount, damage, etc.)
        public float BuffDuration { get; set; } = 0f;   // seconds (for buff-type items)
        public float Radius { get; set; } = 0f;         // world units (for AoE items)
        public int MaxStack { get; set; } = 1;          // per-slot max count (default 1 = single-use)
    }

    /// <summary>
    /// Pickup rarity config — controls the weighted random roll for tier distribution
    /// when spawning pickups on enemy death. Luck from towers (TowerLuck > 0) shifts
    /// probability mass from Common→Rare+ tier weights.
    /// </summary>
    public class PickupRarityConfig
    {
        // Base weights (must sum to 1.0) for tiers 0..4 (Common..Legendary).
        public float[] TierWeights { get; set; } = new float[] { 0.50f, 0.30f, 0.15f, 0.04f, 0.01f };
        // Per-point luck shift: how much of Common's weight migrates to Rare per luck unit.
        // Capped by MaxLuckBonus so high-luck towers don't unbalance the system.
        public float LuckShiftPerPoint { get; set; } = 0.02f;
        public float MaxLuckBonus { get; set; } = 0.20f;
    }

    /// <summary>
    /// Resource node definition — a fixed map object that produces gold, mana, or research.
    /// Nodes can be captured by towers or neutral, and can be destroyed by enemies.
    /// </summary>
    public class ResourceNodeDef
    {
        /// <summary>Unique identifier for this node, e.g. "gold_mine_1"</summary>
        public string Id { get; set; } = "";
        /// <summary>Display name, e.g. "Gold Mine", "Mana Spring"</summary>
        public string Name { get; set; } = "";
        /// <summary>Node type: 0=GoldMine, 1=ManaSpring, 2=TechRelic</summary>
        public int Type { get; set; } = 0;
        /// <summary>Map X coordinate</summary>
        public float X { get; set; }
        /// <summary>Map Y coordinate</summary>
        public float Y { get; set; }
        /// <summary>Production rate: gold/sec for GoldMine, mana/sec for ManaSpring, research/sec for TechRelic</summary>
        public float ProductionRate { get; set; } = 1f;
        /// <summary>Max health (if destructible). 0 = indestructible.</summary>
        public float MaxHealth { get; set; } = 0f;
        /// <summary>Initial ownership: -1 = neutral, 0 = player 0</summary>
        public int InitialOwner { get; set; } = -1;
        /// <summary>Radius for enemy capture (enemies within this range start capturing)</summary>
        public float CaptureRadius { get; set; } = 2f;
        /// <summary>
        /// Seconds until the node respawns at full HP after being destroyed.
        /// 0 or negative = never respawn (one-shot, default legacy behavior).
        /// Recommended 30-60 seconds for a re-spawnable economy.
        /// </summary>
        public float RegenDelay { get; set; } = 0f;
    }

    /// <summary>
    /// Resource node type enumeration.
    /// </summary>
    public enum ResourceNodeTypeEnum
    {
        GoldMine = 0,
        ManaSpring = 1,
        TechRelic = 2
    }

    /// <summary>
    /// Tower link combo definition — loaded from Data/Towers/tower_links.json.
    /// Defines behavior when two specific tower types are placed adjacent to each other.
    /// </summary>
    public class TowerLinkDef
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>
        /// Ordered pair of tower types that trigger this link combo.
        /// For bidirectional checks, both orderings are tried.
        /// </summary>
        public string[] RequiredTowerTypes { get; set; } = Array.Empty<string>();
        public string Description { get; set; } = "";
        public TowerLinkEffect ComboEffect { get; set; } = new();
        /// <summary>Cooldown in seconds between combo activations (0 = no cooldown).</summary>
        public float Cooldown { get; set; } = 0f;
        /// <summary>Minimum grid distance between linked towers (inclusive).</summary>
        public int MinDistance { get; set; } = 1;
        /// <summary>Maximum grid distance between linked towers (inclusive).</summary>
        public int MaxDistance { get; set; } = 2;
    }

    public class TowerLinkEffect
    {
        /// <summary>Flat damage per second for lightning-style combo attacks.</summary>
        public float DamagePerSecond { get; set; } = 0f;
        /// <summary>Slow multiplier applied to enemies in the combo area.</summary>
        public float SlowAmount { get; set; } = 0f;
        /// <summary>Slow duration in turns.</summary>
        public float SlowDuration { get; set; } = 0f;
        /// <summary>Chain lightning range in grid cells.</summary>
        public int ChainRange { get; set; } = 0;
        /// <summary>Additional chain lightning hops.</summary>
        public int ChainCount { get; set; } = 0;
        /// <summary>Lifesteal fraction (0.3 = 30% of damage as heal).</summary>
        public float LifestealPercent { get; set; } = 0f;
        /// <summary>DoT damage bonus multiplier.</summary>
        public float DotDamageBonus { get; set; } = 0f;
        /// <summary>Damage multiplier for the link tower's primary attack.</summary>
        public float DamageMultiplier { get; set; } = 1f;
        /// <summary>Radius for AOE effects.</summary>
        public int AoeRadius { get; set; } = 0;
        /// <summary>Damage multiplier vs enemies above health threshold.</summary>
        public float DamageVsHighHealthMult { get; set; } = 1f;
        /// <summary>Enemy health fraction threshold (e.g. 0.5 = 50% max HP).</summary>
        public float HealthThreshold { get; set; } = 0.5f;
    }

    /// <summary>
    /// Corpse ground effect definition — loaded from Data/Configs/corpse_effects.json.
    /// Defines what ground effect a monster type leaves behind when it dies.
    /// </summary>
    public class CorpseEffectDef
    {
        /// <summary>Unique identifier for this corpse effect, e.g. "poison_pool", "slime_slow"</summary>
        public string Id { get; set; } = "";
        /// <summary>Display name, e.g. "Poison Pool", "Slime Patch"</summary>
        public string Name { get; set; } = "";
        /// <summary>Effect type: 0=Poison (DoT), 1=Slow, 2=Ice (freeze), 3=Fire (DoT), 4=Healing, 5=DamageBoost</summary>
        public int EffectType { get; set; } = 0;
        /// <summary>Duration in seconds the corpse effect persists.</summary>
        public float Duration { get; set; } = 5f;
        /// <summary>Radius in grid units.</summary>
        public float Radius { get; set; } = 1.5f;
        /// <summary>Damage per tick (for DoT types).</summary>
        public float DamagePerTick { get; set; } = 0f;
        /// <summary>Tick interval in seconds.</summary>
        public float TickInterval { get; set; } = 1f;
        /// <summary>Slow multiplier (0.5 = 50% speed, for Slow type).</summary>
        public float SlowAmount { get; set; } = 1f;
        /// <summary>
        /// Round 171 Direction 4 — Blighted Ground armor reduction (additive, e.g. 0.3 = -30% armor).
        /// Only consumed by effectType=8 (BlightedGround). 0 = no debuff (default for all other types).
        /// </summary>
        public float ArmorReduction { get; set; } = 0f;
        /// <summary>
        /// Round 171 Direction 4 — Blighted Ground attack/move speed reduction (additive, e.g. 0.2 = -20% speed).
        /// Only consumed by effectType=8 (BlightedGround). 0 = no debuff (default for all other types).
        /// </summary>
        public float SpeedReduction { get; set; } = 0f;
        /// <summary>
        /// Round 175 Direction 9 — Smokescreen tower miss chance (e.g. 0.30 = 30% miss).
        /// Only consumed by effectType=9 (Smokescreen). 0 = no miss (default for all other types).
        /// Applies to towers within zone radius; multiple overlapping smokescreens use max() so
        /// they don't stack multiplicatively into 100% miss.
        /// </summary>
        public float MissChance { get; set; } = 0f;
        /// <summary>
        /// Round 175 Direction 9 — Smokescreen enemy move-speed multiplier (e.g. 1.20 = +20% speed).
        /// Only consumed by effectType=9 (Smokescreen). 1.0 = no boost (default for all other types).
        /// Multiplies into EnemyTerrainMoveSpeedMult[] for enemies in range.
        /// </summary>
        public float EnemySpeedBoost { get; set; } = 1f;
        /// <summary>Enemy types that leave this corpse effect (comma-separated in JSON).</summary>
        public List<string> MonsterTypes { get; set; } = new List<string>();
    }

    /// <summary>
    /// Random mid-wave event definition — defines the structure of one random event type.
    /// Loaded from Data/Configs/random_events.json.
    /// </summary>
    public class RandomEventDef
    {
        /// <summary>Unique identifier: ambush, supply_drop, earthquake, boss_rush, merchant</summary>
        public string Id { get; set; } = "";
        /// <summary>Display name shown to player when event triggers.</summary>
        public string Name { get; set; } = "";
        /// <summary>
        /// Event type enum value: 1=Ambush, 2=SupplyDrop, 3=Earthquake, 4=BossRush, 5=Merchant
        /// </summary>
        public int EventType { get; set; } = 0;
        /// <summary>Weight for random selection (higher = more likely). 0 = never auto-trigger.</summary>
        public float Weight { get; set; } = 0f;
        /// <summary>Earliest wave number this event can appear (0 = always).</summary>
        public int MinWave { get; set; } = 0;
        /// <summary>Latest wave number this event can appear (-1 = no limit).</summary>
        public int MaxWave { get; set; } = -1;
        /// <summary>Minimum time (in seconds) between consecutive events of this type.</summary>
        public float Cooldown { get; set; } = 60f;
        /// <summary>Event duration in turns (-1 = instant/one-shot, 0 = permanent until ended).</summary>
        public float Duration { get; set; } = 0f;
        /// <summary>Difficulty multiplier applied to enemy stats during this event (e.g. 1.2 = +20%).</summary>
        public float DifficultyMult { get; set; } = 1f;
        /// <summary>Bonus gold awarded when the event ends successfully (surviving).</summary>
        public float BonusGold { get; set; } = 0f;
        /// <summary>Bonus research points awarded when the event ends successfully.</summary>
        public int BonusResearch { get; set; } = 0;
        /// <summary>For Ambush: extra enemy count. For SupplyDrop: gold amount. For Earthquake: damage to all.</summary>
        public float Param { get; set; } = 0f;
        /// <summary>Secondary parameter (e.g. for AoE radius, speed penalty, etc.).</summary>
        public float Param2 { get; set; } = 0f;
    }

    /// <summary>
    /// Random event configuration — holds all RandomEventDefs and global settings.
    /// </summary>
    public class RandomEventConfig
    {
        /// <summary>Event type IDs matching RandomEventDef.EventType.</summary>
        public const int None = 0;
        public const int Ambush = 1;
        public const int SupplyDrop = 2;
        public const int Earthquake = 3;
        public const int BossRush = 4;
        public const int Merchant = 5;

        /// <summary>Global probability that an event triggers at any wave start (0-1). Default: 0.3 (30%).</summary>
        public float GlobalEventChance { get; set; } = 0.3f;
        /// <summary>Minimum turn gap between consecutive random events.</summary>
        public float MinEventGap { get; set; } = 30f;
        /// <summary>All defined event types.</summary>
        public List<RandomEventDef> Events { get; set; } = new List<RandomEventDef>();
    }

    /// <summary>
    /// One modifier in the daily challenge pool (Round 105 Direction 9).
    /// Each modifier multiplies or adds to a small number of game-wide stats and
    /// is selected by DailyChallengeSystem at run start using a date-seeded RNG.
    /// Defaults are neutral (DamageMult/GoldMult/EnemyHpMult = 1.0, StartingGoldBonus = 0)
    /// so an unmodified pool has no effect.
    /// </summary>
    public class DailyModifierDef
    {
        /// <summary>Unique modifier id (e.g. "glass_cannon", "rich_start", "tank_horde").</summary>
        public string Id { get; set; } = "";
        /// <summary>Display name shown in the daily summary UI.</summary>
        public string Name { get; set; } = "";
        /// <summary>One-line flavor text describing the modifier.</summary>
        public string Description { get; set; } = "";
        /// <summary>Damage multiplier (1.0 = neutral, 1.3 = +30% damage, 0.7 = -30% damage).</summary>
        public float DamageMult { get; set; } = 1.0f;
        /// <summary>Gold earn multiplier (1.0 = neutral).</summary>
        public float GoldMult { get; set; } = 1.0f;
        /// <summary>Enemy max-HP multiplier (1.0 = neutral, 1.5 = +50% HP).</summary>
        public float EnemyHpMult { get; set; } = 1.0f;
        /// <summary>Flat starting-gold bonus (additive, 0 = neutral).</summary>
        public float StartingGoldBonus { get; set; } = 0f;
    }

    public enum CorpseEffectTypeEnum
    {
        Poison = 0,
        Slow = 1,
        Ice = 2,
        Fire = 3,
        Healing = 4,
        DamageBoost = 5,
        // Round 168 Direction 3 — Hallowed Ground: positive-feedback ground zone.
        // Damages enemies in range every tick. (Friendly-tower buff is a planned v2 extension.)
        HallowedGround = 6,
        // Round 169 Direction 10 — Thorny Bramble: DoT + slow combo zone.
        ThornyBramble = 7,
        // Round 171 Direction 4 — Blighted Ground: continuous DoT + armor/speed debuff zone.
        // Reuses EnemyCurseArmorReduction / EnemyCurseSpeedReduction fields set by
        // CurseAuraSystem (Round 77). Note: these values are accumulated in the SOA arrays
        // and are *not* reset when the enemy leaves the zone — this matches existing
        // CurseAuraSystem semantics (zone debuffs persist for a short window until the
        // next full reset). For v2, decay-timer fields can be added to time-bound the buff.
        BlightedGround = 8
    }

    // ── Meta Progression / Prestige Node Definition ─────────────────────
    /// <summary>
    /// One upgradeable node in the meta-progression (prestige) tree.
    /// Persistent across runs: once unlocked with "Stardust" currency, the bonuses
    /// stack on every subsequent run.
    ///
    /// Effect fields default to 1.0f (multiplicative) or 0f (additive) so that
    /// "unspent" nodes contribute nothing. Systems read resolved multipliers from
    /// GameConfig.Meta*Mult (precomputed at boot from all unlocked nodes).
    /// </summary>
    public class MetaProgressionNode
    {
        /// <summary>Unique node id (e.g. "damage_1", "starting_gold_1").</summary>
        public string Id { get; set; } = "";
        /// <summary>Display name shown in the prestige UI.</summary>
        public string Name { get; set; } = "";
        /// <summary>Human-readable description.</summary>
        public string Description { get; set; } = "";
        /// <summary>Stardust cost to unlock this node.</summary>
        public int Cost { get; set; } = 10;
        /// <summary>Maximum number of times this node can be unlocked (0 = unlimited).</summary>
        public int MaxRank { get; set; } = 1;
        /// <summary>Optional prerequisite node id (must be unlocked first).</summary>
        public string PrerequisiteId { get; set; } = "";

        // Effect fields (defaults are no-op: mult=1.0, bonus=0)
        public float DamageMult { get; set; } = 1.0f;           // multiplicative
        public float GoldEarnedMult { get; set; } = 1.0f;       // multiplicative
        public float AttackSpeedMult { get; set; } = 1.0f;     // multiplicative
        public float StartingGoldBonus { get; set; } = 0f;      // additive
        public int StartingLivesBonus { get; set; } = 0;        // additive
        public float CritRateBonus { get; set; } = 0f;          // additive (0-1)
        public int FreeTechLevels { get; set; } = 0;            // additive
    }

    /// <summary>
    /// Round 91 Synergy Tiering (同类塔聚集 tier) — 同一类塔聚集 N 个时触发 tier 1/2/3 协同
    /// 配置：聚集阈值 + 每 tier 的 damage mult 叠加（与既有 TowerSynergyMultiplier 串行叠加）
    /// 零开销：当前活跃塔数 < SynergyTier1Count 时直接跳过整个 tier 路径
    /// </summary>
    public static class SynergyTierConfig
    {
        /// <summary>聚集 3 个同类塔时触发 tier 1（damage mult 叠加 +10%）</summary>
        public const int SynergyTier1Count = 3;
        public const float SynergyTier1Bonus = 0.10f;
        /// <summary>聚集 5 个同类塔时触发 tier 2（damage mult 叠加 +20%）</summary>
        public const int SynergyTier2Count = 5;
        public const float SynergyTier2Bonus = 0.20f;
        /// <summary>聚集 8 个同类塔时触发 tier 3（damage mult 叠加 +35%）</summary>
        public const int SynergyTier3Count = 8;
        public const float SynergyTier3Bonus = 0.35f;
    }

    /// <summary>
    /// Damage Saturation (Round 92 Direction 1): per-enemy diminishing returns on incoming damage
    /// when cumulative damage within a short window exceeds a multiple of the enemy's max HP.
    /// Forces players to mix high-DPS and slow-DPS towers against Boss/Elite enemies, preventing
    /// single-tower-type builds from trivializing high-HP threats via over-DPS "wasted" damage.
    /// All three tunables are loaded from <c>Data/Configs/damage_saturation.json</c>; the constants
    /// below are safe defaults used when the JSON is absent or fails to load.
    /// </summary>
    public static class DamageSaturationConfig
    {
        /// <summary>Number of frames over which incoming damage is accumulated for saturation checks.
        /// At 60 FPS, 30 frames ≈ 0.5s — a typical "burst" window for high-DPS towers.</summary>
        public static int SaturationWindowFrames = 30;
        /// <summary>Damage-taken ratio threshold (multiple of EnemyMaxHealth) above which saturation kicks in.
        /// E.g. 2.0f means: once an enemy has taken more than 2× its max HP in damage within the window,
        /// further damage starts to be reduced. Built-in cushion: a single tower dealing 1.5× max HP in
        /// one window is fine; only "sustained overkill" triggers reduction.</summary>
        public static float SaturationThresholdMult = 2.0f;
        /// <summary>Final-damage multiplier applied when the saturation threshold is exceeded.
        /// Hard cap at 0.1f inside the apply code (configurable floor to allow future tuning to 0.3 etc.).
        /// 0.1f means: once saturated, further damage is reduced to 10% of its pre-saturation value,
        /// strongly discouraging "wasted DPS" against the same target.</summary>
        public static float SaturationScaleMult = 0.1f;
    }

    /// <summary>
    /// CC Immunity (Round 97 Direction 3): per-enemy bitmask of which CC types are fully ignored.
    /// Stacks with <c>EnemyIsUnstoppable</c> (total CC immunity): if either the bit for the CC
    /// type is set OR the unstoppable flag is on, the CC is skipped.
    /// Boss/Elite monsters can be configured with 0xFF (immune to all CC); weaker monsters may
    /// only be immune to part (e.g. Slow) to balance CC-heavy tower compositions.
    /// Default value 0 = no immunity, fully backward compatible.
    /// </summary>
    public static class CCImmunityConfig
    {
        // Bit positions match the CC type. Bits 0-7 are reserved; CC types use bits 0-5.
        public const int Mask_Slow       = 1 << 0;
        public const int Mask_Stun       = 1 << 1;
        public const int Mask_Freeze     = 1 << 2;
        public const int Mask_Knockback  = 1 << 3;
        public const int Mask_Polymorph  = 1 << 4;
        public const int Mask_Stagger    = 1 << 5;
        public const int Mask_Disarm     = 1 << 6;   // Disarm: enemy cannot use abilities (call of Round 124)
        /// <summary>All CC types (full immunity — equivalent to EnemyIsUnstoppable=true).</summary>
        public const int Mask_AllCC      = Mask_Slow | Mask_Stun | Mask_Freeze | Mask_Knockback | Mask_Polymorph | Mask_Stagger | Mask_Disarm;
        /// <summary>Boss/Elite default: immune to all CC (forces pure-damage tower compositions).</summary>
        public const int Mask_BossDefault = Mask_AllCC;
    }

    /// <summary>
    /// Tower windup / pre-cast configuration. (Round 98)
    /// When a tower has TowerWindupFrames > 0, it counts down frames between cooldown end and actual fire,
    /// creating a "charging" window during which silence / stun / disable cancels the shot.
    /// </summary>
    public static class WindupConfig
    {
        /// <summary>Default windup frames for new towers. 0 = no windup, instant fire.</summary>
        public const int DefaultWindupFrames = 0;
        /// <summary>Upper bound on configured windup. Beyond this, gameplay feels sluggish.</summary>
        public const int MaxWindupFrames = 30;
        /// <summary>Minimum windup to enable the interrupt window. 0 means disabled (instant fire).</summary>
        public const int MinWindupFrames = 1;
        /// <summary>If true, tower CC (silence/stun/sabotage) cancels in-flight windup + resets attack cooldown. If false, windup is uninterruptible.</summary>
        public const bool WindupInterruptOnCC = true;
    }

    /// <summary>
    /// Execute Threshold / Finisher Bonus (Round 105 Direction 8).
    /// Enemies with <c>EnemyExecuteThreshold &gt; 0</c> opt in to a finisher economy: killing
    /// them grants a flat gold + mana bonus to the player. Pairs with the existing Death Mark
    /// system to reward high-damage "assassination" plays on low-HP enemies. Tunables below
    /// are used as safe defaults when the JSON config is absent or fails to load.
    /// </summary>
    public static class ExecuteConfig
    {
        /// <summary>Default opt-out threshold. 0 = execute effect disabled for new enemies.</summary>
        public const float DefaultExecuteThreshold = 0f;
        /// <summary>Default gold bonus when an enemy is executed. 0 = no gold bonus.</summary>
        public const float DefaultExecuteBonusGold = 0f;
        /// <summary>Default mana bonus when an enemy is executed. 0 = no mana bonus.</summary>
        public const float DefaultExecuteBonusMana = 0f;
        /// <summary>Recommended HP fraction (0-1) for "executable" enemies. 0.20 = below 20% HP.</summary>
        public const float RecommendedExecuteThreshold = 0.20f;
        /// <summary>Recommended gold bonus for a finisher kill. Tunable via JSON.</summary>
        public const float RecommendedExecuteBonusGold = 25f;
        /// <summary>Recommended mana bonus for a finisher kill. Tunable via JSON.</summary>
        public const float RecommendedExecuteBonusMana = 15f;
    }

    /// <summary>
    /// Threat Score / Dynamic Difficulty Scaling (Round 99 Direction 5).
    /// Tracks player DPS over a rolling window and scales incoming enemy HP upward when the
    /// player is over-performing. This keeps the challenge curve roughly constant across
    /// BuildPhase optimizations — a "tower-comp" player faces the same effective difficulty
    /// as a "minimal-tower" player. Inspired by Vampire Survivors / Diablo's dynamic difficulty.
    ///
    /// Hot-path design: <c>PlayerRecentDPS</c> is updated once per frame in FrameScheduler
    /// (exponential decay), and read once per enemy spawn in WaveSpawningSystem. Damage
    /// accumulation in PlayerTowerAttackSystem is a single += per hit — no per-enemy work.
    /// </summary>
    public static class ThreatScoreConfig
    {
        /// <summary>Half-life in seconds for the EMA-style decay of PlayerRecentDPS.
        /// At 60 FPS, 5s ≈ 300 frames — high-DPS spikes fade over ~5s of inactivity.</summary>
        public const float DPSWindowSec = 5.0f;
        /// <summary>Per-DPS-point HP scaling rate applied at spawn time.
        /// 0.0001f means 10000 sustained DPS adds 1.0× HP to new enemies (i.e. doubles them).</summary>
        public const float ThreatScalingRate = 0.0001f;
        /// <summary>Upper cap on the threat multiplier. Prevents runaway scaling if a player
        /// is dealing extreme burst damage (e.g. one-shot Boss with super-crit).</summary>
        public const float MaxThreatMultiplier = 3.0f;
        /// <summary>Lower cap on the threat multiplier. Always &gt;= 1.0f so enemies never spawn
        /// weaker than their base stats — the system only makes things harder, never easier.</summary>
        public const float MinThreatMultiplier = 1.0f;
    }

    /// <summary>
    /// Round 120 Direction 3 — Adaptive Spawn Count (Rubber-band Spawn Pacing).
    /// Adjusts the number of enemies spawned per wave based on the player's previous-wave
    /// performance: a player who kills more than expected sees more enemies next wave
    /// (challenge ramps up); a player who leaks more than expected sees fewer (catch-up).
    /// The scaling is applied in <c>WaveSpawningSystem</c>'s three spawn sites
    /// (batch Update / InjectExtraEnemies / SpawnMinionNearPosition) as a multiplier
    /// on the per-type enemy count.
    ///
    /// Pure performance signal: <see cref="Systems.AdaptiveDifficultySystem.OnWaveComplete"/>
    /// reads the kill count for the just-finished wave, compares against
    /// <c>WaveConfig.ExpectedKillCount</c> (0 disables the system for that wave —
    /// backward-compatible), and writes the resulting multiplier to
    /// <c>WaveSpawningSystem.PerformanceSpawnMultiplier</c> at the start of the
    /// next wave. Multiplier is clamped to <c>[MinSpawnMultiplier, MaxSpawnMultiplier]</c>.
    ///
    /// Hot-path design: a single float field on WaveSpawningSystem + one branch in
    /// each spawn site. The branch is a single `if (mult != 1f)` check so the zero-scaling
    /// common case stays zero-overhead.
    /// </summary>
    public static class AdaptiveSpawnConfig
    {
        /// <summary>Default sensitivity for the rubber-band formula. The raw delta
        /// (actualKills - expectedKills) / expectedKills is multiplied by this value
        /// before being added to 1.0. 0.5 means: a 100% over-kill (twice expected)
        /// → +50% spawn count next wave. Set to 0 to disable the system entirely.</summary>
        public const float DefaultSpawnSensitivity = 0.5f;

        /// <summary>Lower bound on <c>PerformanceSpawnMultiplier</c>. Even if the player
        /// leaks every enemy (0 kills vs huge expected), spawn count is never less than
        /// half the baseline (preserves wave identity for level scripting).</summary>
        public const float MinSpawnMultiplier = 0.5f;

        /// <summary>Upper bound on <c>PerformanceSpawnMultiplier</c>. Prevents one super-good
        /// wave from doubling every wave afterward (runaway scaling). 2.0 = at most 2x spawns.</summary>
        public const float MaxSpawnMultiplier = 2.0f;

        /// <summary>When <c>true</c>, also apply the multiplier to mid-wave events
        /// (InjectExtraEnemies ambush + SpawnMinionNearPosition boss-phase summon).
        /// Designers can disable for a more predictable event cadence.</summary>
        public const bool ApplyToMidWaveSpawns = true;
    }

    /// <summary>
    /// Palisade Tower configuration (Round 100 Direction 6).
    /// A palisade is a control-type tower: it has zero attack damage but applies a brief
    /// movement delay to any enemy that walks into its block radius. The delay is implemented
    /// by writing to the existing <c>EnemyStunDurationLeft</c> field, which means the stun
    /// timer decrements automatically inside <c>EnemyMovementSystem</c> (no new path).
    ///
    /// HP-based destructible: <c>PalisadeHP</c> starts at <c>DefaultPalisadeHP</c>; if an enemy
    /// deals damage to the palisade (handled in EnemyMovementSystem at end of step), HP
    /// decreases; HP &lt;= 0 → <c>DestroyEntity</c>. Set <c>DefaultPalisadeHP=0</c> to keep
    /// palisades indestructible (pathing blockers only).
    /// </summary>
    public static class PalisadeConfig
    {
        /// <summary>Default stun frames applied to enemies that step into a palisade's radius.
        /// 18 frames @ 60 FPS ≈ 0.3s. Reuses <c>EnemyStunDurationLeft</c> countdown path.</summary>
        public const int DefaultPalisadeStunFrames = 18;
        /// <summary>Default block radius in grid cells (Manhattan-style). 1 = 3x3 area centered on palisade.</summary>
        public const int DefaultPalisadeBlockRadius = 1;
        /// <summary>Default HP pool of a palisade. 0 = indestructible. 100 means enemies can
        /// grind it down over multiple waves.</summary>
        public const float DefaultPalisadeHP = 100f;
        /// <summary>Damage enemies deal to a palisade when standing on it (per frame at melee range).
        /// 0 = enemies cannot damage palisade (treated as scenery).</summary>
        public const float EnemyContactDamageToPalisade = 5f;
    }

    /// <summary>
    /// Round 106 Direction 2 — Mine / Trap Tower System.
    /// Configured per-tower via Data/Configs/mine_towers.json; the system falls back to
    /// these defaults when a tower is placed via PlaceTower without an explicit config id.
    /// </summary>
    public static class MineConfig
    {
        /// <summary>Default trigger radius in grid cells. 0 = mine never triggers (zero-overhead).</summary>
        public const float DefaultTriggerRadius = 1.5f;
        /// <summary>Default arm time in seconds (mine cannot trigger until this many seconds elapse).</summary>
        public const float DefaultArmTime = 0.5f;
        /// <summary>Default explosion damage. 0 = no damage.</summary>
        public const float DefaultDamage = 80f;
        /// <summary>Default explosion radius in grid cells. 0 = no AoE (point damage).</summary>
        public const float DefaultExplosionRadius = 2f;
        /// <summary>Default number of independent stacks per mine. 1 = single-use, &gt;1 = multi-charge.</summary>
        public const int DefaultMaxStacks = 1;
        /// <summary>Default cost in gold.</summary>
        public const float DefaultCost = 25f;
    }

    /// <summary>
    /// Round 101 Direction 10 — Mana Drain (tower → enemy).
    /// Towers with <c>ManaDrainPct > 0</c> drain a fraction of target enemy's current mana
    /// on each successful attack hit and add it to the player mana pool.
    /// Note: this is the INVERSE direction of <c>ManaBurnSystem</c> (which is enemy→player).
    /// Reuses the <c>EnemyCurrentMana[]</c> field populated on AddEnemy from
    /// monster config (default 0 — only Mana-Wielder enemies have a mana pool).
    /// </summary>
    public static class ManaDrainConfig
    {
        /// <summary>Default fraction of target's current mana drained per hit when no per-tower
        /// override is supplied. Designers can override per-tower via <c>TowerConfig.ManaDrainPct</c>.
        /// 0.1 = 10% per hit. Set to 0 to disable globally.</summary>
        public const float DefaultManaDrainPct = 0.1f;

        /// <summary>Hard cap on mana drained from a single enemy in a single hit event.
        /// Prevents boss-mana (e.g. 10K mana) from instantly filling the player pool.</summary>
        public const float ManaDrainCap = 50f;

        /// <summary>Per-enemy default mana pool when <c>EnemyMaxMana</c> is not configured.
        /// 0 means "this enemy has no mana to drain" — drain silently no-ops.
        /// Designers can override per-monster-type via <c>EnemyTypeEntry.MaxMana</c>.</summary>
        public const float DefaultEnemyMaxMana = 0f;
    }

    /// <summary>
    /// Round 102 — Direction 7: Damage Conversion (Physical → Elemental split).
    /// Lets tower attacks split a configurable fraction of their damage into a different
    /// DamageType (e.g. 30% of Physical damage becomes Fire). The two portions are applied
    /// independently so the converted portion can bypass immunities to the original type
    /// and trigger elemental reactions on the converted type.
    /// </summary>
    public static class DamageConversionConfig
    {
        /// <summary>Global cap on the conversion ratio (0..1). Tower configs that set
        /// <c>DamageConversionRatio</c> above this value are clamped at <c>ConversionDefaultCap</c>
        /// in the deserializer to keep design in check (a 100% conversion defeats the purpose).
        /// 0.5 = 50% max — the sweet spot for "split damage" play.</summary>
        public const float ConversionDefaultCap = 0.5f;

        /// <summary>Minimum meaningful ratio to enter the split code path. Below this value
        /// the conversion branch is skipped entirely (zero-overhead fast path). 0.01 = 1%.</summary>
        public const float MinMeaningfulRatio = 0.01f;
    }

    /// <summary>
    /// Buff Share (Round 103 Direction 8) — towers with non-zero radius share a snapshot of
    /// their own attack speed with nearby friendly towers. Encourages tight tower clusters
    /// (4-tower surround) over spread-out placement.
    /// </summary>
    public static class BuffShareConfig
    {
        /// <summary>Hard cap on the buff share radius. Designer-set values above this are
        /// clamped at deserializer. 8 = a 17x17 cell envelope around the sharing tower.</summary>
        public const float MaxShareRadius = 8f;

        /// <summary>Per-frame efficiency applied to the shared attack-speed bonus. 0.3 = each
        /// sharing tower contributes +30% multiplicative attack speed to towers in range.
        /// 4 sharing towers surrounding a target → target's attack speed is multiplied by
        /// 1.3^4 ≈ 2.86 (a meaningful cluster bonus without breaking single-tower play).</summary>
        public const float DefaultShareEfficiencyPct = 0.3f;

        /// <summary>Bitmask flag indicating the tower shares its own attack speed with
        /// nearby friendly towers. Designers OR these into TowerBuffShareMask per tower
        /// type to opt into sharing.</summary>
        public const int ShareAttackSpeed = 0x01;
    }

    /// <summary>
    /// Target Mark Subsystem (Round 107 Direction 6) — stack-based debuff counter applied
    /// by tower/player attacks. Each mark hit increments <c>EnemyMarkStacks</c> by +1 (capped
    /// at <c>EnemyMarkMaxThreshold</c>) and resets <c>EnemyMarkDecayTimer</c>. When the timer
    /// expires, one stack is consumed. When stacks reach <c>EnemyMarkMaxThreshold</c> the
    /// system fires <see cref="Systems.MarkSystem.OnMarkThreshold"/>, which subscribers can
    /// use to apply vulnerability/execute payoff effects.
    ///
    /// Hot-path design: enemies with <c>EnemyMarkStacks == 0</c> AND <c>EnemyMarkDecayTimer == 0</c>
    /// skip with a single bool check. Non-mark enemies incur zero per-frame cost.
    /// </summary>
    public static class MarkSubsystemConfig
    {
        /// <summary>Default decay interval (seconds) for one stack's expiration.
        /// Reset on every AddMark() call. Mirrors MarkSystem.MarkConfig.Default.</summary>
        public const float DefaultDecayInterval = 1.0f;

        /// <summary>Hard upper cap on total stacks per enemy. Prevents runaway stack
        /// accumulation in long fights. 100 = enough for ~3 minute fights at 1 stack/sec.</summary>
        public const int DefaultMaxStackCap = 100;

        /// <summary>Recommended threshold for "寒冰标记 Frost Mark" (1 of 3 default mark types).
        /// 5 = after 5 hits within decay window, mark is "active" and OnMarkThreshold fires.</summary>
        public const int RecommendedFrostThreshold = 5;

        /// <summary>Recommended threshold for "灼烧标记 Scorch Mark" (faster decay, higher threshold).
        /// 10 stacks at 0.5s decay = ~5 seconds to fully stack.</summary>
        public const int RecommendedScorchThreshold = 10;

        /// <summary>Recommended threshold for "电能标记 Volt Mark" (slow decay, low threshold).
        /// 3 stacks at 2.0s decay = ~6 seconds to fully stack.</summary>
        public const int RecommendedVoltThreshold = 3;
    }
}