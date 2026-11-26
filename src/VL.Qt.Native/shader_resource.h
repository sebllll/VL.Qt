/* shader_resource.h - resource kind VLQT_RESOURCE_KIND_SHADER: one shader of one stage for the Shader item (protocol.h
 * "ShaderEffect"; vlqt.h vlqt_shader_load, vlqt_shader_load_source). A .qsb file or "builtin:<name>" is read in a worker;
 * GLSL source is baked in a worker (one bake at a time, results in a process-wide cache). Every problem ends in Failed
 * with RESOURCE_EVENT_ERROR; a uniform named like a GLSL builtin gives RESOURCE_EVENT_WARNING. No upload, nothing to
 * restore after a device loss. Main thread only, except decode (like image_resource.h).
 * Load, bake targets, cache, checks and the rename check: docs/design/effects.md, "Runtime bake".
 */
#pragma once

#include "resources/resources.h"

#include <QtCore/QByteArray>
#include <QtCore/QString>
#include <rhi/qshader.h>
#include <rhi/qshaderbaker.h>

#include <cstdint>
#include <memory>
#include <string>

namespace vlqt::res::shader {

// One shader (the Content of the kind). Input fields are set before start, the decode result by the worker; after Ready
// the content is immutable and read on the main thread (QShader is implicitly shared, copies are cheap).
class ShaderResource final : public Content
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

// The kind for the manager (resources/resource_registry.cpp kindOf).
Kind* kind();

// vlqt_shader_load: utf8Path = .qsb file or "builtin:<name>", stage = VLQT_SHADER_STAGE_*. 0 / -7 / -9.
int load(uint64_t id, const char* utf8Path, int stage);

// vlqt_shader_load_source: utf8Source = full Qt GLSL, baked in the worker (QShaderBaker), cached per hash.
// 0 / -7 / -9.
int loadSource(uint64_t id, const char* utf8Source, int stage);

// vlqt_shader_reflect: SPIR-V bake under s_bakeMutex + QShaderDescription as text (format in vlqt.h), on the calling
// thread, but the text of a source seen before comes from the bake cache (stage + SHA-1, a separate entry from the
// runtime bake). Returns the required length without terminator, -7 on bad arguments. Any thread.
int reflect(const char* utf8Source, int stage, char* buf, int bufLen, int* written, int* required);

// Current content of a Ready shader resource, nullptr otherwise (also during the first load or after Failed).
std::shared_ptr<ShaderResource> ready(uint64_t id);

// Qt's default ShaderEffect shader of a stage (VLQT_SHADER_STAGE_*), embedded in Qt Quick's resources; read once per
// stage on first use. Vertex: qt_Vertex, qt_MultiTexCoord0 -> qt_TexCoord0, block qt_Matrix + qt_Opacity. Fragment:
// source * qt_Opacity. Invalid QShader (and one warning) if Qt does not have it.
const QShader& qtDefaultShader(int stage);

} // namespace vlqt::res::shader
