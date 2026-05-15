# BattleSystem-ECS Post-Mortem & Lessons Learned

**Date**: 2026-05-16
**Context**: Claude Code review cycle (Round 1 + Round 2), 46 bugs reviewed, 4 critical fixes + 1 revert

---

## 1. L-7 — 错误修复导致帧率暴跌（最重要的教训）

**问题**：EnemyAISystem `_cacheVersion++` 加在 Update() 开始处，意图"每帧失效缓存"。

**现象**：帧率从 ~5100 FPS 跌到 ~2928 FPS（暴跌 40%+）。

**根因**：原设计通过 `_enemyHealthCache[enemyId] == enemyHealth` 具体值比较来控制缓存失效（每个敌人在每帧血量变化 = 自然失效）。`_cacheVersion` 本来就是兜底设计，实际上几乎不触发。加上每帧递增后，所有 10K 敌人永远 cache miss，每帧都重跑完整行为树求值。

**教训**：性能敏感路径上的"修复"，必须先理解原有设计意图。如果一个修复会导致 30-50% 的性能损失，停下来重新审视：这个"bug"真的是 bug 吗？

**行动**：revert 了这个改动（commit `1ad8cb7`），保持原设计。

---

## 2. M-3 快照修复的性能代价

**问题**：`ActiveEnemyIds` / `ActiveTowerIds` 改为 `.ToList()` 快照返回，保证线程安全。

**现象**：每次调用都分配新 List，在 10K 敌人、每帧多次调用的热路径上，内存分配成本明显。mode 4 FPS 从 ~5100 降到 ~3800。

**教训**：正确性换来的性能代价是真实的。高频调用路径上要慎用 `.ToList()` 快照，考虑在调用方本地缓存列表。

---

## 3. report-0515.md 的 H-1 和 L-3 — 误报

**H-1**：TowerAttackSystem 中 `activeTowerIds[ti]` — 报告认为 `ti` 是直接当 entityId 使用，实际上 `ti` 是循环索引，`activeTowerIds[ti]` 正确从快照列表取值。

**L-3**：GameConfigLoader 的 `Substring(start, length)` — 报告认为缺最后一个字符，但 `FindMatchingBrace` 返回的是 exclusive 索引，`length` 参数是正确的。

**教训**：扫描报告（不管是 Hermes 还是 Claude Code）不能照单全收，必须实际看代码确认问题存在，再动手修复。

---

## 4. OpenClaw config patch 对 heartbeat 的破坏性

**问题**：使用 `openclaw config patch` 修改 `agents.defaults.heartbeat` 时，配置被重写为最小有效版本（从 15710 bytes 缩到 5419 bytes），大量字段丢失。

**教训**：对嵌套深的配置，用 `openclaw cron edit` 或直接编辑 `openclaw.json` 文件，不要用 patch 覆盖。

---

## 5. git push 超时 / SIGKILL

**现象**：在网络不稳定时，git push 会超时被 SIGKILL 终止。

**教训**：在循环里 push 时，预估网络状况。必要时加 retry，或者手动确认网络再 push。

---

## 6. Claude Code 报告的历史版本误报

**现象**：Claude Code 对 C-2/C-3/C-4 等的报告是基于旧代码状态的误报（代码已经修复，编译和测试都正常）。

**教训**：外部工具的扫描结果不代表当前代码状态，必须用当前 git HEAD 重新拉取验证。

---

## 7. heartbeat skipWhenBusy 的限制

**问题**：想把心跳设为"主 session 忙碌时停止"，但 `openclaw cron edit` 没有 `--skip-when-busy` 这样的选项。

**教训**：cron 的行为由工具能力决定，不能完全按产品意图定制。需要通过调整 heartbeat prompt 让 agent 自己判断是否应该执行。

---

## 8. `List.RemoveAll()` vs `List.Remove()` 的性能差异（perf-debug-notes 记录）

**问题**：单条删除时使用 `RemoveAll(predicate)` 导致 5x 性能下降（Mode 2: 6400→1200 FPS）。

### 错误代码

```csharp
// ❌ 错误：RemoveAll 用于单条删除，O(n) 线性扫描 + 后续元素位移
_activeEnemyIds.RemoveAll(id => id == entityId);
```

### 正确代码

```csharp
// ✅ 正确：List.Remove(entityId) 找到元素后 O(1) 交换末尾并删除
_activeEnemyIds.Remove(entityId);
```

### 原理

| 方法 | 时间复杂度 | 内存分配 |
|------|-----------|---------|
| `List<T>.Remove(item)` | O(n) 找元素 + O(1) 交换删除 | 无委托分配 |
| `List<T>.RemoveAll(predicate)` | O(n) 全量扫描 + O(n) 元素位移 | 每次调用分配委托对象 |

对于**单条删除**场景，`RemoveAll` 无论匹配几个，都会做完整扫描 + **后续所有元素前移一位**（内存拷贝），还额外分配 lambda 委托，性能损耗在高频路径上极其显著。

### 何时用 RemoveAll

`RemoveAll` 适合**批量删除**场景（如删除所有死亡实体），不适合单条删除：

```csharp
// ✅ 批量删除：RemoveAll 合适
_activeEnemyIds.RemoveAll(id => deadEntityIds.Contains(id));

// ❌ 单条删除：用 Remove
_activeEnemyIds.Remove(entityId);
```

### 排查过程

1. **stash 对比法**: `git stash` 暂存修改，跑基准；`git stash pop` 还原，跑修改后版本。两次都用 `dotnet build -c Release` 干净编译。
2. **逐文件回退**: 用 `git checkout <file>` 逐个去掉改动，定位哪个文件导致性能下降。
3. **ComponentStore 内部定位**: 5个改动逐个 apply，最终确认 `RemoveAll` 是唯一元凶。
4. **数据状态确认**: 发现原始代码跑的是 config 缺失状态，重新建立干净基准（原始代码 + config 正常）后对比。

---

## 架构层面建议

1. **缓存修复前必须做性能基准**：任何涉及热路径缓存的修改，修复前后都要跑 mode 2 / mode 4 压测，确认没有回退。

2. **Review 报告必须逐条验证**：特别是涉及性能（帧率）的修改，要实际跑代码确认，而不是照单修复。

3. **并行安全修复要权衡**：Two-phase 模式（并行收集 + 串行 resolve）已经保护了大部分场景，不要过度加锁。

4. **不要用 `RemoveAll` 替代 `Remove`**：两者语义不同，单条删除场景用 `Remove()`。

5. **配置加载状态对性能的影响**：config 缺失时 FPS 虚高约 2%，benchmark 时必须确认 config 状态一致。

6. **性能测试必须用 `dotnet build -c Release`** 干净编译，Debug/Release 差异显著。

7. **魔法数字统一管理**：L-1 提到的魔法数字分散问题（20/maxTurns, 10/20/mapSize 等）仍然是待处理项。

---

*记录：2026-05-16*
*项目：BattleSystem-ECS*
*相关 commits: a6b0097, 7170918, 0159d46, 9a0996d, fa5f260, 1ad8cb7*