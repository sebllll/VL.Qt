/* snapshot_resource.h - resource kind VLQT_RES_KIND_SNAPSHOT (#363, images package 06).
 *
 * vlqt_res_snapshot grabs one element of a window with QQuickItem::grabToImage (never grabWindow): asynchronous, the
 * window renders the item into an offscreen texture in its next render and reads it back; QQuickItemGrabResult::ready
 * arrives on the main thread in that or a later pump. The result is a CPU copy (QImage ARGB32_Premultiplied = BGRA8
 * premultiplied in memory), no texture and no upload budget. vlqt_res_read_pixels copies it out.
 *
 * Content comes through the manager's external path (resources.h beginExternal / finishExternal): Pending/Failed ->
 * Decoding (grab in flight) -> Ready | Failed; a grab on a Ready id keeps the old pixels readable until the new ones
 * are there. Only the newest grab of an id counts; older ones in flight are kept alive (Qt still references them)
 * but their result is ignored.
 *
 * A grab never stays in Decoding: endFrame (every pump) fails it when the element or the window was destroyed, the
 * window is no longer exposed (an offscreen scene, never exposed: no longer bound; while bound it is marked dirty so
 * that render_batch renders it, #128) or nothing came for timeoutMs. Released ids drop their result. shutdown drops every grab.
 * Main thread only.
 */
#pragma once

#include "resources.h"

#include <QtGui/QImage>

#include <cstdint>

struct vlqt_pixel_desc;

namespace vlqt {
struct Scene;
}

namespace vlqt::res::snap {

// The Work of a Ready snapshot: the pixels, ARGB32_Premultiplied, devicePixelRatio 1.
struct SnapshotWork final : Work
{
    QImage image;
};

// The kind for the manager (resources.cpp kindOf).
Kind* kind();

// --- C API backends (vlqt.h), main thread ---
// Element elementId (0 = the window root) of scene. 0 / -7 / -9; element errors end in Failed (RES_EV_ERROR).
int start(uint64_t id, Scene& scene, unsigned elementId, float scale);
// Like start, with the scale chosen from the element's frame: longest edge <= maxEdgePx, scale <= 1 (DIP * scale,
// independent of the DPR). 0 / -7 (maxEdgePx <= 0) / -9.
int startFit(uint64_t id, Scene& scene, unsigned elementId, int maxEdgePx);
int readPixels(uint64_t id, void* dst, int cap, vlqt_pixel_desc* out);

// --- Cost of the snapshots for the F5 Resources page (#548) ---
// Grab start (startImpl) to a successful delivery (onReady), over the last ~10 s; every snapshot counts.
struct Stats
{
    int perSecond = 0;  // delivered within the last second
    int inWindow = 0;   // delivered within the last 10 s (0: lastMs / maxMs are meaningless)
    double lastMs = 0;  // duration of the most recent delivery
    double maxMs = 0;   // longest duration within the last 10 s
};
void stats(Stats& out); // main thread, no allocation

// --- Frame hooks (resources.cpp), main thread ---
void endFrame(); // gives up hanging grabs, deletes finished ones
void shutdown(); // drops every grab (before the manager frees the resources)

} // namespace vlqt::res::snap
