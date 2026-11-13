#!/usr/bin/env bash
# Prepares WSL2 Ubuntu 24.04 (also plain Ubuntu 24.04) for building/running VL.Qt on Linux:
#   1. system packages via apt (needs sudo): toolchain, GL/xkb/xcb/wayland runtime deps of Qt, python3-venv
#   2. .NET 10 SDK user-local in ~/.dotnet via dotnet-install.sh (no sudo, same channel as CI: 10.0.x)
# Qt itself comes from tools/setup-qt.sh. Usage: tools/setup-wsl.sh [--runtime-only] [--no-apt] [--no-dotnet]
# --runtime-only: only what an exported app needs (Qt's runtime libraries + the .NET 10 runtime, no compiler,
#   CMake or SDK), for a machine that only runs exports. Works from /mnt/c/<repo>/tools/ in WSL as well.
# Checks the prerequisites first (Ubuntu 24.04, sudo, WSLg) and says in plain words what is missing.
set -euo pipefail

DO_APT=1
DO_DOTNET=1
RUNTIME_ONLY=0
while [ $# -gt 0 ]; do
    case "$1" in
        --no-apt) DO_APT=0; shift ;;
        --no-dotnet) DO_DOTNET=0; shift ;;
        --runtime-only) RUNTIME_ONLY=1; shift ;;
        -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

# What an exported app needs on the target: Qt's runtime libraries, GL drivers, fonts, .NET runtime dependencies.
runtime_packages=(
    # runtime deps of Qt's xcb platform plugin (wayland works without them)
    libxkbcommon0 libxkbcommon-x11-0 libxcb-cursor0 libxcb-icccm4 libxcb-image0 libxcb-keysyms1 libxcb-render-util0
    libxcb-shape0 libxcb-util1 libxcb-xkb1 libxcb-randr0 libxcb-xinerama0 libxcb-xinput0 libxcb-xfixes0 libxcb-sync1
    libsm6 libice6 libxrender1 libxrandr2 libglib2.0-0t64
    # wayland, fonts, dbus, GL drivers (Mesa incl. d3d12 for WSLg)
    libwayland-client0 libwayland-cursor0 libwayland-egl1 libfontconfig1 libfreetype6 fonts-dejavu-core libdbus-1-3
    libgl1 libegl1 libgbm1 libgl1-mesa-dri
    # Qt Multimedia FFmpeg plugin (audio output), .NET runtime dep, download of dotnet-install.sh
    libpulse0 libicu74 curl ca-certificates
)
# Build and test from source: toolchain, headers, Vulkan drivers.
build_packages=(
    # build toolchain (QtNative: CMake + Ninja + gcc), aqtinstall venv
    build-essential cmake ninja-build pkg-config python3-venv
    # Qt bundle next to libvlqt.so (tools/deploy-linux.sh sets RUNPATH; falls back to pip in the aqt venv)
    patchelf
    # headers Qt6Gui's CMake config looks for (OpenGL/EGL, Vulkan, xkbcommon)
    libgl-dev libegl-dev libvulkan-dev libxkbcommon-dev mesa-vulkan-drivers
)
# Package names that change between Ubuntu releases (libicu74 on 24.04, libicu78 on 26.04): take what apt knows.
icu_package="$(apt-cache search --names-only '^libicu[0-9]+$' 2>/dev/null | awk '{print $1}' | sort -V | tail -1)"
runtime_packages=("${runtime_packages[@]/libicu74/${icu_package:-libicu74}}")
packages=("${runtime_packages[@]}")
[ "$RUNTIME_ONLY" = 1 ] || packages+=("${build_packages[@]}")

die() { echo "ERROR: $*" >&2; exit 1; }

# Prerequisites, in plain words.
[ "$(uname -s)" = Linux ] || die "this script is for Linux (Ubuntu 24.04, also WSL2). On Windows run it in the WSL shell: wsl -d Ubuntu-24.04"
if [ -r /etc/os-release ]; then
    . /etc/os-release
    if [ "${ID:-}" != ubuntu ] || [ "${VERSION_ID:-}" != 24.04 ]; then
        echo "WARN: tested on Ubuntu 24.04 only (this is ${PRETTY_NAME:-unknown}); package names may differ and apt may fail." >&2
    fi
fi
SUDO=''
if [ "$DO_APT" = 1 ] && [ "$(id -u)" != 0 ]; then
    command -v sudo >/dev/null 2>&1 || die "sudo not found: run as root, or install the packages yourself and use --no-apt."
    SUDO=sudo
fi
if grep -qi microsoft /proc/version 2>/dev/null; then
    [ -e /dev/dxg ] || echo "WARN: /dev/dxg is missing (WSL1 or an old WSL): use WSL2 and run 'wsl --update' in PowerShell. Without it the GPU (d3d12) is not used." >&2
    if [ -z "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
        echo "WARN: neither DISPLAY nor WAYLAND_DISPLAY is set: WSLg is not running, so no window can open (needs Windows 11, 'wsl --update', then 'wsl --shutdown')." >&2
    fi
fi

if [ "$DO_APT" = 1 ]; then
    $SUDO apt-get update
    $SUDO env DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "${packages[@]}"
fi

if [ "$DO_DOTNET" = 1 ]; then
    if [ "$RUNTIME_ONLY" = 1 ]; then
        have="$("$HOME/.dotnet/dotnet" --list-runtimes 2>/dev/null | grep '^Microsoft.NETCore.App 10\.' | tail -1 || true)"
        kind=runtime
        dn_args=(--runtime dotnet)
    else
        have="$("$HOME/.dotnet/dotnet" --list-sdks 2>/dev/null | grep '^10\.' | tail -1 || true)"
        kind=SDK
        dn_args=()
    fi
    if [ -n "$have" ]; then
        echo ".NET 10 $kind already present: $have"
    else
        script="$(mktemp)"
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$script" || die "cannot download dotnet-install.sh (no network?)"
        bash "$script" --channel 10.0 --install-dir "$HOME/.dotnet" "${dn_args[@]}"
        rm -f "$script"
    fi
    case ":$PATH:" in
        *":$HOME/.dotnet:"*) ;;
        *) echo "Add to ~/.bashrc:  export DOTNET_ROOT=\$HOME/.dotnet; export PATH=\$HOME/.dotnet:\$PATH" ;;
    esac
fi

if [ "$RUNTIME_ONLY" = 1 ]; then
    echo "Done. Start an export with: DOTNET_ROOT=\$HOME/.dotnet ./<AppName>   (chmod +x <AppName> after copying it from Windows)"
else
    echo "Next: tools/setup-qt.sh && tools/check-wslg.sh"
fi
