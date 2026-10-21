using UnityEngine;

/// <summary>
/// "-benchmark" 引数付きで起動された時だけ、シーン内で非アクティブな計測オブジェクトを有効にして計測を始める。
/// 引数が無ければ何もしない（エディタでの通常のPlayには影響しない）。
/// </summary>
public static class BenchmarkAutoStart
{
    public const string BenchmarkFlag = "-benchmark";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ActivateBenchmarkIfRequested()
    {
        if (!BenchmarkCommandLine.HasFlag(BenchmarkFlag)) return;

        // 計測値が垂直同期やフレーム上限で頭打ちにならないようにする
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        // 非アクティブなものも含めて計測の管理クラスを探す（見つからなければ計測できないので終了する）
        BenchmarkController[] controllers = Object.FindObjectsByType<BenchmarkController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (controllers.Length == 0)
        {
            Debug.LogError("BenchmarkAutoStart: シーンにBenchmarkControllerが無いため計測できません。");
            Application.Quit(1);
            return;
        }

        Debug.Log("BenchmarkAutoStart: 自動計測を開始します。");
        controllers[0].gameObject.SetActive(true);
    }
}
