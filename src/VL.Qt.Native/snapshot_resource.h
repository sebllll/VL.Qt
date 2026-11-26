/* snapshot_resource.h - resource kind VLQT_RESOURCE_KIND_SNAPSHOT: vlqt_snapshot_start grabs one element of a window
 * with QQuickItem::grabToImage (asynchronous); the result is a CPU copy (QImage ARGB32_Premultiplied), no texture and no
 * upload budget, which vlqt_snapshot_read copies out. Content comes through the manager's external path (resources.h
 * beginExternal / finishExternal). A grab never stays in Decoding: endFrame fails it when the element or the window is
 * gone, the window is not exposed or nothing came for timeoutMs. Main thread only.
 * Newest-grab rule, timeout and offscreen scenes: docs/design/resources.md, "External content".
 */
#pragma once

#include "resources/resources.h"

#include <QtGui/QImage>

#include <cstdint>

struct vlqt_pixel_desc;

namespace vlqt {
class Scene;
}

namespace vlqt::res::snapshot {

// The Content of a Ready snapshot: the pixels, ARGB32_Premultiplied, devicePixelRatio 1.
struct SnapshotContent final : Content
{
    QImage image;
};

// The kind for the manager (resources/resource_registry.cpp kindOf).
Kind* kind();

// --- C API backends (vlqt.h), main thread ---
// Element elementId (0 = the window root) of scene. 0 / -7 / -9; element errors end in Failed (RESOURCE_EVENT_ERROR).
int start(uint64_t id, Scene& scene, unsigned elementId, float scale);
// Like start, with the scale chosen from the element's frame: longest edge <= maxEdgePx, scale <= 1 (DIP * scale,
// independent of the DPR). 0 / -7 (maxEdgePx <= 0) / -9.
int startFit(uint64_t id, Scene& scene, unsigned elementId, int maxEdgePx);
int readPixels(uint64_t id, void* dst, int cap, vlqt_pixel_desc* out, int* written, int* required);

// --- Cost of the snapshots for the F5 Resources page ---
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

} // namespace vlqt::res::snapshot
