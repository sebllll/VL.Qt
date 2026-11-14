// Offscreen scenes (#116, QtTexture): a Qt scene without a native window (QQuickRenderControl) on the shared D3D11
// device, rendering into a texture of a foreign device (Stride's), ordered by two shared fences. C ABI: the
// vlqt_offscreen_* exports in vlqt.h; design: docs/decisions/render-texture.md. Main thread only.
#pragma once

namespace vlqt::offscreen {

// Backend of vlqt_test_offscreen_state (test_offscreen.cpp): fence values and render count of a bound scene.
// Out pointers may be null. Returns a vlqt_offscreen_result code (protocol.h).
int testState(void* scene, unsigned long long* readFence, unsigned long long* writeFence, long long* renders);

} // namespace vlqt::offscreen
