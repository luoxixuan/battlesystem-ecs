namespace BattleSystemECS.Components
{
    /// <summary>
    /// 生命值组件
    /// </summary>
    public class HealthComponent
    {
        public float Current { get; set; }
        public float Max { get; set; }

        public HealthComponent(float current, float max)
        {
            Current = current;
            Max = max;
        }
    }

    /// <summary>
    /// 攻击力组件
    /// </summary>
    public class AttackPowerComponent
    {
        public float Value { get; set; }

        public AttackPowerComponent(float value)
        {
            Value = value;
        }
    }

    /// <summary>
    /// 防御力组件
    /// </summary>
    public class DefensePowerComponent
    {
        public float Value { get; set; }

        public DefensePowerComponent(float value)
        {
            Value = value;
        }
    }

    /// <summary>
    /// 名称组件
    /// </summary>
    public class NameComponent
    {
        public string Value { get; set; }

        public NameComponent(string value)
        {
            Value = value;
        }
    }

    /// <summary>
    /// 玩家标签组件
    /// </summary>
    public class PlayerTagComponent
    {
    }

    /// <summary>
    /// 敌人标签组件
    /// </summary>
    public class EnemyTagComponent
    {
    }

    /// <summary>
    /// 战斗状态组件
    /// </summary>
    public class BattleStateComponent
    {
        public enum State
        {
            Idle,
            Fighting,
            Attacking,
            Defending,
            Dead
        }

        public State CurrentState { get; set; }

        public BattleStateComponent()
        {
            CurrentState = State.Idle;
        }
    }
}
