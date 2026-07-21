using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEditor.Profiling;

public static class ProfilerDataExporter
{
    [MenuItem("Tools/PointCloud/Export Loaded Profiler Data CSV")]
    private static void ExportLoadedProfilerData()
    {
        string path = EditorUtility.SaveFilePanel("Export Profiler CSV", "", "profiler-export.csv", "csv");
        if (string.IsNullOrEmpty(path)) return;
        var csv = new StringBuilder("frame,thread,sample,time_ms\n");
        for (int frame = ProfilerDriver.firstFrameIndex; frame <= ProfilerDriver.lastFrameIndex; frame++)
        {
            // スレッド0だけでなく、GPU/Render Threadを含む有効な全スレッドを読む。
            for (int thread = 0; thread < 64; thread++)
            {
                using (RawFrameDataView data = ProfilerDriver.GetRawFrameDataView(frame, thread))
                {
                    if (data == null || !data.valid) break;
                    for (int i = 0; i < data.sampleCount; i++)
                    {
                        string name = data.GetSampleName(i);
                        if (!IsRelevant(name)) continue;
                        csv.Append(frame).Append(',').Append(Escape(data.threadName)).Append(',')
                            .Append(Escape(name)).Append(',')
                            .Append(data.GetSampleTimeMs(i).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                    }
                }
            }
        }
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
        AssetDatabase.Refresh();
        Debug.Log($"Profiler CSV exported: {path}");
    }

    private static bool IsRelevant(string name) => !string.IsNullOrEmpty(name) &&
        (name.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0 ||
         name.IndexOf("Render", StringComparison.OrdinalIgnoreCase) >= 0 ||
         name.IndexOf("Draw", StringComparison.OrdinalIgnoreCase) >= 0 ||
         name.IndexOf("Opc", StringComparison.OrdinalIgnoreCase) >= 0 ||
         name.IndexOf("WaitFor", StringComparison.OrdinalIgnoreCase) >= 0 ||
         name.IndexOf("Present", StringComparison.OrdinalIgnoreCase) >= 0 ||
         name.IndexOf("VSync", StringComparison.OrdinalIgnoreCase) >= 0 ||
         name.IndexOf("Semaphore", StringComparison.OrdinalIgnoreCase) >= 0);

    private static string Escape(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
}
