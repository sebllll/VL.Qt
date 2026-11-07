# Licenses

VL.Qt itself (C#, QtNative, help patches, scripts) is licensed under the **MIT License**, see [LICENSE](LICENSE).

The package also ships Qt (LGPL-3.0) and FFmpeg (LGPL-2.1-or-later) as unmodified, dynamically linked libraries. They keep their own licenses.

## Export checklist (Qt and FFmpeg)

If you export or redistribute an application that contains the VL.Qt runtime:

- Ship the license texts from [LICENSES/](LICENSES/) (`LGPL-3.0.txt`, `GPL-3.0.txt`, `LGPL-2.1.txt`, on Linux also `ICU.txt` and `DejaVu-Fonts.txt`) and the [Qt notice](LICENSES/Qt-NOTICE.md) with your application.
- Keep the Qt and FFmpeg DLLs (Linux: `.so`) unmodified and replaceable, so that users can swap in their own ABI-compatible build (Qt 6.12, same architecture, same modules).
- Point to the sources of the shipped versions: the links in the table below and in the [Qt notice](LICENSES/Qt-NOTICE.md).

## Third-party components

Status: Qt 6.12.0. Checked with every Qt update (`tools/check-licenses.ps1`, on Linux `tools/check-licenses.sh`, enforce the module list).

| Component | Version | License | Linking / form | Source |
|---|---|---|---|---|
| Qt Core, Gui, Network, OpenGL, Qml (+QmlMeta, QmlModels, QmlWorkerScript), Quick, QuickShapes | 6.12.0 | LGPL-3.0 | dynamic (DLL), unmodified | <https://download.qt.io/archive/qt/6.12/6.12.0/submodules/> |
| Qt Svg (+ plugins `qsvg`, `qsvgicon`) | 6.12.0 | LGPL-3.0 | dynamic, unmodified (omitted with `trim-deploy.ps1 -DropSvg`) | as above |
| Qt plugins `platforms/qwindows`, `imageformats/*` | 6.12.0 | LGPL-3.0 | dynamic (plugin), unmodified | as above |
| QML modules `QtQml`, `QtQuick`, `QtQuick.Shapes` and others | 6.12.0 | LGPL-3.0 | dynamic (plugin), unmodified | as above |
| Qt Shader Tools (`Qt6ShaderTools.dll` / `libQt6ShaderTools.so.6`, QShaderBaker for the ShaderEffect runtime bake; `qsb` itself is not shipped) | 6.12.0 | LGPL-3.0; contains glslang (BSD-3-Clause, Apache-2.0, MIT) and SPIRV-Cross (Apache-2.0 / MIT) | dynamic, unmodified | as above (`qtshadertools-everywhere-src-6.12.0.tar.xz`); third-party parts: <https://github.com/KhronosGroup/glslang>, <https://github.com/KhronosGroup/SPIRV-Cross> |
| Qt Multimedia, Multimedia Quick (+ plugin `multimedia/ffmpegmediaplugin`, QML module `QtMultimedia`) | 6.12.0 | LGPL-3.0 | dynamic, unmodified (Video element) | as above (`qtmultimedia-everywhere-src-6.12.0.tar.xz`) |
| FFmpeg (`avcodec`, `avformat`, `avutil`, `swresample`, `swscale`), built and shipped by Qt | 9.0.1 | LGPL-2.1-or-later (without `--enable-gpl`/`--enable-nonfree`, checked by `check-licenses.ps1`) | dynamic (DLL), unmodified | <https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz>, Git <https://git.ffmpeg.org/ffmpeg.git> (tag `n9.0.1`) |
| Libraries bundled in Qt (libpng, FreeType, HarfBuzz, zlib, PCRE2, …) | see Qt | permissive (BSD/MIT/FTL/zlib) | contained in the Qt DLLs | <https://doc.qt.io/qt-6/licenses-used-in-qt.html> |
| Linux only: Qt DBus, XcbQpa, WaylandClient, WlShellIntegration, EglFSDeviceIntegration, EglFsKmsSupport, EglFsKmsGbmSupport, FFmpegStub-* (+ plugins `platforms/*`, `xcbglintegrations`, `egldeviceintegrations`, `wayland-*`, `platforminputcontexts`) | 6.12.0 | LGPL-3.0 | dynamic (`.so`), unmodified (only RUNPATH set to `$ORIGIN` with `patchelf`) | as above (`qtbase`, `qtwayland`, `qtmultimedia`) |
| Linux only: ICU (`libicudata`, `libicui18n`, `libicuuc`), shipped by Qt | 73.2 | Unicode License (ICU), permissive | dynamic (`.so`), only RUNPATH set with `patchelf` | <https://github.com/unicode-org/icu/releases/tag/release-73-2> |
| Linux only: DejaVu Sans Regular (`fonts/DejaVuSans.ttf`, ~0.7 MB; fallback font, QtNative registers it only if fontconfig provides no system fonts) | 2.37 | Bitstream Vera License (permissive; DejaVu changes are public domain) | file, unmodified | <https://dejavu-fonts.github.io/> |
| VL.Qt QtNative `vlqt.dll` / `libvlqt.so` | this repo | MIT ([LICENSE](LICENSE)) | dynamic against Qt | `src/VL.Qt.Native` |

## Not included (on purpose)

GPL-only or commercial Qt modules (Qt Quick 3D, Qt Charts, Qt Data Visualization, Qt Graphs, Qt Quick Timeline,
Qt Virtual Keyboard, Qt MQTT/CoAP/OPC UA, Qt Wayland Compositor). The CI check `tools/check-licenses.ps1` / `check-licenses.sh` fails if one of them
or any other module that is not approved ends up in the package.

## Notes for users

These notes point out two topics that the licenses above do not settle for you. They are general information, not legal advice. If they matter for your project, ask someone with license experience.

- **Codec patents.** The FFmpeg libraries shipped through Qt Multimedia can decode formats such as H.264 and HEVC. Software licenses (LGPL, MIT) and patent rights are independent of each other: the LGPL does not grant patent licenses for these codecs. Whether you need patent licenses (for example from a patent pool) to distribute or use an application that plays or encodes such formats depends on the format, the country and your use. That is your responsibility. VL.Qt does not provide patent licenses.
- **Devices that you sell or ship.** The LGPL-3.0 requires that users of a product can replace the LGPL library with a modified version. If you ship Qt inside a device that is locked down (for example firmware that only runs signed code), you must also give the user the "Installation Information" needed to install a replacement Qt on that device, see section 4 (Combined Works) and the definition of "Installation Information" in `LICENSES/LGPL-3.0.txt`. Apps that you only export and hand to users who can swap the DLLs are not affected, see the [export checklist](#export-checklist-qt-and-ffmpeg).

## Trademarks

Qt is a registered trademark of The Qt Company Ltd. and its subsidiaries. VL.Qt is an independent open source project. It is not affiliated with, sponsored or endorsed by The Qt Company. All other trademarks belong to their respective owners.

## License texts

- [LICENSE](LICENSE): VL.Qt itself (MIT)
- `LICENSES/LGPL-3.0.txt`, `LICENSES/GPL-3.0.txt` (the LGPL-3.0 builds on the GPL-3.0)
- `LICENSES/LGPL-2.1.txt` (FFmpeg)
- `LICENSES/Qt-NOTICE.md`: Qt notice, source links, replacing the Qt DLLs
- `LICENSES/ICU.txt` (Linux only: ICU 73.2, Unicode License, from `icu4c/LICENSE` of tag `release-73-2`)
- `LICENSES/DejaVu-Fonts.txt` (Linux only: DejaVu Sans, Bitstream Vera License; also shipped as `fonts/DejaVu-Fonts-LICENSE.txt` in the bundle)
