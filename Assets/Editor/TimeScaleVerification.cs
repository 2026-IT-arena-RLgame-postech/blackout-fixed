using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Temporary diagnostic harness for verifying the Time.timeScale fix in
/// BlackOutEpisodeCoordinator/BlackOutAgent. Not part of the game — safe to delete
/// once the fix is verified. Never saves the scene or any asset; it only reads
/// runtime state during a throwaway Play session.
///
/// Runs the ML training scene headless for a fixed wall-clock window at a given
/// Time.timeScale (no Python trainer attached, so agents run on Heuristic
/// automatically — see BehaviorParameters.GeneratePolicy) and records how many
/// FixedUpdate ticks / how much game-time elapsed, plus any runtime errors, so two
/// runs (e.g. timeScale=1 vs 100) can be diffed to confirm timeScale actually
/// speeds up simulation and the episode-end/restart race stays fixed under it.
///
/// Usage (no -quit — the harness exits itself via EditorApplication.Exit):
///   Unity -batchmode -nographics -projectPath &lt;path-to-blackout&gt; \
///     -executeMethod TimeScaleVerification.Run \
///     -verifyTimeScale 100 -verifyDuration 12 -verifyOutFile /tmp/out_100.json -logFile -
/// </summary>
public static class TimeScaleVerification
{
    private const string ScenePath = "Assets/Project/Runtime/Scenes/Prototype.unity";

    [Serializable]
    private class Result
    {
        public float requestedTimeScale;
        public float requestedDurationSec;
        public float realElapsedSec;
        public float gameTimeElapsedSec;
        public int fixedUpdateTicks;
        public float fixedDeltaTime;
        public float maxEpisodeTimeUsed;
        public int[] episodeEndTicks;
        public float[] episodeEndRealTime;
        public int errorCount;
        public string[] errors;
        public string[] trace;
        public int seedUsed;
        public string[] episodeOutcomes;
        public string[] episodeStartFingerprints;
    }

    private class TickCounter : MonoBehaviour
    {
        public int Ticks;
        private void FixedUpdate() => Ticks++;
    }

    private static TickCounter s_Counter;
    private static float s_RealStart;
    private static float s_TargetTimeScale;
    private static float s_Duration;
    private static float s_MaxEpisodeTimeOverride;
    private static string s_OutFile;
    private static List<string> s_Errors;
    private static List<int> s_EpisodeEndTicks;
    private static List<float> s_EpisodeEndRealTime;
    private static bool s_TimeScaleApplied;
    private static bool s_SubscribedToGameEnded;
    private static float s_MaxEpisodeTimeUsed;
    private static bool s_PrevEnterPlayModeOptionsEnabled;
    private static EnterPlayModeOptions s_PrevEnterPlayModeOptions;

    // Debug-only reflection trace (root-causing the "stuck after episode 1" bug).
    private static List<string> s_Trace;
    private static MonoBehaviour s_Coordinator;
    private static FieldInfo s_EpisodeBeginCountField;
    private static FieldInfo s_AgentsField;
    private static Component s_MatchManager;
    private static FieldInfo s_GameEndedField;
    private static object s_LastEpisodeBeginCount;
    private static object s_LastGameEnded;
    private static GameState s_LastState = (GameState)(-1);
    private static int s_LastRewardLogCount = -1;
    private static int s_LastSnapshotTick = -1;
    private static FieldInfo s_TimersField;
    private static int s_EpisodeStartedCount;
    private static int s_Seed;
    private static List<string> s_EpisodeOutcomes;
    private static List<string> s_EpisodeStartFingerprints;

    public static void Run()
    {
        s_TargetTimeScale = GetFloatArg("-verifyTimeScale", 1f);
        s_Duration = GetFloatArg("-verifyDuration", 10f);
        // <=0 means "don't touch MaxEpisodeTime, use whatever GameBalanceConfig has".
        s_MaxEpisodeTimeOverride = GetFloatArg("-verifyMaxEpisodeTime", -1f);
        s_OutFile = GetStringArg("-verifyOutFile", "/tmp/timescale_verify.json");
        s_Errors = new List<string>();
        s_EpisodeEndTicks = new List<int>();
        s_EpisodeEndRealTime = new List<float>();
        s_TimeScaleApplied = false;
        s_SubscribedToGameEnded = false;
        s_MaxEpisodeTimeUsed = -1f;
        s_Counter = null;
        s_Trace = new List<string>();
        s_Coordinator = null;
        s_EpisodeBeginCountField = null;
        s_AgentsField = null;
        s_MatchManager = null;
        s_GameEndedField = null;
        s_LastEpisodeBeginCount = null;
        s_LastGameEnded = null;
        s_LastState = (GameState)(-1);
        s_LastRewardLogCount = -1;
        s_LastSnapshotTick = -1;
        s_TimersField = null;
        s_EpisodeStartedCount = 0;
        s_Seed = (int)GetFloatArg("-verifySeed", -1f);
        s_EpisodeOutcomes = new List<string>();
        s_EpisodeStartFingerprints = new List<string>();
        RewardEventLog.Clear();

        Application.logMessageReceived += OnLog;

        // Keep static state (and this delegate) alive across the Play-mode transition,
        // which would otherwise wipe them via a domain reload.
        s_PrevEnterPlayModeOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
        s_PrevEnterPlayModeOptions = EditorSettings.enterPlayModeOptions;
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;

        EditorSceneManager.OpenScene(ScenePath);

        // Override MaxEpisodeTime *before* entering Play so episode 1 (not just later
        // episodes) uses the shortened duration too — GameScenario.Initialize()/EpisodeBegin()
        // read balanceConfig.MaxEpisodeTime when they run at Awake/Start. This mutates the
        // ScriptableObject asset only in memory; it's never saved back to disk (no
        // AssetDatabase.SaveAssets call anywhere in this file), so the .asset on disk is
        // untouched once this batch process exits.
        if (s_MaxEpisodeTimeOverride > 0f)
        {
            var editModeScenario = UnityEngine.Object.FindFirstObjectByType<GameScenario>();
            if (editModeScenario != null && editModeScenario.BalanceConfig != null)
            {
                editModeScenario.BalanceConfig.MaxEpisodeTime = s_MaxEpisodeTimeOverride;
            }
        }

        // Seed once, in edit mode, right before Play starts — covers episode 1's map
        // generation too (Awake/Start for frame 1 run before this harness's own Pump gets a
        // chance to touch anything). Deliberately NOT re-seeded every episode: the point is to
        // check whether the *same starting seed* produces the *same sequence* of Random.Range
        // calls across all episodes regardless of speed, not to force every episode to look
        // identical to every other episode within one run.
        if (s_Seed >= 0)
            UnityEngine.Random.InitState(s_Seed);

        EditorApplication.update += Pump;
        EditorApplication.isPlaying = true;
    }

    private static void Pump()
    {
        if (!EditorApplication.isPlaying) return;

        if (s_Counter == null)
        {
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            var go = new GameObject("__TickCounter");
            s_Counter = go.AddComponent<TickCounter>();
        }

        if (!s_SubscribedToGameEnded)
        {
            var scenario = UnityEngine.Object.FindFirstObjectByType<GameScenario>();
            if (scenario != null && scenario.EventBus != null)
            {
                s_MaxEpisodeTimeUsed = scenario.BalanceConfig != null ? scenario.BalanceConfig.MaxEpisodeTime : -1f;
                scenario.EventBus.Flow.OnGameEnded += winner =>
                {
                    s_EpisodeEndTicks.Add(s_Counter.Ticks);
                    s_EpisodeEndRealTime.Add(Time.realtimeSinceStartup - s_RealStart);
                    s_Trace.Add($"t={s_Counter.Ticks} OnGameEnded fired, winner={(winner == null ? "null" : winner.name)}");

                    var mm = scenario.MatchManager;
                    int scoreA = mm.GetTeamContext(mm.TeamA).Score;
                    int scoreB = mm.GetTeamContext(mm.TeamB).Score;
                    s_EpisodeOutcomes.Add($"t={s_Counter.Ticks} winner={(winner == null ? "Draw" : winner.name)} scoreA={scoreA} scoreB={scoreB}");
                };
                scenario.EventBus.Flow.OnEpisodeStarted += (mm, gs) =>
                {
                    s_Trace.Add($"t={s_Counter.Ticks} OnEpisodeStarted fired (episode #{++s_EpisodeStartedCount})");

                    // Fingerprint the freshly-generated world: all 10 unit spawn positions
                    // (rounded) plus active item count. If the RNG stays in sync between two
                    // runs (same seed, same sequence of Random.Range consumers), this string
                    // should be byte-identical episode-for-episode regardless of Time.timeScale.
                    var units = mm.Units;
                    var parts = new List<string>();
                    for (int i = 0; i < units.Length; i++)
                    {
                        var p = units[i].GlobalPos;
                        parts.Add($"u{i}:({p.x:F2},{p.y:F2})");
                    }
                    int itemCount = gs.LevelDirector != null ? gs.LevelDirector.ActiveItems.Count : -1;
                    s_EpisodeStartFingerprints.Add($"ep#{s_EpisodeStartedCount} t={s_Counter.Ticks} items={itemCount} {string.Join(" ", parts)}");
                };
                s_SubscribedToGameEnded = true;

                s_Coordinator = UnityEngine.Object.FindFirstObjectByType<BlackOutEpisodeCoordinator>();
                if (s_Coordinator != null)
                {
                    var t = s_Coordinator.GetType();
                    s_EpisodeBeginCountField = t.GetField("episodeBeginCount", BindingFlags.NonPublic | BindingFlags.Instance);
                    s_AgentsField = t.GetField("agents", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (s_AgentsField?.GetValue(s_Coordinator) is Array agentsArr)
                        s_Trace.Add($"t=0 agents.Length={agentsArr.Length}");
                }

                s_MatchManager = scenario.MatchManager;
                if (s_MatchManager != null)
                    s_GameEndedField = s_MatchManager.GetType().GetField("gameEnded", BindingFlags.NonPublic | BindingFlags.Instance);

                // Dump who's actually subscribed to OnGameEnded right now (field-like C# events
                // compile to a private backing field with the same name as the event).
                var flowObj = scenario.EventBus.Flow;
                var onGameEndedBacking = flowObj.GetType().GetField("OnGameEnded", BindingFlags.NonPublic | BindingFlags.Instance);
                if (onGameEndedBacking?.GetValue(flowObj) is Delegate del)
                {
                    var subs = del.GetInvocationList().Select(d => $"{d.Target?.GetType().Name}.{d.Method.Name}");
                    s_Trace.Add($"t=0 OnGameEnded subscribers: [{string.Join(", ", subs)}]");
                }
                else
                {
                    s_Trace.Add("t=0 OnGameEnded subscribers: <none or reflection failed>");
                }
            }
        }

        // Poll+log state transitions every tick (cheap; this harness only runs for a few
        // hundred ticks). Direct field reads via reflection since these are private and this
        // is a throwaway diagnostic, not something worth adding public accessors for.
        if (s_Counter != null)
        {
            var scenario2 = UnityEngine.Object.FindFirstObjectByType<GameScenario>();
            if (scenario2 != null && scenario2.CurrentState != s_LastState)
            {
                s_Trace.Add($"t={s_Counter.Ticks} CurrentState: {s_LastState} -> {scenario2.CurrentState}");
                s_LastState = scenario2.CurrentState;
            }

            if (s_EpisodeBeginCountField != null && s_Coordinator != null)
            {
                var v = s_EpisodeBeginCountField.GetValue(s_Coordinator);
                if (!Equals(v, s_LastEpisodeBeginCount))
                {
                    s_Trace.Add($"t={s_Counter.Ticks} episodeBeginCount: {s_LastEpisodeBeginCount} -> {v}");
                    s_LastEpisodeBeginCount = v;
                }
            }

            if (s_GameEndedField != null && s_MatchManager != null)
            {
                var v = s_GameEndedField.GetValue(s_MatchManager);
                if (!Equals(v, s_LastGameEnded))
                {
                    s_Trace.Add($"t={s_Counter.Ticks} MatchManager.gameEnded: {s_LastGameEnded} -> {v}");
                    s_LastGameEnded = v;
                }
            }

            int rewardLogCount = RewardEventLog.Entries.Count;
            if (rewardLogCount != s_LastRewardLogCount)
            {
                s_Trace.Add($"t={s_Counter.Ticks} RewardEventLog.Entries.Count: {s_LastRewardLogCount} -> {rewardLogCount}");
                s_LastRewardLogCount = rewardLogCount;
            }

            // Unconditional periodic snapshot (not just change-detection) so a same-tick
            // flip-flop that change-detection could theoretically miss still shows up.
            if (s_Counter.Ticks % 20 == 0 && s_Counter.Ticks != s_LastSnapshotTick)
            {
                s_LastSnapshotTick = s_Counter.Ticks;
                var ebc = s_EpisodeBeginCountField?.GetValue(s_Coordinator);
                var ge = s_GameEndedField?.GetValue(s_MatchManager);
                var et = scenario2.EpisodeTimer;
                var at = scenario2.AbsorptionTimer;
                int timerCount = -1;
                bool etInList = false, atInList = false;
                if (s_TimersField == null && scenario2.TimerManager != null)
                    s_TimersField = scenario2.TimerManager.GetType().GetField("timers", BindingFlags.NonPublic | BindingFlags.Instance);
                if (s_TimersField != null && scenario2.TimerManager != null && s_TimersField.GetValue(scenario2.TimerManager) is System.Collections.IEnumerable coll)
                {
                    foreach (var item in coll)
                    {
                        timerCount = (timerCount < 0 ? 0 : timerCount) + 1;
                        if (ReferenceEquals(item, et)) etInList = true;
                        if (ReferenceEquals(item, at)) atInList = true;
                    }
                }
                s_Trace.Add($"t={s_Counter.Ticks} SNAPSHOT state={scenario2.CurrentState} episodeBeginCount={ebc} gameEnded={ge} " +
                    $"episodeTimer={(et != null ? $"{et.CurrentTime:F2}/{et.Duration:F2}(inList={etInList})" : "null")} " +
                    $"absorptionTimer={(at != null ? $"{at.CurrentTime:F2}/{at.Duration:F2}(inList={atInList})" : "null")} timerCount={timerCount}");
            }
        }

        if (!s_TimeScaleApplied)
        {
            Time.timeScale = s_TargetTimeScale;
            s_RealStart = Time.realtimeSinceStartup;
            s_TimeScaleApplied = true;
        }

        if (Time.realtimeSinceStartup - s_RealStart >= s_Duration)
        {
            Finish();
        }
    }

    private static void Finish()
    {
        EditorApplication.update -= Pump;
        Application.logMessageReceived -= OnLog;

        var result = new Result
        {
            requestedTimeScale = s_TargetTimeScale,
            requestedDurationSec = s_Duration,
            realElapsedSec = Time.realtimeSinceStartup - s_RealStart,
            gameTimeElapsedSec = Time.time,
            fixedUpdateTicks = s_Counter != null ? s_Counter.Ticks : -1,
            fixedDeltaTime = Time.fixedDeltaTime,
            maxEpisodeTimeUsed = s_MaxEpisodeTimeUsed,
            episodeEndTicks = s_EpisodeEndTicks.ToArray(),
            episodeEndRealTime = s_EpisodeEndRealTime.ToArray(),
            errorCount = s_Errors.Count,
            errors = s_Errors.Take(20).ToArray(),
            trace = s_Trace.ToArray(),
            seedUsed = s_Seed,
            episodeOutcomes = s_EpisodeOutcomes.ToArray(),
            episodeStartFingerprints = s_EpisodeStartFingerprints.ToArray(),
        };

        File.WriteAllText(s_OutFile, JsonUtility.ToJson(result, true));

        EditorSettings.enterPlayModeOptionsEnabled = s_PrevEnterPlayModeOptionsEnabled;
        EditorSettings.enterPlayModeOptions = s_PrevEnterPlayModeOptions;

        EditorApplication.isPlaying = false;
        EditorApplication.Exit(0);
    }

    private static void OnLog(string condition, string stackTrace, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            s_Errors.Add($"[{type}] {condition}");
    }

    private static float GetFloatArg(string name, float fallback)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name && float.TryParse(args[i + 1], out var v)) return v;
        return fallback;
    }

    private static string GetStringArg(string name, string fallback)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return fallback;
    }
}
