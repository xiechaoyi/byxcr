# byxcr - 镜像搬运箱

开源的轻量级容器镜像下载、定时同步工具，按目录结构保存为标准 OCI 镜像文件，方便后期维护。可直接通过WebDAV上传至网盘存储，还可配合Xcr镜像仓库，将镜像存储目录发布为自建镜像仓库。

顺便分享一下我的收藏：**[123云盘-DockerHub仓库](https://1856999906.share.123pan.cn/123pan/g2b0vd-lYIgH)**

## 主要特点

- **多个镜像源自动切换**：可以配置一串加速源，程序逐个探测，某个源不通就换下一个，不会因为单个源挂掉导致整批同步失败。
- **多架构合并成一个包**：默认拉取清单里的全部平台并合并进同一个 tar，跨平台共用的文件块按摘要去重；`download.platforms` 可以只要指定架构。
- **两种导入方式都认**：产物是 OCI image-layout 的 tar（内部 gzip 压缩，文件名保持 `.tar`）。同一个文件，podman、新版 Docker、传统 graph driver 的 Docker 都能直接 `load`。
- **按摘要决定要不要重下**：定时扫描到期任务，上游摘要没变就跳过，每次同步的结果都记进 SQLite。
- **三种操作方式**：命令行、WebAPI、内置 Web 控制台。

## 快速上手

### 用 Docker 跑

```bash
docker run -d --name=byxcr \
  -p 5088:5088 \
  -v $(pwd)/data:/wln/data \
  -v $(pwd)/images:/wln/images \
  ccr.ccs.tencentyun.com/xiechaoyi/xcr:byxcr

# 加一个任务
docker exec byxcr byxcr add mysql:5.6 --interval 24h
```

之后浏览器打开 `http://<主机>:5088/` 就是控制台。仓库里也带了 `docker-compose.yml`，改完环境变量 `docker compose up -d` 即可。

### 用本地二进制

```bash
byxcr init                          # 生成默认配置 ./data/byxcr.yaml（带注释）
byxcr doctor                        # 自检：镜像源、目录、压缩参数是否正常
byxcr add mysql:5.6 --interval 24h  # 添加任务，校验可拉取后立刻开始下载
byxcr run                           # 启动守护进程，按各任务自己的周期同步
```

不加参数或带 `-h` 只显示帮助，不会启动服务；定时同步必须显式执行 `run`。

## 归档位置与命名

归档根目录默认和数据目录同级（`./data` → `./images`）；启用 WebDAV 后改为上传到远端，不再写本地。

```text
data/
├── byxcr.yaml          配置文件
└── byxcr.db            任务、同步记录、状态
images/                 归档根目录
├── library/mysql/5.6.tar
└── alpine/socat/latest.tar
```

命名规则是「仓库目录 + tag 文件名」，非 Docker Hub 的镜像会带上主机名，避免同名镜像互相覆盖：

```text
mysql:5.6                  → images/library/mysql/5.6.tar
alpine/socat               → images/alpine/socat/latest.tar
quay.io/prometheus/busybox → images/quay.io/prometheus/busybox/latest.tar
```

`add -o` 和 `sync -o` 都能改归档位置：路径以 `/` 结尾视为目录，自动补上默认文件名；自己带了扩展名就按你写的来，不再补 `.tar`。区别是 `add -o` 写进任务配置长期生效，`sync -o` 只影响这一次。

## 配置

只认 YAML。优先级从高到低：环境变量 → `-c` 指定的配置文件 → 默认数据目录下的 `byxcr.yaml` → 程序内置默认值。相对路径以程序运行目录为基准，可以用 `{dataDir}`、`{cwd}` 占位。

### 环境变量

嵌套式用双下划线表示层级，能覆盖任意配置项；常用项另有扁平别名。数组既接受 JSON 数组，也接受逗号分隔。

```bash
# 嵌套式
export BYXCR__Storage__DataDir=/data
export BYXCR__Download__Platforms='["linux/amd64","linux/arm64"]'
export BYXCR__Storage__Webdav__Url=https://dav.example.com/dav/byxcr

# 扁平别名
export BYXCR_DATA_DIR=/data
export BYXCR_REGISTRIES="docker.1ms.run,mirror.ccs.tencentyun.com"
export BYXCR_PLATFORMS="linux/amd64,linux/arm64"
export BYXCR_WEBDAV=true
export BYXCR_WEBDAV_URL=http://nas.lan:5005/byxcr
export BYXCR_WEBDAV_USERNAME=byxcr
export BYXCR_WEBDAV_PASSWORD=secret
export BYXCR_SYNC_INTERVAL=1440
export BYXCR_MAX_CONCURRENCY=2
export BYXCR_API_TOKEN=change-me
export BYXCR_API_PORT=5088
export BYXCR_LOG_LEVEL=info
```

### 配置文件全貌（byxcr.yaml）

```yaml
storage:
  dataDir: ./data                  # 数据根目录（配置、数据库存放位置）
  databaseFile: ""                 # 数据库路径，留空用 {dataDir}/byxcr.db
  imageRoot: ""                    # 归档根目录，留空用./images
  tempDir: ""                      # 临时目录，留空用系统临时目录
  keepTar: false                   # 是否保留压缩前的中间 tar
  webdav:                          # WebDAV 归档
    enabled: false                 # 开启后不再写本地，全部上传远端
    url: https://dav.example.com/dav/byxcr
    username: ""
    password: ""
    timeoutSeconds: 1800           # 单次上传超时
    retries: 2                     # 上传失败重试次数
    createCollections: true        # 自动逐级创建远程目录
    keepLocalCopy: false           # 上传成功后是否保留本地副本
    allowInvalidCertificate: false # 允许无效 SSL 证书

download:                          # 镜像下载
  platforms: []                    # 限定架构，留空下载全部
  maxRetries: 3                    # 单个 HTTP 请求最大重试次数
  requestTimeoutSeconds: 300
  maxParallelDownloads: 4          # 并发下载的文件块数

compression:
  mode: auto                       # auto / external / managed
  level: 6                         # gzip 等级
  gzipPath: ""                     # 外部 gzip 程序路径
  threads: 0                       # 压缩线程数
  commandTimeoutSeconds: 3600

registry:
  useDefaultRegistryFirst: true    # 是否优先用官方 docker.io
  verifyBeforeUse: true            # 使用前先探测镜像源可用性
  probeTimeoutSeconds: 6
  items:                           # 自定义镜像源，按顺序重试
    - name: docker.1ms.run
    - name: mirror.ccs.tencentyun.com
  credentials:                     # 私有仓库认证
    "ghcr.io":
      username: user
      password: token

sync:
  enabled: true
  defaultIntervalMinutes: 1440     # 默认同步周期（分钟）
  scanIntervalSeconds: 60          # 后台扫描间隔
  maxConcurrency: 2                # add / 手动同步的并发数
  scheduleConcurrency: 4           # 定时任务并发数
  checkStrategy: digest            # digest 按摘要校验 / always 强制重下
  ignoreArchiveCheck: false        # 是否忽略归档文件存在性校验
  retryTimes: 1                    # 同步失败重试次数
  retryDelaySeconds: 10
  syncOnStartup: false             # 启动时是否全量同步一次

api:
  enabled: true
  host: ""                         # 监听地址，留空监听所有地址
  port: 5088
  token: ""                        # 写操作令牌，留空则写接口统一提示需要配置

log:
  level: info                      # debug / info / warn / error / none
  file: ""                         # 日志文件路径
  color: true                      # 终端日志是否上色
  timeZone: ""                     # 留空读系统时区
  directory: ""                    # 留空用系统默认位置
```

有一点要留意：**镜像任务列表只存在 SQLite 里**，配置文件和环境变量都改不了，只能用命令行或 WebAPI 管理。好处是备份 `data` 目录就等于备份了全部任务。

## 命令行

### 命令

```text
run                        启动守护进程（定时同步 + WebAPI + Web 控制台）
sync [镜像...]             立即同步，不给镜像则同步所有启用项
                           -l 本次忽略 WebDAV 只归档到本地，-o 指定本次归档位置
list [关键字]              列出任务，按添加时间倒序，默认 20 条
add <镜像>                 校验可拉取后入库并立刻后台下载
                           -i 指定同步周期，-o 指定归档位置，--no-check 跳过校验
remove <镜像>              移除任务
enable / disable <镜像>    启用 / 停用任务
interval <镜像> <时长>     改同步周期（别名 iv）
records [镜像]             查看同步记录，--clear 顺带清除 3 天前的记录
registries                 探测各镜像源的可用性与延迟
doctor                     输出环境自检报告
config                     打印当前生效的完整配置
init                       在数据目录生成默认配置和环境变量示例
version / help             版本号 / 帮助
```

### 选项

```text
-c, --config <路径>    指定配置文件（默认 ./data/byxcr.yaml）
-j, --json             以 JSON 输出结果，方便脚本处理
-v, --verbose          输出调试日志
-f, --force            忽略摘要比对，强制重新下载
-l, --local            本次同步忽略 WebDAV
-i, --interval <时长>  add 时的同步周期
-o, --output <路径>    add 的归档位置 / sync 本次的归档位置
    --no-check         add 时跳过「能否拉取」校验
    --disable          add 时直接加为停用状态
    --limit <数量>     records / list 的返回条数
    --clear            清除 3 天前的同步记录
```

### 同步周期写法

支持 `30d`、`24h`、`1440m`、`86400s`，也可以组合成 `1d12h`；纯数字按秒算。库里统一按秒存，列表展示时再换算成易读格式。

### add 对已存在任务的处理

不会重复创建。`-i` 与库里不一致就更新周期，`-o` 就更新归档位置，两个都不带则保持原值不动。周期改短后如果任务已到期，会顺带补一次下载。

## 同步流程

```text
定时扫描到期任务 → 比对上游摘要（没变就跳过）→ 依次尝试可用镜像源 → 拉取清单 / 配置 / 图层
→ 合并多架构并去重 → 组装 OCI 布局 tar → gzip 压缩 → 写入本地或 WebDAV → 结果落库
```

## WebAPI 与 Web 控制台

守护进程启动后默认监听 5088，根路径是只读的可视化控制台，同时提供一套 RESTful API 供脚本和第三方工具调用。

公开接口，不需要令牌：

```text
GET      /               Web 控制台，查看任务、状态、同步记录
GET      /api            接口清单与权限说明
GET/POST /api/list       任务列表，支持筛选分页
GET/POST /api/health     健康检查
```

写接口需要令牌。在配置里设好 `api.token`，请求带上 `Authorization: Bearer <令牌>`；没配令牌时这些接口统一返回提示，不会放行：

```text
/api/add      新增任务          /api/enable    启用任务
/api/sync     触发同步          /api/disable   停用任务
/api/remove   删除任务          /api/records   查询、清理同步记录
```

所有接口同时接受 GET 和 POST，参数放查询串或 JSON 请求体都行。

## WebDAV 归档

开启后镜像不再写本地，全部走 HTTP PUT 上传到远端，123 云盘、Nextcloud、群晖、Apache DAV 这类主流服务都能用。

- 远程目录自动逐级创建，已存在时不报错
- 上传前先 HEAD 比对远端文件，没变化就跳过
- 上传失败只重试上传，不切换镜像源、不重新下载镜像
- 可以保留本地副本，也可以忽略 SSL 证书校验，适配内网自签证书

## 常见问题

**Q：所有镜像源都探测失败，还能同步吗？**

能。探测只是预检，实际同步时仍会按配置顺序逐个尝试。先用 `byxcr registries` 看看各源的状态和延迟。

**Q：内容明明压缩了，为什么文件名还是 `.tar`？**

压缩照做（`compression` 段可调模式和等级），只是文件名统一成 `.tar`，让脚本、网盘和各类工具按同一套规则识别。容器运行时按文件内容自动判断压缩格式，导入时不用改名也不用手工解压。

**Q：导入后只有一个平台，多架构去哪了？**

分两种情况。归档本身是完整的：根索引 `index.json` 只登记一条 manifest（容器工具要求归档里恰好一个镜像），多平台清单折叠进它指向的内层索引，导入时运行时按机器架构自己挑。

丢平台的是传统 Docker。docker-archive 的一个条目只能描述一个镜像（`docker save` 本身也不支持多架构），所以兼容清单 `manifest.json` 只能登记一个平台——优先宿主机架构，其次 `linux/amd64`。想保留全部平台，用 `podman load`，或者开 containerd 镜像存储的新版 Docker，它们走 OCI 布局分支导入。

只想要部分架构的话，用 `download.platforms` 或 `BYXCR_PLATFORMS=linux/amd64,linux/arm64` 直接限定，也不必下完再丢。

**Q：WebDAV 上传失败会重新下载镜像吗？**

不会。只重试上传，不换源、不重下，不浪费流量。
