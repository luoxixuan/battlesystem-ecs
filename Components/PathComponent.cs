using System.Collections.Generic;
using System.Numerics;

namespace BattleSystemECS.Components
{
    /// <summary>
    /// 路径组件 - SOA (Struct of Arrays) 优化
    /// 定义敌人的移动路径
    /// </summary>
    public struct PathComponent
    {
        /// <summary>
        /// 路径点列表
        /// </summary>
        public List<Vector2> PathPoints;

        /// <summary>
        /// 当前路径索引
        /// </summary>
        public int CurrentPathIndex;

        /// <summary>
        /// 是否沿路径移动
        /// </summary>
        public bool FollowPath;

        public PathComponent()
        {
            PathPoints = new List<Vector2>();
            CurrentPathIndex = 0;
            FollowPath = true;
        }
    }
}