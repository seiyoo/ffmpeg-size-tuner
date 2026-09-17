using System.Globalization;
using System.Text.Json;

namespace FfmpegSizeTuner.Core;

public sealed class MediaProbeException(string message) : Exception(message);

/// <summary>ffprobe を呼んで <see cref="MediaInfo"/> を組み立てる。</summary>
public static class MediaProbe
{
    private const string Entries =
        "stream=codec_name,codec_type,width,height,r_frame_rate,bit_rate,pix_fmt:format=duration,size";

    public static async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new MediaProbeException($"ファイルが見つかりません: {path}");

        var result = await ProcessRunner.RunAsync(
            "ffprobe",
            new[] { "-v", "error", "-show_entries", Entries, "-of", "json", path },
            ct: ct).ConfigureAwait(false);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
            throw new MediaProbeException("解析できませんでした（対応していない形式かもしれません）");

        using var doc = JsonDocument.Parse(result.StdOut);
        var root = doc.RootElement;

        JsonElement? video = null, audio = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                if (type == "video" && video is null) video = s;
                else if (type == "audio" && audio is null) audio = s;
            }
        }
        if (video is null) throw new MediaProbeException("映像ストリームが見つかりません");

        var format = root.GetProperty("format");
        double duration = ParseDouble(format, "duration");
        if (duration <= 0) throw new MediaProbeException("再生時間を取得できません");

        return new MediaInfo
        {
            FullName   = Path.GetFullPath(path),
            Width      = GetInt(video.Value, "width"),
            Height     = GetInt(video.Value, "height"),
            Fps        = ParseFrameRate(GetString(video.Value, "r_frame_rate")),
            DurationSec= duration,
            VideoCodec = GetString(video.Value, "codec_name") ?? "",
            PixFmt     = GetString(video.Value, "pix_fmt") ?? "",
            AudioCodec = audio is null ? null : GetString(audio.Value, "codec_name"),
            AudioKbps  = audio is null ? 0 : ParseDouble(audio.Value, "bit_rate") / 1000.0,
            SizeBytes  = (long)ParseDouble(format, "size"),
        };
    }

    /// <summary>"30000/1001" 形式の分数を fps に直す。</summary>
    public static double ParseFrameRate(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return 0;
        var parts = raw.Split('/');
        if (parts.Length != 2) return Parse(raw);
        double den = Parse(parts[1]);
        return den == 0 ? 0 : Parse(parts[0]) / den;
    }

    private static double Parse(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? p.GetString() : null;

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : 0;

    // ffprobe は数値も文字列で返すことがあるため両対応にする
    private static double ParseDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return 0;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetDouble(),
            JsonValueKind.String => Parse(p.GetString() ?? ""),
            _ => 0,
        };
    }
}
