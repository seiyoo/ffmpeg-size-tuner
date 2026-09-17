using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FfmpegSizeTuner.Core;

namespace FfmpegSizeTuner.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<QueueItem> _queue = new();
    private MediaInfo? _src;
    private SizeScale.Range? _range;
    private readonly List<double> _boundaries = new();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _suppress;

    /// <summary>直近の「サンプルで確認」で確定した CRF。素材と設定が一致する間だけ有効。</summary>
    private (string Path, EncodeSettings Settings, double Crf)? _sampled;

    public MainWindow()
    {
        InitializeComponent();
        QueueGrid.ItemsSource = _queue;
        _queue.CollectionChanged += (_, _) => UpdateQueueButtons();

        PreviewDragOver += OnDragOver;
        PreviewDrop += OnDrop;

        Loaded += (_, _) =>
        {
            if (!Ffmpeg.IsAvailable())
            {
                MessageBox.Show(this, "ffmpeg / ffprobe が PATH に見つかりません。",
                    "ffmpeg Size Tuner", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            UpdateModeHint();
            UpdateQueueButtons();

            // 引数で動画を渡されたら読み込む（エクスプローラの「プログラムから開く」用）
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) _ = LoadSourceAsync(args[1]);
        };

        Closing += (_, e) =>
        {
            if (!_busy) return;
            var r = MessageBox.Show(this, "エンコード中です。中止して終了しますか？",
                "ffmpeg Size Tuner", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes) _cts?.Cancel();
            else e.Cancel = true;
        };
    }

    // ================================================================ 入力

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        e.Handled = true;

        if (files.Length == 1)
        {
            await LoadSourceAsync(files[0]);
            return;
        }

        // 複数ドロップは現在の設定でまとめてキューに入れる
        foreach (var f in files)
        {
            try
            {
                var info = await MediaProbe.ProbeAsync(f);
                EnqueueInternal(info, BuildSettings(), null);
            }
            catch (Exception ex)
            {
                Log($"!! {Path.GetFileName(f)}: {ex.Message}");
            }
        }
    }

    private async void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "動画ファイル|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.ts;*.m2ts;*.flv;*.webm|すべて|*.*",
        };
        if (dlg.ShowDialog(this) == true) await LoadSourceAsync(dlg.FileName);
    }

    private async Task LoadSourceAsync(string path)
    {
        try
        {
            _src = await MediaProbe.ProbeAsync(path);
        }
        catch (Exception ex)
        {
            _src = null;
            _range = null;
            SrcText.Text = path;
            InfoText.Text = "⚠ " + ex.Message;
            TargetSlider.IsEnabled = false;
            UpdateSingleButtons();
            return;
        }

        _sampled = null;
        SrcText.Text = _src.FullName;
        SrcText.Foreground = Brushes.Black;

        var info = $"{_src.Width}x{_src.Height} @ {_src.Fps:N2}fps  {_src.VideoCodec}／{_src.PixFmt}   " +
                   $"{Hms(_src.DurationSec)}   {_src.SizeGiB:N2} GiB（映像 {_src.VideoKbps:N0} kbps・bpp {_src.VideoBpp:N4}）";
        if (_src.AudioCodec is not null) info += $"   音声 {_src.AudioCodec} {_src.AudioKbps:N0} kbps";
        if (_src.IsHighBitDepth) info += "\n※ 10bit 素材のため出力も 10bit(Main10) になります";
        if (_src.AudioBitrateUnknown) info += "\n※ 音声ビットレートが取得できない形式です。bpp が実際より高めに出ます";
        InfoText.Text = info;

        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = _src.BaseName + "_cmp";

        RebuildTargetScale(preferGiB: 1.8);
        UpdateSingleButtons();
        UpdateOutPreview();
    }

    // ================================================================ 目標スライダー

    private (int W, int H) OutputSize()
    {
        if (_src is null) return (0, 0);
        int want = ResBox.SelectedIndex switch { 1 => 1080, 2 => 720, _ => 0 };
        if (want > 0 && want < _src.Height)
            return ((int)(Math.Round(_src.Width * (double)want / _src.Height / 2) * 2), want);
        return (_src.Width, _src.Height);
    }

    private double AudioKbpsNow() => AudioBox.SelectedIndex switch
    {
        2 => _src is { AudioKbps: > 0 } ? Math.Round(_src.AudioKbps) : 128,
        1 => 96,
        _ => 128,
    };

    private double CurrentGiB() =>
        _range is null ? 1.8 : _range.GiBAt((int)TargetSlider.Value);

    /// <summary>いまの目標サイズを、解像度・fps に依らない正規化 bpp に直す。</summary>
    private double CurrentNormalizedBpp()
    {
        if (_src is null) return BitrateModel.DefaultNormalizedBpp;
        var (w, h) = OutputSize();
        double kbps = Math.Max(1, BitrateModel.VideoKbpsForSize(CurrentGiB(), _src.DurationSec, AudioKbpsNow()));
        return BitrateModel.Normalize(BitrateModel.BppFromKbps(kbps, w, h, _src.Fps), h, _src.Fps);
    }

    /// <summary>解像度や音声を変えると意味のある範囲が変わるので、目盛りを作り直す。</summary>
    private void RebuildTargetScale(double? preferGiB = null)
    {
        if (_src is null) { TargetSlider.IsEnabled = false; return; }

        double keep = preferGiB ?? CurrentGiB();
        var (w, h) = OutputSize();
        _range = SizeScale.For(_src, w, h, AudioKbpsNow());

        _suppress = true;
        TargetSlider.Maximum = _range.Steps;
        TargetSlider.Value = _range.StepOf(Math.Clamp(keep, _range.MinGiB, _range.MaxGiB));
        _suppress = false;

        TargetSlider.IsEnabled = true;
        DrawBandStrip();
        UpdateTargetReadout();
    }

    private void DrawBandStrip()
    {
        BandStrip.ColumnDefinitions.Clear();
        BandStrip.Children.Clear();
        _boundaries.Clear();
        if (_src is null || _range is null) return;

        var (w, h) = OutputSize();
        double aud = AudioKbpsNow();
        double prev = _range.MinGiB;
        int col = 0;

        foreach (var b in BitrateModel.Bands)
        {
            double to = SizeScale.BoundaryGiB(b.To, _src, w, h, aud, _range);
            if (to <= prev + 1e-9) continue;

            BandStrip.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(to - prev, GridUnitType.Star),
            });
            var cell = new Border { Background = Palette.Chip(b.Grade), ToolTip = b.Name };
            Grid.SetColumn(cell, col++);
            BandStrip.Children.Add(cell);

            if (to < _range.MaxGiB - 1e-9) _boundaries.Add(to);
            prev = to;
        }
        LayoutOverlays();
    }

    private void BandStrip_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutOverlays();

    /// <summary>境界のラベルと現在位置のマーカーを、実寸に合わせて置き直す。</summary>
    private void LayoutOverlays()
    {
        MarkLayer.Children.Clear();
        TickLayer.Children.Clear();
        if (_range is null || BandStrip.ActualWidth <= 0) return;
        double px = BandStrip.ActualWidth;

        foreach (var g in _boundaries)
        {
            var tb = new TextBlock
            {
                Text = g.ToString("N1"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, Math.Clamp(_range.PositionOf(g) * px - tb.DesiredSize.Width / 2,
                                          0, Math.Max(0, px - tb.DesiredSize.Width)));
            TickLayer.Children.Add(tb);
        }

        var cap = new TextBlock
        {
            Text = $"上限 {_range.MaxGiB:N1} GiB",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA3, 0x2D, 0x2D)),
        };
        cap.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(cap, Math.Max(0, px - cap.DesiredSize.Width));
        Canvas.SetTop(cap, 0);
        TickLayer.Children.Add(cap);

        var mark = new System.Windows.Shapes.Rectangle
        {
            Width = 3,
            Height = 20,
            RadiusX = 1.5,
            RadiusY = 1.5,
            Fill = Brushes.Black,
        };
        Canvas.SetLeft(mark, Math.Clamp(_range.PositionOf(CurrentGiB()) * px - 1.5, 0, px - 3));
        Canvas.SetTop(mark, -2);
        MarkLayer.Children.Add(mark);
    }

    private void TargetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress || !IsLoaded) return;
        UpdateTargetReadout();
        LayoutOverlays();
    }

    private void UpdateTargetReadout()
    {
        if (_src is null || _range is null)
        {
            RdSize.Text = "—";
            RdKbps.Text = RdPct.Text = "";
            return;
        }

        EncodePlan plan;
        try
        {
            plan = EncodePlanner.Build(_src, BuildSettings());
        }
        catch (Exception ex)
        {
            RdSize.Text = "—";
            RdKbps.Text = RdPct.Text = "";
            DescName.Text = "⚠";
            DescBody.Text = ex.Message;
            return;
        }

        RdSize.Text = $"{plan.EstimatedGiB:N2} GiB";
        RdKbps.Text = $"映像 {plan.TargetKbps:N0} kbps";
        RdPct.Text = $"素材の {plan.SourceRatio:P0} のビットレート　／　元は {_src.SizeGiB:N2} GiB";

        var band = plan.Band;
        var (w, h) = OutputSize();
        double aud = AudioKbpsNow();
        double from = SizeScale.BoundaryGiB(band.From, _src, w, h, aud, _range);
        double to = SizeScale.BoundaryGiB(band.To, _src, w, h, aud, _range);

        DescCard.Background = Palette.Card(band.Grade);
        DescName.Text = band.Name;
        DescName.Foreground = Palette.Ink(band.Grade);
        DescBody.Text = band.Description;
        DescBody.Foreground = Palette.Ink(band.Grade);
        DescRange.Text = $"この帯は {from:N2} 〜 {to:N2} GiB";
        DescRange.Foreground = Palette.Ink(band.Grade);

        if (plan.Clamped)
        {
            DescWarn.Text = $"⚠ 目標が素材の映像ビットレート（{_src.VideoKbps:N0} kbps）を超えるため " +
                            $"{plan.CapKbps:N0} kbps に制限しました。これ以上増やしても画質は上がりません";
            DescWarn.Visibility = Visibility.Visible;
        }
        else
        {
            DescWarn.Visibility = Visibility.Collapsed;
        }
    }

    // ================================================================ 設定

    private EncodeSettings BuildSettings() => new()
    {
        Mode = ModeQuality.IsChecked == true ? TargetMode.Quality : TargetMode.Size,
        TargetGiB = CurrentGiB(),
        TargetNormalizedBpp = CurrentNormalizedBpp(),
        Method = ModeAbr.IsChecked == true ? EncodeMethod.TwoPass : EncodeMethod.CappedCrf,
        TargetHeight = ResBox.SelectedIndex switch { 1 => 1080, 2 => 720, _ => 0 },
        Preset = PresetBox.SelectedIndex switch { 0 => "fast", 2 => "slow", _ => "medium" },
        AudioKbps = AudioBox.SelectedIndex == 1 ? 96 : 128,
        CopyAudio = AudioBox.SelectedIndex == 2,
        NameMode = NameMode.IsChecked == true ? Core.NameMode.Explicit : Core.NameMode.Suffix,
        Suffix = SuffixBox.Text,
        ExplicitName = NameBox.Text,
    };

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        SuffixBox.IsEnabled = SuffixMode.IsChecked == true;
        NameBox.IsEnabled = NameMode.IsChecked == true;
        UpdateModeHint();
        RebuildTargetScale();
        UpdateOutPreview();
    }

    private void UpdateModeHint() => ModeHint.Text = ModeQuality.IsChecked == true
        ? "キューでは尺が違っても全ファイルが同じ画質になります"
        : "キューでは全ファイルが同じサイズになります";

    private void UpdateOutPreview()
    {
        if (_src is null) { OutPreview.Text = "—"; return; }
        var st = BuildSettings();
        var stem = OutputNaming.BuildStem(st.NameMode, _src.BaseName, st.Suffix, st.ExplicitName);
        if (string.IsNullOrEmpty(stem))
        {
            OutPreview.Text = "⚠ 出力ファイル名を入力してください";
            AddBtn.IsEnabled = false;
            return;
        }
        var want = Path.Combine(_src.Directory, stem + ".mp4");
        var actual = OutputNaming.Resolve(_src.Directory, stem, File.Exists)!;
        OutPreview.Text = actual == want
            ? "出力: " + actual
            : "出力: " + actual + "（同名があるため連番にしました）";
        UpdateSingleButtons();
    }

    // ================================================================ 単体操作

    private async void SampleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_src is null || _busy) return;
        await RunExclusiveAsync(async ct =>
        {
            var plan = EncodePlanner.Build(_src, BuildSettings());
            var pipeline = NewPipeline();

            if (!plan.TwoPass)
            {
                SetStatus("試し焼き中…");
                await pipeline.DetermineCrfAsync(plan, ct);
                _sampled = (_src.FullName, plan.Settings, plan.Crf);
            }

            SetStatus("サンプル生成中…");
            var sample = await pipeline.CreateSampleAsync(plan, ct);
            Log($"== サンプル: {sample} ==");
            LogResolved(plan);
            SetStatus($"サンプル（{pipeline.PreviewSeconds}秒）を再生します。良ければ「キューに追加」");
            TryOpen(sample);
        });
    }

    private async void MeasureBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_src is null || _busy) return;
        await RunExclusiveAsync(async ct =>
        {
            var plan = EncodePlanner.Build(_src, BuildSettings());
            if (plan.TwoPass)
            {
                Log($"2パス方式は試し焼き不要です。-b:v {plan.TargetKbps:N0}k で確定します。");
                SetStatus("2パスは測定なしで確定します");
                return;
            }
            SetStatus("試し焼き中…");
            await NewPipeline().DetermineCrfAsync(plan, ct);
            _sampled = (_src.FullName, plan.Settings, plan.Crf);
            LogResolved(plan);
            SetStatus("測定完了。「キューに追加」で同じ設定のまま登録します");
        });
    }

    private void LogResolved(EncodePlan plan) => Log(plan.TwoPass
        ? $"  確定: -b:v {plan.TargetKbps:N0}k -preset {plan.Settings.Preset}（2パス ABR）→ 想定 {plan.EstimatedGiB:N2} GiB"
        : $"  確定: -crf {plan.Crf:N1} -maxrate {plan.Maxrate}k -preset {plan.Settings.Preset} → 想定 {plan.EstimatedGiB:N2} GiB");

    private void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_src is null) return;
        var st = BuildSettings();
        try { EncodePlanner.Build(_src, st); }
        catch (Exception ex) { SetStatus("⚠ " + ex.Message); return; }

        double? known = _sampled is { } s && s.Path == _src.FullName && s.Settings == st ? s.Crf : null;
        EnqueueInternal(_src, st, known);
    }

    private void EnqueueInternal(MediaInfo src, EncodeSettings st, double? knownCrf)
    {
        var item = new QueueItem { Source = src, Settings = st, KnownCrf = knownCrf };
        // 追加時に出力先を確保しておく。キュー内の他の行が取った名前も避ける
        item.OutPath = item.ResolveOutPath(IsClaimedByQueue);
        item.OutputName = Path.GetFileName(item.OutPath ?? "（名前が不正）");
        _queue.Add(item);
        Log($"キューに追加: {item.FileName} → {item.OutputName}" +
            (knownCrf is null ? "" : $"（CRF {knownCrf:N1} 測定済み）"));
    }

    // ================================================================ キュー実行

    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunExclusiveAsync(RunQueueAsync);
    }

    private async Task RunQueueAsync(CancellationToken ct)
    {
        foreach (var item in _queue.ToList())
        {
            if (item.Status is not QueueStatus.Waiting) continue;
            ct.ThrowIfCancellationRequested();

            item.Status = QueueStatus.Running;
            item.Progress = 0;
            QueueGrid.ScrollIntoView(item);
            Log($"── {item.FileName} ──");

            try
            {
                // 追加時に確保した名前を使う。その後に同名が現れていたら取り直す
                var outPath = item.OutPath;
                if (outPath is null || File.Exists(outPath)) outPath = item.ResolveOutPath(IsClaimedByQueue);
                if (outPath is null) throw new EncodePlanException("出力ファイル名が不正です");

                var plan = EncodePlanner.Build(item.Source, item.Settings);
                var pipeline = NewPipeline(item);
                Log($"  目標 {plan.TargetKbps:N0} kbps（{plan.Basis}）→ 想定 {plan.EstimatedGiB:N2} GiB" +
                    $"／「{plan.Band.Name}」の水準");

                if (!plan.TwoPass)
                {
                    if (item.KnownCrf is { } crf)
                    {
                        plan.Crf = crf;
                        Log($"  測定済みの CRF {crf:N1} を使います");
                    }
                    else
                    {
                        SetStatus($"{item.FileName}: 試し焼き中…");
                        await pipeline.DetermineCrfAsync(plan, ct);
                    }
                }

                SetStatus($"{item.FileName}: エンコード中…");
                var res = await pipeline.EncodeAsync(plan, outPath, ct);

                item.OutPath = outPath;
                item.Status = QueueStatus.Done;
                item.Progress = 100;
                item.Detail = $"{res.SizeGiB:N2} GiB（元の {res.SizeGiB / item.Source.SizeGiB:P0}）" +
                              (plan.SizeBased
                                  ? $" 目標比 {(res.SizeGiB - plan.TargetGiB) / plan.TargetGiB:P1}"
                                  : "");
                Log($"  完了: {outPath}  {item.Detail}");
                OpenBtn.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
                item.Status = QueueStatus.Cancelled;
                item.Detail = "中止";
                Log("  中止しました");
                throw;
            }
            catch (Exception ex)
            {
                item.Status = QueueStatus.Failed;
                item.Detail = ex.Message.Split('\n')[0];
                Log($"  !! {ex.Message}");
            }
        }
        SetStatus("キューを処理しました");
    }

    private EncodePipeline NewPipeline(QueueItem? item = null)
    {
        var p = new EncodePipeline();
        p.Log += m => Dispatcher.BeginInvoke(() => Log(m));
        p.Progressed += pp => Dispatcher.BeginInvoke(() =>
        {
            // 各フェーズ内の進捗を、そのジョブ全体の進捗に均す
            double overall = pp.StepCount > 0
                ? ((pp.StepIndex - 1) + pp.Progress.Percent / 100.0) / pp.StepCount * 100.0
                : pp.Progress.Percent;
            Bar.Value = Math.Clamp(overall, 0, 100);
            if (item is not null) item.Progress = Bar.Value;

            var eta = pp.Progress.Eta is { } t ? $"  残り約 {Hms(t.TotalSeconds)}" : "";
            var speed = pp.Progress.Speed > 0 ? $"（{pp.Progress.Speed:N2}x）" : "";
            SetStatus($"{pp.Label}  {pp.StepIndex}/{pp.StepCount}  {pp.Progress.Percent:N0}%{eta}{speed}");
        });
        return p;
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ================================================================ キュー編集

    private void UpBtn_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void DownBtn_Click(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        if (QueueGrid.SelectedItem is not QueueItem item) return;
        int i = _queue.IndexOf(item), j = i + delta;
        if (i < 0 || j < 0 || j >= _queue.Count) return;
        _queue.Move(i, j);
        QueueGrid.SelectedItem = item;
    }

    private void RemoveBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in QueueGrid.SelectedItems.Cast<QueueItem>().ToList())
        {
            if (item.Status == QueueStatus.Running) continue;
            _queue.Remove(item);
        }
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _queue.Where(q => q.Status is QueueStatus.Done or QueueStatus.Cancelled).ToList())
            _queue.Remove(item);
    }

    private void OpenBtn_Click(object sender, RoutedEventArgs e)
    {
        var target = (QueueGrid.SelectedItem as QueueItem)?.OutPath
                     ?? _queue.LastOrDefault(q => q.OutPath is not null)?.OutPath;
        if (target is not null && File.Exists(target))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
    }

    // ================================================================ 共通

    /// <summary>実行中は操作を止め、終わったら戻す。例外はログとステータスに出す。</summary>
    private async Task RunExclusiveAsync(Func<CancellationToken, Task> body)
    {
        _busy = true;
        _cts = new CancellationTokenSource();
        SetControlsEnabled(false);
        try
        {
            await body(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("中止しました");
        }
        catch (Exception ex)
        {
            Log("!! " + ex.Message);
            SetStatus("⚠ " + ex.Message.Split('\n')[0]);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _busy = false;
            SetControlsEnabled(true);
            Bar.Value = 0;
        }
    }

    private void SetControlsEnabled(bool idle)
    {
        foreach (var c in new Control[]
        {
            BrowseBtn, ModeSize, ModeQuality, ModeCrf, ModeAbr, ResBox, PresetBox, AudioBox,
            SuffixMode, NameMode, UpBtn, DownBtn, RemoveBtn, ClearBtn,
        })
        {
            c.IsEnabled = idle;
        }
        TargetSlider.IsEnabled = idle && _range is not null;
        SuffixBox.IsEnabled = idle && SuffixMode.IsChecked == true;
        NameBox.IsEnabled = idle && NameMode.IsChecked == true;
        StopBtn.IsEnabled = !idle;
        UpdateSingleButtons();
        UpdateQueueButtons();
    }

    private void UpdateSingleButtons()
    {
        bool ok = !_busy && _src is not null;
        SampleBtn.IsEnabled = ok;
        MeasureBtn.IsEnabled = ok;
        AddBtn.IsEnabled = ok && OutPreview.Text.StartsWith("出力:");
    }

    /// <summary>キュー内の他の行が既に確保している出力先か。</summary>
    private bool IsClaimedByQueue(string path) =>
        _queue.Any(q => string.Equals(q.OutPath, path, StringComparison.OrdinalIgnoreCase));

    private void UpdateQueueButtons() =>
        StartBtn.IsEnabled = !_busy && _queue.Any(q => q.Status == QueueStatus.Waiting);

    private void SetStatus(string msg) => StatusText.Text = msg;

    private void Log(string msg)
    {
        LogBox.AppendText(msg + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private static string Hms(double sec)
    {
        if (sec < 0 || double.IsNaN(sec) || double.IsInfinity(sec)) return "--:--:--";
        var t = TimeSpan.FromSeconds(sec);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    private void TryOpen(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log("!! 既定のプレイヤーで開けませんでした: " + ex.Message); }
    }
}

/// <summary>帯ごとの色。帯の色見本・説明カードの地・文字色をそろえる。</summary>
internal static class Palette
{
    private static SolidColorBrush B(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    public static Brush Chip(QualityGrade g) => g switch
    {
        QualityGrade.ClearlyDegraded => B("#F7C1C1"),
        QualityGrade.VisiblyDegraded => B("#FAC775"),
        QualityGrade.Practical => B("#C0DD97"),
        QualityGrade.NearTransparent => B("#9FE1CB"),
        _ => B("#B5D4F4"),
    };

    public static Brush Card(QualityGrade g) => g switch
    {
        QualityGrade.ClearlyDegraded => B("#FCEBEB"),
        QualityGrade.VisiblyDegraded => B("#FAEEDA"),
        QualityGrade.Practical => B("#EAF3DE"),
        QualityGrade.NearTransparent => B("#E1F5EE"),
        _ => B("#E6F1FB"),
    };

    public static Brush Ink(QualityGrade g) => g switch
    {
        QualityGrade.ClearlyDegraded => B("#501313"),
        QualityGrade.VisiblyDegraded => B("#412402"),
        QualityGrade.Practical => B("#173404"),
        QualityGrade.NearTransparent => B("#04342C"),
        _ => B("#042C53"),
    };
}
