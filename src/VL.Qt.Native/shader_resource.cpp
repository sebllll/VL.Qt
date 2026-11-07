#include "shader_resource.h"

#include "protocol.h"

#include <QtCore/QCryptographicHash>
#include <QtCore/QDirIterator>
#include <QtCore/QFile>
#include <QtCore/QFileInfo>
#include <QtCore/QLoggingCategory>
#include <QtCore/QRegularExpression>
#include <QtGui/QOpenGLContext>
#include <QtQuick/QQuickWindow>
#include <rhi/qshaderbaker.h>
#include <rhi/qshaderdescription.h>

#include <algorithm>
#include <cstring>
#include <deque>
#include <iterator>
#include <mutex>

#ifdef Q_OS_WIN
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <dlfcn.h>
#endif

Q_LOGGING_CATEGORY(lcShader, "VL.Qt.Shader")

namespace vlqt::res::shader {

namespace {

const char* stageName(int stage) { return stage == VLQT_SHADER_STAGE_VERTEX ? "vertex" : "fragment"; }

// Directory of vlqt.dll / libvlqt.so (the builtin shaders lie next to it in shaders/).
QString qtNativeDir()
{
#ifdef Q_OS_WIN
    HMODULE module = nullptr;
    wchar_t buf[MAX_PATH * 2];
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&qtNativeDir), &module))
        return {};
    const DWORD n = GetModuleFileNameW(module, buf, DWORD(std::size(buf)));
    if (n == 0 || n >= std::size(buf))
        return {};
    return QFileInfo(QString::fromWCharArray(buf, int(n))).absolutePath();
#else
    Dl_info info{};
    if (!dladdr(reinterpret_cast<void*>(&qtNativeDir), &info) || !info.dli_fname)
        return {};
    return QFileInfo(QString::fromUtf8(info.dli_fname)).absolutePath();
#endif
}

// QShader::Source the shaders must carry for the backend of the process (-1 = not checked).
int backendSource()
{
    switch (QQuickWindow::graphicsApi()) {
    case QSGRendererInterface::Direct3D11:
    case QSGRendererInterface::Direct3D12: return QShader::HlslShader;
    case QSGRendererInterface::OpenGL: return QShader::GlslShader;
    case QSGRendererInterface::Vulkan: return QShader::SpirvShader;
    case QSGRendererInterface::Metal: return QShader::MslShader;
    default: return -1;
    }
}

const char* sourceName(int source)
{
    switch (source) {
    case QShader::HlslShader: return "HLSL (D3D)";
    case QShader::GlslShader: return "GLSL (OpenGL)";
    case QShader::SpirvShader: return "SPIR-V (Vulkan)";
    case QShader::MslShader: return "MSL (Metal)";
    default: return "?";
    }
}

bool hasVariant(const QShader& s, int source)
{
    for (const QShaderKey& k : s.availableShaders()) {
        if (k.source() == source)
            return true;
        if (source == QShader::HlslShader && k.source() == QShader::DxbcShader)
            return true;
    }
    return false;
}

// Reflection checks the ShaderEffect item relies on; empty = fine.
std::string checkReflection(const QShader& s)
{
    const QShaderDescription d = s.description();
    const auto blocks = d.uniformBlocks();
    if (blocks.size() > 1)
        return "more than one uniform block (ShaderEffect takes one std140 block at binding 0, like Qt's ShaderEffect)";
    if (!d.pushConstantBlocks().isEmpty() || !d.storageBlocks().isEmpty() || !d.storageImages().isEmpty())
        return "push constants, storage buffers or storage images are not supported";
    if (!d.separateImages().isEmpty() || !d.separateSamplers().isEmpty())
        return "separate images/samplers are not supported (use sampler2D)";
    for (const auto& b : blocks) {
        for (const auto& m : b.members) {
            if (m.name == "qt_Matrix" && (m.type != QShaderDescription::Mat4 || !m.arrayDims.isEmpty()))
                return "qt_Matrix must be a mat4";
            if (m.name == "qt_Opacity" && (m.type != QShaderDescription::Float || !m.arrayDims.isEmpty()))
                return "qt_Opacity must be a float";
        }
    }
    for (const auto& smp : d.combinedImageSamplers()) {
        if (smp.type != QShaderDescription::Sampler2D)
            return "sampler '" + smp.name.toStdString() + "' is not a sampler2D";
        if (smp.binding < 1 || smp.binding > 15)
            return "sampler '" + smp.name.toStdString() + "' must use a binding 1..15 (binding 0 is the uniform block)";
    }
    if (s.stage() == QShader::VertexStage) {
        for (const auto& in : d.inputVariables())
            if (in.location != 0 && in.location != 1 && in.name != "_qt_order") // _qt_order: qsb -b (batchable)
                return "vertex input '" + in.name.toStdString()
                    + "' at a location other than 0 (qt_Vertex) or 1 (qt_MultiTexCoord0)";
    }
    return {};
}

// --- runtime bake (#483) ----------------------------------------------------------------------------------------------

// Targets QShaderBaker generates: only the shader language of the process backend (main thread, at loadSource).
// Unknown backend: SPIR-V, HLSL and GLSL like qsb's usual set.
QList<QShaderBaker::GeneratedShader> bakeTargets(int source)
{
    using G = QShaderBaker::GeneratedShader;
    switch (source) {
    case QShader::HlslShader: return {G(QShader::HlslShader, QShaderVersion(50))};
    case QShader::SpirvShader: return {G(QShader::SpirvShader, QShaderVersion(100))};
    case QShader::MslShader: return {G(QShader::MslShader, QShaderVersion(12))};
    case QShader::GlslShader:
        // the GL backend of QRhi picks the best version the context supports from what is there
        if (QOpenGLContext::openGLModuleType() == QOpenGLContext::LibGLES)
            return {G(QShader::GlslShader, QShaderVersion(300, QShaderVersion::GlslEs)),
                G(QShader::GlslShader, QShaderVersion(100, QShaderVersion::GlslEs))};
        return {G(QShader::GlslShader, QShaderVersion(330)), G(QShader::GlslShader, QShaderVersion(150)),
            G(QShader::GlslShader, QShaderVersion(120))};
    default:
        return {G(QShader::SpirvShader, QShaderVersion(100)), G(QShader::HlslShader, QShaderVersion(50)),
            G(QShader::GlslShader, QShaderVersion(100, QShaderVersion::GlslEs)),
            G(QShader::GlslShader, QShaderVersion(120)), G(QShader::GlslShader, QShaderVersion(150))};
    }
}

// glslang's log ("ERROR: <file>:<line>: <text>", "compilation terminated" / "N compilation errors" lines) -> one
// line per problem, "<name>, line <n>: <text>"; anything else is kept as "<name>: <text>".
std::string cleanBakeErrors(const QString& name, const QString& log)
{
    static const QRegularExpression located(QStringLiteral("^(?:ERROR|WARNING):\\s*[^:]*:(\\d+):\\s*(.*)$"));
    static const QRegularExpression summary(QStringLiteral("^ERROR:\\s*\\d+ compilation errors"));
    QStringList lines;
    for (const QString& raw : log.split(u'\n')) {
        const QString line = raw.trimmed();
        if (line.isEmpty() || summary.match(line).hasMatch())
            continue;
        const auto m = located.match(line);
        if (m.hasMatch() && m.captured(2).endsWith(QStringLiteral("compilation terminated")))
            continue; // glslang's follow-up to the first error, no information
        lines <<(m.hasMatch() ? name + QStringLiteral(", line ") + m.captured(1) + QStringLiteral(": ") + m.captured(2)
                               : name + QStringLiteral(": ") + line);
    }
    if (lines.isEmpty())
        lines << name + QStringLiteral(": bake failed");
    return lines.join(u'\n').toStdString();
}

// Process wide bake cache: key = stage + backend source + SHA-1 of the source, value = the checked QShader or the
// error text (a failing source is not baked again either). Worker threads: guarded by s_cacheMutex. FIFO with LRU
// touch on hits, kMaxCache entries. Cancelled bakes are not stored.
struct CacheEntry
{
    QByteArray key;
    QShader shader;
    std::string error;
};
constexpr size_t kMaxCache = 64;
std::mutex s_cacheMutex;
std::deque<CacheEntry> s_cache;

QByteArray cacheKey(int stage, int source, const QByteArray& code)
{
    return QByteArray::number(stage) + ':' + QByteArray::number(source) + ':'
        + QCryptographicHash::hash(code, QCryptographicHash::Sha1).toHex();
}

bool cacheFind(const QByteArray& key, QShader& shader, std::string& error)
{
    std::lock_guard lock(s_cacheMutex);
    for (auto it = s_cache.begin(); it != s_cache.end(); ++it) {
        if (it->key != key)
            continue;
        CacheEntry e = std::move(*it);
        s_cache.erase(it);
        shader = e.shader;
        error = e.error;
        s_cache.push_back(std::move(e));
        return true;
    }
    return false;
}

void cacheStore(const QByteArray& key, const QShader& shader, const std::string& error)
{
    std::lock_guard lock(s_cacheMutex);
    for (auto it = s_cache.begin(); it != s_cache.end(); ++it) {
        if (it->key == key) {
            s_cache.erase(it);
            break;
        }
    }
    s_cache.push_back({key, shader, error});
    while (s_cache.size() > kMaxCache)
        s_cache.pop_front();
}

// glslang's process init and shared pools are not documented as thread safe across bakes; bakes are rare (live
// edits), so one at a time (each with its own QShaderBaker).
std::mutex s_bakeMutex;

// Checks of a decoded or baked shader against the request; empty = fine. where = " (<path or name>)".
std::string checkShader(const QShader& s, int stage, int source, bool baked, const std::string& where)
{
    const QShader::Stage want = stage == VLQT_SHADER_STAGE_VERTEX ? QShader::VertexStage : QShader::FragmentStage;
    if (s.stage() != want)
        return std::string("expected a ") + stageName(stage) + " shader, the .qsb holds another stage" + where;
    if (source >= 0 && !hasVariant(s, source))
        return std::string(baked ? "the bake produced no " : "the .qsb has no ") + sourceName(source)
            + (baked ? " variant" : " variant for this graphics backend (bake with qsb --hlsl 50 --glsl ... )") + where;
    if (const std::string e = checkReflection(s); !e.empty())
        return e + where;
    return {};
}

} // namespace

class ShaderKind final : public Kind
{
public:
    const char* name() const override { return "shader"; }

    std::string decode(Work& work) override
    {
        auto& w = static_cast<ShaderResource&>(work);
        if (!w.m_code.isEmpty())
            return bake(w);
        QFile f(w.m_path);
        if (!f.exists())
            return std::string(stageName(w.m_stage)) + " shader: file not found: " + w.m_path.toStdString();
        if (!f.open(QIODevice::ReadOnly))
            return std::string(stageName(w.m_stage)) + " shader: cannot read " + w.m_path.toStdString() + ": "
                + f.errorString().toStdString();
        const QByteArray bytes = f.readAll();
        if (w.cancel.load())
            return "cancelled";
        const QShader s = QShader::fromSerialized(bytes);
        const std::string where = " (" + w.m_path.toStdString() + ")";
        if (!s.isValid())
            return "not a .qsb shader, bake it with Qt's qsb tool" + where;
        if (std::string e = checkShader(s, w.m_stage, w.m_source, false, where); !e.empty())
            return e;
        w.m_shader = s;
        return {};
    }

    // Source (#483): cache hit or QShaderBaker for the targets noted at loadSource, then the same checks as a .qsb.
    static std::string bake(ShaderResource& w)
    {
        const QByteArray key = cacheKey(w.m_stage, w.m_source, w.m_code);
        QShader s;
        std::string err;
        if (cacheFind(key, s, err)) {
            qCDebug(lcShader, "%s: bake cache hit", qPrintable(w.m_path));
        } else {
            if (w.cancel.load())
                return "cancelled";
            {
                std::lock_guard lock(s_bakeMutex);
                if (w.cancel.load())
                    return "cancelled";
                QShaderBaker baker;
                baker.setSourceString(w.m_code,
                    w.m_stage == VLQT_SHADER_STAGE_VERTEX ? QShader::VertexStage : QShader::FragmentStage);
                baker.setGeneratedShaderVariants({QShader::StandardShader});
                baker.setGeneratedShaders(w.m_targets);
                s = baker.bake();
                if (!s.isValid())
                    err = cleanBakeErrors(w.m_path, baker.errorMessage());
            }
            if (w.cancel.load())
                return "cancelled";
            if (err.empty())
                err = checkShader(s, w.m_stage, w.m_source, true, " (" + w.m_path.toStdString() + ")");
            if (!err.empty())
                s = QShader();
            cacheStore(key, s, err);
        }
        if (!err.empty())
            return err;
        w.m_shader = s;
        return {};
    }

    int64_t upload(Work&, int64_t, bool& done, std::string&) override
    {
        done = true; // no GPU upload for shaders
        return 0;
    }
};

Kind* kind()
{
    static ShaderKind s_kind;
    return &s_kind;
}

int load(uint64_t id, const char* utf8Path, int stage)
{
    if (!utf8Path || !*utf8Path) {
        qCWarning(lcShader, "load_shader: empty path");
        return badValueRc;
    }
    if (stage != VLQT_SHADER_STAGE_VERTEX && stage != VLQT_SHADER_STAGE_FRAGMENT) {
        qCWarning(lcShader, "load_shader: unknown stage %d", stage);
        return badValueRc;
    }
    auto w = std::make_shared<ShaderResource>();
    QString path = QString::fromUtf8(utf8Path);
    static const QString builtin = QStringLiteral("builtin:");
    if (path.startsWith(builtin)) {
        const QString name = path.mid(builtin.size());
        if (name.isEmpty() || name.contains(u'/') || name.contains(u'\\') || name.contains(QStringLiteral(".."))) {
            qCWarning(lcShader, "load_shader: invalid builtin name '%s'", qPrintable(name));
            return badValueRc;
        }
        static const QString dir = qtNativeDir();
        path = dir + QStringLiteral("/shaders/") + name
            + (stage == VLQT_SHADER_STAGE_VERTEX ? QStringLiteral(".vert.qsb") : QStringLiteral(".frag.qsb"));
    }
    w->m_path = path;
    w->m_stage = stage;
    w->m_source = backendSource();
    qCDebug(lcShader, "load %s shader %s", stageName(stage), qPrintable(path));
    return start(id, std::move(w), VLQT_RES_KIND_SHADER);
}

int loadSource(uint64_t id, const char* utf8Source, int stage)
{
    if (!utf8Source || !*utf8Source) {
        qCWarning(lcShader, "load_shader_source: empty source");
        return badValueRc;
    }
    if (stage != VLQT_SHADER_STAGE_VERTEX && stage != VLQT_SHADER_STAGE_FRAGMENT) {
        qCWarning(lcShader, "load_shader_source: unknown stage %d", stage);
        return badValueRc;
    }
    auto w = std::make_shared<ShaderResource>();
    w->m_code = QByteArray(utf8Source); // copied: the caller's buffer is only valid during the call
    w->m_path = stage == VLQT_SHADER_STAGE_VERTEX ? QStringLiteral("vertex code") : QStringLiteral("fragment code");
    w->m_stage = stage;
    w->m_source = backendSource();
    w->m_targets = bakeTargets(w->m_source);
    qCDebug(lcShader, "load %s (%lld bytes)", qPrintable(w->m_path), static_cast<long long>(w->m_code.size()));
    return start(id, std::move(w), VLQT_RES_KIND_SHADER);
}

namespace {

// GLSL name of a reflected variable type, "other" for everything the pin model does not map.
const char* glslTypeName(QShaderDescription::VariableType t)
{
    using D = QShaderDescription;
    switch (t) {
    case D::Float: return "float";
    case D::Vec2: return "vec2";
    case D::Vec3: return "vec3";
    case D::Vec4: return "vec4";
    case D::Int: return "int";
    case D::Int2: return "ivec2";
    case D::Int3: return "ivec3";
    case D::Int4: return "ivec4";
    case D::Uint: return "uint";
    case D::Uint2: return "uvec2";
    case D::Uint3: return "uvec3";
    case D::Uint4: return "uvec4";
    case D::Bool: return "bool";
    case D::Mat2: return "mat2";
    case D::Mat3: return "mat3";
    case D::Mat4: return "mat4";
    case D::Sampler2D: return "sampler2D";
    default: return "other";
    }
}

} // namespace

int reflect(const char* utf8Source, int stage, char* buf, int bufLen)
{
    if (!utf8Source || !*utf8Source || bufLen < 0 || (!buf && bufLen > 0)
        || (stage != VLQT_SHADER_STAGE_VERTEX && stage != VLQT_SHADER_STAGE_FRAGMENT))
        return badValueRc;
    const bool vertex = stage == VLQT_SHADER_STAGE_VERTEX;

    std::string text;
    {
        std::lock_guard lock(s_bakeMutex);
        QShaderBaker baker;
        baker.setSourceString(QByteArray(utf8Source), vertex ? QShader::VertexStage : QShader::FragmentStage);
        baker.setGeneratedShaderVariants({QShader::StandardShader});
        baker.setGeneratedShaders({QShaderBaker::GeneratedShader(QShader::SpirvShader, QShaderVersion(100))});
        const QShader s = baker.bake();
        if (!s.isValid()) {
            text = "error\n" + cleanBakeErrors(vertex ? QStringLiteral("vertex code") : QStringLiteral("fragment code"),
                baker.errorMessage());
        } else {
            text = "ok";
            const QShaderDescription d = s.description();
            for (const auto& block : d.uniformBlocks()) {
                auto members = block.members;
                std::stable_sort(members.begin(), members.end(),
                    [](const auto& a, const auto& b) { return a.offset < b.offset; });
                for (const auto& m : members) {
                    text += "\nuniform " + m.name.toStdString() + ' ' + glslTypeName(m.type) + ' '
                        + std::to_string(m.arrayDims.isEmpty() ? 0 : m.arrayDims.first());
                }
            }
            auto samplers = d.combinedImageSamplers();
            std::stable_sort(samplers.begin(), samplers.end(),
                [](const auto& a, const auto& b) { return a.binding < b.binding; });
            for (const auto& smp : samplers)
                text += "\nsampler " + smp.name.toStdString() + ' ' + std::to_string(smp.binding);
        }
    }
    const int needed = static_cast<int>(text.size());
    if (bufLen > 0)
        memcpy(buf, text.data(), static_cast<size_t>(std::min(needed, bufLen)));
    return needed;
}

std::shared_ptr<ShaderResource> ready(uint64_t id)
{
    return std::static_pointer_cast<ShaderResource>(readyWork(id, VLQT_RES_KIND_SHADER));
}

const QShader& defaultVertexShader()
{
    static const QShader s = [] {
        // Qt Quick's own ShaderEffect default (qsgrhishadereffectnode.cpp); the path has been stable through Qt 6.
        const QString known = QStringLiteral(":/qt-project.org/scenegraph/shaders_ng/shadereffect.vert.qsb");
        QString path = QFile::exists(known) ? known : QString();
        if (path.isEmpty()) {
            QDirIterator it(QStringLiteral(":/qt-project.org"), QDirIterator::Subdirectories);
            while (it.hasNext()) {
                const QString p = it.next();
                if (p.endsWith(QStringLiteral("/shadereffect.vert.qsb"))) {
                    path = p;
                    break;
                }
            }
        }
        QFile f(path);
        QShader r;
        if (!path.isEmpty() && f.open(QIODevice::ReadOnly))
            r = QShader::fromSerialized(f.readAll());
        if (!r.isValid())
            qCWarning(lcShader, "Qt's default ShaderEffect vertex shader not found: ShaderEffect needs a Vertex Shader");
        return r;
    }();
    return s;
}

} // namespace vlqt::res::shader
