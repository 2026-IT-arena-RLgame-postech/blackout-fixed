using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Computes the per-agent navigation potential Φ_i used for Phase 1.5 auxiliary reward
/// shaping (see reward_proposal.md §15). Unlike <see cref="PotentialRewardCalculator"/>'s
/// team-level Ψ_k (which assigns zero contribution to unclaimed field items — §14.3), this
/// gives each Collectable unit a dense, individual proximity signal toward its current
/// sub-goal: the nearest unclaimed field item while empty-handed, or the nearest own-team
/// storage tile that can accept its held item while carrying.
///
/// Distances are grid path distances (<see cref="GridPathfinder"/>), walked as this unit's own
/// team, rather than straight-line — §15.2/§15.7 flagged straight-line as a known approximation
/// gap since a wall between a unit and its nearest item in a straight line previously understated
/// how far away that item actually was.
///
/// 2026-09-12: the empty-handed potential now folds in the candidate item's own distance to
/// storage (see <see cref="NearestUnclaimedItemDistance"/>), so potential no longer jumps
/// upward right as a unit reaches an item as if the delivery were already done — it was
/// previously blind to the carry leg still ahead, then dropped sharply the instant the item was
/// actually picked up. See reward_proposal.md §16.5-1.
/// </summary>
public class IndividualNavPotentialCalculator
{
    private readonly GameScenario scenario;
    private readonly RewardConfig config;

    // Distance (in units of navPotentialScale) beyond which Saturate() is already ~0.
    // Used as the "no target exists" sentinel so no separate branch is needed downstream
    // (see reward_proposal.md §15.2). Also returned when a target technically exists but no
    // walkable path reaches it, which should shape identically to "no target".
    private const float NoTargetScaleMultiplier = 8f;

    private float? cachedTileWorldSize;

    public IndividualNavPotentialCalculator(GameScenario scenario, RewardConfig config)
    {
        this.scenario = scenario;
        this.config = config;
    }

    /// <summary>World-unit distance between adjacent tile centers, so GridPathfinder's grid-step
    /// distances stay in the same units navPotentialScale was tuned against.</summary>
    private float TileWorldSize()
    {
        if (!cachedTileWorldSize.HasValue)
        {
            MapManager mapManager = scenario.MapManager;
            cachedTileWorldSize = Vector2.Distance(
                mapManager.CellToCenterWorld(Vector2Int.zero),
                mapManager.CellToCenterWorld(Vector2Int.right));
        }
        return cachedTileWorldSize.Value;
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
    /// Grid path distance to the nearest ItemObject that is on the ground, not already sitting
    /// in any team's storage, and interactable by this unit's team (§15.2's U_i(s)), PLUS that
    /// same item's own grid path distance to the nearest storage tile that would accept it.
    ///
    /// 2026-09-12: this used to be just the distance to the item, which made potential jump to
    /// near its maximum right as the unit reached the item — as if the delivery were basically
    /// done — even though the (possibly long) carry leg to storage hadn't started yet. The
    /// instant the unit actually picks the item up, <see cref="NearestOwnStorageTileDistance"/>
    /// then re-evaluated from scratch and could be much larger, producing a sudden potential
    /// drop (and a spurious negative nav-shaping reward) at exactly the moment of a good event
    /// (successfully reaching the item). Folding the item's own distance-to-storage into the
    /// not-holding potential fixes this: right at pickup the unit's position equals the item's
    /// position, so this sum and the post-pickup carry-phase distance agree exactly — no jump.
    /// Deposit's drop (carry potential -> a fresh, unrelated fetch target) is NOT smoothed the
    /// same way: finishing one delivery genuinely means a new, independently-distant delivery is
    /// just starting, so a drop there reflects real remaining work rather than a modeling error.
    /// </summary>
    private float NearestUnclaimedItemDistance(Unit unit)
    {
        MapManager mapManager = scenario.MapManager;
        var itemsByCell = new Dictionary<Vector2Int, ItemObject>();
        foreach (ItemObject item in scenario.LevelDirector.ActiveItems)
        {
            if (item.State != ItemObject.ItemState.OnGround) continue;
            if (item.OwnedTile?.OwnedRegion is Storage) continue;
            if (!item.IsInteractable(unit.Team)) continue;

            itemsByCell[mapManager.WorldToCell(item.GlobalPos)] = item;
        }
        if (itemsByCell.Count == 0) return NoTargetDistance();

        Vector2Int startCell = mapManager.WorldToCell(unit.GlobalPos);
        var match = GridPathfinder.NearestMatching(
            startCell,
            cell => mapManager.IsWalkable(cell, unit.Team),
            itemsByCell.ContainsKey);
        if (match == null) return NoTargetDistance();

        float dFetch = match.Value.distance * TileWorldSize();
        ItemObject targetItem = itemsByCell[match.Value.cell];
        float dItemToStorage = NearestAcceptingStorageDistance(match.Value.cell, unit.Team, targetItem.ItemData);
        // No reachable storage from the item's own position (e.g. sealed off): fall back to the
        // fetch-only distance rather than the NoTargetDistance sentinel, so this degrades to the
        // old behavior instead of falsely reporting "no target" for an item that IS reachable.
        return dItemToStorage < 0f ? dFetch : dFetch + dItemToStorage;
    }

    /// <summary>
    /// Grid path distance to the nearest tile in a Storage region owned by this unit's team that
    /// can currently accept the item the unit is carrying — empty, or holding the same item type
    /// below its max stack amount (§15.2's S_i(s)).
    /// </summary>
    private float NearestOwnStorageTileDistance(Unit unit)
    {
        Vector2Int startCell = scenario.MapManager.WorldToCell(unit.GlobalPos);
        float d = NearestAcceptingStorageDistance(startCell, unit.Team, unit.HoldingItem.ItemData);
        return d < 0f ? NoTargetDistance() : d;
    }

    /// <summary>
    /// Grid path distance (world units) from <paramref name="fromCell"/> to the nearest
    /// accepting storage tile, or -1 if none exists or none is reachable. Shared by the
    /// carry-phase potential (from the unit's own position) and the fetch-phase potential
    /// (from a candidate item's position, to fold the remaining carry leg into that item's
    /// total delivery distance — see <see cref="NearestUnclaimedItemDistance"/>).
    /// </summary>
    private float NearestAcceptingStorageDistance(Vector2Int fromCell, TeamData team, ItemData heldData)
    {
        var targetCells = new HashSet<Vector2Int>();
        foreach (MapRegion region in scenario.MapManager.Regions)
        {
            if (region is not Storage storage || storage.OwnedTeam != team) continue;

            foreach (MapTile tile in storage.MapTiles)
                if (CanAccept(tile, heldData))
                    targetCells.Add(tile.CellPos);
        }
        if (targetCells.Count == 0) return -1f;

        MapManager mapManager = scenario.MapManager;
        float? cells = GridPathfinder.NearestMatchingDistance(
            fromCell,
            cell => mapManager.IsWalkable(cell, team),
            targetCells.Contains);

        return cells.HasValue ? cells.Value * TileWorldSize() : -1f;
    }

    private static bool CanAccept(MapTile tile, ItemData heldData)
    {
        ItemObject existing = tile.MapObjects.Find(x => x is ItemObject) as ItemObject;
        return existing == null || (existing.ItemData == heldData && existing.ItemAmount < heldData.MaxItemAmount);
    }
}
