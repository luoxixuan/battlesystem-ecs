using BattleSystemECS.Components;

namespace BattleSystemECS.Tests.Infrastructure
{
    /// <summary>
    /// 配置驱动的敌人生成规格。字段默认值对应测试中最常见的"普通敌人"
    /// <c>AddEnemy(0, 0, 5f, 100f, 100f, 5f, 10, 1, "TestEnemy")</c>：
    /// 原点 (0,0)、5f 移速、100/100 血、5f 伤害、10 金币、第 1 波、无抗性。
    /// 通过 lambda（<c>e =&gt; { e.Health = 500; e.Name = "Boss"; }</c>）或对象初始化器覆盖任意字段。
    /// </summary>
    public sealed class EnemySpec
    {
        /// <summary>出生 X 坐标。</summary>
        public float X = 0f;

        /// <summary>出生 Y 坐标。</summary>
        public float Y = 0f;

        /// <summary>移动速度。</summary>
        public float MoveSpeed = 5f;

        /// <summary>当前生命（也作为 MaxHealth 未显式指定时的默认值）。</summary>
        public float Health = 100f;

        /// <summary>最大生命。null 表示等于 <see cref="Health"/>（最常见的满血敌人）。</summary>
        public float? MaxHealth = null;

        /// <summary>攻击伤害。</summary>
        public float Damage = 5f;

        /// <summary>击杀金币奖励。</summary>
        public int GoldReward = 10;

        /// <summary>所属波次。</summary>
        public int WaveNumber = 1;

        /// <summary>名称（用于行为树/技能查找）。</summary>
        public string Name = "TestEnemy";

        /// <summary>护甲（0..1 减伤）。</summary>
        public float Armor = 0f;

        /// <summary>护盾。</summary>
        public float Shield = 0f;

        /// <summary>魔法抗性。</summary>
        public float MagicResist = 0f;

        /// <summary>火抗。</summary>
        public float FireResist = 0f;

        /// <summary>冰抗。</summary>
        public float IceResist = 0f;

        /// <summary>雷抗。</summary>
        public float LightningResist = 0f;

        /// <summary>神圣抗性。</summary>
        public float HolyResist = 0f;
    }

    /// <summary>
    /// 配置驱动的塔放置规格。默认值对应测试中最常见的"基础攻击塔"：
    /// Basic 类型、50f 伤害、3 格射程、1f 攻速、50f 造价。
    /// </summary>
    public sealed class TowerSpec
    {
        /// <summary>塔格 X。</summary>
        public int X = 0;

        /// <summary>塔格 Y。</summary>
        public int Y = 0;

        /// <summary>塔类型。</summary>
        public TowerType Type = TowerType.Basic;

        /// <summary>伤害。</summary>
        public float Damage = 50f;

        /// <summary>射程（格）。</summary>
        public int Range = 3;

        /// <summary>攻速。</summary>
        public float Speed = 1f;

        /// <summary>造价。</summary>
        public float Cost = 50f;
    }

    /// <summary>
    /// 配置驱动的玩家生成规格。默认值对应测试中最常见的玩家：
    /// 实体 id 0、3f 射程、3f 攻速、10f 伤害、1 级、10 条命、原点、200 血、0 金币。
    /// </summary>
    public sealed class PlayerSpec
    {
        /// <summary>玩家实体 id（0 为默认玩家槽）。</summary>
        public int EntityId = 0;

        /// <summary>攻击射程。</summary>
        public float AttackRange = 3f;

        /// <summary>攻击速度。</summary>
        public float AttackSpeed = 3f;

        /// <summary>攻击伤害。</summary>
        public float AttackDamage = 10f;

        /// <summary>当前等级。</summary>
        public int Level = 1;

        /// <summary>基础生命条数。</summary>
        public int BaseLives = 10;

        /// <summary>出生 X 坐标。</summary>
        public float X = 0f;

        /// <summary>出生 Y 坐标。</summary>
        public float Y = 0f;

        /// <summary>初始金币。</summary>
        public float Gold = 0f;

        /// <summary>初始生命（同时设置当前与最大）。</summary>
        public float Health = 200f;
    }
}
