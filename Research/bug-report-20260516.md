# BattleSystem-ECS Bug Report 2026-05-16

## Summary
| Severity | Count |
|----------|-------|
| HIGH     | 4     |
| MEDIUM   | 4     |
| LOW      | 5     |

---

## HIGH Bugs

### H-1: `BenchmarkSystem.cs` — IndexOutOfBounds in moveDir lookup
**File:** `BenchmarkSystem.cs:161`
**Severity:** HIGH — runtime crash under normal conditions
**Category:** Index out of bounds

```csharp
// Pre-compute move direction lookup to eliminate switch in hot path
var moveDir = new sbyte[] { -1, 0, 0, 0, 0, 1, -1 };
// ...
store.PositionY[enemyId] = y + moveDir[(int)ae] * moveSpeed;
```

`EnemyActionType` has 7 values (0=None, 1=MoveToTarget, 2=AttackMelee, 3=RangedAttack, 4=ChargeAttack, 5=Dodge, 6=Retreat). When `ae == EnemyActionType.Retreat` (6), the lookup is valid. However, the array is indexed by casting `(int)ae` directly. If `ae` ever holds a value outside the enum range (due to uninitialized memory or corrupted state), or if a new action type is added without updating `moveDir`, this will `IndexOutOfRangeException`. This is fragile by design — the array size should be validated or the enum should be explicitly bounded.

---

### H-2: `WaveSpawningSystem.cs` — Null/Reference access on failed entity creation
**File:** `WaveSpawningSystem.cs:106`
**Severity:** HIGH — NullReferenceException when entity pool exhausted
**Category:** Null reference

```csharp
int enemyId = store.AddEnemy(
    startX, startY,
    monsterConfig.MoveSpeed,
    monsterConfig.Health,
    monsterConfig.MaxHealth,
    monsterConfig.Damage,
    monsterConfig.GoldReward,
    currentWave,
    enemyName
);
store.SetEntityName(enemyId, enemyName);  // ← enemyId can be -1
store.EnemyBehaviorTree[enemyId] = gameConfig.GetCachedBehaviorTree(waveConfig.MonsterType); // ← -1 index
enemiesSpawnedInWave++;
totalEnemiesSpawned += 5;
```

`store.AddEnemy()` returns `-1` when entity creation fails (line 437 in `ComponentStore.cs`). Lines 106–110 unconditionally execute regardless of failure. An array access with index `-1` will throw `IndexOutOfBoundsException`. Also, `store.SetEntityName(-1, ...)` will corrupt or throw.

**Fix needed:** Guard with `if (enemyId < 0) { renderer.Log("[SPAWN] Failed to spawn enemy (entity pool exhausted)"); return; }`

---

### H-3: `SkillSystem.cs` — Null `_activeEnemyList` in CastSingleTarget, CastCrossArea, CastBoxArea
**File:** `SkillSystem.cs:195, 231, 261`
**Severity:** HIGH — NullReferenceException when `CastSkill` called without prior `SetTurn`
**Category:** Null reference

```csharp
private int CastSingleTarget(float finalDamage, float playerX, float playerY, int range, string name)
{
    int hitCount = 0;
    float closestDistance = float.MaxValue;
    int closestEnemyId = -1;

    var activeEnemyIds = _activeEnemyList ?? store.GetAllActiveEnemyIds(); // ← fallback only
    foreach (int enemyId in activeEnemyIds)                               // ← NPE if _activeEnemyList is null AND store.GetAll...() returns null
    {
```

The `??` fallback calls `store.GetAllActiveEnemyIds()`. If `ComponentStore` is in a state where this method returns null (e.g., after `BeginFrame` is called mid-initialization before any enemies exist), iterating `null` will throw. More critically, `CastSkill` has no `SetTurn` call before it — `SkillSystem.Update` updates cooldowns but `CastSkill` is called externally without guaranteeing `_activeEnemyList` is populated.

---

### H-4: `SkillSystem.cs` — Inconsistent damage application pattern in ResolveSkillDamage
**File:** `SkillSystem.cs:304-305`
**Severity:** HIGH — Logic error: direct assignment vs. accumulation
**Category:** Logic error / damage race

```csharp
public void ResolveSkillDamage()
{
    foreach (var (enemyId, damage) in _skillDamageQueue)
    {
        // ...
        float newHealth = Math.Max(0f, currentHealth - damage);
        store.EnemyHealth[enemyId] = newHealth; // ← DIRECT ASSIGNMENT
        if (newHealth <= 0f)
            HandleKill(enemyId);
    }
}
```

The AGENTS.md two-phase safety rule states: *"damage queue stores raw value, not derived value"* and *"serial apply must use `EnemyHealth -= damage`"* (last-write-wins accumulation). Here, `SkillSystem.ResolveSkillDamage()` uses direct assignment (`= newHealth`) instead of accumulation (`-= damage`). This is inconsistent with `PlayerTowerAttackSystem` (line 99) and `TowerAttackSystem` (line 91) which both correctly use `EnemyHealth[enemyId] -= damage`. If multiple attack systems hit the same enemy in one frame, the direct assignment will overwrite accumulated damage from other systems.

---

## MEDIUM Bugs

### M-1: `ComponentStore.cs` — GetActiveEnemyCount() allocates a new list every call
**File:** `ComponentStore.cs:692`
**Severity:** MEDIUM — GC pressure in hot path
**Category:** Performance / allocation

```csharp
public int GetActiveEnemyCount()
{
    return ActiveEnemyIds.Count; // ← calls .ToList() each time
}
```

`ActiveEnemyIds` is defined as `ActiveEnemyIds => _activeEnemyIds.ToList()` (line 110). Every call to `GetActiveEnemyCount()` allocates a new `List<int>`. This is called from `GameManager.CheckEnemiesAtBottom()` each frame. The fix is to return `_activeEnemyIds.Count` directly.

---

### M-2: `BenchmarkSystem.cs` — RunBenchmark warm-up missing BeginFrame before Resolve
**File:** `BenchmarkSystem.cs:94`
**Severity:** MEDIUM — Potential assertion failure in debug builds
**Category:** Logic error / frame lifecycle

```csharp
for (int f = 0; f < 5; f++)
{
    int turn = f + 6;
    store.BeginFrame();
    // ...
    store.ResolveEnemiesKilledThisFrame(); // ← Resolve called
}
// Then in main loop:
for (int f = 0; f < frames; f++)
{
    int turn = f + 6;
    store.BeginFrame(); // ← BeginFrame each frame
```

The warm-up loop at line 94 calls `ResolveEnemiesKilledThisFrame()` but the **main benchmark loop** (line 112) correctly calls `BeginFrame()` before each `Resolve`. However, the microbenchmark `RunMicroBenchmark` (line 214) and `RunRealSystemChainBenchmark` (line 313) do NOT call `BeginFrame()` before their `Resolve` calls, which will trigger the assertion at `ComponentStore.cs:132` in debug builds:
> "BeginFrame() called but ResolveEnemiesKilledThisFrame() was not called for the previous frame."

---

### M-3: `SkillSystem.cs` — Ability cooldown decrement uses mutable struct pattern
**File:** `SkillSystem.cs:112-120`
**Severity:** MEDIUM — Struct copy semantics can silently drop updates
**Category:** Logic error

```csharp
for (int slot = 0; slot < count; slot++)
{
    var inst = store.GetAbility(playerId, slot); // ← copies struct
    if (inst.CurrentCooldown > 0f)
    {
        inst.CurrentCooldown = Math.Max(0f, inst.CurrentCooldown - deltaTime);
        store.SetAbility(playerId, slot, inst); // ← must write back
    }
}
```

This works correctly **only** if `store.SetAbility` is always called. If `store.SetAbility` is ever removed or refactored, the cooldown decrement will silently drop. The pattern is fragile. A safer approach would be to use `ref` returns or a dedicated `DecrementCooldown` method on `ComponentStore`.

---

### M-4: `GameConfigLoader.cs` — Off-by-one in skills JSON extraction
**File:** `GameConfigLoader.cs:289-293`
**Severity:** MEDIUM — Config parsing drops last character of skills array
**Category:** Logic error / parsing

```csharp
int diff = skillsEndBracket - skillsStartBracket;
string skillsJson = jsonContent.Substring(skillsStartBracket, diff); // ← diff is [end - start], excludes closing ]
gameConfig.Skills = ParseSkillConfigs(skillsJson);
```

The code correctly extracts from `[` but the length `diff` equals `end - start`, which means it stops **before** the closing `]`. All subsequent skill objects will be parsed without their closing delimiter. Additionally, the `ParseSkillConfigs` method expects a JSON array but `skillsJson` may not include the trailing `]`. The same bug pattern exists for `Towers` parsing at lines 308–309.

---

## LOW Bugs

### L-1: `SkillSystem.cs` — Dead code: HandleKill calls QueueEnemyDeath then logs
**File:** `SkillSystem.cs:284-289`
**Severity:** LOW — Redundant death queuing
**Category:** Code quality

```csharp
private void HandleKill(int enemyId)
{
    // Queue death for serial resolution — ResolveEnemiesKilledThisFrame() called at frame end
    store.QueueEnemyDeath(enemyId, playerId);
    renderer.Log($"[SKILL] Killed enemy {enemyId}");
}
```

`HandleKill` is called from `ResolveSkillDamage()` at line 308 **after** `EnemyHealth[enemyId]` was already set to 0. `QueueEnemyDeath` will add to `_deathQueue`, which will be processed again in `GameManager.Run()` via `store.ResolveEnemiesKilledThisFrame()`. This means `DestroyEntity` will be called **twice** for skill kills. The double-destroy is harmless (line 163 has `if (!EnemyActive[enemyId]) continue;`) but wasteful.

---

### L-2: `EnemyMovementSystem.cs` — Unused private method ParseDodgeDirection
**File:** `EnemyMovementSystem.cs:90-103`
**Severity:** LOW — Dead code
**Category:** Code quality

```csharp
private static int ParseDodgeDirection(string action)
{
    if (string.IsNullOrEmpty(action))
        return 1;
    // ... parsing logic
    return 1; // default dodge right
}
```

This method is never called. The system uses enum-based dispatch via `EnemyActionType.Dodge` with no direction parameter. The method can be removed.

---

### L-3: `MapSystem.cs` — Map rendering O(n*m*enemies) complexity
**File:** `MapSystem.cs:39-83`
**Severity:** LOW — Performance degradation with many enemies
**Category:** Performance

```csharp
for (int y = mapHeight - 1; y >= 0; y--)
{
    for (int x = 0; x < mapWidth; x++)
    {
        // ...
        foreach (int eid in activeEnemyIds)  // ← O(enemies) per cell
        {
            if (!store.EnemyActive[eid]) continue;
            int ex = (int)Math.Round(store.PositionX[eid]);
            int ey = (int)Math.Round(store.PositionY[eid]);
            if (ex == x && ey == y) { hasEnemy = true; break; }
        }
    }
}
```

Complexity is O(mapHeight × mapWidth × enemyCount). With 10K enemies, this is 200×10K = 200K iterations per frame, dominated by the innermost loop. Should build a spatial hash or use a reverse lookup (position→entityId) for O(1) cell queries.

---

### L-4: `ComponentStore.cs` — Default cooldown epsilon too large for fast attack speeds
**File:** `GameplayAbility.cs:48`
**Severity:** LOW — May cause issues with very fast cooldowns
**Category:** Logic error

```csharp
private const float EPSILON = 0.0001f;
public bool CanActivate() => CurrentCooldown <= EPSILON;
```

With `EPSILON = 0.0001f`, an ability with `Cooldown = 0.0002f` will be permanently unavailable after first use (cooldown never drops below epsilon in one update tick). While not relevant for current game balance (minimum cooldown is 5s), this is architecturally fragile.

---

### L-5: `Program.cs` — Console.ReadLine() returns null in non-interactive pipelines
**File:** `Program.cs:18`
**Severity:** LOW — NullReferenceException in piped/redirected input
**Category:** Null reference

```csharp
string input = Console.ReadLine();
if (input == "2") { ... }
```

`Console.ReadLine()` returns `null` when stdin is closed or piped without input. The null check is missing — directly comparing `null == "2"` is always false, so the code falls through to the `else` branch (Initialize/Run). This is likely intentional for the benchmark modes, but worth documenting.

---

## Notes

1. **Previously documented bugs** in `docs/bug-fix.md` referenced in `AGENTS.md` (45 fixed, 1 pending) were cross-checked against this review. The following appear to be already addressed:
   - Bug#2: Two-phase death resolution (ComponentStore lines 123–171) — correctly implemented
   - Bug#3: PlaceTower hardcoded ID — fixed with return value
   - Bug#9: SkillSystem ability reset — fixed with `ResetPlayerAbilities`
   - Bug#17: Defensive copy for GetPlayerBuffs — fixed
   - Bug#30: Magic numbers in MapSystem/EnemyMovementSystem — fixed with config
   - Bug#31: Hardcoded buff strings — fixed via config
   - Bug#35: BT cache double lookup — fixed

2. **Thread safety** is well-implemented overall: `activeIdsLock` protects `_activeEnemyIds`/`_activeTowerIds`, `entityNamesLock` protects the dictionary, and the two-phase pattern is consistently applied in `PlayerTowerAttackSystem`, `TowerAttackSystem`, and `SkillSystem`.

3. **No new race conditions** were identified in the parallel code paths — the Parallel.For blocks in all systems are read-only during the parallel phase.
