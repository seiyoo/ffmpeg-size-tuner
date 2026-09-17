using FfmpegSizeTuner.Core;

namespace FfmpegSizeTuner.Tests;

/// <summary>実際に ffprobe / ffmpeg を動かす結合試験。動画のパスを引数で渡したときだけ走る。</summary>
public static class Integration
{
    public static async Task<int> RunAsync(string videoPath)
    {
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"  [{(ok ? "OK" : "NG")}] {name,-40} {detail}");
            if (ok) pass++; else fail++;
        }

        Console.WriteLine("== ffprobe ==");
        var src = await MediaProbe.ProbeAsync(videoPath);
        Console.WriteLine($"  {src.Width}x{src.Height} @{src.Fps:N2}fps {src.VideoCodec}/{src.PixFmt} " +
                          $"{TimeSpan.FromSeconds(src.DurationSec):hh\\:mm\\:ss} " +
                          $"{src.SizeGiB:N3} GiB 映像{src.VideoKbps:N0}kbps bpp{src.VideoBpp:N4}");
        Check("解像度が取れる", src.Width > 0 && src.Height > 0, $"{src.Width}x{src.Height}");
        Check("fps が取れる", src.Fps > 0, $"{src.Fps:N3}");
        Check("尺が取れる", src.DurationSec > 0, $"{src.DurationSec:N1}s");
        Check("サイズが取れる", src.SizeBytes > 0, $"{src.SizeBytes:N0} bytes");

        Console.WriteLine("== フレームレート表記の解釈 ==");
        Check("30000/1001", Math.Abs(MediaProbe.ParseFrameRate("30000/1001") - 29.97) < 0.01, "29.97");
        Check("25/1", Math.Abs(MediaProbe.ParseFrameRate("25/1") - 25) < 1e-9, "25");
        Check("0/0 は 0", MediaProbe.ParseFrameRate("0/0") == 0, "0");

        Console.WriteLine("== プラン組み立て ==");
        var settings = new EncodeSettings
        {
            Mode = TargetMode.Size,
            TargetGiB = Math.Max(0.004, src.SizeGiB * 0.45),
            Preset = "veryfast",
            AudioKbps = 128,
        };
        var plan = EncodePlanner.Build(src, settings);
        Console.WriteLine($"  目標 {plan.TargetKbps:N0} kbps ({plan.Basis}) bpp {plan.TargetBpp:N4} " +
                          $"= 「{BitrateModel.GradeLabel(plan.Grade)}」 → 想定 {plan.EstimatedGiB:N3} GiB");
        Check("目標が正の値", plan.TargetKbps > 0, $"{plan.TargetKbps:N0} kbps");
        Check("想定サイズが目標と一致", Math.Abs(plan.EstimatedGiB - settings.TargetGiB) < 0.002,
              $"{plan.EstimatedGiB:N3} vs {settings.TargetGiB:N3}");

        Console.WriteLine("== 素材ビットレート超過時のクランプ ==");
        var huge = EncodePlanner.Build(src, settings with { TargetGiB = src.SizeGiB * 5 });
        Check("クランプが働く", huge.Clamped, $"cap {huge.CapKbps:N0} kbps");
        Check("素材の95%で頭打ち", Math.Abs(huge.TargetKbps - Math.Round(src.VideoKbps * 0.95)) < 1,
              $"{huge.TargetKbps:N0} kbps");

        Console.WriteLine("== 試し焼き → CRF 確定（実エンコード） ==");
        var pipeline = new EncodePipeline
        {
            SampleCount = 2,
            SampleSeconds = 6,
            PreviewSeconds = 5,
        };
        pipeline.Log += m => Console.WriteLine(m);
        int progressCount = 0;
        double lastPct = -1;
        pipeline.Progressed += p => { progressCount++; lastPct = p.Progress.Percent; };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        double crf = await pipeline.DetermineCrfAsync(plan);
        sw.Stop();
        Check("CRF が確定した", crf > 0, $"CRF {crf:N1} ({sw.Elapsed.TotalSeconds:N0}s)");
        Check("進捗が通知された", progressCount > 0, $"{progressCount} 回, 最後 {lastPct:N0}%");
        Check("plan に反映された", Math.Abs(plan.Crf - crf) < 1e-9, $"{plan.Crf:N1}");

        Console.WriteLine("== サンプル生成 ==");
        var sample = await pipeline.CreateSampleAsync(plan);
        var sampleOk = File.Exists(sample) && new FileInfo(sample).Length > 0;
        Check("サンプルができた", sampleOk, sampleOk ? $"{new FileInfo(sample).Length:N0} bytes" : "なし");
        if (sampleOk)
        {
            var si = await MediaProbe.ProbeAsync(sample);
            Check("サンプルが hevc", si.VideoCodec == "hevc", si.VideoCodec);
            Check("サンプルに音声がある", si.AudioCodec is not null, si.AudioCodec ?? "なし");
        }

        Console.WriteLine("== 本番エンコード（capped CRF） ==");
        var outPath = OutputNaming.Resolve(Path.GetTempPath(),
            OutputNaming.BuildStem(NameMode.Suffix, src.BaseName, "_cstest", null), File.Exists)!;
        var res = await pipeline.EncodeAsync(plan, outPath);
        Console.WriteLine($"  {outPath}");
        Console.WriteLine($"  {res.SizeGiB:N3} GiB / 目標 {settings.TargetGiB:N3} GiB " +
                          $"(差 {(res.SizeGiB - settings.TargetGiB) / settings.TargetGiB:P1})");
        Check("出力が hevc", res.VideoCodec == "hevc", res.VideoCodec);
        Check("尺が保たれている", Math.Abs(res.DurationSec - src.DurationSec) < 1.0,
              $"{res.DurationSec:N1}s");
        Check("サイズが目標の ±25% 以内", Math.Abs(res.SizeGiB - settings.TargetGiB) / settings.TargetGiB < 0.25,
              $"{res.SizeGiB:N3} GiB");
        TryDelete(outPath);

        Console.WriteLine("== 本番エンコード（2パス ABR） ==");
        var plan2 = EncodePlanner.Build(src, settings with { Method = EncodeMethod.TwoPass });
        var out2 = OutputNaming.Resolve(Path.GetTempPath(),
            OutputNaming.BuildStem(NameMode.Suffix, src.BaseName, "_cstest2", null), File.Exists)!;
        var res2 = await pipeline.EncodeAsync(plan2, out2);
        Console.WriteLine($"  {res2.SizeGiB:N3} GiB / 目標 {settings.TargetGiB:N3} GiB " +
                          $"(差 {(res2.SizeGiB - settings.TargetGiB) / settings.TargetGiB:P1})");
        Check("2パスがサイズを当てる（±6%）",
              Math.Abs(res2.SizeGiB - settings.TargetGiB) / settings.TargetGiB < 0.06,
              $"{res2.SizeGiB:N3} GiB");
        Check("2パスの stats が残っていない",
              Directory.GetFiles(Path.GetTempPath(), "x265_2pass.log*").Length == 0, "掃除済み");
        TryDelete(out2);

        Console.WriteLine("== 異常系 ==");
        try
        {
            await MediaProbe.ProbeAsync(Path.Combine(Path.GetTempPath(), "___no_such_file___.mp4"));
            Check("存在しないファイルで例外", false, "例外が出なかった");
        }
        catch (MediaProbeException) { Check("存在しないファイルで例外", true, "MediaProbeException"); }

        try
        {
            EncodePlanner.Build(src, settings with { TargetGiB = 0.0000001 });
            Check("小さすぎる目標で例外", false, "例外が出なかった");
        }
        catch (EncodePlanException) { Check("小さすぎる目標で例外", true, "EncodePlanException"); }

        Console.WriteLine();
        Console.WriteLine($"結合試験: {pass} passed / {fail} failed");
        return fail;
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { }
    }
}
