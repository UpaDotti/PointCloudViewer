using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Unity CLI（-batchmode -executeMethod BenchmarkPlayerBuilder.Build）から、自動計測用のWindowsプレイヤーをビルドする。
/// 結果は終了コードで返す（0=成功、1=失敗）。
/// </summary>
public static class BenchmarkPlayerBuilder
{
    // 計測に使うシーン（OpcLoaderと非アクティブなBenchmarkを含む）
    private const string ScenePath = "Assets/Scenes/PointCloudViewer.unity";
    // 出力先（Builds/ はgitignore済み）
    private const string OutputPath = "Builds/Benchmark/PointCloudViewer.exe";

    public static void Build()
    {
        // 計測対象のシーンだけを、通常（非Development）ビルドで出す
        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = OutputPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        bool succeeded = report.summary.result == BuildResult.Succeeded;
        Debug.Log($"BenchmarkPlayerBuilder: {report.summary.result} ({OutputPath}, errors={report.summary.totalErrors})");

        // バッチモードではUnityを終了し、成否を終了コードで呼び出し元へ返す
        if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
    }
}
