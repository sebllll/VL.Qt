#!/usr/bin/env bash
# Verifies that the Linux runtime bundle contains only LGPL-compatible Qt parts (whitelist; unknown => fail).
# Linux counterpart of check-licenses.ps1 (same module lists, .so instead of .dll). Called by deploy-linux.sh.
# Usage: tools/check-licenses.sh [--rid linux-x64|linux-arm64 | --path <native dir>] [--skip-docs]
# Without --rid/--path every runtimes/linux-*/native is checked. Exit code 1 on any violation.
set -euo pipefail

RID=''
NATIVE=''
SKIP_DOCS=0
while [ $# -gt 0 ]; do
    case "$1" in
        --rid) RID="$2"; shift 2 ;;
        --path) NATIVE="$2"; shift 2 ;;
        --skip-docs) SKIP_DOCS=1; shift ;;
        -h|--help) sed -n '2,5p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# --- Whitelist (LGPL-3.0 / permissive). Extend only after checking the module's license on qt.io. ---
# Same Qt modules as check-licenses.ps1, plus the Linux platform-support libraries of qtbase/qtwayland (LGPL-3.0).
qt_allowed=' Core Gui Network OpenGL Qml QmlMeta QmlModels QmlWorkerScript QmlCore QmlLocalStorage QmlXmlListModel
  Quick QuickShapes QuickEffects QuickLayouts QuickTemplates2 QuickControls2 QuickControls2Basic QuickControls2Impl QuickControls2BasicStyleImpl
  ShaderTools Svg Concurrent Multimedia MultimediaQuick
  DBus XcbQpa WaylandClient WlShellIntegration EglFSDeviceIntegration EglFsKmsSupport EglFsKmsGbmSupport '
# Known GPL-only/commercial modules: listed only to give a precise message (everything not whitelisted fails anyway).
qt_denied='^libQt6(Quick3D[A-Za-z]*|Charts[A-Za-z]*|DataVisualization[A-Za-z]*|Graphs[A-Za-z]*|QuickTimeline[A-Za-z]*|VirtualKeyboard[A-Za-z]*|Mqtt|Coap|Opcua[A-Za-z]*|WaylandCompositor[A-Za-z]*)\.so'
# Qt Multimedia's dlopen stubs for OpenSSL/VA-API (part of Qt Multimedia, LGPL-3.0).
qt_stub='^libQt6FFmpegStub-(crypto|ssl|va|va-drm|va-x11)\.so\.[0-9]+$'
# ICU shipped with Qt's Linux binaries (Qt6Core NEEDED): Unicode License, permissive.
icu='^libicu(data|i18n|uc)\.so\.[0-9]+$'
# FFmpeg of Qt Multimedia: LGPL-2.1-or-later only, must carry the license string and no --enable-gpl/--enable-nonfree.
ffmpeg='^lib(avcodec|avformat|avutil|swresample|swscale)\.so\.[0-9]+$'
other_allowed=' libvlqt.so '
# Plugin dirs (relative to native/plugins) and QML modules (relative to native/qml).
plugin_dirs=' platforms imageformats iconengines generic tls networkinformation qmltooling multimedia
  xcbglintegrations egldeviceintegrations platforminputcontexts
  wayland-decoration-client wayland-graphics-integration-client wayland-shell-integration '
qml_modules=' QML QtQml QtQml/Models QtQml/WorkerScript QtQuick QtQuick/Shapes QtQuick/Effects QtQuick/Layouts
  QtQuick/Templates QtQuick/Window QtMultimedia QtQuick/Controls QtQuick/Controls/impl QtQuick/Controls/Basic
  QtQuick/Controls/Basic/impl '
required_docs='LICENSE LICENSES.md LICENSES/LGPL-3.0.txt LICENSES/LGPL-2.1.txt LICENSES/GPL-3.0.txt LICENSES/Qt-NOTICE.md LICENSES/ICU.txt LICENSES/DejaVu-Fonts.txt'

errors=0
fail() { echo "FAIL  $1" >&2; errors=$((errors + 1)); }
in_list() { case "$2" in *[[:space:]]"$1"[[:space:]]*) return 0 ;; *) return 1 ;; esac; }

if [ -n "$NATIVE" ]; then natives=("$NATIVE")
elif [ -n "$RID" ]; then natives=("$ROOT/runtimes/$RID/native")
else natives=(); for d in "$ROOT"/runtimes/linux-*/native; do [ -d "$d" ] && natives+=("$d"); done
fi
[ "${#natives[@]}" -gt 0 ] || { echo "No runtimes/linux-*/native directory found" >&2; exit 1; }

for n in "${natives[@]}"; do
    [ -d "$n" ] || { fail "missing $n"; continue; }
    n="$(cd "$n" && pwd)"
    echo "Checking $n"
    while read -r f; do
        rel="${f#"$n"/}"; name="${rel##*/}"; dir="${rel%/*}"; [ "$dir" = "$rel" ] && dir=''
        if [[ "$name" =~ ^libQt6 ]]; then
            if [ -n "$dir" ]; then fail "$rel : Qt library outside the native root"
            elif [[ "$name" =~ $qt_denied ]]; then fail "$rel : GPL-only/commercial Qt module"
            elif [[ "$name" =~ $qt_stub ]]; then :
            elif [[ "$name" =~ ^libQt6([A-Za-z0-9]+)\.so\.6$ ]] && in_list "${BASH_REMATCH[1]}" "$qt_allowed"; then :
            else fail "$rel : Qt module not on the whitelist"
            fi
        elif [ -z "$dir" ] && [[ "$name" =~ $ffmpeg ]]; then
            grep -qF 'LGPL version 2.1 or later' "$f" || fail "$rel : FFmpeg library without the LGPL-2.1+ license string"
            ! grep -qE -- '--enable-(gpl|nonfree)' "$f" || fail "$rel : FFmpeg built with --enable-gpl/--enable-nonfree"
        elif [ -z "$dir" ]; then
            [[ "$name" =~ $icu ]] || in_list "$name" "$other_allowed" || fail "$rel : unknown library in native root"
        elif [[ "$dir" == plugins/* ]]; then
            top="${dir#plugins/}"; top="${top%%/*}"
            in_list "$top" "$plugin_dirs" || fail "$rel : plugin directory '$top' not on the whitelist"
        elif [[ "$dir" == qml/* ]]; then
            in_list "${dir#qml/}" "$qml_modules" || fail "$rel : QML module '${dir#qml/}' not on the whitelist"
        else fail "$rel : library in unexpected location"
        fi
    done < <(find "$n" \( -type f -o -type l \) -name '*.so*' | sort)
    # Module folders without a library (qmldir only) are checked too.
    while read -r q; do
        mod="${q#"$n"/qml/}"; mod="${mod%/qmldir}"
        in_list "$mod" "$qml_modules" || fail "qml/$mod : QML module not on the whitelist"
    done < <(find "$n/qml" -name qmldir 2>/dev/null)
    [ -f "$n/libvlqt.so" ] || fail 'libvlqt.so missing'
done
if [ "$SKIP_DOCS" = 0 ]; then
    for d in $required_docs; do [ -f "$ROOT/$d" ] || fail "missing license file: $d"; done
fi
[ "$errors" = 0 ] || { echo "$errors license check violation(s)" >&2; exit 1; }
echo 'License check OK'
