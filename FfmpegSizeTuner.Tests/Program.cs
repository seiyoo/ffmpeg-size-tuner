using FfmpegSizeTuner.Core;

// PowerShell 版で実測・確認済みの値と一致するかを検証する
int pass = 0, fail = 0;

void Check(string name, double actual, double expected, double tol)
{
    bool ok = Math.Abs(actual - expected) <= tol;
    Console.WriteLine($"  [{(ok ? "OK" : "NG")}] {name,-46} actual={actual:F4} expected={expected:F4}");
    if (ok) pass++; else fail++;
}
void CheckStr(string name, string? actual, string? expected)
{
    bool ok = actual == expected;
    Console.WriteLine($"  [{(ok ? "OK" : "NG")}] {name,-46} actual=\"{actual ?? "<null>"}\" expected=\"{expected ?? "<null>"}\"");
    if (ok) pass++; else fail++;
}

Console.WriteLine("== 実素材 1080p25 / 01:42:21 / 音声96k (スクリーンショットの値) ==");
const double dur = 6141.0, aud = 96.0;
const int w = 1920, h = 1080; const double fps = 25.0;

double qBpp = BitrateModel.TargetBpp(BitrateModel.DefaultNormalizedBpp, h, fps);
double qKbps = Math.Round(BitrateModel.KbpsFromBpp(qBpp, w, h, fps));
Check("画質基準(標準) bpp", qBpp, 0.0300, 0.0001);
Check("画質基準(標準) kbps", qKbps, 1554, 1.0);
Check("画質基準(標準) サイズ GiB", BitrateModel.EstimateGiB(qKbps, aud, dur), 1.19, 0.005);
Check("1.8GiB 指定時の映像 kbps", Math.Round(BitrateModel.VideoKbpsForSize(1.8, dur, aud)), 2409, 1.0);
Check("1.8GiB 指定時の bpp", BitrateModel.BppFromKbps(2409, w, h, fps), 0.0465, 0.0002);

Console.WriteLine("== 画質グレード判定 ==");
CheckStr("bpp 0.0300 @1080p25", BitrateModel.GradeLabel(BitrateModel.Grade(0.0300, h, fps)), "実用十分");
CheckStr("bpp 0.0465 @1080p25", BitrateModel.GradeLabel(BitrateModel.Grade(0.0465, h, fps)), "ほぼ判別不能");
CheckStr("bpp 0.0700 @1080p25", BitrateModel.GradeLabel(BitrateModel.Grade(0.0700, h, fps)), "差を探すのが困難");
CheckStr("bpp 0.0180 @1080p25", BitrateModel.GradeLabel(BitrateModel.Grade(0.0180, h, fps)), "明確に劣化");
CheckStr("bpp 0.0333 @720p30 (基準点)", BitrateModel.GradeLabel(BitrateModel.Grade(0.0333, 720, 30)), "実用十分");

Console.WriteLine("== 参照ファイル(720p h264 bpp0.0512)の正規化 ==");
Check("720p基準へ正規化した bpp", BitrateModel.NormalizeReferenceBpp(0.0512, "h264", 720, 30), 0.0333, 0.0002);

Console.WriteLine("== CRF 回帰 (試し焼き実測: CRF24=8480kbps / CRF29=4410kbps) ==");
double slope = BitrateModel.Slope(24, 8480, 29, 4410);
Check("減衰率 CRF+1 あたり", BitrateModel.DecayPerCrf(slope), 0.1226, 0.0005);
double solved = Math.Round(BitrateModel.SolveCrf(24, 8480, 1581, slope), 1);
Check("目標1581kbps の逆算 CRF", solved, 36.8, 0.05);
Check("確認焼き1371kbps での再補正", Math.Round(BitrateModel.CorrectCrf(36.8, 1371, 1581, slope), 1), 35.7, 0.05);
Check("単調でない入力は理論値へフォールバック", BitrateModel.Slope(24, 100, 29, 200), BitrateModel.TheoreticalSlope, 1e-9);

Console.WriteLine("== 出力名 ==");
CheckStr("サフィックス _cmp",        OutputNaming.BuildStem(NameMode.Suffix, "big", "_cmp", null), "big_cmp");
CheckStr("サフィックス 不正文字",     OutputNaming.BuildStem(NameMode.Suffix, "big", "a/b:c*?", null), "bigabc");
CheckStr("サフィックス .mp4",        OutputNaming.BuildStem(NameMode.Suffix, "big", ".mp4", null), "big");
CheckStr("サフィックス 空",          OutputNaming.BuildStem(NameMode.Suffix, "big", "", null), "big");
CheckStr("名前指定 拡張子つき",       OutputNaming.BuildStem(NameMode.Explicit, "big", null, "my movie 2026.mp4"), "my movie 2026");
CheckStr("名前指定 不正文字",         OutputNaming.BuildStem(NameMode.Explicit, "big", null, "bad:name?"), "badname");
CheckStr("名前指定 空",              OutputNaming.BuildStem(NameMode.Explicit, "big", null, ""), null);
CheckStr("名前指定 空白のみ",         OutputNaming.BuildStem(NameMode.Explicit, "big", null, "   "), null);

var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    @"D:\v\big_cmp.mp4", @"D:\v\big_cmp_2.mp4",
};
CheckStr("連番(2つ埋まっている)", OutputNaming.Resolve(@"D:\v", "big_cmp", taken.Contains), @"D:\v\big_cmp_3.mp4");
CheckStr("連番(空いている)",     OutputNaming.Resolve(@"D:\v", "other",   taken.Contains), @"D:\v\other.mp4");
CheckStr("stem が null なら null", OutputNaming.Resolve(@"D:\v", null,    taken.Contains), null);

Console.WriteLine("== 画質の帯 ==");
CheckStr("正規化 0.020", BitrateModel.BandOf(0.020).Name, "明確に劣化");
CheckStr("正規化 0.025", BitrateModel.BandOf(0.025).Name, "劣化が見えやすい");
CheckStr("正規化 0.035", BitrateModel.BandOf(0.035).Name, "実用十分");
CheckStr("正規化 0.050", BitrateModel.BandOf(0.050).Name, "ほぼ判別不能");
CheckStr("正規化 0.080", BitrateModel.BandOf(0.080).Name, "差を探すのが困難");
Check("帯は隙間なく連続", BitrateModel.Bands.Zip(BitrateModel.Bands.Skip(1))
    .Sum(x => Math.Abs(x.First.To - x.Second.From)), 0, 1e-12);
Check("説明が全帯にある", BitrateModel.Bands.Count(b => b.Description.Length > 20),
    BitrateModel.Bands.Length, 0);
Check("正規化の往復", BitrateModel.Normalize(BitrateModel.Denormalize(0.05, 1080, 25), 1080, 25),
    0.05, 1e-12);

Console.WriteLine("== 目標サイズの目盛り (1080p25 / 01:42:21 / 4.14GiB) ==");
var demo = new MediaInfo
{
    FullName = @"D:\demo.mp4",
    Width = 1920, Height = 1080, Fps = 25.0, DurationSec = 6141,
    VideoCodec = "h264", PixFmt = "yuv420p", AudioCodec = "aac", AudioKbps = 130,
    SizeBytes = 4_445_278_951,
};
Check("素材 4.14 GiB", demo.SizeGiB, 4.14, 0.005);
Check("素材 映像 5,658 kbps", demo.VideoKbps, 5658, 5);

var range = SizeScale.For(demo, 1920, 1080, 96);
Check("下端 0.60 GiB", range.MinGiB, 0.60, 1e-9);
Check("上端 3.90 GiB", range.MaxGiB, 3.90, 1e-9);
Check("刻み数", range.Steps, 33, 0);
Check("0.10 刻み", range.GiBAt(12) - range.GiBAt(11), 0.10, 1e-9);
Check("1.80 GiB の目盛り位置", range.GiBAt(range.StepOf(1.80)), 1.80, 1e-9);
Check("範囲外は端に丸める", range.GiBAt(range.StepOf(99.0)), range.MaxGiB, 1e-9);

Check("境界 明確に劣化→劣化が見えやすい",
    SizeScale.BoundaryGiB(0.022, demo, 1920, 1080, 96, range), 0.807, 0.005);
Check("境界 劣化が見えやすい→実用十分",
    SizeScale.BoundaryGiB(0.028, demo, 1920, 1080, 96, range), 1.008, 0.005);
Check("境界 実用十分→ほぼ判別不能",
    SizeScale.BoundaryGiB(0.042, demo, 1920, 1080, 96, range), 1.477, 0.005);
Check("境界 ほぼ判別不能→差を探すのが困難",
    SizeScale.BoundaryGiB(0.065, demo, 1920, 1080, 96, range), 2.248, 0.005);
Check("上端は無限を打ち止め",
    SizeScale.BoundaryGiB(double.PositiveInfinity, demo, 1920, 1080, 96, range), range.MaxGiB, 1e-9);

CheckStr("1.80 GiB の帯", SizeScale.BandAt(1.80, demo, 1920, 1080, 96).Name, "ほぼ判別不能");
CheckStr("1.20 GiB の帯", SizeScale.BandAt(1.20, demo, 1920, 1080, 96).Name, "実用十分");
CheckStr("0.90 GiB の帯", SizeScale.BandAt(0.90, demo, 1920, 1080, 96).Name, "劣化が見えやすい");
CheckStr("3.00 GiB の帯", SizeScale.BandAt(3.00, demo, 1920, 1080, 96).Name, "差を探すのが困難");

Console.WriteLine("== サイズ指定と画質指定 ==");
var szSet = new EncodeSettings { Mode = TargetMode.Size, TargetGiB = 1.80, AudioKbps = 96 };
var szPlan = EncodePlanner.Build(demo, szSet);
Check("サイズ指定 2,409 kbps", szPlan.TargetKbps, 2409, 1);
Check("サイズ指定 想定 1.80 GiB", szPlan.EstimatedGiB, 1.80, 0.005);
CheckStr("サイズ指定の帯", szPlan.Band.Name, "ほぼ判別不能");
CheckStr("サイズ指定の基準表示", szPlan.Basis, "サイズ指定");

double norm = BitrateModel.Normalize(
    BitrateModel.BppFromKbps(szPlan.TargetKbps, 1920, 1080, 25), 1080, 25);
var qlSet = szSet with { Mode = TargetMode.Quality, TargetNormalizedBpp = norm };
var qlPlan = EncodePlanner.Build(demo, qlSet);
Check("画質指定が同じ結果に往復", qlPlan.EstimatedGiB, 1.80, 0.005);
Check("画質指定では TargetGiB を持たない", qlPlan.TargetGiB, 0, 1e-12);
CheckStr("画質指定の基準表示", qlPlan.Basis, "画質指定");
CheckStr("キュー表示ラベル(サイズ)", szSet.TargetLabel, "1.80 GiB");
CheckStr("キュー表示ラベル(画質)", qlSet.TargetLabel, "ほぼ判別不能");

// 尺が半分のファイルでも、画質指定なら同じ画質・違うサイズになる
var half = demo with { DurationSec = 3070.5, SizeBytes = 2_222_639_475 };
var halfQl = EncodePlanner.Build(half, qlSet);
var halfSz = EncodePlanner.Build(half, szSet);
CheckStr("画質指定は尺が変わっても同じ帯", halfQl.Band.Name, qlPlan.Band.Name);
Check("画質指定は尺が半分ならサイズも半分", halfQl.EstimatedGiB, 0.90, 0.01);
Check("サイズ指定は尺が半分でも同じサイズ", halfSz.EstimatedGiB, 1.80, 0.005);
Check("サイズ指定は尺が半分だと帯が上がる",
    (double)(int)halfSz.Band.Grade, (double)(int)QualityGrade.Transparent, 0);

Console.WriteLine();
Console.WriteLine($"単体試験: {pass} passed / {fail} failed");

// 動画パスを渡されたときだけ、実際に ffmpeg を動かす結合試験も行う
if (args.Length > 0 && File.Exists(args[0]))
{
    Console.WriteLine();
    fail += await FfmpegSizeTuner.Tests.Integration.RunAsync(args[0]);
}

return fail == 0 ? 0 : 1;
