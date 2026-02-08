namespace BattleSystemECS.Components
{
    public struct WaveComponent
    {
        public int CurrentWave { get; set; }
        public int EnemiesRemaining { get; set; }
        public float WaveTimer { get; set; }
    }
}
