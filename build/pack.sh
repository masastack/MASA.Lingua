#!/usr/bin/env bash
set -euo pipefail


export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

log() { printf '[pack] %s\n' "$*"; }
warn() { printf '[pack] WARN: %s\n' "$*" >&2; }
die() { printf '[pack] ERROR: %s\n' "$*" >&2; exit 1; }

VERSION_ARG="${1:-}"
PUSH_ARG="${2:-}"

VERSION="${VERSION_ARG:-${VERSION:-}}"
PUSH_FLAG="$PUSH_ARG"

[ -n "$VERSION" ] || die "version is required: pass as \$1 or set VERSION"
[[ $VERSION =~ ^[0-9]+(\.[0-9]+)+([-+][0-9A-Za-z.-]+)?$ ]] \
    || die "invalid version '$VERSION' (expected e.g. 1.2.0 or 1.2.0-beta.1, without leading 'v')"

# ── 默认值（均可用同名环境变量覆盖）──────────────────────────────────────────
: "${ARTIFACTS_DIR:=artifacts}"
: "${BUILD_CONFIG:=Release}"
: "${SOLUTION:=}"
: "${NUGET_SOURCE:=https://api.nuget.org/v3/index.json}"
: "${NUGET_API_KEY:=}"
: "${NUGET_SKIP_EXISTS:=true}"
: "${DOTNET:=dotnet}"
: "${EXTRA_ARGS:=}"
: "${PUSH:=false}"

if ! declare -p PROJECTS >/dev/null 2>&1; then
    PROJECTS=""
fi

DO_PUSH="$PUSH"
if [ "$PUSH_FLAG" = "push" ]; then
    DO_PUSH=true
fi

EXTRA_ARGS_ARR=()
if [ -n "$EXTRA_ARGS" ]; then
    read -r -a EXTRA_ARGS_ARR <<< "$EXTRA_ARGS"
fi

# ── 解决方案：SOLUTION 显式指定，缺省则要求当前目录只有一个 ──────────────────
if [ -z "$SOLUTION" ]; then
    mapfile -t _slns < <(find . -maxdepth 1 \( -name '*.slnx' -o -name '*.sln' \) -print | sort)
    [ ${#_slns[@]} -gt 0 ] || die "no .sln/.slnx in cwd; set SOLUTION"
    [ ${#_slns[@]} -eq 1 ] || die "multiple solutions found (${_slns[*]}); set SOLUTION explicitly"
    SOLUTION="${_slns[0]}"
    warn "SOLUTION not set, auto-detected: $SOLUTION"
fi
[ -f "$SOLUTION" ] || die "solution not found: $SOLUTION"

# ── 项目列表：PROJECTS 支持字符串（逗号/空格分隔）或数组 ─────────────────────
PROJECT_LIST=()
if declare -p PROJECTS 2>/dev/null | grep -q '^declare -a'; then
    PROJECT_LIST=("${PROJECTS[@]}")
elif [ -n "$PROJECTS" ]; then
    read -r -a PROJECT_LIST <<< "${PROJECTS//,/ }"
fi

if [ ${#PROJECT_LIST[@]} -eq 0 ]; then
    mapfile -t PROJECT_LIST < <(
        find . -name '*.csproj' \
            -not -path '*/bin/*' -not -path '*/obj/*' \
            -not -path "*/${ARTIFACTS_DIR%/}/*" \
            -not -path '*/tests/*' -not -path '*/test/*' \
            -not -name '*.Tests.csproj' -not -name '*.Test.csproj' \
            | sort
    )
    [ ${#PROJECT_LIST[@]} -gt 0 ] || die "no project to pack; set PROJECTS"
    warn "PROJECTS not set, auto-detected ${#PROJECT_LIST[@]} project(s)"
fi

for proj in "${PROJECT_LIST[@]}"; do
    [ -f "$proj" ] || die "project not found: $proj"
done

log "version  : $VERSION"
log "solution : $SOLUTION"
log "config   : $BUILD_CONFIG"
log "projects : ${#PROJECT_LIST[@]}"
printf '[pack]            %s\n' "${PROJECT_LIST[@]}"

# ── 构建 ─────────────────────────────────────────────────────────────────────
log "building..."
BUILD_CMD=("$DOTNET" build "$SOLUTION" -c "$BUILD_CONFIG" "-p:Version=$VERSION")
if [ ${#EXTRA_ARGS_ARR[@]} -gt 0 ]; then
    BUILD_CMD+=("${EXTRA_ARGS_ARR[@]}")
fi
"${BUILD_CMD[@]}" || die "build failed"

# ── 准备输出目录 ─────────────────────────────────────────────────────────────
case "$ARTIFACTS_DIR" in
    ""|"/"|"."|".."|"~"|"$HOME"|"$HOME/") die "refusing to clean ARTIFACTS_DIR='$ARTIFACTS_DIR'" ;;
esac
rm -rf -- "$ARTIFACTS_DIR"
mkdir -p -- "$ARTIFACTS_DIR"

# ── 打包 ─────────────────────────────────────────────────────────────────────
log "packing..."
for proj in "${PROJECT_LIST[@]}"; do
    PACK_CMD=("$DOTNET" pack "$proj" -c "$BUILD_CONFIG" --no-build "-p:Version=$VERSION" -o "$ARTIFACTS_DIR")
    if [ ${#EXTRA_ARGS_ARR[@]} -gt 0 ]; then
        PACK_CMD+=("${EXTRA_ARGS_ARR[@]}")
    fi
    "${PACK_CMD[@]}" || die "pack failed: $proj"
done

shopt -s nullglob
PACKAGES=("$ARTIFACTS_DIR"/*.nupkg)
shopt -u nullglob
[ ${#PACKAGES[@]} -gt 0 ] || die "no .nupkg produced in $ARTIFACTS_DIR"

log "generated ${#PACKAGES[@]} package(s):"
ls -lh -- "${PACKAGES[@]}"

# ── 推送 ─────────────────────────────────────────────────────────────────────
if [ "$DO_PUSH" = "true" ]; then
    [ -n "$NUGET_API_KEY" ] || die "NUGET_API_KEY is required to push"
    [ -n "$NUGET_SOURCE" ] || die "NUGET_SOURCE is required to push"
    log "pushing to $NUGET_SOURCE ..."

    for pkg in "${PACKAGES[@]}"; do
        name=$(basename "$pkg")
        body=$(mktemp)
        code=$(curl -sS -o "$body" -w '%{http_code}' \
                    -X PUT -T "$pkg" \
                    -H "X-NuGet-ApiKey: $NUGET_API_KEY" \
                    "$NUGET_SOURCE") || { cat "$body" >&2; rm -f "$body"; die "curl failed: $name"; }

        case "$code" in
            2??)
                log "pushed  $name ($code)"
                ;;
            409)
                if [ "$NUGET_SKIP_EXISTS" = "true" ]; then
                    warn "already exists, skipped: $name (HTTP 409)"
                else
                    cat "$body" >&2
                    rm -f "$body"
                    die "package already exists: $name"
                fi
                ;;
            *)
                cat "$body" >&2
                rm -f "$body"
                die "push failed: $name (HTTP $code)"
                ;;
        esac
        rm -f "$body"
    done
fi

log "done -> $ARTIFACTS_DIR"
