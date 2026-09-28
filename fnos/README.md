# byxcr · 飞牛 fnOS 应用包

把 byxcr NativeAOT 编译出的 Linux 二进制打包成飞牛 fnOS 可安装的 `.fpk`，由 fnOS 的生命周期脚本以专用应用用户拉起，**不依赖 Docker**。

## 目录结构

```text
fnos/
├── manifest               # 应用元数据（名称、版本、版本下限、端口、架构）
├── ICON.PNG               # 64×64 应用图标
├── ICON_256.PNG           # 256×256 应用图标
├── build.sh               # Linux 上的构建脚本（编译 + 打包）
├── tools/
│   ├── make-icons.py      # 图标生成脚本（改样式后重新生成 4 个尺寸）
│   ├── test-env.sh        # 配置落盘逻辑的回归测试（Linux / Git Bash 均可跑）
│   ├── copy-probe.c       # 拷贝路径探针：逐条试 FICLONE / copy_file_range / sendfile / read-write
│   ├── no-fastcopy.c      # LD_PRELOAD 垫片：让 .NET 跳过出问题的内核加速拷贝路径
│   └── fake-dotnet        # 「假冒 dotnet」：在 Windows 上验证 build.sh 的完整打包链路
├── config/
│   ├── privilege          # 以专用应用用户 byxcr 运行
│   └── resource           # 声明共享目录 byxcr/images 与 /usr/local/bin/byxcr
├── cmd/                   # 生命周期脚本
│   ├── install_init       # 安装前：环境提示（gzip / 架构）
│   ├── install_callback   # 安装后：建目录、补执行位、生成配置
│   ├── main               # 运行控制：start / stop / status
│   ├── upgrade_init       # 升级前：记录运行状态并停服务
│   ├── upgrade_callback   # 升级后：刷新配置并按原状态恢复
│   ├── uninstall_init     # 卸载前：停服务
│   ├── uninstall_callback # 卸载后：清理进程残留（不动用户数据）
│   ├── config_init        # 配置变更前：记录运行状态并停服务
│   └── config_callback    # 配置变更后：写新配置并按原状态重启
├── wizard/
│   ├── install            # 安装向导：镜像源、架构、周期、令牌、WebDAV
│   └── config             # 应用设置：同一组参数，留空即保持原值
└── app/                   # 安装后会整体释放到 {TRIM_APPDEST}
    ├── bin/
    │   ├── byxcr          # 命令行包装脚本（被软链到 /usr/local/bin/byxcr）
    │   └── byxcr.bin      # NativeAOT 主程序（构建时注入，不入库）
    ├── lib/byxcr-env.sh   # 所有生命周期脚本共用的路径解析与配置落盘逻辑
    └── ui/
        ├── config         # 桌面入口（iframe 打开 5088 端口的内置控制台）
        └── images/        # 入口图标 icon_64 / icon_256
```

## 构建

> **必须在 Linux 上构建。** .NET 的 NativeAOT 不支持跨操作系统编译，在 Windows 上执行
> `dotnet publish -r linux-x64` 会直接失败：
> `error : Cross-OS native compilation is not supported.`

前置依赖：

```bash
# .NET 10 SDK（https://dotnet.microsoft.com/download）
sudo apt-get install -y clang zlib1g-dev     # NativeAOT 链接阶段必需

# 飞牛官方打包工具
curl -fsSL -o fnpack https://static2.fnnas.com/fnpack/fnpack-1.2.3-linux-amd64   # arm64 用 -linux-arm64
sudo install -m 0755 fnpack /usr/local/bin/fnpack
```

先体检一遍环境（缺什么会直接指出来，不会编到一半才失败）：

```bash
chmod +x fnos/build.sh
./fnos/build.sh --doctor
```

体检会打印 CPU / 内存、仓库与构建根的**文件系统类型、可用空间、单文件写入延迟**——
构建变慢时这几项基本就能定位原因。

构建：

```bash
./fnos/build.sh            # 只出 x86 包
./fnos/build.sh arm        # 只出 arm 包（建议在 arm64 机器上原生构建）
./fnos/build.sh all        # 出双架构包
./fnos/build.sh --clean    # 清掉编译缓存（换过 SDK / 依赖后建议先清）
```

产物：

```text
fnos/dist/byxcr-1.0.0-x86.fpk
fnos/dist/byxcr-1.0.0-arm.fpk
```

可用环境变量：

| 变量 | 默认值 | 说明 |
| --- | --- | --- |
| `BUILD_ROOT` | `${TMPDIR:-/tmp}/byxcr-build` | 编译中间产物与日志目录，见下一节 |
| `OUT_DIR` | `fnos/dist` | `.fpk` 输出目录 |
| `VERSION` | 读 `byxcr.csproj` | 覆盖 `manifest` 里的版本号 |
| `FNPACK` | `fnpack` | fnpack 可执行文件路径 |
| `VERBOSITY` | `m` | MSBuild 控制台详细度，排查问题时设 `n` 或 `d` |
| `NO_FASTCOPY` | `auto` | `auto` 按探测结果决定；`1` 强制屏蔽内核加速拷贝；`0` 强制用原生路径 |
| `PROBE_TIMEOUT` | `12` | 单条拷贝路径的探测超时（秒） |
| `PROBE_BUDGET` | `60` | 拷贝探测的总时间预算（秒） |
| `SKIP_TOOLCHECK` | 空 | 设为 `1` 跳过 clang / zlib / 编译器检查 |

脚本会顺带做几件容易踩坑的事：

- 前置检查 `dotnet` / `clang` / `zlib` / `fnpack`，缺了立刻退出并给出安装命令；
- 把主程序改名为 `byxcr.bin`（`app/bin/byxcr` 是命令行包装脚本，不能互相覆盖）；
- 校验产物是 ELF，**并核对 `e_machine` 是否匹配目标架构**（`3e00` = x86-64，`b700` = arm64），
  选错 RID 时立刻失败，而不是等装到设备上才发现；
- 按目标架构改写 `manifest` 里的 `platform`（`x86` / `arm`）与 `version`；
- 把权限位统一成 0755 / 0644（打包工具在不同平台上对权限位的处理并不一致）；
- 构建前**探测内核拷贝路径**（见《构建为什么可能「看起来卡住」》），一旦发现某条会卡死就自动
  编译并挂上 `LD_PRELOAD` 垫片，把那条路径屏蔽掉；
- 垫片挂载前先校验它是本机可加载的 ELF 共享库 —— 它会被注入之后启动的每一个子进程，
  一个坏 `.so` 足以让整条构建链全挂；
- 编译时终端**实时刷新「当前执行到哪个 MSBuild 目标 + 已耗时」**，完整日志同时写入
  `$BUILD_ROOT/logs/publish-<x86|arm>.log`；编译结束后会把日志里最慢的目标打出来（时间花在哪一目了然）；
- 每个阶段打印耗时；空间 / 内存 / 写入延迟也一并报出来。

已经编译过、只想重新打包时：

```bash
./fnos/build.sh x86 --bin-dir /path/to/publish-output
```

### 在 Windows 上验证完整打包链路

NativeAOT 不能在 Windows 上编出 Linux 二进制，但**打包链路**可以完整验证：让
`fnos/tools/fake-dotnet` 冒充 `dotnet`（它只把收到的参数回显出来，并按约定产出一个合法的
x86-64 ELF 加一份带有性能汇总的日志），就能在本机跑通「参数拼装 → 架构校验 → fnpack 打包」：

```bash
mkdir -p /tmp/shim && cp fnos/tools/fake-dotnet /tmp/shim/dotnet
PATH="/tmp/shim:$PATH" SKIP_TOOLCHECK=1 \
    ./fnos/build.sh x86 --out /tmp/out --build-root /tmp/br
```

改完 `build.sh` 用它回归一遍，能立刻发现参数拼错、产物路径写错、日志被清理之类的低级问题。
注意：产出的 `.fpk` 里是假二进制，**只能验证流程，不能发布**。

## 构建为什么可能「看起来卡住」

典型现象（终端停在某一行，几十分钟不动、CPU 却几乎空闲）：

```text
Restore complete (3.5s)
byxcr net10.0 linux-x64                      _CopyFilesMarkedCopyLocal (1982.9s)
```

此时如果这台机器上还有别的 SSH 会话，它们可能正在刷内核告警 —— 那是**最关键的证据**，
下一节会用到。另外，这种卡住的进程**连 `kill -9` 都杀不掉**。

**先确认进度是否真的停了。** 交互式终端下最后一行就是当前正在执行的目标和已耗时；
另开一个终端看日志更直观：

```bash
tail -f /tmp/byxcr-build/logs/publish-x86.log   # normal 详细度，能看到目标级进展与逐个文件的拷贝
pgrep -af dotnet                                # 进程是否还活着、CPU 是否在跑
```

如果日志也不再增长，先看**内核日志**——这一类卡死几乎都会在上面留下痕迹：

```bash
dmesg | tail -20
```

看到下面这行，原因就基本确定了（`Parallel Copy Task` 正是 MSBuild 的 Copy 任务线程）：

```text
watchdog: BUG: soft lockup - CPU#1 stuck for 52s! [Parallel Copy T:2313763]
```

### 根因：内核级「加速拷贝」在 overlayfs 上卡死

.NET 的 `File.Copy` 在 Linux 上按固定顺序尝试四条路径（源码见 dotnet/runtime 的
`src/native/libs/System.Native/pal_io.c`，函数 `SystemNative_CopyFile`）：

| 顺序 | 方式 | 说明 |
| --- | --- | --- |
| 1 | `ioctl(FICLONE)` | 写时复制克隆 |
| 2 | `copy_file_range()` | 内核态拷贝 |
| 3 | `sendfile()` | 零拷贝 |
| 4 | `read()` / `write()` | 普通用户态循环（兜底） |

前三条都依赖文件系统实现。在 **overlayfs（Docker / 容器工作区）**、网络盘等环境上，
它们存在已公开的缺陷：内核可能陷入死循环或相互等待，把某个 CPU 核占住且无法退出。
而 .NET 里**没有任何环境变量或 AppContext 开关**能关掉它们 —— 一旦卡住，就永远退不到第 4 条兜底路径。

这也解释了另一个反直觉现象：**能不能卡死取决于文件本身**。同一目录里有的文件会卡、有的不会；
把文件 `cp` 到别处再拷回来、甚至只是 append 一行，它就再也不卡了 —— 因为文件从镜像的只读层
（overlay lower layer）被抬到了可写层。

常见的次要原因（这些和上面的现象长得像，但本质不同）：

| 次要原因 | 判断方法 | 处理 |
| --- | --- | --- |
| 可用空间不足 | `df -h` 看仓库、`BUILD_ROOT`、`~/.nuget/packages` | 需要 2~3 GiB；空间不足时 `Copy` 会**每个文件重试 10 次、每次间隔 1 秒**（这是明确报错，不是无输出卡死） |
| 可用内存不足 | `free -m` | NativeAOT 的 IL 编译很吃内存，可用内存低于约 1.5 GiB 会变得极慢甚至被 OOM 杀掉 |
| 仓库在慢挂载上（NFS / SMB / 9p / bind mount） | `--doctor` 里仓库目录的写入延迟远大于构建根 | 脚本已用 `--artifacts-path` 把 `obj/`、`bin/`、打包暂存目录挪到 `BUILD_ROOT` |

> **一个反直觉的点：单文件写入延迟正常，不等于拷贝路径正常。**
> 小文件写入测的是「新建文件」（写 overlay 上层），而卡死发生在「从只读层拷贝已有文件」，
> 两者是不同路径。本项目实测仓库 8 ms、构建根 20 ms 完全正常，构建却照样卡了 33 分钟。

### 怎么确认是哪一条路径卡住

`--doctor` 会编译并运行 `fnos/tools/copy-probe.c`，把上面四条路径**逐条单独**试一遍
（每条限时，默认 12 秒），十几秒内就能问清楚「会不会卡」：

```bash
./fnos/build.sh --doctor
```

```text
==> 拷贝路径探测
      样本  ~/.nuget/packages/.../runtimes/linux-x64/native/libcoreclr.so
            6 MiB，overlay
            FICLONE(ioctl)    ok    14 ms
            copy_file_range   超时（>12s，进程状态 S）
                   ^ 内核卡死的就是这条路径（soft lockup 的成因）
            sendfile          ok    36 ms
            read/write（兜底） ok    58 ms
      结论  cfr 在本机不可用：MSBuild 的 Copy 任务会卡死在内核里，
            表现为长时间无输出 + dmesg 里的 soft lockup，且 kill -9 也杀不掉。
```

> 探针还专门防了一件事：**真卡住的进程连 `kill -9` 都杀不掉**（信号送不进内核态），
> 所以它一律后台运行 + 限时，超时就放弃等待，绝不会把构建脚本一起拖住。
>
> 注意只有**超时**才算「卡死」。系统调用返回错误（例如 `EOPNOTSUPP`）是正常的：
> .NET 会自行回退到普通 `read`/`write`，不影响正确性。

### 处理：屏蔽加速拷贝

默认（`NO_FASTCOPY=auto`）脚本会按探测结果**自动**决定：只要发现某条路径超时，就现场编译
`fnos/tools/no-fastcopy.c` 并用 `LD_PRELOAD` 注入，让 .NET 跳过出问题的路径、全部走普通 `read`/`write`。
也可以手工强制：

```bash
./fnos/build.sh --no-fastcopy all    # 强制屏蔽加速拷贝
NO_FASTCOPY=1 ./fnos/build.sh all    # 等价写法
./fnos/build.sh --fastcopy all       # 反过来：强制走原生加速路径
```

垫片是**按方法选择性屏蔽**的（`BYXCR_NOFASTCOPY=cfr,sendfile` 这样），并且严格对齐 .NET 的回退条件：

- `copy_file_range` 返回 `EOPNOTSUPP` → .NET 收到非正值后**跳过 sendfile**，直接落到 `read`/`write`。
  这里**不能**用 `ENOSYS`：那会让 .NET 判定「不支持」，转而尝试同样有问题的 `sendfile`。
- `sendfile` 返回 `EINVAL` → .NET 只在 `EINVAL` / `ENOSYS` 时才回退，其它 errno 会让拷贝**直接失败**。

挂载前脚本会校验它确实是本机可加载的 ELF 共享库（魔数 + `e_type=ET_DYN` + `e_machine` 与宿主一致）：
垫片要注入之后启动的**每一个**子进程，万一它不是有效的共享库，会把每个子进程都搞挂。

代价：拷贝改走用户态循环，速度略降 —— 这是完全安全的路径，只是慢一点。

### 已经被卡住的进程怎么收

卡在内核的进程**杀不掉**（`kill -9` 也无效），只能重启容器 / 工作区释放：

```bash
ps -o pid,stat,wchan:24,cmd -C dotnet   # stat=D 或 wchan 停在内核函数 = 卡在内核
```

### 备选方案

如果因为某些原因用不了垫片（例如机器上没有 C 编译器），还有一条思路：**让拷贝不跨文件系统**。
把 NuGet 缓存指到与 `BUILD_ROOT` 同一文件系统后，MSBuild 有机会改用硬链接，连数据都不用拷：

```bash
NUGET_PACKAGES=/root/.cache/byxcr-build/nuget ./fnos/build.sh x86
```

代价是首次需要重新还原一遍依赖。

### 顺带：中间产物为什么不放在仓库里

`_CopyFilesMarkedCopyLocal` 本身只是把 `@(ReferenceCopyLocalPaths)` 拷到 `$(OutDir)`，
在本地盘上**几十毫秒**就过（本项目实测 27 ms，只 7 个文件）—— 所以它耗时异常一定不是「文件多」。

因此 `fnos/build.sh` 用 `--artifacts-path` 把 `obj/` 与 `bin/` 放到 `BUILD_ROOT`，
打包暂存目录也放在那里，**只有最终的 `.fpk` 一个文件写回仓库**。
如果 `BUILD_ROOT` 本身也很慢，换到本地盘即可：

```bash
BUILD_ROOT=/root/.cache/byxcr-build ./fnos/build.sh x86
```

### 关于日志与进度

脚本**不会**把 `dotnet` 的输出用 `| tee` 接走：一旦 stdout 变成管道，MSBuild 的终端日志器
会自动关闭，整段编译没有任何输出，看起来就像卡死（而且不知道卡在哪一步）。
现在的做法是：

- 交互式终端 → 保留终端日志器（实时进度），详细日志交给 MSBuild 文件日志器 `-fl` 单独落盘；
- 输出被重定向（CI / `nohup`）→ 自动改用普通控制台日志器 + `-v: n`，保证输出里能看到目标级进展。

日志位于 `$BUILD_ROOT/logs/`（单独一个目录，不会被收尾清理删掉），里面含 MSBuild 的性能汇总。

## 安装与验证

```bash
# 方式一：应用中心 → 手动安装 → 选择 .fpk
# 方式二：设备上命令行安装
appcenter-cli install-fpk byxcr-1.0.0-x86.fpk
```

安装后依次确认：

1. 桌面出现「镜像搬运箱」图标，点击能打开内置 Web 控制台（默认只读）；
2. 应用中心里能启动 / 停止，状态显示正确；
3. 终端里 `byxcr version`、`byxcr doctor` 可用；
4. `byxcr add hello-world:latest --interval 24h` 后，归档出现在共享目录；
5. 服务日志写入 `{数据目录}/logs/service.log`。

## 运行时目录

应用以专用用户 `byxcr` 运行，目录一律通过 `TRIM_*` 变量推导，不硬编码存储空间编号：

| 用途 | `TRIM_*` 变量 | 稳定访问路径 | 实际位置 |
| --- | --- | --- | --- |
| 数据（SQLite、日志、临时） | `TRIM_PKGVAR` | `/var/apps/byxcr/var` | `/vol{n}/@appdata/byxcr` |
| 配置（`byxcr.yaml` / `byxcr.env`） | `TRIM_PKGETC` | `/var/apps/byxcr/etc` | `/vol{n}/@appconf/byxcr` |
| 镜像归档 | `TRIM_DATA_SHARE_PATHS` | `/var/apps/byxcr/share/images` | `/vol{n}/byxcr/images` |
| 运行文件（二进制） | `TRIM_APPDEST` | `/var/apps/byxcr/target` | `/vol{n}/@appcenter/byxcr` |

镜像归档放在**共享目录**里，因此用户可以在 fnOS 文件管理器中直接浏览、下载、取用归档出来的 `*.tar`。

## 配置优先级

byxcr 自身的优先级是「环境变量 > 配置文件 > 内置默认值」，本应用沿用这套语义：

1. **应用设置（向导）** → 写入 `/var/apps/byxcr/etc/byxcr.env`，启动时导入，优先级最高。
   对应「应用中心 → 镜像搬运箱 → 应用设置」里的各项，留空即保持原值。
2. **配置文件** → `/var/apps/byxcr/etc/byxcr.yaml`，安装时用官方模板生成一次、之后不再覆盖，
   适合写私有仓库凭据、限速、WebDAV 高级项等向导里没有的配置。
3. 目录路径不属于用户配置：`cmd/main` 每次启动都按当前 `TRIM_*` 变量重新注入，
   所以把应用换到别的存储空间后不需要重装。

想交还某一项给配置文件控制，删掉 `byxcr.env` 里对应的那一行即可。

## 常见问题

**构建长时间没有任何输出，像是卡住了**

先跑 `./fnos/build.sh --doctor`：它会探测内核拷贝路径，十几秒内就能告诉你是不是
「内核级加速拷贝卡死」，并自动给出处理方式。若不行，再按上一节逐项排查：

```bash
tail -f /tmp/byxcr-build/logs/publish-x86.log   # 日志是否还在增长
dmesg | tail -20                                # 有没有 soft lockup
df -h ; free -m                                 # 空间 / 内存
```

**构建停在 `_CopyFilesMarkedCopyLocal` 几十分钟**

这个目标慢**一定不是「文件多」**（本地盘上实测 27 ms，只 7 个文件）。按成本从低到高：

```bash
dmesg | tail -20                      # 1. 有 soft lockup → ./fnos/build.sh --no-fastcopy
./fnos/build.sh --doctor              # 2. 看探测结论与写入延迟
df -h                                 # 3. 空间不足会让 Copy 逐文件重试
```

**构建报 `error : Cross-OS native compilation is not supported`**

在 Windows 上构建了。NativeAOT 不支持跨操作系统编译，请把仓库放到 Linux 上执行 `fnos/build.sh`。

**构建报 `缺少 clang` 或 zlib 相关错误**

NativeAOT 的链接阶段必须用 clang，且需要 zlib 头文件：

```bash
sudo apt-get update && sudo apt-get install -y clang zlib1g-dev
```

**只出 arm 包时提示可能失败 / 链接阶段报错**

在 x86 机器上编 arm64 属于跨架构编译，需要 aarch64 交叉工具链与 arm64 版 zlib。
脚本会在开始前提示这一点；建议把 arm 包放到 arm64 机器上构建。

**安装时提示「应用包内缺少 Linux 可执行文件」**

这个 `.fpk` 没有注入二进制——通常是直接用了仓库里的 `fnos/` 目录打包，或在 Windows 上打包。
请在 Linux 上执行 `fnos/build.sh` 重新构建。

**桌面图标打得开但页面空白 / 启动后立刻退出**

先看 `{数据目录}/logs/service.log`。常见原因是 5088 端口被占用（`byxcr doctor` 会提示），
或 WebDAV 开了却没填地址（向导已做兜底：会自动关闭并写回配置）。

**装到设备上提示架构不匹配**

`.fpk` 是分架构的：`platform=x86` 的包装不到 ARM 设备上。请改用对应架构的包，
或在 arm64 机器上执行 `fnos/build.sh arm`。

**只想要一个包，不分架构**

可以把 `manifest` 里的 `platform` 改成 `all` 并在 `app/bin/` 下同时放两份目录，
再由 `cmd/main` 按 `uname -m` 选择——但这与官方「`all` 仅用于不含特定架构二进制的包」的约定不符，
上架审核可能不通过，故默认按架构分别出包。

**上架前要补什么**

`manifest` 里的 `maintainer` / `maintainer_url` / `distributor` 目前填的是本项目主页，
正式上架请改成你自己的开发者信息，并核对 `os_min_version` 是否与实际测试过的系统版本一致。
