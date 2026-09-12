using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Runtime debug overlay: per-unit cumulative reward this episode, team totals, and a
/// scrolling feed of individual reward grants from RewardEventLog. Toggle with F9.
///
/// Self-attached by BlackOutEpisodeCoordinator.Awake() — no scene/prefab wiring needed.
/// Only active in the Editor or development builds (see the coordinator's #if guard).
/// </summary>
public class RewardDebugUI : MonoBehaviour
{
    private BlackOutAgent[] agents;
    private bool visible = true;
    private Vector2 scroll;

    private static GUIStyle headerStyle;
    private static GUIStyle rowStyle;
    private static GUIStyle boxStyle;

    public void Initialize(BlackOutAgent[] agentArray)
    {
        agents = agentArray;
    }

    private void Update()
    {
        if (Keyboard.current != null && Keyboard.current.f9Key.wasPressedThisFrame)
            visible = !visible;
    }

    private void OnGUI()
    {
        if (!visible || agents == null) return;

        EnsureStyles();

        const float width = 360f;
        const float height = 420f;
        GUILayout.BeginArea(new Rect(10, 10, width, height), boxStyle);

        GUILayout.Label("Reward Debug (F9 to hide)", headerStyle);
        GUILayout.Space(2);

        float teamATotal = 0f, teamBTotal = 0f;
        foreach (var agent in agents)
        {
            if (agent == null) continue;
            float cum = agent.GetCumulativeReward();
            bool isTeamA = agent.UnitIndex < 5;
            if (isTeamA) teamATotal += cum; else teamBTotal += cum;

            GUI.color = ColorFor(cum);
            GUILayout.Label(
                $"unit_{agent.UnitIndex} [{(isTeamA ? "A" : "B")}]   cum={Signed(cum)}",
                rowStyle);
        }
        GUI.color = Color.white;

        GUILayout.Space(4);
        GUILayout.Label($"Team A total: {Signed(teamATotal)}    Team B total: {Signed(teamBTotal)}", headerStyle);

        GUILayout.Space(6);
        GUILayout.Label("Recent reward events (newest first):", headerStyle);
        scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(220));
        var entries = RewardEventLog.Entries;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            GUI.color = ColorFor(e.amount);
            GUILayout.Label($"[{e.time:0.0}s] unit_{e.unitIndex}  {e.label}  {Signed(e.amount)}", rowStyle);
        }
        GUI.color = Color.white;
        GUILayout.EndScrollView();

        GUILayout.EndArea();
    }

    private static string Signed(float v) => v.ToString("+0.000;-0.000;0.000");

    private static Color ColorFor(float v) =>
        v > 0f ? new Color(0.55f, 1f, 0.55f) : v < 0f ? new Color(1f, 0.55f, 0.55f) : Color.white;

    private static void EnsureStyles()
    {
        if (boxStyle != null) return;

        Texture2D bg = new Texture2D(1, 1);
        bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.78f));
        bg.Apply();

        boxStyle = new GUIStyle(GUI.skin.box);
        boxStyle.normal.background = bg;

        headerStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        headerStyle.normal.textColor = Color.white;

        rowStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
        rowStyle.normal.textColor = Color.white;
    }
}
