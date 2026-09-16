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
/// Distances are grid path distances (same move set and no-corner-cutting rule as
/// <see cref="GridPathfinder"/>), walked as this unit's own team, rather than straight-line —
/// §15.2/§15.7 flagged straight-line as a known approximation gap since a wall between a unit and
/// its nearest item in a straight line previously understated how far away that item actually was.
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
///     by that weighted potential, highest first, ties by unit index then cell x, then y. A unit
///     left without a battery gets Φ = 0 and is free to do something other than fetch.
/// Φ is still a deterministic function of the current state, so the shaping stays potential-based.
///
/// 2026-09-16: units that cannot collect but can kill (Hunters) get a pursuit potential. They were
/// skipped entirely before, so a Collector that transformed lost its only dense signal: Ψ moves
/// only when a carrier actually dies, every direction looked the same to the value function, and
/// the Run 8 model's Hunters oscillated in place (blackout-env docs/offline_pretrain_runs.md).
///   - targets are enemy units this unit beats that do not beat it back (Hunter.asset beats
///     Collector and Carrier; a mutual kill, were one ever configured, is not a pull);
///   - weight = hunterPotentialBaseWeight + (1 - base) * the target's held-battery weight, so a
///     loaded carrier -- whose death Ψ already values -- is the strongest pull, and an empty-handed
///     unit is still worth closing on;
///   - Φ = weight * Saturate(grid path distance), assigned greedily per team with each enemy
///     claimed by at most one Hunter, the same rule as fetch targets.
/// A kill still drops Φ (the target respawns at its base), exactly like a deposit does for a
/// carrier: shaping telescopes, so approach-then-kill nets zero and the kill's value comes from Ψ.
///
/// This runs every physics tick for all units, so the searches are allocation-free: per-team
/// walkability is cached for the episode (tile collision options and region ownership are fixed
/// once a map is built), each search reuses preallocated grid arrays and heap, and each (team,
/// battery type) storage distance field is computed once per call and shared by carriers and
/// fetchers. A first version built a Dictionary per search and called MapManager.IsWalkable per
/// neighbor, which tripled Unity CPU per decision during dataset collection.
/// </summary>
public class IndividualNavPotentialCalculator
{
    private const float Sqrt2 = 1.41421356f;

    private static readonly (int dx, int dy, float cost)[] Moves =
    {
        (1, 0, 1f), (-1, 0, 1f), (0, 1, 1f), (0, -1, 1f),
        (1, 1, Sqrt2), (1, -1, Sqrt2), (-1, 1, Sqrt2), (-1, -1, Sqrt2),
    };

    private readonly GameScenario scenario;
    private readonly RewardConfig config;

    // Distance (in units of navPotentialScale) beyond which Saturate() is already ~0.
    // Used as the "no target exists" sentinel so no separate branch is needed downstream
    // (see reward_proposal.md §15.2). Also returned when a target technically exists but no
    // walkable path reaches it, which should shape identically to "no target".
    private const float NoTargetScaleMultiplier = 8f;

    private float? cachedTileWorldSize;

    // ---- Per-episode grid cache ----
    private MapSpaceInfo cachedSpace;
    private int gridWidth, gridHeight;
    private Vector2Int gridOrigin;
    private readonly Dictionary<TeamData, bool[]> walkableByTeam = new();

    // ---- Reusable search buffers ----
    private float[] scratchField;
    private int[] targetStamp;
    private int currentTargetStamp;
    private readonly List<(float key, int cell)> heap = new();
    private readonly Dictionary<(TeamData, ItemData), float[]> storageFieldPool = new();
    private readonly HashSet<(TeamData, ItemData)> storageFieldsFresh = new();
    private readonly List<int> sourceCells = new();

    // ---- Reusable per-call assignment buffers ----
    private readonly Dictionary<TeamData, List<int>> fetchersByTeam = new();
    private readonly Dictionary<TeamData, List<int>> huntersByTeam = new();
    private readonly List<(int cell, int unitIndex, float weight)> preyUnits = new();
    private readonly List<(int cell, ItemObject item)> batteries = new();
    private readonly List<(float value, int unitIndex, int cell)> candidates = new();
    private readonly HashSet<int> assignedUnits = new();
    private readonly HashSet<int> claimedCells = new();
    private readonly Comparison<(float value, int unitIndex, int cell)> candidateOrder;
    private readonly Comparison<(float value, int unitIndex, int cell)> huntOrder;

    public IndividualNavPotentialCalculator(GameScenario scenario, RewardConfig config)
    {
        this.scenario = scenario;
        this.config = config;
        // Highest value first; ties by unit index, then cell x, then cell y.
        candidateOrder = (a, b) =>
        {
            int byValue = b.value.CompareTo(a.value);
            if (byValue != 0) return byValue;
            int byUnit = a.unitIndex.CompareTo(b.unitIndex);
            if (byUnit != 0) return byUnit;
            int ax = a.cell % gridWidth, bx = b.cell % gridWidth;
            return ax != bx ? ax.CompareTo(bx) : (a.cell / gridWidth).CompareTo(b.cell / gridWidth);
        };
        // Highest value first; ties by hunter index, then prey unit index.
        huntOrder = (a, b) =>
        {
            int byValue = b.value.CompareTo(a.value);
            if (byValue != 0) return byValue;
            int byUnit = a.unitIndex.CompareTo(b.unitIndex);
            return byUnit != 0 ? byUnit : a.cell.CompareTo(b.cell);
        };
    }

    /// <summary>Drops the per-episode walkability cache. Call once at the start of every episode,
    /// after the map for that episode exists.</summary>
    public void OnEpisodeBegin()
    {
        cachedSpace = null;
        walkableByTeam.Clear();
    }

    /// <summary>World-unit distance between adjacent tile centers, so grid-step distances stay in
    /// the same units navPotentialScale was tuned against.</summary>
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
        EnsureGrid();
        storageFieldsFresh.Clear();
        foreach (List<int> list in fetchersByTeam.Values) list.Clear();
        foreach (List<int> list in huntersByTeam.Values) list.Clear();

        var phi = new float[units.Count];
        for (int i = 0; i < units.Count; i++)
        {
            Unit unit = units[i];
            if (!unit.UnitData.Collectable)
            {
                if (unit.UnitData.Beats == null || unit.UnitData.Beats.Length == 0) continue;
                if (!huntersByTeam.TryGetValue(unit.Team, out List<int> hunters))
                    huntersByTeam[unit.Team] = hunters = new List<int>();
                hunters.Add(i);
                continue;
            }

            if (unit.HoldingItem != null)
            {
                float weight = BatteryWeight(unit.HoldingItem);
                if (weight > 0f)
                {
                    float[] toStorage = StorageField(unit.Team, unit.HoldingItem.ItemData);
                    float cells = CellValue(toStorage, CellIndex(unit.GlobalPos));
                    float distance = float.IsPositiveInfinity(cells) ? NoTargetDistance() : cells * TileWorldSize();
                    phi[i] = weight * Saturate(distance);
                }
                continue;
            }

            if (!fetchersByTeam.TryGetValue(unit.Team, out List<int> fetchers))
                fetchersByTeam[unit.Team] = fetchers = new List<int>();
            fetchers.Add(i);
        }

        foreach (var (team, fetchers) in fetchersByTeam)
            if (fetchers.Count > 0)
                AssignFetchTargets(team, fetchers, units, phi);
        foreach (var (team, hunters) in huntersByTeam)
            if (hunters.Count > 0)
                AssignHuntTargets(team, hunters, units, phi);
        return phi;
    }

    private static bool Beats(UnitData attacker, UnitData defender) =>
        attacker.Beats != null && Array.IndexOf(attacker.Beats, defender) >= 0;

    /// <summary>
    /// Assigns each of <paramref name="team"/>'s Hunters at most one enemy it can kill without dying
    /// (and each enemy at most one Hunter), greedily by weight * Saturate(grid path distance), and
    /// writes that value into <paramref name="phi"/>. See the class summary for the weight.
    /// </summary>
    private void AssignHuntTargets(TeamData team, List<int> hunters, IReadOnlyList<Unit> units, float[] phi)
    {
        bool[] walkable = WalkableFor(team);
        float tile = TileWorldSize();
        float baseWeight = Mathf.Clamp01(config.hunterPotentialBaseWeight);
        candidates.Clear();

        foreach (int hunterIndex in hunters)
        {
            Unit hunter = units[hunterIndex];
            int start = CellIndex(hunter.GlobalPos);
            if (start < 0) continue;

            preyUnits.Clear();
            currentTargetStamp++;
            int distinctCells = 0;
            for (int j = 0; j < units.Count; j++)
            {
                Unit prey = units[j];
                if (!team.IsOpponent(prey.Team)) continue;
                if (!Beats(hunter.UnitData, prey.UnitData) || Beats(prey.UnitData, hunter.UnitData)) continue;
                int cell = CellIndex(prey.GlobalPos);
                if (cell < 0) continue;

                float cargo = prey.HoldingItem != null ? BatteryWeight(prey.HoldingItem) : 0f;
                preyUnits.Add((cell, j, baseWeight + (1f - baseWeight) * cargo));
                if (targetStamp[cell] != currentTargetStamp)
                {
                    targetStamp[cell] = currentTargetStamp;
                    distinctCells++;
                }
            }
            if (preyUnits.Count == 0) continue;

            sourceCells.Clear();
            sourceCells.Add(start);
            Flood(sourceCells, walkable, scratchField, distinctCells);

            // candidates' third field holds the prey's unit index here, not a cell, so the claim
            // set below is keyed by enemy unit.
            foreach (var (cell, preyIndex, weight) in preyUnits)
            {
                float cells = scratchField[cell];
                if (float.IsPositiveInfinity(cells)) continue;
                candidates.Add((weight * Saturate(cells * tile), hunterIndex, preyIndex));
            }
        }

        candidates.Sort(huntOrder);

        assignedUnits.Clear();
        claimedCells.Clear();
        foreach (var (value, hunterIndex, preyIndex) in candidates)
        {
            if (assignedUnits.Contains(hunterIndex) || claimedCells.Contains(preyIndex)) continue;
            phi[hunterIndex] = value;
            assignedUnits.Add(hunterIndex);
            claimedCells.Add(preyIndex);
        }
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
        batteries.Clear();
        foreach (ItemObject item in scenario.LevelDirector.ActiveItems)
        {
            if (item.State != ItemObject.ItemState.OnGround) continue;
            if (item.OwnedTile?.OwnedRegion is Storage) continue;
            if (!item.IsInteractable(team)) continue;
            if (BatteryWeight(item) <= 0f) continue;

            int cell = CellIndex(item.GlobalPos);
            if (cell < 0) continue;
            // One item per cell, last one wins -- same as the previous per-cell dictionary.
            int existing = -1;
            for (int b = 0; b < batteries.Count; b++)
                if (batteries[b].cell == cell) { existing = b; break; }
            if (existing >= 0) batteries[existing] = (cell, item);
            else batteries.Add((cell, item));
        }
        if (batteries.Count == 0) return;

        bool[] walkable = WalkableFor(team);
        float tile = TileWorldSize();
        candidates.Clear();

        foreach (int unitIndex in fetchers)
        {
            int start = CellIndex(units[unitIndex].GlobalPos);
            if (start < 0) continue;

            currentTargetStamp++;
            foreach (var (cell, _) in batteries) targetStamp[cell] = currentTargetStamp;
            sourceCells.Clear();
            sourceCells.Add(start);
            Flood(sourceCells, walkable, scratchField, batteries.Count);

            foreach (var (cell, item) in batteries)
            {
                float fetchCells = scratchField[cell];
                if (float.IsPositiveInfinity(fetchCells)) continue;

                float distance = fetchCells * tile;
                float storageCells = StorageField(team, item.ItemData)[cell];
                if (!float.IsPositiveInfinity(storageCells))
                    distance += storageCells * tile;
                candidates.Add((BatteryWeight(item) * Saturate(distance), unitIndex, cell));
            }
        }

        candidates.Sort(candidateOrder);

        assignedUnits.Clear();
        claimedCells.Clear();
        foreach (var (value, unitIndex, cell) in candidates)
        {
            if (assignedUnits.Contains(unitIndex) || claimedCells.Contains(cell)) continue;
            phi[unitIndex] = value;
            assignedUnits.Add(unitIndex);
            claimedCells.Add(cell);
        }
    }

    // ------------------------------------------------------------------
    // Grid search
    // ------------------------------------------------------------------

    private void EnsureGrid()
    {
        MapManager mapManager = scenario.MapManager;
        MapSpaceInfo space = mapManager.MapSpaceInfo;
        if (space == cachedSpace && gridWidth == mapManager.MapWidth && gridHeight == mapManager.MapHeight) return;

        cachedSpace = space;
        gridWidth = mapManager.MapWidth;
        gridHeight = mapManager.MapHeight;
        gridOrigin = space != null ? space.BottomLeft : Vector2Int.zero;
        walkableByTeam.Clear();
        storageFieldPool.Clear();

        int n = gridWidth * gridHeight;
        scratchField = new float[n];
        targetStamp = new int[n];
        currentTargetStamp = 0;
    }

    /// <summary>Grid index of a world position, or -1 outside the map.</summary>
    private int CellIndex(Vector2 worldPos)
    {
        Vector2Int cell = scenario.MapManager.WorldToCell(worldPos);
        int x = cell.x - gridOrigin.x, y = cell.y - gridOrigin.y;
        if (x < 0 || y < 0 || x >= gridWidth || y >= gridHeight) return -1;
        return x + y * gridWidth;
    }

    private static float CellValue(float[] field, int cell) => cell < 0 ? float.PositiveInfinity : field[cell];

    private bool[] WalkableFor(TeamData team)
    {
        if (walkableByTeam.TryGetValue(team, out bool[] walkable)) return walkable;

        MapManager mapManager = scenario.MapManager;
        walkable = new bool[gridWidth * gridHeight];
        for (int y = 0; y < gridHeight; y++)
            for (int x = 0; x < gridWidth; x++)
                walkable[x + y * gridWidth] = mapManager.IsWalkable(new Vector2Int(x + gridOrigin.x, y + gridOrigin.y), team);
        walkableByTeam[team] = walkable;
        return walkable;
    }

    /// <summary>Distance field (tile units, +inf where unreachable) from every storage tile of
    /// <paramref name="team"/> that can currently accept <paramref name="heldData"/>. Moves are
    /// symmetric, so a cell's value is also its distance to the nearest such tile. Built at most once
    /// per ComputePotentials call.</summary>
    private float[] StorageField(TeamData team, ItemData heldData)
    {
        var key = (team, heldData);
        if (!storageFieldPool.TryGetValue(key, out float[] field))
            storageFieldPool[key] = field = new float[gridWidth * gridHeight];
        if (storageFieldsFresh.Contains(key)) return field;

        sourceCells.Clear();
        foreach (MapRegion region in scenario.MapManager.Regions)
        {
            if (region is not Storage storage || storage.OwnedTeam != team) continue;

            foreach (MapTile tile in storage.MapTiles)
            {
                if (!CanAccept(tile, heldData)) continue;
                int x = tile.CellPos.x - gridOrigin.x, y = tile.CellPos.y - gridOrigin.y;
                if (x >= 0 && y >= 0 && x < gridWidth && y < gridHeight)
                    sourceCells.Add(x + y * gridWidth);
            }
        }
        Flood(sourceCells, WalkableFor(team), field, 0);
        storageFieldsFresh.Add(key);
        return field;
    }

    /// <summary>
    /// Dijkstra from <paramref name="sources"/> over <paramref name="walkable"/> cells into
    /// <paramref name="field"/> (+inf where unreached). A diagonal step needs both flanking
    /// orthogonal cells walkable. With <paramref name="stopAfterTargets"/> &gt; 0, stops once that
    /// many cells marked with the current target stamp are settled; their values are final, other
    /// cells may be left partial.
    /// </summary>
    private void Flood(List<int> sources, bool[] walkable, float[] field, int stopAfterTargets)
    {
        Array.Fill(field, float.PositiveInfinity);
        heap.Clear();
        foreach (int source in sources)
        {
            if (field[source] == 0f) continue;
            field[source] = 0f;
            HeapPush(0f, source);
        }

        int settledTargets = 0;
        while (heap.Count > 0)
        {
            (float dist, int cell) = HeapPop();
            if (dist > field[cell]) continue;
            if (stopAfterTargets > 0 && targetStamp[cell] == currentTargetStamp && ++settledTargets >= stopAfterTargets)
                return;

            int cx = cell % gridWidth, cy = cell / gridWidth;
            foreach (var (dx, dy, cost) in Moves)
            {
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= gridWidth || ny >= gridHeight) continue;
                int next = nx + ny * gridWidth;
                if (!walkable[next]) continue;
                if (dx != 0 && dy != 0 && (!walkable[nx + cy * gridWidth] || !walkable[cx + ny * gridWidth]))
                    continue;

                float nd = dist + cost;
                if (field[next] <= nd) continue;
                field[next] = nd;
                HeapPush(nd, next);
            }
        }
    }

    private void HeapPush(float key, int cell)
    {
        heap.Add((key, cell));
        int i = heap.Count - 1;
        while (i > 0)
        {
            int parent = (i - 1) / 2;
            if (heap[parent].key <= heap[i].key) break;
            (heap[parent], heap[i]) = (heap[i], heap[parent]);
            i = parent;
        }
    }

    private (float key, int cell) HeapPop()
    {
        var root = heap[0];
        int last = heap.Count - 1;
        heap[0] = heap[last];
        heap.RemoveAt(last);

        int i = 0;
        while (true)
        {
            int left = 2 * i + 1, right = 2 * i + 2, smallest = i;
            if (left < heap.Count && heap[left].key < heap[smallest].key) smallest = left;
            if (right < heap.Count && heap[right].key < heap[smallest].key) smallest = right;
            if (smallest == i) break;
            (heap[smallest], heap[i]) = (heap[i], heap[smallest]);
            i = smallest;
        }
        return root;
    }

    private static bool CanAccept(MapTile tile, ItemData heldData)
    {
        ItemObject existing = tile.MapObjects.Find(x => x is ItemObject) as ItemObject;
        return existing == null || (existing.ItemData == heldData && existing.ItemAmount < heldData.MaxItemAmount);
    }
}
