/* vlqt_testkit – the stand-in "Stride" D3D11 device of the QtTexture tests (SmokeTest), Windows only.
 *
 * A library of its own next to vlqt.dll in the build output, never packed: a second D3D11 device that stands in for
 * Stride's, its shared textures, readback and leak probes. It does not use QtNative; QtNative's adapter comes in as a
 * LUID (vlqt_test_device_luid of vlqt_test.h). Contract as in vlqt_test.h: tests only, may change in any version.
 * C ABI, cdecl. Main thread. Return codes as the offscreen exports (protocol.h): 0 = ok, -7 bad argument
 * (VLQT_OFFSCREEN_BAD_ARGUMENT), -1 a D3D call failed (text: vlqt_test_last_error). */
#pragma once

#ifdef VLQT_TESTKIT_BUILD
#  define VLQT_TESTKIT_API __declspec(dllexport)
#else
#  define VLQT_TESTKIT_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* UTF-8 text of the last -1 of a function of this library (empty = none); valid until the next failing call. */
VLQT_TESTKIT_API const char* vlqt_test_last_error(void);
/* Creates a D3D11 device (BGRA support, feature level >= 11_0) with its immediate context, both with one reference
 * owned by the caller. adapter: 0 = the adapter with LUID adapterLuid (LowPart | HighPart << 32; 0 = the default
 * adapter), 1 = WARP (an adapter mismatch for vlqt_offscreen_bind, adapterLuid ignored). 0 = ok, -7 bad argument (null
 * out, unknown adapter, no adapter with that LUID), -1 creation failed. */
VLQT_TESTKIT_API int vlqt_test_d3d11_create_device(int adapter, unsigned long long adapterLuid, void** device, void** context);
/* Releases the references of vlqt_test_d3d11_create_device on context and device, like Stride disposing its device.
 * Returns the references left on the device (0 = freed; > 0 = someone, e.g. a bound offscreen scene, still holds it),
 * -7 null device. */
VLQT_TESTKIT_API int vlqt_test_d3d11_release_device(void* device, void* context);
/* Creates a Texture2D on device as Stride does for QtTexture: 1 mip, bind flags RENDER_TARGET | SHADER_RESOURCE, misc
 * flags SHARED | SHARED_NTHANDLE, format dxgiFormat (typeless allowed), and an NT handle of it (GENERIC_ALL). The
 * caller owns one reference on *texture (release with vlqt_test_com_release) and the handle (close with
 * vlqt_test_close_handle). 0 = ok, -7 bad argument, -1 creation failed. */
VLQT_TESTKIT_API int vlqt_test_d3d11_create_shared_texture(void* device, int width, int height, int dxgiFormat,
                                                          void** texture, void** ntHandle);
/* Reads texture (ID3D11Texture2D*, 4 or 8 bytes per pixel) back through a staging copy on the immediate context of its
 * device, which flushes and waits on the CPU. Rows are tightly packed (width * bytes per pixel) in the texture's
 * format; *width, *height and *dxgiFormat are set on success and on -required. Returns the bytes written, -required if
 * cap is too small (nothing copied), -7 bad argument, unsupported format or more than 2^31 - 1 bytes, -1 D3D failure. */
VLQT_TESTKIT_API int vlqt_test_d3d11_read_texture(void* texture, unsigned char* out, int cap, int* width, int* height,
                                                 int* dxgiFormat);
/* COM reference count of obj (IUnknown*: a device of vlqt_test_d3d11_create_device, QtNative's device of
 * vlqt_test_device_info, a texture) without the probe's own AddRef/Release. >= 0, -7 null. */
VLQT_TESTKIT_API int vlqt_test_com_ref_count(void* obj);
/* IUnknown::Release on obj; returns the references left, -7 null. */
VLQT_TESTKIT_API int vlqt_test_com_release(void* obj);
/* CloseHandle on an NT handle (e.g. of vlqt_test_d3d11_create_shared_texture). 0 = ok, -7 null, -1 failed. */
VLQT_TESTKIT_API int vlqt_test_close_handle(void* handle);
/* Number of open handles of this process (GetProcessHandleCount), for the leak test. >= 0, -1 failed. */
VLQT_TESTKIT_API int vlqt_test_process_handle_count(void);

#ifdef __cplusplus
}
#endif
