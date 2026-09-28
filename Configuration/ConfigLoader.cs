using System.Text.Json;
using System.Text.Json.Nodes;
using Byxcr.Core;

namespace Byxcr.Configuration;

public sealed class ConfigLoadResult
{
    public required AppConfig Config { get; init; }

    /// <summary>最终使用的配置文件路径（可能尚不存在）。</summary>
    public required string ConfigFilePath { get; init; }

    public bool ConfigFileExists { get; init; }

    /// <summary>实际生效的环境变量名列表。</summary>
    public List<string> EnvOverrides { get; init; } = [];

    /// <summary>装载过程中发现的废弃配置项提示（由调用方在日志就绪后打印）。</summary>
    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// 配置装载：默认值 → 配置文件深合并 → 环境变量覆盖（环境变量优先级最高）。
/// </summary>
public static class ConfigLoader
{
    public const string EnvConfigPath = "BYXCR_CONFIG";

    /// <summary>默认配置文件名（YAML 格式）。</summary>
    public const string DefaultFileName = "byxcr.yaml";

    /// <summary>默认数据目录名，配置文件默认放在它下面。</summary>
    public const string DefaultDataDirectoryName = "data";

    /// <summary>默认数据目录，未显式配置 storage.dataDir 时使用。</summary>
    public const string DefaultDataDir = "./" + DefaultDataDirectoryName;

    /// <summary>
    /// 按顺序探测的相对路径：数据目录里的配置优先，其次工作目录，最后程序目录，<c>.yaml</c> 优先于 <c>.yml</c>。
    /// 只支持 YAML，配置文件必须是 YAML 格式。
    /// </summary>
    private static readonly string[] RelativeCandidates =
    [
        Path.Combine(DefaultDataDirectoryName, "byxcr.yaml"),
        Path.Combine(DefaultDataDirectoryName, "byxcr.yml"),
        "byxcr.yaml",
        "byxcr.yml",
    ];

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig CreateDefault() => new();

    public static string BuildDefaultJson() => JsonSerializer.Serialize(CreateDefault(), ByxcrJson.Default.AppConfig);

    /// <summary>按 CLI 参数 → 环境变量 → 数据目录 → 当前目录 → 程序目录的顺序确定配置文件位置。</summary>
    public static string ResolvePath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath.Trim());

        var fromEnv = Environment.GetEnvironmentVariable(EnvConfigPath);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return Path.GetFullPath(fromEnv.Trim());

        foreach (var baseDirectory in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            foreach (var relative in RelativeCandidates)
            {
                var candidate = Path.GetFullPath(Path.Combine(baseDirectory, relative));
                if (File.Exists(candidate)) return candidate;
            }
        }

        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, DefaultDataDirectoryName, DefaultFileName));
    }

    public static ConfigLoadResult Load(string? explicitPath)
    {
        var path = ResolvePath(explicitPath);
        var exists = File.Exists(path);

        var warnings = new List<string>();
        var root = JsonNode.Parse(BuildDefaultJson(), null, DocumentOptions) as JsonObject ?? new JsonObject();

        if (exists)
        {
            var text = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var parsed = ParseConfigText(text, path);
                if (parsed is not JsonObject fileRoot)
                    throw new ConfigurationException($"配置文件根节点必须是键值对（YAML 映射）：{path}");

                if (fileRoot["sync"] is JsonObject syncNode && syncNode["images"] is not null)
                {
                    warnings.Add($"配置文件里的 sync.images 已废弃并被忽略（{path}）：{DeprecatedImagesHint}");
                }

                JsonMerge.DeepMerge(root, fileRoot);
            }
        }

        foreach (var envName in DeprecatedImageEnvVars)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envName)))
            {
                warnings.Add($"环境变量 {envName} 已废弃并被忽略：{DeprecatedImagesHint}");
            }
        }

        var envKeys = EnvOverride.Apply(root);

        AppConfig config;
        try
        {
            config = JsonSerializer.Deserialize(root.ToJsonString(), ByxcrJson.Default.AppConfig) ?? CreateDefault();
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException($"配置内容无法解析：{ex.Message}");
        }

        Normalize(config);
        return new ConfigLoadResult
        {
            Config = config,
            ConfigFilePath = path,
            ConfigFileExists = exists,
            EnvOverrides = envKeys,
            Warnings = warnings,
        };
    }

    private const string DeprecatedImagesHint =
        "待同步镜像列表已改为只保存在 SQLite 数据库中，请用 byxcr add / remove / enable / disable / interval 维护";

    /// <summary>已废弃的镜像列表环境变量，出现时仅提示、不再生效。</summary>
    private static readonly string[] DeprecatedImageEnvVars = ["BYXCR_IMAGES", "BYXCR__Sync__Images"];

    /// <summary>解析配置文件正文：一律按 YAML 解析（JSON 格式的配置文件已不再支持）。</summary>
    private static JsonNode? ParseConfigText(string text, string path)
    {
        try
        {
            return YamlReader.Parse(text, path);
        }
        catch (ConfigurationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ConfigurationException($"配置文件解析失败（{path}）：{ex.Message}");
        }
    }

    /// <summary>补全默认路径并裁剪非法取值。</summary>
    public static void Normalize(AppConfig config)
    {
        var baseDirectory = Environment.CurrentDirectory;

        config.Storage ??= new StorageConfig();
        config.Download ??= new DownloadConfig();
        config.Compression ??= new CompressionConfig();
        config.Registry ??= new RegistryConfig();
        config.Sync ??= new SyncConfig();
        config.Api ??= new ApiConfig();
        config.Log ??= new LogConfig();
        config.Log.TimeZone = (config.Log.TimeZone ?? string.Empty).Trim();
        config.Log.Directory = (config.Log.Directory ?? string.Empty).Trim();

        config.Storage.DataDir = ResolveDirectory(config.Storage.DataDir, DefaultDataDir, baseDirectory, null);
        var dataDir = config.Storage.DataDir;
        config.Storage.DatabaseFile = ResolveDirectory(config.Storage.DatabaseFile, Path.Combine(dataDir, "byxcr.db"), baseDirectory, dataDir);
        // 归档目录默认与数据目录同级（./data → ./images），中间产物与数据库分开存放
        config.Storage.ImageRoot = ResolveDirectory(config.Storage.ImageRoot, SiblingDirectory(dataDir, "images"), baseDirectory, dataDir);
        // 临时目录默认落在系统临时目录下，避免中间 tar / OCI 工作目录污染进程工作目录
        config.Storage.TempDir = ResolveDirectory(config.Storage.TempDir, Path.Combine(Path.GetTempPath(), "byxcr"), baseDirectory, dataDir);

        config.Storage.Webdav ??= new WebdavConfig();
        var webdav = config.Storage.Webdav;
        webdav.Url = (webdav.Url ?? string.Empty).Trim().TrimEnd('/');
        webdav.Username = (webdav.Username ?? string.Empty).Trim();
        webdav.Password ??= string.Empty;
        webdav.TimeoutSeconds = Math.Clamp(webdav.TimeoutSeconds, 10, 86400);
        webdav.Retries = Math.Clamp(webdav.Retries, 0, 10);
        if (webdav.Enabled)
        {
            if (webdav.Url.Length == 0)
                throw new ConfigurationException("storage.webdav.enabled 为 true，但未配置 storage.webdav.url");
            if (!Uri.TryCreate(webdav.Url, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                throw new ConfigurationException($"storage.webdav.url 不是合法的 http(s) 地址：{webdav.Url}");
            }
        }

        if (config.Download.Platforms is null) config.Download.Platforms = [];
        config.Download.Platforms = config.Download.Platforms
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().Trim('/').ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.Download.MaxRetries = Math.Clamp(config.Download.MaxRetries, 1, 10);
        config.Download.RequestTimeoutSeconds = Math.Clamp(config.Download.RequestTimeoutSeconds, 10, 86400);
        config.Download.MaxParallelDownloads = Math.Clamp(config.Download.MaxParallelDownloads, 1, 16);

        config.Compression.Mode = (config.Compression.Mode ?? "auto").Trim().ToLowerInvariant();
        if (config.Compression.Mode is not ("auto" or "external" or "managed")) config.Compression.Mode = "auto";
        config.Compression.Level = Math.Clamp(config.Compression.Level, 1, 9);
        config.Compression.Threads = Math.Clamp(config.Compression.Threads, 0, 64);
        config.Compression.CommandTimeoutSeconds = Math.Clamp(config.Compression.CommandTimeoutSeconds, 30, 86400);
        if (!string.IsNullOrWhiteSpace(config.Compression.GzipPath)) config.Compression.GzipPath = config.Compression.GzipPath.Trim();

        config.Registry.ProbeTimeoutSeconds = Math.Clamp(config.Registry.ProbeTimeoutSeconds, 2, 60);
        if (config.Registry.Items is null) config.Registry.Items = [];
        config.Registry.Items = config.Registry.Items
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item =>
            {
                item.Name = item.Name.Trim().TrimEnd('/');
                return item;
            })
            .ToList();

        var credentials = new Dictionary<string, RegistryCredential>(StringComparer.OrdinalIgnoreCase);
        foreach (var (host, credential) in config.Registry.Credentials ?? [])
        {
            if (credential is null || string.IsNullOrWhiteSpace(host)) continue;
            credential.Username ??= string.Empty;
            credential.Password ??= string.Empty;
            credential.Token = credential.Token?.Trim();
            if (credential.Username.Length == 0 && string.IsNullOrEmpty(credential.Token)) continue;
            credentials[host.Trim().ToLowerInvariant()] = credential;
        }

        config.Registry.Credentials = credentials;

        config.Sync.ScanIntervalSeconds = Math.Clamp(config.Sync.ScanIntervalSeconds, 5, 86400);
        config.Sync.MaxConcurrency = Math.Clamp(config.Sync.MaxConcurrency, 1, 32);
        // 定时调度的并发上限固定为 4（小于 5），避免任务列表很长时一次性铺开太多下载
        config.Sync.ScheduleConcurrency = Math.Clamp(config.Sync.ScheduleConcurrency, 1, 4);
        config.Sync.DefaultIntervalMinutes = Math.Clamp(config.Sync.DefaultIntervalMinutes, 1, 525600);
        config.Sync.RetryTimes = Math.Clamp(config.Sync.RetryTimes, 0, 10);
        config.Sync.RetryDelaySeconds = Math.Clamp(config.Sync.RetryDelaySeconds, 0, 600);
        config.Sync.CheckStrategy = string.Equals((config.Sync.CheckStrategy ?? "digest").Trim(), "always", StringComparison.OrdinalIgnoreCase)
            ? "always"
            : "digest";

        config.Api.Host = (config.Api.Host ?? string.Empty).Trim();
        config.Api.Token = (config.Api.Token ?? string.Empty).Trim();
        config.Api.Port = Math.Clamp(config.Api.Port, 1, 65535);

        config.Log.Level = string.IsNullOrWhiteSpace(config.Log.Level) ? "info" : config.Log.Level.Trim();
    }

    private static string ResolveDirectory(string? value, string fallback, string baseDirectory, string? dataDir)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        if (dataDir is not null) text = text.Replace("{dataDir}", dataDir, StringComparison.OrdinalIgnoreCase);
        text = text.Replace("{cwd}", baseDirectory, StringComparison.OrdinalIgnoreCase);

        if (text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            text = Path.Combine(home, text[2..]);
        }

        return Path.GetFullPath(text, baseDirectory);
    }

    /// <summary>
    /// 取与 <paramref name="directory"/> 同级的兄弟目录（位于同一个父目录下）。
    /// 例如 <c>W:\Byxcr\data</c> → <c>W:\Byxcr\images</c>、<c>/srv/byxcr/data</c> → <c>/srv/byxcr/images</c>。
    /// 路径退化到根目录时回退为 <paramref name="directory"/> 下的子目录。
    /// </summary>
    private static string SiblingDirectory(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(directory)) return name;

        var full = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Length == 0) return name;

        var separator = full.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        // 去掉驱动器/根前缀后没有剩余层级（如 C:\ 或 /），无法取兄弟目录
        var parent = separator <= 0 ? string.Empty : full[..separator];
        return parent.Length == 0 ? Path.Combine(full, name) : Path.Combine(parent, name);
    }
}
