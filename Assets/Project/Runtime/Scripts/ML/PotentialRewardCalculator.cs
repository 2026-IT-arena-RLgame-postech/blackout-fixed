using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Computes the bounded team state potential used for potential-based reward shaping.
/// Phase 1 design (see reward_proposal.md §14): Ψ_k(s) is a hand-designed function of
/// confirmed score plus hazard-discounted provisional battery custody — no learned model,
/// no self-play calibration loop, so there is no reward non-stationarity to manage.
///
///   Ψ_A(s) = tanh( (Score_A - Score_B + Σ_b sign_b · amount_b · survive_b) / potentialScale )
///   survive_b = 1 if the enemy cannot reach the battery's tile at all (protected storage),
///             else exp( -hazardCoefficient / max(d_enemy, 0.5) · τ )
/// where b ranges over scoring batteries that are carried or sitting in an owned storage (field
/// batteries count 0), sign_b = +1 if team A has custody and -1 if team B does, d_enemy is the grid
/// path distance in world units from the battery to the nearest enemy unit, and τ is the seconds left
/// until the next absorption. Ψ_B = -Ψ_A exactly.
///
/// Only Ψ_A is computed; BlackOutEpisodeCoordinator.ApplyPotentialShaping pays
/// η[γΨ_A(s') - Ψ_A(s)] to all 5 team-A agents and its negation to all 5 team-B agents every
/// physics tick while the game is Playing, so the term is zero-sum and not credited per unit.
/// </summary>
public class PotentialRewardCalculator
{
    private readonly GameScenario scenario;
    private readonly RewardConfig config;

    // Per-team reachable-tile sets, computed once per episode via BFS flood fill from each
    // team's spawn using IsWalkable as the traversal predicate. This answers only "can this
    // team physically reach this tile at all" (protected-storage detection) — a plain boolean
    // full-connectivity precompute, deliberately kept separate from the per-query grid path
    // distances in GridPathfinder (used below for hazard) since it only needs to run once per
    // episode rather than once per battery per tick.
    private readonly Dictionary<TeamData, HashSet<MapTile>> reachableTiles = new();
    private bool connectivityBuilt;

    public PotentialRewardCalculator(GameScenario scenario, RewardConfig config)
    {
        this.scenario = scenario;
        this.config = config;
    }

    /// <summary>Clears cached map connectivity. Call once at the start of every episode.</summary>
    public void OnEpisodeBegin()
    {
        connectivityBuilt = false;
        reachableTiles.Clear();
    }

    /// <summary>Ψ from <paramref name="team"/>'s perspective, bounded in [-1, 1].</summary>
    public float ComputePotential(TeamData team)
    {
        MatchManager mm = scenario.MatchManager;
        TeamData opponent = mm.OpponentTeam(team);

        // Confirmed score (already absorbed) counts in full; custody of not-yet-absorbed batteries
        // counts as its amount (score points) times the chance of still holding it at absorption.
        int scoreDiff = mm.GetTeamContext(team).Score - mm.GetTeamContext(opponent).Score;

        float provisionalDiff = 0f;
        foreach ((ItemObject item, TeamData custodyTeam) in EnumerateActiveBatteries())
        {
            float survive = SurvivalProbability(item, custodyTeam);
            float signedValue = item.ItemAmount * survive;
            if (custodyTeam == team) provisionalDiff += signedValue;
            else if (custodyTeam == opponent) provisionalDiff -= signedValue;
        }

        double psi = System.Math.Tanh((scoreDiff + provisionalDiff) / Mathf.Max(1f, config.potentialScale));
        return Mathf.Clamp((float)psi, -1f, 1f);
    }

    /// <summary>
    /// All battery-type items currently in play (carried or sitting in a team's storage),
    /// paired with the team that currently has custody of them. Field-dropped, unclaimed
    /// batteries contribute nothing in Phase 1 (see reward_proposal.md §14.3).
    /// </summary>
    private IEnumerable<(ItemObject item, TeamData custodyTeam)> EnumerateActiveBatteries()
    {
        MatchManager mm = scenario.MatchManager;

        foreach (Unit unit in mm.Units)
        {
            ItemObject held = unit.HoldingItem;
            if (held != null && IsScoringBattery(held.ItemData))
                yield return (held, unit.Team);
        }

        foreach (MapRegion region in scenario.MapManager.Regions)
        {
            if (region is not Storage storage || storage.OwnedTeam == null) continue;

            foreach (MapTile tile in storage.MapTiles)
            {
                foreach (IMapObject obj in tile.MapObjects)
                {
                    if (obj is ItemObject item && IsScoringBattery(item.ItemData))
                        yield return (item, storage.OwnedTeam);
                }
            }
        }
    }

    internal static bool IsScoringBattery(ItemData data) =>
        data != null && data.Effects != null && data.Effects.Any(e => e is ScoreItemEffect);

    /// <summary>
    /// Estimated probability that <paramref name="custodyTeam"/> still holds this item at the
    /// next absorption tick. 1.0 if the item sits on a tile the opposing team cannot physically
    /// reach (protected storage); otherwise an exp(-hazard * time-remaining) falloff using
    /// grid path distance (see <see cref="GridPathfinder"/>) to the nearest enemy unit as a
    /// hazard proxy.
    /// </summary>
    private float SurvivalProbability(ItemObject item, TeamData custodyTeam)
    {
        TeamData enemy = scenario.MatchManager.OpponentTeam(custodyTeam);

        if (item.OwnedTile != null && !IsReachableBy(item.OwnedTile, enemy))
            return 1f;

        float nearestEnemyDist = NearestEnemyDistance(item.GlobalPos, enemy);
        float tau = scenario.AbsorptionTimer?.RemainingTime ?? 0f;
        float hazard = config.hazardCoefficient / Mathf.Max(nearestEnemyDist, 0.5f);
        return Mathf.Exp(-hazard * tau);
    }

    /// <summary>
    /// Grid path distance (world units) from <paramref name="pos"/> to the nearest unit of
    /// <paramref name="enemy"/>, walked as that team would (its own IsWalkable), instead of the
    /// straight-line approximation reward_proposal.md §14.1/§14.3 flagged as a known gap. This
    /// answers "how far does the enemy actually have to travel", so a wall between the item and
    /// the enemy correctly lowers hazard even when the enemy happens to be close in a straight
    /// line. Falls back to a large sentinel if no enemy exists or none can reach this cell at
    /// all (e.g. sealed off by its own team's protected-storage walls).
    /// </summary>
    private float NearestEnemyDistance(Vector2 pos, TeamData enemy)
    {
        MapManager mapManager = scenario.MapManager;
        Vector2Int startCell = mapManager.WorldToCell(pos);

        var enemyCells = new HashSet<Vector2Int>();
        foreach (Unit unit in scenario.MatchManager.Units)
            if (unit.Team == enemy)
                enemyCells.Add(mapManager.WorldToCell(unit.GlobalPos));
        if (enemyCells.Count == 0) return 999f;

        float? cells = GridPathfinder.NearestMatchingDistance(
            startCell,
            cell => mapManager.IsWalkable(cell, enemy),
            enemyCells.Contains);

        return cells.HasValue ? cells.Value * TileWorldSize() : 999f;
    }

    private float? cachedTileWorldSize;

    /// <summary>World-unit distance between adjacent tile centers, so grid-step distances from
    /// <see cref="GridPathfinder"/> stay in the same units the existing hazard/potential-scale
    /// hyperparameters were chosen against (no straight-line-vs-path-unit mismatch to retune).</summary>
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

    private bool IsReachableBy(MapTile tile, TeamData team)
    {
        BuildConnectivityIfNeeded();
        return reachableTiles.TryGetValue(team, out HashSet<MapTile> set) && set.Contains(tile);
    }

    private void BuildConnectivityIfNeeded()
    {
        if (connectivityBuilt) return;
        connectivityBuilt = true;

        MatchManager mm = scenario.MatchManager;
        MapSpaceInfo spaceInfo = scenario.MapManager.MapSpaceInfo;
        BuildReachabilitySet(mm.TeamA, spaceInfo.TeamASpawnPoint);
        BuildReachabilitySet(mm.TeamB, spaceInfo.TeamBSpawnPoint);
    }

    private static readonly Vector2Int[] Directions =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
    };

    /// <summary>
    /// BFS flood fill from <paramref name="team"/>'s spawn using IsWalkable(pos, team) as the
    /// traversal predicate. Orthogonal-only 4-connectivity here still yields the exact same
    /// reachable set as GridPathfinder's corner-respecting 8-directional search below (a
    /// diagonal step is only ever allowed when both flanking orthogonal cells are already
    /// walkable, so it never reaches a cell 4-connectivity couldn't also reach — it only ever
    /// shortens the path). Kept as its own plain BFS because this only needs a yes/no
    /// reachability answer, computed once per episode, not a per-query distance.
    /// </summary>
    private void BuildReachabilitySet(TeamData team, Vector2Int spawn)
    {
        MapManager mapManager = scenario.MapManager;
        var visited = new HashSet<MapTile>();
        var queue = new Queue<Vector2Int>();
        queue.Enqueue(spawn);

        while (queue.Count > 0)
        {
            Vector2Int pos = queue.Dequeue();
            MapTile tile = mapManager.GetTile(pos);
            if (tile == null || visited.Contains(tile) || !mapManager.IsWalkable(pos, team))
                continue;

            visited.Add(tile);
            foreach (Vector2Int dir in Directions)
                queue.Enqueue(pos + dir);
        }

        reachableTiles[team] = visited;
    }
}
