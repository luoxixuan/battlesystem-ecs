namespace BattleSystemECS.Components
{
    public struct PositionComponent
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public PositionComponent(float x, float y, float z = 0f)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }
}
