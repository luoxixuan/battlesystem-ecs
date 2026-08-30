using BattleSystemECS.Components;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class DamageTypeContractTests
    {
        [Fact]
        public void ValuesPreserveMaskAndSpecialBranches()
        {
            Assert.Equal(32, (int)DamageType.True);
            Assert.Equal(64, (int)DamageType.Holy);
            Assert.Equal(0, ((int)DamageType.True & (int)DamageImmunityFlags.Fire));
            Assert.Equal((int)DamageImmunityFlags.Fire, ((int)DamageType.Fire & (int)DamageImmunityFlags.Fire));
        }
    }
}
