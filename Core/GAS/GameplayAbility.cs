using System;
using BattleSystemECS.Components;

namespace BattleSystemECS.Core.GAS
{
    public enum AbilityActivation { Instant, InputPressed, Passive }

    /// <summary>
    /// Area shape types. Maps to string values in skills.json:
    /// single / cross / box / circle
    /// </summary>
    public static class AreaShapeType
    {
        public const int Single = 0;
        public const int Cross = 1;
        public const int Box = 2;
        public const int Circle = 3;
        public const int Chain = 4;
        public const int Heal = 5;
        public const int Shield = 6;
        public const int Line = 7;
        public const int Freeze = 8;  // Cold Nova: circle AoE + freeze on hit
        public const int Cone = 9;    // Cone/Triangle: directional fan-shaped AoE (e.g. Dragon Breath).
                                       // coneAngleDegrees controls fan spread (passed via GameplayAbilityDef)
        public const int GroundTarget = 10; // Ground target: player selects a point on the map, AoE hits enemies within radius.
        public const int Slow = 11;          // Slow: circle AoE that slows enemies in radius (non-freeze, move speed reduction)
        public const int TimeWarp = 12;     // TimeWarp: applies GlobalTimeScale + GlobalTimeScaleDuration to slow/fast game time
        public const int Summon = 13;       // Summon: spawns a player-summoned combat unit at the player's position
        public const int HealingZone = 14;   // HealingZone: places a ground healing zone that heals allies in radius
        public const int Polymorph = 15;     // Polymorph: circle AoE that transforms enemies into a harmless form (sheep/chicken)
        public const int TimeRewind = 16;    // TimeRewind: restore player HP / Mana / Shield from a recent snapshot (Round 109)
        public const int ChainHeal = 17;    // ChainHeal: O(N) nearest-neighbor heal chaining (Round 131) — mirror of ChainLightning but applies heal + small shield to friendlies
        public const int MassResurrect = 18; // MassResurrect: AOE revival of all un-reanimated corpses within radius (Round 133) — player-triggered divine spell, mirrors NecromancerSystem.MassResurrect
        // Round 136 Direction 2 — AOE CC group control skills (群体禁锢/击晕)
        public const int AoeStun      = 19; // AoeStun: circle AoE that stuns all enemies in radius for AoeStunDuration turns (war-stomp, earthquake, etc.)
        public const int AoeRoot      = 20; // AoeRoot: circle AoE that roots all enemies in radius for AoeRootDuration turns (immobilizes movement only; enemy can still cast/attack)
        public const int AoeKnockback = 21; // AoeKnockback: circle AoE that applies AoeKnockbackForce push impulse to all enemies in radius (radial direction from player)

        /// <summary>Parse AreaShape string from skills.json config to int constant.</summary>
        public static int FromString(string s)
        {
            return s?.ToLowerInvariant() switch
            {
                "single" => Single,
                "cross" => Cross,
                "box" => Box,
                "circle" => Circle,
                "chain" => Chain,
                "heal" => Heal,
                "shield" => Shield,
                "line" => Line,
                "freeze" => Freeze,
                "cone" => Cone,
                "groundtarget" => GroundTarget,
                "slow" => Slow,
                "time_warp" => TimeWarp,
                "summon" => Summon,
                "healingzone" => HealingZone,
                "polymorph" => Polymorph,
                "timerwind" => TimeRewind,
                "chainheal" => ChainHeal,
                "massresurrect" => MassResurrect,
                "aoestun" => AoeStun,
                "aoeroot" => AoeRoot,
                "aoeknockback" => AoeKnockback,
                _ => Single
            };
        }
    }

    /// <summary>
    /// Ability definition — data only, no runtime state.
    /// </summary>
    public struct GameplayAbilityDef
    {
        public string Name;
        public string Description;
        public float Cooldown; // seconds
        public float Cost;     // resource cost (e.g., mana or gold)
        public int DamageMultiplierAttr; // which attribute to use for base damage (or -1 if fixed)
        public float FixedBaseDamage;     // if DamageMultiplierAttr == -1, use this
        public AbilityActivation Activation;
        public int[] RequiredBuffs; // entity must have these buffs active to use

        // Area shape: 0=single, 1=cross, 2=box, 3=circle
        public int AreaShape;
        public int AreaRadius; // tiles for area effects

        // DoT fields (0 / 0f = no DoT)
        public float DotDuration;       // seconds; 0 = no DoT
        public float DotTickInterval;    // seconds between ticks
        public float DotDamagePerTick;   // damage per tick

        // Stacking fields for DoT effects
        public StackingBehavior DotStackingBehavior;  // how DoT stacks
        public int DotMaxStacks;         // max stack count for DoT (1 = single application)

        // Heal/Shield fields (0 / 0f = no heal/shield)
        public float HealPercent;        // heal percent of max health (e.g. 0.3 = 30%)
        public float ShieldAmount;        // flat shield value absorbed
        public float ShieldDuration;      // shield duration in seconds

        // Freeze fields (Cold Nova)
        public float FreezeDuration;     // turns to freeze enemy; 0 = no freeze
        public float FreezeChance;       // probability [0,1] of freeze applying per enemy
        // Slow fields (Slow Nova — non-freeze move speed reduction)
        public float SlowAmount;         // speed multiplier (e.g. 0.5 = 50% speed); 0 = no slow
        public float SlowDuration;       // seconds of slow effect; 0 = no slow
        // Polymorph fields (变羊/变小鸡 — circle AoE that turns enemies harmless for `PolymorphDuration` turns)
        public float PolymorphDuration;                    // turns enemy stays polymorphed; 0 = no polymorph
        public float PolymorphDamageTakenMultiplier;       // multiplier on damage taken while polymorphed (1.0 = neutral)
        /// <summary>Cone angle in degrees for AreaShape.Cone. Fan spread. Default: 60.</summary>
        public float ConeAngleDegrees;   // degrees, used only when AreaShape == Cone

        // AOE CC fields (Round 136 Direction 2 — group control: stun / root / knockback)
        // Each is only used when AreaShape matches the corresponding constant (AoeStun/AoeRoot/AoeKnockback).
        // 0 = no effect. Stun/Root duration in turns; Knockback force is added to EnemyKnockbackForceLeft.
        public float AoeStunDuration;       // turns to stun enemies in radius; 0 = no stun
        public float AoeRootDuration;       // turns to root enemies in radius; 0 = no root
        public float AoeKnockbackForce;     // radial push impulse for enemies in radius; 0 = no knockback

        public GameplayAbilityDef(string name, string desc, float cooldown, float cost,
            int dmgAttr, float fixedDmg, AbilityActivation act, int areaShape, int areaRadius,
            float dotDuration = 0f, float dotTickInterval = 0f, float dotDamagePerTick = 0f,
            float healPercent = 0f, float shieldAmount = 0f, float shieldDuration = 0f,
            StackingBehavior dotStacking = StackingBehavior.None, int dotMaxStacks = 1,
            float freezeDuration = 0f, float freezeChance = 0f,
            float coneAngleDegrees = 60.0f,
            float slowAmount = 0f, float slowDuration = 0f,
            params int[] requiredBuffs)
        {
            Name = name; Description = desc; Cooldown = cooldown; Cost = cost;
            DamageMultiplierAttr = dmgAttr; FixedBaseDamage = fixedDmg; Activation = act;
            AreaShape = areaShape; AreaRadius = areaRadius;
            DotDuration = dotDuration; DotTickInterval = dotTickInterval; DotDamagePerTick = dotDamagePerTick;
            DotStackingBehavior = dotStacking; DotMaxStacks = dotMaxStacks;
            HealPercent = healPercent; ShieldAmount = shieldAmount; ShieldDuration = shieldDuration;
            FreezeDuration = freezeDuration; FreezeChance = freezeChance;
            SlowAmount = slowAmount; SlowDuration = slowDuration;
            ConeAngleDegrees = coneAngleDegrees;
            RequiredBuffs = requiredBuffs;
            SummonDefId = null;
            // Polymorph fields default to neutral (0 / 1.0). SkillSystem overrides these
            // immediately after construction from the JSON config (def.PolymorphDuration = sc.PolymorphDuration).
            PolymorphDuration = 0f;
            PolymorphDamageTakenMultiplier = 1f;
            // AOE CC fields default to 0 (no effect) — SkillSystem sets from SkillConfig post-construction.
            AoeStunDuration = 0f;
            AoeRootDuration = 0f;
            AoeKnockbackForce = 0f;
        }

        /// <summary>True if this ability applies a periodic DoT effect.</summary>
        public bool HasDot => DotDuration > 0f && DotTickInterval > 0f && DotDamagePerTick > 0f;

        /// <summary>True if this ability applies a heal effect.</summary>
        public bool IsHeal => HealPercent > 0f;

        /// <summary>True if this ability applies a shield effect.</summary>
        public bool IsShield => ShieldAmount > 0f;

        // Summon definition ID for summon_unit ability type (null/empty = not a summon)
        public string SummonDefId;
    }

    /// <summary>
    /// 跨帧能力运行态：Owner 句柄带 generation，冷却与充能集中在此。
    /// </summary>
    public struct AbilityState
    {
        public AbilityId Id;
        public EntityHandle Owner;
        public float Cooldown;
        public int Charges;
        public int MaxCharges;

        public AbilityState(AbilityId id, EntityHandle owner, float cooldown, int charges, int maxCharges)
        {
            Id = id; Owner = owner; Cooldown = cooldown;
            Charges = charges < 0 ? 0 : charges;
            MaxCharges = maxCharges < 1 ? 1 : maxCharges;
        }

        public bool CanActivate() => Cooldown <= 0.0001f && (MaxCharges <= 1 || Charges > 0);
    }

    /// <summary>
    /// AbilityState[] 的剩余冷却投影，保留既有 <c>column[i]</c> 读写，避免测试/系统继续碰裸 float[]。
    /// </summary>
    public readonly struct AbilityCooldownColumn
    {
        public readonly AbilityState[] States;
        public AbilityCooldownColumn(AbilityState[] states)
        {
            States = states ?? throw new ArgumentNullException(nameof(states));
        }
        public int Length => States.Length;
        public float this[int index]
        {
            get => (uint)index < (uint)States.Length ? States[index].Cooldown : 0f;
            set
            {
                if ((uint)index >= (uint)States.Length) return;
                var state = States[index];
                state.Cooldown = value < 0f ? 0f : value;
                States[index] = state;
            }
        }
    }

    /// <summary>
    /// Runtime state for an ability on an entity (cooldown remaining, etc.).
    /// </summary>
    public struct AbilityInstance
    {
        public GameplayAbilityDef Definition;
        public AbilityState State;
        public float CurrentCooldown { get { return State.Cooldown; } set { State.Cooldown = value; } }

        public AbilityInstance(GameplayAbilityDef def)
        {
            Definition = def;
            State = new AbilityState(default(AbilityId), default(EntityHandle), 0f, 1, 1);
        }

        // Bug#37: use epsilon instead of float equality to avoid floating-point residual
        private const float EPSILON = 0.0001f;
        public bool CanActivate() => State.CanActivate() && CurrentCooldown <= EPSILON;

        public void Activate()
        {
            if (!CanActivate()) return;
            State.Cooldown = Definition.Cooldown;
            if (State.MaxCharges > 1 && State.Charges > 0) State.Charges--;
        }
    }
}