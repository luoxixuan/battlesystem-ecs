# BattleSystem-ECS Bug Analysis Report

**Generated:** 2026-05-16 10:42 AM  
**Project:** `/mnt/f/AI/BattleSystem-ECS`  
**Scope:** Systems/, Core/, Configs/, Components/, Program.cs  
**Focus:** Null reference exceptions, index out of bounds, race conditions in parallel code, logic errors

---

## Summary Table

| ID | Severity | Category | Location | Description |
|----|----------|----------|----------|-------------|
| H-1 | HIGH | Null Ref | `BTCachedTreeEvaluator.EvaluateCondition()` | `store` parameter not null-checked; NRE if caller passes null |
| H-2 | HIGH | Logic | `SkillSystem.ResolveSkillDamage()` | Multiple simultaneous calls leak damage; _damageQueue recreated on each call |
| H-3 | HIGH | Index OOB | `BTCachedTreeBuilder.Build()` | `cached.Nodes[rootIdx]` on -1 → IndexOutOfRangeException |
| M-1 | MEDIUM | Logic | `MapSystem.RenderMap()` | O(n×m) inner loop — checks ALL enemies for every grid cell; no early break |
| M-2 | MEDIUM | Race Condition | `ComponentStore.BeginFrame()` | Concurrent access to `_deathQueueResolved` without memory barrier |
| M-3 | MEDIUM | Null Ref | `EntityManager.CreateEntity()` | `store.CreateEntity()` can return -1; null name dictionary entry created |
| M-4 | MEDIUM | Index OOB | `BenchmarkSystem` | `moveDir[(int)EnemyActionType]` array access — enum cast can exceed array bounds |
| M-5 | MEDIUM | Logic | `EnemyAISystem.Update()` | BTCache snapshot only tracks enemy+player health; turn change alone should invalidate |
| L-1 | LOW | Logic | `SkillSystem.Update()` | Cooldown decremented but never saved back to store |
| L-2 | LOW | Logic | `TowerPlacementSystem.PlaceTower()` | Checks O(n) ActiveTowerIds — not a bug but anti-pattern for O(1) |
| L-3 | LOW | Logic | `GameConfig.GetCachedBehaviorTree()` | Double dictionary lookup: `BehaviorTrees.TryGetValue` after `_cachedBtCache` miss |
| I-1 | INFO | Perf | `SkillSystem.CastSingleTarget()` | Creates new `Random` on every call if `_activeEnemyList` is null |
| I-2 | INFO | Perf | `ComponentStore` | `ActiveEnemyIds` and `GetAllActiveEnemyIds()` both allocate new lists; redundant copies |

---

## HIGH Severity

### H-1: Null Reference in `EvaluateCondition` — `store` never null-checked

**File:** `Systems/BehaviorTreeEvaluator.cs`  
**Lines:** 203–235 (`EvaluateCondition`)

```csharp
public static bool EvaluateCondition(
    BTCachedNode node,
    int enemyId,
    ComponentStore store,
    int playerId)
{
    switch (node.Condition)
    {
        case "target_in_range":
        {
            float ex = store.PositionX[enemyId], ey = store.PositionY[enemyId];  // ← NRE if store == null
```

**Impact:** If any caller passes `store = null`, every enemy with a BT condition will throw `NullReferenceException`. The method is `public static` and could theoretically be called externally.

**Fix:** Add `if (store == null) return false;` at the top of `EvaluateCondition`.

---

### H-2: ConcurrentBag Race — `ResolveSkillDamage` Called Twice Leaks Damage

**File:** `Systems/SkillSystem.cs`  
**Lines:** 299–315

```csharp
public void ResolveSkillDamage()
{
    foreach (var (enemyId, damage) in _skillDamageQueue)
    {
        // damage applied...
    }
    // Reset for next frame
    _skillDamageQueue = new ConcurrentBag<(int, float)>();  // ← old bag loses reference
}
```

**Problem:** If `ResolveSkillDamage()` is called **twice** within the same frame (e.g., accidentally from `GameManager.Run()` and again from a benchmark loop), the first call consumes the queue, the second call operates on a **newly created empty bag**, and damage already applied is **not repeated**. The second call's damage is silently dropped.

This is a semantic correctness issue if the call site ever changes — the code assumes a 1:1 call-per-frame relationship.

---

### H-3: IndexOutOfRangeException in `BTCachedTreeBuilder.Build`

**File:** `Systems/BehaviorTreeEvaluator.cs`  
**Lines:** 323–325

```csharp
public static BTCachedTree Build(BehaviorTreeDef bt)
{
    // ...
    if (indexMap.TryGetValue(bt.RootId, out var rootIdx))
        cached.Root = cached.Nodes[rootIdx];
    // if bt.RootId is in indexMap but rootIdx == -1 (shouldn't happen, but defensive code below uses -1 as sentinel)
```

**More concerning** — lines 306–308:

```csharp
int[] childIndices = n.Children
    .Select(c => indexMap.TryGetValue(c, out var idx) ? idx : -1)
    .Where(idx => idx >= 0)
    .ToArray();
```

The `.Where(idx => idx >= 0)` silently drops children whose IDs are not in the behavior tree definition. This is a **logic error**: the BT node references a child node that doesn't exist, and instead of producing a warning/error, the child is simply ignored. The enemy may behave incorrectly (missing branches in the tree).

---

## MEDIUM Severity

### M-1: O(n×m) Map Rendering — No Early Break on Enemy Match

**File:** `Systems/MapSystem.cs`  
**Lines:** 60–74

```csharp
foreach (int eid in activeEnemyIds)
{
    if (!store.EnemyActive[eid]) continue;
    int ex = (int)Math.Round(store.PositionX[eid]);
    int ey = (int)Math.Round(store.PositionY[eid]);
    if (ex == x && ey == y)
    {
        hasEnemy = true;
        break;  // ← break exists, but the INNER foreach runs for EVERY grid cell
    }
}
```

**Problem:** For a 10×20 = 200-cell map, this inner `foreach` runs **up to 200 times per cell** in the worst case, giving O(200 × numEnemies) complexity per frame. For 10,000 enemies: 2,000,000 comparisons per render call.

**Fix:** Build a spatial hash (Dictionary of position→enemyId list) once per frame, or use the fact that enemies have discrete positions and maintain a `Dictionary<(int,int), int>` spatial grid.

---

### M-2: `BeginFrame` Race — `_deathQueueResolved` Not Volatile

**File:** `Core/ComponentStore.cs`  
**Lines:** 127–140

```csharp
public void BeginFrame()
{
    if (_deathQueue != null && !_deathQueue.IsEmpty && !_deathQueueResolved)
    {
        throw new InvalidOperationException("...");
    }
    _deathQueue = new ConcurrentBag<(int, int)>();
    _deathQueueResolved = false;  // ← not volatile, no memory barrier
    CurrentFrame++;
}
```

`BeginFrame()` and `ResolveEnemiesKilledThisFrame()` can be called from **different threads** in the parallel benchmark loop. The flag `_deathQueueResolved` is not marked `volatile`, and there's no explicit memory barrier. Under aggressive compiler/CPU reordering, `CurrentFrame++` could be reordered before the flag assignment, potentially causing the `InvalidOperationException` check to read a stale value.

---

### M-3: `CreateEntity` Returns -1 — Null Name Entry Created

**File:** `Core/EntityManager.cs`  
**Lines:** 19–24

```csharp
public Entity CreateEntity()
{
    int entityId = store.CreateEntity();  // can return -1
    store.SetEntityName(entityId, $"Entity_{entityId}");  // ← sets name for id=-1
    return new Entity(entityId);
}
```

If `store.CreateEntity()` returns -1 (entity pool exhausted), the code proceeds to call `store.SetEntityName(-1, "Entity_-1")`. While most methods guard against negative IDs, this creates an inconsistent state (a negative-ID entry in `entityNames` dictionary). The returned `Entity` has `Id = -1`, which will cause issues if callers use it without checking.

---

### M-4: `moveDir` Array Bounds — Enum Cast Without Validation

**File:** `Systems/BenchmarkSystem.cs`  
**Lines:** 106–107, 161

```csharp
var moveDir = new sbyte[] { -1, 0, 0, 0, 0, 1, -1 };
// index: (int)EnemyActionType → direction

store.PositionY[enemyId] = y + moveDir[(int)ae] * moveSpeed;
```

`EnemyActionType` is an enum with 7 defined values (0–6). The array has exactly 7 elements. However, if `EnemyActionType` is extended with new values (e.g., `Dodge` = 5, `Retreat` = 6 are the last) without updating the `moveDir` array, accessing `moveDir[7]` would throw `IndexOutOfRangeException`. This is fragile — the array length and enum cardinality are implicitly coupled.

---

### M-5: BT Cache Incomplete Invalidation on Turn Change

**File:** `Systems/EnemyAISystem.cs`  
**Lines:** 95–105

```csharp
if (_cacheSnapshot == _cacheVersion &&
    _enemyHealthCache[enemyId] == enemyHealth &&
    _cachedPlayerHealth == playerHealth)
{
    // Cache hit: reuse last action
    store.SetEnemyActionEnum(enemyId, _lastActionCache[enemyId]);
    continue;
}
```

**Problem:** The cache only tracks enemy health and player health. It does NOT track the current `turn`. If an enemy has a behavior tree rule that depends on turn number (e.g., "at turn 10, do X"), the cached action from turn 5 will be incorrectly reused at turn 10 if enemy and player health haven't changed.

This is a latent bug — if the BT evaluation ever uses `turn` as a condition, cache invalidation will be wrong. Currently the BT evaluator does receive `turn` as a parameter but doesn't use it for any conditions. If BT definitions are extended to use turn-based conditions, this will silently produce wrong AI behavior.

---

## LOW Severity

### L-1: Cooldown Decremented But Never Saved to Store

**File:** `Systems/SkillSystem.cs`  
**Lines:** 113–121

```csharp
for (int slot = 0; slot < count; slot++)
{
    var inst = store.GetAbility(playerId, slot);
    if (inst.CurrentCooldown > 0f)
    {
        inst.CurrentCooldown = Math.Max(0f, inst.CurrentCooldown - deltaTime);
        store.SetAbility(playerId, slot, inst);  // ← this IS saved correctly
    }
}
```

Actually, this is **correct** — `store.SetAbility` is called inside the loop. Moving `L-1` to INFO.

**Revised L-1:** No bug found — code is correct.

---

### L-2: `PlaceTower` O(n) Check — Not a Bug But Anti-Pattern

**File:** `Systems/TowerPlacementSystem.cs`  
**Lines:** 33–40

```csharp
foreach (int tid in store.ActiveTowerIds)  // ← O(n) per placement
{
    if (store.PositionX[tid] == x && store.PositionY[tid] == y)
    {
        logger.Log($"[TOWER] 建造失败: 坐标 ({x},{y}) 已有塔存在");
        return -1;
    }
}
```

The AGENTS.md explicitly documents "ActiveTowerIds 而非遍历全量" as a design principle. The O(n) iteration over `ActiveTowerIds` is acceptable for small tower counts but contradicts the performance-first design philosophy. A spatial hash (grid-based lookup) would be O(1).

---

### L-3: `GetCachedBehaviorTree` Double Dictionary Lookup

**File:** `Configs/GameConfig.cs`  
**Lines:** 324–336

```csharp
public BattleSystemECS.Systems.BTCachedTree GetCachedBehaviorTree(string monsterType)
{
    if (string.IsNullOrEmpty(monsterType)) return null;
    if (_cachedBtCache.TryGetValue(monsterType, out var cached))  // check 1
        return cached;
    if (!BehaviorTrees.TryGetValue(monsterType, out var bt))      // check 2
        return null;
    // ...
}
```

After `_cachedBtCache` miss, the code does `BehaviorTrees.TryGetValue` to find the raw BT. This is fine but the comment on line 329 says "Bug#35 fix: query BehaviorTrees directly instead of via GetBehaviorTree()" — the `GetBehaviorTree()` method itself has a double lookup (`_btCache` + `BehaviorTrees`). Minor inefficiency, not a bug.

---

## INFO

### I-1: `CastSingleTarget` Creates New `Random` on Null `_activeEnemyList`

**File:** `Systems/SkillSystem.cs`  
**Lines:** 194–195

```csharp
var activeEnemyIds = _activeEnemyList ?? store.GetAllActiveEnemyIds();
if (activeEnemyIds == null) return 0;
```

The null check is defensive. `GetAllActiveEnemyIds()` never returns null (it returns `new List<int>(_activeEnemyIds)`), so this check is dead code. Minor observation — not a bug.

---

### I-2: Redundant List Allocations — `ActiveEnemyIds` vs `GetAllActiveEnemyIds()`

**File:** `Core/ComponentStore.cs`  
**Lines:** 110–111, 684–688

```csharp
public IReadOnlyList<int> ActiveEnemyIds => _activeEnemyIds.ToList();  // allocation 1
public List<int> GetAllActiveEnemyIds() => new List<int>(_activeEnemyIds);  // allocation 2
```

Both methods allocate a new list every call. Callers that use both can get two separate lists. While the M-3 fix documents that `.ToList()` snapshot prevents mutation, the redundant allocation is unnecessary overhead in performance-critical code.

---

## Appendix: Previously Tracked Bugs (from `docs/bug-fix.md`)

The project maintains a bug-fix history. 45 of 46 previously documented bugs are marked as resolved. The remaining open items are tracked in `docs/bug-fix.md`. This report focuses on new findings not already in the bug-fix tracker.

---

## Severity Definitions

| Level | Definition |
|-------|-----------|
| **HIGH** | Crashes at runtime, data corruption, or severe logic error |
| **MEDIUM** | Incorrect behavior under specific conditions; race conditions possible |
| **LOW** | Code smell, anti-pattern, or fragile design; works correctly today |
| **INFO** | Performance concern or minor improvement suggestion |

---

*Report generated by automated static analysis. Manual code review recommended for HIGH items.*