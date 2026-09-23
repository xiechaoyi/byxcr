using System.Diagnostics;
using System.Text;

namespace Byxcr.Execution;

/// <summary>外部命令执行结果。</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, bool Canceled)
{
    public bool Success => ExitCode == 0 && !TimedOut && !Canceled;

    public string Output
    {
        get
        {
            var stdout = StdOut.Trim();
            var stderr = StdErr.Trim();
            if (stdout.Length == 0) return stderr;
            if (stderr.Length == 0) return stdout;
            return stdout + Environment.NewLine + stderr;
        }
    }

    /// <summary>截取尾部若干字符，避免日志被刷屏。</summary>
    public string Tail(int maxLength = 800)
    {
        var text = Output;
        if (text.Length <= maxLength) return text;
        return "..." + text[^maxLength..];
    }
}

/// <summary>以子进程方式执行外部命令，支持超时、取消与实时输出回调。</summary>
public static class ProcessRunner
{
    private const int MaxCaptureChars = 512 * 1024;

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { outDone.TrySetResult(true); return; }
            Capture(stdout, e.Data);
            Notify(onLine, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { errDone.TrySetResult(true); return; }
            Capture(stderr, e.Data);
            Notify(onLine, e.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message, false, false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var stopwatch = Stopwatch.StartNew();
        var exited = process.WaitForExitAsync();
        var timedOut = false;
        var canceled = false;

        while (!exited.IsCompleted)
        {
            var winner = await Task.WhenAny(exited, Task.Delay(200)).ConfigureAwait(false);
            if (winner == exited) break;

            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                TryKill(process);
                break;
            }

            if (timeout > TimeSpan.Zero && stopwatch.Elapsed > timeout)
            {
                timedOut = true;
                TryKill(process);
                break;
            }
        }

        try { await exited.ConfigureAwait(false); } catch (Exception) { /* 已被强杀 */ }

        var flush = Task.WhenAll(outDone.Task, errDone.Task);
        await Task.WhenAny(flush, Task.Delay(5000)).ConfigureAwait(false);

        var exitCode = -1;
        try { exitCode = process.HasExited ? process.ExitCode : -1; } catch (InvalidOperationException) { }

        return new ProcessResult(exitCode, stdout.ToString(), stderr.ToString(), timedOut, canceled);
    }

    private static void Capture(StringBuilder builder, string line)
    {
        if (builder.Length >= MaxCaptureChars) return;
        builder.AppendLine(line);
    }

    private static void Notify(Action<string>? onLine, string line)
    {
        if (onLine is null) return;
        // 进度条类输出使用 \r 刷新，只保留最后一段，避免日志刷屏
        var normalized = line;
        var carriage = normalized.LastIndexOf('\r');
        if (carriage >= 0) normalized = normalized[(carriage + 1)..];
        normalized = normalized.TrimEnd();
        if (normalized.Length == 0) return;
        try { onLine(normalized); } catch (Exception) { /* 回调异常不应影响命令执行 */ }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 进程可能已经退出
        }
    }
}
