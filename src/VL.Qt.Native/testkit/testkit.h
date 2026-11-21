/* vlqt_testkit – the stand-in "Stride" D3D11 device of the QtTexture tests (SmokeTest), Windows only.
 *
 * A library of its own next to vlqt.dll in the build output, never packed: a second D3D11 device that stands in for
 * Stride's, its shared textures, readback and leak probes. It does not use QtNative; QtNative's adapter comes in as a
 * LUID (vlqt_test_device_luid of vlqt_test.h). Contract as in vlqt_test.h: tests only, may change in any version.
 * C ABI, cdecl. Main thread. Return codes of vlqt_result (vlqt.h): VLQT_ERR_BAD_ARGUMENT for a bad argument,
 * VLQT_ERR_INTERNAL when a D3D call failed; every failure sets the text of vlqt_test_last_error. */
#pragma once

#ifdef VLQT_TESTKIT_BUILD
#  define VLQT_TESTKIT_API __declspec(dllexport)
#else
#  define VLQT_TESTKIT_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* UTF-8 text of the last failing call of this library on the calling thread (empty = none); valid until the next
 * failing call on the same thread. */
VLQT_TESTKIT_API const char* vlqt_test_last_error(void);
/* Creates a D3D11 device (BGRA support, feature level >= 11_0) with its immediate context, both with one reference
 * owned by the caller. adapter: 0 = the adapter with LUID adapterLuid (LowPart | HighPart << 32; 0 = the default
 * adapter), 1 = WARP (an adapter mismatch for vlqt_offscreen_bind, adapterLuid ignored). 0, VLQT_ERR_BAD_ARGUMENT
 * (null out, unknown adapter, no adapter with that LUID), VLQT_ERR_INTERNAL creation failed. */
VLQT_TESTKIT_API int vlqt_test_d3d11_create_device(int adapter, unsigned long long adapterLuid, void** device, void** context);
/* Releases the references of vlqt_test_d3d11_create_device on context and device, like Stride disposing its device.
 * Returns the references left on the device (0 = freed; > 0 = someone, e.g. a bound offscreen scene, still holds it),
 * VLQT_ERR_BAD_ARGUMENT null device. */
VLQT_TESTKIT_API int vlqt_test_d3d11_release_device(void* device, void* context);
/* Creates a Texture2D on device as Stride does for QtTexture: 1 mip, bind flags RENDER_TARGET | SHADER_RESOURCE, misc
 * flags SHARED | SHARED_NTHANDLE, format dxgiFormat (typeless allowed), and an NT handle of it (GENERIC_ALL). The
 * caller owns one reference on *texture (release with vlqt_test_com_release) and the handle (close with
 * vlqt_test_close_handle). 0, VLQT_ERR_BAD_ARGUMENT, VLQT_ERR_INTERNAL creation failed. */
VLQT_TESTKIT_API int vlqt_test_d3d11_create_shared_texture(void* device, int width, int height, int dxgiFormat,
                                                          void** texture, void** ntHandle);
/* Reads texture (ID3D11Texture2D*, 4 or 8 bytes per pixel) back through a staging copy on the immediate context of its
 * device, which flushes and waits on the CPU. Rows are tightly packed (width * bytes per pixel) in the texture's
 * format; *width, *height and *dxgiFormat are set on success and on VLQT_ERR_BUFFER_TOO_SMALL. A buffer export as in
 * vlqt.h (top comment). VLQT_ERR_BAD_ARGUMENT, VLQT_ERR_UNSUPPORTED format or more than 2^31 - 1 bytes,
 * VLQT_ERR_INTERNAL D3D failure. */
VLQT_TESTKIT_API int vlqt_test_d3d11_read_texture(void* texture, unsigned char* out, int cap, int* width, int* height,
                                                 int* dxgiFormat, int* written, int* required);
/* COM reference count of obj (IUnknown*: a device of vlqt_test_d3d11_create_device, QtNative's device of
 * vlqt_test_device_info, a texture) without the probe's own AddRef/Release. >= 0, VLQT_ERR_BAD_ARGUMENT null. */
VLQT_TESTKIT_API int vlqt_test_com_ref_count(void* obj);
/* IUnknown::Release on obj; returns the references left, VLQT_ERR_BAD_ARGUMENT null. */
VLQT_TESTKIT_API int vlqt_test_com_release(void* obj);
/* CloseHandle on an NT handle (e.g. of vlqt_test_d3d11_create_shared_texture). 0, VLQT_ERR_BAD_ARGUMENT null,
 * VLQT_ERR_INTERNAL failed. */
VLQT_TESTKIT_API int vlqt_test_close_handle(void* handle);
/* Number of open handles of this process (GetProcessHandleCount), for the leak test. >= 0, VLQT_ERR_INTERNAL failed. */
VLQT_TESTKIT_API int vlqt_test_process_handle_count(void);

#ifdef __cplusplus
}
#endif
