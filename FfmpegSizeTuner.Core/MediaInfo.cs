namespace FfmpegSizeTuner.Core;

/// <summary>ffprobe で取得した素材の情報。</summary>
public sealed record MediaInfo
{
    public required string FullName { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public double DurationSec { get; init; }
    public string VideoCodec { get; init; } = "";
    public string PixFmt { get; init; } = "";
    public string? AudioCodec { get; init; }
    public double AudioKbps { get; init; }
    public long SizeBytes { get; init; }

    public string Directory => Path.GetDirectoryName(FullName) ?? "";
    public string BaseName  => Path.GetFileNameWithoutExtension(FullName);
    public string FileName  => Path.GetFileName(FullName);

    public double SizeGiB   => SizeBytes * 8.0 / 1000.0 / BitrateModel.KbitPerGiB;
    public double TotalKbps => DurationSec > 0 ? SizeBytes * 8.0 / 1000.0 / DurationSec : 0;
    public double VideoKbps => TotalKbps - AudioKbps;
    public double VideoBpp  => BitrateModel.BppFromKbps(VideoKbps, Width, Height, Fps);

    /// <summary>10bit/12bit 素材か。x265 も同じ深度で出力されるため互換性の注意が要る。</summary>
    public bool IsHighBitDepth =>
        PixFmt.Contains("10le") || PixFmt.Contains("10be") || PixFmt.Contains("12le");

    /// <summary>音声ビットレートが取れないコンテナ。bpp が実際より高めに出る。</summary>
    public bool AudioBitrateUnknown => AudioCodec is not null && AudioKbps <= 0;
}
