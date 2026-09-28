using System.Text.Json.Nodes;

namespace Byxcr.Core;

/// <summary>镜像同步任务（对应 images 表）。</summary>
public sealed class ImageTask
{
    public long Id { get; set; }

    /// <summary>规范化后的镜像名，例如 mysql:5.6。</summary>
    public string Image { get; set; } = "";

    public string Repository { get; set; } = "";

    public string Tag { get; set; } = "";

    /// <summary>
    /// 定时检查频率（<b>秒</b>，数据库与 JSON 均以秒为单位，给人看的输出走 <c>Duration.Format</c>）。CLI 可用 30d / 24h / 1440m / 86400s 书写，
    /// 不带单位按秒，最终落库统一为秒。
    /// </summary>
    public int IntervalSeconds { get; set; }

    /// <summary>
    /// 输出位置：归档文件的保存路径（相对归档根，用 / 分隔）。
    /// 为空时按默认规则（仓库主机 + 仓库名 / tag.tar）落盘；无扩展名时自动补 .tar。
    /// </summary>
    public string? Output { get; set; }

    public bool Enabled { get; set; } = true;

    public string CreatedAt { get; set; } = "";

    public string UpdatedAt { get; set; } = "";

    public string? LastCheckedAt { get; set; }

    public string? LastSuccessAt { get; set; }

    /// <summary>最近一次成功同步时记录的上游摘要。</summary>
    public string? LastDigest { get; set; }

    /// <summary>idle / running / success / skipped / failed。</summary>
    public string? LastStatus { get; set; }

    public string? LastError { get; set; }

    /// <summary>
    /// 最近一次成功同步产出的文件位置：本地归档是绝对路径，WebDAV 归档是完整地址。
    /// <strong>仅限本机使用</strong>——对外输出（WebAPI / Web 控制台）会经
    /// <see cref="ArchivePath.Relative"/> 折叠成相对归档根的路径。
    /// </summary>
    public string? LastFile { get; set; }
}

/// <summary>WebAPI 的请求体（字段均为可选，缺省时回退到查询串）。</summary>
public sealed class ApiRequest
{
    public string? Image { get; set; }

    /// <summary>批量镜像（sync 可用）。</summary>
    public List<string>? Images { get; set; }

    /// <summary>list 时的镜像名关键字筛选（忽略大小写子串匹配）；也接受查询串的 <c>filter</c> / <c>keyword</c> / <c>q</c>。</summary>
    public string? Filter { get; set; }

    /// <summary>list 时按启用状态筛选：true 只看已启用，false 只看已停用，缺省为全部。</summary>
    public bool? Enabled { get; set; }

    /// <summary>list 时的页码（从 1 开始，配合 limit 使用）。</summary>
    public int? Page { get; set; }

    /// <summary>
    /// add 时的检查频率：<c>"30d"</c> / <c>"24h"</c> / <c>"1440m"</c> / <c>"86400s"</c>，或裸数字（按秒）。
    /// 用 JsonNode 承接，是为了同一份请求体里既接受字符串时长也接受数字。
    /// </summary>
    public JsonNode? Interval { get; set; }

    /// <summary>add 时是否添加为停用状态。</summary>
    public bool? Disable { get; set; }

    /// <summary>add 时的输出位置（归档路径，无扩展名自动补 .tar）。</summary>
    public string? Output { get; set; }

    /// <summary>add 时跳过「仓库与标签能否拉取」的预检，直接写入同步列表。</summary>
    public bool? NoCheck { get; set; }

    /// <summary>sync 时忽略摘要比对，强制重新下载。</summary>
    public bool? Force { get; set; }

    /// <summary>records 时同时清除 3 天前的记录。</summary>
    public bool? Clear { get; set; }

    /// <summary>records 返回条数。</summary>
    public int? Limit { get; set; }
}

/// <summary>WebAPI 的统一响应体：只有与本次操作相关的字段会输出（null 字段被忽略）。</summary>
public sealed class ApiResponse
{
    public bool Ok { get; set; }

    public string Message { get; set; } = "";

    /// <summary>add / enable / disable / remove 涉及的镜像任务。</summary>
    public ImageTask? Task { get; set; }

    /// <summary>list 返回的镜像任务列表（分页时只含当前页）。</summary>
    public List<ImageTask>? Tasks { get; set; }

    /// <summary>list 时符合筛选条件的任务总数（不受分页影响）。</summary>
    public int? Total { get; set; }

    /// <summary>list 分页：当前页码（从 1 开始）。</summary>
    public int? Page { get; set; }

    /// <summary>list 分页：每页条数；缺省表示未分页（一次返回全部）。</summary>
    public int? Limit { get; set; }

    /// <summary>list 分页：总页数。</summary>
    public int? PageCount { get; set; }

    /// <summary>list 生效的筛选关键字；未筛选时不输出。</summary>
    public string? Filter { get; set; }

    /// <summary>/api 返回的可用接口清单。</summary>
    public List<ApiEndpoint>? Endpoints { get; set; }

    /// <summary>sync / records 返回的同步记录。</summary>
    public List<SyncRecord>? Records { get; set; }

    /// <summary>records --clear 删除的条数。</summary>
    public int? Removed { get; set; }

    public string? Version { get; set; }
}

/// <summary>/api 返回的接口清单项（供调用方与人直接查阅）。</summary>
public sealed class ApiEndpoint
{
    /// <summary>可用方法：本项目里方法不参与路由，GET / POST 均可。</summary>
    public string Method { get; set; } = "GET/POST";

    /// <summary>接口路径，例如 /api/list。</summary>
    public string Path { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>是否无需 Authorization 令牌（公开接口）。</summary>
    public bool Public { get; set; }

    /// <summary>可用参数，例如 <c>filter, enabled, page, limit</c>。</summary>
    public string? Parameters { get; set; }
}

/// <summary>一次同步执行记录（对应 sync_records 表）。</summary>
public sealed class SyncRecord
{
    public long Id { get; set; }

    public long ImageId { get; set; }

    public string Image { get; set; } = "";

    /// <summary>success / skipped / failed。</summary>
    public string Status { get; set; } = "";

    /// <summary>下载通道；固定为 oci（Registry V2 + OCI 镜像布局）。</summary>
    public string? Channel { get; set; }

    /// <summary>实际使用的镜像源；空表示官方源。</summary>
    public string? Registry { get; set; }

    public string? Digest { get; set; }

    /// <summary>
    /// 归档文件位置：本地归档是绝对路径，WebDAV 归档是完整地址。
    /// <strong>仅限本机使用</strong>——对外输出（WebAPI）会经 <see cref="ArchivePath.Relative"/> 折叠成相对归档根的路径。
    /// </summary>
    public string? FilePath { get; set; }

    public long? FileSize { get; set; }

    public long? DurationMs { get; set; }

    public string? Message { get; set; }

    public string StartedAt { get; set; } = "";

    public string? FinishedAt { get; set; }

    /// <summary>触发方式：schedule / manual / startup / cli。</summary>
    public string? Trigger { get; set; }
}

/// <summary>镜像源探测结果。</summary>
public sealed class RegistryStatus
{
    public string Name { get; set; } = "";

    public bool Available { get; set; }

    public int StatusCode { get; set; }

    public long ElapsedMs { get; set; }

    public string? Message { get; set; }

    /// <summary>是否为官方源（docker.io）。</summary>
    public bool IsDefault { get; set; }
}

/// <summary>环境自检报告。</summary>
public sealed class StatusReport
{
    public string Version { get; set; } = "";

    public string ConfigFile { get; set; } = "";

    public bool ConfigFileExists { get; set; }

    public List<string> EnvOverrides { get; set; } = [];

    public string DataDir { get; set; } = "";

    public string DatabaseFile { get; set; } = "";

    /// <summary>本地归档根目录（仅 WebDAV 未启用时才是最终目标）。</summary>
    public string ImageRoot { get; set; } = "";

    /// <summary>当前实际使用的归档目标（本地目录或 WebDAV 地址）。</summary>
    public string ArchiveTarget { get; set; } = "";

    /// <summary>是否启用了 WebDAV 归档。</summary>
    public bool WebdavEnabled { get; set; }

    public int ImageCount { get; set; }

    /// <summary>下载通道与产物格式说明。</summary>
    public string DownloadChannel { get; set; } = "";

    /// <summary>本次生效的平台过滤；空表示全部架构。</summary>
    public List<string> Platforms { get; set; } = [];

    /// <summary>是否下载清单中的全部架构。</summary>
    public bool AllPlatforms { get; set; } = true;

    public int MaxRetries { get; set; }

    public int RequestTimeoutSeconds { get; set; }

    public int MaxParallelDownloads { get; set; }

    /// <summary>压缩方式说明。</summary>
    public string Compression { get; set; } = "";

    /// <summary>摘要预检策略：digest（默认，摘要未变化则跳过）或 always（每次重下）。</summary>
    public string CheckStrategy { get; set; } = "";

    /// <summary>是否忽略归档文件存在性检查：true 时摘要未变化即跳过下载。</summary>
    public bool IgnoreArchiveCheck { get; set; }

    /// <summary>当前展示时区（数据库存 UTC，展示时按此时区换算）。</summary>
    public string TimeZone { get; set; } = "";

    public List<RegistryStatus> Registries { get; set; } = [];
}
