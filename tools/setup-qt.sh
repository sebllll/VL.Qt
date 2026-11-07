#!/usr/bin/env bash
# Installs the pinned Qt version via aqtinstall (LGPL modules only): base + Qt Multimedia (Video element, FFmpeg backend).
# Linux counterpart of tools/setup-qt.ps1 (same Qt version and modules).
# Usage: tools/setup-qt.sh [--rid linux-x64|linux-arm64] [--out-dir ~/Qt]   (default out dir: $VLQT_QT_ROOT or ~/Qt)
# aqtinstall runs from a private venv (${XDG_CACHE_HOME:-~/.cache}/vlqt/aqt-venv), so no system pip is needed.
set -euo pipefail

QT_VERSION='6.12.0'
# Default RID from the host architecture (x86_64 -> linux-x64, aarch64 -> linux-arm64).
case "$(uname -m)" in aarch64|arm64) RID='linux-arm64' ;; *) RID='linux-x64' ;; esac
OUT_DIR="${VLQT_QT_ROOT:-$HOME/Qt}"
while [ $# -gt 0 ]; do
    case "$1" in
        --rid) RID="$2"; shift 2 ;;
        --out-dir) OUT_DIR="$2"; shift 2 ;;
        -h|--help) sed -n '2,5p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

case "$RID" in
    linux-x64) QT_HOST='linux'; QT_ARCH='linux_gcc_64'; QT_SUB='gcc_64' ;;
    linux-arm64) QT_HOST='linux_arm64'; QT_ARCH='linux_gcc_arm64'; QT_SUB='gcc_arm64' ;;
    *) echo "unsupported rid: $RID (supported: linux-x64, linux-arm64)" >&2; exit 2 ;;
esac

QT_DIR="$OUT_DIR/$QT_VERSION/$QT_SUB"

# qsb (qtshadertools) bakes the ShaderEffect shaders at build time (#437), never shipped.
has_qsb() { [ -x "$QT_DIR/bin/qsb" ] || [ -x "$QT_DIR/libexec/qsb" ]; }
has_base() { { [ -x "$QT_DIR/bin/qtpaths6" ] || [ -x "$QT_DIR/bin/qtpaths" ]; } && has_qsb; }
# Qt Multimedia ships the FFmpeg libraries (libavcodec.so.*, ...) in lib/ next to libQt6Multimedia.so.
has_multimedia() {
    compgen -G "$QT_DIR/lib/libQt6Multimedia.so*" >/dev/null &&
        [ -f "$QT_DIR/plugins/multimedia/libffmpegmediaplugin.so" ] &&
        compgen -G "$QT_DIR/lib/libavcodec.so*" >/dev/null
}

if has_base && has_multimedia; then
    echo "Qt $QT_VERSION ($RID) already present: $QT_DIR"
else
    venv="${XDG_CACHE_HOME:-$HOME/.cache}/vlqt/aqt-venv"
    if [ ! -x "$venv/bin/python" ]; then
        mkdir -p "$(dirname "$venv")"
        # Ubuntu ships python3 without ensurepip unless python3-venv is installed; bootstrap pip directly then.
        if ! python3 -m venv "$venv" 2>/dev/null; then
            rm -rf "$venv"
            python3 -m venv --without-pip "$venv"
            curl -fsSL https://bootstrap.pypa.io/get-pip.py | "$venv/bin/python" - --quiet
        fi
    fi
    "$venv/bin/python" -m pip install --quiet --upgrade aqtinstall
    # Base install contains qtbase, qtdeclarative (QtQuick, QtQuick.Shapes, qml tool) and qtshadertools;
    # -m adds Qt Multimedia.
    "$venv/bin/python" -m aqt install-qt "$QT_HOST" desktop "$QT_VERSION" "$QT_ARCH" -O "$OUT_DIR" -m qtmultimedia qtshadertools
fi

fail() { echo "$1" >&2; exit 1; }
qtpaths="$QT_DIR/bin/qtpaths6"; [ -x "$qtpaths" ] || qtpaths="$QT_DIR/bin/qtpaths"
v="$("$qtpaths" --qt-version)"
[ "$v" = "$QT_VERSION" ] || fail "qtpaths reports $v, expected $QT_VERSION"
[ -d "$QT_DIR/qml/QtQuick/Shapes" ] || fail 'QtQuick.Shapes missing'
[ -f "$QT_DIR/qml/QtMultimedia/qmldir" ] || fail 'QtMultimedia missing'
[ -f "$QT_DIR/plugins/multimedia/libffmpegmediaplugin.so" ] || fail 'Qt Multimedia FFmpeg plugin missing'
compgen -G "$QT_DIR/lib/libavcodec.so*" >/dev/null || fail 'FFmpeg libraries (libavcodec.so*) missing in lib'
has_qsb || fail 'qsb missing (qtshadertools)'
[ -x "$QT_DIR/bin/qml" ] || fail 'qml tool missing (qtdeclarative)'
echo "Qt $v OK: $QT_DIR"
