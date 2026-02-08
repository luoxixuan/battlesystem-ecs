using System;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// 时间系统 - 提供全局时间
    /// </summary>
    public static class Time
    {
        public static float DeltaTime { get; set; } = 0f;  // 从外部设置的增量时间
        public static float TotalTime { get; set; } = 0f; // 总游戏时间

        // 定时器相关
        public static float AttackTimer { get; set; } = 0f;
        public static float WaveTimer { get; set; } = 0f;
    }
}
