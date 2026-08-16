using BattleSystemECS.Tests.Infrastructure;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using Xunit;

namespace BattleSystemECS.Tests.Features.Bosses
{
    /// <summary>
    /// Tests for Round 129 Direction 2: Boss Phase Changed Event.
    /// Verifies that:
    ///   - EventBus.BossPhaseChanged typed channel exists (replaces the string constant)
    ///   - BossPhaseChangedEvent DTO supports all 6 fields (EnemyId / BossTypeName /
    ///     OldPhase / NewPhase / HealthFraction / Turn)
    ///   - EnemyAISystem.PhaseChangeDrainCount / PhaseChangePublishCount default to 0
    ///   - DrainPhaseChangeEvents publishes each event to the EventBus
    ///   - BossTypeName is resolved from store.EnemyTypeName[] during the serial drain
    ///   - Empty/missing BossTypeName is normalized to null in the payload
    ///   - Multiple events are delivered in order via TryTake (drain empties the bag)
    ///   - Subscribers receive the event with the same payload object identity
    ///   - No exception on empty bag (defensive — production code can call drain
    ///     multiple times per frame without ill effects)
    /// </summary>
    public class BossPhaseEventTests
    {
        private const int PlayerId = 0;
        private const float DeltaTime = 1f / 60f;

        // ── GameEvents constant & DTO ─────────────────────────────────────

        [Fact]
        public void EventBus_BossPhaseChanged_ChannelExists()
        {
            // The BossPhaseChanged channel is a compile-time-typed field on EventBus
            // (it replaces the previous string-keyed GameEvents constant). A missing
            // field is now a compile error rather than a runtime string typo.
            var bus = new EventBus();
            Assert.NotNull(bus.BossPhaseChanged);
        }

        [Fact]
        public void BossPhaseChangedEvent_AllFieldsAssignable()
        {
            // DTO sanity: all 6 fields can be set and read back. Subscribers will
            // cast `object` payload to BossPhaseChangedEvent — these fields are
            // the public contract.
            var ev = new BossPhaseChangedEvent
            {
                EnemyId = 42,
                BossTypeName = "Dragon",
                OldPhase = 0,
                NewPhase = 1,
                HealthFraction = 0.45f,
                Turn = 17,
            };
            Assert.Equal(42, ev.EnemyId);
            Assert.Equal("Dragon", ev.BossTypeName);
            Assert.Equal(0, ev.OldPhase);
            Assert.Equal(1, ev.NewPhase);
            Assert.Equal(0.45f, ev.HealthFraction);
            Assert.Equal(17, ev.Turn);
        }

        [Fact]
        public void BossPhaseChangedEvent_DefaultValues_AreSafe()
        {
            // Default-constructed DTO must not throw when published (subscribers
            // expect null BossTypeName is OK, zero int fields are OK).
            var ev = new BossPhaseChangedEvent();
            Assert.Equal(0, ev.EnemyId);
            Assert.Null(ev.BossTypeName);
            Assert.Equal(0, ev.OldPhase);
            Assert.Equal(0, ev.NewPhase);
            Assert.Equal(0f, ev.HealthFraction);
            Assert.Equal(0, ev.Turn);
        }

        // ── EnemyAISystem drain counter defaults ──────────────────────────

        [Fact]
        public void EnemyAISystem_PhaseChangeDrainCount_DefaultsToZero()
        {
            var store = new ComponentStore();
            var config = new GameConfig();
            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility);
            Assert.Equal(0, ai.PhaseChangeDrainCount);
            Assert.Equal(0, ai.PhaseChangePublishCount);
        }

        [Fact]
        public void EnemyAISystem_PhaseChangeBag_Empty_NoException()
        {
            // Drain with no events pushed must not throw. The serial-drain helper
            // uses TryTake in a tight loop; empty bag exits immediately.
            var store = new ComponentStore();
            var config = new GameConfig();
            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility);
            InvokeDrainPhaseChangeEvents(ai);
            Assert.Equal(0, ai.PhaseChangeDrainCount);
            Assert.Equal(0, ai.PhaseChangePublishCount);
        }

        // ── Drain publishes to the EventBus ──────────────────────────────

        [Fact]
        public void DrainPhaseChangeEvents_PublishesToSubscribedBus()
        {
            // Build a bus, subscribe a handler, push 1 event into the bag, invoke
            // drain → handler must receive exactly 1 event with matching payload.
            var store = new ComponentStore();
            int eid = store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "Dragon");
            var config = new GameConfig();
            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var bus = new EventBus();
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility, eventBus: bus);

            var received = new List<BossPhaseChangedEvent>();
            bus.BossPhaseChanged.Subscribe(received.Add);

            // Inject one event into the bag via reflection (production code pushes
            // from the AI loop; tests can't easily simulate a real HP drop).
            var ev = new BossPhaseChangedEvent
            {
                EnemyId = eid,
                OldPhase = 0,
                NewPhase = 1,
                HealthFraction = 0.42f,
                Turn = 7,
            };
            InjectPhaseChangeEvent(ai, ev);

            InvokeDrainPhaseChangeEvents(ai);

            Assert.Equal(1, ai.PhaseChangeDrainCount);
            Assert.Equal(1, ai.PhaseChangePublishCount);
            Assert.Single(received);
            var published = received[0];
            Assert.Equal(eid, published.EnemyId);
            Assert.Equal(0, published.OldPhase);
            Assert.Equal(1, published.NewPhase);
            Assert.Equal(0.42f, published.HealthFraction);
            Assert.Equal(7, published.Turn);
        }

        [Fact]
        public void DrainPhaseChangeEvents_ResolvesBossTypeName()
        {
            // The drain must fill BossTypeName from store.EnemyTypeName[] BEFORE
            // publishing — subscribers expect a non-null name when the boss has one.
            var store = new ComponentStore();
            // AddEnemy truncates the type name at the first 'L' (legacy naming
            // convention from the AddEnemy implementation — see ComponentStore_Enemy.cs
            // line ~1143). So we pick a name that survives the truncation intact.
            int eid = store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "Dragon");
            var config = new GameConfig();
            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var bus = new EventBus();
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility, eventBus: bus);

            BossPhaseChangedEvent? captured = null;
            bus.BossPhaseChanged.Subscribe(ev => captured = ev);

            InjectPhaseChangeEvent(ai, new BossPhaseChangedEvent
            {
                EnemyId = eid,
                OldPhase = 0,
                NewPhase = 2,
                HealthFraction = 0.30f,
                Turn = 11,
            });
            InvokeDrainPhaseChangeEvents(ai);

            Assert.NotNull(captured);
            Assert.Equal("Dragon", captured.BossTypeName);
        }

        [Fact]
        public void DrainPhaseChangeEvents_EmptyBossTypeName_NormalizedToNull()
        {
            // If the boss has no type name (test fixture edge case), the payload's
            // BossTypeName must be null, not empty string. Subscribers may do
            // `name ?? "Unknown"` and rely on the null sentinel.
            var store = new ComponentStore();
            int eid = store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, ""); // empty name
            var config = new GameConfig();
            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var bus = new EventBus();
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility, eventBus: bus);

            BossPhaseChangedEvent? captured = null;
            bus.BossPhaseChanged.Subscribe(ev => captured = ev);

            InjectPhaseChangeEvent(ai, new BossPhaseChangedEvent
            {
                EnemyId = eid,
                OldPhase = 0,
                NewPhase = 1,
            });
            InvokeDrainPhaseChangeEvents(ai);

            Assert.NotNull(captured);
            Assert.Null(captured.BossTypeName);
        }

        [Fact]
        public void DrainPhaseChangeEvents_MultipleEvents_AllDelivered()
        {
            // Push 3 events → drain should publish all 3 in a single call. TryTake
            // is non-deterministic on order (ConcurrentBag), so we assert SET membership
            // rather than order — the contract is "all events delivered", not FIFO.
            var store = new ComponentStore();
            int boss1 = store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "Boss1");
            int boss2 = store.AddEnemy(0, 0, 2f, 200f, 200f, 5f, 10, 1, "Boss2");
            int boss3 = store.AddEnemy(0, 0, 3f, 300f, 300f, 5f, 10, 1, "Boss3");
            var config = new GameConfig();
            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var bus = new EventBus();
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility, eventBus: bus);

            var ids = new List<int>();
            bus.BossPhaseChanged.Subscribe(ev => ids.Add(ev.EnemyId));

            InjectPhaseChangeEvent(ai, new BossPhaseChangedEvent { EnemyId = boss1, NewPhase = 1, Turn = 1 });
            InjectPhaseChangeEvent(ai, new BossPhaseChangedEvent { EnemyId = boss2, NewPhase = 1, Turn = 1 });
            InjectPhaseChangeEvent(ai, new BossPhaseChangedEvent { EnemyId = boss3, NewPhase = 2, Turn = 1 });

            InvokeDrainPhaseChangeEvents(ai);

            Assert.Equal(3, ai.PhaseChangeDrainCount);
            Assert.Equal(3, ai.PhaseChangePublishCount);
            Assert.Equal(3, ids.Count);
            Assert.Contains(boss1, ids);
            Assert.Contains(boss2, ids);
            Assert.Contains(boss3, ids);
        }

        [Fact]
        public void DrainPhaseChangeEvents_SecondDrainOnEmptyBag_NoOp()
        {
            // Calling drain twice in a row on the same bag must not re-publish
            // events (the first drain emptied the bag). Verifies no accumulation.
            var store = new ComponentStore();
            int eid = store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "Boss");
            var config = new GameConfig();
            var renderer = new MockRenderer();
            var enemyAbility = new EnemyAbilitySystem(store, renderer, PlayerId, config);
            var bus = new EventBus();
            var ai = new EnemyAISystem(store, renderer, PlayerId, config, enemyAbility, eventBus: bus);

            int receivedCount = 0;
            bus.BossPhaseChanged.Subscribe(_ => receivedCount++);

            InjectPhaseChangeEvent(ai, new BossPhaseChangedEvent { EnemyId = eid, NewPhase = 1 });
            InvokeDrainPhaseChangeEvents(ai);
            Assert.Equal(1, receivedCount);

            // Second drain must be a no-op (bag is empty).
            InvokeDrainPhaseChangeEvents(ai);
            Assert.Equal(1, receivedCount); // unchanged
        }

        [Fact]
        public void GameEvents_SubscribeAndPublish_RoundTrip()
        {
            // Standalone EventBus round-trip: subscribe → publish → handler invoked.
            // Mirrors the wiring pattern any subscriber (music, telemetry) will use.
            var bus = new EventBus();
            BossPhaseChangedEvent? captured = null;
            int callCount = 0;
            bus.BossPhaseChanged.Subscribe(data =>
            {
                captured = data;
                callCount++;
            });

            var ev = new BossPhaseChangedEvent
            {
                EnemyId = 99,
                BossTypeName = "TestBoss",
                OldPhase = 1,
                NewPhase = 2,
                HealthFraction = 0.25f,
                Turn = 42,
            };
            bus.BossPhaseChanged.Publish(ev);

            Assert.Equal(1, callCount);
            Assert.Same(ev, captured); // same object identity — Publish doesn't copy
        }

        [Fact]
        public void GameEvents_NoSubscribers_PublishNoThrow()
        {
            // Publishing with no subscribers must be a silent no-op (the EventBus
            // does `if (!_handlers.TryGetValue(...)) return;`). This is the
            // production path before any subscriber registers.
            var bus = new EventBus();
            var ex = Record.Exception(() => bus.BossPhaseChanged.Publish(
                new BossPhaseChangedEvent { EnemyId = 1, NewPhase = 1 }));
            Assert.Null(ex);
        }

        // ── Reflection helpers (test-only — no production API needed) ──────

        private static void InjectPhaseChangeEvent(EnemyAISystem ai, BossPhaseChangedEvent ev)
        {
            // _phaseChangeEvents is a private readonly ConcurrentBag<BossPhaseChangedEvent>.
            // We grab it via reflection and Add a single event. In production this
            // happens from EnemyAISystem.Update()'s parallel/sequential paths; for
            // unit tests we bypass the AI loop and test the drain contract directly.
            var field = typeof(EnemyAISystem).GetField(
                "_phaseChangeEvents",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field); // field must exist
            var bag = (ConcurrentBag<BossPhaseChangedEvent>)field.GetValue(ai)!;
            bag.Add(ev);
        }

        private static void InvokeDrainPhaseChangeEvents(EnemyAISystem ai)
        {
            // DrainPhaseChangeEvents is a private method. We invoke it via reflection
            // to test the serial-drain contract without running the full Update()
            // (which would require a full GameConfig + WaveSpawningSystem setup).
            var method = typeof(EnemyAISystem).GetMethod(
                "DrainPhaseChangeEvents",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method); // method must exist
            method.Invoke(ai, null);
        }
    }
}