namespace FfmpegSizeTuner.Core;

/// <summary>
/// 画質の帯。<paramref name="From"/> / <paramref name="To"/> は正規化 bpp
/// (720p/30fps/x265 基準)。<paramref name="Description"/> は「そのサイズにすると
/// 実際どう見えるか」を実写動画前提で書いたもの。
/// </summary>
public sealed record QualityBand(
    QualityGrade Grade,
    double From,
    double To,
    string Name,
    string Description);

/// <summary>
/// 目標サイズスライダーの目盛り。0.10 GiB 刻みで、下端は「明確に劣化」より
/// 少し下、上端は素材の映像ビットレートの 95%(それ以上は情報が増えない)。
/// </summary>
public static class SizeScale
{
    public const double Step = 0.10;

    /// <summary>スライダー下端の正規化 bpp。これ未満は実用外なので刻まない。</summary>
    public const double FloorNormalizedBpp = 0.015;

    public sealed record Range(double MinGiB, double MaxGiB, int Steps)
    {
        public double GiBAt(int step) => Math.Round(MinGiB + step * Step, 2);

        public int StepOf(double gib) =>
            Math.Clamp((int)Math.Round((gib - MinGiB) / Step), 0, Steps);

        /// <summary>0〜1 の位置。帯の描画に使う。</summary>
        public double PositionOf(double gib) =>
            MaxGiB > MinGiB ? Math.Clamp((gib - MinGiB) / (MaxGiB - MinGiB), 0, 1) : 0;
    }

    public static Range For(MediaInfo src, int outWidth, int outHeight, double audioKbps)
    {
        double lowKbps = BitrateModel.KbpsFromBpp(
            BitrateModel.Denormalize(FloorNormalizedBpp, outHeight, src.Fps),
            outWidth, outHeight, src.Fps);

        double highKbps = src.VideoKbps > 0
            ? src.VideoKbps * BitrateModel.SourceCapRatio
            : lowKbps * 8;

        double min = Snap(BitrateModel.EstimateGiB(lowKbps, audioKbps, src.DurationSec), up: true);
        double max = Snap(BitrateModel.EstimateGiB(highKbps, audioKbps, src.DurationSec), up: false);

        min = Math.Max(Step, min);
        if (max < min + Step) max = min + Step;

        return new Range(min, max, (int)Math.Round((max - min) / Step));
    }

    /// <summary>目標サイズに対応する帯を返す。</summary>
    public static QualityBand BandAt(double gib, MediaInfo src, int outWidth, int outHeight, double audioKbps)
    {
        double kbps = BitrateModel.VideoKbpsForSize(gib, src.DurationSec, audioKbps);
        double bpp = BitrateModel.BppFromKbps(Math.Max(0, kbps), outWidth, outHeight, src.Fps);
        return BitrateModel.BandFor(bpp, outHeight, src.Fps);
    }

    /// <summary>帯の境界を GiB に直す。上端は無限なのでスライダー上端で打ち止める。</summary>
    public static double BoundaryGiB(double normalized, MediaInfo src, int outWidth, int outHeight,
                                     double audioKbps, Range range)
    {
        if (double.IsPositiveInfinity(normalized)) return range.MaxGiB;
        double kbps = BitrateModel.KbpsFromBpp(
            BitrateModel.Denormalize(normalized, outHeight, src.Fps), outWidth, outHeight, src.Fps);
        return Math.Clamp(
            BitrateModel.EstimateGiB(kbps, audioKbps, src.DurationSec), range.MinGiB, range.MaxGiB);
    }

    private static double Snap(double gib, bool up) =>
        Math.Round((up ? Math.Ceiling(gib / Step) : Math.Floor(gib / Step)) * Step, 2);
}
