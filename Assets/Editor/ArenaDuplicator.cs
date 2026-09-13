using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Batchmode tooling to build a second, independent scene that runs several copies of the
/// single-match "Prototype" arena side by side in one Unity process, so ML-Agents' communicator
/// batches every arena's decisions into one gRPC round-trip instead of needing one process (and
/// one round-trip) per match -- see docs/perf_experiments_20260913.md in blackout-env for why
/// that round-trip, not payload size or Time.timeScale, is the actual per-step bottleneck.
///
/// Deliberately never touches Prototype.unity or the existing build/mac/BlackOut.app: everything
/// here reads/writes a *copy* (PrototypeMultiArena.unity) and a *separate* build output
/// (build/mac_multiarena/BlackOut.app), so the original single-arena pipeline is unaffected and
/// rollback is just "delete the new scene/build files".
///
/// Usage (from repo root, same pattern as the existing build/mac_build.log invocation):
///   /Applications/Unity/Hub/Editor/6000.4.11f1/Unity.app/Contents/MacOS/Unity \
///     -batchmode -nographics -quit -projectPath /Users/mac/project/26rl/blackout \
///     -executeMethod ArenaDuplicator.ListRootObjects -logFile -
///
///   ARENA_COUNT=4 /Applications/Unity/Hub/Editor/6000.4.11f1/Unity.app/Contents/MacOS/Unity \
///     -batchmode -nographics -quit -projectPath /Users/mac/project/26rl/blackout \
///     -executeMethod ArenaDuplicator.GenerateAndBuild -logFile -
/// </summary>
public static class ArenaDuplicator
{
    const string SourceScenePath = "Assets/Project/Runtime/Scenes/Prototype.unity";
    const string MultiArenaScenePath = "Assets/Project/Runtime/Scenes/PrototypeMultiArena.unity";
    const string MultiArenaBuildPath = "/Users/mac/project/26rl/blackout-env/build/mac_multiarena/BlackOut.app";

    // Root GameObjects that make up one self-contained arena (confirmed via ListRootObjects,
    // not assumed -- the actual hierarchy is flatter than the initial design doc guessed):
    //   Grid      -- Tilemap_Terrain, DebugMap
    //   Managers  -- GameScenario (on itself); children Map (MapManager+ProceduralMapGenerator),
    //                MapObsAgent (BehaviorParameters+MapObsAgent+RenderTextureSensorComponent),
    //                Systems (MatchManager+LevelDirector+BlackOutEpisodeCoordinator+
    //                SemanticMapRenderer), Views (TeamAreaViewer), Visual (UnitVisualManager)
    //   Controls  -- inactive by default (PlayerUnitController+GameBootstrapper, the non-ML
    //                manual-play path); duplicated for structural completeness, but inert
    //   Objects   -- children Units (10 Unit prefabs) and Items (runtime battery/item parent)
    // Everything here is either [SerializeField]-wired or does its own coordinate math relative
    // to its own Transform, so duplicating this whole group and moving the copy's root Transform
    // is enough; no per-arena C# code changes are needed. UnitVisualManager (nested under
    // Managers/Visual) is a DontDestroyOnLoad singleton -- arenas 1..N-1's copy self-destroys in
    // Awake since Instance is already set, which is harmless: the shared TeamData/UnitData
    // ScriptableObject config it serves is identical across every arena anyway.
    static readonly string[] ArenaRootNames = { "Grid", "Managers", "Controls", "Objects" };

    // Left as single shared objects, not duplicated: Main Camera, Canvas (UI), EventSystem --
    // all presentation/input plumbing, irrelevant to headless multi-arena training.

    /// <summary>Diagnostic, read-only: logs the source scene's root GameObjects so the
    /// duplication logic below can be checked against the real hierarchy before it runs.
    /// Never saves anything.</summary>
    public static void ListRootObjects()
    {
        string path = Environment.GetEnvironmentVariable("SCENE_PATH");
        if (string.IsNullOrEmpty(path)) path = SourceScenePath;
        Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        foreach (GameObject root in scene.GetRootGameObjects())
            LogTree(root.transform, 0, maxDepth: 3);
    }

    static void LogTree(Transform t, int depth, int maxDepth)
    {
        var comps = t.GetComponents<Component>()
            .Where(c => c != null && !(c is Transform))
            .Select(c => c.GetType().Name);
        string indent = new string(' ', depth * 2);
        Debug.Log($"[ArenaDuplicator] {indent}'{t.name}' active={t.gameObject.activeSelf} "
            + $"pos={t.position} children={t.childCount} components=[{string.Join(",", comps)}]");
        if (depth >= maxDepth) return;
        for (int i = 0; i < t.childCount; i++)
            LogTree(t.GetChild(i), depth + 1, maxDepth);
    }

    /// <summary>
    /// Builds Assets/.../PrototypeMultiArena.unity with N side-by-side copies of the arena group
    /// and immediately builds it to build/mac_multiarena/BlackOut.app. N comes from the
    /// ARENA_COUNT env var (default 4). Idempotent: re-running regenerates the scene from a
    /// fresh copy of Prototype.unity each time rather than compounding onto a previous run.
    /// </summary>
    public static void GenerateAndBuild()
    {
        int count = 4;
        string envCount = Environment.GetEnvironmentVariable("ARENA_COUNT");
        if (!string.IsNullOrEmpty(envCount) && !int.TryParse(envCount, out count))
        {
            Debug.LogError($"[ArenaDuplicator] ARENA_COUNT='{envCount}' is not an integer");
            EditorApplication.Exit(1);
            return;
        }

        try
        {
            GenerateScene(count, spacing: 100f);
            BuildMultiArenaPlayer();
        }
        catch (Exception e)
        {
            Debug.LogError($"[ArenaDuplicator] failed: {e}");
            EditorApplication.Exit(1);
        }
    }

    /// <summary>Scene generation only, no build -- lets us inspect/verify the generated scene
    /// (e.g. via ListRootObjects on the new path, or opening it in the Editor GUI) before
    /// spending time on a full player build.</summary>
    public static void GenerateSceneOnly()
    {
        int count = 4;
        string envCount = Environment.GetEnvironmentVariable("ARENA_COUNT");
        if (!string.IsNullOrEmpty(envCount) && !int.TryParse(envCount, out count))
        {
            Debug.LogError($"[ArenaDuplicator] ARENA_COUNT='{envCount}' is not an integer");
            EditorApplication.Exit(1);
            return;
        }
        GenerateScene(count, spacing: 100f);
    }

    static void GenerateScene(int count, float spacing)
    {
        if (count < 1)
            throw new ArgumentException($"count must be >= 1, got {count}");

        // Work on a fresh on-disk copy so Prototype.unity itself is never opened for editing,
        // let alone saved -- zero risk of touching the original scene file.
        string fullMultiArenaPath = Path.Combine(Directory.GetCurrentDirectory(), MultiArenaScenePath);
        if (File.Exists(fullMultiArenaPath))
            AssetDatabase.DeleteAsset(MultiArenaScenePath);
        bool copied = AssetDatabase.CopyAsset(SourceScenePath, MultiArenaScenePath);
        if (!copied)
            throw new IOException($"AssetDatabase.CopyAsset({SourceScenePath} -> {MultiArenaScenePath}) failed");

        Scene scene = EditorSceneManager.OpenScene(MultiArenaScenePath, OpenSceneMode.Single);

        GameObject[] roots = scene.GetRootGameObjects();
        var arenaRoots = ArenaRootNames
            .Select(name => roots.FirstOrDefault(r => r.name == name))
            .ToArray();
        for (int i = 0; i < ArenaRootNames.Length; i++)
            if (arenaRoots[i] == null)
                throw new Exception($"expected root GameObject '{ArenaRootNames[i]}' not found in {SourceScenePath} "
                    + $"-- actual roots: {string.Join(", ", roots.Select(r => r.name))}");

        // Group the arena's root objects under one new parent so moving/cloning is a single
        // Transform operation instead of N separate ones per copy.
        var arena0 = new GameObject("Arena_0");
        Undo.RegisterCreatedObjectUndo(arena0, "create arena root"); // batchmode: no-op, harmless
        foreach (GameObject go in arenaRoots)
            go.transform.SetParent(arena0.transform, worldPositionStays: true);

        for (int i = 1; i < count; i++)
        {
            GameObject clone = UnityEngine.Object.Instantiate(arena0);
            clone.name = $"Arena_{i}";
            clone.transform.position = arena0.transform.position + new Vector3(spacing * i, 0f, 0f);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        if (!saved)
            throw new IOException($"EditorSceneManager.SaveScene failed for {MultiArenaScenePath}");

        Debug.Log($"[ArenaDuplicator] generated {MultiArenaScenePath} with {count} arena(s), spacing={spacing}");
    }

    static void BuildMultiArenaPlayer()
    {
        string buildPath = MultiArenaBuildPath;
        string suffix = Environment.GetEnvironmentVariable("BUILD_SUFFIX");
        if (!string.IsNullOrEmpty(suffix))
            buildPath = buildPath.Replace("mac_multiarena/", $"mac_multiarena{suffix}/");

        var options = new BuildPlayerOptions
        {
            scenes = new[] { MultiArenaScenePath },
            locationPathName = buildPath,
            target = BuildTarget.StandaloneOSX,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
        {
            Debug.LogError($"[ArenaDuplicator] build failed: {report.summary.result}, errors={report.summary.totalErrors}");
            EditorApplication.Exit(1);
        }
        else
        {
            Debug.Log($"[ArenaDuplicator] build succeeded: {report.summary.outputPath}, size={report.summary.totalSize}");
        }
    }
}
