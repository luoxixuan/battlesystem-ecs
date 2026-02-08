using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    public class MapSystem
    {
        private IRenderer renderer;

        public MapSystem(IRenderer renderer)
        {
            this.renderer = renderer;
        }

        public void RenderMap(int width, int height)
        {
            Console.WriteLine();
            Console.WriteLine("========================================");
            renderer.Log($"[MAP] 地图大小: {width}x{height}");
            renderer.Log("[MAP] ================================");
            Console.WriteLine();
        }
    }
}
