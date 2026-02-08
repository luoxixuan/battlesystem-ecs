namespace BattleSystemECS.Components
{
    public struct EnemyComponent
    {
        public float MoveSpeed { get; set; }
        public float Health { get; set; }
        public float MaxHealth { get; set; }
        public float Damage { get; set; }
        public int GoldReward { get; set; }
        public int WaveNumber { get; set; }
    }
}
