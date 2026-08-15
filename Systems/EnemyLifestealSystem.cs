using System;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Enemy Lifesteal System — enemies with lifesteal heal a fraction of damage dealt back to themselves.
    ///
    /// NOTE (placeholder): enemy lifesteal is currently applied inline in
    /// <see cref="EnemyAISystem"/>'s serial phase via its own ping-pong
    /// <c>_lifestealEvents</c> queue (see EnemyAISystem.Update). This system is
    /// retained as the AIGroup slot for a future aura-based passive lifesteal
    /// pass and is a deliberate no-op — it is not instantiated in the current
    /// SystemRegistry wiring (BenchmarkSystem also sets it to null).
    /// </summary>
    public class EnemyLifestealSystem
    {
        public EnemyLifestealSystem(ComponentStore store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
        }

        public void SetTurn(int turn)
        {
            // Nothing per-turn to cache
        }

        /// <summary>Placeholder no-op pass. See the class-level note.</summary>
        public void Update()
        {
        }
    }
}
