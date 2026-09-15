using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Computes the per-agent navigation potential Φ_i used for Phase 1.5 auxiliary reward
/// shaping (see reward_proposal.md §15). Unlike <see cref="PotentialRewardCalculator"/>'s
/// team-level Ψ_k (which assigns zero contribution to unclaimed field items — §14.3), this
/// gives each Collectable unit a dense, individual proximity signal toward its current
/// sub-goal: a ground battery while empty-handed, or the nearest own-team storage tile that can
/// accept its held battery while carrying.
///
/// Distances are grid path distances (<see cref="GridPathfinder"/>), walked as this unit's own
/// team, rather than straight-line — §15.2/§15.7 flagged straight-line as a known approximation
/// gap since a wall between a unit and its nearest item in a straight line previously understated
/// how far away that item actually was.
///
/// 2026-09-12: the empty-handed potential folds in the candidate item's own distance to storage,
/// so potential doesn't jump upward right as a unit reaches an item as if the delivery were already
/// done — it was previously blind to the carry leg still ahead, then dropped sharply the instant the
/// item was actually picked up. At pickup the unit's position equals the item's position, so the
/// fetch-phase sum and the carry-phase distance agree exactly. Deposit's drop (carry potential -> a
/// fresh, unrelated fetch target) is NOT smoothed the same way: finishing one delivery genuinely
/// means a new, independently-distant delivery is just starting. See reward_proposal.md §16.5-1.
///
/// 2026-09-15: fetch targets are assigned per team instead of per unit, and weighted by value
/// (blackout-env docs/reward_hypotheses.md H4/H7). Previously every empty-handed unit independently
/// chased its own nearest item of ANY kind, so the reward paid several teammates to converge on the
/// same item, treated a 1-count battery like a full stack, and paid for hauling special items whose
/// use Ψ values at zero. Now:
///   - only scoring batteries (the items Ψ values) are fetch targets, and a unit carrying anything
///     else gets Φ = 0;
///   - Φ is scaled by the battery's amount / MaxItemAmount, both while fetching and while carrying,
///     so pickup still leaves Φ unchanged (same item, same distance, same weight);
///   - each battery is claimed by at most one teammate: (unit, battery) pairs are assigned greedily
///     by that weighted potential, highest first, ties by unit index then cell. A unit left without
///     a battery gets Φ = 0 and is free to do something other than fetch.
/// Φ is still a deterministic function of the current state, so the shaping stays potential-based.
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

    /// <summary>
    /// Φ_i (reward_proposal.md §15.2, value-weighted and team-assigned as described on the class)
    /// for every unit, indexed like <paramref name="units"/>. Bounded in [0, 1]. Computed for all
    /// units together because a unit's fetch target depends on its teammates'.
    /// </summary>
    public float[] ComputePotentials(IReadOnlyList<Unit> units)
    {
        var phi = new float[units.Count];
        var fetchersByTeam = new Dictionary<TeamData, List<int>>();

        for (int i = 0; i < units.Count; i++)
        {
            Unit unit = units[i];
            if (!unit.UnitData.Collectable) continue;

            if (unit.HoldingItem != null)
            {
                float weight = BatteryWeight(unit.HoldingItem);
                if (weight > 0f)
                    phi[i] = weight * Saturate(NearestOwnStorageTileDistance(unit));
                continue;
            }

            if (!fetchersByTeam.TryGetValue(unit.Team, out List<int> fetchers))
                fetchersByTeam[unit.Team] = fetchers = new List<int>();
            fetchers.Add(i);
        }

        foreach (var (team, fetchers) in fetchersByTeam)
            AssignFetchTargets(team, fetchers, units, phi);
        return phi;
    }

    private float Saturate(float distance) =>
        1f - (float)System.Math.Tanh(distance / Mathf.Max(config.navPotentialScale, 0.01f));

    private float NoTargetDistance() => config.navPotentialScale * NoTargetScaleMultiplier;

    /// <summary>Battery amount as a fraction of a full stack; 0 for anything that isn't a scoring
    /// battery (Ψ gives those no value, so fetching or carrying them isn't shaped).</summary>
    private static float BatteryWeight(ItemObject item)
    {
        if (!PotentialRewardCalculator.IsScoringBattery(item.ItemData)) return 0f;
        return Mathf.Clamp01(item.ItemAmount / (float)Mathf.Max(1, item.ItemData.MaxItemAmount));
    }

    /// <summary>
    /// Assigns each of <paramref name="team"/>'s empty-handed Collectable units at most one ground
    /// battery (and each battery at most one unit), greedily by weight * Saturate(total delivery
    /// distance), and writes that value into <paramref name="phi"/>. Total delivery distance is the
    /// unit's grid path distance to the battery (§15.2's U_i(s): on the ground, not in any storage,
    /// interactable by this team) plus the battery's own distance to the nearest storage tile that
    /// would accept it — or the fetch leg alone if no storage is reachable from the battery, so a
    /// reachable item never reads as "no target".
    /// </summary>
    private void AssignFetchTargets(TeamData team, List<int> fetchers, IReadOnlyList<Unit> units, float[] phi)
    {
        MapManager mapManager = scenario.MapManager;
        var batteries = new Dictionary<Vector2Int, ItemObject>();
        foreach (ItemObject item in scenario.LevelDirector.ActiveItems)
        {
            if (item.State != ItemObject.ItemState.OnGround) continue;
            if (item.OwnedTile?.OwnedRegion is Storage) continue;
            if (!item.IsInteractable(team)) continue;
            if (BatteryWeight(item) <= 0f) continue;

            batteries[mapManager.WorldToCell(item.GlobalPos)] = item;
        }
        if (batteries.Count == 0) return;

        Func<Vector2Int, bool> walkable = cell => mapManager.IsWalkable(cell, team);
        float tile = TileWorldSize();
        // One flood per battery type out of every accepting storage tile, shared by all batteries
        // of that type (moves are symmetric, so it gives each cell's distance to storage).
        var storageFields = new Dictionary<ItemData, Dictionary<Vector2Int, float>>();

        var candidates = new List<(float value, int unitIndex, Vector2Int cell)>();
        foreach (int unitIndex in fetchers)
        {
            Vector2Int startCell = mapManager.WorldToCell(units[unitIndex].GlobalPos);
            Dictionary<Vector2Int, float> fromUnit = GridPathfinder.DistanceField(new[] { startCell }, walkable);

            foreach (var (cell, item) in batteries)
            {
                if (!fromUnit.TryGetValue(cell, out float fetchCells)) continue;

                if (!storageFields.TryGetValue(item.ItemData, out Dictionary<Vector2Int, float> toStorage))
                    storageFields[item.ItemData] = toStorage = GridPathfinder.DistanceField(AcceptingStorageCells(team, item.ItemData), walkable);

                float distance = fetchCells * tile;
                if (toStorage.TryGetValue(cell, out float storageCells))
                    distance += storageCells * tile;
                candidates.Add((BatteryWeight(item) * Saturate(distance), unitIndex, cell));
            }
        }

        candidates.Sort((x, y) =>
        {
            int byValue = y.value.CompareTo(x.value);
            if (byValue != 0) return byValue;
            int byUnit = x.unitIndex.CompareTo(y.unitIndex);
            if (byUnit != 0) return byUnit;
            return x.cell.x != y.cell.x ? x.cell.x.CompareTo(y.cell.x) : x.cell.y.CompareTo(y.cell.y);
        });

        var assignedUnits = new HashSet<int>();
        var claimedCells = new HashSet<Vector2Int>();
        foreach (var (value, unitIndex, cell) in candidates)
        {
            if (assignedUnits.Contains(unitIndex) || claimedCells.Contains(cell)) continue;
            phi[unitIndex] = value;
            assignedUnits.Add(unitIndex);
            claimedCells.Add(cell);
        }
    }

    /// <summary>
    /// Grid path distance to the nearest tile in a Storage region owned by this unit's team that
    /// can currently accept the item the unit is carrying — empty, or holding the same item type
    /// below its max stack amount (§15.2's S_i(s)).
    /// </summary>
    private float NearestOwnStorageTileDistance(Unit unit)
    {
        Vector2Int startCell = scenario.MapManager.WorldToCell(unit.GlobalPos);
        HashSet<Vector2Int> targetCells = AcceptingStorageCells(unit.Team, unit.HoldingItem.ItemData);
        if (targetCells.Count == 0) return NoTargetDistance();

        MapManager mapManager = scenario.MapManager;
        float? cells = GridPathfinder.NearestMatchingDistance(
            startCell,
            cell => mapManager.IsWalkable(cell, unit.Team),
            targetCells.Contains);

        return cells.HasValue ? cells.Value * TileWorldSize() : NoTargetDistance();
    }

    private HashSet<Vector2Int> AcceptingStorageCells(TeamData team, ItemData heldData)
    {
        var cells = new HashSet<Vector2Int>();
        foreach (MapRegion region in scenario.MapManager.Regions)
        {
            if (region is not Storage storage || storage.OwnedTeam != team) continue;

            foreach (MapTile tile in storage.MapTiles)
                if (CanAccept(tile, heldData))
                    cells.Add(tile.CellPos);
        }
        return cells;
    }

    private static bool CanAccept(MapTile tile, ItemData heldData)
    {
        ItemObject existing = tile.MapObjects.Find(x => x is ItemObject) as ItemObject;
        return existing == null || (existing.ItemData == heldData && existing.ItemAmount < heldData.MaxItemAmount);
    }
}
