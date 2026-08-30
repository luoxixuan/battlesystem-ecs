using System;
using System.Text.Json;

namespace BattleSystemECS.Core.GAS
{
    internal readonly struct StaticSkillRecord
    {
        internal readonly string Name;
        internal readonly int Range, Width, Height, ManaCost;
        internal readonly float Cooldown, DamageMultiplier;
        internal StaticSkillRecord(string name, int range, int width, int height, float cooldown, float damageMultiplier, int manaCost) { Name = name; Range = range; Width = width; Height = height; Cooldown = cooldown; DamageMultiplier = damageMultiplier; ManaCost = manaCost; }
    }

    internal static class StaticSkillSchemaAdapter
    {
        internal static StaticSkillRecord Read(JsonElement node, string path, int id)
        {
            if (node.ValueKind != JsonValueKind.Object) throw new CatalogValidationException($"{path}: expected object for id {id}");
            string name = Required(node, "Name", path, id);
            return new StaticSkillRecord(name, Number(node, "AttackRange", path, id), Number(node, "AreaWidth", path, id), Number(node, "AreaHeight", path, id), Decimal(node, "Cooldown", path, id), Decimal(node, "DamageMultiplier", path, id), Number(node, "ManaCost", path, id));
        }
        private static string Required(JsonElement node, string key, string path, int id) { if (!node.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new CatalogValidationException($"{path}: missing {key} for id {id}"); return value.GetString(); }
        private static int Number(JsonElement node, string key, string path, int id) { if (!node.TryGetProperty(key, out var value)) throw new CatalogValidationException($"{path}: missing {key} for id {id}"); if (!value.TryGetInt32(out var number) || number < 0) throw new CatalogValidationException($"{path}: invalid {key} for id {id}"); return number; }
        private static float Decimal(JsonElement node, string key, string path, int id) { if (!node.TryGetProperty(key, out var value)) throw new CatalogValidationException($"{path}: missing {key} for id {id}"); if (!value.TryGetSingle(out var number) || number < 0 || float.IsNaN(number) || float.IsInfinity(number)) throw new CatalogValidationException($"{path}: invalid {key} for id {id}"); return number; }
    }
}
