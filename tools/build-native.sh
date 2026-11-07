#!/usr/bin/env bash
# Builds QtNative as libvlqt.so into runtimes/<rid>/native (Linux counterpart of tools/build-native.ps1).
# Usage: tools/build-native.sh [--rid linux-x64|linux-arm64] [--qt-dir ~/Qt/6.12.0/<gcc_64|gcc_arm64>] [--build-dir build/<rid>] [--clean]
#                              [--sanitize asan|tsan]
# Configures CMake directly (same settings as the dev-linux-<arch> presets). Qt root: $VLQT_QT_ROOT or ~/Qt.
# --sanitize: QtNative with ASan/TSan (RelWithDebInfo, presets linux-<arch>-asan/-tsan) into build/<rid>-<san>/native,
#   runtimes/ stays untouched (only libvlqt.so, no Qt deploy; tools/test-linux.sh --sanitize swaps it into a copy).
# Checks afterwards: only vlqt_* symbols exported (nm -D), RUNPATH starts with $ORIGIN (readelf -d).
# Qt deploy next to the .so: tools/deploy-linux.sh (run again after every build, the link resets RUNPATH).
set -euo pipefail

QT_VERSION='6.12.0'
# Default RID from the host architecture (x86_64 -> linux-x64, aarch64 -> linux-arm64).
case "$(uname -m)" in aarch64|arm64) RID='linux-arm64' ;; *) RID='linux-x64' ;; esac
QT_DIR=''
BUILD_DIR=''
CLEAN=0
SANITIZE=''
while [ $# -gt 0 ]; do
    case "$1" in
        --rid) RID="$2"; shift 2 ;;
        --qt-dir) QT_DIR="$2"; shift 2 ;;
        --build-dir) BUILD_DIR="$2"; shift 2 ;;
        --clean) CLEAN=1; shift ;;
        --sanitize) SANITIZE="$2"; shift 2 ;;
        -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

case "$RID" in
    linux-x64) QT_SUB='gcc_64' ;;
    linux-arm64) QT_SUB='gcc_arm64' ;;
    *) echo "unsupported rid: $RID (supported: linux-x64, linux-arm64)" >&2; exit 2 ;;
esac

case "$SANITIZE" in
    '') SAN_FLAG=''; BUILD_TYPE='Release' ;;
    asan) SAN_FLAG='address'; BUILD_TYPE='RelWithDebInfo' ;;
    tsan) SAN_FLAG='thread'; BUILD_TYPE='RelWithDebInfo' ;;
    *) echo "unsupported sanitizer: $SANITIZE (supported: asan, tsan)" >&2; exit 2 ;;
esac

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/src/VL.Qt.Native"
[ -n "$QT_DIR" ] || QT_DIR="${VLQT_QT_ROOT:-$HOME/Qt}/$QT_VERSION/$QT_SUB"
if [ -z "$SANITIZE" ]; then
    [ -n "$BUILD_DIR" ] || BUILD_DIR="$ROOT/build/$RID"
    OUT_DIR="$ROOT/runtimes/$RID/native"
    SAN_ARGS=(-DVLQT_SANITIZE=)
else
    [ -n "$BUILD_DIR" ] || BUILD_DIR="$ROOT/build/$RID-$SANITIZE"
    OUT_DIR="$BUILD_DIR/native"
    # RUNPATH exactly $ORIGIN (no Qt lib dir from the build tree): the .so is used next to the deployed Qt bundle.
    SAN_ARGS=(-DVLQT_SANITIZE="$SAN_FLAG" -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON)
fi

fail() { echo "$1" >&2; exit 1; }
[ -f "$QT_DIR/lib/cmake/Qt6/Qt6Config.cmake" ] || fail "Qt not found in $QT_DIR (run tools/setup-qt.sh)"
for t in cmake ninja nm readelf; do
    command -v "$t" >/dev/null || fail "$t not found (run tools/setup-wsl.sh)"
done

[ "$CLEAN" = 0 ] || rm -rf "$BUILD_DIR"
cmake -S "$SRC" -B "$BUILD_DIR" -G Ninja -DCMAKE_BUILD_TYPE="$BUILD_TYPE" \
    -DCMAKE_PREFIX_PATH="$QT_DIR" -DVLQT_RID="$RID" -DVLQT_OUT_DIR="$OUT_DIR" "${SAN_ARGS[@]}"
cmake --build "$BUILD_DIR"

SO="$OUT_DIR/libvlqt.so"
[ -f "$SO" ] || fail "build produced no $SO"
# Exported (defined, dynamic) symbols must all be part of the C ABI in vlqt.h.
foreign="$(nm -D --defined-only "$SO" | awk '{print $NF}' | grep -v '^vlqt_' || true)"
[ -z "$foreign" ] || fail "unexpected exports in $SO:"$'\n'"$foreign"
count="$(nm -D --defined-only "$SO" | grep -c ' vlqt_' || true)"
runpath="$(readelf -d "$SO" | sed -n 's/.*(\(RUNPATH\|RPATH\)).*\[\(.*\)\]/\2/p')"
case "$runpath" in
    '$ORIGIN'*) ;;
    *) fail "RUNPATH of $SO does not start with \$ORIGIN: '$runpath'" ;;
esac
echo "Built $SO ($count vlqt_* exports, RUNPATH $runpath${SANITIZE:+, sanitizer $SANITIZE})"
