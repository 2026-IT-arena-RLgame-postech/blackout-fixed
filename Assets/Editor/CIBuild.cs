using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class CIBuild
{
    public static void BuildBlackOutMac()
    {
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = "/Users/mac/project/26rl/blackout-env/build/mac/BlackOut.app",
            target = BuildTarget.StandaloneOSX,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
        {
            UnityEngine.Debug.LogError($"Build failed: {report.summary.result}, errors={report.summary.totalErrors}");
            EditorApplication.Exit(1);
        }
        else
        {
            UnityEngine.Debug.Log($"Build succeeded: {report.summary.outputPath}, size={report.summary.totalSize}");
        }
    }
}
