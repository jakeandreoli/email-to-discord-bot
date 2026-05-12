#!/usr/bin/env bash
set -euo pipefail

VERSION="${VERSION:-1.0.0}"
CONFIGURATION="${CONFIGURATION:-Release}"
RUNTIMES=("win-x64" "linux-x64")
CLEAN=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version)       VERSION="$2"; shift 2 ;;
        --configuration) CONFIGURATION="$2"; shift 2 ;;
        --runtimes)      IFS=',' read -r -a RUNTIMES <<< "$2"; shift 2 ;;
        --clean)         CLEAN=1; shift ;;
        -h|--help)
            cat <<EOF
Usage: $0 [--version 1.2.3] [--configuration Release] [--runtimes win-x64,linux-x64] [--clean]
EOF
            exit 0
            ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$REPO_ROOT/EmailToDiscord"
PROJECT_FILE="$PROJECT_DIR/EmailToDiscord.csproj"
RELEASE_DIR="$REPO_ROOT/release"
STAGING_ROOT="$RELEASE_DIR/staging"

if [[ ! -f "$PROJECT_FILE" ]]; then
    echo "Project file not found at $PROJECT_FILE" >&2
    exit 1
fi

if [[ "$CLEAN" -eq 1 && -d "$RELEASE_DIR" ]]; then
    echo "Cleaning $RELEASE_DIR"
    rm -rf "$RELEASE_DIR"
fi

mkdir -p "$RELEASE_DIR" "$STAGING_ROOT"

for rid in "${RUNTIMES[@]}"; do
    echo
    echo "==> Publishing $rid (framework-dependent)"

    stage_name="EmailToDiscord-$VERSION-$rid"
    stage_dir="$STAGING_ROOT/$stage_name"
    rm -rf "$stage_dir"

    dotnet publish "$PROJECT_FILE" \
        --configuration "$CONFIGURATION" \
        --runtime "$rid" \
        --self-contained false \
        --output "$stage_dir" \
        /p:Version="$VERSION" \
        /p:UseAppHost=true

    if [[ -f "$PROJECT_DIR/config.example.yaml" ]]; then
        cp -f "$PROJECT_DIR/config.example.yaml" "$stage_dir/config.example.yaml"
    fi
    rm -f  "$stage_dir/config.yaml"
    rm -rf "$stage_dir/data" "$stage_dir/logs"

    if [[ "$rid" == win-* ]]; then
        archive="$RELEASE_DIR/EmailToDiscord-$VERSION-$rid.zip"
        rm -f "$archive"
        echo "==> Packaging $archive"
        if command -v zip >/dev/null 2>&1; then
            ( cd "$stage_dir" && zip -qr "$archive" . )
        else
            ( cd "$STAGING_ROOT" && python3 -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', sys.argv[2])" "${archive%.zip}" "$stage_name" )
        fi
    else
        archive_name="EmailToDiscord-$VERSION-$rid.tar.gz"
        archive="$RELEASE_DIR/$archive_name"
        rm -f "$archive"
        echo "==> Packaging $archive"
        ( cd "$STAGING_ROOT" && tar -czf "../$archive_name" "$stage_name" )
    fi
done

rm -rf "$STAGING_ROOT"

echo
echo "Done. Artifacts in $RELEASE_DIR:"
ls -1 "$RELEASE_DIR"
