using System;
using System.Collections.Generic;

namespace BattleSystemECS.Components
{
    /// <summary>
    /// Player buff type as bit flags — O(1) query instead of O(n) string list traversal.
    /// </summary>
    [Flags]
    public enum BuffType
    {
        None = 0,
        AttackBoost = 1 << 0,    // "Attack+10%" → damage × 1.1
        CritRateBoost = 1 << 1,  // "Crit Rate+5%" → crit chance +5%
        DefenseBoost = 1 << 2,   // "Defense+10%" → future use
    }

    /// <summary>
    /// Buff/Debuff data attached to an entity.
    /// </summary>
    public struct BuffData
    {
        public string Type;       // "Slow", "Poison", "Burn", "Stun", "AttackUp", "RangeUp"
        public int Duration;      // Turns remaining
        public float Magnitude;   // Effect strength (e.g., 0.5 = 50% slow)
        public int SourceId;      // Entity that applied this buff

        public bool IsExpired => Duration <= 0;

        public BuffData(string type, int duration, float magnitude, int sourceId)
        {
            Type = type;
            Duration = duration;
            Magnitude = magnitude;
            SourceId = sourceId;
        }
    }
}
