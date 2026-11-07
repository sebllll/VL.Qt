#!/usr/bin/env bash
# WSLg/Qt check: runs a small test QML with Qt's qml tool, first headless (QT_QPA_PLATFORM=offscreen), then as a
# visible window on the default platform (WSLg: wayland, xcb as fallback).
# Usage: tools/check-wslg.sh [--qt-dir ~/Qt/6.12.0/<gcc_64|gcc_arm64>] [--seconds 10] [--platform wayland|xcb] [--gpu] [--offscreen-only]
# --gpu: WSLg only, sets GALLIUM_DRIVER=d3d12 (host GPU via Mesa d3d12 instead of llvmpipe). Qt root: $VLQT_QT_ROOT or ~/Qt.
# Exit 0 = offscreen frame rendered and the window run ended without error (the window itself must be checked by eye).
set -euo pipefail

case "$(uname -m)" in aarch64|arm64) QT_SUB='gcc_arm64' ;; *) QT_SUB='gcc_64' ;; esac
QT_DIR="${VLQT_QT_ROOT:-$HOME/Qt}/6.12.0/$QT_SUB"
GPU=0
SECONDS_OPEN=10
PLATFORM=''
OFFSCREEN_ONLY=0
while [ $# -gt 0 ]; do
    case "$1" in
        --qt-dir) QT_DIR="$2"; shift 2 ;;
        --seconds) SECONDS_OPEN="$2"; shift 2 ;;
        --platform) PLATFORM="$2"; shift 2 ;;
        --gpu) GPU=1; shift ;;
        --offscreen-only) OFFSCREEN_ONLY=1; shift ;;
        -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

QML="$QT_DIR/bin/qml"
[ -x "$QML" ] || { echo "qml tool not found in $QT_DIR/bin (run tools/setup-qt.sh)" >&2; exit 1; }

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cat > "$tmp/check.qml" <<'EOF'
import QtQuick
import QtQuick.Shapes

Window {
    id: win
    width: 640; height: 360; visible: true
    title: "VL.Qt WSLg check"
    color: "#202028"
    property int frames: 0
    property int seconds: Number(Qt.application.arguments[Qt.application.arguments.length - 1]) || 10
    onFrameSwapped: if (++frames === 1) console.log("VLQT_CHECK first frame, platform=" + Qt.platform.pluginName)
    Shape {
        anchors.centerIn: parent
        width: 160; height: 160
        RotationAnimation on rotation { from: 0; to: 360; duration: 2000; loops: Animation.Infinite }
        ShapePath {
            fillColor: "#ff4f7b"; strokeColor: "white"; strokeWidth: 4
            startX: 80; startY: 0
            PathLine { x: 160; y: 160 }
            PathLine { x: 0; y: 160 }
            PathLine { x: 80; y: 0 }
        }
    }
    Text {
        anchors { horizontalCenter: parent.horizontalCenter; bottom: parent.bottom; bottomMargin: 16 }
        color: "white"; font.pixelSize: 20
        text: Qt.platform.pluginName + " / frames " + win.frames
    }
    Timer { interval: win.seconds * 1000; running: true; onTriggered: Qt.quit() }
}
EOF

# Offscreen: must render at least one frame within a few seconds.
echo "== offscreen"
log="$tmp/offscreen.log"
QT_QPA_PLATFORM=offscreen QSG_INFO=1 timeout 20 "$QML" "$tmp/check.qml" -- 3 >"$log" 2>&1 || true
grep -E 'VLQT_CHECK|Loading backend|QRhi with backend' "$log" | head -8 || true
grep -q 'VLQT_CHECK first frame' "$log" || { echo "offscreen: no frame rendered" >&2; cat "$log" >&2; exit 1; }
echo "offscreen OK"
[ "$OFFSCREEN_ONLY" = 1 ] && exit 0

echo "== window (${PLATFORM:-default platform}, ${SECONDS_OPEN}s): a dark window with a rotating pink triangle should appear"
echo "   DISPLAY=${DISPLAY:-} WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-}"
log="$tmp/window.log"
rc=0
gpu_env=(); [ "$GPU" = 1 ] && gpu_env=(GALLIUM_DRIVER=d3d12)
env ${PLATFORM:+QT_QPA_PLATFORM=$PLATFORM} "${gpu_env[@]}" QSG_INFO=1 timeout $((SECONDS_OPEN + 20)) "$QML" "$tmp/check.qml" -- "$SECONDS_OPEN" >"$log" 2>&1 || rc=$?
grep -E 'VLQT_CHECK|QRhi with backend|RENDERER:|Physical device' "$log" | head -8 || true
if grep -q 'llvmpipe' "$log"; then
    echo "   note: software rendering (llvmpipe). On WSLg, --gpu (GALLIUM_DRIVER=d3d12) selects the host GPU via Mesa d3d12."
fi
if [ "$rc" != 0 ] || ! grep -q 'VLQT_CHECK first frame' "$log"; then
    echo "window: failed (exit $rc)" >&2; cat "$log" >&2; exit 1
fi
echo "window run OK (confirm visually that the window appeared)"
