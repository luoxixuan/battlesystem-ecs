namespace BattleSystemECS.Components
{
    public struct UpgradeComponent
    {
        public System.Collections.Generic.List<string> Skills { get; set; }
        public System.Collections.Generic.List<string> Buffs { get; set; }
        public float NextUpgradeThreshold { get; set; }

        public UpgradeComponent()
        {
            Skills = new System.Collections.Generic.List<string>();
            Buffs = new System.Collections.Generic.List<string>();
            NextUpgradeThreshold = 100f;
        }
    }
}
