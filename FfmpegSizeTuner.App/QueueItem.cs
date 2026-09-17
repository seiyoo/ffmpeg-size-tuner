using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using FfmpegSizeTuner.Core;

namespace FfmpegSizeTuner.App;

public enum QueueStatus { Waiting, Running, Done, Failed, Cancelled }

/// <summary>キューの 1 行。追加した時点の設定を丸ごと抱える。</summary>
public sealed class QueueItem : INotifyPropertyChanged
{
    public required MediaInfo Source { get; init; }
    public required EncodeSettings Settings { get; init; }

    /// <summary>サンプル確認で既に確定している CRF。未確定なら null。</summary>
    public double? KnownCrf { get; init; }

    public string FileName => Source.FileName;
    public string SizeTarget => Settings.TargetLabel;
    public string Resolution => Settings.HeightLabel;
    public string Speed => Settings.Preset;
    public string Method => Settings.MethodLabel;

    private string _outputName = "";
    public string OutputName
    {
        get => _outputName;
        set => Set(ref _outputName, value);
    }

    private QueueStatus _status = QueueStatus.Waiting;
    public QueueStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) Notify(nameof(StatusText)); }
    }

    public string StatusText => Status switch
    {
        QueueStatus.Waiting   => "待機",
        QueueStatus.Running   => "実行中",
        QueueStatus.Done      => "完了",
        QueueStatus.Failed    => "失敗",
        _                     => "中止",
    };

    private double _progress;
    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    private string _detail = "";
    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    public string? OutPath { get; set; }

    /// <summary>
    /// 出力先を決める。同名があれば連番。決められないときは null。
    /// <paramref name="alsoTaken"/> でキュー内の他の行が既に確保した名前も避ける
    /// （同じ素材を設定違いで複数積んだとき、全行が同名に見えてしまうため）。
    /// </summary>
    public string? ResolveOutPath(Func<string, bool>? alsoTaken = null)
    {
        var stem = OutputNaming.BuildStem(
            Settings.NameMode, Source.BaseName, Settings.Suffix, Settings.ExplicitName);
        return OutputNaming.Resolve(Source.Directory, stem,
            path => File.Exists(path) || (alsoTaken?.Invoke(path) ?? false));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
