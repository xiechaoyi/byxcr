namespace Byxcr.Configuration;

/// <summary>`byxcr init` 写出的环境变量覆盖示例（与默认配置文件一起生成）。</summary>
public static class DefaultEnvTemplate
{
    /// <summary>示例文件名，与配置文件放在同一目录。</summary>
    public const string FileName = "byxcr.env.example";

    public const string Content = """
    # byxcr 环境变量覆盖示例
    # 由 `byxcr init` 生成，可复制为 byxcr.env 后由 systemd 的 EnvironmentFile 读取。
    # 环境变量优先级高于配置文件，可用于在不同环境差异化部署。

    BYXCR__Storage__DataDir=/var/lib/byxcr

    # 镜像源（逗号分隔或 JSON 数组）
    BYXCR__Registry__Items=docker.1ms.run,mirror.ccs.tencentyun.com,dockerproxy.net

    # 待同步镜像列表保存在 SQLite 数据库中，没有对应的环境变量：
    #   byxcr add mysql:5.6 --interval 24h
    #   byxcr list / remove / enable / disable / interval

    BYXCR__Sync__DefaultIntervalMinutes=1440
    BYXCR__Sync__MaxConcurrency=2
    # 定时调度的并发下载数（上限 4，始终小于 5）
    BYXCR__Sync__ScheduleConcurrency=4
    BYXCR__Sync__ScanIntervalSeconds=60
    BYXCR__Sync__CheckStrategy=digest
    # false=摘要未变化且归档确实存在才跳过；true=只比摘要，摘要未变化就跳过（不再探测归档文件）
    BYXCR__Sync__IgnoreArchiveCheck=false

    # 下载通道：Registry V2，产物为 OCI 镜像布局。
    # 平台过滤留空 = 下载清单中的全部架构并合并进同一个 tar（多架构镜像全平台拉取）；
    # 也可只挑部分平台，例如 linux/amd64,linux/arm64
    # BYXCR__Download__Platforms=linux/amd64,linux/arm64
    BYXCR__Download__MaxRetries=3
    BYXCR__Download__RequestTimeoutSeconds=300
    BYXCR__Download__MaxParallelDownloads=4

    # 临时目录：默认落在系统临时目录下的 byxcr 子目录（中间 tar / OCI 工作目录都在那儿，
    # 不会在进程工作目录产生临时文件）。需要固定位置时可显式指定：
    # BYXCR__Storage__TempDir=/var/cache/byxcr

    # 归档目标改为 WebDAV（可选）；默认写本地 storage.imageRoot。
    # 相对路径（仓库名/tag.tar，非 Docker Hub 的镜像还带主机名）会拼接在 url 之后，例如
    #   https://dav.example.com/dav/byxcr/library/mysql/5.6.tar
    # BYXCR__Storage__Webdav__Enabled=true
    # BYXCR__Storage__Webdav__Url=https://dav.example.com/dav/byxcr
    # BYXCR__Storage__Webdav__Username=your-name
    # BYXCR__Storage__Webdav__Password=your-password
    # 上传成功后在本地 imageRoot 同时保留一份副本
    # BYXCR__Storage__Webdav__KeepLocalCopy=true

    # 私有仓库凭据（键含点号的主机名请用 JSON 整体覆盖，纯标量键无法表达 ghcr.io 这类层级）
    # BYXCR__Registry__Credentials='{"ghcr.io":{"username":"your-name","password":"your-token"}}'

    BYXCR__Compression__Mode=auto
    BYXCR__Compression__Level=6

    # 内置 WebAPI + Web 控制台：byxcr run 会随守护进程监听 5088 端口；根路由 / 即控制台页面。
    # /、/api、/api/list、/api/health 为公开接口，无需令牌；
    # 写接口需携带 Authorization: Bearer <token>，留空则写接口一律返回「请先配置Token」。
    # BYXCR_API_TOKEN=change-me
    # BYXCR_API_PORT=5088
    # BYXCR_API_HOST=127.0.0.1

    BYXCR__Log__Level=info
    BYXCR__Log__Color=false
    # 日志输出目录（后台下载日志等）：留空 = Linux/macOS /var/log/byxcr，Windows {数据目录}/logs
    # BYXCR__Log__Directory=/var/log/byxcr
    # 时间显示时区（留空 = 环境变量 TZ = 系统时区）；数据库始终存 UTC，仅展示时换算。
    # Alpine 等精简镜像需先安装 tzdata 才能用 Asia/Shanghai 这类名称，也可直接写固定偏移。
    # BYXCR__Log__TimeZone=Asia/Shanghai
    # BYXCR__Log__TimeZone=+08:00
    """;
}
