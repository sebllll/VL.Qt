cmake_minimum_required(VERSION 3.21)
project(vlqt LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Default RID from the architecture of the compiler (the build scripts and presets pass -DVLQT_RID anyway).
if(WIN32)
    if(CMAKE_CXX_COMPILER_ARCHITECTURE_ID STREQUAL "ARM64")
        set(_vlqt_default_rid "win-arm64")
    else()
        set(_vlqt_default_rid "win-x64")
    endif()
elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64)$")
    set(_vlqt_default_rid "linux-arm64")
else()
    set(_vlqt_default_rid "linux-x64")
endif()
set(VLQT_RID "${_vlqt_default_rid}" CACHE STRING "Target runtime identifier")
set(VLQT_OUT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../runtimes/${VLQT_RID}/native" CACHE PATH "Deployment folder")
cmake_path(NORMAL_PATH VLQT_OUT_DIR)

find_package(Qt6 6.12 REQUIRED COMPONENTS Gui GuiPrivate Qml Quick QuickPrivate QuickControls2 Multimedia ShaderTools ShaderToolsPrivate QuickShapes QuickShapesPrivate)
qt_standard_project_setup()

add_library(vlqt SHARED)
target_include_directories(vlqt PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
# One file per line, grouped by the folders of the target layout. Files that still lie at the top level move into
# their group folder as the cuts of the native layer land.
target_sources(vlqt PRIVATE
    # Contract headers
    vlqt.h
    vlqt_test.h
    protocol.h

    # app/: start and pump, exports and last error, log, animation clock, render gate, input delivery, app fonts
    app/animation_clock.cpp
    app/animation_clock.h
    app/app.cpp
    app/app.h
    app/exports.h
    app/fonts.cpp
    app/fonts.h
    app/input_delivery.cpp
    app/last_error.cpp
    app/last_error.h
    app/log.cpp
    app/log.h
    app/render_gate.cpp
    app/render_gate.h
    app/thread_name.h

    # platform/: platform.h here, the sources of one platform in the if(WIN32) block below
    platform/platform.h

    # window/: the scene handle, windows and their filters, focus release, injected touch
    window/focus_release.cpp
    window/focus_release.h
    window/scene_handle.h
    window/touch_input.cpp
    window/window.cpp
    window/window.h
    window/window_exports.cpp
    window/window_filters.cpp
    window/window_filters.h

    # scene/: object table, command decoder, event queue, measurement, input handlers, previous-frame layer
    scene/clear_state.cpp
    scene/clear_state.h
    scene/commands.cpp
    scene/commands.h
    scene/event_queue.cpp
    scene/event_queue.h
    scene/handlers.cpp
    scene/handlers.h
    scene/measure.cpp
    scene/measure.h
    scene/previous_frame_layer.cpp
    scene/previous_frame_layer.h
    scene/scene.cpp
    scene/scene.h
    scene/touch_handler.cpp
    scene/touch_handler.h

    # elements/: element types and their properties, geometry, Group frame, effect sources
    elements/effect_sources.cpp
    elements/element_entry.h
    elements/element_types.cpp
    elements/element_types.h
    elements/elements.cpp
    elements/elements.h
    elements/geometry.cpp
    elements/geometry.h
    elements/group_item.cpp
    elements/group_item.h
    elements/shape_path_element.cpp
    elements/shape_path_element.h
    elements/type_controls.cpp
    elements/type_image.cpp
    elements/type_layout.cpp
    elements/type_qml.cpp
    elements/type_rectangle.cpp
    elements/type_shader.cpp
    elements/type_shape.cpp
    elements/type_text.cpp
    elements/type_video.cpp

    # effects/ and user_qml/
    effectchain.cpp
    effectchain.h
    effects/layer_requests.cpp
    effects/layer_requests.h
    effects/shader_item.cpp
    effects/shader_item.h
    effects/shader_material.cpp
    effects/shader_material.h
    qmlitem.cpp
    qmlitem.h

    # style/
    vlstyle.cpp
    vlstyle.h

    # resources/
    resources/pixel_format.cpp
    resources/pixel_format.h
    resources/resource_events.cpp
    resources/resource_events.h
    resources/resource_exports.cpp
    resources/resource_kind.h
    resources/resource_manager.cpp
    resources/resource_manager.h
    resources/resource_registry.cpp
    resources/resource_workers.cpp
    resources/resource_workers.h
    resources/resources.cpp
    resources/resources.h
    image_resource.cpp
    image_resource.h
    qtimageitem.cpp
    qtimageitem.h
    snapshot_resource.cpp
    snapshot_resource.h
    image_writer.cpp
    image_writer.h
    shader_resource.cpp
    shader_resource.h
    audio_tap.cpp
    audio_tap.h

    # device/
    device.cpp
    device.h
    render_thread.cpp
    render_thread.h

    # offscreen/
    offscreen/offscreen.h
    offscreen/offscreen_scene.cpp
    offscreen/offscreen_scene.h
    offscreen/offscreen_input.cpp
    offscreen/offscreen_exports.cpp

    # diagnostics/: perf monitor (statistics, hooks, pages, graph and timeline items) and debug overlay
    diagnostics/debug_overlay.cpp
    diagnostics/debug_overlay.h
    diagnostics/perf.h
    diagnostics/perf_exports.cpp
    diagnostics/perf_items.cpp
    diagnostics/perf_items.h
    diagnostics/perf_monitor.cpp
    diagnostics/perf_monitor.h
    diagnostics/perf_pages.cpp
    diagnostics/perf_pages.h
    diagnostics/perf_stats.cpp
    diagnostics/perf_stats.h

    # test/: the test exports of vlqt_test.h are in every build; only the SmokeTest and the unit tests call them
    test/test_hooks.h
    test/test_input.cpp
    test/test_probes.cpp
    test/test_resource_kind.cpp)
target_compile_definitions(vlqt PRIVATE VLQT_BUILD
    # windows.h without the min/max macros (std::min/std::max) and without the rarely used APIs
    $<$<PLATFORM_ID:Windows>:NOMINMAX;WIN32_LEAN_AND_MEAN>)
# Base style "VLQt": Qt Quick Controls style module embedded in QtNative (qrc:/qt/qml/VLQt, on the engine's
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
# Default window icon: PNGs in several sizes (scaled from docs/icon/icon.png) embedded as
# :/vlqt/icon/default-<size>.png, so it also shows in an export without a file next to it. Artwork replaceable: same
# file names; a new size = file in icon/ plus a line below and in window/window.cpp (defaultIcon).
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
# Sanitizer builds (Linux, gcc/clang): only QtNative is instrumented, the host (dotnet) gets the runtime per
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
# GuiPrivate: QRhi (GPU timestamps). QuickPrivate: QQuickPointerDeviceHandler (scene/touch_handler.h, check on Qt updates).
# QuickControls2: QQuickStyle. Multimedia: QAudioBufferOutput of the audio tap (deployed for the Video element anyway).
target_link_libraries(vlqt PRIVATE Qt6::Gui Qt6::GuiPrivate Qt6::Qml Qt6::Quick Qt6::QuickPrivate Qt6::QuickControls2 Qt6::Multimedia)
# ShaderToolsPrivate: QShaderBaker (runtime bake of ShaderEffect sources). rhi/qshaderbaker.h lies only in the
# versioned include dir (include/QtShaderTools/<ver>/QtShaderTools), which just the Private target adds (RHI API,
# limited compatibility guarantees: check on Qt updates). Qt6ShaderTools is deployed next to QtNative.
target_link_libraries(vlqt PRIVATE Qt6::ShaderTools Qt6::ShaderToolsPrivate)
# QuickShapes: Shape/ShapePath for Line, Polyline, Polygon, Path. QuickShapesPrivate: QQuickShape/QQuickShapePath
# (qquickshape_p.h) built from C++ with typed setters (makeShape in elements/type_shape.cpp); ShapePathElement (elements/shape_path_element.h)
# subclasses the private QQuickCurve of QuickPrivate (qquickpath_p.h). Private API, limited compatibility guarantees:
# check on Qt updates. Qt6QuickShapes and qml/QtQuick/Shapes are deployed next to QtNative (qml/deploy.qml).
target_link_libraries(vlqt PRIVATE Qt6::QuickShapes Qt6::QuickShapesPrivate)
if(WIN32)
    target_sources(vlqt PRIVATE
        # platform/: dialog keys for vvvv's message loop, DllMain
        platform/platform_win.cpp
        # offscreen/: the D3D11 binding and the frames of a scene; linear_pass converts QtTexture into SRGB and float textures;
        # d3dcompiler_47.dll ships with Windows 10 and up
        offscreen/offscreen_binding.cpp
        offscreen/offscreen_render.cpp
        offscreen/linear_pass.cpp
        offscreen/linear_pass.h
        # test/: D3D11On12 test device of vlqt_test_device_lost; d3d12.dll is loaded at run time, not linked
        test/test_device.cpp
        test/test_device.h)
    # device.cpp: one D3D11 device per process
    target_link_libraries(vlqt PRIVATE d3d11 dxgi d3dcompiler)
else()
    target_sources(vlqt PRIVATE
        # platform/: WSLg GPU default and probe, display check, fallback font, unload hook
        platform/platform_linux.cpp
        # offscreen/: X geometry of the offscreen platform's GLX drawable for window grabs. Uses the X11 headers that Qt's
        # OpenGL dependency brings (libgl-dev); libX11/libGL are looked up at run time, not linked.
        offscreen/offscreen_glx.cpp
        offscreen/offscreen_glx.h)
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
        # Qt libs are deployed next to the .so (tools/deploy-linux.sh); CMake appends the Qt lib dir for builds in the tree.
        BUILD_RPATH "\$ORIGIN"
        INSTALL_RPATH "\$ORIGIN"
        LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/vlqt.map")
    target_link_options(vlqt PRIVATE
        "LINKER:--version-script=${CMAKE_CURRENT_SOURCE_DIR}/vlqt.map"
        "LINKER:--no-undefined")
endif()

# vlqt_testkit (testkit/testkit.h): the stand-in "Stride" D3D11 device of the SmokeTest's QtTexture tests. A library of its own,
# no Qt, next to vlqt.dll so the SmokeTest finds it; never packed (src/VL.Qt/VL.Qt.csproj excludes it). Windows only.
option(VLQT_BUILD_TESTKIT "Build vlqt_testkit, the stand-in D3D11 device of the SmokeTest (Windows only)" ON)
if(WIN32 AND VLQT_BUILD_TESTKIT)
    add_library(vlqt_testkit SHARED)
    target_sources(vlqt_testkit PRIVATE
        testkit/testkit.cpp
        testkit/testkit.h)
    target_compile_definitions(vlqt_testkit PRIVATE VLQT_TESTKIT_BUILD NOMINMAX WIN32_LEAN_AND_MEAN)
    target_link_libraries(vlqt_testkit PRIVATE d3d11 dxgi)
    set_target_properties(vlqt_testkit PROPERTIES
        AUTOMOC OFF AUTOUIC OFF AUTORCC OFF
        RUNTIME_OUTPUT_DIRECTORY "${VLQT_OUT_DIR}"   # vlqt_testkit.dll
        PDB_OUTPUT_DIRECTORY "${VLQT_OUT_DIR}")
endif()

# ShaderEffect: bundled shaders src/VL.Qt.Native/shaders/<name>.frag|.vert are baked at build time by qsb (Qt host
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

if(WIN32) # Qt deploy (Linux: tools/deploy-linux.sh)
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
