namespace BattleSystemECS.Core.GAS
{
    public enum AbilityActivation { Instant, InputPressed, Passive }

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

        // Area shape: 0=single target, 1=line cross, 2=box
        public int AreaShape; // 0=single, 1=cross, 2=box
        public int AreaRadius; // tiles for area effects

        public GameplayAbilityDef(string name, string desc, float cooldown, float cost,
            int dmgAttr, float fixedDmg, AbilityActivation act, int areaShape, int areaRadius,
            params int[] requiredBuffs)
        {
            Name = name; Description = desc; Cooldown = cooldown; Cost = cost;
            DamageMultiplierAttr = dmgAttr; FixedBaseDamage = fixedDmg; Activation = act;
            AreaShape = areaShape; AreaRadius = areaRadius; RequiredBuffs = requiredBuffs;
        }
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