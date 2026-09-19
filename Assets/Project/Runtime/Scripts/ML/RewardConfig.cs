using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Reward values for ML training, loaded from StreamingAssets/reward_config.json.
/// Item rewards are keyed by ItemData asset name.
///
/// Who reads what (see Documentation/reward_shaping.md):
///   - fixed event rewards (itemRewards, kill/death/teamScore*) -> BlackOutAgent.Setup's event handlers;
///   - potentialEta/Gamma/Scale, hazardCoefficient -> team potential Ψ (PotentialRewardCalculator),
///     paid by BlackOutEpisodeCoordinator.ApplyPotentialShaping;
///   - navPotentialEta/Scale, hunterPotentialBaseWeight -> per-unit Φ (IndividualNavPotentialCalculator),
///     paid by BlackOutEpisodeCoordinator.ApplyNavShaping.
/// The terminal ±1 (win/lose, 0 on a draw) is hard-coded in BlackOutEpisodeCoordinator.OnGameEnded
/// and is not configurable here.
/// </summary>
[Serializable]
public class RewardConfig
{
    [Serializable]
    public struct ItemRewardEntry
    {
        public string itemName;
        public float value;
    }

    /// <summary>Per-item fixed reward, looked up by <c>ItemData.name</c> (the asset name, e.g.
    /// "Battery", "BuffSpeed"). Paid on field pickup, on deposit, and to the whole team when one of
    /// its storage items is absorbed; subtracted from the whole team when an enemy picks the item
    /// up out of its storage. Names not listed fall back to 0 (<see cref="GetItemReward"/>).</summary>
    public ItemRewardEntry[] itemRewards = Array.Empty<ItemRewardEntry>();

    // Fixed event rewards default to 0 (reward_proposal.md §12): all strategic value is meant
    // to come from potential-based shaping (Ψ_k, Φ_i) instead. These fields only exist so a
    // reward_config.json override can still re-enable them for ablation experiments; the
    // class defaults themselves must stay 0 so a missing/corrupt config file (see Load()
    // below) can never silently reintroduce fixed rewards the design forbids.
    /// <summary>Paid to the killer's agent only.</summary>
    public float killReward = 0f;
    /// <summary>Subtracted from every agent on the victim's team (not just the victim).</summary>
    public float deathPenalty = 0f;
    /// <summary>Paid to every agent on a team each time its score changes to a positive value.</summary>
    public float teamScoreReward = 0f;
    /// <summary>Subtracted from every agent on a team each time the opponent's score changes to a positive value.</summary>
    public float teamScorePenalty = 0f;

    // Potential-based shaping (see reward_proposal.md §14). Phase 1: Ψ is a hand-designed
    // function of state, not a learned model.
    /// <summary>η: weight of the team shaping term η[γΨ(s') - Ψ(s)]. 0 disables it (and skips computing Ψ).</summary>
    public float potentialEta = 0.25f;
    /// <summary>γ used in both shaping terms. Applied per physics tick (0.02 s), not per decision,
    /// and independent of the learner's own discount.</summary>
    public float potentialGamma = 0.99995f;
    /// <summary>Score points that map to tanh(1) ≈ 0.76 in Ψ = tanh(diff / potentialScale).</summary>
    public float potentialScale = 40f;
    /// <summary>c in survive = exp(-c / max(d, 0.5) · τ): d = grid path distance (world units) from the
    /// battery to the nearest enemy, τ = seconds until the next absorption.</summary>
    public float hazardCoefficient = 0.05f;

    // Individual navigation potential shaping — Phase 1.5 (see reward_proposal.md §15).
    // Fills the "unclaimed field item contributes 0 to Ψ" gap with a per-agent, non-zero-sum
    // proximity signal. Reuses potentialGamma for γ (see §15.3 — no separate discount added).
    /// <summary>η_nav: weight of the per-unit term η_nav[γΦ_i(s') - Φ_i(s)]. 0 disables it (and skips computing Φ).</summary>
    public float navPotentialEta = 0.08f;
    /// <summary>L in Saturate(d) = 1 - tanh(d / L), d in world units. Also sets the "no target"
    /// sentinel distance, 8 · L.</summary>
    public float navPotentialScale = 12f;

    // Hunter pursuit potential (IndividualNavPotentialCalculator, 2026-09-16): the pull toward an
    // empty-handed enemy, as a fraction of the pull toward one carrying a full battery stack.
    public float hunterPotentialBaseWeight = 0.25f;

    private Dictionary<string, float> _map;

    public float GetItemReward(string itemName, float fallback = 0f)
    {
        if (_map == null) BuildMap();
        return _map.TryGetValue(itemName, out float v) ? v : fallback;
    }

    private void BuildMap()
    {
        _map = new Dictionary<string, float>();
        if (itemRewards == null) return;
        foreach (var entry in itemRewards)
            _map[entry.itemName] = entry.value;
    }

    /// <summary>Command-line flag that turns both potential shapings off (η = 0), so the
    /// coordinator skips computing Ψ and Φ entirely. For runs whose reward is computed in Python
    /// from observations (blackout_env reward_v2) or not used at all (evaluation, recording).</summary>
    public const string NoShapingArg = "-noRewardShaping";

    private static RewardConfig ApplyCommandLine(RewardConfig config)
    {
        if (Array.IndexOf(Environment.GetCommandLineArgs(), NoShapingArg) < 0) return config;
        config.potentialEta = 0f;
        config.navPotentialEta = 0f;
        Debug.Log($"[RewardConfig] {NoShapingArg}: potential and nav shaping disabled");
        return config;
    }

    public static RewardConfig Load()
    {
        string path = System.IO.Path.Combine(Application.streamingAssetsPath, "reward_config.json");
        if (!System.IO.File.Exists(path))
        {
            Debug.LogError($"[RewardConfig] {path} not found. Falling back to class defaults " +
                            "(all fixed event rewards 0, shaping hyperparameters at their Phase 1/1.5 " +
                            "design values) — verify StreamingAssets was packaged with this build.");
            return ApplyCommandLine(new RewardConfig());
        }
        RewardConfig config = JsonUtility.FromJson<RewardConfig>(System.IO.File.ReadAllText(path));
        Debug.Log($"[RewardConfig] Loaded from {path}");
        ApplyCommandLine(config);
        return config;
    }
}
