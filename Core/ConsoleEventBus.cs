using BattleSystemECS.Components;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// ConsoleEventBus — writes battle events as console lines.
    /// Used by the existing console host. Most events are silent
    /// (logs already go through IRenderer). Only wave/game-end events print.
    /// </summary>
    public sealed class ConsoleEventBus : IBattleEventBus
    {
        public void OnEntityCreated(int entityId, float x, float y, string entityType) { }
        public void OnTowerCreated(int entityId, float x, float y, TowerType towerType) { }
        public void OnEntityDestroyed(int entityId) { }
        public void OnPositionChanged(int entityId, float x, float y) { }
        public void OnPositionsChanged(System.Collections.Generic.List<(int entityId, float x, float y)> changes) { }
        public void OnDamageDealt(int targetId, float amount, string damageType, bool isCritical) { }
        public void OnEntityKilled(int entityId, int killerId) { }
        public void OnProjectileFired(float fromX, float fromY, float toX, float toY, float speed) { }
        public void OnWaveStarted(int waveNumber) { System.Console.WriteLine($"[EVENT] Wave {waveNumber} started"); }
        public void OnGameOver(bool victory) { System.Console.WriteLine(victory ? "[EVENT] Victory!" : "[EVENT] Game Over!"); }
    }
}
