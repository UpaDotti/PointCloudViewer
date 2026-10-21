using System;

/// <summary>
/// Unity CLI から渡されたコマンドライン引数（"-name value" / "-flag"）を読む。
/// 自動計測で描画モードや計測開始を切り替えるために使う。引数が無ければ何も上書きしない。
/// </summary>
public static class BenchmarkCommandLine
{
    /// <summary>"-name" という引数があるか（大文字小文字は区別しない）</summary>
    public static bool HasFlag(string name)
    {
        return IndexOf(name) >= 0;
    }

    /// <summary>"-name value" の value を返す。引数が無い、または値が続いていなければfalse。</summary>
    public static bool TryGetValue(string name, out string value)
    {
        value = null;
        string[] args = Environment.GetCommandLineArgs();

        // 名前の直後の要素を値とみなす（次も"-"で始まるなら値は無い）
        int index = IndexOf(name);
        if (index < 0 || index + 1 >= args.Length) return false;
        if (args[index + 1].StartsWith("-", StringComparison.Ordinal)) return false;

        value = args[index + 1];
        return true;
    }

    // 引数配列の中で name が最初に現れる位置（無ければ-1）
    private static int IndexOf(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }
}
