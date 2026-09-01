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
    ///   - BossTypeName is resolved from Store.EnemyTypeName[] during the serial drain
    ///   - Empty/missing BossTypeName is normalized to null in the payload
    ///   - Multiple events are delivered in order via TryTake (drain empties the bag)
    ///   - Subscribers receive the event with the same payload object identity
    ///   - No exception on empty bag (defensive — production code can call drain
    ///     multiple times per frame without ill effects)
    /// </summary>
    public class BossPhaseEventTests : BattleTestBase
    {
        private const int PlayerId = 0;
        private const float DeltaTime = 1f / 60f;

        /// <summary>文件内共享构造：基于基类 Store/Config 创建 EnemyAISystem（含 EnemyAbilitySystem）。</summary>
        private EnemyAISystem CreateAi(EventBus? bus = null)
        {
            var ability = new EnemyAbilitySystem(Store, Renderer, PlayerId, Config);
            return new EnemyAISystem(Store, Renderer, PlayerId, Config, ability, eventBus: bus);
        }

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
            var ai = CreateAi();
            Assert.Equal(0, ai.PhaseChangeDrainCount);
            Assert.Equal(0, ai.PhaseChangePublishCount);
        }

        [Fact]
        public void EnemyAISystem_PhaseChangeBag_Empty_NoException()
        {
            var ai = CreateAi();
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
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Damage = 5f; e.Name = "Dragon"; });
            var bus = new EventBus();
            var ai = CreateAi(bus);

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
            // The drain must fill BossTypeName from Store.EnemyTypeName[] BEFORE
            // publishing — subscribers expect a non-null name when the boss has one.
            // AddEnemy truncates the type name at the first 'L' (legacy naming
            // convention from the AddEnemy implementation — see ComponentStore_Enemy.cs
            // line ~1143). So we pick a name that survives the truncation intact.
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Damage = 5f; e.Name = "Dragon"; });
            var bus = new EventBus();
            var ai = CreateAi(bus);

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
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Damage = 5f; e.Name = ""; }); // empty name
            var bus = new EventBus();
            var ai = CreateAi(bus);

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
            // Bug 回归：多个 phase 事件必须按收集顺序稳定发布。
            int boss1 = Enemy(e => { e.MoveSpeed = 1f; e.Damage = 5f; e.Name = "Boss1"; });
            int boss2 = Enemy(e => { e.MoveSpeed = 2f; e.MaxHealth = 200f; e.Health = 200f; e.Damage = 5f; e.Name = "Boss2"; });
            int boss3 = Enemy(e => { e.MoveSpeed = 3f; e.MaxHealth = 300f; e.Health = 300f; e.Damage = 5f; e.Name = "Boss3"; });
            var bus = new EventBus();
            var ai = CreateAi(bus);

            var ids = new List<int>();
            bus.BossPhaseChanged.Subscribe(ev => ids.Add(ev.EnemyId));

            InjectPhaseChangeEvent(ai, new BossPhaseChangedEvent { EnemyId = boss1, NewPhase = 1, Turn = 1 });
            InjectPhaseChangeEvent(ai, new BossPhaseChangedEvent { EnemyId = boss2, NewPhase = 1, Turn = 1 });
            InjectPhaseChangeEvent(ai, new BossPhaseChangedEvent { EnemyId = boss3, NewPhase = 2, Turn = 1 });

            InvokeDrainPhaseChangeEvents(ai);

            Assert.Equal(3, ai.PhaseChangeDrainCount);
            Assert.Equal(3, ai.PhaseChangePublishCount);
            Assert.Equal(3, ids.Count);
            Assert.Equal(new[]{boss1,boss2,boss3},ids);
        }

        [Fact]
        public void DrainPhaseChangeEvents_SecondDrainOnEmptyBag_NoOp()
        {
            // Calling drain twice in a row on the same bag must not re-publish
            // events (the first drain emptied the bag). Verifies no accumulation.
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Damage = 5f; e.Name = "Boss"; });
            var bus = new EventBus();
            var ai = CreateAi(bus);

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
            ai.EnqueuePhaseChangeForDiagnostics(ev);
        }

        private static void InvokeDrainPhaseChangeEvents(EnemyAISystem ai)
        {
            // DrainPhaseChangeEvents is a private method. We invoke it via reflection
            // to test the serial-drain contract without running the full Update()
            // (which would require a full GameConfig + WaveSpawningSystem setup).
            ai.DrainPhaseChangeEvents();
        }
    }
}
