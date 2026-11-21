#include "app/fonts.h"
#include "app/app.h"
#include "app/exports.h"
#include "app/log.h"
#include "test/test_hooks.h"
#include "vlqt.h"

#include <QtCore/QByteArray>
#include <QtCore/QDateTime>
#include <QtCore/QFileInfo>
#include <QtCore/QStringList>
#include <QtCore/QThread>
#include <QtGui/QFontDatabase>
#include <QtGui/QGuiApplication>

#include <string>
#include <utility>
#include <vector>

using vlqt::fail;
using vlqt::g_app;
using vlqt::guarded;
using vlqt::log::emitLog;

namespace {

// App fonts (LoadFont node). One entry per font file, shared by every LoadFont node that names it.
// Thread: QFontDatabase app fonts and every text layout belong to the Qt GUI thread, which is the vvvv main thread
// (vlqt_app_init, basic render loop, no render thread). Adding there means the next layout in the same pump already
// sees the family, so no queue to another thread is needed; calls from other threads are rejected.
// Key: canonical path + size + last-modified time. Qt reads the file when it is registered; a later change on disk is
// not watched, but the next add after a change (path edit, app restart) registers the new version as its own entry.
// A deleted file keeps working until its entry is removed.
// Removal: refs 0 only marks the entry; fontsEndFrame removes it at the end of the pump if nobody took it again, so a
// path swap between nodes, a node re-created by a live edit or an app restart (F8) does not remove and re-add the font.
// Texts still naming a removed family keep their glyphs until their next layout, then Qt substitutes another font.
// The QGuiApplication is never deleted, so fonts still registered at shutdown stay with the process.
struct FontEntry {
    QString key;
    int qtId = -1;
    int refs = 0;
    QByteArray families; // '\n'-separated, UTF-8
};
std::vector<std::pair<unsigned long long, FontEntry>> g_fonts; // few entries: linear search
unsigned long long g_nextFontHandle = 1;

FontEntry* findFont(unsigned long long handle)
{
    for (auto& [h, e] : g_fonts)
        if (h == handle)
            return &e;
    return nullptr;
}

} // namespace

namespace vlqt {

void fontsEndFrame()
{
    for (auto it = g_fonts.begin(); it != g_fonts.end();) {
        if (it->second.refs > 0) {
            ++it;
            continue;
        }
        QFontDatabase::removeApplicationFont(it->second.qtId);
        emitLog(0, "vlqt.font", "Font removed: " + it->second.families);
        it = g_fonts.erase(it);
    }
}

} // namespace vlqt

// test hook: the references of a font handle (test/test_hooks.h).
namespace vlqt::test {

int fontRefs(unsigned long long handle)
{
    const FontEntry* e = findFont(handle);
    return e ? e->refs : fail(VLQT_ERR_UNKNOWN_ID, "test_font_refs: unknown handle " + std::to_string(handle));
}

} // namespace vlqt::test

extern "C" {

VLQT_API int vlqt_font_add(const char* utf8Path, unsigned long long* outHandle, char* families, int familiesCap,
    int* written, int* required)
{
    return guarded([&]() -> int {
        if (outHandle)
            *outHandle = 0;
        if (const int rc = vlqt::beginBuffer(families, familiesCap, written, required); rc != VLQT_OK)
            return rc;
        if (!g_app)
            return fail(VLQT_ERR_NOT_INITIALIZED, "font_add: app not initialized");
        if (QThread::currentThread() != g_app->thread())
            return fail(VLQT_ERR_WRONG_THREAD, "font_add: not on the Qt GUI thread (the vvvv main thread)");
        if (!utf8Path || !outHandle)
            return fail(VLQT_ERR_BAD_ARGUMENT, "font_add: null argument");
        const QFileInfo info(QString::fromUtf8(utf8Path));
        if (!info.isFile())
            return fail(VLQT_ERR_IO, QStringLiteral("font file not found: %1").arg(info.filePath()));
        QString key = info.canonicalFilePath();
#ifdef _WIN32
        key = key.toLower(); // NTFS paths are case-insensitive
#endif
        key += QStringLiteral("|%1|%2").arg(info.size()).arg(info.lastModified().toMSecsSinceEpoch());
        unsigned long long handle = 0;
        FontEntry* entry = nullptr;
        for (auto& [h, e] : g_fonts)
            if (e.key == key) {
                handle = h;
                entry = &e;
                break;
            }
        if (!entry) {
            const int id = QFontDatabase::addApplicationFont(info.filePath());
            const QStringList names = id < 0 ? QStringList() : QFontDatabase::applicationFontFamilies(id);
            if (names.isEmpty()) {
                if (id >= 0)
                    QFontDatabase::removeApplicationFont(id);
                return fail(VLQT_ERR_UNSUPPORTED, QStringLiteral("not a font file Qt can read: %1").arg(info.filePath()));
            }
            FontEntry e;
            e.key = key;
            e.qtId = id;
            e.families = names.join(QLatin1Char('\n')).toUtf8();
            handle = g_nextFontHandle++;
            g_fonts.emplace_back(handle, std::move(e));
            entry = &g_fonts.back().second;
            emitLog(1, "vlqt.font", "Font registered: " + entry->families + " (" + info.filePath().toUtf8() + ")");
        }
        // Too small: no reference taken; an entry registered just now without one is removed at the end of the pump
        // unless the call with a larger buffer finds it first.
        if (const int rc = vlqt::copyText(entry->families.constData(), int(entry->families.size()), families, familiesCap,
                written, required);
            rc != VLQT_OK)
            return rc;
        entry->refs++;
        *outHandle = handle;
        return VLQT_OK;
    });
}

VLQT_API int vlqt_font_release(unsigned long long handle)
{
    return guarded([&]() -> int {
        if (g_app && QThread::currentThread() != g_app->thread())
            return fail(VLQT_ERR_WRONG_THREAD, "font_release: not on the Qt GUI thread (the vvvv main thread)");
        FontEntry* e = findFont(handle);
        if (!e || e->refs <= 0)
            return fail(VLQT_ERR_UNKNOWN_ID, "font_release: unknown handle " + std::to_string(handle));
        e->refs--; // removed at the end of the pump if still 0 (fontsEndFrame)
        return VLQT_OK;
    });
}

} // extern "C"
