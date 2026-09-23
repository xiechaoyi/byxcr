#!/bin/bash
# ============================================================================
# byxcr fnOS 应用 · 配置落盘逻辑回归测试
#
# 验证 app/lib/byxcr-env.sh 里「向导值 → ${TRIM_PKGETC}/byxcr.env」这一段的行为，
# 不需要真机、不需要二进制：用伪 TRIM_* 变量指向临时目录即可，
# Linux / macOS / Windows 上的 Git Bash 都能跑。
#
#   bash fnos/tools/test-env.sh
#
# 覆盖：特殊字符往返（令牌/口令里的 ' $ 空格 # " \）、留空沿用既有值、
#       keep 语义、数值夹取边界、枚举白名单、WebDAV 缺地址兜底、注释不污染取值。
# ============================================================================
set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PKG="$(cd "$HERE/.." && pwd)"
LIB="$PKG/app/lib/byxcr-env.sh"

[ -r "$LIB" ] || { echo "找不到 $LIB" >&2; exit 1; }

T="${TMPDIR:-/tmp}/byxcr-envtest.$$"
rm -rf "$T"; mkdir -p "$T"
trap 'rm -rf "$T"' EXIT

export TRIM_APPNAME=byxcr
export TRIM_APPDEST="$PKG/app"
export TRIM_PKGVAR="$T/var"
export TRIM_PKGETC="$T/etc"
# 故意不设 TRIM_DATA_SHARE_PATHS：脚本以 set -u 运行，这一项最容易触发 unbound variable
unset TRIM_DATA_SHARE_PATHS

pass=0
fail=0
check() { # check <描述> <期望> <实际>
    if [ "$2" = "$3" ]; then
        printf '  OK   %s\n' "$1"; pass=$((pass + 1))
    else
        printf '  FAIL %s\n       期望=[%s]\n       实际=[%s]\n' "$1" "$2" "$3"; fail=$((fail + 1))
    fi
}

# 全部在 set -eu 下执行，与设备上的生命周期脚本保持一致
WRITE()  { ( set -eu; . "$LIB"; byxcr_write_env >/dev/null ); }
GET()    { ( set -eu; . "$LIB"; byxcr_env_get "$T/etc/byxcr.env" "$1" ); }
EXPORT() { ( set -eu; . "$LIB"; byxcr_env_export "$T/etc/byxcr.env"; printf '%s' "${!1}" ); }

# 覆盖单引号、$、空格、#、双引号、反斜杠
NASTY='p'"'"'a$s s#w"o\rd'

echo "=== 场景1：首次安装，令牌含特殊字符；周期/并发/级别给非法值 ==="
( set -eu; . "$LIB"
  wizard_api_token="$NASTY"
  wizard_registries="a.example.com, b.example.com"
  wizard_interval="0"
  wizard_max_concurrency="99"
  wizard_schedule_concurrency="abc"
  wizard_log_level="nonsense"
  byxcr_write_env >/dev/null )
sed -n '/^BYXCR_REGISTRIES=/p; /^BYXCR_API_TOKEN=/p' "$T/etc/byxcr.env" | sed 's/^/     /'

check "env_get 原样读回特殊字符" "$NASTY" "$(GET BYXCR_API_TOKEN)"
check "env_export 原样注入"     "$NASTY" "$(EXPORT BYXCR_API_TOKEN)"
check "镜像源归一化（去空格）"   "a.example.com,b.example.com" "$(GET BYXCR_REGISTRIES)"
check "周期 0 被夹到下限 1"      "1" "$(GET BYXCR_SYNC_INTERVAL)"
check "并发 99 被夹到上限 32"    "32" "$(GET BYXCR_MAX_CONCURRENCY)"
check "定时并发非法值回落 4"     "4" "$(GET BYXCR_SCHEDULE_CONCURRENCY)"
check "日志级别非法值回落 info"  "info" "$(GET BYXCR_LOG_LEVEL)"

echo "=== 场景2：不带任何向导变量重写（全部保持原值） ==="
WRITE
check "令牌跨次保留"   "$NASTY" "$(GET BYXCR_API_TOKEN)"
check "镜像源跨次保留" "a.example.com,b.example.com" "$(GET BYXCR_REGISTRIES)"
check "周期跨次保留"   "1" "$(GET BYXCR_SYNC_INTERVAL)"

echo "=== 场景3：向导提交 keep / 留空 ==="
( set -eu; . "$LIB"
  wizard_webdav_enabled="keep" wizard_registries="" wizard_log_level="keep"
  byxcr_write_env >/dev/null )
check "keep 保留布尔开关" "false" "$(GET BYXCR_WEBDAV)"
check "留空保留文本字段"  "a.example.com,b.example.com" "$(GET BYXCR_REGISTRIES)"
check "keep 保留枚举字段" "info" "$(GET BYXCR_LOG_LEVEL)"

echo "=== 场景4：全新安装时开启 WebDAV 却没填地址（自动关闭，且注释不污染取值） ==="
T2="$T/fresh"; mkdir -p "$T2"
( set -eu; . "$LIB"
  export TRIM_PKGVAR="$T2/var" TRIM_PKGETC="$T2/etc"
  wizard_webdav_enabled="true" wizard_webdav_password='pw#1'"'"'2'
  byxcr_write_env >/dev/null )
grep -nE '^(# 注意|BYXCR_WEBDAV)' "$T2/etc/byxcr.env" | sed 's/^/     /'
check "WebDAV 被自动关闭"   "false" "$( ( . "$LIB"; byxcr_env_get "$T2/etc/byxcr.env" BYXCR_WEBDAV ) )"
check "口令未被注释污染"     'pw#1'"'"'2' "$( ( . "$LIB"; byxcr_env_get "$T2/etc/byxcr.env" BYXCR_WEBDAV_PASSWORD ) )"

echo
printf '结果：%d 通过，%d 失败\n' "$pass" "$fail"
[ "$fail" -eq 0 ]
