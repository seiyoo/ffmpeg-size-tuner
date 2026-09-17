using System.Globalization;
using System.Text.RegularExpressions;

namespace FfmpegSizeTuner.Core;

public sealed record EncodeProgress(double SecondsDone, double TotalSec, double Speed)
{
    public double Percent => TotalSec > 0 ? Math.Clamp(SecondsDone / TotalSec * 100.0, 0, 100) : 0;

    public TimeSpan? Eta => Speed > 0 && TotalSec > SecondsDone
        ? TimeSpan.FromSeconds((TotalSec - SecondsDone) / Speed)
        : null;
}

public sealed class FfmpegException(string message) : Exception(message);

public static partial class Ffmpeg
{
    [GeneratedRegex(@"^out_time_us=(-?\d+)$")]
    private static partial Regex OutTimeRegex();

    [GeneratedRegex(@"^speed=\s*([\d.]+)x$")]
    private static partial Regex SpeedRegex();

    /// <summary>
    /// ffmpeg を実行する。進捗は -progress pipe:1 の出力を stdout から読み取る。
    /// 失敗時は stderr の末尾を添えて例外にする。
    /// </summary>
    public static async Task RunAsync(
        IReadOnlyList<string> args,
        double totalSec,
        IProgress<EncodeProgress>? progress = null,
        Action<string>? log = null,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        double speed = 0;

        void OnStdOut(string line)
        {
            var m = SpeedRegex().Match(line);
            if (m.Success)
            {
                speed = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                return;
            }
            m = OutTimeRegex().Match(line);
            if (m.Success && progress is not null)
            {
                double sec = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1_000_000.0;
                if (sec >= 0) progress.Report(new EncodeProgress(sec, totalSec, speed));
            }
        }

        var result = await ProcessRunner.RunAsync(
            "ffmpeg", args, workingDirectory, OnStdOut, log, ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            var tail = string.Join(Environment.NewLine,
                result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(6));
            throw new FfmpegException($"ffmpeg が異常終了しました (exit {result.ExitCode})\n{tail}");
        }
    }

    /// <summary>ffmpeg / ffprobe が PATH にあるか。</summary>
    public static bool IsAvailable()
    {
        foreach (var exe in new[] { "ffmpeg", "ffprobe" })
        {
            try
            {
                var r = ProcessRunner.RunAsync(exe, new[] { "-version" }).GetAwaiter().GetResult();
                if (r.ExitCode != 0) return false;
            }
            catch { return false; }
        }
        return true;
    }
}
