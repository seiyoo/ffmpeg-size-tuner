namespace FfmpegSizeTuner.Core;

public sealed class EncodePlanException(string message) : Exception(message);

/// <summary>設定と素材から確定した、実際に使う数値一式。</summary>
public sealed record EncodePlan
{
    public required MediaInfo Source { get; init; }
    public required EncodeSettings Settings { get; init; }

    public int OutWidth { get; init; }
    public int OutHeight { get; init; }
    public string? ScaleFilter { get; init; }

    public double AudioKbps { get; init; }
    public bool CopyAudio { get; init; }

    public double TargetKbps { get; init; }
    public double TargetBpp { get; init; }

    /// <summary>Mode=Size のときの目標。Mode=Quality なら 0(サイズは結果として決まる)。</summary>
    public double TargetGiB { get; init; }
    public bool SizeBased => TargetGiB > 0;
    public string Basis => SizeBased ? "サイズ指定" : "画質指定";

    /// <summary>素材のビットレートを超えたため頭打ちにしたか。</summary>
    public bool Clamped { get; init; }
    public double CapKbps { get; init; }

    public bool TwoPass => Settings.Method == EncodeMethod.TwoPass;
    public int Maxrate => (int)Math.Round(TargetKbps * 1.4);
    public int Bufsize => Maxrate * 2;

    /// <summary>試し焼きで確定した CRF。2 パスでは使わない。</summary>
    public double Crf { get; set; }

    public double EstimatedGiB => BitrateModel.EstimateGiB(TargetKbps, AudioKbps, Source.DurationSec);

    /// <summary>この設定で到達する画質の帯。</summary>
    public QualityBand Band => BitrateModel.BandFor(TargetBpp, OutHeight, Source.Fps);
    public QualityGrade Grade => Band.Grade;

    /// <summary>素材の映像ビットレートに対する割合。</summary>
    public double SourceRatio => Source.VideoKbps > 0 ? TargetKbps / Source.VideoKbps : 0;
}

public static class EncodePlanner
{
    public static EncodePlan Build(MediaInfo src, EncodeSettings st)
    {
        int outW = src.Width, outH = src.Height;
        string? scale = null;
        if (st.TargetHeight > 0 && st.TargetHeight < src.Height)
        {
            outH = st.TargetHeight;
            outW = (int)(Math.Round(src.Width * (double)st.TargetHeight / src.Height / 2) * 2);
            scale = $"scale=-2:{st.TargetHeight}:flags=lanczos";
        }

        double audioKbps = st.CopyAudio && src.AudioKbps > 0
            ? Math.Round(src.AudioKbps)
            : st.AudioKbps;

        double targetGiB = 0;
        double targetKbps;

        if (st.Mode == TargetMode.Size)
        {
            targetGiB = st.TargetGiB;
            if (targetGiB <= 0 || double.IsNaN(targetGiB))
                throw new EncodePlanException("目標サイズの指定が不正です");

            targetKbps = Math.Round(BitrateModel.VideoKbpsForSize(targetGiB, src.DurationSec, audioKbps));
            if (targetKbps <= 50)
                throw new EncodePlanException($"{targetGiB:N2} GiB はこの尺には小さすぎます");
        }
        else
        {
            if (st.TargetNormalizedBpp <= 0 || double.IsNaN(st.TargetNormalizedBpp))
                throw new EncodePlanException("目標画質の指定が不正です");

            double bpp = BitrateModel.Denormalize(st.TargetNormalizedBpp, outH, src.Fps);
            targetKbps = Math.Round(BitrateModel.KbpsFromBpp(bpp, outW, outH, src.Fps));
            if (targetKbps <= 50)
                throw new EncodePlanException("目標画質がこの素材には低すぎます");
        }

        // 素材の映像ビットレートを超えても画質は上がらないので頭打ちにする
        bool clamped = false;
        double cap = 0;
        if (src.VideoKbps > 0)
        {
            cap = Math.Round(src.VideoKbps * BitrateModel.SourceCapRatio);
            if (targetKbps > cap) { targetKbps = cap; clamped = true; }
        }

        return new EncodePlan
        {
            Source = src,
            Settings = st,
            OutWidth = outW,
            OutHeight = outH,
            ScaleFilter = scale,
            AudioKbps = audioKbps,
            CopyAudio = st.CopyAudio,
            TargetKbps = targetKbps,
            TargetBpp = BitrateModel.BppFromKbps(targetKbps, outW, outH, src.Fps),
            TargetGiB = targetGiB,
            Clamped = clamped,
            CapKbps = cap,
        };
    }
}
