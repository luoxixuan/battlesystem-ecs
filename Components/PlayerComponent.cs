namespace BattleSystemECS.Components
{
    public struct PlayerComponent
    {
        public float AttackRange { get; set; }
        public float AttackSpeed { get; set; }
        public float AttackDamage { get; set; }
        public int CurrentLevel { get; set; }
    }
}
