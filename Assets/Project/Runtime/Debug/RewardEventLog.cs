using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// In-memory log of individual reward grants, recorded by BlackOutAgent every time it
/// calls AddReward. Read by RewardDebugUI to show a live feed of who got rewarded, why,
/// and how much — without that, only the cumulative total is visible and individual
/// shaping events (kill, steal, deposit, ...) are invisible.
/// </summary>
public static class RewardEventLog
{
    public struct Entry
    {
        public int unitIndex;
        public string label;
        public float amount;
        public float time;
    }

    private const int MaxEntries = 60;
    private static readonly List<Entry> entries = new();

    public static IReadOnlyList<Entry> Entries => entries;

    public static void Record(int unitIndex, string label, float amount)
    {
        if (Mathf.Approximately(amount, 0f)) return;

        entries.Add(new Entry { unitIndex = unitIndex, label = label, amount = amount, time = Time.time });
        if (entries.Count > MaxEntries)
            entries.RemoveAt(0);
    }

    public static void Clear() => entries.Clear();
}
