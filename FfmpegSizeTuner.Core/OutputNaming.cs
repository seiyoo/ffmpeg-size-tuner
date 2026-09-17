using System.Text.RegularExpressions;

namespace FfmpegSizeTuner.Core;

public enum NameMode { Suffix, Explicit }

/// <summary>出力ファイル名の組み立て。既存ファイルは絶対に上書きしない。</summary>
public static class OutputNaming
{
    private static readonly Regex Invalid   = new(@"[\/:*?""<>|]");
    private static readonly Regex Extension = new(@"\.(mp4|mkv|mov|avi|m4v|ts|wmv|webm)$", RegexOptions.IgnoreCase);

    /// <summary>使えない文字を落とし、打たれた拡張子も無視する。</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var x = Invalid.Replace(text, string.Empty).Trim();
        x = Extension.Replace(x, string.Empty);
        return x.TrimEnd('.', ' ');
    }

    /// <summary>拡張子なしのファイル名を決める。決められないときは null。</summary>
    public static string? BuildStem(NameMode mode, string sourceBaseName, string? suffix, string? explicitName)
    {
        if (mode == NameMode.Explicit)
        {
            var stem = Sanitize(explicitName);
            return string.IsNullOrEmpty(stem) ? null : stem;
        }
        return (sourceBaseName + Sanitize(suffix)).TrimEnd('.', ' ');
    }

    /// <summary>同名があれば _2, _3 と連番にして、空いているパスを返す。</summary>
    public static string? Resolve(string directory, string? stem, Func<string, bool> exists)
    {
        if (string.IsNullOrEmpty(stem)) return null;
        var basePath = Path.Combine(directory, stem);
        var candidate = basePath + ".mp4";
        for (int n = 2; exists(candidate); n++)
            candidate = $"{basePath}_{n}.mp4";
        return candidate;
    }
}
