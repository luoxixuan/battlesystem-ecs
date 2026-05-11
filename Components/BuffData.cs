using System;
using System.Collections.Generic;

namespace BattleSystemECS.Components
{
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
