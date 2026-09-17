namespace FfmpegSizeTuner.Core;

/// <summary>ffmpeg の引数組み立て。試し焼きと本番で同じパラメータを使うことが精度の前提。</summary>
public static class FfmpegArgs
{
    public const string X265Quiet = "log-level=none:aq-mode=3";
    public const string X265Final = "aq-mode=3";

    /// <summary>測定用。生の hevc を吐き音声なし。コンテナ分の誤差を避ける。</summary>
    public static List<string> Probe(EncodePlan p, double crf, double seekSec, int lengthSec, string outPath)
    {
        var a = Common(p, seekSec, lengthSec);
        a.AddRange(new[] { "-map", "0:v:0" });
        AddScale(a, p);
        a.AddRange(new[] { "-c:v", "libx265", "-preset", p.Settings.Preset,
                           "-crf", crf.ToString("F1"),
                           "-x265-params", X265Quiet, "-an", "-f", "hevc", outPath });
        return a;
    }

    /// <summary>本番前に見せる短いサンプル。本番と同一設定で音声つきの mp4。</summary>
    public static List<string> Sample(EncodePlan p, double seekSec, int lengthSec, string outPath)
    {
        var a = Common(p, seekSec, lengthSec);
        AddScale(a, p);
        a.AddRange(new[] { "-c:v", "libx265", "-preset", p.Settings.Preset });
        if (p.TwoPass)
        {
            // 2 パスは短尺で再現できないため、同じ目標ビットレートの 1 パス ABR で近似する
            a.AddRange(new[] { "-b:v", $"{(int)p.TargetKbps}k" });
        }
        else
        {
            a.AddRange(new[] { "-crf", p.Crf.ToString("F1"),
                               "-maxrate", $"{p.Maxrate}k", "-bufsize", $"{p.Bufsize}k" });
        }
        a.AddRange(new[] { "-x265-params", X265Final });
        AddAudio(a, p);
        a.AddRange(new[] { "-tag:v", "hvc1", "-movflags", "+faststart", outPath });
        return a;
    }

    public static List<string> Final(EncodePlan p, string outPath)
    {
        var a = Common(p, -1, 0);
        AddScale(a, p);
        a.AddRange(new[] { "-c:v", "libx265", "-preset", p.Settings.Preset,
                           "-crf", p.Crf.ToString("F1"),
                           "-maxrate", $"{p.Maxrate}k", "-bufsize", $"{p.Bufsize}k",
                           "-x265-params", X265Final });
        AddAudio(a, p);
        a.AddRange(new[] { "-tag:v", "hvc1", "-movflags", "+faststart", outPath });
        return a;
    }

    public static List<string> Pass1(EncodePlan p)
    {
        var a = Common(p, -1, 0);
        AddScale(a, p);
        a.AddRange(new[] { "-c:v", "libx265", "-preset", p.Settings.Preset,
                           "-b:v", $"{(int)p.TargetKbps}k",
                           "-x265-params", $"{X265Final}:pass=1", "-an", "-f", "null", "NUL" });
        return a;
    }

    public static List<string> Pass2(EncodePlan p, string outPath)
    {
        var a = Common(p, -1, 0);
        AddScale(a, p);
        a.AddRange(new[] { "-c:v", "libx265", "-preset", p.Settings.Preset,
                           "-b:v", $"{(int)p.TargetKbps}k",
                           "-x265-params", $"{X265Final}:pass=2" });
        AddAudio(a, p);
        a.AddRange(new[] { "-tag:v", "hvc1", "-movflags", "+faststart", outPath });
        return a;
    }

    // -progress pipe:1 で進捗を stdout に流す。PowerShell 版のファイル監視より確実。
    private static List<string> Common(EncodePlan p, double seekSec, int lengthSec)
    {
        var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" };
        if (seekSec >= 0)
        {
            a.AddRange(new[] { "-ss", seekSec.ToString("F3"), "-t", lengthSec.ToString() });
        }
        a.AddRange(new[] { "-i", p.Source.FullName });
        return a;
    }

    private static void AddScale(List<string> a, EncodePlan p)
    {
        if (p.ScaleFilter is not null) a.AddRange(new[] { "-vf", p.ScaleFilter });
    }

    private static void AddAudio(List<string> a, EncodePlan p)
    {
        if (p.CopyAudio) a.AddRange(new[] { "-c:a", "copy" });
        else a.AddRange(new[] { "-c:a", "aac", "-b:a", $"{(int)p.AudioKbps}k", "-ac", "2" });
    }
}
