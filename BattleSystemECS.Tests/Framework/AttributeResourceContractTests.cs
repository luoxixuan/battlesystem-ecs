using System;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Config;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class AttributeResourceContractTests
    {
        private static readonly AttributeKey Damage = new AttributeKey(8);
        private static ModifierDefinition Mod(AttributeModifierOp op, float value, int priority = 0, SnapshotPolicy snapshot = SnapshotPolicy.ReevaluateOnRead)
            => new ModifierDefinition(Damage, op, value, priority, snapshot: snapshot);

        [Fact] public void Aggregator_OrdersOperationsByPriorityAndSequence()
        { var a = new AttributeAggregator(); a.SetBase(1, Damage, 10f); a.AddModifier(1, Mod(AttributeModifierOp.Add, 2f, 0)); a.AddModifier(1, Mod(AttributeModifierOp.Multiply, 3f, 1)); Assert.Equal(36f, a.GetComputed(1, Damage)); }
        [Fact] public void Aggregator_OverrideReplacesAggregationStart()
        { var a = new AttributeAggregator(); a.SetBase(1, Damage, 10f); a.AddModifier(1, Mod(AttributeModifierOp.Override, 4f)); a.AddModifier(1, Mod(AttributeModifierOp.Add, 2f, 1)); Assert.Equal(6f, a.GetComputed(1, Damage)); }
        [Fact] public void Aggregator_UsesOverrideThenAllAddsThenAllMultipliers()
        { var a = new AttributeAggregator(); a.SetBase(1, Damage, 10f); a.AddModifier(1, Mod(AttributeModifierOp.Add, 2f, 10)); a.AddModifier(1, Mod(AttributeModifierOp.Override, 4f, 0)); a.AddModifier(1, Mod(AttributeModifierOp.Multiply, 3f, 0)); Assert.Equal(18f, a.GetComputed(1, Damage)); }
        [Fact] public void Aggregator_TiePriorityUsesApplicationSequence()
        { var a = new AttributeAggregator(); a.SetBase(1, Damage, 10f); a.AddModifier(1, Mod(AttributeModifierOp.Override, 2f)); a.AddModifier(1, Mod(AttributeModifierOp.Override, 7f)); Assert.Equal(7f, a.GetComputed(1, Damage)); }
        [Fact] public void Aggregator_CaptureSnapshotSurvivesRefreshAndBaseChange()
        { var a = new AttributeAggregator(); a.SetBase(1, Damage, 10f); var h = a.AddModifier(1, Mod(AttributeModifierOp.Add, 2f, snapshot: SnapshotPolicy.CaptureOnApply), 5f); a.SetBase(1, Damage, 20f); Assert.True(a.RefreshModifier(1, h, 9f)); Assert.Equal(25f, a.GetComputed(1, Damage)); }
        [Fact] public void Aggregator_ReevaluateSnapshotRefreshesMagnitude()
        { var a = new AttributeAggregator(); a.SetBase(1, Damage, 10f); var h = a.AddModifier(1, Mod(AttributeModifierOp.Add, 2f)); Assert.True(a.RefreshModifier(1, h, 9f)); Assert.Equal(19f, a.GetComputed(1, Damage)); }
        [Fact] public void Aggregator_RemoveRecomputesFromBaseWithoutInverseMath()
        { var a = new AttributeAggregator(); a.SetBase(1, Damage, 1f); var h = a.AddModifier(1, Mod(AttributeModifierOp.Multiply, 1.1f)); Assert.Equal(1.1f, a.GetComputed(1, Damage), 5); Assert.True(a.RemoveModifier(1, h)); Assert.Equal(1f, a.GetComputed(1, Damage)); }
        [Fact] public void Aggregator_DirtyIsConsumedAtExplicitBoundary()
        { var a = new AttributeAggregator(); a.SetBase(1, Damage, 4f); Assert.True(a.DirtyCount > 0); a.AggregateDirty(); Assert.Equal(0, a.DirtyCount); Assert.Equal(4f, a.GetComputed(1, Damage)); }
        [Fact] public void Aggregator_RejectsModifierOnResource()
        { var a = new AttributeAggregator(); Assert.Throws<InvalidOperationException>(() => a.AddModifier(1, new ModifierDefinition(new AttributeKey(3), AttributeModifierOp.Add, 1f))); }
        [Fact] public void Schema_ResourceAttributesRejectModifiers()
        {
            foreach (var key in new[] { 2, 3, 4, 7, 9 })
                Assert.False(AttributeSchema.Default.Get(new AttributeKey(key)).AllowsModifiers);
            Assert.Equal(AttributeDomain.Resource, AttributeSchema.Default.Get(new AttributeKey(9)).Domain);
        }
        [Fact] public void ResourceResolver_ClampsHealthAndMaxHealthRules()
        { using (var s = new ComponentStore()) { s.PlayerMaxHealth[0] = 100f; s.PlayerCurrentHealth[0] = 40f; Assert.Equal(60f, s.ResourceResolver.Heal(0, 100f)); s.ResourceResolver.SetMaxHealth(0, 25f); Assert.Equal(25f, s.PlayerCurrentHealth[0]); s.ResourceResolver.SetMaxHealth(0, 80f); Assert.Equal(25f, s.PlayerCurrentHealth[0]); } }
        [Fact] public void ResourceResolver_ClampsShieldManaGoldAndRejectsNonFinite()
        { using (var s = new ComponentStore()) { s.PlayerMaxMana[0] = 10f; Assert.Equal(10f, s.ResourceResolver.ApplyMana(0, 20f)); Assert.Equal(-10f, s.ResourceResolver.ApplyMana(0, -20f)); Assert.Equal(0f, s.ResourceResolver.ApplyShield(0, float.NaN)); Assert.Equal(0f, s.ResourceResolver.ApplyGold(0, float.PositiveInfinity)); } }
        [Fact] public void ResourceResolver_UnknownKeyReturnsDiagnostic()
        { using (var s = new ComponentStore()) { var result = s.ResourceResolver.TryApply(new ResourceRequest(default(EntityHandle), new EntityHandle(0, 1), new AttributeKey(999), 1f, 1)); Assert.False(result.Accepted); Assert.Equal(ResourceRejectionReason.UnknownResource, result.Reason); } }
        [Fact] public void DestroyEntity_ClearsComputedModifierState()
        { using (var s = new ComponentStore()) { int id = s.CreateEntity(); s.AttributeAggregator.SetBase(id, Damage, 5f); var h = s.AddAttributeModifier(id, Mod(AttributeModifierOp.Add, 3f)); Assert.Equal(8f, s.AttributeAggregator.GetComputed(id, Damage)); s.DestroyEntity(id); Assert.Equal(0, s.AttributeAggregator.DirtyCount); Assert.False(s.RemoveAttributeModifier(id, h)); } }

        [Fact]
        public void ComputedMode_OnlyChangesAtFrameBoundary()
        {
            using (var s = new ComponentStore())
            {
                s.AddPlayer(0, 5f, 1f, 10f, 1);
                s.SetPlayerAttackDamage(0, 10f);
                s.AddAttributeModifier(0, new ModifierDefinition(new AttributeKey(0), AttributeModifierOp.Multiply, 2f));
                s.UseComputedAttributes = true;
                Assert.Equal(10f, s.GetPlayerAttackDamageProjection(0));
                var scheduler = new FrameScheduler(s, new GameConfig());
                scheduler.Phase = GameState.BuildPhase;
                scheduler.Tick(0f, 0);
                Assert.Equal(20f, s.GetPlayerAttackDamageProjection(0));
                s.UseComputedAttributes = false;
                Assert.Equal(20f, s.GetPlayerAttackDamageProjection(0));
                scheduler.Tick(0f, 1);
                Assert.Equal(10f, s.GetPlayerAttackDamageProjection(0));
                s.UseComputedAttributes = true;
                scheduler.Tick(0f, 2);
                Assert.Equal(20f, s.GetPlayerAttackDamageProjection(0));
            }
        }

        [Fact]
        public void ResourceResolver_RejectsStaleEntityHandle()
        {
            using (var s = new ComponentStore())
            {
                int id = s.AddEnemy(1f, 1f, 1f, 10f, 10f, 1f, 0, 1);
                var stale = s.GetEntityHandle(id);
                s.DestroyEntity(id);
                int recycled = s.AddEnemy(1f, 1f, 1f, 10f, 10f, 1f, 0, 1);
                Assert.Equal(id, recycled);
                var result = s.ResourceResolver.TryApply(new ResourceRequest(default(EntityHandle), stale, new AttributeKey(4), 1f, 1));
                Assert.False(result.Accepted);
                Assert.Equal(ResourceRejectionReason.InvalidTarget, result.Reason);
            }
        }
    }
}
