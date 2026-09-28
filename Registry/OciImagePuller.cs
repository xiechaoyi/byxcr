using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Byxcr.Configuration;
using Byxcr.Core;
using Byxcr.Logging;
using Byxcr.Services;

namespace Byxcr.Registry;

/// <summary>一次 OCI 归档拉取的结果。</summary>
public sealed record OciPullResult(
    string? Digest,
    IReadOnlyList<string> Platforms,
    int BlobCount,
    int LayerCount,
    long DownloadedBytes)
{
    /// <summary>去重后的架构列表，保持清单里的出现顺序。</summary>
    public IReadOnlyList<string> DistinctPlatforms => Format.DistinctPlatforms(Platforms);

    /// <summary>
    /// 架构摘要文本：去重后用 <c> · </c> 连接，超过 3 个时折叠为数量（如 <c>8 个平台</c>）。
    /// 没有平台信息时返回空串。
    /// </summary>
    public string PlatformText => Format.PlatformText(Platforms);
}

/// <summary>
/// Registry V2 下载器：通过 HTTP 拉取镜像，并组装成 <b>OCI 镜像布局</b>。
/// <para>
/// 输出结构（标准 image-layout + 一份 docker 兼容清单）：
/// <code>
/// oci-layout                 {"imageLayoutVersion":"1.0.0"}
/// index.json                 根索引；只含一条 manifest（podman load / docker load 要求归档内恰好一个镜像）
/// manifest.json              docker-archive 兼容清单；传统 docker（graph driver）的 docker load 只认它
/// blobs/sha256/&lt;hex&gt;        各清单 / 配置 / 图层 blob，与上游逐字节一致
/// </code>
/// </para>
/// <para>
/// 多架构镜像（manifest list / OCI index）会下载清单中的<strong>全部平台</strong>并合并进同一个 tar，
/// blob 按 digest 去重；可用 <c>download.platforms</c> 限定只拉取部分平台。
/// 多平台时各平台清单先合成「内层索引」再被根索引指向，结构与 <c>skopeo copy --all</c> 的产物一致。
/// </para>
/// <para>
/// 同一个 tar 同时满足两类读取方：podman / 新版 Docker（含 containerd 镜像存储）按 OCI 布局导入，
/// 平台齐全；传统 Docker（graph driver）按 <c>manifest.json</c> 走 docker-archive 路径导入，
/// 只能得到一个平台 —— 与 <c>containerd</c> 导出器（buildkit <c>type=docker</c>）的做法一致。
/// </para>
/// </summary>
public sealed class OciImagePuller : IDisposable
{
    private const int CopyBufferSize = 1 << 20;
    private const int ProgressIntervalSeconds = 10;
    private const int ProgressTickMilliseconds = 250;
    private const long UstarSizeLimit = 0x1FFFFFFFF;

    private const string OciIndexMediaType = "application/vnd.oci.image.index.v1+json";
    private const string OciManifestMediaType = "application/vnd.oci.image.manifest.v1+json";
    private const string DockerManifestListMediaType = "application/vnd.docker.distribution.manifest.list.v2+json";
    private const string DockerManifestMediaType = "application/vnd.docker.distribution.manifest.v2+json";
    private const string LayoutVersion = "1.0.0";
    private const string RefNameAnnotation = "org.opencontainers.image.ref.name";

    private static readonly string[] ManifestAccept =
    [
        OciIndexMediaType,
        DockerManifestListMediaType,
        OciManifestMediaType,
        DockerManifestMediaType,
    ];

    private static readonly string[] ConfigAccept =
    [
        "application/vnd.oci.image.config.v1+json",
        "application/vnd.docker.container.image.v1+json",
        "application/json",
    ];

    private static readonly string[] BlobAccept =
    [
        "application/vnd.docker.image.rootfs.diff.tar.gzip",
        "application/vnd.oci.image.layer.v1.tar+gzip",
        "application/vnd.oci.image.layer.v1.tar+zstd",
        "application/octet-stream",
        "*/*",
    ];

    private readonly AppConfig _config;
    private readonly PathLayout _paths;
    private readonly RegistryTransport _transport;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _probeTimeout;
    private readonly int _attempts;
    private readonly int _parallelism;

    /// <summary>预检（只取一次清单）允许的最长等待：清单只有几百字节，不该按图层下载的超时来等。</summary>
    private const int ProbeTimeoutCeilingSeconds = 20;

    public OciImagePuller(AppConfig config, PathLayout paths)
    {
        _config = config;
        _paths = paths;
        _timeout = TimeSpan.FromSeconds(config.Download.RequestTimeoutSeconds);
        _probeTimeout = TimeSpan.FromSeconds(Math.Min(config.Download.RequestTimeoutSeconds, ProbeTimeoutCeilingSeconds));
        _attempts = config.Download.MaxRetries;
        _parallelism = config.Download.MaxParallelDownloads;
        _transport = new RegistryTransport(config.Registry.Credentials);
    }

    /// <summary>当前生效的平台策略描述，例如「全部架构」或 linux/amd64、linux/arm64。</summary>
    public string PlatformPolicy
    {
        get
        {
            var filter = ParseFilter(_config.Download.Platforms);
            return filter.Count == 0 ? "全部架构" : string.Join("、", filter.Select(spec => spec.Display));
        }
    }

    /// <summary>下载镜像并写出 OCI 镜像布局 tar（压缩由调用方 ImageSyncService 负责）。</summary>
    /// <param name="progress">图层下载进度回调：(已下载字节, 计划下载字节)。</param>
    public async Task<OciPullResult> PullAsync(
        ImageReference image,
        RegistryCandidate candidate,
        string tarPath,
        Action<string>? log,
        Action<long, long>? progress,
        CancellationToken cancellationToken)
    {
        var reference = image.Digest.Length > 0 ? image.Digest : image.Tag;
        var top = await FetchBytesAsync(candidate, image.Repository, $"manifests/{reference}", ManifestAccept, "获取镜像清单", cancellationToken)
            .ConfigureAwait(false);

        var filter = ParseFilter(_config.Download.Platforms);
        var targets = BuildTargets(top, filter, log);

        // 1) 逐个平台抓取清单与 config（体积极小），据此确定需要写入的 blob 全集
        var platforms = new List<PlatformImage>(targets.Count);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            platforms.Add(await LoadPlatformAsync(candidate, image, target, cancellationToken).ConfigureAwait(false));
        }

        var blobs = CollectBlobs(platforms);
        var pending = blobs.Where(pair => pair.Value.Bytes is null).ToList();

        var workDirectory = _paths.PullWorkDirectory(image);
        ResetDirectory(workDirectory);

        try
        {
            // 2) 并发下载所有图层 blob 到临时目录，逐个校验 sha256
            if (pending.Count > 0)
            {
                await DownloadBlobsAsync(candidate, image.Repository, pending, workDirectory, log, progress, cancellationToken).ConfigureAwait(false);
            }

            // 3) 组装 OCI 镜像布局 tar
            await WriteLayoutAsync(tarPath, image, platforms, blobs, workDirectory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
            _paths.PruneEmptyDirectories(workDirectory);
        }

        var layerCount = platforms.Sum(item => item.Layers.Count);
        var downloaded = platforms.Sum(item => item.ConfigBytes.LongLength + item.ManifestBytes.LongLength)
                         + pending.Sum(pair => pair.Value.Length);

        return new OciPullResult(
            top.Digest,
            platforms.Select(item => item.Platform.Display).ToList(),
            blobs.Count,
            layerCount,
            downloaded);
    }

    // ------------------------------------------------------------ 清单与平台

    /// <summary>
    /// 只取一次顶层清单，判断「仓库 + 标签」在本源上能否拉取（add 前的预检）。
    /// 不下载任何图层、不落盘；平台过滤与正式下载完全一致，因此平台不匹配也会被这里挡下。
    /// </summary>
    public async Task<ImageProbeResult> ProbeAsync(
        ImageReference image,
        RegistryCandidate candidate,
        CancellationToken cancellationToken)
    {
        var reference = image.Digest.Length > 0 ? image.Digest : image.Tag;
        var url = $"v2/{image.Repository}/manifests/{reference}";

        try
        {
            using var fetch = await _transport
                .FetchAsync(candidate, image.Repository, url, ManifestAccept, _probeTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (!fetch.Success)
            {
                // 404/403 是「这个源明确说没有」，属于确定性结论
                var definitive = fetch.StatusCode is 404 or 403;
                return ImageProbeResult.Failure(DescribeStatus(candidate, fetch.StatusCode, fetch.Error), definitive);
            }

            using var buffer = new MemoryStream();
            await fetch.Content!.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            var bytes = buffer.ToArray();
            var digest = string.IsNullOrWhiteSpace(fetch.Digest) ? "sha256:" + Sha256Hex(bytes) : fetch.Digest;

            List<ManifestTarget> targets;
            try
            {
                targets = BuildTargets(new Fetched(bytes, digest, fetch.ContentType), ParseFilter(_config.Download.Platforms), null);
            }
            catch (UnrecoverableImageException ex)
            {
                // 清单里没有可下载的平台（含 filter 不匹配）：换源也不会变，直接告知
                return ImageProbeResult.Failure($"{candidate.DisplayName}：{ex.Message}", definitive: true);
            }
            catch (JsonException)
            {
                return ImageProbeResult.Failure($"{candidate.DisplayName}：返回内容不是合法的清单 JSON（该地址可能不是镜像仓库）", definitive: true);
            }

            return ImageProbeResult.Success(
                candidate.DisplayName,
                digest,
                targets.Select(target => target.Platform?.Display ?? string.Empty));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ImageProbeResult.Failure($"{candidate.DisplayName}：{ex.Message}");
        }
    }

    /// <summary>把拉清单失败的状态码翻译成用户能看懂的原因。</summary>
    private static string DescribeStatus(RegistryCandidate candidate, int statusCode, string? error)
    {
        var reason = statusCode switch
        {
            401 => "需要认证或凭据无效（HTTP 401）",
            403 => "仓库或标签不存在，或没有访问权限（HTTP 403）",
            404 => "镜像仓库或标签不存在（HTTP 404）",
            429 => "请求过于频繁，稍后重试（HTTP 429）",
            _ => string.IsNullOrWhiteSpace(error) ? $"HTTP {statusCode}" : TrimUrl(error),
        };

        return $"{candidate.DisplayName}：{reason}";
    }

    /// <summary>去掉错误文本里附带的完整 URL（如「请求超时（300 秒）：https://…」），只留结论。</summary>
    private static string TrimUrl(string error)
    {
        var index = error.IndexOf("：http", StringComparison.Ordinal);
        return index > 0 ? error[..index] : error;
    }

    private static List<ManifestTarget> BuildTargets(Fetched top, List<PlatformSpec> filter, Action<string>? log)
    {
        var targets = new List<ManifestTarget>();

        using var document = JsonDocument.Parse(top.Bytes);
        var root = document.RootElement;

        if (!IsIndex(root))
        {
            var mediaType = GetString(root, "mediaType");
            if (mediaType.Length == 0) mediaType = InferMediaType(top.ContentType);
            targets.Add(new ManifestTarget(mediaType, top.Digest, top.Bytes));
            return targets;
        }

        var available = new List<string>();
        foreach (var item in root.GetProperty("manifests").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var digest = GetString(item, "digest");
            if (digest.Length == 0) continue;

            var mediaType = GetString(item, "mediaType");
            if (!IsImageManifest(mediaType)) continue;

            var platform = ReadPlatform(item);
            if (platform is null || platform.Os.Length == 0 || platform.Architecture.Length == 0) continue;

            // buildkit 的 unknown/unknown 证明清单不是可运行镜像
            if (platform.Os.Equals("unknown", StringComparison.OrdinalIgnoreCase)) continue;

            available.Add(platform.Display);
            if (filter.Count > 0 && !filter.Any(spec => spec.Matches(platform))) continue;

            targets.Add(new ManifestTarget(mediaType.Length == 0 ? OciManifestMediaType : mediaType, digest, null, platform));
        }

        if (targets.Count == 0)
        {
            throw new UnrecoverableImageException(available.Count == 0
                ? "镜像索引中没有任何可下载的平台清单"
                : $"镜像不包含指定平台（download.platforms = {string.Join("、", filter.Select(spec => spec.Display))}），可选：{string.Join("、", available)}");
        }

        Log.Debug($"多架构索引：共 {available.Count} 个平台，本次拉取 {targets.Count} 个 → {string.Join("、", targets.Select(t => t.Platform!.Display))}");
        log?.Invoke($"多架构索引：共 {available.Count} 个平台，本次拉取 {targets.Count} 个");
        return targets;
    }

    private async Task<PlatformImage> LoadPlatformAsync(
        RegistryCandidate candidate,
        ImageReference image,
        ManifestTarget target,
        CancellationToken cancellationToken)
    {
        byte[] manifestBytes;
        if (target.Bytes is not null)
        {
            manifestBytes = target.Bytes;
        }
        else
        {
            var fetched = await FetchBytesAsync(
                candidate, image.Repository, $"manifests/{target.Digest}", ManifestAccept,
                $"获取 {(target.Platform is null ? string.Empty : target.Platform.Display + " ")}镜像清单", cancellationToken).ConfigureAwait(false);

            if (!string.Equals(fetched.Digest, target.Digest, StringComparison.OrdinalIgnoreCase))
            {
                // 极少数镜像源会重新序列化清单；此时以实际字节摘要为准，保证归档内部自洽
                Log.Warn($"[{image.CanonicalName}] {candidate.DisplayName} 返回的清单摘要与索引不一致"
                         + $"（索引 {Format.ShortDigest(target.Digest)} / 实际 {Format.ShortDigest(fetched.Digest)}），已按实际内容记录");
            }

            manifestBytes = fetched.Bytes;
        }

        var manifestDigest = "sha256:" + Sha256Hex(manifestBytes);

        string configDigest;
        string configMediaType;
        List<BlobDescriptor> layers;
        using (var document = JsonDocument.Parse(manifestBytes))
        {
            (configDigest, configMediaType, layers) = ReadImageManifest(document.RootElement);
        }

        var config = await FetchBytesAsync(
            candidate, image.Repository, $"blobs/{configDigest}", ConfigAccept,
            $"获取 {(target.Platform is null ? string.Empty : target.Platform.Display + " ")}镜像配置", cancellationToken).ConfigureAwait(false);

        if (!string.Equals(config.Digest, configDigest, StringComparison.OrdinalIgnoreCase))
            throw new UnrecoverableImageException($"镜像配置摘要不匹配：期望 {configDigest}，实际 {config.Digest}");

        var platform = target.Platform ?? ReadPlatformFromConfig(config.Bytes) ?? new PlatformInfo("linux", "unknown", string.Empty, string.Empty);
        Log.Debug($"平台 {platform.Display}：{layers.Count} 个图层，manifest {Format.ShortDigest(manifestDigest)}");

        return new PlatformImage(target.MediaType, manifestDigest, manifestBytes, configDigest, configMediaType, config.Bytes, layers, platform);
    }

    /// <summary>汇总所有需要写入归档的 blob（按 digest 去重）。</summary>
    private static List<KeyValuePair<string, BlobSource>> CollectBlobs(IReadOnlyList<PlatformImage> platforms)
    {
        var blobs = new Dictionary<string, BlobSource>(StringComparer.OrdinalIgnoreCase);

        foreach (var platform in platforms)
        {
            blobs.TryAdd(Format.DigestHex(platform.Digest), new BlobSource(platform.ManifestBytes, platform.ManifestMediaType, 0));
            blobs.TryAdd(Format.DigestHex(platform.ConfigDigest), new BlobSource(platform.ConfigBytes, platform.ConfigMediaType, 0));

            foreach (var layer in platform.Layers)
            {
                var hex = Format.DigestHex(layer.Digest);
                if (hex.Length == 0) continue;
                blobs.TryAdd(hex, new BlobSource(null, layer.MediaType, layer.Size) { DigestValue = layer.Digest });
            }
        }

        return blobs.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
    }

    // ---------------------------------------------------------------- 下载

    private async Task DownloadBlobsAsync(
        RegistryCandidate candidate,
        string repository,
        List<KeyValuePair<string, BlobSource>> pending,
        string workDirectory,
        Action<string>? log,
        Action<long, long>? progress,
        CancellationToken cancellationToken)
    {
        var expected = pending.Sum(pair => pair.Value.Length);
        var written = 0L;
        var active = 0;

        using var stop = new CancellationTokenSource();
        var reporter = ReportProgressAsync(() => Interlocked.Read(ref written), expected, () => Volatile.Read(ref active), log, progress, stop.Token);

        using var gate = new SemaphoreSlim(_parallelism, _parallelism);
        var tasks = new List<Task>(pending.Count);

        foreach (var (hex, source) in pending)
        {
            var path = Path.Combine(workDirectory, hex);
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref active);
                try
                {
                    await DownloadBlobAsync(
                        candidate, repository, source.DigestValue, path,
                        delta => Interlocked.Add(ref written, delta), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                    gate.Release();
                }
            }, cancellationToken));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);

            // 收尾：清单声明的体积与实际字节可能略有出入，这里以「下载阶段结束」为准停在 100%
            progress?.Invoke(expected, expected);
        }
        finally
        {
            stop.Cancel();
            await reporter.ConfigureAwait(false);
        }

        Log.Debug($"共下载 {pending.Count} 个 blob，合计 {Format.Size(Interlocked.Read(ref written))}");
    }

    private async Task<long> DownloadBlobAsync(
        RegistryCandidate candidate,
        string repository,
        string digest,
        string targetPath,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        var url = $"v2/{repository}/blobs/{digest}";
        var label = $"blob {Format.ShortDigest(digest)}";
        var temporary = targetPath + ".part";
        string? lastError = null;

        for (var attempt = 1; attempt <= _attempts; attempt++)
        {
            if (attempt > 1)
            {
                Log.Debug($"{label} 第 {attempt} 次尝试 …");
                await Task.Delay(TimeSpan.FromMilliseconds(600 * attempt), cancellationToken).ConfigureAwait(false);
            }

            using var fetch = await _transport
                .FetchAsync(candidate, repository, url, BlobAccept, _timeout, cancellationToken)
                .ConfigureAwait(false);

            if (!fetch.Success)
            {
                lastError = fetch.Error ?? $"HTTP {fetch.StatusCode}";
                continue;
            }

            try
            {
                var (bytes, hash) = await CopyToFileAsync(fetch.Content!, temporary, progress, cancellationToken).ConfigureAwait(false);
                if (bytes == 0) throw new ByxcrException("响应内容为空");

                var expected = Format.DigestHex(digest);
                if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
                    throw new UnrecoverableImageException($"{label} 内容校验失败：期望 {expected}，实际 {hash}");

                File.Move(temporary, targetPath, overwrite: true);
                return bytes;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (UnrecoverableImageException)
            {
                TryDelete(temporary);
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                TryDelete(temporary);
            }
        }

        TryDelete(temporary);
        throw new ByxcrException($"{label} 下载失败（{candidate.DisplayName}）：{lastError}");
    }

    private static async Task<(long Bytes, string Sha256Hex)> CopyToFileAsync(
        Stream source,
        string targetPath,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        PathLayout.EnsureDirectory(targetPath);

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long written = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            await using var file = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;

                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                progress?.Invoke(read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        return (written, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task ReportProgressAsync(
        Func<long> written,
        long expected,
        Func<int> active,
        Action<string>? log,
        Action<long, long>? progress,
        CancellationToken cancellationToken)
    {
        if (expected <= 0 || (log is null && progress is null)) return;

        // 按 250ms 采样（是否真正刷新由 Log 侧限频），debug 日志仍保持 10 秒一条
        var debugTicks = Math.Max(1, ProgressIntervalSeconds * 1000 / ProgressTickMilliseconds);
        var tick = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(ProgressTickMilliseconds), cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) break;

                tick++;
                progress?.Invoke(written(), expected);

                if (log is not null && tick % debugTicks == 0)
                    log($"已下载 {Format.Size(written())} / {Format.Size(expected)}（{active()} 个 blob 进行中）");
            }
        }
        catch (OperationCanceledException)
        {
            // 下载结束，正常退出
        }
    }

    private async Task<Fetched> FetchBytesAsync(
        RegistryCandidate candidate,
        string repository,
        string relativePath,
        string[] accept,
        string what,
        CancellationToken cancellationToken)
    {
        var url = $"v2/{repository}/{relativePath}";
        string? lastError = null;

        for (var attempt = 1; attempt <= _attempts; attempt++)
        {
            if (attempt > 1)
            {
                Log.Debug($"{what}第 {attempt} 次尝试 …");
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken).ConfigureAwait(false);
            }

            using var fetch = await _transport
                .FetchAsync(candidate, repository, url, accept, _timeout, cancellationToken)
                .ConfigureAwait(false);

            if (fetch.Success)
            {
                using var buffer = new MemoryStream();
                await fetch.Content!.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                var bytes = buffer.ToArray();
                return new Fetched(bytes, "sha256:" + Sha256Hex(bytes), fetch.ContentType);
            }

            lastError = fetch.Error ?? $"HTTP {fetch.StatusCode}";
        }

        throw new ByxcrException($"{what}失败（{candidate.DisplayName}）：{lastError}");
    }

    // ------------------------------------------------------------ OCI 布局

    private static async Task WriteLayoutAsync(
        string tarPath,
        ImageReference image,
        IReadOnlyList<PlatformImage> platforms,
        IReadOnlyList<KeyValuePair<string, BlobSource>> blobs,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        PathLayout.EnsureDirectory(tarPath);
        TryDelete(tarPath);

        var items = new List<TarItem>(blobs.Count + 4)
        {
            new("oci-layout", Encoding.UTF8.GetBytes($"{{\"imageLayoutVersion\":\"{LayoutVersion}\"}}"), null),
        };

        // 根索引里只放一条 manifest：containers/image 的 oci-archive 传输（podman load / skopeo）
        // 在未显式指定 reference 时要求归档内「恰好一个镜像」，平铺多个平台会被判为
        // "more than one image in oci, choose an image"。
        // 所以多平台镜像先把各平台清单合成一个内层镜像索引，根索引只留一条指向它的 descriptor
        // （与 `skopeo copy --all` 的产物结构一致）；单平台镜像直接指向该平台清单，少一层嵌套。
        if (platforms.Count == 1)
        {
            var only = platforms[0];
            items.Add(new TarItem("index.json", Encoding.UTF8.GetBytes(BuildRootIndexJson(
                image, only.ManifestMediaType, only.Digest, only.ManifestBytes.LongLength, BuildPlatformJson(only.Platform))), null));
        }
        else
        {
            var inner = Encoding.UTF8.GetBytes(BuildPlatformIndexJson(platforms));
            var innerDigest = "sha256:" + Sha256Hex(inner);
            items.Add(new TarItem("index.json", Encoding.UTF8.GetBytes(BuildRootIndexJson(
                image, OciIndexMediaType, innerDigest, inner.LongLength, null)), null));
            items.Add(new TarItem($"blobs/sha256/{Format.DigestHex(innerDigest)}", inner, null));
        }

        // docker 兼容层：在 OCI 布局之外补一份 docker-archive 的 manifest.json。
        // 传统 docker（graph driver，未启用 containerd 镜像存储）的 docker load 只认 tar 根部的
        // manifest.json，找不到就退回 legacy 模式去读 repositories，最终报「no such file or directory」。
        // 与 containerd 导出器（buildkit type=docker）一致：blob 路径直接复用 blobs/sha256/<hex>，
        // 图层保持 gzip 原样（docker 侧经 archive.DecompressStream 解压，本就支持压缩层）。
        var primary = PickDockerPlatform(platforms);
        if (BuildDockerManifestJson(image, primary) is { } dockerManifest)
        {
            items.Add(new TarItem("manifest.json", Encoding.UTF8.GetBytes(dockerManifest), null));
        }
        else
        {
            Log.Warn($"[{image.CanonicalName}] 图层摘要不完整，已跳过 docker 兼容清单 manifest.json");
        }

        foreach (var (hex, source) in blobs)
        {
            items.Add(source.Bytes is not null
                ? new TarItem($"blobs/sha256/{hex}", source.Bytes, null)
                : new TarItem($"blobs/sha256/{hex}", null, Path.Combine(workDirectory, hex)));
        }

        var longest = 0L;
        foreach (var item in items)
        {
            item.Length = item.Bytes?.LongLength ?? SafeLength(item.FilePath!);
            if (item.Length > longest) longest = item.Length;
        }

        // 文件名都很短、体积也远小于 8GB 时使用 USTAR（兼容性最好），否则退回 PAX
        var usePax = longest > UstarSizeLimit || items.Any(item => item.Name.Length > 100);
        var format = usePax ? TarEntryFormat.Pax : TarEntryFormat.Ustar;
        var modified = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await using (var stream = new FileStream(
            tarPath, FileMode.Create, FileAccess.Write, FileShare.None,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            using var writer = new TarWriter(stream, format, leaveOpen: true);

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Stream data;
                if (item.Bytes is not null)
                {
                    data = new MemoryStream(item.Bytes, writable: false);
                }
                else
                {
                    data = new FileStream(
                        item.FilePath!, FileMode.Open, FileAccess.Read, FileShare.Read,
                        CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                }

                try
                {
                    await writer.WriteEntryAsync(CreateEntry(format, item.Name, data, modified), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await data.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        var size = new FileInfo(tarPath).Length;
        if (size <= 0) throw new ByxcrException($"生成 OCI 归档失败或为空：{tarPath}");
        Log.Debug($"OCI 布局写入完成：{items.Count} 个条目，{Format.Size(size)}");
    }

    /// <summary>
    /// 根索引（index.json）：<strong>只含一条</strong> manifest descriptor，让归档被当作「单个镜像」。
    /// 指向平台清单本身（单平台）或内层镜像索引（多平台）；tag 注解写在这条 descriptor 上。
    /// </summary>
    private static string BuildRootIndexJson(ImageReference image, string mediaType, string digest, long size, string? platformJson)
    {
        // 手工拼接而非 JsonNode：避免 JsonArray.Add<T> 在 NativeAOT 下的动态代码告警
        var builder = new StringBuilder(320);
        builder.Append("{\"schemaVersion\":2,\"mediaType\":").Append(Quote(OciIndexMediaType))
               .Append(",\"manifests\":[{\"mediaType\":").Append(Quote(mediaType))
               .Append(",\"digest\":").Append(Quote(digest))
               .Append(",\"size\":").Append(size.ToString(CultureInfo.InvariantCulture));

        if (platformJson is not null) builder.Append(",\"platform\":").Append(platformJson);

        builder.Append(",\"annotations\":{").Append(Quote(RefNameAnnotation)).Append(':').Append(Quote(image.OciRefName))
               .Append("}}]}");
        return builder.ToString();
    }

    /// <summary>内层镜像索引：列出归档内的全部平台清单（多平台合并时由根索引指向它）。</summary>
    private static string BuildPlatformIndexJson(IReadOnlyList<PlatformImage> platforms)
    {
        var builder = new StringBuilder(256 + platforms.Count * 256);
        builder.Append("{\"schemaVersion\":2,\"mediaType\":").Append(Quote(OciIndexMediaType))
               .Append(",\"manifests\":[");

        for (var i = 0; i < platforms.Count; i++)
        {
            var item = platforms[i];
            if (i > 0) builder.Append(',');

            builder.Append("{\"mediaType\":").Append(Quote(item.ManifestMediaType))
                   .Append(",\"digest\":").Append(Quote(item.Digest))
                   .Append(",\"size\":").Append(item.ManifestBytes.LongLength.ToString(CultureInfo.InvariantCulture))
                   .Append(",\"platform\":").Append(BuildPlatformJson(item.Platform))
                   .Append('}');
        }

        builder.Append("]}");
        return builder.ToString();
    }

    /// <summary>平台描述符：os / architecture（+ variant / os.version）。</summary>
    private static string BuildPlatformJson(PlatformInfo platform)
    {
        var builder = new StringBuilder(96);
        builder.Append("{\"architecture\":").Append(Quote(platform.Architecture))
               .Append(",\"os\":").Append(Quote(platform.Os));

        if (platform.Variant.Length > 0) builder.Append(",\"variant\":").Append(Quote(platform.Variant));
        if (platform.OsVersion.Length > 0) builder.Append(",\"os.version\":").Append(Quote(platform.OsVersion));

        builder.Append('}');
        return builder.ToString();
    }

    /// <summary>
    /// docker-archive 兼容清单（manifest.json）：<c>docker load</c> 的入口清单，形状参照 buildkit
    /// <c>type=docker</c> 的产物 —— Config / Layers 都指向 OCI 布局里已有的 <c>blobs/sha256/&lt;hex&gt;</c>。
    /// moby 只要求这些路径在 tar 内存在，且 Layers 的数量、顺序与 config 的 <c>rootfs.diff_ids</c> 一致。
    /// 图层摘要不完整时返回 null，由调用方跳过该文件（宁可少写，也不写出必失败的清单）。
    /// </summary>
    private static string? BuildDockerManifestJson(ImageReference image, PlatformImage primary)
    {
        var configHex = Format.DigestHex(primary.ConfigDigest);
        if (configHex.Length == 0) return null;

        var layers = new List<string>(primary.Layers.Count);
        foreach (var layer in primary.Layers)
        {
            var hex = Format.DigestHex(layer.Digest);
            if (hex.Length == 0) return null;
            layers.Add("blobs/sha256/" + hex);
        }

        var builder = new StringBuilder(160 + layers.Count * 80);
        builder.Append("[{\"Config\":").Append(Quote("blobs/sha256/" + configHex))
               .Append(",\"RepoTags\":[").Append(Quote(image.DockerRepoTag)).Append(']')
               .Append(",\"Layers\":[");

        for (var i = 0; i < layers.Count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(Quote(layers[i]));
        }

        builder.Append("]}]");
        return builder.ToString();
    }

    /// <summary>
    /// 为 docker 兼容清单挑「主平台」：docker-archive 的一个条目只能描述一个镜像
    /// （<c>docker save</c> 本身也不支持 manifest list），多平台归档只能用单条 manifest.json
    /// 登记其中一个。优先宿主机架构（x64 → linux/amd64），其次 linux/amd64，最后取第一个。
    /// </summary>
    private static PlatformImage PickDockerPlatform(IReadOnlyList<PlatformImage> platforms)
    {
        if (platforms.Count == 1) return platforms[0];

        var host = HostArchitecture();
        if (host.Length > 0)
        {
            foreach (var item in platforms)
            {
                if (IsLinux(item) && item.Platform.Architecture.Equals(host, StringComparison.OrdinalIgnoreCase)) return item;
            }
        }

        foreach (var item in platforms)
        {
            if (IsLinux(item) && item.Platform.Architecture.Equals("amd64", StringComparison.OrdinalIgnoreCase)) return item;
        }

        return platforms[0];
    }

    private static bool IsLinux(PlatformImage item)
        => item.Platform.Os.Equals("linux", StringComparison.OrdinalIgnoreCase);

    /// <summary>宿主进程架构 → OCI 架构名，用于挑选 docker 兼容清单的主平台。</summary>
    private static string HostArchitecture() => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "amd64",
        System.Runtime.InteropServices.Architecture.X86 => "386",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.Arm => "arm",
        System.Runtime.InteropServices.Architecture.S390x => "s390x",
        System.Runtime.InteropServices.Architecture.LoongArch64 => "loong64",
        _ => string.Empty,
    };

    private static string Quote(string value) => JsonSerializer.Serialize(value, ByxcrJson.Default.String);

    private static TarEntry CreateEntry(TarEntryFormat format, string name, Stream data, DateTimeOffset modified)
    {
        TarEntry entry = format == TarEntryFormat.Pax
            ? new PaxTarEntry(TarEntryType.RegularFile, name)
            : new UstarTarEntry(TarEntryType.RegularFile, name);

        // Length 由 TarWriter 依据 DataStream 推导（MemoryStream / FileStream 均可寻址）
        entry.DataStream = data;
        entry.ModificationTime = modified;
        entry.Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        return entry;
    }

    // -------------------------------------------------------- 清单解析

    private static bool IsIndex(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty("manifests", out var manifests)
           && manifests.ValueKind == JsonValueKind.Array;

    private static bool IsImageManifest(string mediaType)
        => mediaType.Length == 0
           || mediaType.Equals(OciManifestMediaType, StringComparison.OrdinalIgnoreCase)
           || mediaType.Equals(DockerManifestMediaType, StringComparison.OrdinalIgnoreCase);

    private static string InferMediaType(string? contentType)
        => string.IsNullOrWhiteSpace(contentType) ? DockerManifestMediaType : contentType.Trim();

    private static (string ConfigDigest, string ConfigMediaType, List<BlobDescriptor> Layers) ReadImageManifest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new UnrecoverableImageException("镜像清单不是合法的 JSON 对象");

        var configDigest = string.Empty;
        var configMediaType = string.Empty;
        if (root.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object)
        {
            configDigest = GetString(config, "digest");
            configMediaType = GetString(config, "mediaType");
        }

        if (configDigest.Length == 0) throw new UnrecoverableImageException("镜像清单缺少 config 描述符");

        var layers = new List<BlobDescriptor>();
        if (root.TryGetProperty("layers", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var digest = GetString(item, "digest");
                if (digest.Length == 0) continue;

                long size = 0;
                if (item.TryGetProperty("size", out var sizeElement) && sizeElement.ValueKind == JsonValueKind.Number)
                    sizeElement.TryGetInt64(out size);

                layers.Add(new BlobDescriptor(digest, GetString(item, "mediaType"), size));
            }
        }

        if (layers.Count == 0) throw new UnrecoverableImageException("镜像清单不包含任何图层");

        return (configDigest, configMediaType, layers);
    }

    private static PlatformInfo? ReadPlatform(JsonElement entry)
    {
        if (!entry.TryGetProperty("platform", out var platform) || platform.ValueKind != JsonValueKind.Object) return null;
        return new PlatformInfo(
            GetString(platform, "os"),
            GetString(platform, "architecture"),
            GetString(platform, "variant"),
            GetString(platform, "os.version"));
    }

    private static PlatformInfo? ReadPlatformFromConfig(byte[] configBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(configBytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var os = GetString(root, "os");
            var architecture = GetString(root, "architecture");
            if (os.Length == 0 || architecture.Length == 0) return null;

            return new PlatformInfo(os, architecture, GetString(root, "variant"), GetString(root, "os.version"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<PlatformSpec> ParseFilter(List<string>? values)
    {
        var filter = new List<PlatformSpec>();
        if (values is null) return filter;

        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;

            var os = parts[0].ToLowerInvariant();
            if (os is "auto" or "all" or "*") continue;

            filter.Add(new PlatformSpec(
                os,
                parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty,
                parts.Length > 2 ? parts[2].ToLowerInvariant() : string.Empty));
        }

        return filter;
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty)
            : string.Empty;

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // -------------------------------------------------------- 辅助

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception) { return 0; }
    }

    private static void ResetDirectory(string path)
    {
        TryDeleteDirectory(path);
        Directory.CreateDirectory(path);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不影响结果
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

    public void Dispose() => _transport.Dispose();

    // -------------------------------------------------------- 内部类型

    private sealed record Fetched(byte[] Bytes, string Digest, string? ContentType);

    private sealed record ManifestTarget(string MediaType, string Digest, byte[]? Bytes, PlatformInfo? Platform = null);

    private sealed record BlobDescriptor(string Digest, string MediaType, long Size);

    /// <summary>归档中一个 blob 的来源：内存字节（清单/配置）或待下载的图层。</summary>
    private sealed class BlobSource(byte[]? bytes, string mediaType, long length)
    {
        public byte[]? Bytes { get; } = bytes;

        public string MediaType { get; } = mediaType;

        public long Length { get; } = length;

        public string DigestValue { get; init; } = string.Empty;
    }

    private sealed record PlatformImage(
        string ManifestMediaType,
        string Digest,
        byte[] ManifestBytes,
        string ConfigDigest,
        string ConfigMediaType,
        byte[] ConfigBytes,
        List<BlobDescriptor> Layers,
        PlatformInfo Platform);

    private sealed class TarItem(string name, byte[]? bytes, string? filePath)
    {
        public string Name { get; } = name;

        public byte[]? Bytes { get; } = bytes;

        public string? FilePath { get; } = filePath;

        public long Length { get; set; }
    }

    private sealed record PlatformInfo(string Os, string Architecture, string Variant, string OsVersion)
    {
        public string Display => Variant.Length > 0 ? $"{Os}/{Architecture}/{Variant}" : $"{Os}/{Architecture}";
    }

    private readonly record struct PlatformSpec(string Os, string Architecture, string Variant)
    {
        public string Display => Variant.Length > 0 ? $"{Os}/{Architecture}/{Variant}" : $"{Os}/{Architecture}";

        public bool Matches(PlatformInfo info)
            => Os.Equals(info.Os, StringComparison.OrdinalIgnoreCase)
               && (Architecture.Length == 0 || Architecture.Equals(info.Architecture, StringComparison.OrdinalIgnoreCase))
               && (Variant.Length == 0 || Variant.Equals(info.Variant, StringComparison.OrdinalIgnoreCase));
    }
}
