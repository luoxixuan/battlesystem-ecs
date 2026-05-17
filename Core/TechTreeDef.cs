using System;
using System.Collections.Generic;

namespace BattleSystemECS.Config
{
    /// <summary>
    /// 单个科技节点的效果
    /// </summary>
    public class TechEffect
    {
        public string type;   // attack_damage_mult, crit_rate_add, gold_on_kill_mult, ...
        public float value;  // 效果数值
    }

    /// <summary>
    /// 单个科技节点定义
    /// </summary>
    public class TechNodeDef
    {
        public string id;
        public string name;
        public string description;
        public int cost;                        // 研究点数消耗
        public List<string> prerequisites;     // 前置节点 id 列表
        public List<TechEffect> effects;
    }

    /// <summary>
    /// 科技分支（进攻/防御/经济）
    /// </summary>
    public class TechBranchDef
    {
        public string id;
        public string name;
        public string color;
        public List<TechNodeDef> nodes;
    }

    /// <summary>
    /// 完整科技树配置
    /// </summary>
    public class TechTreeConfig
    {
        public int researchPointsPerWave;
        public List<TechBranchDef> branches;
    }
}
