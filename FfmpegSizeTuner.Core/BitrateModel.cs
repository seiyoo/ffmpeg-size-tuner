namespace FfmpegSizeTuner.Core;

public enum QualityGrade
{
    ClearlyDegraded,   // 明確に劣化
    VisiblyDegraded,   // 劣化が見えやすい
    Practical,         // 実用十分
    NearTransparent,   // ほぼ判別不能
    Transparent,       // 差を探すのが困難
}

/// <summary>
/// サイズ・ビットレート・bpp・CRF の相互変換。PowerShell 版と同じ係数を使う。
/// </summary>
public static class BitrateModel
{
    public const double KbitPerGiB  = 8589934.592;  // 1 GiB を kbit で表した値
    public const double MuxOverhead = 1.005;        // MP4 コンテナのオーバーヘッド

    /// <summary>スライダーの既定位置(720p/30fps/x265 基準の bpp)。</summary>
    public const double DefaultNormalizedBpp = 0.0333;

    /// <summary>目標が素材のこの割合を超えたら頭打ちにする(情報が増えない領域)。</summary>
    public const double SourceCapRatio = 0.95;

    /// <summary>同じ体感画質に必要な bpp は解像度が上がるほど下がる(720p を 1.00 とした相対値)。</summary>
    public static double ResFactor(int height) =>
        height <= 720  ? 1.00 :
        height <= 1080 ? 0.90 :
        height <= 1440 ? 0.83 : 0.75;

    /// <summary>高フレームレートはフレーム間相関が高いので 1 フレームあたりは安く済む。</summary>
    public static double FpsFactor(double fps) => fps > 40 ? 0.70 : 1.00;

    /// <summary>参照ファイルのコデックから x265 換算係数を得る。</summary>
    public static double CodecGain(string codec) => codec switch
    {
        "hevc"       => 1.00,
        "av1"        => 1.25,
        "vp9"        => 1.05,
        "h264"       => 0.65,
        "vp8"        => 0.55,
        "mpeg4"      => 0.50,
        "mpeg2video" => 0.45,
        _            => 0.65,
    };

    public static double TargetBpp(double basisBpp, int outHeight, double fps) =>
        basisBpp * ResFactor(outHeight) * FpsFactor(fps);

    public static double KbpsFromBpp(double bpp, int width, int height, double fps) =>
        bpp * width * height * fps / 1000.0;

    public static double BppFromKbps(double kbps, int width, int height, double fps)
    {
        double px = (double)width * height * fps;
        return px > 0 ? kbps * 1000.0 / px : 0;
    }

    /// <summary>目標サイズ(GiB)から映像ビットレート(kbps)を求める。</summary>
    public static double VideoKbpsForSize(double targetGiB, double durationSec, double audioKbps) =>
        targetGiB * KbitPerGiB / durationSec / MuxOverhead - audioKbps;

    /// <summary>映像+音声のビットレートから出力サイズ(GiB)を見積もる。</summary>
    public static double EstimateGiB(double videoKbps, double audioKbps, double durationSec) =>
        (videoKbps + audioKbps) * durationSec * MuxOverhead / KbitPerGiB;

    /// <summary>bpp を 720p/30fps 基準に正規化する。解像度や fps が違う素材を同じ尺度で比べるため。</summary>
    public static double Normalize(double bpp, int height, double fps) =>
        bpp / ResFactor(height) / FpsFactor(fps);

    /// <summary>正規化 bpp を、その出力解像度での実 bpp に戻す。</summary>
    public static double Denormalize(double normalized, int height, double fps) =>
        normalized * ResFactor(height) * FpsFactor(fps);

    /// <summary>
    /// 画質の帯。境界は正規化 bpp で、実写動画を前提に置いている
    /// (アニメや CG は平坦な面が多く圧縮が効くため、同じ体感でもこれより下に来る)。
    /// </summary>
    public static readonly QualityBand[] Bands =
    {
        new(QualityGrade.ClearlyDegraded, 0, 0.022, "明確に劣化",
            "暗いシーンや動きの速い場面でブロック状のノイズが出ます。肌や髪の細部が溶け、背景の質感が失われます。保存用には向きません。"),
        new(QualityGrade.VisiblyDegraded, 0.022, 0.028, "劣化が見えやすい",
            "静かな場面は問題ありませんが、カメラが動く場面や暗部で細部が甘くなります。一度見返す程度の用途なら実用範囲です。"),
        new(QualityGrade.Practical, 0.028, 0.042, "実用十分",
            "通常の視聴では気になりません。元と並べて見比べると、細かい質感やグレインの再現にわずかな差がわかる程度です。"),
        new(QualityGrade.NearTransparent, 0.042, 0.065, "ほぼ判別不能",
            "元と並べても違いを指摘するのが難しい水準です。動きの激しい場面でもディテールが保たれます。保存用として安心できる領域です。"),
        new(QualityGrade.Transparent, 0.065, double.PositiveInfinity, "差を探すのが困難",
            "静止して拡大しても差を見つけにくい水準です。サイズの増加に対して得られるものは小さいので、再編集の素材として残す場合向けです。"),
    };

    public static QualityBand BandOf(double normalized) =>
        Bands.First(b => normalized < b.To);

    public static QualityBand BandFor(double bpp, int height, double fps) =>
        BandOf(Normalize(bpp, height, fps));

    public static QualityGrade Grade(double bpp, int height, double fps) =>
        BandFor(bpp, height, fps).Grade;

    public static string GradeLabel(QualityGrade g) =>
        Bands.First(b => b.Grade == g).Name;

    /// <summary>
    /// 参照ファイルの実測 bpp を、720p/30fps/x265 基準の bpp へ正規化する。
    /// </summary>
    public static double NormalizeReferenceBpp(double refVideoBpp, string refCodec, int refHeight, double refFps) =>
        refVideoBpp * CodecGain(refCodec) / ResFactor(refHeight) / FpsFactor(refFps);

    // ---- CRF ↔ ビットレート ----
    // log(bitrate) は CRF に対してほぼ線形。2 点測れば傾き k が決まる。

    public const double TheoreticalSlope = 0.11552453009332421; // ln(2)/6 : CRF+6 で半減

    public static double Slope(double crfLow, double kbpsLow, double crfHigh, double kbpsHigh)
    {
        if (kbpsLow <= 0 || kbpsHigh <= 0 || crfHigh <= crfLow || kbpsLow <= kbpsHigh)
            return TheoreticalSlope;
        return (Math.Log(kbpsLow) - Math.Log(kbpsHigh)) / (crfHigh - crfLow);
    }

    /// <summary>基準点の実測から、目標ビットレートに一致する CRF を逆算する。</summary>
    public static double SolveCrf(double refCrf, double refKbps, double targetKbps, double slope) =>
        refCrf + (Math.Log(refKbps) - Math.Log(targetKbps)) / slope;

    /// <summary>確認焼きの実測値で CRF を補正する。</summary>
    public static double CorrectCrf(double crf, double measuredKbps, double targetKbps, double slope) =>
        crf + Math.Log(measuredKbps / targetKbps) / slope;

    /// <summary>CRF を 1 上げたときのビットレート減衰率。</summary>
    public static double DecayPerCrf(double slope) => 1 - Math.Exp(-slope);
}
