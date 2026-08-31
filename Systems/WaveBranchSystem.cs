using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Wave Branching System — Slay the Spire style roguelike wave selection.
    ///
    /// When a wave completes that has WaveBranches (2-3 options), this system:
    ///  1. Pauses the game and displays the options to the player.
    ///  2. After player selects one, applies bonuses (gold / research) and queues that wave.
    ///
    /// In benchmark mode (non-interactive), automatically selects option 0.
    ///
    /// Integration:
    ///   - Subscribes to WaveSpawningSystem.OnWaveComplete.
    ///   - Reads WaveConfig.WaveBranches to build the option list.
    ///   - Fires OnBranchNeeded (options) when a branch point is reached.
    ///   - Call SelectBranch(optionIndex) to commit a choice.
    /// </summary>
    public class WaveBranchSystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer renderer;
        private readonly GameConfig gameConfig;
        private readonly StateMachine stateMachine;

        // Current branch context (set when a branch wave completes)
        private List<WaveBranchOption> _pendingOptions = new List<WaveBranchOption>();
        private WaveConfig _pendingBranchWave;
        private int _pendingWaveIndex; // 0-based index into levelConfig.Waves[]
        private int _currentLevel;
        private bool _branchActive = false;

        // Events
        public event Action<List<WaveBranchOption>> OnBranchNeeded;
        public event Action<int> OnBranchSelected; // optionIndex chosen

        public IReadOnlyList<WaveBranchOption> PendingOptions => _pendingOptions;
        public bool IsBranchActive => _branchActive;

        public WaveBranchSystem(ComponentStore store, IRenderer renderer, GameConfig gameConfig, StateMachine stateMachine)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            this.stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        }

        /// <summary>
        /// Wire this into WaveSpawningSystem.OnWaveComplete.
        /// </summary>
        public void OnWaveCompleted()
        {
            if (!_branchActive) return;

            // Wave was already processed; clear state
            ClearBranchState();
        }

        /// <summary>
        /// Called each frame from FrameScheduler (WavePhase, after wave spawn starts).
        /// Detects wave-complete with branches and triggers the branch UI.
        /// Returns true if the branch UI is currently active (game should pause for input).
        /// </summary>
        public bool Update(int currentWave, int currentLevel)
        {
            if (!_branchActive) return false;

            // The branch state is active — the frame scheduler knows to pause combat
            // while BranchSelection state is active. Nothing to animate here.
            return true;
        }

        /// <summary>
        /// Call this from the game loop when the player presses 1/2/3 to select a branch option.
        /// Returns true if the selection was valid and the game should resume.
        /// </summary>
        public bool SelectBranch(int optionIndex)
        {
            if (!_branchActive)
            {
                renderer.Log("[BRANCH] No active branch to select.");
                return false;
            }

            if (optionIndex < 0 || optionIndex >= _pendingOptions.Count)
            {
                renderer.Log($"[BRANCH] Invalid option index {optionIndex}. Expected 0-{_pendingOptions.Count - 1}.");
                return false;
            }

            var option = _pendingOptions[optionIndex];
            renderer.Log($"[BRANCH] Player chose option {optionIndex + 1}: {option.Name} ({option.Difficulty})");

            // ── Apply bonuses ────────────────────────────────────────────────
            int playerId = store.PlayerEntityId;

            if (option.GoldBonus > 0f)
            {
                float newGold = store.PlayerGold[playerId] + option.GoldBonus;
                store.ApplyPlayerResourceAuthority(playerId, playerId, new Core.GAS.AttributeKey(4), option.GoldBonus);
                renderer.Log($"[BRANCH] +{option.GoldBonus} gold bonus! New gold: {newGold:F0}");
            }

            if (option.ResearchBonus > 0)
            {
                store.PlayerResearchPoints[playerId] += option.ResearchBonus;
                renderer.Log($"[BRANCH] +{option.ResearchBonus} research points!");
            }

            // ── Queue the selected wave into WaveSpawning ──────────────────────
            // We patch the WaveConfig that WaveSpawning will read next time it starts a wave.
            // The simplest approach: store the selected option and let WaveSpawning see it
            // via the gameConfig when the next wave starts.
            _selectedBranchOption = option;

            // Fire event for UI layer
            OnBranchSelected?.Invoke(optionIndex);

            ClearBranchState();
            return true;
        }

        private WaveBranchOption _selectedBranchOption;
        public WaveBranchOption GetSelectedBranchOption() => _selectedBranchOption;

        /// <summary>
        /// Called by GameManager when a wave completes (hooked into WaveSpawningSystem.OnWaveComplete).
        /// Checks if the completed wave has branch options and activates the branch UI if so.
        /// </summary>
        public void CheckAndActivateBranch(int waveIndex, int levelNumber)
        {
            _currentLevel = levelNumber;

            var levelConfig = gameConfig.GetLevelConfig(levelNumber);
            if (levelConfig == null) return;

            if (waveIndex < 0 || waveIndex >= levelConfig.Waves.Count) return;

            var waveConfig = levelConfig.Waves[waveIndex];
            if (waveConfig.WaveBranches == null || waveConfig.WaveBranches.Count == 0) return;

            // Activate branch — pause game and show options
            _pendingBranchWave = waveConfig;
            _pendingWaveIndex = waveIndex;
            _pendingOptions = waveConfig.WaveBranches;

            _branchActive = true;

            renderer.Log($"[BRANCH] Branch point reached! {waveConfig.WaveBranches.Count} options available.");
            for (int i = 0; i < waveConfig.WaveBranches.Count; i++)
            {
                var opt = waveConfig.WaveBranches[i];
                renderer.Log($"[BRANCH]   [{i + 1}] {opt.Name} — {opt.EnemyCount}x {opt.MonsterType} | +{opt.GoldBonus}g | {opt.Difficulty}");
            }

            // Transition to BranchSelection state
            stateMachine.TransitionTo(GameState.BranchSelection);

            // Notify UI
            OnBranchNeeded?.Invoke(_pendingOptions);
        }

        private void ClearBranchState()
        {
            _branchActive = false;
            _pendingOptions = new List<WaveBranchOption>();
            _pendingBranchWave = null;
            _selectedBranchOption = null;
        }
    }
}
