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
        /// <summary>Cone angle in degrees for AreaShape.Cone. Fan spread. Default: 60.</summary>
        public float ConeAngleDegrees;   // degrees, used only when AreaShape == Cone

        public GameplayAbilityDef(string name, string desc, float cooldown, float cost,
            int dmgAttr, float fixedDmg, AbilityActivation act, int areaShape, int areaRadius,
            float dotDuration = 0f, float dotTickInterval = 0f, float dotDamagePerTick = 0f,
            float healPercent = 0f, float shieldAmount = 0f, float shieldDuration = 0f,
            StackingBehavior dotStacking = StackingBehavior.None, int dotMaxStacks = 1,
            float freezeDuration = 0f, float freezeChance = 0f,
            float coneAngleDegrees = 60.0f,
            params int[] requiredBuffs)
        {
            Name = name; Description = desc; Cooldown = cooldown; Cost = cost;
            DamageMultiplierAttr = dmgAttr; FixedBaseDamage = fixedDmg; Activation = act;
            AreaShape = areaShape; AreaRadius = areaRadius;
            DotDuration = dotDuration; DotTickInterval = dotTickInterval; DotDamagePerTick = dotDamagePerTick;
            DotStackingBehavior = dotStacking; DotMaxStacks = dotMaxStacks;
            HealPercent = healPercent; ShieldAmount = shieldAmount; ShieldDuration = shieldDuration;
            FreezeDuration = freezeDuration; FreezeChance = freezeChance;
            ConeAngleDegrees = coneAngleDegrees;
            RequiredBuffs = requiredBuffs;
        }

        /// <summary>True if this ability applies a periodic DoT effect.</summary>
        public bool HasDot => DotDuration > 0f && DotTickInterval > 0f && DotDamagePerTick > 0f;

        /// <summary>True if this ability applies a heal effect.</summary>
        public bool IsHeal => HealPercent > 0f;

        /// <summary>True if this ability applies a shield effect.</summary>
        public bool IsShield => ShieldAmount > 0f;
    }

    /// <summary>
    /// Runtime state for an ability on an entity (cooldown remaining, etc.).
    /// </summary>
    public struct AbilityInstance
    {
        public GameplayAbilityDef Definition;
        public float CurrentCooldown;

        public AbilityInstance(GameplayAbilityDef def)
        {
            Definition = def;
            CurrentCooldown = 0f;
        }

        // Bug#37: use epsilon instead of float equality to avoid floating-point residual
        private const float EPSILON = 0.0001f;
        public bool CanActivate() => CurrentCooldown <= EPSILON;

        public void Activate() { if (CanActivate()) CurrentCooldown = Definition.Cooldown; }
    }
}