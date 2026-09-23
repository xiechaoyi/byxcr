using System.Text.Json.Serialization;

namespace Byxcr.Configuration;

/// <summary>应用配置根节点。支持配置文件与（优先的）环境变量两种来源。</summary>
public sealed class AppConfig
{
    public StorageConfig Storage { get; set; } = new();

    public DownloadConfig Download { get; set; } = new();

    public CompressionConfig Compression { get; set; } = new();

    public RegistryConfig Registry { get; set; } = new();

    public SyncConfig Sync { get; set; } = new();

    public ApiConfig Api { get; set; } = new();

    public LogConfig Log { get; set; } = new();
}

public sealed class StorageConfig
{
    /// <summary>数据根目录，其余路径默认基于它派生。</summary>
    public string DataDir { get; set; } = "./data";

    /// <summary>SQLite 数据库文件；留空则使用 {DataDir}/byxcr.db。</summary>
    public string DatabaseFile { get; set; } = "";

    /// <summary>镜像归档根目录；留空则使用与 {DataDir} <strong>同级</strong>的 images 目录（./data → ./images）。</summary>
    public string ImageRoot { get; set; } = "";

    /// <summary>配置文件路径；留空则按 <see cref="ConfigLoader"/> 的探测顺序查找。</summary>
    public string ConfigFile { get; set; } = "";

    /// <summary>临时目录；留空则使用系统临时目录下的 byxcr 子目录（不在进程工作目录内产生中间文件）。</summary>
    public string TempDir { get; set; } = "";

    /// <summary>压缩完成后是否保留中间 .tar 文件。</summary>
    public bool KeepTar { get; set; } = false;

    /// <summary>把归档上传到 WebDAV 服务（启用后归档目标不再是本地 imageRoot）。</summary>
    public WebdavConfig Webdav { get; set; } = new();
}

/// <summary>
/// WebDAV 归档目标。启用后最终产物通过 HTTP PUT 写入远端集合，
/// 归档相对路径（仓库名/tag.tar，非 Docker Hub 的镜像还带主机名）原样拼在 <see cref="Url"/> 之后。
/// </summary>
public sealed class WebdavConfig
{
    /// <summary>是否把归档写入 WebDAV；为 false 时写本地 storage.imageRoot。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// WebDAV 基础集合地址，例如
    /// <c>https://dav.example.com/dav/byxcr</c>、
    /// <c>https://dav.jianguoyun.com/dav/byxcr</c>、
    /// <c>http://nas.lan:5005/byxcr</c>。
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>用户名。标注宽松读取，避免 YAML 里未加引号的纯数字账号被解析成数字。</summary>
    [JsonConverter(typeof(LenientStringConverter))]
    public string Username { get; set; } = "";

    [JsonConverter(typeof(LenientStringConverter))]
    public string Password { get; set; } = "";

    /// <summary>单次上传超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 1800;

    /// <summary>上传失败后的额外重试次数。</summary>
    public int Retries { get; set; } = 2;

    /// <summary>上传前自动逐级创建集合（MKCOL），已存在时忽略。</summary>
    public bool CreateCollections { get; set; } = true;

    /// <summary>上传成功后是否同时在本地 imageRoot 保留一份副本。</summary>
    public bool KeepLocalCopy { get; set; } = false;

    /// <summary>跳过 TLS 证书校验（自签名证书的内网 WebDAV）。</summary>
    public bool AllowInvalidCertificate { get; set; } = false;
}

/// <summary>
/// 下载通道配置。byxcr 只用一种通道：Registry V2 HTTP 客户端，
/// 产物为 OCI 镜像布局（oci-layout + index.json + blobs/sha256/*）。
/// </summary>
public sealed class DownloadConfig
{
    /// <summary>
    /// 平台过滤，形如 <c>linux/amd64</c>、<c>linux/arm64/v8</c>。
    /// <para>留空（默认）表示下载清单中的<strong>全部架构</strong>，并合并进同一个归档。</para>
    /// </summary>
    public List<string> Platforms { get; set; } = [];

    /// <summary>单个 HTTP 请求的最大尝试次数。</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>请求超时（秒）；仅约束「拿到响应头」阶段，不会中断大 blob 的持续下载。</summary>
    public int RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>同时下载的最大 blob 数。</summary>
    public int MaxParallelDownloads { get; set; } = 4;
}

public sealed class CompressionConfig
{
    /// <summary>auto：优先外部 gzip/pigz，缺失时回退内置实现；external：仅外部；managed：仅内置。</summary>
    public string Mode { get; set; } = "auto";

    /// <summary>压缩级别 1-9。</summary>
    public int Level { get; set; } = 6;

    /// <summary>外部压缩程序路径，留空自动查找 pigz / gzip。</summary>
    public string? GzipPath { get; set; }

    /// <summary>pigz 线程数，0 表示自动。</summary>
    public int Threads { get; set; } = 0;

    /// <summary>单条外部命令超时时间（秒）。</summary>
    public int CommandTimeoutSeconds { get; set; } = 3600;
}

public sealed class RegistryConfig
{
    /// <summary>是否把官方源（docker.io）排在最前。</summary>
    public bool UseDefaultRegistryFirst { get; set; } = true;

    /// <summary>使用前是否探测镜像源可用性。</summary>
    public bool VerifyBeforeUse { get; set; } = true;

    /// <summary>镜像源探测超时（秒）。</summary>
    public int ProbeTimeoutSeconds { get; set; } = 6;

    /// <summary>按顺序尝试的镜像源列表。</summary>
    public List<RegistryItem> Items { get; set; } =
    [
        new RegistryItem { Name = "docker.1ms.run" },
        new RegistryItem { Name = "mirror.ccs.tencentyun.com" },
        new RegistryItem { Name = "dockerproxy.net" },
        new RegistryItem { Name = "hub.rat.dev" },
        new RegistryItem { Name = "docker.1panel.live" },
    ];

    /// <summary>
    /// 私有仓库凭据（键为仓库主机名，如 ghcr.io）。
    /// 下载与摘要预检会用它向 token 服务换取 Bearer token；Token 非空时直接作为 Bearer 使用。
    /// </summary>
    public Dictionary<string, RegistryCredential> Credentials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RegistryCredential
{
    /// <summary>账号。与口令一样标注宽松读取：YAML 里纯数字的内容不会被误当成数字。</summary>
    [JsonConverter(typeof(LenientStringConverter))]
    public string Username { get; set; } = "";

    [JsonConverter(typeof(LenientStringConverter))]
    public string Password { get; set; } = "";

    /// <summary>可直接使用的 Bearer token；设置后优先于用户名/密码。</summary>
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Token { get; set; }
}

public sealed class RegistryItem
{
    /// <summary>镜像源主机名，例如 docker.1ms.run。</summary>
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>是否使用 http 而非 https。</summary>
    public bool Insecure { get; set; } = false;

    /// <summary>探测用的 /v2/ 主机，默认与 Name 相同。</summary>
    public string? ProbeHost { get; set; }
}

public sealed class SyncConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>未单独指定时的默认检查间隔（分钟）。</summary>
    public int DefaultIntervalMinutes { get; set; } = 1440;

    /// <summary>调度扫描周期（秒）。</summary>
    public int ScanIntervalSeconds { get; set; } = 60;

    /// <summary>并发下载数量（add 后台下载、手动 sync 的多镜像并发）。</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>定时调度的并发下载数量：任务列表很长时也不会一次性铺开，默认 4（上限 4，即始终小于 5）。</summary>
    public int ScheduleConcurrency { get; set; } = 4;

    /// <summary>digest：镜像内容无变化则跳过；always：每次都重新下载。</summary>
    public string CheckStrategy { get; set; } = "digest";

    /// <summary>
    /// 是否忽略「归档文件已存在」这一判定。默认 false：摘要未变化<b>且</b>归档目标上确实有文件才跳过下载。
    /// 设为 true 后不再探测归档目标（本地目录 / WebDAV），只要远端摘要未变化就跳过下载。
    /// </summary>
    public bool IgnoreArchiveCheck { get; set; } = false;

    /// <summary>单个镜像源失败后的额外重试次数。</summary>
    public int RetryTimes { get; set; } = 1;

    public int RetryDelaySeconds { get; set; } = 10;

    /// <summary>启动时立即同步一轮（忽略间隔）。</summary>
    public bool SyncOnStartup { get; set; } = false;
}

/// <summary>
/// 内置 WebAPI 服务。为 add / sync / remove / records / enable / disable（外加 list / health / 接口索引）提供 HTTP 接口，
/// 并在根路由 <c>/</c> 提供一个直接浏览同步任务列表的 Web 控制台页面；
/// 自身实现在 <see cref="Services.ApiServer"/>：基于 TcpListener 的零依赖 HTTP/1.1，不引入 ASP.NET，
/// 以便在 NativeAOT 下正常工作，也绕开 Windows 上 HttpListener 需要 URL ACL 预留的限制。
/// </summary>
public sealed class ApiConfig
{
    /// <summary>是否启用 WebAPI：<c>byxcr run</c> 启动守护进程时会一并监听（没有单独的启动命令）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>监听地址：留空监听所有地址；填 127.0.0.1 只对本机开放，填具体网卡地址则只监听该网卡。</summary>
    public string Host { get; set; } = "";

    /// <summary>监听端口。</summary>
    public int Port { get; set; } = 5088;

    /// <summary>
    /// 写接口的调用令牌。非空时 add / sync / remove / enable / disable 等必须携带
    /// <c>Authorization: Bearer &lt;token&gt;</c>（也接受直接写令牌），与配置一致才允许调用；
    /// <strong>留空时这些接口一律返回「请先配置Token」</strong>。
    /// <para>公开接口不需要令牌：根路由 <c>/</c>（Web 控制台页面）、<c>/api</c>（接口清单）、<c>/api/list</c>（任务列表）、<c>/api/health</c>。</para>
    /// </summary>
    [JsonConverter(typeof(LenientStringConverter))]
    public string Token { get; set; } = "";
}

public sealed class LogConfig
{
    public string Level { get; set; } = "info";

    public string? File { get; set; }

    public bool Color { get; set; } = true;

    /// <summary>
    /// 展示时区（如 Asia/Shanghai、China Standard Time 或固定偏移 +08:00）。
    /// 留空则用环境变量 TZ，仍未设置则用系统时区。数据库里始终存 UTC，只有展示时换算。
    /// </summary>
    public string TimeZone { get; set; } = "";

    /// <summary>
    /// 日志输出目录（后台下载日志等）。留空时 Linux/macOS 用 /var/log/byxcr，Windows 用 {数据目录}/logs；
    /// 相对路径按数据目录解析。目录不可写时自动退化到 {数据目录}/logs。
    /// </summary>
    public string Directory { get; set; } = "";
}
