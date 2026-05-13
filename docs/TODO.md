# BattleSystem-ECS 设计治理与 Bug 追踪

> 来源：漂泊者审查反馈（2026-05-13）
> 最后更新：2026-05-13（commit `69bb49b`）

---

## 执行顺序（设计治理 10 项）

| # | 优先级 | 任务 | 状态 | 备注 |
|---|--------|------|------|------|
| 1 | 最高 | 主循环刷新缓存 | ✅ 已完成 | GameManager 每回合对所有系统调用 SetTurn()（`commit 743664c`）|
| 2 | 最高 | 并行系统两阶段死亡解析 | ✅ 已完成 | PlayerAttack/TowerAttack 两阶段（`commit 2248d4a`），统一帧末 resolve（`commit 3bd1c9c`）|
| 3 | 最高 | 禁止并行段直接改 active list / EventBus | ✅ 已完成 | EnemyAI 两阶段（`ccc42e3`），EventBus 加锁（`afb988d`），两阶段模式保证调用路径安全 |
| 4 | 最高 | SkillSystem 击杀统一 DestroyEntity | ✅ 已完成 | HandleKill 只 queue 死亡（`commit 3bd1c9c`），帧末统一 resolve |
| 5 | 高 | ComponentStore 生命周期字段收口 | ✅ 已完成 | ActiveEnemyIds/TowerIds 暴露为 IReadOnlyList（`commit 840bc3e`），freeEntityIds Stack 并发安全由两阶段模式保证 |
| 6 | 高 | DestroyEntity 完整清理 | ✅ 已完成 | 清所有 archetype 字段（`commit c0d85cf`）|
| 7 | 高 | GameConfigLoader 完整解析 | ✅ 已完成 | 解析 MaxHealth / StartingSkills（`commit 736746c`）|
| 8 | 高 | EventBus 并行安全改造 | ✅ 已完成 | lock + snapshot iteration + Reset()（`commit afb988d`）|
| 9 | 中 | 击杀奖励集中化 | ✅ 已完成 | 帧末统一 resolve 内处理（`commit 3bd1c9c`）|
| 10 | 中 | Benchmark 真实系统链路 | ✅ 已完成 | mode 4 真实系统链路 benchmark（`commit 7ef56aa`），AGENTS.md 写入 dual benchmark 规则（`commit 41cc6a5`）|

---

## Bug 追踪汇总

| 严重度 | 总数 | 已修复 | 未修复 |
|--------|------|--------|--------|
| HIGH   | 13   | 13     | 0      |
| MEDIUM | 15   | 14     | 1      |
| LOW    | 9    | 9      | 0      |
| INFO   | 6    | 5      | 1      |
| **合计** | **46** | **45** | **1** |

### 未修复项

| # | 严重度 | 描述 |
|---|--------|------|
| — | MEDIUM | 待追踪（bug-fix.md 内有记录）|
| — | INFO | 待追踪 |

---

## 并行安全原则（两阶段模式）

所有涉及并行写共享状态的系统，必须遵循以下原则：

### 两阶段模式（Two-Phase Pattern）

```
并行段（Parallel.For）
  → 只读组件数据，收集 damage/death 事件到 ConcurrentBag
  → 禁止写 EnemyHealth / PlayerHealth / ActiveEnemyIds / ActiveTowerIds / EventBus

串行段（帧末统一结算）
  → 从 ConcurrentBag 取出事件，串行 apply damage（`enemyHealth -= damage`）
  → QueueEnemyDeath → ResolveEnemiesKilledThisFrame() 统一销毁实体 + 结算奖励
```

### 调用链

```
GameManager.Run() / BenchmarkSystem
  → BeginFrame()（重置 queues）
  → 各系统 Update()（只 queue，不 resolve）
  → ResolveEnemiesKilledThisFrame()（统一结算，死亡队列自清空）
```

### 关键原则

- **damage queue 存 raw value**：`(enemyId, damage)` + `enemyHealth -= damage` 累加
  - ❌ 禁止存 `(enemyId, newHealth)`，否则 last-write-wins，多攻击者丢伤害
- **帧末唯一死亡结算点**：系统只 queue，GameManager/Benchmark 统一 resolve
- **EnemyAI 两阶段**：并行段做 BT 评估 + 写 EnemyActionEnum，串行段执行动作（含 EventBus.Publish）

---

## 性能基准

| benchmark | FPS | 说明 |
|-----------|-----|------|
| mode 2（合并热路径） | ~9500 | 手写合并热路径，参考用 |
| mode 4（真实系统链路） | ~5100 | **主指标**，直接调用各系统 `.Update()` |

mode 2 和 mode 4 是不同的语义，**不要再用一个 FPS 代表全部性能**。

测试覆盖：48 单元测试。

---

## 系统说明（关键设计更新）

| 系统 | 职责 | 关键设计 |
|------|------|---------|
| EnemyAISystem | 行为树评估 | **两阶段**：并行 BT 评估写 EnemyActionEnum，串行动作执行（含 EventBus.Publish）|
| PlayerTowerAttackSystem | 玩家攻击 | **两阶段**：并行收集 `(enemyId, damage)`，串行 `enemyHealth -= damage` + queue 死亡 |
| TowerAttackSystem | 塔攻击 | **两阶段**：遍历 `ActiveTowerIds`，并行收集 damage，串行 apply + queue 死亡 |
| SkillSystem | 技能施放 | **只 queue 死亡，帧末统一 resolve** |
| BenchmarkSystem | 性能压测 | **dual mode**：mode 2 合并热路径 / mode 4 真实系统链路，各独立计时 |

---

## 今日完成（2026-05-13）

| # | 内容 | commit |
|---|------|--------|
| 1 | EnemyAI 两阶段重构（并行 eval + 串行动作执行） | `ccc42e3` |
| 2 | 删除未使用队列字段 `_playerDamageQueue` / `_eventQueue` | `a92116c` |
| 3 | PlayerAttack + TowerAttack 两阶段（并行收集 → 串行 apply） | `2248d4a` |
| 4 | damage queue 累加正确性修复（存 damage 不存 newHealth） | `d707920` |
| 5 | TowerAttackSystem.cs 残留 `float newHealth` 死代码 | `d707920` |
| 6 | 死亡队列自清空（Resolve 后 new ConcurrentBag） | `7ef56aa` |
| 7 | 模式 4 真实系统链路 benchmark | `7ef56aa` |
| 8 | 统一帧末死亡结算（系统只 queue，GameManager/Benchmark resolve） | `3bd1c9c` |
| 9 | AGENTS.md 并行安全原则写入 | `41cc6a5` |
| 10 | 文档同步（46 bugs、48 tests、mode 2/4 FPS） | `63d9c1f` |

---

## 开发理念

详见 `docs/philosophy.md`。

核心：
1. **先问"对不对"，再问"快不快"** — 牺牲正确性换速度是走捷径
2. **两阶段模式是通用的"延迟写，统一同步"思维** — 并行段只读不写，串行段做真正的写
3. **职责收口比功能实现更重要** — 系统只 queue，调度层统一 resolve
4. **死代码是负债** — 删除不仅是清理，更是降低认知负担
5. **Benchmark 必须代表真实调用链** — 测量必须代表真实场景
6. **文档是开发的一部分** — 每次 commit 时同步更新
7. **接受不完美，但不要假装完美** — 标注状态比假装完成更好

---

_记录时间：2026-05-13 21:20 GMT+8_