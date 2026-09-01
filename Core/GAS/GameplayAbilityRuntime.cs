namespace BattleSystemECS.Core.GAS
{
    /// <summary>
    /// Single writer for ability-slot activation state. Legacy systems may inspect
    /// definitions, but cooldown ownership stays in the ECS ability store through
    /// this boundary.
    /// </summary>
    public static class GameplayAbilityRuntime
    {
        public static bool TryActivate(float[] cooldowns, int index)
        {
            return cooldowns != null && index >= 0 && index < cooldowns.Length && cooldowns[index] <= 0f;
        }

        public static bool AbilityCommit(float[] cooldowns, int index, float cooldown)
        {
            if (!TryActivate(cooldowns, index)) return false;
            cooldowns[index] = System.Math.Max(0f, cooldown);
            return true;
        }

        public static bool TickCooldown(float[] cooldowns, int index, float deltaSeconds)
        {
            if (cooldowns == null || deltaSeconds <= 0f || index < 0 || index >= cooldowns.Length) return false;
            cooldowns[index] = System.Math.Max(0f, cooldowns[index] - deltaSeconds);
            return true;
        }

        public static bool TryActivate(ComponentStore store, int entityId, int slot, out AbilityInstance ability)
        {
            ability = default(AbilityInstance);
            if (store == null || entityId < 0 || entityId >= ComponentStore.MAX_ENTITIES ||
                slot < 0 || slot >= store.AbilityCount[entityId]) return false;
            ability = store.GetAbility(entityId, slot);
            return ability.CanActivate();
        }

        public static bool AbilityCommit(ComponentStore store, int entityId, int slot)
        {
            if (!TryActivate(store, entityId, slot, out var ability)) return false;
            ability.Activate();
            store.SetAbility(entityId, slot, ability);
            return true;
        }

        public static bool TickCooldown(ComponentStore store, int entityId, int slot, float deltaSeconds)
        {
            if (store == null || deltaSeconds <= 0f || entityId < 0 || entityId >= ComponentStore.MAX_ENTITIES ||
                slot < 0 || slot >= store.AbilityCount[entityId]) return false;
            var ability = store.GetAbility(entityId, slot);
            if (ability.CurrentCooldown <= 0f) return true;
            ability.CurrentCooldown = System.Math.Max(0f, ability.CurrentCooldown - deltaSeconds);
            store.SetAbility(entityId, slot, ability);
            return true;
        }
    }
}
