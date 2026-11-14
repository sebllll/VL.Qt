// TEST ONLY exports of #116 (QtTexture): a second D3D11 device standing in for Stride's, shared textures, readback and
// leak probes for the SmokeTest (vlqt.h, "TEST ONLY (#116 ...)").
#include "offscreen.h"
#include "protocol.h"
#include "vlqt.h"

// TODO: Stubs of #116 task 1.1; task 1.7 implements the D3D11 parts (Windows), the other platforms keep
// VLQT_OFFSCREEN_NOT_SUPPORTED.

extern "C" {

VLQT_API int vlqt_test_d3d11_create_device(int adapter, void** device, void** context)
{
    (void)adapter;
    if (device)
        *device = nullptr;
    if (context)
        *context = nullptr;
    return VLQT_OFFSCREEN_NOT_IMPLEMENTED;
}

VLQT_API int vlqt_test_d3d11_release_device(void* device, void* context)
{
    (void)context;
    if (!device)
        return VLQT_OFFSCREEN_BAD_ARGUMENT;
    return VLQT_OFFSCREEN_NOT_IMPLEMENTED;
}

VLQT_API int vlqt_test_d3d11_create_shared_texture(void* device, int width, int height, int dxgiFormat, void** texture,
                                                   void** ntHandle)
{
    (void)device;
    (void)width;
    (void)height;
    (void)dxgiFormat;
    if (texture)
        *texture = nullptr;
    if (ntHandle)
        *ntHandle = nullptr;
    return VLQT_OFFSCREEN_NOT_IMPLEMENTED;
}

VLQT_API int vlqt_test_d3d11_read_texture(void* texture, unsigned char* out, int cap, int* width, int* height,
                                          int* dxgiFormat)
{
    (void)out;
    (void)cap;
    (void)width;
    (void)height;
    (void)dxgiFormat;
    if (!texture)
        return VLQT_OFFSCREEN_BAD_ARGUMENT;
    return VLQT_OFFSCREEN_NOT_IMPLEMENTED;
}

VLQT_API int vlqt_test_com_ref_count(void* obj)
{
    if (!obj)
        return VLQT_OFFSCREEN_BAD_ARGUMENT;
    return VLQT_OFFSCREEN_NOT_IMPLEMENTED;
}

VLQT_API int vlqt_test_com_release(void* obj)
{
    if (!obj)
        return VLQT_OFFSCREEN_BAD_ARGUMENT;
    return VLQT_OFFSCREEN_NOT_IMPLEMENTED;
}

VLQT_API int vlqt_test_close_handle(void* handle)
{
    if (!handle)
        return VLQT_OFFSCREEN_BAD_ARGUMENT;
    return VLQT_OFFSCREEN_NOT_IMPLEMENTED;
}

VLQT_API int vlqt_test_process_handle_count(void)
{
    return VLQT_OFFSCREEN_NOT_IMPLEMENTED;
}

VLQT_API int vlqt_test_offscreen_state(void* scene, unsigned long long* readFence, unsigned long long* writeFence,
                                       long long* renders)
{
    return vlqt::offscreen::testState(scene, readFence, writeFence, renders);
}

} // extern "C"
