using System.Diagnostics;
using System.Text;

namespace FfmpegSizeTuner.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// 外部プロセスの実行。ArgumentList を使うので、空白や日本語を含むパスの
/// クォート処理を自前で行う必要がない(PowerShell 版で苦労した箇所)。
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string exe,
        IEnumerable<string> args,
        string? workingDirectory = null,
        Action<string>? onStdOutLine = null,
        Action<string>? onStdErrLine = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var outBuf = new StringBuilder();
        var errBuf = new StringBuilder();

        if (!proc.Start()) throw new InvalidOperationException($"{exe} を起動できませんでした。");
        proc.StandardInput.Close();

        var readOut = PumpAsync(proc.StandardOutput, outBuf, onStdOutLine);
        var readErr = PumpAsync(proc.StandardError,  errBuf, onStdErrLine);

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }
        await Task.WhenAll(readOut, readErr).ConfigureAwait(false);

        return new ProcessResult(proc.ExitCode, outBuf.ToString(), errBuf.ToString());
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder sink, Action<string>? onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            sink.AppendLine(line);
            onLine?.Invoke(line);
        }
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* 既に終了 */ }
    }
}
