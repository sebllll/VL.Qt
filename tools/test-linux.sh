#!/usr/bin/env bash
# Builds and runs all Linux tests in one go (L4a): QtNative + Qt bundle, SmokeTest, dotnet test (VL.Qt.Tests).
# Usage: tools/test-linux.sh [--rid linux-x64|linux-arm64] [--qt-dir <dir>] [--platform offscreen|wayland|xcb|auto]
#                            [--software] [--skip-native] [--no-smoke] [--no-unit] [--out <dir>] [--sanitize asan|tsan]
#                            [-- <SmokeTest args>]
# auto = QT_QPA_PLATFORM unset, QtNative decides (WSLg: xcb + d3d12, like an exported patch, #216).
# Default RID from the host architecture (uname -m), default platform offscreen (headless, CPU rendering via llvmpipe:
# VLQT_GPU=software, the GPU rule does not apply to these functional tests). SmokeTest args e.g. --perf-warn-only.
# offscreen default: QT_QUICK_BACKEND=rhi (OpenGL via GLX, needs an X server, WSLg has one).
# --software: Qt Quick software backend, no X server/xvfb needed (CI, GPU-less systems); MultiEffect/video pixel checks SKIP.
# .NET: dotnet from PATH or ~/.dotnet (tools/setup-wsl.sh). Exit 0 = everything green.
# --sanitize asan|tsan (#365): additionally builds QtNative with ASan/TSan (build/<rid>-<san>, runtimes/ untouched),
#   swaps it into a copy of the SmokeTest output and runs the SmokeTest with the sanitizer runtime per LD_PRELOAD
#   (dotnet is not instrumented). Reports in <out>/<san>.<pid>, suppressions tools/sanitizers/. No dotnet test.
#   e.g. tools/test-linux.sh --sanitize tsan -- --only resources,image-resource
set -euo pipefail

case "$(uname -m)" in aarch64|arm64) RID='linux-arm64' ;; *) RID='linux-x64' ;; esac
QT_ARGS=()
PLATFORM='offscreen'
SOFTWARE=0
SKIP_NATIVE=0
SMOKE=1
UNIT=1
OUT=''
SMOKE_ARGS=()
SANITIZE=''
while [ $# -gt 0 ]; do
    case "$1" in
        --rid) RID="$2"; shift 2 ;;
        --qt-dir) QT_ARGS=(--qt-dir "$2"); shift 2 ;;
        --platform) PLATFORM="$2"; shift 2 ;;
        --software) SOFTWARE=1; shift ;;
        --skip-native) SKIP_NATIVE=1; shift ;;
        --no-smoke) SMOKE=0; shift ;;
        --no-unit) UNIT=0; shift ;;
        --out) OUT="$2"; shift 2 ;;
        --sanitize) SANITIZE="$2"; shift 2 ;;
        --) shift; SMOKE_ARGS=("$@"); break ;;
        -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done
case "$RID" in linux-x64|linux-arm64) ;; *) echo "unsupported rid: $RID" >&2; exit 2 ;; esac
case "$SANITIZE" in
    '') ;;
    # dotnet test does not load the sanitized QtNative copy; perf limits only warn (TSan 5-15x slower, ASan ~2x)
    asan|tsan) UNIT=0; SMOKE_ARGS=(--perf-warn-only "${SMOKE_ARGS[@]}") ;;
    *) echo "unsupported sanitizer: $SANITIZE (supported: asan, tsan)" >&2; exit 2 ;;
esac

# #216: Wayland + Mesa d3d12 hangs WSLg at the first window (process in state D, whole WSL VM unresponsive,
# only `wsl --shutdown` helps). Check before anything is built or started. d3d12 is active when GALLIUM_DRIVER=d3d12
# or, unset, when QtNative picks it (WSLg, no VLQT_GPU=software; --software sets VLQT_GPU=software below).
case "$PLATFORM" in
    wayland*)
        if [ -e /dev/dxg ]; then
            GPU_SOFT="${VLQT_GPU:-}"
            [ "$SOFTWARE" = 0 ] || GPU_SOFT="${VLQT_GPU:-software}"
            if [ "${GALLIUM_DRIVER:-}" = d3d12 ] || { [ -z "${GALLIUM_DRIVER:-}" ] && [ "$GPU_SOFT" != software ]; }; then
                echo "ABORT: --platform $PLATFORM with Mesa d3d12 under WSLg hangs the whole WSL VM (#216)." >&2
                echo "       Use GALLIUM_DRIVER=llvmpipe tools/test-linux.sh --platform $PLATFORM, or --platform xcb." >&2
                exit 3
            fi
        fi ;;
esac

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ART="$ROOT/build/dotnet-$RID" # own obj/bin tree: the Windows build of the same checkout keeps its obj/ (other TFM)
SUFFIX=''
[ "$SOFTWARE" = 0 ] || SUFFIX='-software'
[ -z "$SANITIZE" ] || SUFFIX="$SUFFIX-$SANITIZE"
[ -n "$OUT" ] || OUT="$ROOT/build/test-linux-$RID/$PLATFORM$SUFFIX"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)" # absolute: the sanitizers' log_path and the smoke subshell (cd) need it

if ! command -v dotnet >/dev/null 2>&1; then
    [ -x "$HOME/.dotnet/dotnet" ] || { echo "dotnet not found (run tools/setup-wsl.sh)" >&2; exit 1; }
    export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

step() { echo "== $*"; }
fail=0

if [ "$SKIP_NATIVE" = 0 ]; then
    step "QtNative + Qt bundle ($RID)"
    "$ROOT/tools/build-native.sh" --rid "$RID" "${QT_ARGS[@]}" > "$OUT/build-native.log" 2>&1 \
        || { tail -20 "$OUT/build-native.log" >&2; exit 1; }
    "$ROOT/tools/deploy-linux.sh" --rid "$RID" "${QT_ARGS[@]}" > "$OUT/deploy-linux.log" 2>&1 \
        || { tail -20 "$OUT/deploy-linux.log" >&2; exit 1; }
    tail -1 "$OUT/deploy-linux.log"
    if [ -n "$SANITIZE" ]; then
        step "QtNative with $SANITIZE ($RID)"
        "$ROOT/tools/build-native.sh" --rid "$RID" "${QT_ARGS[@]}" --sanitize "$SANITIZE" > "$OUT/build-native-$SANITIZE.log" 2>&1             || { tail -20 "$OUT/build-native-$SANITIZE.log" >&2; exit 1; }
        tail -1 "$OUT/build-native-$SANITIZE.log"
    fi
fi
[ -f "$ROOT/runtimes/$RID/native/libvlqt.so" ] || { echo "runtimes/$RID/native/libvlqt.so missing (drop --skip-native)" >&2; exit 1; }
SAN_SO="$ROOT/build/$RID-$SANITIZE/native/libvlqt.so"
[ -z "$SANITIZE" ] || [ -f "$SAN_SO" ] || { echo "$SAN_SO missing (drop --skip-native)" >&2; exit 1; }

# Headless: Qt platform offscreen, no GPU forcing by QtNative (d3d12 under WSL), synthetic test input only.
if [ "$PLATFORM" = auto ]; then unset QT_QPA_PLATFORM; else export QT_QPA_PLATFORM="$PLATFORM"; fi
if [ "$SOFTWARE" = 1 ]; then
    # Qt Quick software backend: no GL, no X server. The SmokeTest checks that this backend is active
    # (VLQT_TEST_QUICK_BACKEND) and reports checks it cannot pass in principle (MultiEffect, video pixels) as SKIP.
    unset QT_QUICK_BACKEND
    export VLQT_GPU="${VLQT_GPU:-software}" VLQT_TEST_QUICK_BACKEND=software
elif [ "$PLATFORM" = offscreen ]; then
    export VLQT_GPU="${VLQT_GPU:-software}"
    # offscreen reports no RHI support, Qt Quick would pick its software backend (no ShaderEffect/MultiEffect, no
    # video). QT_QUICK_BACKEND=rhi keeps OpenGL; the offscreen plugin gets GL via GLX (needs an X server: WSLg's
    # XWayland, otherwise use --software).
    export QT_QUICK_BACKEND="${QT_QUICK_BACKEND:-rhi}"
    [ -n "${DISPLAY:-}" ] || echo "WARN no DISPLAY: offscreen has no OpenGL without an X server (use --software)" >&2
fi
# QtNative must come from the bundle, not from a Qt installation on LD_LIBRARY_PATH.
unset LD_LIBRARY_PATH
# Log the active scenegraph/RHI backend and GL driver (qt.scenegraph.general/qt.rhi.general info, L4b).
export QSG_INFO="${QSG_INFO:-1}"

if [ "$SMOKE" = 1 ]; then
    step "SmokeTest ($RID, QT_QPA_PLATFORM=$PLATFORM, QT_QUICK_BACKEND=${QT_QUICK_BACKEND:-<default>})"
    dotnet build "$ROOT/src/VL.Qt.SmokeTest" -c Release -v q -nologo -p:RuntimeIdentifier="$RID" \
        --artifacts-path "$ART" > "$OUT/smoke-build.log" 2>&1 || { grep -E 'error|Error' "$OUT/smoke-build.log" | head -20 >&2; exit 1; }
    SMOKE_EXE="$(find "$ART/bin/VL.Qt.SmokeTest" -name VL.Qt.SmokeTest.dll -path '*release*' | head -1)"
    [ -n "$SMOKE_EXE" ] || { echo "SmokeTest build output not found under $ART/bin" >&2; exit 1; }
    SMOKE_DIR="$(dirname "$SMOKE_EXE")"
    RUN=()
    if [ -n "$SANITIZE" ]; then
        # Copy of the SmokeTest output with the sanitized QtNative next to the normal Qt bundle (RUNPATH $ORIGIN).
        rm -rf "$OUT/bin" "$OUT/$SANITIZE".*
        cp -a "$SMOKE_DIR" "$OUT/bin"
        cp "$SAN_SO" "$OUT/bin/runtimes/$RID/native/libvlqt.so"
        SMOKE_DIR="$OUT/bin"
        SUPP="$ROOT/tools/sanitizers"
        # The runtime must be the first DSO in the process: dotnet is not instrumented, libvlqt.so needs it on dlopen.
        if [ "$SANITIZE" = asan ]; then
            # .NET turns SIGSEGV/SIGFPE into NullReference/DivideByZero exceptions: ASan must leave these handlers alone.
            # LSan runs at exit; the runtime's own allocations are suppressed in lsan.supp (never libvlqt frames).
            RUN=(env LD_PRELOAD="$(gcc -print-file-name=libasan.so)"
                ASAN_OPTIONS="handle_segv=0:handle_sigbus=0:handle_sigfpe=0:allow_user_segv_handler=1:handle_abort=1:${VLQT_ASAN_LEAKS:-detect_leaks=1}:halt_on_error=1:detect_odr_violation=0:symbolize=1:log_path=$OUT/asan:suppressions=$SUPP/asan.supp${ASAN_OPTIONS:+:$ASAN_OPTIONS}"
                LSAN_OPTIONS="suppressions=$SUPP/lsan.supp:print_suppressions=0${LSAN_OPTIONS:+:$LSAN_OPTIONS}")
        else
            # setarch -R: gcc 13's TSan needs the fixed shadow layout, ASLR with high mmap_rnd_bits (WSL 6.x: 33)
            # maps outside of it ("unexpected memory mapping"); newer LLVM TSan re-execs itself, gcc's does not.
            RUN=(setarch "$(uname -m)" -R env LD_PRELOAD="$(gcc -print-file-name=libtsan.so)"
                TSAN_OPTIONS="halt_on_error=0:report_signal_unsafe=0:second_deadlock_stack=1:log_path=$OUT/tsan:suppressions=$SUPP/tsan.supp${TSAN_OPTIONS:+:$TSAN_OPTIONS}")
        fi
    fi
    set +e
    # timeout: guards against hangs on exit (known issue L2d, Qualcomm d3d12 driver on arm64 when VLQT_GPU != software)
    (cd "$SMOKE_DIR" && timeout -k 10 "${VLQT_SMOKE_TIMEOUT:-600}" "${RUN[@]}" dotnet VL.Qt.SmokeTest.dll "$OUT/smoke" "${SMOKE_ARGS[@]}") > "$OUT/smoke.out" 2>&1
    rc=$?
    set -e
    if [ -n "$SANITIZE" ]; then
        reports=("$OUT/$SANITIZE".*)
        if [ -e "${reports[0]}" ]; then
            # Offline symbolization (TSan does not symbolize inside dotnet): <report>.sym next to each report.
            for r in "${reports[@]}"; do
                python3 "$SUPP/symbolize.py" "$r" "$SMOKE_DIR/runtimes/$RID/native" "$(dirname "$(readlink -f "$(command -v dotnet)")")"/shared/Microsoft.NETCore.App/* \
                    /lib/"$(uname -m)"-linux-gnu || true
            done
            echo "$SANITIZE reports: ${reports[*]} (+ .sym)"
            grep -hE '^(==[0-9]+==ERROR|SUMMARY|WARNING: ThreadSanitizer)' "${reports[@]}" | sort | uniq -c | head -20
            san_fail=1
            if [ "$SANITIZE" = asan ] && ! grep -qE '^==[0-9]+==ERROR: AddressSanitizer' "${reports[@]}"; then
                # Leak reports only: foreign leaks (runtime, host) do not count, QtNative leaks do.
                sym=(); for r in "${reports[@]}"; do sym+=("$r.sym"); done
                python3 "$SUPP/lsan-filter.py" "${sym[@]}" > "$OUT/lsan-QtNative.txt" && san_fail=0
                head -1 "$OUT/lsan-QtNative.txt"
                # LSan sets exit 1 on any leak; only keep it when a QtNative leak or a smoke failure remains.
                [ "$san_fail" = 1 ] || [ "$rc" != 1 ] || [ "$(tail -1 "$OUT/smoke.out")" != 'SMOKE OK' ] || rc=0
            fi
            [ "$san_fail" = 0 ] || [ "$rc" != 0 ] || rc=99
        else
            echo "$SANITIZE: no reports"
        fi
    fi
    grep -E '^(FAIL|WARN|SKIP|DRIFT)' "$OUT/smoke.out" || true
    # active backend/driver (first occurrence each)
    grep -m1 -E 'Loading backend|Creating QRhi with backend' "$OUT/smoke.out" || true
    grep -m1 -E 'GL_RENDERER|Graphics API|vlqt.gpu' "$OUT/smoke.out" || true
    tail -1 "$OUT/smoke.out"
    [ "$rc" = 0 ] || { echo "SmokeTest exit $rc (full log: $OUT/smoke.out)"; fail=1; }
fi

if [ "$UNIT" = 1 ]; then
    step "dotnet test VL.Qt.Tests"
    set +e
    # console logger at normal verbosity: names + messages of failed tests in unit.out; TRX next to it (#429)
    dotnet test "$ROOT/src/VL.Qt.Tests" -c Release -nologo --artifacts-path "$ART" --logger "console;verbosity=normal" --logger "trx;LogFileName=unit.trx" --results-directory "$OUT" > "$OUT/unit.out" 2>&1
    rc=$?
    set -e
    grep -E '^\s*(Failed |Passed!|Failed!|Skipped!)|error' "$OUT/unit.out" | head -30 || true
    # message of the first failures (the console logger at normal verbosity prints them)
    grep -A6 -E '^\s+Failed ' "$OUT/unit.out" | head -40 || true
    if [ "$rc" != 0 ]; then
        # failed test names also on stderr, so they show up in the CI step log right away
        { echo "FAILED TESTS:"; grep -E '^\s+Failed ' "$OUT/unit.out" | sed -E 's/^\s+//' | head -30; } >&2 || true
        echo "dotnet test exit $rc (full log: $OUT/unit.out, TRX: $OUT/unit.trx)"; fail=1
    fi
fi

[ "$fail" = 0 ] && echo "LINUX TESTS OK ($RID, $PLATFORM${QT_QUICK_BACKEND:+, $QT_QUICK_BACKEND})" || echo "LINUX TESTS FAILED ($RID, $PLATFORM${QT_QUICK_BACKEND:+, $QT_QUICK_BACKEND})"
exit "$fail"
