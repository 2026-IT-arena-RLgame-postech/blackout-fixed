using UnityEngine;

/// <summary>
/// Computes the per-agent navigation potential Φ_i used for Phase 1.5 auxiliary reward
/// shaping (see reward_proposal.md §15). Unlike <see cref="PotentialRewardCalculator"/>'s
/// team-level Ψ_k (which assigns zero contribution to unclaimed field items — §14.3), this
/// gives each Collectable unit a dense, individual proximity signal toward its current
/// sub-goal: the nearest unclaimed field item while empty-handed, or the nearest own-team
/// storage tile that can accept its held item while carrying.
/// </summary>
public class IndividualNavPotentialCalculator
{
    private readonly GameScenario scenario;
    private readonly RewardConfig config;

    // Distance (in units of navPotentialScale) beyond which Saturate() is already ~0.
    // Used as the "no target exists" sentinel so no separate branch is needed downstream
    // (see reward_proposal.md §15.2).
    private const float NoTargetScaleMultiplier = 8f;

    public IndividualNavPotentialCalculator(GameScenario scenario, RewardConfig config)
    {
        this.scenario = scenario;
        this.config = config;
    }

    /// <summary>Φ_i from reward_proposal.md §15.2. Bounded in [0, 1].</summary>
    public float ComputePotential(Unit unit)
    {
        if (!unit.UnitData.Collectable) return 0f;

        float distance = unit.HoldingItem == null
            ? NearestUnclaimedItemDistance(unit)
            : NearestOwnStorageTileDistance(unit);

        return Saturate(distance);
    }

    private float Saturate(float distance) =>
        1f - (float)System.Math.Tanh(distance / Mathf.Max(config.navPotentialScale, 0.01f));

    private float NoTargetDistance() => config.navPotentialScale * NoTargetScaleMultiplier;

    /// <summary>
    /// Nearest ItemObject that is on the ground, not already sitting in any team's storage,
    /// and interactable by this unit's team (§15.2's U_i(s)).
    /// </summary>
    private float NearestUnclaimedItemDistance(Unit unit)
    {
        float best = float.MaxValue;
        foreach (ItemObject item in scenario.LevelDirector.ActiveItems)
        {
            if (item.State != ItemObject.ItemState.OnGround) continue;
            if (item.OwnedTile?.OwnedRegion is Storage) continue;
            if (!item.IsInteractable(unit.Team)) continue;

            float d = Vector2.Distance(unit.GlobalPos, item.GlobalPos);
            if (d < best) best = d;
        }
        return best == float.MaxValue ? NoTargetDistance() : best;
    }

    /// <summary>
    /// Nearest tile in a Storage region owned by this unit's team that can currently accept
    /// the item the unit is carrying — empty, or holding the same item type below its max
    /// stack amount (§15.2's S_i(s)).
    /// </summary>
    private float NearestOwnStorageTileDistance(Unit unit)
    {
        ItemData heldData = unit.HoldingItem.ItemData;
        float best = float.MaxValue;

        foreach (MapRegion region in scenario.MapManager.Regions)
        {
            if (region is not Storage storage || storage.OwnedTeam != unit.Team) continue;

            foreach (MapTile tile in storage.MapTiles)
            {
                if (!CanAccept(tile, heldData)) continue;

                Vector2 tileCenter = scenario.MapManager.CellToCenterWorld(tile.CellPos);
                float d = Vector2.Distance(unit.GlobalPos, tileCenter);
                if (d < best) best = d;
            }
        }
        return best == float.MaxValue ? NoTargetDistance() : best;
    }

    private static bool CanAccept(MapTile tile, ItemData heldData)
    {
        ItemObject existing = tile.MapObjects.Find(x => x is ItemObject) as ItemObject;
        return existing == null || (existing.ItemData == heldData && existing.ItemAmount < heldData.MaxItemAmount);
    }
}
