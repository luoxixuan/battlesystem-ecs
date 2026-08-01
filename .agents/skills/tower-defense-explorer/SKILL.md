---
name: tower-defense-explorer
version: 1.0.0
description: "塔防 + ECS + GAS 开源仓库调研知识库与爬取工具：BattleSystem-ECS 项目的架构参考与机制灵感来源。"
metadata:
  project: BattleSystem-ECS
  platforms: [windows]
---

# Tower Defense Explorer（塔防 + ECS + GAS 调研）

BattleSystem-ECS 项目的塔防游戏机制调研工具与知识库。从 GitHub 爬取塔防 + ECS + GAS 相关开源仓库，沉淀可落地的架构参考和具体游戏机制。

## When to Use

- 设计塔防玩法新机制时（技能系统、分裂、护盾、弹道、锁链等）——先查知识库，找现成参考
- 需要更新调研数据 / 补充新仓库时——运行爬取脚本
- 回答"某机制业界怎么做"的问题时——引用知识库来源

## 内容结构

```
.agents/skills/tower-defense-explorer/
├── SKILL.md                          ← 本文件
├── scripts/
│   └── tower_defense_explorer.py     ← 爬取脚本（--deep 模式拉源码结构）
└── references/
    ├── knowledge_base.json           ← 原始爬取数据（85+ 仓库）
    └── visited_repos.json            ← 已访问仓库记录（避免重复爬）
```

生成的**可读知识库**在 `Research/tower_defense_knowledge.md`（268 行，2026-06-17 生成，85 个仓库分析结果）。

## 机制速查（知识库精华）

| 机制 | 做法 | 参考仓库 |
|------|------|---------|
| 技能系统 | GAS 风格 Ability + Modifier 分离 | felipeggrod/gasify, intrxx/Obsidian, Pantong51/GASContent |
| 分裂/克隆 | 敌人死后分裂为多个小怪 | MaiKuraki/UnityStarter, pshenok/server-survival |
| 实体管理器 | ECS 风格实体创建/销毁/查询 | sebas77/Svelto.ECS, Gornhoth/Unity-Smoothed-Particle-Hydrodynamics |
| N击护盾 | 需 N 次命中击破的护盾/伤害阈值盾 | FlameskyDexive/Legends-Of-Heroes, MaiKuraki/UnityStarter |
| 弧线弹道 | 迫击炮/抛射弹道，无视地形 | PixeyeHQ/actors.unity, chromealex/ecs, Antoshidza/NSprites |
| 锁链/连接 | 两个敌人生命/伤害共享 | prabdhal/Tower-Defence-3D, MaiKuraki/UnityStarter |

完整机制清单见 `Research/tower_defense_knowledge.md`。

## 更新流程

```bash
# 轻量模式（只拉 README）
python .agents/skills/tower-defense-explorer/scripts/tower_defense_explorer.py

# 深度模式（README + 文件树 + 源码片段，推荐）
python .agents/skills/tower-defense-explorer/scripts/tower_defense_explorer.py --deep

# 同步知识库到 Research/
cp .agents/skills/tower-defense-explorer/scripts/../references/knowledge_base.json Research/ 2>/dev/null
# 实际知识库 md 由脚本直接写入 Research/tower_defense_knowledge.md
```

依赖：GitHub token（环境变量 `GITHUB_TOKEN`，历史 cron 用 `~/.hermes/github_token`，WSL Hermes 已卸载）。Windows 侧运行需配置相同 token。

## 历史背景

- 原为 WSL Hermes cron job `tower_defense_explorer`（job id `0dd22ff2f2fb`），每日/手动运行，输出同步到 `BattleSystem-ECS/Research/tower_defense_knowledge.md` 并 git commit
- 2026-08-01 WSL Hermes 已卸载，cron 不再运行；本 skill 保留脚本与数据，需要时手动运行
- 知识库最后更新：2026-06-17（85 个仓库）
