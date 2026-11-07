#!/usr/bin/env bash
# Prepares WSL2 Ubuntu 24.04 (also plain Ubuntu 24.04) for building/running VL.Qt on Linux:
#   1. system packages via apt (needs sudo): toolchain, GL/xkb/xcb/wayland runtime deps of Qt, python3-venv
#   2. .NET 10 SDK user-local in ~/.dotnet via dotnet-install.sh (no sudo, same channel as CI: 10.0.x)
# Qt itself comes from tools/setup-qt.sh. Usage: tools/setup-wsl.sh [--no-apt] [--no-dotnet]
set -euo pipefail

DO_APT=1
DO_DOTNET=1
while [ $# -gt 0 ]; do
    case "$1" in
        --no-apt) DO_APT=0; shift ;;
        --no-dotnet) DO_DOTNET=0; shift ;;
        -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

packages=(
    # build toolchain (QtNative: CMake + Ninja + gcc), aqtinstall venv
    build-essential cmake ninja-build pkg-config python3-venv curl ca-certificates
    # Qt bundle next to libvlqt.so (tools/deploy-linux.sh sets RUNPATH; falls back to pip in the aqt venv)
    patchelf
    # headers Qt6Gui's CMake config looks for (OpenGL/EGL, Vulkan, xkbcommon)
    libgl-dev libegl-dev libvulkan-dev libxkbcommon-dev
    # runtime deps of Qt's xcb platform plugin (wayland works without them)
    libxkbcommon-x11-0 libxcb-cursor0 libxcb-icccm4 libxcb-image0 libxcb-keysyms1 libxcb-render-util0
    libxcb-shape0 libxcb-util1 libxcb-xkb1 libxcb-randr0 libxcb-xinerama0 libxcb-xinput0 libxcb-xfixes0 libxcb-sync1
    # wayland, fonts, dbus, GL/Vulkan drivers (Mesa incl. d3d12 for WSLg)
    libwayland-cursor0 libwayland-egl1 libfontconfig1 libfreetype6 fonts-dejavu-core libdbus-1-3
    libgl1 libegl1 libgl1-mesa-dri mesa-vulkan-drivers
    # Qt Multimedia FFmpeg plugin (audio output), .NET runtime dep
    libpulse0 libicu74
)

if [ "$DO_APT" = 1 ]; then
    sudo apt-get update
    sudo DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "${packages[@]}"
fi

if [ "$DO_DOTNET" = 1 ]; then
    if [ -x "$HOME/.dotnet/dotnet" ] && "$HOME/.dotnet/dotnet" --list-sdks | grep -q '^10\.'; then
        echo ".NET 10 SDK already present: $("$HOME/.dotnet/dotnet" --list-sdks | grep '^10\.' | tail -1)"
    else
        script="$(mktemp)"
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$script"
        bash "$script" --channel 10.0 --install-dir "$HOME/.dotnet"
        rm -f "$script"
    fi
    case ":$PATH:" in
        *":$HOME/.dotnet:"*) ;;
        *) echo "Add to ~/.bashrc:  export DOTNET_ROOT=\$HOME/.dotnet; export PATH=\$HOME/.dotnet:\$PATH" ;;
    esac
fi

echo "Next: tools/setup-qt.sh && tools/check-wslg.sh"
