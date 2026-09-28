using System.Diagnostics;
using Byxcr.Configuration;
using Byxcr.Core;
using Byxcr.Persistence;
using Byxcr.Logging;
using Byxcr.Registry;

namespace Byxcr.Services;

/// <summary>
/// 单个镜像的完整同步流水线：
/// 按镜像源列表逐个尝试 → 用 HTTP 拉取全部平台并组装 OCI 镜像布局 → gzip 压缩
/// → 写入归档目标（本地目录或 WebDAV）→ 落库。
/// <para>压缩后的归档文件名统一为 .tar（不带 .gz），容器运行环境按内容自动识别 gzip。</para>
/// </summary>
public sealed class ImageSyncService
{
    /// <summary>下载通道标识，写入 sync_records.channel。</summary>
    public const string Channel = "oci";

    /// <summary>手动 sync 指令的 trigger：不受并发限制，始终执行。</summary>
    public const string ManualTrigger = "cli";

    /// <summary>同步租约（代替 .lock 文件）的最长持有时间；进程异常退出留下的租约超过该时间即可被抢占。</summary>
    public const int LeaseSeconds = 1800;

    private readonly AppConfig _config;
    private readonly PathLayout _paths;
    private readonly ImageStore _images;
    private readonly SyncRecordStore _records;
    private readonly RegistryResolver _resolver;
    private readonly RegistryClient _registryClient;
    private readonly GzipCompressor _gzip;
    private readonly OciImagePuller _puller;
    private readonly IArchiveSink _sink;

    public ImageSyncService(
        AppConfig config,
        PathLayout paths,
        ImageStore images,
        SyncRecordStore records,
        RegistryResolver resolver,
        RegistryClient registryClient,
        GzipCompressor gzip,
        OciImagePuller puller,
        IArchiveSink sink)
    {
        _config = config;
        _paths = paths;
        _images = images;
        _records = records;
        _resolver = resolver;
        _registryClient = registryClient;
        _gzip = gzip;
        _puller = puller;
        _sink = sink;
    }

    /// <summary>
    /// 执行一次同步。除「该镜像正被其它进程同步而跳过」外，始终返回并落库一条记录（不向调用方抛业务异常）。
    /// </summary>
    /// <param name="outputOverride">
    /// 本次同步专用的归档位置（已解析好的相对路径，如 <c>sync -o</c> 指定），
    /// 非空时覆盖任务的 <see cref="ImageTask.Output"/>，但**不写回数据库**。
    /// </param>
    public async Task<SyncRecord> SyncAsync(
        ImageTask task,
        string trigger,
        bool force,
        CancellationToken cancellationToken,
        string? outputOverride = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var record = new SyncRecord
        {
            ImageId = task.Id,
            Image = task.Image,
            Status = "failed",
            Trigger = trigger,
            StartedAt = Clock.Now(),
        };

        // 同一镜像同一时刻只允许一次同步：租约记在数据库 images 表里（不再生成 .lock 文件），最长 1800 秒，
        // 进程崩溃/被杀留下的租约超时后自动失效，不会永久堵住后续下载。
        // 手动 sync 指令（trigger = cli）不受并发限制：不因别人正在同步而跳过。
        var leaseOwner = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var enforceLease = !string.Equals(trigger, ManualTrigger, StringComparison.OrdinalIgnoreCase);

        if (enforceLease && !_images.TryAcquireLease(task.Id, leaseOwner, LeaseSeconds))
        {
            record.Status = "skipped";
            record.Message = $"该镜像正在同步中（{LeaseSeconds} 秒内不重复触发），跳过本次";
            Log.Warn($"[{task.Image}] 该镜像正在同步中，跳过本次触发（{LeaseSeconds} 秒后自动解除）");
            return record;
        }

        try
        {
            try
            {
                _images.SetStatus(task.Id, "running");
                await ExecuteAsync(task, record, force, outputOverride, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                record.Status = "failed";
                record.Message = "已取消";
                throw;
            }
            catch (Exception ex)
            {
                record.Status = "failed";
                record.Message = ex.Message;
                Log.Error($"[{task.Image}] 同步失败：{ex.Message}");
            }

            return await FinishAsync(task, record, stopwatch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (enforceLease)
            {
                try { _images.ReleaseLease(task.Id, leaseOwner); }
                catch (Exception ex) { Log.Debug($"释放同步租约失败：{ex.Message}"); }
            }
        }
    }

    private async Task ExecuteAsync(
        ImageTask task,
        SyncRecord record,
        bool force,
        string? outputOverride,
        CancellationToken cancellationToken)
    {
        if (!ImageReference.TryParse(task.Image, out var image))
            throw new ByxcrException($"镜像名称无法解析：{task.Image}");

        var log = CreateLogger(task.Image);
        record.Channel = Channel;

        // 归档落盘位置：本次同步指定了输出位置（sync -o）优先，其次任务上的 output，都没有才走默认规则
        var relative = outputOverride ?? ArchiveNaming.Resolve(image, task.Output);

        var (candidates, _) = await _resolver.ResolveAsync(image, MirrorOverrides, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
            throw new ByxcrException("没有可用的镜像源，请检查 registry.items 配置");

        // 摘要预检（增量）：先查远端 manifest 摘要，和上次落库的 last_digest 比对。
        // 只有「摘要未变化 且 归档已存在」才跳过 —— 否则继续走下面的完整下载。
        string? remoteDigest = null;
        if (!force && CheckStrategy() == "digest")
        {
            var probed = await TryGetRemoteDigestAsync(candidates, image, cancellationToken).ConfigureAwait(false);
            remoteDigest = probed?.Digest;

            if (remoteDigest is null)
            {
                Log.Warn($"[{task.Image}] 未能获取远端摘要，跳过增量比对，直接重新下载");
            }
            else if (string.IsNullOrEmpty(task.LastDigest))
            {
                Log.Info($"[{task.Image}] 尚无历史摘要，首次下载（{Format.ShortDigest(remoteDigest)}）");
            }
            else if (string.Equals(remoteDigest, task.LastDigest, StringComparison.OrdinalIgnoreCase))
            {
                var registryName = probed!.Value.Candidate.IsDockerHubRegistry ? string.Empty : probed.Value.Candidate.Name;

                // sync.ignoreArchiveCheck = true：不再探测归档目标，摘要未变化就直接跳过
                if (_config.Sync.IgnoreArchiveCheck)
                {
                    record.Status = "skipped";
                    record.Digest = remoteDigest;
                    record.Registry = registryName;
                    record.FilePath = _sink.Locate(relative);
                    record.Message = "镜像内容无变化，跳过下载（已忽略归档检查）";
                    Log.Ok($"[{task.Image}] 镜像内容无变化（{Format.ShortDigest(remoteDigest)}）");
                    return;
                }

                var probe = await _sink.ProbeAsync(relative, cancellationToken).ConfigureAwait(false);
                if (probe.Exists)
                {
                    record.Status = "skipped";
                    record.Digest = remoteDigest;
                    record.Registry = registryName;
                    record.FilePath = _sink.Locate(relative);
                    record.FileSize = probe.Size;
                    record.Message = "镜像内容无变化，跳过下载";
                    Log.Ok($"[{task.Image}] 镜像内容无变化（{Format.ShortDigest(remoteDigest)}），跳过下载");
                    return;
                }

                Log.Warn($"[{task.Image}] 镜像内容无变化但归档缺失，重新下载");
            }
            else
            {
                Log.Note($"[{task.Image}] 镜像内容已更新（{Format.ShortDigest(task.LastDigest)} → {Format.ShortDigest(remoteDigest)}），重新下载");
            }
        }

        Log.Note($"[{task.Image}] 开始下载 → {_sink.Locate(relative)}");

        var tarPath = _paths.TempTarFile(image);
        var errors = new List<string>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attempts = Math.Max(1, _config.Sync.RetryTimes + 1);
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    if (attempt > 1)
                    {
                        Log.Warn($"[{task.Image}] 第 {attempt} 次尝试 {candidate.DisplayName} …");
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _config.Sync.RetryDelaySeconds)), cancellationToken).ConfigureAwait(false);
                    }

                    // 拉取阶段不再打印平台信息，改为在原行上原地刷新进度百分比：
                    // [12:00:00 INF] [x/y:tag] 从 <源> 下载 OCI 镜像… 42%（15.2 MB / 36.1 MB）
                    // 该行只输出一次（首帧无百分比），后续帧原地覆盖；下载结束后把这一行**直接换成**
                    // 完成摘要（完成下载 → 2.17 MB · 2 blob · 3 平台），不会再多出一行。
                    DownloadOutcome outcome;
                    using (var bar = Log.BeginProgress($"[{task.Image}] 从 {candidate.DisplayName} 下载 OCI 镜像…"))
                    {
                        outcome = await DownloadAsync(
                            image, relative, candidate, tarPath, log,
                            (downloaded, total) => bar.Update(Format.Progress(downloaded, total)),
                            cancellationToken).ConfigureAwait(false);

                        record.Status = "success";
                        record.Registry = candidate.IsDockerHubRegistry ? string.Empty : candidate.Name;
                        record.Digest = outcome.Digest ?? remoteDigest;
                        record.FilePath = outcome.Location;
                        record.FileSize = outcome.Size;
                        record.Message = outcome.Note;

                        bar.Complete($"[{task.Image}] 完成下载 → {Format.Size(outcome.Size)}{Summarize(outcome.Note)}",
                            ConsoleColor.Green);
                    }

                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (UnrecoverableImageException ex)
                {
                    // 与镜像源无关的确定性失败（清单结构、摘要校验等），换源重试也没有意义
                    CleanupTemporary(tarPath);
                    Log.Warn($"[{task.Image}] {ex.Message}");
                    throw;
                }
                catch (ArchiveTargetException ex)
                {
                    // 归档目标写不进去与镜像源无关，换源只会白白重新下载
                    CleanupTemporary(tarPath);
                    Log.Error($"[{task.Image}] {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    var text = $"{candidate.DisplayName}: {ex.Message}";
                    if (!errors.Contains(text)) errors.Add(text);
                    Log.Warn($"[{task.Image}] 源 {candidate.DisplayName} 失败：{ex.Message}");
                    CleanupTemporary(tarPath);
                }
            }
        }

        if (errors.Count == 0) errors.Add("所有镜像源均未成功");
        throw new ByxcrException("所有镜像源尝试均失败 → " + string.Join(" | ", errors));
    }

    private async Task<DownloadOutcome> DownloadAsync(
        ImageReference image,
        string relativePath,
        RegistryCandidate candidate,
        string tarPath,
        Action<string>? log,
        Action<long, long>? progress,
        CancellationToken cancellationToken)
    {
        var pull = await _puller.PullAsync(image, candidate, tarPath, log, progress, cancellationToken).ConfigureAwait(false);

        // gzip 压缩，但归档文件名统一为 .tar（不带 .gz）：容器运行环境按内容自动识别压缩，
        // podman load / docker load 都能直接吃下，也省去手工解压。
        var gzPath = tarPath + ".gz";
        var compression = await _gzip.CompressAsync(tarPath, gzPath, log, cancellationToken).ConfigureAwait(false);
        if (!compression.Success || !File.Exists(gzPath))
            throw new ByxcrException($"gzip 压缩失败：{compression.Error ?? "未知错误"}");

        if (!_paths.KeepTar) TryDelete(tarPath);

        // relativePath 已由 ArchiveNaming 算成 .tar，这里写入的是它的 gzip 压缩内容
        var written = await _sink.WriteAsync(gzPath, relativePath, log, cancellationToken).ConfigureAwait(false);
        TryDelete(gzPath);

        // 临时 tar / tar.gz 都在系统临时目录里，用完整仓目录一并清掉，避免留下空目录骨架
        _paths.PruneEmptyDirectories(tarPath);

        // 记录说明只保留「下载了几个 blob」与架构列表，例如：1 blob · linux/amd64 · linux/arm/v7
        // 架构先去重；去重后超过 3 个时折叠为数量，例如：80 blob · 8 平台
        var note = $"{pull.BlobCount} 个blob";
        var platforms = pull.PlatformText;
        if (platforms.Length > 0) note += $" · {platforms}";
        return new DownloadOutcome(pull.Digest, written.Size, written.Location, note);
    }

    private async Task<SyncRecord> FinishAsync(ImageTask task, SyncRecord record, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        stopwatch.Stop();
        record.DurationMs = stopwatch.ElapsedMilliseconds;
        record.FinishedAt = Clock.Now();

        var success = record.Status is "success" or "skipped";
        try
        {
            _images.MarkChecked(
                task.Id,
                record.Status,
                success ? null : record.Message,
                record.Digest,
                record.FilePath,
                record.FinishedAt,
                success);
        }
        catch (Exception ex)
        {
            Log.Warn($"更新镜像状态失败：{ex.Message}");
        }

        try
        {
            _records.Add(record);
        }
        catch (Exception ex)
        {
            Log.Warn($"写入同步记录失败：{ex.Message}");
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return record;
    }

    private string CheckStrategy()
    {
        var strategy = (_config.Sync.CheckStrategy ?? "digest").Trim().ToLowerInvariant();
        return strategy == "always" ? "always" : "digest";
    }

    private static IReadOnlyList<string>? MirrorOverrides => null;

    /// <summary>
    /// 按候选镜像源顺序查询远端 manifest 摘要，返回第一个成功的源与摘要。
    /// 某个源探测失败（超时/鉴权失败）时继续试下一个，避免「第一个源不可用 → 误判无变化」。
    /// 全部失败返回 null，调用方回退为直接下载。
    /// </summary>
    private async Task<(string Digest, RegistryCandidate Candidate)?> TryGetRemoteDigestAsync(
        IReadOnlyList<RegistryCandidate> candidates,
        ImageReference image,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var digest = await _registryClient.GetRemoteDigestAsync(candidate, image, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(digest)) return (digest, candidate);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 该源查询摘要失败，换下一个候选源
            }
        }

        return null;
    }

    private void CleanupTemporary(string tarPath)
    {
        TryDelete(tarPath);
        TryDelete(tarPath + ".gz");
        TryDelete(tarPath + ".gz.part");
        _paths.PruneEmptyDirectories(tarPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 忽略清理失败
        }
    }

    private static Action<string>? CreateLogger(string image)
    {
        if (!Log.IsEnabled(LogLevel.Debug)) return null;
        return line => Log.Debug($"[{image}] {line}");
    }

    public static string FormatSize(long bytes) => Format.Size(bytes);

    /// <summary>完成行里的摘要后缀：blob 数与架构列表；为空时不加分隔符。</summary>
    private static string Summarize(string? note)
        => string.IsNullOrWhiteSpace(note) ? string.Empty : " · " + note.Trim();

    private sealed record DownloadOutcome(string? Digest, long Size, string Location, string Note);
}
