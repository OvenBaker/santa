#!/usr/bin/env bash
# Build a self-contained santa binary and symlink it onto PATH.
# Re-run any time after pulling changes — it'll overwrite cleanly.

set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIR="${SANTA_BIN_DIR:-$HOME/.local/bin}"
LINK="$BIN_DIR/santa"
ONNX_FLAVOR="${SANTA_ONNX_RUNTIME_FLAVOR:-gpu}"

if [ "$ONNX_FLAVOR" != "cpu" ] && [ "$ONNX_FLAVOR" != "gpu" ]; then
    echo "error: SANTA_ONNX_RUNTIME_FLAVOR must be cpu or gpu" >&2
    exit 2
fi

# Keep CPU and GPU native runtimes in separate publish directories. Publishing on top of
# the other flavor can otherwise leave stale ONNX provider libraries behind.
DIST="$REPO/dist/$ONNX_FLAVOR"

# One-time state migration for installs predating the santa-claude → santa rename.
STATE_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}"
if [ -d "$STATE_ROOT/santa-claude" ] && [ ! -e "$STATE_ROOT/santa" ]; then
    mv "$STATE_ROOT/santa-claude" "$STATE_ROOT/santa"
    echo "↻ migrated state: $STATE_ROOT/santa-claude → $STATE_ROOT/santa"
fi

# Single-file framework-dependent publish. Self-contained adds ~80MB and the .NET 10 runtime
# is already installed system-wide, so we don't bundle it.
dotnet publish "$REPO/src/Santa.Cli" \
    -c Release \
    -r linux-x64 \
    --self-contained false \
    -p:SantaOnnxRuntimeFlavor="$ONNX_FLAVOR" \
    -p:PublishSingleFile=true \
    -p:DebugType=embedded \
    -o "$DIST" \
    --nologo \
    -v:q

mkdir -p "$BIN_DIR"
ln -sf "$DIST/santa" "$LINK"
# Drop the pre-rename `santa-claude` symlink if an older install left one behind.
rm -f "$BIN_DIR/santa-claude"

echo
echo "✓ installed ($ONNX_FLAVOR): $BIN_DIR/santa -> $DIST/santa"

if ! command -v santa >/dev/null 2>&1; then
    echo
    echo "  $BIN_DIR is not on PATH. Add it to your shell rc:"
    echo "    export PATH=\"\$HOME/.local/bin:\$PATH\""
fi
