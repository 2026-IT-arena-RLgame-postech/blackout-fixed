using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class CIBuild
{
    // Default output: blackout-env/build/mac/BlackOut.app next to this Unity project (both repos cloned into the
    // same parent folder). Override with -buildPath <path to .app>.
    private static string OutputPath()
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "-buildPath");
        if (i >= 0 && i + 1 < args.Length) return Path.GetFullPath(args[i + 1]);
        string projectRoot = Directory.GetParent(UnityEngine.Application.dataPath).FullName;
        return Path.Combine(Directory.GetParent(projectRoot).FullName, "blackout-env", "build", "mac", "BlackOut.app");
    }

    public static void BuildBlackOutMac()
    {
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = OutputPath(),
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
