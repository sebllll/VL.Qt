#!/usr/bin/env bash
# Trims runtimes/<rid>/native after tools/deploy-linux.sh: removes Qt parts VL.Qt never loads (Linux counterpart of
# trim-deploy.ps1). Idempotent, arch-neutral. Called by deploy-linux.sh; run order: build-native.sh -> deploy-linux.sh
# (deploy + trim + check-licenses.sh) -> tests.
# Usage: tools/trim-linux.sh [--rid linux-x64|linux-arm64 | --path <native dir>] [--dry-run]
set -euo pipefail

case "$(uname -m)" in aarch64|arm64) RID='linux-arm64' ;; *) RID='linux-x64' ;; esac
NATIVE=''
DRY=0
while [ $# -gt 0 ]; do
    case "$1" in
        --rid) RID="$2"; shift 2 ;;
        --path) NATIVE="$2"; shift 2 ;;
        --dry-run) DRY=1; shift ;;
        -h|--help) sed -n '2,5p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
[ -n "$NATIVE" ] || NATIVE="$ROOT/runtimes/$RID/native"
[ -d "$NATIVE" ] || { echo "$NATIVE missing (run tools/deploy-linux.sh)" >&2; exit 1; }
NATIVE="$(cd "$NATIVE" && pwd)"

# Each entry: path relative to native/ (glob allowed) | reason. Verified against VL.Qt.Probe (offscreen, wayland, xcb).
remove=(
    'plugins/wayland-graphics-integration-client/lib*-server.so|Server-buffer integrations (dmabuf/drm-egl/shm-emulation/vulkan): only used when the compositor itself is a Qt Wayland Compositor sharing buffers with its clients; regular rendering uses qt-plugin-wayland-egl.'
    'plugins/egldeviceintegrations/libqeglfs-emu-integration.so|eglfs backend for the Qt emulator, not a real device.'
    'plugins/egldeviceintegrations/libqeglfs-x11-integration.so|eglfs inside an X11 window (development aid); on X11 VL.Qt uses the xcb platform.'
    'qml/*/plugins.qmltypes|Type metadata for tooling (qmllint/qmlls); the engine loads types from the plugin libraries.'
    'qml/*/*/plugins.qmltypes|same as above, nested modules.'
    'qml/*/*/*/plugins.qmltypes|same as above, nested modules (QtQuick/Controls/Basic, ...).'
    'qml/*/*/*/*/plugins.qmltypes|same as above, nested modules (QtQuick/Controls/Basic/impl).'
    'qml/QtQuick/Controls/*.qml|Loose QML sources; qmldir `prefer :/qt-project.org/...` loads the copies compiled into the plugin libraries (#213).'
    'qml/QtQuick/Controls/*/*.qml|same as above (Basic, impl).'
    'qml/QtQuick/Controls/*/*/*.qml|same as above (Basic/impl).'
)
# Qt Quick Controls styles other than Basic (#213; our own style VLQt is embedded in vlqt and falls back to Basic): deploy-linux.sh already skips them; safety net for older deploys.
for style in Fusion Imagine Material Universal FluentWinUI3 Windows Native macOS iOS; do
    remove+=("qml/QtQuick/Controls/$style|Controls style $style: not used, only Basic is (#213).")
    remove+=("libQt6QuickControls2$style*.so*|Controls style $style libraries (only NEEDED by its style plugin).")
done
remove+=('qml/QtQuick/NativeStyle|Native style base, not used (#213).' 'libQt6QuickNativeStyle.so*|Only NEEDED by the native styles.')
remove+=('qml/QtQuick/Layouts|QtQuick.Layouts: only imported by the removed styles.' 'libQt6QuickLayouts.so*|Only NEEDED by the Layouts plugin and the removed styles.')
# Kept on purpose: all wayland-shell-integration plugins (xdg-shell is the default; fullscreen-shell-v1, ivi-shell,
# qt-shell, wl-shell are what kiosk/embedded compositors may offer, 64 KB each), both decoration plugins, ibus input
# context (DBus is a hard import of Qt6Gui anyway), Svg (adwaita decoration, Image{source: *.svg}).
# NOT removable (NEEDED of kept libs): ICU (Qt6Core), Qt6Network (Qt6Qml/Quick/Multimedia), Qt6DBus (Qt6Gui), FFmpeg.

bytes() { find "$1" -type f -printf '%s\n' | awk '{s += $1} END {printf "%.1f", s / 1048576}'; }
before="$(bytes "$NATIVE")"
count=0
for e in "${remove[@]}"; do
    pat="${e%%|*}"; why="${e#*|}"
    # shellcheck disable=SC2086 # glob on purpose
    for f in "$NATIVE"/$pat; do
        [ -e "$f" ] || continue
        count=$((count + 1))
        if [ "$DRY" = 1 ]; then echo "would remove  ${f#"$NATIVE"/}  ($why)"; else rm -rf "$f"; echo "removed  ${f#"$NATIVE"/}"; fi
    done
done
[ "$DRY" = 1 ] || find "$NATIVE" -mindepth 1 -type d -empty -delete
echo "Trimmed $NATIVE : $count item(s), $before MB -> $(bytes "$NATIVE") MB"
