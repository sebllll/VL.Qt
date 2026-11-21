#!/usr/bin/env bash
# Bundles the Qt runtime next to libvlqt.so in runtimes/<rid>/native (Linux counterpart of windeployqt in build-native.ps1).
# Usage: tools/deploy-linux.sh [--rid linux-x64|linux-arm64] [--qt-dir ~/Qt/6.12.0/<gcc_64|gcc_arm64>] [--clean]
# Run after tools/build-native.sh. Copies Qt libs (NEEDED closure), selected plugins/ and the qml/ modules imported by
# src/VL.Qt.Native/qml/deploy.qml, then sets RUNPATH of every ELF to $ORIGIN-relative paths (patchelf).
# Checks afterwards: no RUNPATH outside the bundle, ldd finds everything; prints the system libs the bundle relies on.
# Finally trims the bundle (tools/trim-linux.sh) and runs the license check (tools/check-licenses.sh); fails if it fails.
set -euo pipefail

QT_VERSION='6.12.0'
# Default RID from the host architecture (x86_64 -> linux-x64, aarch64 -> linux-arm64).
case "$(uname -m)" in aarch64|arm64) RID='linux-arm64' ;; *) RID='linux-x64' ;; esac
QT_DIR=''
CLEAN=0
while [ $# -gt 0 ]; do
    case "$1" in
        --rid) RID="$2"; shift 2 ;;
        --qt-dir) QT_DIR="$2"; shift 2 ;;
        --clean) CLEAN=1; shift ;;
        -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

case "$RID" in
    linux-x64) QT_SUB='gcc_64' ;;
    linux-arm64) QT_SUB='gcc_arm64' ;;
    *) echo "unsupported rid: $RID (supported: linux-x64, linux-arm64)" >&2; exit 2 ;;
esac

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
[ -n "$QT_DIR" ] || QT_DIR="${VLQT_QT_ROOT:-$HOME/Qt}/$QT_VERSION/$QT_SUB"
NATIVE="$ROOT/runtimes/$RID/native"
SO="$NATIVE/libvlqt.so"

fail() { echo "$1" >&2; exit 1; }
[ -f "$QT_DIR/lib/libQt6Core.so.6" ] || fail "Qt not found in $QT_DIR (run tools/setup-qt.sh)"
[ -f "$SO" ] || fail "$SO missing (run tools/build-native.sh)"
GLPROBE="$NATIVE/vlqt-glprobe"
[ -f "$GLPROBE" ] || fail "$GLPROBE missing (run tools/build-native.sh)"
for t in readelf ldd python3; do
    command -v "$t" >/dev/null || fail "$t not found (run tools/setup-wsl.sh)"
done
# patchelf: from PATH (apt package, setup-wsl.sh) or pip into the aqtinstall venv of setup-qt.sh (no sudo needed).
PATCHELF="$(command -v patchelf || true)"
if [ -z "$PATCHELF" ]; then
    VENV="$HOME/.cache/vlqt/aqt-venv"
    [ -x "$VENV/bin/patchelf" ] || { [ -x "$VENV/bin/pip" ] && "$VENV/bin/pip" install -q patchelf; } \
        || fail "patchelf not found (apt install patchelf, or run tools/setup-qt.sh first)"
    PATCHELF="$VENV/bin/patchelf"
fi

# Remove a previous deploy, keep QtNative (and its debug files).
if [ "$CLEAN" = 1 ]; then
    rm -rf "$NATIVE/plugins" "$NATIVE/qml" "$NATIVE/fonts"
    find "$NATIVE" -maxdepth 1 -name 'lib*.so*' ! -name 'libvlqt.so' -delete
fi

# --- plugins: what VL.Qt can load on Linux. Left out on purpose (as in trim-deploy.ps1): tls, networkinformation,
# generic (TUIO), qmltooling, sqldrivers, printsupport, platformthemes (gtk3/xdg portal pull in GTK), designer, help,
# qmllint/qmlls, gstreamer multimedia backend (Video uses FFmpeg, gstreamer would need system gst libs).
plugins=(
    platforms/libqxcb.so platforms/libqwayland.so platforms/libqeglfs.so platforms/libqoffscreen.so
    platforms/libqminimal.so
    'xcbglintegrations/*.so' 'egldeviceintegrations/*.so'
    'wayland-decoration-client/*.so' 'wayland-graphics-integration-client/*.so' 'wayland-shell-integration/*.so'
    'platforminputcontexts/*.so' 'imageformats/*.so' 'iconengines/*.so'
    multimedia/libffmpegmediaplugin.so
)
for p in "${plugins[@]}"; do
    # shellcheck disable=SC2086 # glob on purpose
    for f in "$QT_DIR"/plugins/$p; do
        [ -f "$f" ] || { echo "warning: plugin $p not in Qt, skipped" >&2; continue; }
        rel="${f#"$QT_DIR"/}"
        install -D -m 644 "$f" "$NATIVE/$rel"
    done
done

# --- qml: modules imported by deploy.qml (qmlimportscanner resolves transitive imports). Only the files of each
# module folder itself; nested folders are separate modules (e.g. QtQuick/Controls) and only come if imported.
modules="$("$QT_DIR/libexec/qmlimportscanner" -rootPath "$ROOT/src/VL.Qt.Native/qml" -importPath "$QT_DIR/qml" \
    | python3 -c 'import json,sys
for m in json.load(sys.stdin):
    if m.get("type") == "module" and m.get("relativePath"): print(m["relativePath"])')"
[ -n "$modules" ] || fail "qmlimportscanner found no QML modules"
# Qt Quick Controls (Button, #213): only Basic is deployed: our own style VLQt (embedded in vlqt) uses it as fallback and uses Basic.impl (QQuickStyle::setStyle in app/app.cpp). The other styles
# (optional imports of QtQuick/Controls/qmldir) and what only they import (NativeStyle, Layouts) are not deployed, so
# their libs stay out of the NEEDED closure too. Same list as trim-deploy.ps1 / trim-linux.sh; #298 decides about more.
skip_re='^QtQuick/(Controls/(Fusion|Imagine|Material|Universal|FluentWinUI3|Windows|macOS|iOS|Native)(/.*)?|NativeStyle|Layouts)$'
modules="$(printf '%s\n' $modules | grep -Ev "$skip_re" || true)"
for m in $modules; do
    mkdir -p "$NATIVE/qml/$m"
    find "$QT_DIR/qml/$m" -maxdepth 1 -type f -exec cp -L {} "$NATIVE/qml/$m/" \;
done

# --- libs: NEEDED closure of all ELFs in the bundle, resolved against Qt's lib/ (Qt, ICU, FFmpeg). Copied as real
# files under the NEEDED name (no symlinks: MSBuild/vvvv exports copy them as files anyway).
needed() { readelf -d "$1" 2>/dev/null | sed -n 's/.*(NEEDED).*\[\(.*\)\]/\1/p'; }
elfs() { find "$NATIVE" -type f -name '*.so*'; }
while :; do
    added=0
    while read -r lib; do
        [ -e "$NATIVE/$lib" ] && continue
        [ -e "$QT_DIR/lib/$lib" ] || continue   # system lib
        cp -L "$QT_DIR/lib/$lib" "$NATIVE/$lib"; chmod 644 "$NATIVE/$lib"
        added=1
    done < <(elfs | while read -r f; do needed "$f"; done | sort -u)
    [ "$added" = 1 ] || break
done

# --- fallback font (#427): QtNative registers it only when fontconfig finds no system font (platform/platform_linux.cpp, ensureFonts).
install -D -m 644 "$ROOT/src/VL.Qt.Native/fonts/DejaVuSans.ttf" "$NATIVE/fonts/DejaVuSans.ttf"
install -D -m 644 "$ROOT/LICENSES/DejaVu-Fonts.txt" "$NATIVE/fonts/DejaVu-Fonts-LICENSE.txt"

# --- OpenGL check under WSLg (platform/platform_linux.cpp, probeGlx): vlqt-glprobe, built by build-native.sh next to
# libvlqt.so, needs no Qt and no RUNPATH. A package may lose the executable bit; QtNative then runs it through the
# dynamic loader.
chmod 755 "$GLPROBE"

# --- RUNPATH: $ORIGIN for files in native/, $ORIGIN/../.. for plugins/<cat>/x.so etc. (decision: only $ORIGIN-relative).
while read -r f; do
    rel="${f#"$NATIVE"/}"
    depth="$(awk -F/ '{print NF-1}' <<<"$rel")"
    rp='$ORIGIN'
    for ((i = 0; i < depth; i++)); do rp="$rp/.."; done
    "$PATCHELF" --set-rpath "$rp" "$f"
done < <(elfs)

# --- checks
bad=0
while read -r f; do
    rp="$(readelf -d "$f" | sed -n 's/.*(\(RUNPATH\|RPATH\)).*\[\(.*\)\]/\2/p')"
    case "$rp" in '$ORIGIN'*) ;; *) echo "bad RUNPATH '$rp': $f" >&2; bad=1 ;; esac
    missing="$(env -u LD_LIBRARY_PATH ldd "$f" | grep 'not found' || true)"
    [ -z "$missing" ] || { echo "unresolved in ${f#"$NATIVE"/}:"$'\n'"$missing" >&2; bad=1; }
    leak="$(env -u LD_LIBRARY_PATH ldd "$f" | grep -F "$QT_DIR" || true)"
    [ -z "$leak" ] || { echo "resolves into Qt install, not bundle: ${f#"$NATIVE"/}:"$'\n'"$leak" >&2; bad=1; }
done < <(elfs)
missing="$(env -u LD_LIBRARY_PATH ldd "$GLPROBE" | grep 'not found' || true)"
[ -z "$missing" ] || { echo "unresolved in vlqt-glprobe:"$'\n'"$missing" >&2; bad=1; }
[ "$bad" = 0 ] || fail "deploy check failed"

# System libs the bundle relies on (resolved outside native/), with the Debian/Ubuntu package if dpkg knows it.
echo "System libraries used by the bundle:"
elfs | while read -r f; do env -u LD_LIBRARY_PATH ldd "$f"; done \
    | awk '$2 == "=>" && $3 ~ /^\// {print $3}' | grep -vF "$NATIVE/" | sort -u \
    | while read -r p; do
        pkg="$(dpkg -S "$p" 2>/dev/null | head -1 | cut -d: -f1 || true)"
        [ -n "$pkg" ] || pkg="$(dpkg -S "$(readlink -f "$p")" 2>/dev/null | head -1 | cut -d: -f1 || true)"
        printf '  %-40s %s\n' "$(basename "$p")" "${pkg:-?}"
    done
"$ROOT/tools/trim-linux.sh" --path "$NATIVE"
"$ROOT/tools/check-licenses.sh" --path "$NATIVE"
size="$(du -sh "$NATIVE" | cut -f1)"
echo "Deployed Qt $QT_VERSION to $NATIVE ($(elfs | wc -l) ELF files, $size)"
