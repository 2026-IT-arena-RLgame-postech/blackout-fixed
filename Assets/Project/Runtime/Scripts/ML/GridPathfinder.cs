using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Grid shortest-path distance used by <see cref="PotentialRewardCalculator"/> and
/// <see cref="IndividualNavPotentialCalculator"/> in place of the straight-line distance
/// approximation noted as a known gap in reward_proposal.md §14.1/§14.3/§15.2/§15.7.
///
/// Both call sites ask "how far is the nearest tile matching some condition" (nearest enemy,
/// nearest unclaimed item, nearest storage tile that can accept this item) rather than "what
/// is the distance to this one specific tile". For that shape of query, a single best-first
/// (Dijkstra/A* with zero heuristic) flood outward from the source is both simpler and cheaper
/// than running a separate point-to-point A* against every candidate: it finds the true
/// nearest match and stops, without ever evaluating candidates that turn out to be farther.
/// Edge costs are uniform (1 orthogonal, sqrt(2) diagonal) so this is exactly what A* would
/// return for any of those candidates individually — just computed once instead of per
/// candidate.
/// </summary>
public static class GridPathfinder
{
    private const float Sqrt2 = 1.41421356f;

    private static readonly (int dx, int dy, float cost)[] Moves =
    {
        (1, 0, 1f), (-1, 0, 1f), (0, 1, 1f), (0, -1, 1f),
        (1, 1, Sqrt2), (1, -1, Sqrt2), (-1, 1, Sqrt2), (-1, -1, Sqrt2),
    };

    /// <summary>
    /// Shortest walkable-grid distance, in tile units, from <paramref name="startCell"/> to the
    /// nearest cell for which <paramref name="isTarget"/> is true. <paramref name="isWalkable"/>
    /// is the traversal predicate for whichever team/traveler this search is for (pass e.g.
    /// <c>cell => mapManager.IsWalkable(cell, team)</c> — walkability in this game is
    /// team-relative, see MapManager.IsWalkable). A diagonal step is only allowed when both
    /// flanking orthogonal cells are walkable (no corner-cutting), matching the rule the
    /// existing heuristic policies already use (docs/heuristic_policy_ko.md).
    /// Returns null if no reachable cell satisfies <paramref name="isTarget"/>.
    /// </summary>
    public static float? NearestMatchingDistance(
        Vector2Int startCell,
        Func<Vector2Int, bool> isWalkable,
        Func<Vector2Int, bool> isTarget,
        int maxVisited = 4096) => NearestMatching(startCell, isWalkable, isTarget, maxVisited)?.distance;

    /// <summary>
    /// Same search as <see cref="NearestMatchingDistance"/>, but also returns which target cell
    /// was matched. Needed when the caller has to look up per-target data (e.g. which item was
    /// nearest, to then query something rooted at that item's own position) rather than just the
    /// distance to it.
    /// </summary>
    public static (float distance, Vector2Int cell)? NearestMatching(
        Vector2Int startCell,
        Func<Vector2Int, bool> isWalkable,
        Func<Vector2Int, bool> isTarget,
        int maxVisited = 4096)
    {
        if (isTarget(startCell)) return (0f, startCell);

        var best = new Dictionary<Vector2Int, float> { [startCell] = 0f };
        var heap = new MinHeap();
        heap.Push(0f, startCell);
        int visited = 0;

        while (heap.Count > 0 && visited < maxVisited)
        {
            (float dist, Vector2Int cell) = heap.Pop();
            if (dist > best[cell]) continue; // stale entry, a shorter path was already found
            visited++;

            foreach (var (dx, dy, cost) in Moves)
            {
                var next = new Vector2Int(cell.x + dx, cell.y + dy);
                if (!isWalkable(next)) continue;
                if (dx != 0 && dy != 0 &&
                    (!isWalkable(new Vector2Int(cell.x + dx, cell.y)) || !isWalkable(new Vector2Int(cell.x, cell.y + dy))))
                    continue; // no cutting across a blocked corner

                float nd = dist + cost;
                if (best.TryGetValue(next, out float existing) && existing <= nd) continue;
                best[next] = nd;

                if (isTarget(next)) return (nd, next);
                heap.Push(nd, next);
            }
        }
        return null;
    }

    /// <summary>Minimal binary min-heap keyed by float priority. Avoids depending on
    /// System.Collections.Generic.PriorityQueue, whose availability varies with Unity's
    /// configured API compatibility level.</summary>
    private sealed class MinHeap
    {
        private readonly List<(float priority, Vector2Int value)> items = new();

        public int Count => items.Count;

        public void Push(float priority, Vector2Int value)
        {
            items.Add((priority, value));
            int i = items.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (items[parent].priority <= items[i].priority) break;
                (items[parent], items[i]) = (items[i], items[parent]);
                i = parent;
            }
        }

        public (float priority, Vector2Int value) Pop()
        {
            var root = items[0];
            int last = items.Count - 1;
            items[0] = items[last];
            items.RemoveAt(last);

            int i = 0;
            while (true)
            {
                int left = 2 * i + 1, right = 2 * i + 2, smallest = i;
                if (left < items.Count && items[left].priority < items[smallest].priority) smallest = left;
                if (right < items.Count && items[right].priority < items[smallest].priority) smallest = right;
                if (smallest == i) break;
                (items[smallest], items[i]) = (items[i], items[smallest]);
                i = smallest;
            }
            return root;
        }
    }
}
