#!/usr/bin/env bash
# ============================================================================
# 构建 byxcr 的飞牛 fnOS 应用包（.fpk）
#
# 必须在 Linux 上执行。.NET 的 NativeAOT 不支持跨操作系统编译：
# 在 Windows 上跑 dotnet publish -r linux-x64 会直接失败并报
#   error : Cross-OS native compilation is not supported.
#
# 依赖：
#   * .NET 10 SDK
#   * clang 与 zlib 开发包（NativeAOT 链接阶段必需，缺失会直接报错退出）
#       Debian/Ubuntu: sudo apt-get install -y clang zlib1g-dev
#       RHEL/CentOS  : sudo dnf install -y clang zlib-devel
#       Alpine       : sudo apk add clang zlib-dev build-base
#   * fnpack（飞牛官方打包工具，默认从 PATH 查找，可用 FNPACK 指定路径）
#   * cc 或 gcc（仅用于编译下面提到的探针与垫片；本地构建自带）
#
# 用法：
#   fnos/build.sh                 # 只出 x86 包（fnOS 设备绝大多数是 x86_64）
#   fnos/build.sh arm             # 只出 arm 包（建议在 arm64 机器上原生构建）
#   fnos/build.sh all             # 出双架构包
#   fnos/build.sh --doctor        # 只做环境体检（含「拷贝会不会卡死」探测），不构建
#   fnos/build.sh --clean         # 清掉构建缓存后退出
#   fnos/build.sh --no-fastcopy   # 强制屏蔽内核加速拷贝（构建卡死时用）
#   fnos/build.sh --fastcopy      # 强制使用内核加速拷贝（默认由探测结果决定）
#   fnos/build.sh x86 --bin-dir /path/to/publish   # 复用已有编译产物，跳过 dotnet publish
#
# 环境变量：
#   FNPACK=...         fnpack 可执行文件路径
#   VERSION=...        覆盖 manifest 中的版本号
#   OUT_DIR=...        输出目录，默认 fnos/dist
#   BUILD_ROOT=...     编译中间产物目录，默认 ${TMPDIR:-/tmp}/byxcr-build
#   VERBOSITY=...      MSBuild 控制台详细度，默认 m；排查问题时用 n 或 d
#   NO_FASTCOPY=...    auto（默认，按探测结果决定）| 1 强制屏蔽 | 0 强制禁用
#   PROBE_TIMEOUT=...  单条拷贝路径的探测超时秒数，默认 12
#   PROBE_BUDGET=...   拷贝探测的总时间预算秒数，默认 60
#   SKIP_TOOLCHECK=1   跳过 clang / zlib / 编译器检查（仅在清楚后果时使用）
#
# ---------------------------------------------------------------------------
# 构建卡死（长时间无任何输出、CPU 却空闲）是怎么回事
#
#   典型症状是 MSBuild 停在
#       myapp net10.0 linux-x64            _CopyFilesMarkedCopyLocal (1982.9s)
#   同时别的 SSH 终端刷出内核日志：
#       watchdog: BUG: soft lockup - CPU#1 stuck for 52s! [Parallel Copy T:...]
#
#   `Parallel Copy Task` 就是 MSBuild 的 Copy 任务线程，而 soft lockup 表示它
#   卡在**内核里**出不来 —— 不是死锁、不是磁盘慢、也不是空间不足。
#
#   根因在 .NET 的 File.Copy：Linux 上它按固定顺序尝试四条路径
#   （dotnet/runtime, src/native/libs/System.Native/pal_io.c，
#     函数 SystemNative_CopyFile）：
#       1. ioctl(FICLONE)     写时复制克隆
#       2. copy_file_range()  内核态拷贝
#       3. sendfile()         零拷贝
#       4. read()/write()     普通循环（兜底）
#   前三条都依赖文件系统实现，在 overlayfs（Docker / Coder 这类容器）、网络盘
#   等环境下存在已知缺陷：内核可能陷入死循环。而 .NET 里**没有任何环境变量或
#   AppContext 开关**能关掉它们 —— 一旦卡住就永远退不到第 4 条。
#
#   本脚本的应对：
#     * `--doctor` 会编译并运行 fnos/tools/copy-probe.c，把四条路径逐条单独
#       试一遍（每条限时 PROBE_TIMEOUT 秒），十几秒内就能确认是哪一条卡住；
#     * 默认（NO_FASTCOPY=auto）按探测结果自动决定是否加载
#       fnos/tools/no-fastcopy.c 编译出的垫片，用 LD_PRELOAD 让 .NET 跳过
#       出问题的那几条路径，全部走普通 read/write；
#     * 真卡住时进程连 kill -9 都杀不掉（信号送不进内核态），所以探针一律
#       后台运行 + 限时，超时后不再等待，绝不把脚本一起拖住。
#
#   为什么 obj/ 与 bin/ 不放在仓库里：
#   --artifacts-path 把 obj/、bin/ 与打包暂存目录都放到 BUILD_ROOT，
#   只有最终的 .fpk 一个文件写回仓库，避免在慢挂载上逐文件写。
#
#   为什么不用 `dotnet publish ... | tee 日志`：
#   一旦 stdout 变成管道，MSBuild 的终端日志器会自动关闭，整段编译没有任何输出，
#   看起来就像卡死。现在交互式终端下保留终端日志器（实时刷新「当前目标 + 已耗时」），
#   详细日志另由 MSBuild 文件日志器（-fl）落盘。输出被重定向时自动降级为普通日志器。
#
# 怀疑卡住时，另开一个终端：
#   tail -30 <BUILD_ROOT>/logs/publish-<x86|arm>.log   # 最后执行到的目标、正在拷哪个文件
#   pgrep -af dotnet                                   # 进程是否还活着
#   ps -o pid,stat,wchan:24,cmd -C dotnet              # stat=D 或 wchan 停在内核函数 = 卡在内核
#   dmesg | tail -20                                   # 是否有 soft lockup
#   注意：卡在内核的进程可能 kill -9 也杀不掉，需要重启容器 / 工作区才能释放。
# ============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PKG="$ROOT/fnos"
PROJECT="$ROOT/byxcr.csproj"
OUT_DIR="${OUT_DIR:-$PKG/dist}"
BUILD_ROOT="${BUILD_ROOT:-${TMPDIR:-/tmp}/byxcr-build}"
VERBOSITY="${VERBOSITY:-m}"
NO_FASTCOPY="${NO_FASTCOPY:-auto}"
PROBE_TIMEOUT="${PROBE_TIMEOUT:-12}"
PROBE_BUDGET="${PROBE_BUDGET:-60}"

TARGETS=()
BIN_DIR=""
DOCTOR=0
CLEAN=0

# 拷贝路径探测与垫片相关的全局状态
PROBE_RAN=0
PROBE_OK=0
RW_HUNG=0
BROKEN_METHODS=""
COPY_MODE="native"
COPY_SHIELD_LIST=""
SHIM_PATH=""

# ------------------------------------------------------------------ 参数解析

while [ $# -gt 0 ]; do
    case "$1" in
        --bin-dir)
            BIN_DIR="${2:-}"
            [ -n "$BIN_DIR" ] || { echo "错误：--bin-dir 缺少参数" >&2; exit 2; }
            shift 2
            ;;
        --bin-dir=*)
            BIN_DIR="${1#*=}"
            shift
            ;;
        --version)
            VERSION="${2:-}"
            shift 2
            ;;
        --version=*)
            VERSION="${1#*=}"
            shift
            ;;
        --out)
            OUT_DIR="${2:-}"
            shift 2
            ;;
        --out=*)
            OUT_DIR="${1#*=}"
            shift
            ;;
        --build-root)
            BUILD_ROOT="${2:-}"
            shift 2
            ;;
        --build-root=*)
            BUILD_ROOT="${1#*=}"
            shift
            ;;
        --no-fastcopy)
            NO_FASTCOPY=1
            shift
            ;;
        --fastcopy)
            NO_FASTCOPY=0
            shift
            ;;
        --doctor|--check)
            DOCTOR=1
            shift
            ;;
        --clean)
            CLEAN=1
            shift
            ;;
        -h|--help)
            # 打印 shebang 之后连续的注释行，去掉 "# " 前缀（表头长度变了也不用改）
            awk 'NR>1 && /^#/ { sub(/^# ?/, ""); print; next } NR>1 { exit }' "${BASH_SOURCE[0]}"
            exit 0
            ;;
        -*)
            echo "错误：未知选项 $1" >&2
            exit 2
            ;;
        *)
            TARGETS+=("$1")
            shift
            ;;
    esac
done

[ "${#TARGETS[@]}" -gt 0 ] || TARGETS=(x86)

# ------------------------------------------------------------------ 输出与测量

log() { printf '\n\033[1;34m==>\033[0m %s\n' "$*"; }
info() { printf '    %s\n' "$*"; }
err() { printf '\033[1;31m错误：\033[0m %s\n' "$*" >&2; }
warn() { printf '\033[1;33m警告：\033[0m %s\n' "$*" >&2; }
die() {
    err "$*"
    exit 1
}

now_ms() {
    local v
    v="$(date +%s%3N 2>/dev/null || true)"
    case "$v" in '' | *[!0-9]*) v="$(( $(date +%s) * 1000 ))" ;; esac
    printf '%s' "$v"
}

# 测一次「建文件 → fsync → 删除」的往返耗时（毫秒）。
# 返回 -1 表示目录不可写，-2 表示超过 limit 秒仍未完成。
# 注意：这只测「新建文件」，代表不了拷贝路径 —— 拷贝的坑在内核里（见文件头）。
probe_dir_ms() {
    local dir="$1" limit="${2:-10}" t0 t1 rc=0 script
    mkdir -p "$dir" 2>/dev/null || { printf -- '-1'; return 0; }
    script='
        p="$1/.byxcr-probe.$$"
        printf byxcr > "$p" || exit 1
        command -v sync >/dev/null 2>&1 && sync -f "$p" 2>/dev/null
        rm -f "$p"
    '
    t0="$(now_ms)"
    if command -v timeout >/dev/null 2>&1; then
        timeout "$limit" sh -c "$script" _ "$dir" 2>/dev/null || rc=$?
    else
        sh -c "$script" _ "$dir" 2>/dev/null || rc=$?
    fi
    t1="$(now_ms)"
    # 被 timeout 杀掉时里面的 rm 不会执行，这里兜底清掉探测文件
    rm -f "$dir"/.byxcr-probe.* 2>/dev/null || true
    if [ "$rc" -ne 0 ]; then
        printf -- '-2'
        return 0
    fi
    printf '%s' "$((t1 - t0))"
}

describe_ms() {
    case "$1" in
        -1) printf '不可写' ;;
        -2) printf '超时（>10 s，严重异常）' ;;
        *)
            if [ "$1" -ge 1000 ]; then
                printf '%s.%s s' "$(( $1 / 1000 ))" "$(( ( $1 % 1000 ) / 100 ))"
            else
                printf '%s ms' "$1"
            fi
            ;;
    esac
}

# 目录是否属于「慢挂载」
is_slow_ms() {
    [ "$1" = "-2" ] || { [ "$1" != "-1" ] && [ "$1" -gt 200 ]; }
}

# 目录所在文件系统类型（ext4 / overlay / nfs / cifs / fuse.virtiofs …）
fs_type_of() {
    stat -f -c %T "$1" 2>/dev/null || printf '未知'
}

# 目录是否落在「已知对内核加速拷贝不友好」的文件系统上
is_risky_fs() {
    case "$1" in
        overlay | overlayfs | fuse* | fuse.* | nfs* | cifs | smb* | 9p | virtiofs) return 0 ;;
        *) return 1 ;;
    esac
}

# 目录的挂载来源与类型（用于判断「源」和「目标」是不是同一文件系统）
mount_info_of() {
    local d="$1" line=""
    if command -v findmnt >/dev/null 2>&1; then
        line="$(findmnt -T "$d" -n -o SOURCE,FSTYPE 2>/dev/null | head -n1)"
    fi
    if [ -z "$line" ]; then
        line="$(fs_type_of "$d")"
    fi
    printf '%s' "$line"
}

# 目录所在分区的可用空间（MiB）；读不到返回非 0
avail_mib_of() {
    local kb
    kb="$(df -Pk "$1" 2>/dev/null | awk 'NR==2 {print $4}')"
    case "$kb" in '' | *[!0-9]*) return 1 ;; esac
    printf '%s' "$((kb / 1024))"
}

human_mib() {
    if [ "$1" -ge 1024 ]; then printf '%s GiB' "$(( $1 / 1024 ))"; else printf '%s MiB' "$1"; fi
}

# 可用内存（MiB）；读不到返回非 0
mem_avail_mib() {
    local v
    v="$(free -m 2>/dev/null | awk '/^Mem:/ {print $NF}')"
    case "$v" in '' | *[!0-9]*) return 1 ;; esac
    printf '%s' "$v"
}

nuget_cache_dir() {
    if [ -n "${NUGET_PACKAGES:-}" ]; then printf '%s' "$NUGET_PACKAGES"
    else printf '%s' "${HOME:-/root}/.nuget/packages"
    fi
}

# 空间不足会让 NativeAOT 的中间产物写失败（会明确报错，不是这次的无输出卡死）。
warn_low_disk() {
    local d m
    for d in "$OUT_DIR" "$BUILD_ROOT" "$(nuget_cache_dir)"; do
        [ -n "$d" ] || continue
        m="$(avail_mib_of "$d" 2>/dev/null)" || continue
        if [ "$m" -lt 3072 ]; then
            warn "「$d」可用空间仅 $(human_mib "$m")：NativeAOT 的中间产物与运行时包需要 2~3 GiB。"
        fi
    done
}

warn_low_memory() {
    local m
    m="$(mem_avail_mib)" || return 0
    if [ "$m" -lt 1500 ]; then
        warn "可用内存仅 ${m} MiB：NativeAOT 的 IL 编译阶段很吃内存，内存不足会变得极慢甚至被 OOM 杀掉。"
        warn "建议给它 2 GiB 以上可用内存后再重试。"
    fi
}

# ============================================================================
# 内核级拷贝路径探测：确认「卡死」到底卡在哪一条路径上
# ============================================================================

# 编译探针（极快，约零点几秒）。成功则打印可执行文件路径。
build_copy_probe() {
    local cc_bin out="$BUILD_ROOT/tools/copy-probe"
    cc_bin="$(command -v cc 2>/dev/null || command -v gcc 2>/dev/null || true)"
    [ -n "$cc_bin" ] || return 1
    [ -f "$PKG/tools/copy-probe.c" ] || return 1
    mkdir -p "$BUILD_ROOT/tools"
    "$cc_bin" -O2 -o "$out" "$PKG/tools/copy-probe.c" >/dev/null 2>&1 || return 1
    printf '%s' "$out"
}

# 编译垫片：让 .NET 跳过有缺陷的加速拷贝路径
build_nofastcopy_shim() {
    local cc_bin out="$BUILD_ROOT/tools/no-fastcopy.so"
    cc_bin="$(command -v cc 2>/dev/null || command -v gcc 2>/dev/null || true)"
    [ -n "$cc_bin" ] || return 1
    [ -f "$PKG/tools/no-fastcopy.c" ] || return 1
    mkdir -p "$BUILD_ROOT/tools"
    # 老 glibc 需要显式 -ldl；新的不需要，两种都试一遍
    if ! "$cc_bin" -shared -fPIC -O2 -o "$out" "$PKG/tools/no-fastcopy.c" -ldl >/dev/null 2>&1; then
        "$cc_bin" -shared -fPIC -O2 -o "$out" "$PKG/tools/no-fastcopy.c" >/dev/null 2>&1 || return 1
    fi
    [ -f "$out" ] || return 1
    printf '%s' "$out"
}

# 挑几个「构建时真的会被 MSBuild 拷走」的文件当探测样本。
# 优先取 NuGet 运行时包里的文件，其次取系统里同样来自镜像层的大文件。
probe_sources() {
    local pkgroot f d
    pkgroot="$(nuget_cache_dir)"

    # 1) 运行时包里的原生库（自包含发布时会被逐个拷进输出目录）
    for d in "$pkgroot"/microsoft.netcore.app.runtime.linux-x64/*/runtimes/linux-x64/native; do
        [ -d "$d" ] || continue
        f="$(find "$d" -maxdepth 1 -type f -name '*.so' -printf '%s %p\n' 2>/dev/null |
            sort -rn | head -n1 | cut -d' ' -f2-)" || true
        [ -n "$f" ] && { printf '%s\n' "$f"; break; }
    done

    # 2) 同一个包里的托管程序集（不同目录，在多层 overlay 下可能落在不同层）
    for d in "$pkgroot"/microsoft.netcore.app.runtime.linux-x64/*/runtimes/linux-x64/lib/net10.0; do
        [ -d "$d" ] || continue
        f="$(find "$d" -maxdepth 1 -type f -name '*.dll' -printf '%s %p\n' 2>/dev/null |
            sort -rn | head -n1 | cut -d' ' -f2-)" || true
        [ -n "$f" ] && { printf '%s\n' "$f"; break; }
    done

    # 3) 系统里的大文件（通常就是 overlay 的只读 lower 层，最容易触发缺陷）
    for d in clang dotnet; do
        f="$(readlink -f "$(command -v "$d" 2>/dev/null || true)" 2>/dev/null || true)"
        [ -n "$f" ] && [ -f "$f" ] || continue
        [ "$(stat -c %s "$f" 2>/dev/null || printf 0)" -gt 1048576 ] || continue
        printf '%s\n' "$f"
        break
    done

    return 0
}

# 轮询用的 sleep 粒度：GNU / busybox 的 sleep 都支持小数；
# 若某个环境不支持，就退化成 1 秒，超时预算同步按秒换算（否则会变成 1/10 预算）。
sleep_unit() {
    if [ -z "${SLEEP_ARG:-}" ]; then
        if sleep 0.1 2>/dev/null; then
            SLEEP_ARG="0.1"
            SLEEP_UNIT=10
        else
            SLEEP_ARG="1"
            SLEEP_UNIT=1
        fi
    fi
}

# 校验 .so 是「本机能加载的 ELF 共享库」：魔数 + e_type=ET_DYN + e_machine 与宿主一致。
# 垫片要 LD_PRELOAD 注入到之后启动的**每一个**子进程（dotnet、cp、fnpack …），
# 一旦它不是有效共享库，会把每个子进程都搞挂 —— 所以挂之前必须验一遍。
check_shared_object() {
    local so="$1" magic etype mach host_expect
    [ -f "$so" ] || return 1
    magic="$(head -c 4 "$so" 2>/dev/null | od -An -tx1 | tr -d ' \n')"
    [ "$magic" = "7f454c46" ] || return 1
    # ELF 头：偏移 16 = e_type（小端 2 字节），偏移 18 = e_machine
    etype="$(od -An -tx1 -j16 -N2 "$so" 2>/dev/null | tr -d ' \n')"
    [ "$etype" = "0300" ] || return 1 # ET_DYN = 3
    mach="$(od -An -tx1 -j18 -N2 "$so" 2>/dev/null | tr -d ' \n')"
    case "$(uname -m)" in
        x86_64 | amd64) host_expect="3e00" ;;
        aarch64 | arm64) host_expect="b700" ;;
        *) return 0 ;; # 其它架构不做比对，只要求确实是 ELF 共享库
    esac
    [ "$mach" = "$host_expect" ] || return 1
    return 0
}

# 后台运行探针并限时等待。
#   * 真卡住时进程连 kill -9 都杀不掉（信号送不进内核态），所以这里绝不 `wait` 到底；
#   * 退出码只区分「超时」和「跑完了」，具体结论看探针自己打印的 result=ok/fail，
#     不依赖 wait 返回的子进程退出码（各平台对已结束子进程的语义不一致）；
#   * 探针的输出落盘并转出到 stdout；万一什么都没打印，就把 stderr 首行带出来。
# 返回：0 = 跑完了（结论看 stdout）；124 = 超时（卡住）；1 = 探针没能正常执行
run_copy_method() {
    local probe="$1" method="$2" src="$3" dst="$4" limit="$5"
    local out_file="$BUILD_ROOT/tools/probe-$method.out"
    local err_file="$BUILD_ROOT/tools/probe-$method.err"
    local pid waited=0 budget_units timed_out=0 child_rc=0 st="" line

    sleep_unit
    budget_units=$((limit * SLEEP_UNIT))
    : >"$out_file"
    : >"$err_file"
    "$probe" "$method" "$src" "$dst" >"$out_file" 2>"$err_file" &
    pid=$!
    while kill -0 "$pid" 2>/dev/null; do
        [ "$waited" -ge "$budget_units" ] && break
        sleep "$SLEEP_ARG" 2>/dev/null || true
        waited=$((waited + 1))
    done

    if kill -0 "$pid" 2>/dev/null; then
        timed_out=1
        st="$(awk '{print $3}' "/proc/$pid/stat" 2>/dev/null || printf '?')"
        kill -9 "$pid" 2>/dev/null || true
    else
        wait "$pid" 2>/dev/null || child_rc=$?
    fi

    line="$(grep -m1 '^method=' "$out_file" 2>/dev/null || true)"
    if [ "$timed_out" = "1" ]; then
        printf 'TIMEOUT state=%s' "${st:--}"
        [ -n "$line" ] && printf ' %s' "$line"
        return 124
    fi
    if [ -n "$line" ]; then
        printf '%s' "$line"
        return 0
    fi
    # 没有任何输出：把 stderr 首行带出来，便于定位（路径、权限、架构不符等）
    printf 'NORESULT rc=%s stderr=%s' "$child_rc" "$(head -n1 "$err_file" 2>/dev/null || true)"
    return 1
}

# 逐条试拷贝路径，把结果与结论打进全局：PROBE_OK / BROKEN_METHODS
probe_copy_report() {
    BROKEN_METHODS=""
    PROBE_RAN=0
    PROBE_OK=0
    RW_HUNG=0

    local probe
    probe="$(build_copy_probe 2>/dev/null || true)"
    if [ -z "$probe" ]; then
        info "拷贝路径探测：跳过（找不到 cc/gcc，无法编译 fnos/tools/copy-probe.c）"
        info "  安装后即可获得这项检测：sudo apt-get install -y gcc"
        return 0
    fi

    mkdir -p "$BUILD_ROOT/tools"
    local dst="$BUILD_ROOT/tools/probe-dst.bin"
    local srcs synth=0
    srcs="$(probe_sources 2>/dev/null || true)"
    if [ -z "$srcs" ]; then
        if head -c 67108864 /dev/zero >"$BUILD_ROOT/tools/probe-src.bin" 2>/dev/null; then
            srcs="$BUILD_ROOT/tools/probe-src.bin"
            synth=1
        else
            info "拷贝路径探测：跳过（找不到合适的大文件，也无法创建样本）"
            return 0
        fi
    fi

    info "拷贝路径探测"
    info "  复刻 .NET 的 File.Copy 策略：FICLONE → copy_file_range → sendfile → read/write"
    if [ "$synth" = "1" ]; then
        info "  注意：样本是新建的（落在可写层），可能测不出「从只读镜像层读」才发作的缺陷"
    fi

    local m label line rc ms state src size fs_src t_start elapsed src_idx
    local total_runs=0
    t_start="$(now_ms)"

    # 逐行读取样本：路径里可能有空格，不能直接用 for 展开 $srcs
    src_idx=0
    while IFS= read -r src; do
        [ -n "$src" ] || continue
        src_idx=$((src_idx + 1))
        size="$(stat -c %s "$src" 2>/dev/null || printf 0)"
        fs_src="$(fs_type_of "$(dirname "$src")")"
        info "  样本  $src"
        info "        $((size / 1048576)) MiB，$fs_src $(mount_info_of "$(dirname "$src")")"

        for m in ficlone cfr sendfile rw; do
            case "$m" in
                ficlone)  label="FICLONE(ioctl)   " ;;
                cfr)      label="copy_file_range  " ;;
                sendfile) label="sendfile         " ;;
                rw)       label="read/write（兜底）" ;;
            esac

            # read/write 是基准路径：第一份样本验一次就够，后面不必重复
            if [ "$m" = "rw" ] && [ "$src_idx" -gt 1 ]; then
                continue
            fi

            # 已确认卡住的路径不必在别的样本上重复验证（换样本只会得出同一结论）；
            # 但还没验过的路径要继续验 —— 这类缺陷可能只对特定文件发作。
            case ",$BROKEN_METHODS," in
                *",$m,"*)
                    info "        $label 跳过（已在其它样本上确认卡住）"
                    continue
                    ;;
            esac

            elapsed=$(( ($(now_ms) - t_start) / 1000 ))
            if [ "$total_runs" -gt 0 ] && [ "$elapsed" -ge "$PROBE_BUDGET" ]; then
                info "        $label 未探测（已超出探测预算 ${PROBE_BUDGET}s）"
                continue
            fi

            line="$(run_copy_method "$probe" "$m" "$src" "$dst" "$PROBE_TIMEOUT" 2>/dev/null)" && rc=0 || rc=$?
            total_runs=$((total_runs + 1))
            rm -f "$dst" 2>/dev/null || true

            ms="$(printf '%s' "$line" | sed -n 's/.*ms=\([0-9]*\).*/\1/p')"
            [ -n "$ms" ] || ms="?"
            state="$(printf '%s' "$line" | sed -n 's/.*state=\([^ ]*\).*/\1/p')"

            case "$rc" in
                124)
                    # 唯一真正要找的缺陷：超时 = 内核陷在里面出不来
                    if [ "$m" = "rw" ]; then
                        RW_HUNG=1
                        info "        $label 超时（>${PROBE_TIMEOUT}s，进程状态 ${state:--}）"
                        warn "连普通 read/write 拷贝都卡住 —— 问题在文件系统 / 存储层，屏蔽加速拷贝帮不上忙。"
                        break 2
                    fi
                    BROKEN_METHODS="${BROKEN_METHODS:+$BROKEN_METHODS,}$m"
                    info "        $label 超时（>${PROBE_TIMEOUT}s，进程状态 ${state:--}）"
                    info "               ^ 内核卡死的就是这条路径（soft lockup 的成因）"
                    ;;
                0)
                    PROBE_RAN=1
                    if printf '%s' "$line" | grep -q 'result=ok'; then
                        [ "$m" = "rw" ] && PROBE_OK=1
                        info "        $label ok    ${ms} ms"
                    else
                        # 系统调用明确报错：.NET 会自行回退到 read/write，不会卡死
                        info "        $label 不可用（$line）"
                        info "               .NET 会自动回退到普通 read/write，不影响正确性"
                        if [ "$m" = "rw" ]; then
                            PROBE_OK=0
                            break 2
                        fi
                    fi
                    ;;
                *)
                    info "        $label 没能执行（$line）"
                    if [ "$m" = "rw" ]; then
                        PROBE_OK=0
                        break 2
                    fi
                    ;;
            esac
        done
    done <<EOF
$srcs
EOF

    # 探测残留（样本可能是新造的 64 MiB 文件）
    [ "$synth" = "1" ] && rm -f "$BUILD_ROOT/tools/probe-src.bin" 2>/dev/null || true
    rm -f "$BUILD_ROOT/tools"/probe-*.out "$BUILD_ROOT/tools"/probe-*.err 2>/dev/null || true

    if [ "$RW_HUNG" = "1" ]; then
        info "  结论  连普通 read/write 都卡住：问题在文件系统 / 存储层本身，挂垫片帮不上忙。"
    elif [ "$PROBE_RAN" != "1" ]; then
        info "  结论  探测没能执行（探针跑不起来），本次不改变拷贝模式。"
        info "        如需强制屏蔽加速拷贝：fnos/build.sh --no-fastcopy"
    elif [ "$PROBE_OK" != "1" ]; then
        info "  结论  兜底路径也报错，探测结论不可信，保持原生模式。"
        info "        若构建确实卡住：fnos/build.sh --no-fastcopy"
    elif [ -n "$BROKEN_METHODS" ]; then
        info "  结论  $BROKEN_METHODS 在本机不可用：MSBuild 的 Copy 任务会卡死在内核里，"
        info "        表现为长时间无输出 + dmesg 里的 soft lockup，且 kill -9 也杀不掉。"
    else
        info "  结论  四条路径均正常，未探测到会导致卡死的加速拷贝缺陷。"
    fi
    return 0
}

# 按探测结果（或用户强制）决定拷贝模式，需要时编译并挂上垫片
decide_copy_mode() {
    COPY_MODE="native"
    COPY_SHIELD_LIST=""
    SHIM_PATH=""

    case "$NO_FASTCOPY" in
        0 | off | no | native) COPY_MODE="native" ;;
        1 | on | yes | shim | force) COPY_MODE="shim"; COPY_SHIELD_LIST="all" ;;
        *)
            # 只有在探测确实跑通、兜底路径正常、且发现了「超时」的路径时才挂垫片
            if [ "$PROBE_RAN" = "1" ] && [ "$PROBE_OK" = "1" ] &&
                [ "$RW_HUNG" != "1" ] && [ -n "$BROKEN_METHODS" ]; then
                COPY_MODE="shim"
                COPY_SHIELD_LIST="$BROKEN_METHODS"
            fi
            ;;
    esac

    if [ "$COPY_MODE" != "shim" ]; then
        return 0
    fi

    local so
    so="$(build_nofastcopy_shim 2>/dev/null || true)"
    if [ -z "$so" ]; then
        warn "需要「屏蔽加速拷贝」的垫片，但本机没有可用的 C 编译器（cc / gcc）。"
        warn "请安装后重试：sudo apt-get install -y gcc"
        warn "或改用别的方式：把 NUGET_PACKAGES 指到与 BUILD_ROOT 同一文件系统（有助走硬链接）。"
        COPY_MODE="native"
        return 0
    fi

    if ! check_shared_object "$so"; then
        warn "垫片 $so 不是本机可加载的 ELF 共享库，放弃挂载（保持原生模式）。"
        warn "请检查 cc -shared 是否正常工作：cc -shared -fPIC -o /tmp/t.so fnos/tools/no-fastcopy.c"
        COPY_MODE="native"
        return 0
    fi

    SHIM_PATH="$so"
    # 全局导出：连脚本自己的 cp -a 也一并保护，避免脚本自身卡在同一个内核缺陷上
    export LD_PRELOAD="$SHIM_PATH"
    export BYXCR_NOFASTCOPY="$COPY_SHIELD_LIST"
    return 0
}

log_copy_mode() {
    if [ "$COPY_MODE" = "shim" ]; then
        log "拷贝模式：屏蔽内核加速拷贝"
        info "垫片       $SHIM_PATH（屏蔽 $COPY_SHIELD_LIST）"
        info "效果       .NET / MSBuild 的拷贝全部走普通 read/write，不再进内核的加速路径"
        info "关掉它     fnos/build.sh --fastcopy"
    else
        log "拷贝模式：原生"
        info "未探测到会导致卡死的加速拷贝缺陷，保持 .NET 默认策略"
        info "若仍然卡死     fnos/build.sh --no-fastcopy 强制屏蔽加速拷贝"
    fi
}

# 读取 PNG 的 IHDR 尺寸，输出 "宽x高"；不是 PNG / 读不出则返回非 0。
# PNG 前 8 字节是固定签名，紧接着 IHDR：偏移 16 起是宽、高各 4 字节**大端**。
png_size() {
    local f="$1" b
    [ -f "$f" ] || return 1
    [ "$(head -c 8 "$f" | od -An -tx1 | tr -d ' \n')" = "89504e470d0a1a0a" ] || return 1
    b="$(od -An -tu1 -j16 -N8 "$f" 2>/dev/null || true)"
    # shellcheck disable=SC2086
    set -- $b
    [ $# -eq 8 ] || return 1
    printf '%dx%d' \
        "$(( $1 * 16777216 + $2 * 65536 + $3 * 256 + $4 ))" \
        "$(( $5 * 16777216 + $6 * 65536 + $7 * 256 + $8 ))"
}

# 图标预检。
# fnpack 只校验 desktop_uidir 这个目录存在，**不校验图标本身**：图标缺失、
# 文件名和 ui/config 里引用的对不上、或尺寸不对时，包照样能打成功，
# 装到设备上才表现为「入口在、图标空」。所以这里自己拦一道，把静默失败
# 变成构建期硬失败 —— 而且要放在编译之前，别等 AOT 跑完几分钟才发现。
check_icons() {
    local stage="$1" ui_config="$1/app/ui/config" icon_tpl f sz want rc=0 pair

    # 包根的两个图标（fnpack 会检查存在性，但不会检查尺寸）
    for pair in "ICON.PNG:64" "ICON_256.PNG:256"; do
        f="$stage/${pair%%:*}"
        want="${pair##*:}"
        if [ ! -f "$f" ]; then
            err "缺少图标 $f"
            rc=1
            continue
        fi
        sz="$(png_size "$f" 2>/dev/null || true)"
        if [ -z "$sz" ]; then
            err "$f 不是 PNG（或已损坏）"
            rc=1
        elif [ "$sz" != "${want}x${want}" ]; then
            err "$f 尺寸为 $sz，应为 ${want}x${want}"
            rc=1
        fi
    done

    # 桌面入口引用的图标：从 ui/config 取出 icon 模板，按尺寸展开后逐个核对
    if [ ! -f "$ui_config" ]; then
        err "缺少桌面入口配置 $ui_config"
        return 1
    fi
    icon_tpl="$(grep -o '"icon"[[:space:]]*:[[:space:]]*"[^"]*"' "$ui_config" 2>/dev/null |
        head -n1 | sed 's/.*:[[:space:]]*"//; s/"$//')"
    if [ -z "$icon_tpl" ]; then
        err "在 $ui_config 里取不到 icon 字段"
        return 1
    fi
    info "桌面图标模板   $icon_tpl"
    for want in 64 256; do
        f="$stage/app/ui/${icon_tpl//\{0\}/$want}"
        if [ ! -f "$f" ]; then
            err "桌面入口引用了 $icon_tpl（展开后 ${f##*/}），但该文件不存在"
            err "  期望路径：$f"
            err "  提示：图标目录曾因 .gitignore 里未锚定的 'images/' 规则被误忽略，"
            err "        确认它已纳入版本管理（git ls-files fnos/app/ui/images）"
            rc=1
            continue
        fi
        sz="$(png_size "$f" 2>/dev/null || true)"
        if [ -z "$sz" ]; then
            err "$f 不是 PNG（或已损坏）"
            rc=1
        elif [ "$sz" != "${want}x${want}" ]; then
            err "$f 尺寸为 $sz，应为 ${want}x${want}"
            rc=1
        fi
    done

    [ "$rc" = "0" ] || err "图标预检未通过（图标缺失时 fnpack 不会报错，装到设备上就是「有入口、没图标」）"
    return "$rc"
}

# 跨架构编译提前预警：在 x86 机器上编 arm64 包需要 aarch64 交叉工具链，
# 缺了会在链接阶段失败（且要等 IL 编译跑完才发现），不如一开始就说清楚。
cross_arch_hint() {
    local rid="$1" host
    host="$(uname -m)"
    case "$rid:$host" in
        linux-arm64:x86_64 | linux-arm64:amd64)
            if ! command -v aarch64-linux-gnu-gcc >/dev/null 2>&1 &&
                ! command -v aarch64-linux-gnu-ld >/dev/null 2>&1; then
                warn "本机是 $host，目标是 linux-arm64（跨架构编译）。"
                warn "NativeAOT 交叉编译需要 aarch64 交叉工具链与 arm64 版 zlib，当前未检测到，"
                warn "很可能在链接阶段失败。建议把这一个目标放到 arm64 机器上构建。"
            fi
            ;;
    esac
}

# 目标名 → RID
rid_for() {
    case "$1" in
        x86 | amd64 | x64) printf 'linux-x64' ;;
        arm | arm64 | aarch64) printf 'linux-arm64' ;;
        linux-x64 | linux-arm64) printf '%s' "$1" ;;
        *) return 1 ;;
    esac
}

# 目标名 → 短标签（用于产物文件名）
tag_for() {
    case "$1" in
        x86 | linux-x64) printf 'x86' ;;
        arm | linux-arm64) printf 'arm' ;;
        *) return 1 ;;
    esac
}

# RID → manifest 里的 platform 取值
platform_for() {
    case "$1" in
        linux-x64) printf 'x86' ;;
        linux-arm64) printf 'arm' ;;
        *) return 1 ;;
    esac
}

# RID → ELF 头里 e_machine 字段的期望字节（小端两字节，hex 字符串）
machine_for() {
    case "$1" in
        linux-x64) printf '3e00' ;;
        linux-arm64) printf 'b700' ;;
        *) return 1 ;;
    esac
}

# 把日志里的「目标/任务性能摘要」拉出来，直接告诉你时间花在哪。
# .NET 的输出会跟随系统语言（中文是「毫秒」，英文是 ms），两种都匹配。
show_slowest() {
    local logf="$1" lines
    [ -s "$logf" ] || return 0
    lines="$(grep -E '^[[:space:]]*[0-9]+[[:space:]]+(毫秒|ms)[[:space:]]' "$logf" 2>/dev/null |
        sed 's/^[[:space:]]*//' | sort -rn -k1 | head -8)" || true
    [ -n "$lines" ] || return 0
    info "最慢的目标/任务（前 8）："
    printf '%s\n' "$lines" | sed 's/^/      /'
}

# ------------------------------------------------------------------ 环境检查

check_zlib() {
    local d tmp
    for d in /usr/include /usr/local/include /usr/include/x86_64-linux-gnu /usr/include/aarch64-linux-gnu; do
        [ -f "$d/zlib.h" ] && return 0
    done
    if command -v pkg-config >/dev/null 2>&1 && pkg-config --exists zlib 2>/dev/null; then
        return 0
    fi
    # 头文件可能在非标准路径，交给 clang 自己判断
    tmp="$(mktemp 2>/dev/null || printf '/tmp/.byxcr-zlib-%s.c' "$$")"
    printf '#include <zlib.h>\nint main(void){return 0;}\n' > "$tmp" 2>/dev/null || return 1
    if clang -fsyntax-only "$tmp" >/dev/null 2>&1; then
        rm -f "$tmp"
        return 0
    fi
    rm -f "$tmp"
    return 1
}

check_toolchain() {
    command -v dotnet >/dev/null 2>&1 || die \
        "找不到 dotnet，请先安装 .NET 10 SDK：https://dotnet.microsoft.com/download"

    local v
    v="$(dotnet --version 2>/dev/null || true)"
    case "$v" in
        10.*) info "dotnet SDK $v" ;;
        '') warn "无法读取 dotnet 版本" ;;
        *) warn "检测到 .NET SDK $v，本项目目标为 net10.0，建议使用 .NET 10 SDK" ;;
    esac

    if [ "${SKIP_TOOLCHECK:-0}" = "1" ]; then
        warn "SKIP_TOOLCHECK=1：已跳过 clang / zlib 检查"
        return 0
    fi

    # clang 是硬依赖：NativeAOT 的链接阶段直接调用它，缺了必然失败，
    # 而且会等到编译跑完才报错，白等几十分钟，所以这里提前拦住。
    if ! command -v clang >/dev/null 2>&1; then
        die "缺少 clang —— NativeAOT 的链接阶段必须用它，缺了构建一定失败。
请先安装：
    Debian/Ubuntu  sudo apt-get update && sudo apt-get install -y clang zlib1g-dev
    RHEL/CentOS    sudo dnf install -y clang zlib-devel
    Alpine         sudo apk add clang zlib-dev build-base
确认环境没问题、只想强行继续：SKIP_TOOLCHECK=1 fnos/build.sh ..."
    fi
    info "clang      $(command -v clang)"

    if ! check_zlib; then
        die "缺少 zlib 开发包（zlib.h）—— NativeAOT 链接阶段会用到。
请先安装：sudo apt-get install -y zlib1g-dev"
    fi
    info "zlib       已就绪"

    # cc/gcc 不是编译项目的必需项，但没有它就无法运行拷贝路径探测 / 编译垫片
    if command -v cc >/dev/null 2>&1 || command -v gcc >/dev/null 2>&1; then
        info "C 编译器   $(command -v cc 2>/dev/null || command -v gcc)"
    else
        warn "未找到 cc / gcc：无法运行「拷贝路径探测」，也无法在内核加速拷贝出问题时挂垫片。"
        warn "建议安装：sudo apt-get install -y gcc"
    fi
}

# 打印磁盘 / 内存 / 文件系统与写入延迟，并给出针对性告警
resource_report() {
    local repo_ms root_ms fs_repo fs_root avail_repo avail_root avail_nuget mem_txt

    repo_ms="$(probe_dir_ms "$OUT_DIR")"
    root_ms="$(probe_dir_ms "$BUILD_ROOT")"
    fs_repo="$(fs_type_of "$OUT_DIR")"
    fs_root="$(fs_type_of "$BUILD_ROOT")"
    avail_repo="$(avail_mib_of "$OUT_DIR" 2>/dev/null || true)"
    avail_root="$(avail_mib_of "$BUILD_ROOT" 2>/dev/null || true)"
    avail_nuget="$(avail_mib_of "$(nuget_cache_dir)" 2>/dev/null || true)"

    info "文件系统   仓库 $fs_repo｜构建根 $fs_root"
    info "可用空间   仓库 $( [ -n "$avail_repo" ] && human_mib "$avail_repo" || printf '未知' )｜构建根 $( [ -n "$avail_root" ] && human_mib "$avail_root" || printf '未知' )｜NuGet $( [ -n "$avail_nuget" ] && human_mib "$avail_nuget" || printf '未知' )"
    info "新建文件   仓库 $(describe_ms "$repo_ms")｜构建根 $(describe_ms "$root_ms")"
    info "           （仅代表新建文件；拷贝路径的坑在内核里，见下一节的探测）"

    if is_slow_ms "$repo_ms"; then
        warn "仓库目录新建文件耗时 $(describe_ms "$repo_ms")，疑似网络盘 / 共享挂载。"
        warn "本脚本已把 obj/ bin/ 与打包暂存目录放到 $BUILD_ROOT，只把 .fpk 写回仓库。"
    fi
    if is_slow_ms "$root_ms"; then
        warn "构建根目录 $BUILD_ROOT 同样缓慢，建议换本地盘：BUILD_ROOT=/root/.cache/byxcr-build fnos/build.sh ..."
    fi
    if [ "$fs_root" != "$fs_repo" ]; then
        info "提示       仓库与构建根不在同一文件系统，MSBuild 只能用真拷贝（跨设备无法硬链接）。"
    fi
    warn_low_disk
    warn_low_memory
}

health_report() {
    log "环境体检"
    info "宿主架构   $(uname -m)｜CPU $(nproc 2>/dev/null || printf '未知') 核"
    info "内核       $(uname -r)"
    info "仓库根目录 $ROOT"
    info "构建根目录 $BUILD_ROOT"

    if command -v dotnet >/dev/null 2>&1; then
        info "dotnet     $(dotnet --version 2>/dev/null)  $(command -v dotnet)"
    else
        info "dotnet     未安装（NativeAOT 编译必需）"
    fi

    local m
    m="$(mem_avail_mib)" && info "内存       可用 $(human_mib "$m")" || info "内存       未知"

    local t zlib_state
    for t in clang gcc cc ld nm objcopy strip ar fnpack awk grep sed du df od findmnt ps; do
        if command -v "$t" >/dev/null 2>&1; then
            info "$(printf '%-12s %s' "$t" "$(command -v "$t")")"
        else
            info "$(printf '%-12s %s' "$t" "未找到")"
        fi
    done

    if command -v clang >/dev/null 2>&1 && check_zlib; then
        zlib_state="已就绪"
    else
        zlib_state="未就绪 —— 需要 zlib 开发包（apt-get install -y zlib1g-dev）"
    fi
    info "$(printf '%-12s %s' "zlib" "$zlib_state")"

    log "关键路径的挂载情况"
    local d fs mi
    for d in "$OUT_DIR" "$BUILD_ROOT" "$(nuget_cache_dir)"; do
        [ -n "$d" ] || continue
        fs="$(fs_type_of "$d")"
        mi="$(mount_info_of "$d")"
        info "$(printf '%-10s %-12s %s' "$(basename "$d")" "$fs" "$mi")"
        if is_risky_fs "$fs"; then
            warn "「$d」在 $fs 上：这类文件系统对内核加速拷贝（FICLONE / copy_file_range / sendfile）"
            warn "存在已知缺陷，可能是构建卡死的原因 —— 请看下面的探测结果。"
        fi
    done

    log "资源与延迟"
    resource_report

    log "拷贝路径探测（决定是否要屏蔽内核加速拷贝）"
    probe_copy_report
    decide_copy_mode
    log_copy_mode
}

# ------------------------------------------------------------------ 前置校验

[ -f "$PROJECT" ] || die "找不到项目文件：$PROJECT"
[ -d "$PKG/app" ] || die "找不到应用包目录：$PKG/app（请在仓库根目录下运行本脚本）"

if [ "$CLEAN" = "1" ]; then
    log "清理构建缓存 $BUILD_ROOT"
    rm -rf "$BUILD_ROOT"
    info "已清理（下次构建会重新探测拷贝路径）"
    exit 0
fi

FNPACK="${FNPACK:-fnpack}"
HAVE_FNPACK=0
if command -v "$FNPACK" >/dev/null 2>&1 || [ -x "$FNPACK" ]; then
    HAVE_FNPACK=1
fi

mkdir -p "$OUT_DIR"

# 体检模式：只报告、不拦截，缺什么也照样把结论打出来
if [ "$DOCTOR" = "1" ]; then
    health_report
    exit 0
fi

[ "$HAVE_FNPACK" = "1" ] || die \
    "找不到 fnpack。请从 https://developer.fnnas.com/docs/cli/fnpack/ 下载并放入 PATH，或用 FNPACK=/path/to/fnpack 指定。"

if [ -z "$BIN_DIR" ]; then
    check_toolchain
fi

# 版本号：优先取命令行/环境变量，其次读 csproj
if [ -z "${VERSION:-}" ]; then
    VERSION="$(sed -n 's@.*<Version>\(.*\)</Version>.*@\1@p' "$PROJECT" | head -n 1)"
fi
[ -n "${VERSION:-}" ] || die "无法从 byxcr.csproj 解析版本号，请用 --version 指定。"

log "byxcr fnOS 应用包构建 · 版本 $VERSION"
info "仓库根目录 $ROOT"
info "输出目录   $OUT_DIR"
info "构建根目录 $BUILD_ROOT"

mkdir -p "$BUILD_ROOT"

if [ -z "$BIN_DIR" ]; then
    # 每次构建都报一次延迟 / 空间 / 内存，出问题时不必再单独跑 --doctor
    log "资源与延迟"
    resource_report

    # 是否要屏蔽内核加速拷贝：先用探针问清楚，再自动决定
    if [ "$NO_FASTCOPY" = "auto" ]; then
        log "拷贝路径探测"
        probe_copy_report
    else
        log "拷贝路径探测：已跳过（NO_FASTCOPY=$NO_FASTCOPY）"
    fi
fi

# 复用已有产物（--bin-dir）时不跑 dotnet，但脚本自己也要拷文件，
# 所以无论哪种情况都按需挂上垫片。
decide_copy_mode
log_copy_mode

# ------------------------------------------------------------------ 单个目标

build_one() {
    local target="$1"
    local rid tag platform expect arch stage pub art log_file produced final size bin actual src_dir
    local t_start t_end

    rid="$(rid_for "$target")" || { err "不支持的目标：$target（可选：x86 / arm / all）"; return 1; }
    tag="$(tag_for "$target")" || { err "不支持的目标：$target"; return 1; }
    platform="$(platform_for "$rid")"
    expect="$(machine_for "$rid")"

    stage="$BUILD_ROOT/work-$tag"
    pub="$BUILD_ROOT/publish-$tag"
    art="$BUILD_ROOT/artifacts"
    # 日志单独放 logs/：收尾清理 work-*/publish-* 时不会把排查用的日志一并删掉
    log_file="$BUILD_ROOT/logs/publish-$tag.log"
    mkdir -p "$BUILD_ROOT/logs"

    log "构建 $tag（$rid，platform=$platform）"

    # ---- 1. 复制应用包骨架（暂存在快盘，避免逐文件写慢挂载） ----
    rm -rf "$stage" "$pub"
    mkdir -p "$stage"
    cp -a \
        "$PKG/app" "$PKG/cmd" "$PKG/config" "$PKG/wizard" \
        "$PKG/manifest" "$PKG/ICON.PNG" "$PKG/ICON_256.PNG" \
        "$stage/"

    # 图标预检放在编译之前：图标没了属于「包能打出来但装上去不完整」，
    # 必须现在拦，别等 AOT 跑完几分钟再退。
    check_icons "$stage" || return 1

    # ---- 2. 编译 NativeAOT 产物 ----
    t_start="$(now_ms)"
    if [ -n "$BIN_DIR" ]; then
        info "复用已有编译产物：$BIN_DIR"
        [ -f "$BIN_DIR/byxcr" ] || { err "$BIN_DIR 下找不到 byxcr 可执行文件"; return 1; }
        src_dir="$BIN_DIR"
    else
        cross_arch_hint "$rid"
        info "编译中（NativeAOT 首次通常 1~5 分钟，CPU 会跑满）"
        info "详细日志：$log_file"
        if [ -t 1 ]; then
            info "进度看终端最后一行（会实时刷新「当前目标 + 已耗时」）"
        fi

        # 交互式终端：保留 MSBuild 终端日志器（实时进度），详细日志交给文件日志器。
        # 输出被重定向（CI / nohup / 管道）时终端日志器本来就不会启用，
        # 这里改用普通控制台日志器 + normal 详细度，保证输出里能看到目标级进展。
        local -a mblog=()
        local vflag="$VERBOSITY"
        mblog=(-fl "-flp:logfile=$log_file;verbosity=normal;encoding=utf-8;performanceSummary;summary")
        if [ ! -t 1 ]; then
            vflag="n"
            mblog=(-tl:off "${mblog[@]}")
        fi

        # 不要写成 `dotnet publish ... | tee 日志`：stdout 变成管道后 MSBuild 的
        # 终端日志器会自动关闭，整段编译将毫无输出，看起来就像卡死。

        # --artifacts-path 把 obj/ 与 bin/ 挪出仓库；-o 决定发布输出目录（两者可同时用）
        if ! dotnet publish "$PROJECT" \
            -c Release \
            -r "$rid" \
            --artifacts-path "$art" \
            -o "$pub" \
            -p:PublishAot=true \
            -p:PublishTrimmed=true \
            -p:StripSymbols=true \
            -p:IlcUseEnvironmentalTools=true \
            -v:"$vflag" \
            "${mblog[@]}"; then
            err "dotnet publish 失败（$rid）。完整日志：$log_file"
            if [ -f "$log_file" ]; then
                info "日志末尾 20 行："
                tail -n 20 "$log_file" | sed 's/^/      /' || true
            fi
            info "若日志停在 _CopyFilesMarkedCopyLocal 之类的位置且长时间无输出，"
            info "而 dmesg 里有 soft lockup，则用：fnos/build.sh --no-fastcopy $target"
            return 1
        fi
        show_slowest "$log_file"
        src_dir="$pub"
    fi
    t_end="$(now_ms)"
    info "编译耗时 $(( (t_end - t_start) / 1000 )) s"

    # ---- 3. 放置运行文件：byxcr.bin（主程序）+ 原生依赖 ----
    # 注意：app/bin/byxcr 是命令行包装脚本，主程序必须改名，否则会互相覆盖
    cp -a "$src_dir/byxcr" "$stage/app/bin/byxcr.bin"
    find "$src_dir" -maxdepth 1 -name '*.so*' -exec cp -a {} "$stage/app/bin/" \;
    rm -f "$stage/app/bin"/*.dbg "$stage/app/bin"/*.pdb 2>/dev/null || true

    bin="$stage/app/bin/byxcr.bin"
    [ -f "$bin" ] || { err "编译产物缺少 byxcr.bin"; return 1; }

    # 校验 ELF 魔数 + 机器架构：RID 选错时第一时间发现，而不是等装到设备上才炸
    if [ "$(head -c 4 "$bin" | od -An -tx1 | tr -d ' \n')" != "7f454c46" ]; then
        err "byxcr.bin 不是 ELF 可执行文件（RID=$rid 可能不匹配宿主平台）"
        return 1
    fi
    actual="$(od -An -tx1 -j18 -N2 "$bin" | tr -d ' \n')"
    if [ "$actual" != "$expect" ]; then
        err "byxcr.bin 的 ELF 架构为 $actual，与目标 $rid 期望的 $expect 不符"
        return 1
    fi

    chmod 0755 "$bin" "$stage/app/bin/byxcr"
    find "$stage/app/bin" -maxdepth 1 -name '*.so*' -exec chmod 0644 {} \;

    if ! ls "$stage/app/bin" | grep -q '^libe_sqlite3\.so'; then
        warn "未找到 libe_sqlite3.so：SQLite 依赖缺失时应用无法启动，请确认已还原 Microsoft.Data.Sqlite 运行时包。"
    fi

    # ---- 4. 写入架构与版本 ----
    sed -i "s@^platform=.*@platform=$platform@" "$stage/manifest"
    sed -i "s@^version=.*@version=$VERSION@" "$stage/manifest"

    grep -q "^platform=$platform\$" "$stage/manifest" || { err "manifest 中 platform 写入失败"; return 1; }
    grep -q "^version=$VERSION\$" "$stage/manifest" || { err "manifest 中 version 写入失败"; return 1; }

    # ---- 5. 规范文件权限 ----
    # 打包工具在不同平台上对权限位的处理并不一致（Windows 上会全部落成 0666），
    # 这里统一收口，保证安装到设备后的权限可预期。
    find "$stage" -type d -exec chmod 0755 {} +
    find "$stage" -type f -exec chmod 0644 {} +
    # 生命周期脚本由 fnOS 调用，命令行包装脚本经 usr-local-linker 暴露到 /usr/local/bin
    find "$stage/cmd" -type f -exec chmod 0755 {} +
    chmod 0755 "$stage/app/bin/byxcr" "$bin"

    # ---- 6. 打包 ----
    (cd "$stage" && "$FNPACK" build)

    produced="$stage/byxcr.fpk"
    [ -f "$produced" ] || { err "未生成 $produced"; return 1; }

    final="$OUT_DIR/byxcr-${VERSION}-${tag}.fpk"
    mv -f "$produced" "$final"

    size="$(du -h "$final" | cut -f1)"
    log "已生成 $final（$size）"
    return 0
}

# ------------------------------------------------------------------ 主流程

QUEUE=()
for t in "${TARGETS[@]}"; do
    if [ "$t" = "all" ]; then
        QUEUE+=(x86 arm)
    else
        QUEUE+=("$t")
    fi
done

DONE=()
FAILED=()
for t in "${QUEUE[@]}"; do
    # 子 shell 里重新打开 errexit，避免 if 条件上下文把 set -e 关掉
    set +e
    (set -e; build_one "$t")
    rc=$?
    set -e
    if [ "$rc" -eq 0 ]; then
        DONE+=("$t")
    else
        FAILED+=("$t")
    fi
done

# 中间产物（work/publish）用完即清；artifacts 保留以加速二次构建。
# 清理失败（权限、沙箱限制等）不应该让一次成功的构建报错，所以这里不中断。
rm -rf "$BUILD_ROOT"/work-* "$BUILD_ROOT"/publish-* 2>/dev/null || true

log "构建结束"
if [ "${#DONE[@]}" -gt 0 ]; then
    info "成功：${DONE[*]}"
    ls -1 "$OUT_DIR"/*.fpk 2>/dev/null | sed 's/^/    /' || true
fi
if [ "${#FAILED[@]}" -gt 0 ]; then
    err "失败：${FAILED[*]}"
    info "日志位于 $BUILD_ROOT/logs/publish-*.log"
    info "若日志卡在某一步长时间无输出，先看：dmesg | tail -20"
    info "若确认是 soft lockup（内核卡死），用：fnos/build.sh --no-fastcopy ${FAILED[*]}"
    info "清掉缓存重来：fnos/build.sh --clean"
    exit 1
fi
