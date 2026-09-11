using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Computes the bounded team state potential used for potential-based reward shaping.
/// Phase 1 design (see reward_proposal.md §14): Ψ_k(s) is a hand-designed function of
/// confirmed score plus hazard-discounted provisional battery custody — no learned model,
/// no self-play calibration loop, so there is no reward non-stationarity to manage.
/// </summary>
public class PotentialRewardCalculator
{
    private readonly GameScenario scenario;
    private readonly RewardConfig config;

    // Per-team reachable-tile sets, computed once per episode via BFS flood fill from each
    // team's spawn using IsWalkable as the traversal predicate. This answers "can this team
    // physically reach this tile" (protected-storage detection) — it is NOT full pathfinding
    // (no distances/times); see reward_proposal.md §14.1 (no pathfinding infra exists yet).
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

    private static bool IsScoringBattery(ItemData data) =>
        data != null && data.Effects != null && data.Effects.Any(e => e is ScoreItemEffect);

    /// <summary>
    /// Estimated probability that <paramref name="custodyTeam"/> still holds this item at the
    /// next absorption tick. 1.0 if the item sits on a tile the opposing team cannot physically
    /// reach (protected storage); otherwise an exp(-hazard * time-remaining) falloff using
    /// straight-line distance to the nearest enemy unit as a hazard proxy.
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

    private float NearestEnemyDistance(Vector2 pos, TeamData enemy)
    {
        float best = float.MaxValue;
        foreach (Unit unit in scenario.MatchManager.Units)
        {
            if (unit.Team != enemy) continue;
            float d = Vector2.Distance(pos, unit.GlobalPos);
            if (d < best) best = d;
        }
        return best == float.MaxValue ? 999f : best;
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
    /// traversal predicate. Reachability only (no distances) — a minimal stand-in until real
    /// pathfinding exists (reward_proposal.md §14.1/§14.3).
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
