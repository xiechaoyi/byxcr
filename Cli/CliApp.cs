using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Byxcr.Configuration;
using Byxcr.Core;
using Byxcr.Logging;
using Byxcr.Registry;
using Byxcr.Services;

namespace Byxcr.Cli;

/// <summary>命令行入口与各子命令实现。</summary>
public static class CliApp
{
    /// <summary>add 后台下载的 trigger：受数据库同步租约约束。</summary>
    private const string BackgroundTrigger = "add";

    public static string Version { get; } = ResolveVersion();

    public static async Task<int> RunAsync(string[] args)
    {
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (ConfigurationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }

        // 不带子命令（或 -h / help）时只打印用法：必须在加载配置、打开数据库之前返回。
        if (options.Help || options.Command == "help" || !options.HasCommand)
        {
            PrintUsage();
            return 0;
        }

        if (options.Command == "version")
        {
            Log.Raw($"byxcr {Version}");
            return 0;
        }

        if (options.Command == "init") return RunInit(options);

        try
        {
            using var app = ByxcrApplication.Create(options.ConfigPath, options.Verbose);

            return options.Command switch
            {
                "run" or "start" or "daemon" => await RunDaemonAsync(app, options).ConfigureAwait(false),
                "sync" or "download" => await RunSyncAsync(app, options).ConfigureAwait(false),
                "list" or "ls" => RunList(app, options),
                "add" => await RunAddAsync(app, options).ConfigureAwait(false),
                "remove" or "rm" or "delete" or "del" => RunRemove(app, options),
                "enable" => RunToggle(app, options, true),
                "disable" => RunToggle(app, options, false),
                "interval" or "set-interval" or "iv" => RunSetInterval(app, options),
                "records" or "log" or "history" => RunRecords(app, options),
                "registries" or "registry" or "mirrors" => await RunRegistriesAsync(app, options).ConfigureAwait(false),
                "doctor" or "check" => await RunDoctorAsync(app, options).ConfigureAwait(false),
                "config" => RunConfig(app, options),
                _ => RunUnknown(options.Command),
            };
        }
        catch (ConfigurationException ex)
        {
            Log.Error(ex.Message);
            return 2;
        }
        catch (ByxcrException ex)
        {
            Log.Error(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Log.Error($"未处理的异常：{ex.GetType().Name}: {ex.Message}");
            if (options.Verbose) Log.Raw(ex.ToString());
            return 1;
        }
    }

    // ------------------------------------------------------------------ run

    private static async Task<int> RunDaemonAsync(ByxcrApplication app, CliOptions options)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Log.Warn("收到中断信号，正在优雅退出 …");
            cts.Cancel();
        };

        Log.Raw($"byxcr {Version} · 数据目录 {app.Paths.DataDir}");
        Log.Raw($"配置文件 {app.Load.ConfigFilePath}{(app.Load.ConfigFileExists ? "" : "（不存在，使用默认值）")}");
        if (app.Load.EnvOverrides.Count > 0)
        {
            Log.Raw($"生效的环境变量 {string.Join(", ", app.Load.EnvOverrides)}");
        }
        Log.Raw($"下载通道 Registry V2 → OCI 镜像布局 · 平台 {app.Puller.PlatformPolicy}");
        Log.Raw($"归档目标 {app.Archive.Describe}");
        Log.Raw(string.Empty);

        if (app.Images.Count() == 0)
        {
            Log.Warn("同步列表为空，调度器不会同步任何镜像：请用 byxcr add <镜像> [-i|--interval 时长] 添加待同步镜像");
        }

        if (app.Compressor.ExternalTool is not null)
        {
            Log.Debug($"外部压缩程序：{app.Compressor.ExternalTool}");
        }

        // WebAPI 随守护进程一起提供：只有配置了调用令牌（api.token / BYXCR_API_TOKEN）才启动，
        // 避免默认就在没鉴权的端口上暴露 add / sync / remove 等写操作。
        StartWebApi(app, cts.Token);

        await app.Scheduler.RunAsync(cts.Token).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// 守护进程内的 WebAPI（<c>api.enabled</c> 时启动，失败只告警不影响调度）：没有单独的启动命令，
    /// <c>byxcr run</c> 起来就有。根路由 <c>/</c> 是内置的 Web 控制台页面，另外 <c>/api</c> 返回接口清单、
    /// <c>/api/list</c> 返回同步任务列表，这三个（含 <c>/api/health</c>）都是公开接口，不需要令牌，
    /// 控制台页面正是靠 <c>/api/list</c> 在浏览器里直接读取任务；写接口仍需令牌，未配置 <c>api.token</c> 时统一返回「请先配置Token」。
    /// </summary>
    private static void StartWebApi(ByxcrApplication app, CancellationToken token)
    {
        var config = app.Config.Api;
        if (!config.Enabled) return;

        // 没有配置令牌也照常监听：公开接口（/、/api、/api/list、/api/health）可正常访问，
        // 写接口返回「请先配置Token」——避免用户改完配置却忘了令牌在哪、误以为服务没起来。
        if (config.Token.Length == 0)
        {
            Log.Warn("未配置 api.token：Web 控制台与 /api/list 可正常访问，add/sync/remove/enable/disable 会返回「请先配置Token」");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await new ApiServer(app).RunAsync(token).ConfigureAwait(false);
            }
            catch (ByxcrException ex)
            {
                Log.Warn(ex.Message);
            }
            catch (Exception ex)
            {
                Log.Warn($"WebAPI 启动失败：{ex.Message}");
            }
        }, CancellationToken.None);
    }

    // ----------------------------------------------------------------- sync

    private static async Task<int> RunSyncAsync(ByxcrApplication app, CliOptions options)
    {
        List<ImageTask> targets;

        if (options.Positionals.Count > 0)
        {
            targets = [];
            foreach (var name in options.Positionals)
            {
                if (!ImageReference.TryParse(name, out var image))
                {
                    Log.Error($"镜像名称无效：{name}");
                    return 2;
                }

                var existing = app.Images.Find(image.CanonicalName);
                if (existing is null)
                {
                    Log.Warn($"镜像 {image.CanonicalName} 不在同步列表中，本次仅做一次性同步");
                    existing = new ImageTask
                    {
                        Image = image.CanonicalName,
                        Repository = image.Repository,
                        Tag = image.Tag,
                        IntervalSeconds = Duration.FromMinutes(app.Config.Sync.DefaultIntervalMinutes),
                        Enabled = true,
                    };
                }
                targets.Add(existing);
            }
        }
        else
        {
            targets = app.Images.GetAll().Where(task => task.Enabled).ToList();
            if (targets.Count == 0)
            {
                Log.Warn("同步列表为空，可用 byxcr add <镜像> 添加待同步镜像");
                return 0;
            }
        }

        // sync -o：本次同步专用的归档位置。只影响这一次，不写回任务配置
        // （要长期生效请用 `add <镜像> -o <路径>`）。一次同步多个镜像时把它当成目录，
        // 其下沿用默认的「仓库/镜像/tag.tar」结构，避免互相覆盖。
        var output = NormalizeOutput(options.Output);
        var outputAsDirectory = targets.Count > 1;

        string? OverrideFor(ImageTask task)
        {
            if (output is null) return null;
            return ImageReference.TryParse(task.Image, out var parsed)
                ? ArchiveNaming.Resolve(parsed, output, outputAsDirectory)
                : null;
        }

        if (output is not null)
        {
            Log.Note(outputAsDirectory
                ? $"本次同步使用输出目录：{output}（{targets.Count} 个镜像各自按默认结构存放）"
                : $"本次同步使用输出位置：{OverrideFor(targets[0])}（仅本次；长期生效请用 add <镜像> -o <路径>）");
        }

        var concurrency = Math.Clamp(app.Config.Sync.MaxConcurrency, 1, 32);
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);
        var results = new SyncRecord?[targets.Count];

        // 手动 sync 不受并发租约限制；作为后台下载进程（add 拉起）运行时按后台 trigger 记账。
        var trigger = BackgroundConsole.IsBackground ? BackgroundTrigger : ImageSyncService.ManualTrigger;

        // sync -l：本次强制写本地归档目录，忽略 storage.webdav（用于临时把镜像拉到本机）
        var sync = app.Sync;
        IArchiveSink? localSink = null;
        if (options.Local && !(app.Archive is LocalArchiveSink))
        {
            localSink = new LocalArchiveSink(app.Paths.ImageRoot);
            sync = app.CreateSyncWith(localSink);
            Log.Note($"本次同步忽略 WebDAV，归档到本地：{app.Paths.ImageRoot}");
        }

        try
        {
            var jobs = targets.Select(async (task, index) =>
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    results[index] = await sync.SyncAsync(task, trigger, options.Force, CancellationToken.None, OverrideFor(task)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error($"[{task.Image}] 同步异常：{ex.Message}");
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToArray();

            await Task.WhenAll(jobs).ConfigureAwait(false);
        }
        finally
        {
            (localSink as IDisposable)?.Dispose();
        }

        var records = results.Where(record => record is not null).Select(record => record!).ToList();

        // 每条结果的结论已经作为「完成下载 → …」逐条打印（跳过的也有对应日志），这里不再重复汇总表格。
        if (options.Json)
        {
            WriteJson(records, ByxcrJson.Default.ListSyncRecord);
        }

        return records.Any(record => record.Status == "failed") ? 1 : 0;
    }

    // ----------------------------------------------------------------- list

    private static int RunList(ByxcrApplication app, CliOptions options)
    {
        // 可选的筛选关键字：byxcr list alpine → 只列出镜像名里含 alpine 的任务；不带参数则不筛选。
        var keyword = options.Positionals.Count > 0 ? options.Positionals[0].Trim() : null;
        if (string.IsNullOrEmpty(keyword)) keyword = null;

        // 默认只显示最近添加的 20 条（--json 是给机器读的，不受默认上限约束，可用 --limit 限定）。
        var limit = options.Limit is > 0 ? options.Limit.Value : options.Json ? int.MaxValue : DefaultListLimit;

        var total = app.Images.CountMatching(keyword);
        var items = app.Images.Query(keyword, limit);

        if (options.Json)
        {
            WriteJson(items, ByxcrJson.Default.ListImageTask);
            return 0;
        }

        if (total == 0)
        {
            Log.Warn(keyword is null ? "同步列表为空" : $"没有匹配「{keyword}」的镜像");
            return 0;
        }

        var rows = items.Select(task => new[]
        {
            task.Enabled ? "启用" : "停用",
            task.Image,
            Duration.Format(task.IntervalSeconds),
            task.LastStatus ?? "-",
            Clock.Local(task.LastCheckedAt),
            Clock.Local(task.LastSuccessAt),
            ShortDigest(task.LastDigest),
            OutputLocation(task),
            Remark(task),
        }).ToList();

        ConsoleTable.Print(["状态", "镜像", "检查频率", "最近结果", "最近检查", "最近下载时间", "已记录摘要", "输出位置", "备注"], rows);
        Log.Raw(string.Empty);
        if (total > items.Count)
        {
            var scope = keyword is null ? string.Empty : $"匹配「{keyword}」的";
            Log.Note($"共 {total} 条{scope}任务，已按添加时间倒序显示最近 {items.Count} 条（--limit 可调整，筛选用 byxcr list <关键字>）");
        }
        Log.Raw($"归档目标：{app.Archive.Describe}");
        return 0;
    }

    /// <summary>list 默认最多显示的任务条数。</summary>
    private const int DefaultListLimit = 20;

    // ------------------------------------------------------------------ add

    private static async Task<int> RunAddAsync(ByxcrApplication app, CliOptions options)
    {
        if (options.Positionals.Count == 0)
        {
            Log.Error("用法：byxcr add <镜像> [-i|--interval 时长] [--disable] [-o|--output 路径] [--no-check]");
            return 2;
        }

        // 写入数据库之前先确认「仓库 + 标签」真的能拉取（只取一次清单，不下载图层）。
        // 拉不动的镜像直接拒绝入库，避免列表里堆一批永远同步失败的任务。
        var probes = await ProbeNewImagesAsync(app, options).ConfigureAwait(false);

        var pending = new List<ImageTask>();   // 需要在后台立即同步一次的任务
        var invalid = 0;

        foreach (var name in options.Positionals)
        {
            if (!ImageReference.TryParse(name, out var image))
            {
                Log.Error($"镜像名称无效：{name}");
                invalid++;
                continue;
            }

            var existing = app.Images.Find(image.CanonicalName);
            if (existing is not null)
            {
                // 已存在时若带了 --output，就单独更新输出位置（下次同步起生效）
                if (NormalizeOutput(options.Output) is { } changed)
                {
                    app.Images.SetOutput(image.CanonicalName, changed);
                    existing.Output = changed;
                    Log.Ok($"已更新输出位置：{image.CanonicalName} → {app.Archive.Locate(ArchiveNaming.Resolve(image, changed))}");
                }

                // 已存在时若带了 -i/--interval 且与库里的值不一致，就更新检查频率（同样是单独改一项，
                // 不影响启用状态与输出位置）；值相同则静默跳过，避免重复 add 刷出一行无意义的「已更新」。
                if (options.IntervalSeconds is { } interval && interval != existing.IntervalSeconds)
                {
                    var previous = existing.IntervalSeconds;
                    app.Images.SetInterval(image.CanonicalName, interval);
                    existing.IntervalSeconds = Math.Max(1, interval);
                    Log.Ok($"已更新检查频率：{image.CanonicalName}｜{Duration.Format(previous)} → {Duration.Format(existing.IntervalSeconds)}（{existing.IntervalSeconds} 秒）");
                }

                // 已存在：已到期（含从未同步）才补一次后台下载，否则只提示状态
                // （频率刚改成更短的值时会因此变「已到期」，这里顺带补一次下载）
                var due = IsDue(existing);
                Log.Warn($"任务已存在：{image.CanonicalName}{StateSuffix(existing)}｜{NextSyncText(existing, due)}");
                if (due) pending.Add(existing);
                continue;
            }

            // 预检没通过的镜像不写库；通过时先把校验结论打出来，便于溯源
            if (probes.TryGetValue(image.CanonicalName, out var probe))
            {
                if (!probe.Ok)
                {
                    Log.Error($"无法拉取，未加入同步列表：{image.CanonicalName}｜{probe.Error}");
                    invalid++;
                    continue;
                }

                Log.Info($"校验通过：{image.CanonicalName} 可拉取（{probe.Summary}）");
            }

            var task = new ImageTask
            {
                Image = image.CanonicalName,
                Repository = image.Repository,
                Tag = image.Tag,
                IntervalSeconds = options.IntervalSeconds ?? Duration.FromMinutes(app.Config.Sync.DefaultIntervalMinutes),
                Enabled = options.Enabled ?? true,
                Output = NormalizeOutput(options.Output),
            };

            app.Images.Upsert(task, overwriteSettings: true);
            Log.Ok($"已加入同步列表：{image.CanonicalName}｜频率 {Duration.Format(task.IntervalSeconds)}｜{(task.Enabled ? "启用" : "停用")}｜归档 {app.Archive.Locate(ArchiveNaming.Resolve(image, task.Output))}");

            if (task.Enabled) pending.Add(app.Images.Find(image.CanonicalName) ?? task);
            else Log.Note("已添加为停用状态，未触发下载；可用 byxcr enable 启用后同步");
        }

        // 新增的任务、以及已存在但已过同步间隔的任务，统一交给独立的后台进程下载
        if (pending.Count > 0)
        {
            foreach (var task in pending)
            {
                Log.Info($"镜像已在后台开始下载，稍后可使用 byxcr records {task.Image} 命令查看下载记录");
            }

            if (!TryStartBackgroundDownload(app, options, pending))
            {
                // 子进程拉不起来时退回进程内线程（输出同样重定向到后台日志文件）
                await RunBackgroundDownloadsAsync(app, options, pending).ConfigureAwait(false);
            }
        }

        if (invalid == 0) return 0;
        return invalid == options.Positionals.Count ? 2 : 1;
    }

    /// <summary>
    /// 对本次要新增的镜像做「能否拉取」预检：并发地向候选镜像源各取一次清单。
    /// 已在列表中的镜像不重复检查；<c>--no-check</c> 时整体跳过并返回空表。
    /// </summary>
    private static async Task<Dictionary<string, ImageProbeResult>> ProbeNewImagesAsync(ByxcrApplication app, CliOptions options)
    {
        var results = new Dictionary<string, ImageProbeResult>(StringComparer.OrdinalIgnoreCase);
        if (options.NoCheck)
        {
            Log.Warn("已跳过拉取校验（--no-check）：无法拉取的镜像也会写入同步列表");
            return results;
        }

        var targets = new List<ImageReference>();
        foreach (var name in options.Positionals)
        {
            if (!ImageReference.TryParse(name, out var image)) continue;
            if (app.Images.Find(image.CanonicalName) is not null) continue;
            if (targets.Any(item => string.Equals(item.CanonicalName, image.CanonicalName, StringComparison.OrdinalIgnoreCase))) continue;
            targets.Add(image);
        }

        if (targets.Count == 0) return results;

        Log.Info(targets.Count == 1
            ? $"正在校验镜像是否可拉取：{targets[0].CanonicalName}"
            : $"正在校验 {targets.Count} 个镜像是否可拉取 …");

        var checks = await Task.WhenAll(targets.Select(async image =>
        {
            try
            {
                var result = await app.ProbeImageAsync(image, CancellationToken.None).ConfigureAwait(false);
                return (image.CanonicalName, Result: result);
            }
            catch (Exception ex)
            {
                // 预检本身出错时按「拉不到」处理，绝不因此把任务写进库
                return (image.CanonicalName, Result: ImageProbeResult.Failure(ex.Message));
            }
        })).ConfigureAwait(false);

        foreach (var (key, result) in checks) results[key] = result;
        return results;
    }

    /// <summary>输出位置：空白视为未指定（回退默认归档规则）。</summary>
    private static string? NormalizeOutput(string? output)
        => string.IsNullOrWhiteSpace(output) ? null : output.Trim();

    /// <summary>list 的备注列：优先最近错误，其次自定义输出位置。</summary>
    /// <summary>备注列：只放最近一次失败原因，正常时留 -。</summary>
    private static string Remark(ImageTask task)
        => string.IsNullOrWhiteSpace(task.LastError) ? "-" : Shorten(task.LastError, 48);

    /// <summary>输出位置列：该任务下次同步实际会写到的位置（相对归档根）。</summary>
    private static string OutputLocation(ImageTask task)
    {
        if (!ImageReference.TryParse(task.Image, out var image))
            return string.IsNullOrWhiteSpace(task.Output) ? "-" : Shorten(task.Output, 60);

        return Shorten(ArchiveNaming.Resolve(image, task.Output), 60);
    }

    /// <summary>展示时区的来源，便于排查「为什么显示成 UTC」。</summary>
    private static string TimeZoneSource(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return "来自 log.timeZone";

        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TZ"))) return "来自环境变量 TZ";
        }
        catch (Exception)
        {
            // 环境变量不可读时按未设置处理
        }

        return "取自系统时区";
    }

    /// <summary>仅在最近一次结果不正常时补一句状态，避免与「下次同步」重复。</summary>
    private static string StateSuffix(ImageTask task) => task.LastStatus switch
    {
        "failed" => "｜上次同步失败",
        "running" => "｜上次未正常结束",
        _ => string.Empty,
    };

    /// <summary>任务是否已到检查时间：从未检查过也算到期；停用项不算到期。</summary>
    private static bool IsDue(ImageTask task)
    {
        if (!task.Enabled) return false;

        var last = Clock.Parse(task.LastCheckedAt);
        if (last is null) return true;

        return DateTime.UtcNow - last.Value >= TimeSpan.FromSeconds(Math.Max(1, task.IntervalSeconds));
    }

    /// <summary>单行的同步计划描述：停用 / 尚未同步 / 已到期 / 具体时刻。</summary>
    private static string NextSyncText(ImageTask task, bool due)
    {
        if (!task.Enabled) return "已停用，不会自动同步";
        if (due) return "已到期，立即下载";

        var last = Clock.Parse(task.LastCheckedAt) ?? DateTime.UtcNow;
        var next = last + TimeSpan.FromSeconds(Math.Max(1, task.IntervalSeconds));
        return $"下次同步 {Clock.Local(next)}";
    }

    /// <summary>
    /// 拉起一个**完全独立**的后台下载子进程（<c>byxcr sync &lt;镜像…&gt;</c>）：没有控制台窗口、
    /// stdout/stderr 全部落到 {数据目录}/logs/background-sync.log，父进程打印完提示即可立刻退出，
    /// 用户马上能继续输入下一条命令，不必等下载结束。子进程的 trigger 记为 add，受数据库同步租约
    /// （1800 秒，见 <see cref="ImageSyncService.LeaseSeconds"/>）约束，不会与定时任务重复下载同一镜像。
    /// </summary>
    private static bool TryStartBackgroundDownload(ByxcrApplication app, CliOptions options, IReadOnlyList<ImageTask> tasks)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Log.Warn("无法定位当前可执行文件，改为在本进程内后台下载");
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory,
            };

            psi.ArgumentList.Add("sync");
            foreach (var task in tasks) psi.ArgumentList.Add(task.Image);
            if (options.Force) psi.ArgumentList.Add("--force");
            if (!string.IsNullOrWhiteSpace(options.ConfigPath))
            {
                psi.ArgumentList.Add("--config");
                psi.ArgumentList.Add(options.ConfigPath);
            }

            // 子进程据此把控制台输出接到后台日志文件（见 BackgroundConsole）
            psi.Environment[BackgroundConsole.LogFileVariable] = BackgroundLogPath(app);

            using var process = Process.Start(psi);
            return process is not null;
        }
        catch (Exception ex)
        {
            Log.Warn($"后台下载进程启动失败（{ex.Message}），改为在本进程内后台下载");
            return false;
        }
    }

    /// <summary>
    /// 后台下载日志的候选路径：先是日志目录（Linux 默认 <c>/var/log/byxcr</c>，Windows 默认 <c>{数据目录}/logs</c>，
    /// 可用 <c>log.directory</c> 覆盖），不可写时退化到 <c>{数据目录}/logs</c>。
    /// </summary>
    private static IEnumerable<string> BackgroundLogCandidates(ByxcrApplication app)
    {
        yield return Path.Combine(app.Paths.LogRoot, "background-sync.log");
        yield return Path.Combine(app.Paths.DataDir, "logs", "background-sync.log");
    }

    /// <summary>后台下载日志文件路径：取第一个可写的候选位置。</summary>
    private static string BackgroundLogPath(ByxcrApplication app)
    {
        foreach (var path in BackgroundLogCandidates(app))
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) continue;

            try
            {
                Directory.CreateDirectory(directory);
                return path;
            }
            catch (Exception)
            {
                // 目录不可写，试下一个候选
            }
        }

        return Path.Combine(app.Paths.DataDir, "logs", "background-sync.log");
    }

    /// <summary>
    /// 在后台线程同步这些镜像（子进程拉不起来时的退路）：调用方打完自己的提示后，下载期间不再向控制台输出任何内容——
    /// 整篇控制台输出（<see cref="Log"/> 与 Console 都走 Console.Out）临时重定向到后台日志文件，命令结束时再还原。
    /// 下载跑在本进程的线程里，因此进程必须存活到全部下载完成，否则线程会被一并终止。
    /// 并发数沿用 sync.maxConcurrency。
    /// </summary>
    private static async Task RunBackgroundDownloadsAsync(ByxcrApplication app, CliOptions options, IReadOnlyList<ImageTask> tasks)
    {
        var concurrency = Math.Clamp(app.Config.Sync.MaxConcurrency, 1, 32);
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var sink = OpenBackgroundLog(app);
        Console.SetOut(sink);
        Console.SetError(sink);

        // 输出已经去了日志文件：既无法回到行首刷进度，也不该在日志里堆百分比
        Log.ProgressEnabled = false;
        Log.BatchOutput = true;

        try
        {
            var jobs = tasks.Select(task => Task.Run(async () =>
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    await app.Sync.SyncAsync(task, BackgroundTrigger, options.Force, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error($"[{task.Image}] 后台同步异常：{ex.Message}");
                }
                finally
                {
                    semaphore.Release();
                }
            })).ToArray();

            await Task.WhenAll(jobs).ConfigureAwait(false);
        }
        finally
        {
            Log.ProgressEnabled = true;
            Console.SetOut(originalOut);
            Console.SetError(originalError);

            if (!ReferenceEquals(sink, TextWriter.Null))
            {
                try
                {
                    sink.Flush();
                    sink.Dispose();
                }
                catch (Exception)
                {
                    // 关闭失败无需处理
                }
            }
        }
    }

    /// <summary>打开后台下载日志文件；无法写入时退化为丢弃输出，绝不因此影响下载。</summary>
    private static TextWriter OpenBackgroundLog(ByxcrApplication app)
    {
        Exception? failure = null;

        foreach (var path in BackgroundLogCandidates(app))
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true,
                };
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        Log.Debug($"后台下载日志不可写，下载期间的输出将被丢弃：{failure?.Message}");
        return TextWriter.Null;
    }

    private static int RunRemove(ByxcrApplication app, CliOptions options)
    {
        if (options.Positionals.Count == 0)
        {
            Log.Error("用法：byxcr remove <镜像>");
            return 2;
        }

        var ok = 0;
        foreach (var name in options.Positionals)
        {
            if (app.Images.Delete(name))
            {
                Log.Ok($"已从同步列表移除：{name}");
                ok++;
            }
            else
            {
                Log.Warn($"同步列表中不存在：{name}");
            }
        }

        return ok > 0 ? 0 : 1;
    }

    private static int RunToggle(ByxcrApplication app, CliOptions options, bool enabled)
    {
        if (options.Positionals.Count == 0)
        {
            Log.Error($"用法：byxcr {(enabled ? "enable" : "disable")} <镜像>");
            return 2;
        }

        foreach (var name in options.Positionals)
        {
            if (app.Images.SetEnabled(name, enabled)) Log.Ok($"{(enabled ? "已启用" : "已停用")}：{name}");
            else Log.Warn($"同步列表中不存在：{name}");
        }

        return 0;
    }

    private static int RunSetInterval(ByxcrApplication app, CliOptions options)
    {
        if (options.Positionals.Count < 2)
        {
            Log.Error($"用法：byxcr interval <镜像> <时长>（{Duration.Hint}）");
            return 2;
        }

        var text = options.Positionals[1];
        if (!Duration.TryParseSeconds(text, out var seconds))
        {
            Log.Error($"检查频率无效：{text}（{Duration.Hint}）");
            return 2;
        }

        return app.Images.SetInterval(options.Positionals[0], seconds)
            ? Ok($"已更新 {options.Positionals[0]} 的检查频率为 {Duration.Format(seconds)}（{seconds} 秒）")
            : Fail($"同步列表中不存在：{options.Positionals[0]}");
    }

    // -------------------------------------------------------------- records

    private static int RunRecords(ByxcrApplication app, CliOptions options)
    {
        var image = options.Positionals.Count > 0 ? options.Positionals[0] : null;

        // records --clear：先清除 3 天前的记录，再展示剩余记录
        if (options.Clear)
        {
            var removed = app.Records.DeleteOlderThan(3, image);
            if (!options.Json)
            {
                var scope = image is null ? "全部镜像" : image;
                Log.Ok($"已清除 {removed} 条 {scope} 3 天前的同步记录");
            }
        }

        var records = app.Records.Recent(options.Limit ?? 20, image);

        if (options.Json)
        {
            WriteJson(records, ByxcrJson.Default.ListSyncRecord);
            return 0;
        }

        if (records.Count == 0)
        {
            Log.Warn("暂无同步记录");
            return 0;
        }

        PrintRecords(records);
        return 0;
    }

    // ----------------------------------------------------------- registries

    private static async Task<int> RunRegistriesAsync(ByxcrApplication app, CliOptions options)
    {
        var image = ResolveProbeImage(app);
        var (candidates, probes) = await app.Resolver
            .ResolveAsync(image, null, CancellationToken.None, forceProbe: true)
            .ConfigureAwait(false);

        if (options.Json)
        {
            WriteJson(probes, ByxcrJson.Default.ListRegistryStatus);
            return 0;
        }

        if (probes.Count == 0)
        {
            Log.Warn("未配置任何镜像源（registry.items 为空）");
            return 0;
        }

        var rows = probes.Select(probe => new[]
        {
            probe.Name,
            probe.Available ? "可用" : "不可用",
            probe.StatusCode == 0 ? "-" : probe.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            probe.ElapsedMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Shorten(probe.Message, 40),
        }).ToList();

        ConsoleTable.Print(["镜像源", "状态", "HTTP", "耗时(ms)", "说明"], rows);
        Log.Raw(string.Empty);
        Log.Note($"将按此顺序自动尝试：{string.Join(" → ", candidates.Select(c => c.DisplayName))}");
        return 0;
    }

    // --------------------------------------------------------------- doctor

    private static async Task<int> RunDoctorAsync(ByxcrApplication app, CliOptions options)
    {
        var image = ResolveProbeImage(app);
        await app.Compressor.DetectAsync(CancellationToken.None).ConfigureAwait(false);
        var (candidates, probes) = await app.Resolver
            .ResolveAsync(image, null, CancellationToken.None, forceProbe: true)
            .ConfigureAwait(false);

        var report = new StatusReport
        {
            Version = Version,
            ConfigFile = app.Load.ConfigFilePath,
            ConfigFileExists = app.Load.ConfigFileExists,
            EnvOverrides = app.Load.EnvOverrides,
            DataDir = app.Paths.DataDir,
            DatabaseFile = app.Paths.DatabaseFile,
            ImageRoot = app.Paths.ImageRoot,
            ArchiveTarget = app.Archive.Describe,
            WebdavEnabled = app.Config.Storage.Webdav.Enabled,
            ImageCount = app.Images.Count(),
            DownloadChannel = "Registry V2 → OCI 镜像布局",
            Platforms = app.Config.Download.Platforms,
            AllPlatforms = app.Config.Download.Platforms.Count == 0,
            MaxRetries = app.Config.Download.MaxRetries,
            RequestTimeoutSeconds = app.Config.Download.RequestTimeoutSeconds,
            MaxParallelDownloads = app.Config.Download.MaxParallelDownloads,
            Compression = $"{app.Config.Compression.Mode}（级别 {app.Config.Compression.Level}）"
                          + (app.Compressor.ExternalTool is null ? " · 内置 gzip" : $" · {app.Compressor.ExternalTool}"),
            CheckStrategy = app.Config.Sync.CheckStrategy,
            IgnoreArchiveCheck = app.Config.Sync.IgnoreArchiveCheck,
            TimeZone = $"{Clock.ZoneId}（UTC{Clock.ZoneOffset}） · {TimeZoneSource(app.Config.Log.TimeZone)}",
            Registries = probes,
        };

        if (options.Json)
        {
            WriteJson(report, ByxcrJson.Default.StatusReport);
            return 0;
        }

        Log.Raw($"byxcr {Version}");
        Log.Raw($"配置来源        ：{report.ConfigFile}{(report.ConfigFileExists ? "" : "（不存在，使用默认值）")}");
        Log.Raw($"生效环境变量    ：{(report.EnvOverrides.Count == 0 ? "无" : string.Join(", ", report.EnvOverrides))}");
        Log.Raw($"数据目录        ：{report.DataDir}");
        Log.Raw($"数据库          ：{report.DatabaseFile}");
        Log.Raw($"归档目标        ：{report.ArchiveTarget}{(report.WebdavEnabled ? "（WebDAV）" : string.Empty)}");
        Log.Raw($"同步镜像数量    ：{report.ImageCount}");
        Log.Raw($"下载通道        ：{report.DownloadChannel}");
        Log.Raw($"拉取平台        ：{(report.AllPlatforms ? "全部架构（清单中的所有平台合并进同一个 tar）" : string.Join("、", report.Platforms))}");
        Log.Raw($"请求重试/超时   ：{report.MaxRetries} 次 / {report.RequestTimeoutSeconds} 秒（并发 {report.MaxParallelDownloads} 个 blob）");
        Log.Raw($"压缩            ：{report.Compression}");
        Log.Raw($"检查策略        ：{report.CheckStrategy}"
                + (report.IgnoreArchiveCheck
                    ? "（忽略归档检查：摘要未变化即跳过下载）"
                    : "（摘要未变化且归档存在才跳过）"));
        Log.Raw($"时区            ：{report.TimeZone}");
        Log.Raw(string.Empty);

        ConsoleTable.Print(["镜像源", "状态", "HTTP", "耗时(ms)", "说明"], probes
            .Select(probe => new[]
            {
                probe.Name,
                probe.Available ? "可用" : "不可用",
                probe.StatusCode == 0 ? "-" : probe.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                probe.ElapsedMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Shorten(probe.Message, 40),
            })
            .ToList());

        Log.Raw(string.Empty);
        Log.Note($"实际尝试顺序：{string.Join(" → ", candidates.Select(c => c.DisplayName))}");
        return 0;
    }

    // --------------------------------------------------------------- config

    private static int RunConfig(ByxcrApplication app, CliOptions options)
    {
        var json = JsonSerializer.Serialize(app.Config, ByxcrJson.Default.AppConfig);

        if (options.Json)
        {
            Log.Raw(json);
            return 0;
        }

        // 默认输出 YAML，可直接对照 / 粘贴回配置文件；-j 输出 JSON
        Log.Raw(YamlWriter.Write(JsonNode.Parse(json)));
        return 0;
    }

    /// <summary>
    /// 生成默认配置文件与环境变量示例，两者都放在默认数据目录（./data）下。
    /// 配置文件已存在时提示「已初始化」并且不覆盖，也不再生成本身就存在的示例文件。
    /// </summary>
    private static int RunInit(CliOptions options)
    {
        var path = ConfigLoader.ResolvePath(options.ConfigPath);
        var directory = Path.GetDirectoryName(path);

        if (File.Exists(path))
        {
            Log.Warn($"已初始化，未覆盖：{path}");
            Log.Raw("如需重新生成，请先删除该文件或改用 --config 指定其它路径。");
            return 0;
        }

        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        WriteIfMissing(path, DefaultConfigTemplate.Content, "默认配置文件");
        WriteIfMissing(
            Path.Combine(directory ?? ".", DefaultEnvTemplate.FileName),
            DefaultEnvTemplate.Content,
            "环境变量示例");

        Log.Raw("可编辑配置文件，或用环境变量（如 BYXCR__Storage__DataDir=/data）覆盖其中的任意配置项。");
        return 0;
    }

    private static void WriteIfMissing(string path, string content, string label)
    {
        if (File.Exists(path))
        {
            Log.Warn($"{label}已存在，未覆盖：{path}");
            return;
        }

        File.WriteAllText(path, content);
        Log.Ok($"已生成{label}：{path}");
    }

    private static int RunUnknown(string command)
    {
        Log.Error($"未知命令：{command}");
        Log.Raw(string.Empty);
        PrintUsage();
        return 2;
    }

    // -------------------------------------------------------------- helpers

    /// <summary>挑一个用于探测镜像源可用性的镜像：优先用同步列表里的 docker.io 镜像，否则退回 alpine。</summary>
    private static ImageReference ResolveProbeImage(ByxcrApplication app)
    {
        foreach (var task in app.Images.GetAll())
        {
            if (ImageReference.TryParse(task.Image, out var parsed) && parsed.IsDockerHub) return parsed;
        }

        return ImageReference.Parse("library/alpine:latest");
    }

    private static void PrintRecords(IReadOnlyList<SyncRecord> records)
    {
        if (records.Count == 0)
        {
            Log.Warn("暂无同步记录");
            return;
        }

        var rows = records.Select(record => new[]
        {
            Clock.Local(record.StartedAt),
            record.Image,
            ShortDigest(record.Digest),
            record.Status,
            record.FileSize is null ? "-" : ImageSyncService.FormatSize(record.FileSize.Value),
            record.DurationMs is null ? "-" : $"{record.DurationMs.Value / 1000.0:0.0}s",
            // 说明是结构化摘要（N blob + 架构列表），整列完整显示，不做截断
            string.IsNullOrWhiteSpace(record.Message) ? "-" : record.Message,
        }).ToList();

        ConsoleTable.Print(["时间", "镜像", "摘要", "结果", "大小", "耗时", "说明"], rows);
    }

    private static int Ok(string message)
    {
        Log.Ok(message);
        return 0;
    }

    private static int Fail(string message)
    {
        Log.Warn(message);
        return 1;
    }

    private static string Shorten(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "-";
        var value = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }

    /// <summary>展示已落库的镜像摘要，未记录时显示 -。</summary>
    private static string ShortDigest(string? digest)
        => string.IsNullOrWhiteSpace(digest) ? "-" : Format.ShortDigest(digest);

    private static void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo)
        => Log.Raw(ByxcrJson.ToJson(value, typeInfo));

    private static string ResolveVersion()
    {
        try
        {
            var name = typeof(CliApp).Assembly.GetName();
            var version = name.Version;
            if (version is not null) return $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch (Exception)
        {
            // 回退到固定版本号
        }
        return "1.0.0";
    }

    private static void PrintUsage()
    {
        Log.Raw("""
            byxcr · 兼容 NativeAOT 的容器镜像下载与同步工具

            用法：
              byxcr <命令> [参数...] [选项]

            命令：
              run                        启动调度守护进程（定时同步；不带子命令时只显示本帮助）
                                         随守护进程提供 WebAPI 与 Web 控制台（默认 5088 端口，浏览器打开 http://<主机>:5088/）
              sync [镜像...]             立即同步（不指定镜像则同步全部启用项）
                                        加 -l 时忽略 WebDAV，本次归档到本地目录
                                        -o/--output 可指定本次的归档位置（不影响任务配置）
              list [关键字]              列出镜像任务（按添加时间倒序，最多 20 条，可按关键字筛选）
              add <镜像>                 校验可拉取后加入同步列表，并立刻拉起后台下载进程
                                        -i/--interval 可指定检查频率（30d/24h/1440m/86400s，可组合如 1d12h，不带单位按秒）
                                        -o/--output 可指定输出位置（无扩展名自动补 .tar）
                                        --no-check 跳过拉取校验，直接写入列表
                                        任务已存在时：-i 与库中不一致就更新检查频率，-o 则更新输出位置，
                                        两者都不带则保留原值（频率改短后若已到期会顺带补一次下载）
              remove <镜像>              从同步列表移除
              enable <镜像>              启用镜像同步任务
              disable <镜像>             停用镜像同步任务
              interval <镜像> <时长>      修改该镜像的定时检查频率（别名 iv）
              records [镜像]             查看同步记录（--clear 同时清除 3 天前的记录）
              registries                 探测并列出镜像源的可用性
              doctor                     输出完整环境自检报告
              config                     打印生效后的完整配置（YAML，-j 输出 JSON）
              init                       在数据目录（./data）生成默认配置与环境变量示例
              version                    显示版本号
              help                       显示本帮助

            选项：
              -c, --config <路径>        指定配置文件（默认 ./data/byxcr.yaml）
              -j, --json                 以 JSON 输出结果
              -v, --verbose              输出调试日志（含下载进度）
              -f, --force                忽略摘要比对，强制重新下载
              -l, --local                sync 时忽略 WebDAV，本次归档到本地目录
              -i, --interval <时长>      add 时的检查频率
              -o, --output <路径>        add 的输出位置 / sync 本次的归档位置（相对归档根）
                  --disable              配合 add 使用，添加为停用状态
                  --limit <数量>         records/list 返回的记录条数（list 默认 20）
                  --clear                清除 3 天前的同步记录（配合 records 使用）
                  --no-check             add 时跳过「能否拉取」校验，直接写入同步列表

            环境变量：
              BYXCR_CONFIG               配置文件路径
              BYXCR__<节>__<键>          覆盖任意配置项，如 BYXCR__Storage__DataDir=/data
              BYXCR_DATA_DIR             数据根目录（扁平别名）
              BYXCR_REGISTRIES           镜像源列表，逗号分隔
              BYXCR_PLATFORMS            拉取平台（留空 = 全部架构），如 linux/amd64,linux/arm64
              BYXCR_WEBDAV               是否把归档写入 WebDAV（true/false）
              BYXCR_WEBDAV_URL           WebDAV 基础集合地址
              BYXCR_WEBDAV_USERNAME      WebDAV 用户名
              BYXCR_WEBDAV_PASSWORD      WebDAV 密码
              BYXCR_SYNC_INTERVAL        默认检查间隔（分钟）
              BYXCR_MAX_CONCURRENCY      并发下载数
              BYXCR_API_TOKEN            写接口令牌（/、/api、/api/list、/api/health 无需令牌）
              BYXCR_API_PORT             WebAPI 监听端口（默认 5088）
              BYXCR_LOG_LEVEL            日志级别

            下载通道：
              只有一条通道：直接调用 Registry V2 API。

            产物格式：
              OCI 镜像布局（image-layout）打包为 tar 后 gzip 压缩，归档名统一为 .tar（不带 .gz），
              同一个文件既装得进 podman / 新版 Docker，也装得进传统 Docker（graph driver）：
                oci-layout                {"imageLayoutVersion":"1.0.0"}
                index.json                根索引，只含一条 manifest（归档内恰好一个镜像）
                manifest.json             docker-archive 兼容清单（传统 docker 的 docker load 只认它）
                blobs/sha256/<hex>        清单 / 配置 / 图层 blob，与上游逐字节一致
              多架构镜像默认下载清单中的全部平台并合并进同一个 tar（download.platforms 可限定），
              各平台清单先合成内层索引、再由根索引指向，导入时按机器架构自动挑选；
              docker-archive 一个条目只能描述一个镜像，故 manifest.json 只登记一个平台
              （优先宿主机架构），传统 Docker 只导入这一个 —— 多平台请用 podman load 或新版 Docker。

            归档规则：
              「仓库主机名（非 Docker Hub 时）+ 仓库名」为目录、tag 为文件名：
                mysql:5.6                  -> {归档目标}/library/mysql/5.6.tar
                alpine/socat               -> {归档目标}/alpine/socat/latest.tar
                quay.io/prometheus/busybox -> {归档目标}/quay.io/prometheus/busybox/latest.tar
              归档目标默认为 storage.imageRoot；启用 storage.webdav 后改为上传到 WebDAV 集合。
            """);
    }
}
