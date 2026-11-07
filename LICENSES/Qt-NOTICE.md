# Qt notice (LGPL-3.0)

VL.Qt ships parts of the Qt framework (version 6.12.0) as **unmodified, dynamically linked** libraries
(`runtimes/<rid>/native/Qt6*.dll`, `plugins/`, `qml/`). Qt is licensed under the GNU Lesser General Public License
version 3 (LGPL-3.0, text: `LGPL-3.0.txt`, supplemented by the GPL-3.0: `GPL-3.0.txt`). Copyright (C) The Qt Company Ltd. and
Qt contributors.

## Source code
The complete source code of the shipped Qt version (Qt 6.12.0) is available here:

- Archives: <https://download.qt.io/archive/qt/6.12/6.12.0/submodules/> (`qtbase-everywhere-src-6.12.0.tar.xz`,
  `qtdeclarative-everywhere-src-6.12.0.tar.xz`, `qtshadertools-everywhere-src-6.12.0.tar.xz`, `qtsvg-everywhere-src-6.12.0.tar.xz`,
  `qtmultimedia-everywhere-src-6.12.0.tar.xz`)
- Git: <https://code.qt.io/cgit/qt/> (tag `v6.12.0`) or <https://github.com/qt>

## What VL.Qt does and does not do
- Qt is **not modified** and not statically linked. The DLLs come unmodified from the official Qt installer layout
  (`aqtinstall`, see `tools/setup-qt.ps1`) and are assembled by `windeployqt`. `tools/trim-deploy.ps1` only deletes
  whole files that are not needed (list with reasons in the script), it patches nothing.
- QtNative `vlqt.dll` (source code in `src/VL.Qt.Native`, MIT license) links dynamically against Qt.
- Users may replace the shipped Qt DLLs with their own, ABI-compatible Qt build (LGPL-3.0 §4): replace the Qt DLLs
  in the folder `runtimes/<rid>/native/` (Qt 6.12, same architecture, same modules).
- Only LGPL or permissively licensed Qt modules are shipped. `tools/check-licenses.ps1` fails as soon as
  a module that is not approved (for example Qt Quick 3D, Qt Charts) is in the deploy folder.

## Linux
On Linux the same Qt modules are shipped as `.so` in `runtimes/linux-*/native/` (assembled by
`tools/deploy-linux.sh`, trimmed by `tools/trim-linux.sh`, checked by `tools/check-licenses.sh`), plus the
Linux platform parts from `qtbase` and `qtwayland` (`qtwayland-everywhere-src-6.12.0.tar.xz`, LGPL-3.0) and the ICU 73.2
(Unicode License) shipped by Qt. The only change to the files: `patchelf` sets the RUNPATH to
`$ORIGIN`-relative paths so that the bundle starts without `LD_LIBRARY_PATH`. Replacing it with your own Qt build works as above
(Qt 6.12, same architecture, same modules).

## Third-party parts in Qt
Qt bundles its own third-party libraries (among others libpng, FreeType, HarfBuzz, zlib, PCRE2, double-conversion, md4c).
Their licenses: <https://doc.qt.io/qt-6/licenses-used-in-qt.html>.

Qt Shader Tools (`Qt6ShaderTools`, runtime bake of GLSL source) bundles glslang (BSD-3-Clause, Apache-2.0, MIT) and
SPIRV-Cross (Apache-2.0, MIT); both are contained unmodified in the Qt library. The command-line tool `qsb` is not shipped.

## FFmpeg (LGPL-2.1-or-later)
Qt Multimedia (Video element) uses FFmpeg 9.0.1, which Qt ships as unmodified, dynamically linked DLLs
(`avcodec-*.dll`, `avformat-*.dll`, `avutil-*.dll`, `swresample-*.dll`, `swscale-*.dll` in `runtimes/<rid>/native/`).
In this build FFmpeg is licensed under the GNU Lesser General Public License version 2.1 or later (text: `LGPL-2.1.txt`).
`tools/check-licenses.ps1` checks the license identifier in every DLL and fails on `--enable-gpl`/`--enable-nonfree`.
Source code: <https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz> or <https://git.ffmpeg.org/ffmpeg.git> (tag `n9.0.1`).
Like the Qt DLLs, the DLLs may be replaced with your own ABI-compatible build. Copyright (C) the FFmpeg developers.

## Trademarks
Qt and the Qt logo are trademarks of The Qt Company Ltd. VL.Qt is not a product of The Qt Company.
