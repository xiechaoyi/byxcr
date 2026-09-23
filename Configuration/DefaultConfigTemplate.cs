namespace Byxcr.Configuration;

/// <summary>`byxcr init` 写出的带注释默认配置模板（YAML）。</summary>
public static class DefaultConfigTemplate
{
    public const string Content = """
    # ============================================================
    #  byxcr 配置文件（YAML）
    #  默认位置：<数据目录>/byxcr.yaml（默认 ./data/byxcr.yaml）；
    #  可用 --config 或环境变量 BYXCR_CONFIG 指定其它位置。
    #  优先级：环境变量 > 本文件 > 内置默认值
    #  环境变量写法：
    #    嵌套式：BYXCR__Storage__DataDir=/data
    #    扁平式：BYXCR_DATA_DIR=/data、BYXCR_REGISTRIES=docker.1ms.run,mirror.ccs.tencentyun.com
    #  待同步镜像列表保存在 SQLite 数据库中（用 byxcr add / remove / enable / disable / interval 维护），
    #  不在本文件里声明。
    #  相对路径以「进程工作目录」为基准；支持 {dataDir} 与 {cwd} 占位符。
    #  提示：纯数字内容（口令、账号等）请加引号，否则会按 YAML 隐式类型解析成数字。
    # ============================================================

    storage:
      # 数据根目录，数据库默认落在它下面；归档目录默认在它的同级
      dataDir: ./data
      # 留空 => {dataDir}/byxcr.db
      databaseFile: ""
      # 留空 => ./images 目录，归档形如 library/mysql/5.6.tar
      imageRoot: "./images"
      # 留空 => 系统临时目录下的 byxcr 子目录（中间 tar 与 OCI 工作目录都在这里，不会在工作目录留下临时文件）
      tempDir: ""
      # 是否保留压缩前的中间 .tar 文件
      keepTar: false

      # 归档目标可改为 WebDAV：启用后归档 tar 通过 HTTP PUT 上传到远端集合
      webdav:
        # 是否启用 WebDAV 归档（false = 写本地 imageRoot）
        enabled: false
        # WebDAV 基础集合地址（坚果云 https://dav.jianguoyun.com/dav/、Nextcloud .../remote.php/dav/files/<用户>/ 等）
        url: ""
        username: ""
        password: ""
        # 单次上传超时（秒）
        timeoutSeconds: 1800
        # 上传失败后的额外重试次数
        retries: 2
        # 上传前自动逐级创建集合（MKCOL，已存在则忽略）
        createCollections: true
        # 上传成功后是否在本地 imageRoot 同时保留一份副本
        keepLocalCopy: false
        # 跳过 TLS 证书校验（自签名证书的内网 WebDAV）
        allowInvalidCertificate: false

    download:
      # 下载通道：Registry V2 协议，产物是 OCI 镜像布局。
      #
      # 平台过滤：留空 = 下载清单里的全部架构并合并进同一个 tar（多架构镜像会全部拉取）；
      # 也可只挑部分平台，例如 [linux/amd64, linux/arm64] 或 [linux/arm64/v8]
      platforms: []
      # 单个 HTTP 请求的最大尝试次数
      maxRetries: 3
      # 请求超时（秒），仅约束响应头阶段，不会中断大 blob 的持续下载
      requestTimeoutSeconds: 300
      # 同时下载的最大 blob 数（多架构镜像会有大量 blob）
      maxParallelDownloads: 4

    compression:
      # auto：优先系统 gzip/pigz，缺失时回退内置实现 | external：仅外部 | managed：仅内置
      mode: auto
      level: 6
      # 留空则自动查找 pigz -> gzip
      gzipPath: ""
      # pigz 线程数，0 表示自动
      threads: 0
      # 单条外部命令超时（秒）
      commandTimeoutSeconds: 3600

    registry:
      # 是否把官方源 docker.io 排在最前
      useDefaultRegistryFirst: true
      # 使用前是否探测镜像源可用性
      verifyBeforeUse: true
      probeTimeoutSeconds: 6
      # 按列表顺序自动尝试可用的镜像源
      items:
        - name: docker.1ms.run
          enabled: true
        - name: mirror.ccs.tencentyun.com
          enabled: true
        - name: dockerproxy.net
          enabled: true
        - name: hub.rat.dev
          enabled: true
        - name: docker.1panel.live
          enabled: true
      # 私有仓库凭据（键为镜像源主机名），下载与摘要预检都会使用。
      # 键里含冒号（如 registry.internal:5000）时务必加引号。
      # credentials:
      #   "ghcr.io":
      #     username: your-name
      #     password: your-token
      #   "registry.internal:5000":
      #     token: eyJhbGciOi...

    sync:
      enabled: true
      # 未单独指定时的默认检查间隔（分钟；写入数据库时换算成秒）
      defaultIntervalMinutes: 1440
      # 调度扫描周期（秒）
      scanIntervalSeconds: 60
      # 并发下载数（按镜像计）：add 后台下载与手动 sync 的多镜像并发
      maxConcurrency: 2
      # 定时调度的并发下载数：任务列表很多时也不会一次性铺开，默认 4，上限 4（即始终小于 5）
      scheduleConcurrency: 4
      # digest：镜像内容无变化则跳过下载 | always：每次都重新下载
      checkStrategy: digest
      # 是否忽略「归档文件已存在」这一判定：false=摘要未变化且归档确实存在才跳过；
      # true=只比摘要，摘要未变化就跳过（不再探测归档目标）
      ignoreArchiveCheck: false
      # 单个镜像源失败后的额外重试次数与间隔
      retryTimes: 1
      retryDelaySeconds: 10
      # 启动时是否立即同步一轮
      syncOnStartup: false

      # 待同步镜像列表保存在 SQLite（<dataDir>/byxcr.db 的 images 表），本文件不再声明：
      #   byxcr add mysql:5.6 --interval 24h     # 添加（时长支持 30d/24h/1440m/86400s）
      #   byxcr list                             # 查看
      #   byxcr interval mysql:5.6 720m          # 改频率
      #   byxcr disable mysql:5.6                # 停用
      #   byxcr remove mysql:5.6                 # 移除

    log:
      # debug | info | warn | error | none
      level: info
      # 追加写入的日志文件，留空表示只输出到控制台
      file: ""
      color: true
      # 时间显示时区：留空 = 环境变量 TZ，再留空 = 系统时区。
      # 可填 Asia/Shanghai、China Standard Time，或固定偏移 +08:00；
      # 数据库里始终存 UTC，只有 list/records/日志等展示时换算成该时区。
      timeZone: ""
      # 日志输出目录（后台下载日志等）：留空时 Linux/macOS 用 /var/log/byxcr，Windows 用 {数据目录}/logs
      directory: ""

    api:
      # 内置 WebAPI（add / sync / remove / records / enable / disable / list / health）+ Web 控制台，
      # 无需额外命令：byxcr run 启动守护进程会一并监听端口。
      # 根路由 / 是控制台页面（浏览同步任务，带筛选与分页）；/api 是接口清单，
      # /、/api、/api/list、/api/health 都是公开接口，不需要令牌。
      enabled: true
      # 监听地址：留空 = 监听所有地址；填 127.0.0.1 只对本机开放
      host: ""
      # 监听端口
      port: 5088
      # 写接口令牌：留空 = add / sync / remove / enable / disable 一律返回「请先配置Token」；
      # 非空时调用方必须携带 Authorization: Bearer <token>，与此处一致才允许调用。
      token: ""
    """;
}
