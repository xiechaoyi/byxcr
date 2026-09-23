using System.IO.Compression;
using Byxcr.Configuration;
using Byxcr.Execution;
using Byxcr.Logging;

namespace Byxcr.Services;

public sealed record CompressionReport(bool Success, bool UsedExternal, string? Error);

/// <summary>把镜像 tar 压缩为 gzip 流（归档文件名统一为 .tar，不带 .gz 后缀）。
/// 优先使用系统 gzip/pigz，缺失时回退到内置实现。</summary>
public sealed class GzipCompressor
{
    private readonly CompressionConfig _config;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _externalPath;
    private bool _resolved;

    public GzipCompressor(CompressionConfig config)
    {
        _config = config;
        _timeout = TimeSpan.FromSeconds(Math.Max(60, config.CommandTimeoutSeconds));
    }

    /// <summary>当前使用的外部压缩程序路径（未使用则为 null）。</summary>
    public string? ExternalTool { get; private set; }

    /// <summary>提前解析外部压缩程序，便于 doctor 等命令展示。</summary>
    public async Task<string?> DetectAsync(CancellationToken cancellationToken)
    {
        var mode = (_config.Mode ?? "auto").Trim().ToLowerInvariant();
        if (mode == "managed") return null;
        return await ResolveExternalAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompressionReport> CompressAsync(string sourceTar, string targetGz, Action<string>? log, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourceTar)) return new CompressionReport(false, false, $"待压缩文件不存在：{sourceTar}");

        PathLayout.EnsureDirectory(targetGz);
        TryDelete(targetGz);

        var mode = (_config.Mode ?? "auto").Trim().ToLowerInvariant();

        if (mode is "external" or "auto")
        {
            var executable = await ResolveExternalAsync(cancellationToken).ConfigureAwait(false);
            if (executable is not null)
            {
                var produced = await RunExternalAsync(executable, sourceTar, log, cancellationToken).ConfigureAwait(false);
                if (produced is not null && File.Exists(produced))
                {
                    try
                    {
                        File.Move(produced, targetGz, overwrite: true);
                        return new CompressionReport(true, true, null);
                    }
                    catch (Exception ex)
                    {
                        return new CompressionReport(false, true, $"移动压缩结果失败：{ex.Message}");
                    }
                }

                if (mode == "external")
                {
                    return new CompressionReport(false, true, "外部压缩程序执行失败，且已配置为仅使用外部压缩");
                }

                Log.Warn("外部压缩失败，回退为内置 gzip 实现");
            }
            else if (mode == "external")
            {
                return new CompressionReport(false, false, "未找到 gzip / pigz，且已配置为仅使用外部压缩");
            }
        }

        try
        {
            await CompressManagedAsync(sourceTar, targetGz, _config.Level, cancellationToken).ConfigureAwait(false);
            return new CompressionReport(true, false, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CompressionReport(false, false, ex.Message);
        }
    }

    private async Task<string?> ResolveExternalAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_resolved) return _externalPath;

            var configured = _config.GzipPath;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                _externalPath = BinaryLocator.Find(configured) ?? (File.Exists(configured) ? configured : null);
            }
            else
            {
                _externalPath = BinaryLocator.Find("pigz") ?? BinaryLocator.Find("gzip");
            }

            _resolved = true;
            ExternalTool = _externalPath;
            if (_externalPath is not null) Log.Debug($"使用外部压缩程序：{_externalPath}");
            return _externalPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> RunExternalAsync(string executable, string sourceTar, Action<string>? log, CancellationToken cancellationToken)
    {
        var level = Math.Clamp(_config.Level, 1, 9);
        var isPigz = Path.GetFileNameWithoutExtension(executable).Equals("pigz", StringComparison.OrdinalIgnoreCase);

        var arguments = new List<string>();
        if (isPigz)
        {
            var threads = _config.Threads > 0 ? _config.Threads : Environment.ProcessorCount;
            arguments.Add("-p");
            arguments.Add(Math.Clamp(threads, 1, 64).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        arguments.Add("-n");             // 不写入原始文件名与时间戳，保证结果可复现
        arguments.Add("-f");             // 覆盖输出
        arguments.Add("-" + level.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add(sourceTar);

        log?.Invoke($"$ {Path.GetFileName(executable)} {string.Join(' ', arguments)}");

        var result = await ProcessRunner.RunAsync(executable, arguments, _timeout, log, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            log?.Invoke($"[warn] 外部压缩失败：{result.Tail(300)}");
            return null;
        }

        var produced = sourceTar + ".gz";
        return File.Exists(produced) ? produced : null;
    }

    private static async Task CompressManagedAsync(string sourceTar, string targetGz, int level, CancellationToken cancellationToken)
    {
        var temporary = targetGz + ".part";
        var compressionLevel = level switch
        {
            <= 1 => CompressionLevel.Fastest,
            >= 9 => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };

        try
        {
            await using (var input = new FileStream(sourceTar, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var gzip = new GZipStream(output, compressionLevel, leaveOpen: false))
            {
                await input.CopyToAsync(gzip, 1 << 20, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, targetGz, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 忽略
        }
    }
}
