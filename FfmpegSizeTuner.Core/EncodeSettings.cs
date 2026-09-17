namespace FfmpegSizeTuner.Core;

/// <summary>
/// 何を固定するか。スライダーは同じ 1 本だが、キューに入れたとき
/// Size は全ファイルが同じサイズに、Quality は尺が違っても同じ画質になる。
/// </summary>
public enum TargetMode { Size, Quality }

public enum EncodeMethod { CappedCrf, TwoPass }

/// <summary>画面で指定する設定。キューの各項目がこれを 1 つずつ持つ。</summary>
public sealed record EncodeSettings
{
    public TargetMode Mode { get; init; } = TargetMode.Size;

    /// <summary>Mode=Size のときの目標サイズ(GiB)。0.10 刻み。</summary>
    public double TargetGiB { get; init; } = 1.8;

    /// <summary>Mode=Quality のときの目標画質(720p/30fps/x265 基準の正規化 bpp)。</summary>
    public double TargetNormalizedBpp { get; init; } = BitrateModel.DefaultNormalizedBpp;

    public EncodeMethod Method { get; init; } = EncodeMethod.CappedCrf;

    /// <summary>出力解像度の高さ。0 なら等倍。素材より大きい値は無視される。</summary>
    public int TargetHeight { get; init; }

    public string Preset { get; init; } = "medium";
    public int AudioKbps { get; init; } = 128;
    public bool CopyAudio { get; init; }

    public NameMode NameMode { get; init; } = NameMode.Suffix;
    public string Suffix { get; init; } = "_cmp";
    public string ExplicitName { get; init; } = "";

    /// <summary>キュー一覧の「目標」列。</summary>
    public string TargetLabel => Mode == TargetMode.Size
        ? $"{TargetGiB:N2} GiB"
        : BitrateModel.BandOf(TargetNormalizedBpp).Name;

    public string HeightLabel => TargetHeight <= 0 ? "元のまま" : $"{TargetHeight}p";
    public string MethodLabel => Method == EncodeMethod.TwoPass ? "2パス" : "CRF";
}
