using Byxcr.Configuration;
using Byxcr.Core;
using Byxcr.Persistence;
using Byxcr.Logging;
using Byxcr.Registry;

namespace Byxcr.Services;

/// <summary>把配置、存储、下载器与同步服务组装在一起的应用上下文。</summary>
public sealed class ByxcrApplication : IDisposable
{
    private ByxcrApplication(
        ConfigLoadResult load,
        SqliteDatabase database,
        PathLayout paths,
        RegistryClient registryClient,
        OciImagePuller puller,
        IArchiveSink archive)
    {
        Load = load;
        Database = database;
        Paths = paths;
        RegistryClient = registryClient;
        Puller = puller;
        Archive = archive;
        Images = new ImageStore(database);
        Records = new SyncRecordStore(database);
        Resolver = new RegistryResolver(load.Config.Registry, registryClient);
        Compressor = new GzipCompressor(load.Config.Compression);
        Sync = new ImageSyncService(load.Config, paths, Images, Records, Resolver, registryClient, Compressor, puller, archive);
        Scheduler = new SchedulerService(load.Config, Images, Records, Sync);
    }

    public ConfigLoadResult Load { get; }

    public AppConfig Config => Load.Config;

    public SqliteDatabase Database { get; }

    public PathLayout Paths { get; }

    public ImageStore Images { get; }

    public SyncRecordStore Records { get; }

    public RegistryClient RegistryClient { get; }

    /// <summary>Registry V2 下载器（→ OCI 镜像布局）。</summary>
    public OciImagePuller Puller { get; }

    /// <summary>归档目标：本地目录或 WebDAV。</summary>
    public IArchiveSink Archive { get; }

    public RegistryResolver Resolver { get; }

    public GzipCompressor Compressor { get; }

    public ImageSyncService Sync { get; }

    public SchedulerService Scheduler { get; }

    /// <summary>
    /// 用指定的归档目标另建一个同步服务（配置、存储、下载器等全部复用），用于 <c>sync -l</c>
    /// 这类「本次同步不走 WebDAV，直接写本地目录」的场景。
    /// </summary>
    public ImageSyncService CreateSyncWith(IArchiveSink sink)
        => new(Config, Paths, Images, Records, Resolver, RegistryClient, Compressor, Puller, sink);

    /// <summary>
    /// add 之前的预检：并发向所有候选镜像源各取一次清单，取优先级最高的成功结果。
    /// <para>
    /// 刻意不先做 <c>/v2/</c> 探测，也不串行尝试：拉清单本身就是最准确的可用性判断。
    /// 并发是为了不让「连不通的官方源」把耗时拖到几十秒——只要有一个源能拉到，立刻返回并取消其余请求。
    /// </para>
    /// </summary>
    public async Task<ImageProbeResult> ProbeImageAsync(ImageReference image, CancellationToken cancellationToken)
    {
        var candidates = Resolver.Build(image, null);
        if (candidates.Count == 0)
            return ImageProbeResult.Failure("没有可用的镜像源，请检查 registry.items 配置");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = candidates.Select(candidate => Puller.ProbeAsync(image, candidate, linked.Token)).ToList();

        try
        {
            var remaining = new List<Task<ImageProbeResult>>(tasks);
            var failures = new ImageProbeResult?[tasks.Count];
            ImageProbeResult? success = null;

            while (remaining.Count > 0 && success is null)
            {
                var finished = await Task.WhenAny(remaining).ConfigureAwait(false);
                remaining.Remove(finished);

                ImageProbeResult result;
                try
                {
                    result = await finished.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 外部取消：探测已经失去意义，剩下的交给 finally 收尾
                    return ImageProbeResult.Failure("镜像源探测已取消");
                }

                if (result.Ok) success = result;
                else failures[tasks.IndexOf(finished)] = result;
            }

            if (success is not null) return success;

            var reported = failures.Where(failure => failure is { Error: { Length: > 0 } }).ToList();
            if (reported.Count == 0) return ImageProbeResult.Failure("所有镜像源均无法拉取该镜像");

            // 有「确定性结论」（不存在 / 平台不匹配）时优先展示它，
            // 免得被「官方源连不通」这类环境噪音盖住真正原因。
            var definitive = reported.Where(failure => failure!.Definitive).ToList();
            var errors = (definitive.Count > 0 ? definitive : reported)
                .Select(failure => failure!.Error!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return ImageProbeResult.Failure(string.Join("｜", errors));
        }
        finally
        {
            // 成功、失败、取消三条路径都要走这里：先取消尚未返回的探测，再逐个吃掉结果，
            // 否则留在 remaining 里的任务会成为「未被观察的任务异常」。
            linked.Cancel();
            await DrainAsync(tasks).ConfigureAwait(false);
        }
    }

    /// <summary>静默收尾：取消后剩余任务会抛取消异常，这里逐个吃掉，避免留下未被观察的任务异常。</summary>
    private static async Task DrainAsync(IEnumerable<Task> tasks)
    {
        foreach (var task in tasks)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception) { /* 已取消 / 已超时，忽略 */ }
        }
    }

    public static ByxcrApplication Create(string? configPath, bool verbose = false)
    {
        var load = ConfigLoader.Load(configPath);

        Log.Configure(
            verbose ? "debug" : load.Config.Log.Level,
            load.Config.Log.File,
            load.Config.Log.Color);

        // 展示时区：log.timeZone / BYXCR_TZ → 环境变量 TZ → 系统时区；库里始终存 UTC，仅展示时换算
        var timeZoneWarning = Clock.UseTimeZone(load.Config.Log.TimeZone);
        if (timeZoneWarning is not null) Log.Warn(timeZoneWarning);

        Log.Debug($"配置文件：{load.ConfigFilePath}（{(load.ConfigFileExists ? "已加载" : "不存在，使用默认值")}）");
        if (load.EnvOverrides.Count > 0)
        {
            Log.Debug($"生效的环境变量：{string.Join(", ", load.EnvOverrides)}");
        }

        foreach (var warning in load.Warnings)
        {
            Log.Warn(warning);
        }

        var paths = new PathLayout(load.Config.Storage, load.Config.Log);
        paths.EnsureDirectories();

        var database = new SqliteDatabase(paths.DatabaseFile);
        database.Initialize();

        var registryClient = new RegistryClient(load.Config.Registry);
        var puller = new OciImagePuller(load.Config, paths);

        var localSink = new LocalArchiveSink(paths.ImageRoot);
        IArchiveSink archive = load.Config.Storage.Webdav.Enabled
            ? new WebDavArchiveSink(load.Config.Storage.Webdav, localSink)
            : localSink;

        return new ByxcrApplication(load, database, paths, registryClient, puller, archive);
    }

    public void Dispose()
    {
        RegistryClient.Dispose();
        Puller.Dispose();
        (Archive as IDisposable)?.Dispose();
    }
}
