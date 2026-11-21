/* shader_resource.h - resource kind VLQT_RESOURCE_KIND_SHADER (effects package 03; runtime bake).
 *
 * One shader of one stage for the ShaderEffect item (protocol.h "ShaderEffect", vlqt.h vlqt_shader_load /
 * vlqt_shader_load_source).
 *   load:   main thread: checks the arguments, resolves "builtin:<name>" against <QtNative dir>/shaders/<name>.frag.qsb
 *           (stage FRAGMENT) or <name>.vert.qsb (stage VERTEX) and notes the shader language the process backend needs
 *           (QQuickWindow::graphicsApi: D3D11 HLSL/DXBC, OpenGL GLSL, Vulkan SPIR-V, Metal MSL).
 *   loadSource: main thread: checks the arguments, copies the GLSL and notes the bake targets for that language only
 *           (D3D HLSL 50, OpenGL GLSL 330/150/120 or ES 300/100, Vulkan SPIR-V 100, Metal MSL 12; unknown: several).
 *   decode: worker: .qsb: reads the file, QShader::fromSerialized. Source: QShaderBaker (own instance, one bake at a
 *           time), results (shader or error text) in a process wide cache per stage + backend + SHA-1 of the source
 *           (64 entries), a hit does not bake. Both then check stage, a variant for the backend and the parts of
 *           the reflection the ShaderEffect item relies on (at most one uniform block, qt_Matrix / qt_Opacity types).
 *           Every problem ends in Failed with RESOURCE_EVENT_ERROR; bake errors one line per glslang message,
 *           "fragment code, line <n>: <text>".
 *           A uniform or sampler named like a GLSL builtin (`length`, ...) is renamed in the GLSL code but not in
 *           the reflection, so the OpenGL backend never sets it: RESOURCE_EVENT_WARNING. Checked on the GLSL variant of
 *           a .qsb, or for a source on another backend on an extra GLSL 150 translation (cached with the bake).
 *   upload: nothing (no GPU object, RESOURCE_EVENT_INFO all 0); each ShaderEffect item builds its pipeline from the QShader.
 * Device lost: CPU data only, nothing to restore. A new load on a Ready id keeps the old shader until the new one is
 * Ready (resources.h, replacement). Main thread only, except decode (like image_resource.h).
 */
#pragma once

#include "resources.h"

#include <QtCore/QByteArray>
#include <QtCore/QString>
#include <rhi/qshader.h>
#include <rhi/qshaderbaker.h>

#include <cstdint>
#include <memory>
#include <string>

namespace vlqt::res::shader {

// One shader (the Work of the kind). Input fields are set before start, the decode result by the worker; after Ready
// the content is immutable and read on the main thread (QShader is implicitly shared, copies are cheap).
class ShaderResource final : public Work
{
public:
    // --- for the ShaderEffect item, main thread, on a Ready resource ---
    const QShader& shader() const { return m_shader; }
    int stage() const { return m_stage; } // VLQT_SHADER_STAGE_*
    const QString& path() const { return m_path; } // resolved file path, "fragment code" / "vertex code" for a source

private:
    friend class ShaderKind;
    friend int load(uint64_t, const char*, int);
    friend int loadSource(uint64_t, const char*, int);

    // input
    QString m_path;
    QByteArray m_code; // loadSource: the GLSL to bake (empty = load a .qsb from m_path)
    QList<QShaderBaker::GeneratedShader> m_targets; // loadSource: languages to generate (process backend)
    int m_stage = 0;
    int m_source = 0; // QShader::Source the backend needs (DXBC is accepted for HLSL), -1 = no check

    // decode result (worker)
    QShader m_shader;
    std::string m_warning; // a uniform or sampler the GLSL translation renamed, for Kind::takeWarning
};

// The kind for the manager (resources.cpp kindOf).
Kind* kind();

// vlqt_shader_load: utf8Path = .qsb file or "builtin:<name>", stage = VLQT_SHADER_STAGE_*. 0 / -7 / -9.
int load(uint64_t id, const char* utf8Path, int stage);

// vlqt_shader_load_source: utf8Source = full Qt GLSL, baked in the worker (QShaderBaker), cached per hash.
// 0 / -7 / -9.
int loadSource(uint64_t id, const char* utf8Source, int stage);

// vlqt_shader_reflect: synchronous SPIR-V bake under s_bakeMutex + QShaderDescription as text (format in
// vlqt.h). Returns the required length without terminator, -7 on bad arguments. Any thread.
int reflect(const char* utf8Source, int stage, char* buf, int bufLen, int* written, int* required);

// Current content of a Ready shader resource, nullptr otherwise (also during the first load or after Failed).
std::shared_ptr<ShaderResource> ready(uint64_t id);

// Qt's default ShaderEffect vertex shader (qt_Vertex, qt_MultiTexCoord0 -> qt_TexCoord0, block qt_Matrix +
// qt_Opacity), embedded in Qt Quick's resources; read once on first use. Invalid QShader if Qt does not have it.
const QShader& defaultVertexShader();

} // namespace vlqt::res::shader
