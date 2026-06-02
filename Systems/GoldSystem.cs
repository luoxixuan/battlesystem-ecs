using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 金币系统 - 负责管理金币获取和花费
    /// 金币奖励逻辑已迁移到 PlayerTowerAttackSystem 和 TowerAttackSystem
    /// 科技树击杀金币倍率通过 GoldKillMultiplier 同步到 ComponentStore
    /// </summary>
    public class GoldSystem
    {
        private ComponentStore store;
        private IRenderer renderer;
        private TechTreeSystem techTreeSystem;
        private readonly bool hasTechTreeSystem;

        /// <summary>
        /// Full constructor with TechTreeSystem — enables gold-on-kill multiplier sync.
        /// </summary>
        public GoldSystem(ComponentStore store, IRenderer renderer, TechTreeSystem techTreeSystem)
        {
            this.store = store;
            this.renderer = renderer;
            this.techTreeSystem = techTreeSystem;
            this.hasTechTreeSystem = true;
        }

        /// <summary>
        /// Backwards-compatible constructor without TechTreeSystem.
        /// Defaults multiplier to 1.0 (no bonus).
        /// </summary>
        public GoldSystem(ComponentStore store, IRenderer renderer)
        {
            this.store = store;
            this.renderer = renderer;
            this.hasTechTreeSystem = false;
        }

        /// <summary>
        /// Wire Breather-wave bonus: subscribe to WaveSpawningSystem.OnBreatherWaveComplete.
        /// When a Breather-rhythm wave completes, this method applies:
        ///   1) Gold x2 on the per-wave bonus portion (extra gold from Breather rhythm)
        ///   2) Player HP heal by PlayerHealOnBreatherWave[playerId] * maxHp
        ///   3) Cooldown reduction by PlayerCooldownReduceOnBreather[playerId] seconds on every global skill
        /// Call this AFTER both GoldSystem and WaveSpawningSystem are constructed.
        /// </summary>
        public void SubscribeToBreatherWave(WaveSpawningSystem waveSpawning)
        {
            if (waveSpawning == null) return;
            waveSpawning.OnBreatherWaveComplete += HandleBreatherWaveComplete;
        }

        /// <summary>
        /// Apply the three Breather-wave bonuses. Idempotent against missing arrays.
        /// </summary>
        private void HandleBreatherWaveComplete(int waveNumber)
        {
            if (store == null) return;
            // Use array length (not a hardcoded constant) so this stays in sync if MAX_PLAYERS ever changes.
            int maxPlayers = store.PlayerHealOnBreatherWave.Length;
            for (int pid = 0; pid < maxPlayers; pid++)
            {
                // ── 1) Heal player by % of max HP ─────────────────────────
                float healPct = store.PlayerHealOnBreatherWave[pid];
                if (healPct > 0f)
                {
                    float maxHp = store.GetPlayerMaxHealth(pid);
                    if (maxHp > 0f)
                    {
                        float newHp = store.GetPlayerCurrentHealth(pid) + healPct * maxHp;
                        // Clamp at maxHp so we never overheal; floor at 0 for safety.
                        newHp = Math.Max(0f, Math.Min(newHp, maxHp));
                        store.SetPlayerCurrentHealth(pid, newHp);
                        renderer.Log($"[GOLD][BREATHER] Wave {waveNumber}: player {pid} healed +{healPct:P0} ({healPct * maxHp:F1} HP)");
                    }
                }

                // ── 2) Reduce all global skill cooldowns by N seconds ────
                float cdrSec = store.PlayerCooldownReduceOnBreather[pid];
                if (cdrSec > 0f)
                {
                    // PlayerGlobalSkillCooldown is flat 1D: pid * 8 + skillIdx
                    const int SKILL_SLOTS = 8;
                    int baseIdx = pid * SKILL_SLOTS;
                    for (int s = 0; s < SKILL_SLOTS; s++)
                    {
                        int idx = baseIdx + s;
                        float cd = store.PlayerGlobalSkillCooldown[idx];
                        if (cd > 0f)
                        {
                            store.PlayerGlobalSkillCooldown[idx] = Math.Max(0f, cd - cdrSec);
                        }
                    }
                    renderer.Log($"[GOLD][BREATHER] Wave {waveNumber}: player {pid} CDR -{cdrSec:F1}s on all skills");
                }

                // ── 3) Gold x2 on the Breather bonus portion ──────────────
                // We use PlayerBreatherGoldBonus[pid] (default 0) as the base amount, then award 2x that.
                // This avoids depending on PlayerWaveCompleteGold (which is dead code in the codebase)
                // and lets config or future callers tune the bonus amount independently of per-wave gold.
                float breatherGold = store.PlayerBreatherGoldBonus[pid];
                if (breatherGold > 0f)
                {
                    float doubleBonus = breatherGold * 2f;
                    store.SetPlayerGold(pid, store.GetPlayerGold(pid) + doubleBonus);
                    renderer.Log($"[GOLD][BREATHER] Wave {waveNumber}: player {pid} gold x2 (+{doubleBonus:F1} = {breatherGold:F1} × 2)");
                }
            }
        }

        public void SetTurn(int turn)
        {
            // Gold rewards for kills are handled by PlayerTowerAttackSystem and TowerAttackSystem.
        }

        public void Update()
        {
            // Sync tech tree gold multipliers to ComponentStore every frame
            if (hasTechTreeSystem)
            {
                store.GoldKillMultiplier = techTreeSystem.GetGoldOnKillMult();
                store.AllIncomeMultKill = techTreeSystem.GetAllIncomeMult();
                store.GoldOnEliteKill = techTreeSystem.GetGoldOnEliteKill();
            }
            else
            {
                store.GoldKillMultiplier = 1.0f;
                store.AllIncomeMultKill = 1.0f;
                store.GoldOnEliteKill = 0f;
            }
        }

        /// <summary>
        /// Award gold bonus when a wave completes, applying tech tree wave bonus and all_income_mult.
        /// </summary>
        public void AwardGoldForWave(float baseGold, int playerId)
        {
            if (playerId < 0 || playerId >= 10) return;
            float bonus = 0f;
            if (hasTechTreeSystem)
            {
                bonus = Math.Max(0f, techTreeSystem.GetGoldOnWaveBonus());
            }
            float subtotal = baseGold + bonus;
            // Apply all_income_mult multiplier (layered on top of gold_on_wave_bonus)
            float mult = hasTechTreeSystem ? techTreeSystem.GetAllIncomeMult() : 1.0f;
            float totalGold = subtotal * mult;
            if (totalGold > 0f)
            {
                float currentGold = store.GetPlayerGold(playerId);
                store.SetPlayerGold(playerId, currentGold + totalGold);
                store.PlayerWaveCompleteGold[playerId] = totalGold;
                renderer.Log($"[GOLD] Wave complete: +{totalGold} gold (base {baseGold}, bonus {bonus}, mult {mult:F2})");
            }
        }

        public bool SpendGold(float amount)
        {
            float currentGold = store.GetPlayerGold(store.PlayerEntityId);
            if (currentGold < amount)
            {
                renderer.Log($"[GOLD] 金币不足，需要 {amount}，当前只有 {currentGold}");
                return false;
            }
            store.SetPlayerGold(store.PlayerEntityId, currentGold - amount);
            renderer.Log($"[GOLD] 花费 {amount} 金币");
            return true;
        }
    }
}
