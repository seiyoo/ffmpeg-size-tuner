namespace FfmpegSizeTuner.Core;

/// <summary>進捗の通知単位。ラベルは「試し焼き CRF24」「本番エンコード」など。</summary>
public sealed record PhaseProgress(string Label, int StepIndex, int StepCount, EncodeProgress Progress);

/// <summary>
/// 試し焼き → CRF 逆算 → 確認焼き → 再補正 → サンプル → 本番、の一連を実行する。
/// UI から独立しているので単体で試験できる。
/// </summary>
public sealed class EncodePipeline
{
    public int SampleCount { get; init; } = 2;
    public int SampleSeconds { get; init; } = 20;
    public double[] ProbeCrfs { get; init; } = { 24, 29 };
    public bool Verify { get; init; } = true;
    public int PreviewSeconds { get; init; } = 30;

    /// <summary>実用域を外れた CRF はこの範囲に丸めず、警告だけ出す。</summary>
    public const double MinUsableCrf = 16;
    public const double MaxUsableCrf = 34;

    public event Action<string>? Log;
    public event Action<PhaseProgress>? Progressed;

    private int _step, _stepCount;

    private void Say(string msg) => Log?.Invoke(msg);

    private IProgress<EncodeProgress> Reporter(string label) =>
        new Progress<EncodeProgress>(p => Progressed?.Invoke(new PhaseProgress(label, _step, _stepCount, p)));

    /// <summary>素材の 20%〜80% に等間隔で測定位置を取る。</summary>
    public IEnumerable<double> SamplePositions(double durationSec)
    {
        if (SampleCount <= 1) { yield return durationSec * 0.4; yield break; }
        for (int i = 0; i < SampleCount; i++)
            yield return durationSec * (0.2 + 0.6 * i / (SampleCount - 1));
    }

    /// <summary>指定 CRF で短いサンプルを焼き、実効ビットレート(kbps)を返す。</summary>
    private async Task<double> MeasureAsync(EncodePlan plan, double crf, string label, CancellationToken ct)
    {
        long totalBytes = 0;
        int totalSec = 0;
        int i = 0;
        foreach (var pos in SamplePositions(plan.Source.DurationSec))
        {
            ct.ThrowIfCancellationRequested();
            _step++;
            var tmp = Path.Combine(Path.GetTempPath(), $"fst_probe_{crf:F1}_{i}.hevc");
            try
            {
                await Ffmpeg.RunAsync(
                    FfmpegArgs.Probe(plan, crf, pos, SampleSeconds, tmp),
                    SampleSeconds, Reporter(label), null, null, ct).ConfigureAwait(false);

                var len = new FileInfo(tmp).Length;
                if (len <= 0) throw new FfmpegException($"試し焼きの出力が空でした (crf={crf:F1})");
                totalBytes += len;
                totalSec += SampleSeconds;
            }
            finally
            {
                TryDelete(tmp);
            }
            i++;
        }
        return totalBytes * 8.0 / 1000.0 / totalSec;
    }

    /// <summary>試し焼きから目標ビットレートに一致する CRF を確定し、plan.Crf に入れる。</summary>
    public async Task<double> DetermineCrfAsync(EncodePlan plan, CancellationToken ct = default)
    {
        _step = 0;
        _stepCount = SampleCount * (ProbeCrfs.Length + (Verify ? 1 : 0));

        var ordered = ProbeCrfs.OrderBy(c => c).ToArray();
        var measured = new double[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            measured[i] = await MeasureAsync(plan, ordered[i], $"試し焼き CRF{ordered[i]:0.#}", ct)
                .ConfigureAwait(false);
            Say($"  CRF {ordered[i]:0.#} → {measured[i]:N0} kbps" +
                $"（全体 {BitrateModel.EstimateGiB(measured[i], plan.AudioKbps, plan.Source.DurationSec):N2} GiB 相当）");
        }

        double slope = ordered.Length >= 2
            ? BitrateModel.Slope(ordered[0], measured[0], ordered[^1], measured[^1])
            : BitrateModel.TheoreticalSlope;

        double crf = Math.Round(
            BitrateModel.SolveCrf(ordered[0], measured[0], plan.TargetKbps, slope), 1);
        Say($"  減衰率 CRF+1 あたり {BitrateModel.DecayPerCrf(slope):P1} → 逆算 CRF {crf:N1}");

        if (Verify)
        {
            double check = await MeasureAsync(plan, crf, $"確認焼き CRF{crf:0.#}", ct).ConfigureAwait(false);
            double err = (check - plan.TargetKbps) / plan.TargetKbps;
            Say($"  確認焼き {check:N0} kbps（目標との差 {err:P1}）");
            if (Math.Abs(err) > 0.08)
            {
                crf = Math.Round(BitrateModel.CorrectCrf(crf, check, plan.TargetKbps, slope), 1);
                Say($"  再補正 CRF {crf:N1}");
            }
        }

        if (crf < MinUsableCrf || crf > MaxUsableCrf)
            Say($"  ⚠ CRF {crf:N1} は実用域({MinUsableCrf}〜{MaxUsableCrf})の外です。目標サイズか解像度を見直してください");

        plan.Crf = crf;
        return crf;
    }

    /// <summary>本番と同一設定の短いサンプルを作り、そのパスを返す。</summary>
    public async Task<string> CreateSampleAsync(EncodePlan plan, CancellationToken ct = default)
    {
        _step = 0; _stepCount = 1;
        var outPath = Path.Combine(Path.GetTempPath(), "fst_sample.mp4");
        TryDelete(outPath);
        _step = 1;
        await Ffmpeg.RunAsync(
            FfmpegArgs.Sample(plan, plan.Source.DurationSec * 0.4, PreviewSeconds, outPath),
            PreviewSeconds, Reporter("サンプル生成"), null, null, ct).ConfigureAwait(false);
        return outPath;
    }

    /// <summary>本番エンコード。完了後の出力を probe して返す。</summary>
    public async Task<MediaInfo> EncodeAsync(EncodePlan plan, string outPath, CancellationToken ct = default)
    {
        var temp = Path.GetTempPath();
        double dur = plan.Source.DurationSec;

        if (plan.TwoPass)
        {
            _step = 0; _stepCount = 2;
            try
            {
                _step = 1;
                await Ffmpeg.RunAsync(FfmpegArgs.Pass1(plan), dur, Reporter("1パス目"), null, temp, ct)
                    .ConfigureAwait(false);
                _step = 2;
                await Ffmpeg.RunAsync(FfmpegArgs.Pass2(plan, outPath), dur, Reporter("2パス目"), null, temp, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                foreach (var f in Directory.GetFiles(temp, "x265_2pass.log*")) TryDelete(f);
            }
        }
        else
        {
            _step = 1; _stepCount = 1;
            await Ffmpeg.RunAsync(FfmpegArgs.Final(plan, outPath), dur, Reporter("本番エンコード"), null, null, ct)
                .ConfigureAwait(false);
        }

        return await MediaProbe.ProbeAsync(outPath, ct).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 使用中なら諦める */ }
    }
}
