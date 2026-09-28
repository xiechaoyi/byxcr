#!/bin/bash
# ============================================================================
# byxcr · 飞牛 fnOS 应用公共函数
#
# 被 cmd/ 下的生命周期脚本与 app/bin/byxcr（命令行包装脚本）加载：
#     . "${TRIM_APPDEST}/lib/byxcr-env.sh"
#
# 约定：
#   * 脚本可能运行在安装、升级、配置、启动等不同流程中，所有函数都必须可重复执行；
#   * 目录用 TRIM_* 变量推导，不硬编码存储空间编号（/vol1、/vol2 …会变）；
#   * 用户配置落到 ${TRIM_PKGETC}/byxcr.env（对应 byxcr「环境变量优先级最高」的语义）。
#
# byxcr.env 的格式是纯 KEY=VALUE，值原样存放、不做 shell 转义：
# 读取时由 byxcr_env_export 逐行解析后 export，而不是 source 整个文件。
# 这样令牌 / WebDAV 口令里出现单引号、空格、$ 等字符都不会出问题。
# ============================================================================

# 内置镜像源（向导留空且没有历史配置时使用）
BYXCR_DEFAULT_REGISTRIES="docker.1ms.run,mirror.ccs.tencentyun.com,dockerproxy.net,hub.rat.dev,docker.1panel.live"

# ---------------------------------------------------------------- 路径解析

# 应用在 /var/apps 下的稳定路径（跨存储空间不变，卸载前始终有效）
byxcr_app_root() { printf '%s' "/var/apps/${TRIM_APPNAME:-byxcr}"; }

# 运行时数据目录：数据库、日志、临时文件
byxcr_data_dir() { printf '%s' "${TRIM_PKGVAR:-$(byxcr_app_root)/var}"; }

# 应用配置目录
byxcr_etc_dir() { printf '%s' "${TRIM_PKGETC:-$(byxcr_app_root)/etc}"; }

# 应用运行文件目录（target）
byxcr_appdest() { printf '%s' "${TRIM_APPDEST:-$(byxcr_app_root)/target}"; }

# 主程序（NativeAOT 产物）
byxcr_bin() { printf '%s' "$(byxcr_appdest)/bin/byxcr.bin"; }

# 主配置文件（缺失时 byxcr 使用内置默认值）
byxcr_conf() { printf '%s' "$(byxcr_etc_dir)/byxcr.yaml"; }

# 向导生成的运行时配置
byxcr_env_file() { printf '%s' "$(byxcr_etc_dir)/byxcr.env"; }

# 镜像归档根目录：优先用系统创建的共享目录（用户可在文件管理器里直接取用归档）
byxcr_image_root() {
    local first share paths
    # TRIM_DATA_SHARE_PATHS 可能是多个路径，用 : 分隔；
    # 注意先补默认值再切分：脚本以 set -u 运行，直接对未定义变量做 %% 展开会直接报错
    paths="${TRIM_DATA_SHARE_PATHS:-}"
    first="${paths%%:*}"
    if [ -n "$first" ] && [ -d "$first" ]; then
        printf '%s' "$first"
        return
    fi
    share="$(byxcr_app_root)/share/images"
    if [ -d "$share" ]; then
        printf '%s' "$share"
        return
    fi
    printf '%s' "$(byxcr_data_dir)/images"
}

# 日志文件（服务标准输出/错误）
byxcr_service_log() { printf '%s' "$(byxcr_data_dir)/logs/service.log"; }

# PID 文件
byxcr_pid_file() { printf '%s' "$(byxcr_data_dir)/byxcr.pid"; }

# ---------------------------------------------------------------- 小工具

byxcr_log() { printf '[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*"; }

# 产出用户可见错误：写入 TRIM_TEMP_LOGFILE（fnOS 会在界面上展示）
byxcr_fail() {
    printf '\n[byxcr] %s\n' "$*" >&2
    if [ -n "${TRIM_TEMP_LOGFILE:-}" ]; then
        printf '\n%s\n' "$*" >> "$TRIM_TEMP_LOGFILE" 2>/dev/null || true
    fi
}

# 从既有 env 文件取一个键的值（原样返回，不做引号剥离）
byxcr_env_get() {
    local file="$1" key="$2" line
    [ -f "$file" ] || return 0
    line="$(grep -m1 "^${key}=" "$file" 2>/dev/null)" || return 0
    [ -n "$line" ] || return 0
    line="${line%$'\r'}"
    printf '%s' "${line#*=}"
}

# 把 env 文件里的键值导入当前进程环境。
# 逐行解析后 export，而不是 `. file`：配置文件里可能含空格、引号、$ 等字符，
# 交给 shell 求值既可能读错值，也可能因为一个引号把整行变成语法错误。
byxcr_env_export() {
    local file="$1" line key value
    [ -r "$file" ] || return 0
    while IFS= read -r line || [ -n "$line" ]; do
        line="${line%$'\r'}"
        case "$line" in ''|'#'*) continue ;; esac
        case "$line" in *=*) ;; *) continue ;; esac
        key="${line%%=*}"
        value="${line#*=}"
        # 只接受合法的变量名，避免把注释或意外内容当成变量
        case "$key" in *[!A-Za-z0-9_]*) continue ;; esac
        export "$key=$value"
    done < "$file"
    return 0
}

# 取值优先级：向导本次输入 > 既有配置 > 内置默认。
# 向导里的「保持不变」会提交字面量 keep，这里统一折算成「没填」。
byxcr_pick() {
    local wizard="$1" existing="$2" fallback="$3"
    if [ "$wizard" = "keep" ]; then wizard=""; fi
    if [ -n "$wizard" ]; then printf '%s' "$wizard"
    elif [ -n "$existing" ]; then printf '%s' "$existing"
    else printf '%s' "$fallback"
    fi
}

# 只保留数字，非数字或超出范围时回落到默认值
byxcr_pick_int() {
    local value="$1" fallback="$2" min="$3" max="$4"
    case "$value" in
        ''|*[!0-9]*) value="$fallback" ;;
    esac
    # 用 if 而不是 `[ ] && x`：后者在 set -e 下的行为依赖 AND-OR 列表的豁免规则，容易踩坑
    if [ "$value" -lt "$min" ]; then value="$min"; fi
    if [ "$value" -gt "$max" ]; then value="$max"; fi
    printf '%s' "$value"
}

# 把各种布尔写法统一成 true / false
byxcr_bool() {
    case "$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')" in
        1|true|yes|on) printf 'true' ;;
        *) printf 'false' ;;
    esac
}

# 归一化「逗号分隔的列表」：允许顺手用分号或空格分隔，并去掉空项
byxcr_normalize_list() {
    printf '%s' "$1" | tr ';' ',' | tr -d ' ' | sed 's/,,*/,/g; s/^,//; s/,$//'
}

# ---------------------------------------------------------------- 目录准备

# 建立运行时目录、给二进制补执行位
byxcr_prepare_dirs() {
    local data_dir image_root appdest
    data_dir="$(byxcr_data_dir)"
    image_root="$(byxcr_image_root)"
    appdest="$(byxcr_appdest)"

    mkdir -p "$data_dir" "$data_dir/logs" "$data_dir/tmp" 2>/dev/null || true
    mkdir -p "$image_root" 2>/dev/null || true
    mkdir -p "$(byxcr_etc_dir)" 2>/dev/null || true

    # .fpk 解包可能丢掉可执行位，这里补上（byxcr.bin 与命令行包装脚本）
    if [ -f "$appdest/bin/byxcr.bin" ]; then chmod 0755 "$appdest/bin/byxcr.bin" 2>/dev/null || true; fi
    if [ -f "$appdest/bin/byxcr" ]; then chmod 0755 "$appdest/bin/byxcr" 2>/dev/null || true; fi
    return 0
}

# 生成注释齐全的主配置文件。
# byxcr init 写出来的就是官方模板，这里只生成一次，之后不再覆盖用户的改动。
byxcr_prepare_config() {
    local bin conf
    bin="$(byxcr_bin)"
    conf="$(byxcr_conf)"

    if [ -f "$conf" ]; then
        return 0
    fi
    if [ ! -x "$bin" ]; then
        return 0
    fi

    BYXCR_CONFIG="$conf" "$bin" init --config "$conf" >/dev/null 2>&1 || true
    return 0
}

# ---------------------------------------------------------------- 配置落盘

# 把向导收集到的值写成 ${TRIM_PKGETC}/byxcr.env。
# 留空的字段会沿用上一次的值；首次安装则使用内置默认值。
byxcr_write_env() {
    local etc envfile data_dir image_root
    etc="$(byxcr_etc_dir)"
    envfile="$(byxcr_env_file)"
    data_dir="$(byxcr_data_dir)"
    image_root="$(byxcr_image_root)"

    if ! mkdir -p "$etc" 2>/dev/null; then
        byxcr_fail "无法创建应用配置目录：$etc"
        return 1
    fi

    local registries platforms interval concurrency schedule_concurrency api_token
    local webdav_enabled webdav_url webdav_user webdav_pass log_level note=""

    registries="$(byxcr_normalize_list "$(byxcr_pick "${wizard_registries:-}" "$(byxcr_env_get "$envfile" BYXCR_REGISTRIES)" "$BYXCR_DEFAULT_REGISTRIES")")"
    platforms="$(byxcr_normalize_list "$(byxcr_pick "${wizard_platforms:-}" "$(byxcr_env_get "$envfile" BYXCR_PLATFORMS)" '')")"

    interval="$(byxcr_pick_int "$(byxcr_pick "${wizard_interval:-}" "$(byxcr_env_get "$envfile" BYXCR_SYNC_INTERVAL)" 1440)" 1440 1 525600)"
    concurrency="$(byxcr_pick_int "$(byxcr_pick "${wizard_max_concurrency:-}" "$(byxcr_env_get "$envfile" BYXCR_MAX_CONCURRENCY)" 2)" 2 1 32)"
    schedule_concurrency="$(byxcr_pick_int "$(byxcr_pick "${wizard_schedule_concurrency:-}" "$(byxcr_env_get "$envfile" BYXCR_SCHEDULE_CONCURRENCY)" 4)" 4 1 4)"

    api_token="$(byxcr_pick "${wizard_api_token:-}" "$(byxcr_env_get "$envfile" BYXCR_API_TOKEN)" '')"

    webdav_enabled="$(byxcr_bool "$(byxcr_pick "${wizard_webdav_enabled:-}" "$(byxcr_env_get "$envfile" BYXCR_WEBDAV)" false)")"
    webdav_url="$(byxcr_pick "${wizard_webdav_url:-}" "$(byxcr_env_get "$envfile" BYXCR_WEBDAV_URL)" '')"
    webdav_user="$(byxcr_pick "${wizard_webdav_username:-}" "$(byxcr_env_get "$envfile" BYXCR_WEBDAV_USERNAME)" '')"
    webdav_pass="$(byxcr_pick "${wizard_webdav_password:-}" "$(byxcr_env_get "$envfile" BYXCR_WEBDAV_PASSWORD)" '')"

    # 开了 WebDAV 却没填地址会让 byxcr 启动即失败（Normalize 会直接抛异常），这里兜底关掉并留提示
    if [ "$webdav_enabled" = "true" ] && [ -z "$webdav_url" ]; then
        webdav_enabled="false"
        note="# 注意：本次开启了 WebDAV 但未填写地址，已自动关闭；补全地址后在「应用设置」中重新开启
"
    fi

    log_level="$(byxcr_pick "${wizard_log_level:-}" "$(byxcr_env_get "$envfile" BYXCR_LOG_LEVEL)" info)"
    case "$log_level" in
        debug|info|warn|error|none) ;;
        *) log_level="info" ;;
    esac

    # 值一律原样写入（不加引号）：读取端是 byxcr_env_export 的逐行解析，
    # 加引号反而会把引号变成值的一部分
    cat > "$envfile.tmp" <<EOF
# ============================================================================
# byxcr · 运行时配置（由飞牛应用「安装向导 / 应用设置」生成，请勿手工改本文件）
#
# 读取方式：启动时逐行解析 KEY=VALUE 后注入为环境变量（不做 shell 求值），
# 因此值请原样书写，不要加引号。
#
# byxcr 的配置优先级为：环境变量 > 配置文件 > 内置默认值。
# 这里每一项都会覆盖 byxcr.yaml 里的同名配置；需要精细控制
#（限速、私有仓库凭据、WebDAV 高级项等）请直接编辑上层配置文件：
#     $(byxcr_conf)
# 想交还某一项给配置文件控制，删掉本文件里对应的那一行即可。
# ============================================================================

# ---- 运行目录（由应用安装位置决定，请勿修改） ----
BYXCR_DATA_DIR=${data_dir}
BYXCR_IMAGE_ROOT=${image_root}
BYXCR_TEMP_DIR=${data_dir}/tmp
BYXCR_LOG_DIR=${data_dir}/logs
BYXCR_LOG_COLOR=false

# ---- 镜像源（按顺序探测与重试） ----
BYXCR_REGISTRIES=${registries}
# ---- 限定下载架构，留空表示下载清单中的全部架构 ----
BYXCR_PLATFORMS=${platforms}

# ---- 同步调度 ----
BYXCR_SYNC_INTERVAL=${interval}
BYXCR_MAX_CONCURRENCY=${concurrency}
BYXCR_SCHEDULE_CONCURRENCY=${schedule_concurrency}

# ---- 内置 Web 控制台 / WebAPI（端口在应用包的 manifest 中声明，固定 5088） ----
BYXCR_API_PORT=5088
BYXCR_API_TOKEN=${api_token}

# ---- 归档到 WebDAV（开启后不再写入本地共享目录） ----
${note}BYXCR_WEBDAV=${webdav_enabled}
BYXCR_WEBDAV_URL=${webdav_url}
BYXCR_WEBDAV_USERNAME=${webdav_user}
BYXCR_WEBDAV_PASSWORD=${webdav_pass}

# ---- 日志级别：debug / info / warn / error / none ----
BYXCR_LOG_LEVEL=${log_level}
EOF

    if [ ! -f "$envfile.tmp" ]; then
        byxcr_fail "写入运行时配置失败：$envfile"
        return 1
    fi
    if ! mv -f "$envfile.tmp" "$envfile"; then
        byxcr_fail "写入运行时配置失败：$envfile"
        return 1
    fi

    # 含令牌与 WebDAV 口令，仅允许应用用户读取
    if [ "$(id -u 2>/dev/null)" = "0" ] && [ -n "${TRIM_USERNAME:-}" ]; then
        chown "${TRIM_USERNAME}:${TRIM_GROUPNAME:-$TRIM_USERNAME}" "$envfile" 2>/dev/null || true
    fi
    chmod 0600 "$envfile" 2>/dev/null || true

    byxcr_log "运行时配置已更新：$envfile"
    return 0
}

# ---------------------------------------------------------------- 服务控制

# 是否正在运行（PID 文件优先，进程名兜底）
byxcr_service_running() {
    local pidfile pid bin
    pidfile="$(byxcr_pid_file)"
    bin="$(byxcr_bin)"

    if [ -f "$pidfile" ]; then
        pid="$(head -n 1 "$pidfile" 2>/dev/null | tr -d '[:space:]')"
        if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
            return 0
        fi
        rm -f "$pidfile" 2>/dev/null
    fi

    if [ -n "$(pgrep -f "$bin run" 2>/dev/null)" ]; then
        return 0
    fi
    return 1
}

byxcr_service_start() {
    local bin envfile logfile data_dir image_root pidfile pid real

    if byxcr_service_running; then
        byxcr_log "服务已在运行，跳过启动"
        return 0
    fi

    bin="$(byxcr_bin)"
    if [ ! -f "$bin" ]; then
        byxcr_fail "缺少可执行文件 ${bin}。应用包内没有 Linux 二进制，请在 Linux 上执行 fnos/build.sh 重新构建 .fpk 后再安装。"
        return 1
    fi
    chmod 0755 "$bin" 2>/dev/null || true

    byxcr_prepare_dirs

    data_dir="$(byxcr_data_dir)"
    image_root="$(byxcr_image_root)"
    envfile="$(byxcr_env_file)"
    logfile="$(byxcr_service_log)"
    pidfile="$(byxcr_pid_file)"
    mkdir -p "$data_dir/logs" 2>/dev/null || true

    # 导入向导生成的运行时配置（镜像源、令牌、WebDAV 等）
    byxcr_env_export "$envfile"

    # 目录以本次运行的系统变量为准：换存储空间后无需重新安装
    export BYXCR_CONFIG="$(byxcr_conf)"
    export BYXCR_DATA_DIR="$data_dir"
    export BYXCR_IMAGE_ROOT="$image_root"
    export BYXCR_TEMP_DIR="$data_dir/tmp"
    export BYXCR_LOG_DIR="$data_dir/logs"
    if [ -z "${TZ:-}" ]; then export TZ=Asia/Shanghai; fi

    byxcr_log "启动 byxcr（$bin）"
    byxcr_log "  数据目录 ${data_dir}"
    byxcr_log "  归档目录 ${image_root}"
    byxcr_log "  配置文件 $(byxcr_conf)"
    byxcr_log "  运行日志 ${logfile}"

    cd "$data_dir" 2>/dev/null || cd "$(byxcr_appdest)" 2>/dev/null || return 1

    # setsid 让守护进程脱离生命周期脚本的会话，避免脚本退出时被一起回收
    if command -v setsid >/dev/null 2>&1; then
        setsid "$bin" run >> "$logfile" 2>&1 < /dev/null &
    else
        nohup "$bin" run >> "$logfile" 2>&1 < /dev/null &
    fi
    pid=$!
    printf '%s\n' "$pid" > "$pidfile"

    sleep 2
    if ! kill -0 "$pid" 2>/dev/null; then
        # setsid 可能先 fork 再执行，此时 $! 已经退出，按进程名找回真正的 PID
        real="$(pgrep -f "$bin run" 2>/dev/null | head -n 1)"
        if [ -n "$real" ]; then
            pid="$real"
            printf '%s\n' "$pid" > "$pidfile"
        else
            rm -f "$pidfile" 2>/dev/null
            byxcr_fail "byxcr 启动失败或已退出，请查看日志：${logfile}"
            return 1
        fi
    fi

    byxcr_log "已启动（PID ${pid}）"
    return 0
}

byxcr_service_stop() {
    local pidfile pid bin i

    pidfile="$(byxcr_pid_file)"
    bin="$(byxcr_bin)"

    pid=""
    if [ -f "$pidfile" ]; then
        pid="$(head -n 1 "$pidfile" 2>/dev/null | tr -d '[:space:]')"
    fi
    if [ -z "$pid" ] || ! kill -0 "$pid" 2>/dev/null; then
        pid="$(pgrep -f "$bin run" 2>/dev/null | head -n 1)"
    fi

    if [ -z "$pid" ]; then
        rm -f "$pidfile" 2>/dev/null
        byxcr_log "服务未在运行"
        return 0
    fi

    byxcr_log "停止 byxcr（PID ${pid}）…"
    # SIGINT：byxcr 注册了 Ctrl+C 处理，收到后会取消调度与在途下载并优雅退出
    kill -INT "$pid" 2>/dev/null || true
    i=0
    while [ "$i" -lt 30 ] && kill -0 "$pid" 2>/dev/null; do
        sleep 1
        i=$((i + 1))
    done

    if kill -0 "$pid" 2>/dev/null; then
        kill -TERM "$pid" 2>/dev/null || true
        i=0
        while [ "$i" -lt 10 ] && kill -0 "$pid" 2>/dev/null; do
            sleep 1
            i=$((i + 1))
        done
    fi

    if kill -0 "$pid" 2>/dev/null; then
        byxcr_log "进程未响应，强制结束"
        kill -KILL "$pid" 2>/dev/null || true
    fi

    rm -f "$pidfile" 2>/dev/null
    byxcr_log "已停止"
    return 0
}

# 供 cmd/main status 使用：0=运行中，3=未运行
byxcr_service_status() {
    if byxcr_service_running; then
        return 0
    fi
    return 3
}
