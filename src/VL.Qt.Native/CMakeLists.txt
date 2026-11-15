cmake_minimum_required(VERSION 3.21)
project(vlqt LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(WIN32)
    set(VLQT_RID "win-arm64" CACHE STRING "Target runtime identifier")
elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64)$")
    set(VLQT_RID "linux-arm64" CACHE STRING "Target runtime identifier")
else()
    set(VLQT_RID "linux-x64" CACHE STRING "Target runtime identifier")
endif()
set(VLQT_OUT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../runtimes/${VLQT_RID}/native" CACHE PATH "Deployment folder")
cmake_path(NORMAL_PATH VLQT_OUT_DIR)

find_package(Qt6 6.12 REQUIRED COMPONENTS Gui GuiPrivate Qml Quick QuickPrivate QuickControls2 ShaderTools ShaderToolsPrivate)
qt_standard_project_setup()

add_library(vlqt SHARED vlqt.cpp vlqt.h device.cpp device.h perf.cpp perf.h commands.cpp commands.h effectchain.cpp effectchain.h elements.cpp elements.h events.cpp events.h touch.cpp touch.h debug.cpp debug.h resources.cpp resources.h image_resource.cpp image_resource.h qtimageitem.cpp qtimageitem.h snapshot_resource.cpp snapshot_resource.h shader_resource.cpp shader_resource.h shadereffectitem.cpp shadereffectitem.h qmlitem.cpp qmlitem.h vlstyle.cpp vlstyle.h offscreen.cpp offscreen.h render_thread.cpp render_thread.h test_offscreen.cpp protocol.h)
target_compile_definitions(vlqt PRIVATE VLQT_BUILD)
# Base style "VLQt" (#292): Qt Quick Controls style module embedded in QtNative (qrc:/qt/qml/VLQt, on the engine's
# default import path), nothing to deploy. Plain resources + hand-written qmldir: no qmlcachegen/plugin target; the
# C++ part (attached VlStyle) is registered at app start as module VLQt.Style (vlstyle.cpp). Controls without a file
# here fall back to Basic (qmldir "import QtQuick.Controls.Basic auto"). A new control = <Control>.qml in style/VLQt,
# a line "<Control> 1.0 <Control>.qml" in its qmldir and the file in FILES below.
qt_add_resources(vlqt "vlqt_style"
    PREFIX "/qt/qml/VLQt"
    BASE "${CMAKE_CURRENT_SOURCE_DIR}/style/VLQt"
    FILES
        style/VLQt/qmldir
        style/VLQt/Button.qml
        style/VLQt/Slider.qml
        style/VLQt/CheckBox.qml
        style/VLQt/Switch.qml
        style/VLQt/SpinBox.qml
        style/VLQt/TextField.qml)
# Default window icon (#517): placeholder PNGs in several sizes embedded as :/vlqt/icon/default-<size>.png, so it also
# shows in an export without a file next to it. Artwork replaceable: same file names (icon/gen_default_icon.py makes
# the placeholder); a new size = file in icon/ plus a line below and in vlqt.cpp (defaultIcon).
qt_add_resources(vlqt "vlqt_icon"
    PREFIX "/vlqt/icon"
    BASE "${CMAKE_CURRENT_SOURCE_DIR}/icon"
    FILES
        icon/default-16.png
        icon/default-24.png
        icon/default-32.png
        icon/default-48.png
        icon/default-64.png
        icon/default-128.png
        icon/default-256.png)
# Sanitizer builds (Linux, gcc/clang; #365): only QtNative is instrumented, the host (dotnet) gets the runtime per
# LD_PRELOAD (tools/test-linux.sh --sanitize). Own build/out dir (presets linux-*-asan/-tsan, build-native.sh --sanitize).
set(VLQT_SANITIZE "" CACHE STRING "Sanitizer for QtNative: empty, address or thread")
set_property(CACHE VLQT_SANITIZE PROPERTY STRINGS "" address thread)
if(VLQT_SANITIZE)
    if(WIN32 OR NOT VLQT_SANITIZE MATCHES "^(address|thread)$")
        message(FATAL_ERROR "VLQT_SANITIZE='${VLQT_SANITIZE}' not supported (Linux only: address or thread)")
    endif()
    target_compile_options(vlqt PRIVATE -fsanitize=${VLQT_SANITIZE} -fno-omit-frame-pointer -g)
    target_link_options(vlqt PRIVATE -fsanitize=${VLQT_SANITIZE})
endif()
target_link_libraries(vlqt PRIVATE Qt6::Gui Qt6::GuiPrivate Qt6::Qml Qt6::Quick Qt6::QuickPrivate Qt6::QuickControls2) # GuiPrivate: QRhi (GPU timestamps); QuickPrivate: QQuickPointerDeviceHandler (touch.h, check on Qt updates); QuickControls2: QQuickStyle
# ShaderToolsPrivate: QShaderBaker (runtime bake of ShaderEffect sources, #483). rhi/qshaderbaker.h lies only in the
# versioned include dir (include/QtShaderTools/<ver>/QtShaderTools), which just the Private target adds (RHI API,
# limited compatibility guarantees: check on Qt updates). Qt6ShaderTools is deployed next to QtNative.
target_link_libraries(vlqt PRIVATE Qt6::ShaderTools Qt6::ShaderToolsPrivate)
if(WIN32)
    target_link_libraries(vlqt PRIVATE d3d11 dxgi) # device.cpp: one D3D11 device per process (#358)
    # linear_pass.cpp: QtTexture into SRGB and float textures; d3dcompiler_47.dll ships with Windows 10 and up.
    target_sources(vlqt PRIVATE linear_pass.cpp linear_pass.h)
    target_link_libraries(vlqt PRIVATE d3dcompiler)
endif()
set_target_properties(vlqt PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${VLQT_OUT_DIR}"   # vlqt.dll
    LIBRARY_OUTPUT_DIRECTORY "${VLQT_OUT_DIR}"   # libvlqt.so
    PDB_OUTPUT_DIRECTORY "${VLQT_OUT_DIR}")

if(NOT WIN32)
    # Export only the VLQT_API functions (vlqt.h); the version script also hides template instantiations that
    # inherit default visibility from libstdc++/Qt headers.
    set_target_properties(vlqt PROPERTIES
        CXX_VISIBILITY_PRESET hidden
        VISIBILITY_INLINES_HIDDEN ON
        # Qt libs are deployed next to the .so (L3); CMake appends the Qt lib dir for builds in the tree.
        BUILD_RPATH "\$ORIGIN"
        INSTALL_RPATH "\$ORIGIN"
        LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/vlqt.map")
    target_link_options(vlqt PRIVATE
        "LINKER:--version-script=${CMAKE_CURRENT_SOURCE_DIR}/vlqt.map"
        "LINKER:--no-undefined")
endif()

# ShaderEffect (#437): bundled shaders src/VL.Qt.Native/shaders/<name>.frag|.vert are baked at build time by qsb (Qt host
# tool, build-time only: qsb is never deployed or packed) into <out>/shaders/<name>.frag.qsb|.vert.qsb next to qml/.
# Targets: GLSL 100es/120/150 (OpenGL, GLES), HLSL 5.0 (D3D11), MSL 1.2 (Metal), plus SPIR-V (Vulkan) by default.
# Own custom commands instead of qt_add_shaders: that one embeds into a target's resources, we need loose files.
find_package(Qt6 REQUIRED COMPONENTS ShaderToolsTools)
file(GLOB _shader_srcs CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/shaders/*.frag" "${CMAKE_CURRENT_SOURCE_DIR}/shaders/*.vert")
set(_shader_outs "")
foreach(_src IN LISTS _shader_srcs)
    get_filename_component(_name "${_src}" NAME)
    set(_out "${VLQT_OUT_DIR}/shaders/${_name}.qsb")
    add_custom_command(OUTPUT "${_out}"
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${VLQT_OUT_DIR}/shaders"
        COMMAND Qt6::qsb --glsl "100 es,120,150" --hlsl 50 --msl 12 -o "${_out}" "${_src}"
        DEPENDS "${_src}" Qt6::qsb
        COMMENT "qsb ${_name}"
        VERBATIM)
    list(APPEND _shader_outs "${_out}")
endforeach()
add_custom_target(vlqt_shaders ALL DEPENDS ${_shader_outs})

if(WIN32) # Qt deploy (Linux: L3)
# Deploy Qt DLLs, plugins/ and qml/ next to QtNative (LGPL: dynamic, unmodified).
# qml/ holds only deploy.qml: windeployqt collects the QML modules it imports.
# Own custom command (not POST_BUILD) so it also re-runs when deploy.qml changes; the stamp lives in
# the build dir, build-native.ps1 deletes it to force a deploy (e.g. when qml/QtQuick/Effects is missing).
get_target_property(_qmake Qt6::qmake IMPORTED_LOCATION)
get_filename_component(_qtbin "${_qmake}" DIRECTORY)
set(_deploy_stamp "${CMAKE_CURRENT_BINARY_DIR}/windeployqt.stamp")
add_custom_command(OUTPUT "${_deploy_stamp}"
    COMMAND "${_qtbin}/windeployqt.exe" --release
        --qmldir "${CMAKE_CURRENT_SOURCE_DIR}/qml"
        --dir "${VLQT_OUT_DIR}"
        --plugindir "${VLQT_OUT_DIR}/plugins"
        --qml-deploy-dir "${VLQT_OUT_DIR}/qml"
        --no-translations --no-opengl-sw --no-system-d3d-compiler --no-system-dxc-compiler
        --no-compiler-runtime
        "$<TARGET_FILE:vlqt>"
    COMMAND "${CMAKE_COMMAND}" -E touch "${_deploy_stamp}"
    DEPENDS vlqt "${CMAKE_CURRENT_SOURCE_DIR}/qml/deploy.qml"
    COMMENT "windeployqt -> ${VLQT_OUT_DIR}"
    VERBATIM)
add_custom_target(vlqt_deploy ALL DEPENDS "${_deploy_stamp}")
endif() # WIN32
