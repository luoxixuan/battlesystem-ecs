using BattleSystemECS.Components;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// 战斗事件总线 — 向渲染层发结构化事件。
    /// 与 IRenderer（人类可读日志）职责分离。
    /// Unity 侧实现 UnityEventBus，测试用 NullEventBus（所有方法空实现）。
    /// </summary>
    public interface IBattleEventBus
    {
        // ── 实体生命周期 ──
        void OnEntityCreated(int entityId, float x, float y, string entityType);
        /// <summary>Called when a tower entity is created. Passes TowerType for rendering differentiation.</summary>
        void OnTowerCreated(int entityId, float x, float y, TowerType towerType);
        void OnEntityDestroyed(int entityId);

        // ── 位置变化（移动阶段结束后批量发射）──
        void OnPositionChanged(int entityId, float x, float y);

        // ── 战斗 ──
        void OnDamageDealt(int targetId, float amount, string damageType, bool isCritical);
        void OnEntityKilled(int entityId, int killerId);

        // ── 投射物 ──
        void OnProjectileFired(float fromX, float fromY, float toX, float toY, float speed);

        // ── 波次 / 流程 ──
        void OnWaveStarted(int waveNumber);
        void OnGameOver(bool victory);
    }

    /// <summary>
    /// NullEventBus — 所有方法空实现。测试 + headless 模式使用。
    /// 单例避免重复分配。
    /// </summary>
    public sealed class NullEventBus : IBattleEventBus
    {
        public static readonly NullEventBus Instance = new NullEventBus();

        public void OnEntityCreated(int entityId, float x, float y, string entityType) { }
        public void OnTowerCreated(int entityId, float x, float y, TowerType towerType) { }
        public void OnEntityDestroyed(int entityId) { }
        public void OnPositionChanged(int entityId, float x, float y) { }
        public void OnDamageDealt(int targetId, float amount, string damageType, bool isCritical) { }
        public void OnEntityKilled(int entityId, int killerId) { }
        public void OnProjectileFired(float fromX, float fromY, float toX, float toY, float speed) { }
        public void OnGameOver(bool victory) { }
        public void OnWaveStarted(int waveNumber) { }
    }
}
