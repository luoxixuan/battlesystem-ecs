namespace BattleSystemECS.Components
{
    public struct MapComponent
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[,] Grid { get; set; }

        public MapComponent(int width, int height)
        {
            Width = width;
            Height = height;
            Grid = new byte[width, height];
        }
    }
}
