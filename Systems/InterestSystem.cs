using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Bank / Interest System — pays interest on banked gold at the end of each wave.
    /// 
    /// Design:
    /// - Gold stored in the bank (PlayerBankedGold) earns interest each wave.
    /// - Interest rate is capped at InterestRateCap to prevent exploits.
    /// - Bank gold cap prevents infinite gold accumulation.
    /// - Player can deposit/withdraw via GoldSystem (not yet wired — this system
    ///   currently auto-deposits all available gold above a threshold each wave).
    /// - InterestSystem.Update() is called during BuildPhase to pay interest on the
    ///   banked gold from the previous wave.
    /// </summary>
    public class InterestSystem
    {
        private ComponentStore store;
        private IRenderer renderer;
        private GameConfig gameConfig;
        private readonly int playerId;

        // Minimum bank gold to trigger auto-deposit (deposit spare gold into bank)
        private const float MIN_GOLD_THRESHOLD = 100f;

        public InterestSystem(ComponentStore store, IRenderer renderer, GameConfig gameConfig, int playerId)
        {
            this.store = store;
            this.renderer = renderer;
            this.gameConfig = gameConfig;
            this.playerId = playerId;
        }

        /// <summary>
        /// Called when a wave completes — pays interest on banked gold and auto-deposits spare gold.
        /// </summary>
        public void OnWaveComplete()
        {
            PayInterest();

            // Auto-deposit spare gold into the bank
            AutoDepositSpareGold();
        }

        /// <summary>
        /// Pay interest on banked gold. Interest rate is capped at InterestRateCap.
        /// </summary>
        private void PayInterest()
        {
            float banked = store.PlayerBankedGold[playerId];
            if (banked <= 0f) return;

            float rate = store.PlayerInterestRate[playerId];
            float cap = gameConfig.Bank.InterestRateCap;
            rate = Math.Min(rate, cap);

            float interest = banked * rate;
            if (interest <= 0f) return;

            // Apply bank gold cap
            float newBanked = Math.Min(banked + interest, gameConfig.Bank.BankGoldCap);
            float actualInterest = newBanked - banked;

            store.PlayerBankedGold[playerId] = newBanked;

            // Also credit the interest to available gold
            float currentGold = store.GetPlayerGold(playerId);
            store.SetPlayerGold(playerId, currentGold + actualInterest);

            renderer.Log($"[BANK] Interest paid: +{actualInterest:F1} gold ({rate:P1} of {banked:F1} banked). Bank total: {newBanked:F1}");
        }

        /// <summary>
        /// Auto-deposit spare available gold into the bank up to BankGoldCap.
        /// Called each wave to sweep excess gold into the bank automatically.
        /// </summary>
        private void AutoDepositSpareGold()
        {
            float available = store.GetPlayerGold(playerId);
            float banked = store.PlayerBankedGold[playerId];
            float cap = gameConfig.Bank.BankGoldCap;

            // Only deposit if we have spare gold above threshold and bank is not full
            if (available <= MIN_GOLD_THRESHOLD) return;
            if (banked >= cap) return;

            float spare = available - MIN_GOLD_THRESHOLD;
            float depositAmount = Math.Min(spare, cap - banked);
            if (depositAmount <= 0f) return;

            store.SetPlayerGold(playerId, available - depositAmount);
            store.PlayerBankedGold[playerId] = banked + depositAmount;

            renderer.Log($"[BANK] Auto-deposited: {depositAmount:F1} gold. Bank: {banked + depositAmount:F1} / {cap:F1}");
        }

        /// <summary>
        /// Called during BuildPhase to update interest system state.
        /// Currently a no-op since interest is paid via OnWaveComplete.
        /// </summary>
        public void Update()
        {
            // Interest is event-driven (paid on wave complete), no per-frame work needed.
        }

        /// <summary>
        /// Manual deposit — move gold from available balance into the bank.
        /// </summary>
        public bool Deposit(float amount)
        {
            if (amount <= 0f) return false;
            float available = store.GetPlayerGold(playerId);
            if (available < amount) return false;

            float banked = store.PlayerBankedGold[playerId];
            float cap = gameConfig.Bank.BankGoldCap;
            float actualDeposit = Math.Min(amount, cap - banked);
            if (actualDeposit <= 0f) return false;

            store.SetPlayerGold(playerId, available - actualDeposit);
            store.PlayerBankedGold[playerId] = banked + actualDeposit;

            renderer.Log($"[BANK] Deposited: {actualDeposit:F1} gold. Bank: {banked + actualDeposit:F1} / {cap:F1}");
            return true;
        }

        /// <summary>
        /// Manual withdrawal — move gold from the bank back to available balance.
        /// </summary>
        public bool Withdraw(float amount)
        {
            if (amount <= 0f) return false;
            float banked = store.PlayerBankedGold[playerId];
            if (banked < amount) return false;

            float available = store.GetPlayerGold(playerId);
            store.SetPlayerGold(playerId, available + amount);
            store.PlayerBankedGold[playerId] = banked - amount;

            renderer.Log($"[BANK] Withdrew: {amount:F1} gold. Bank: {banked - amount:F1}");
            return true;
        }

        /// <summary>
        /// Set the interest rate multiplier for this player.
        /// </summary>
        public void SetInterestRate(float rate)
        {
            store.PlayerInterestRate[playerId] = rate;
        }

        /// <summary>
        /// Get current bank balance.
        /// </summary>
        public float GetBankBalance()
        {
            return store.PlayerBankedGold[playerId];
        }

        /// <summary>
        /// Get current interest rate.
        /// </summary>
        public float GetInterestRate()
        {
            return store.PlayerInterestRate[playerId];
        }
    }
}