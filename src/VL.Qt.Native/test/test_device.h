// Test device of vlqt_test_device_lost (Windows only): with VLQT_TEST_D3D11ON12=1 the shared device (device.cpp) is a
// D3D11 device on top of a D3D12 device and queue (D3D11On12), because only D3D12 can remove a device for real
// (ID3D12Device5::RemoveDevice); D3D11 has no way to. device.cpp calls these at the places marked "test hook:".
#pragma once

#ifdef _WIN32

#include <d3d11.h>
#include <d3d12.h>
#include <wrl/client.h>

#include <vector>

namespace vlqt::device::test {

// The D3D12 device and queue under a D3D11On12 device, one reference each; empty for a plain D3D11 device.
struct On12
{
    Microsoft::WRL::ComPtr<ID3D12Device> dev12;
    Microsoft::WRL::ComPtr<ID3D12CommandQueue> queue12;
};

// VLQT_TEST_D3D11ON12=1 in the process environment (read at every device creation).
bool isOn12Requested();

// D3D12 device + direct queue on adapter, D3D11 device and immediate context on top (D3D11On12). d3d12.dll is loaded
// dynamically: no link dependency in the shipped QtNative. On success dev, ctx and out hold one reference each (dev and
// ctx must be empty on entry); on a failure all stay null. DXGI_ERROR_DEVICE_REMOVED: only a removed D3D12 singleton was available (dropWindowRhisOn).
HRESULT createOn12(IDXGIAdapter1* adapter, UINT flags, ID3D11Device** dev, ID3D11DeviceContext** ctx,
    D3D_FEATURE_LEVEL* level, On12& out);

// D3D12 singleton (no device factory): the removed D3D12 device lives on as long as a window QRhi holds it, so a new
// D3D11On12 device would sit on the removed one. Lets each window whose QRhi is on one of devices (ID3D11Device*)
// render now (past the render gate): Qt notices the loss at Present and drops its QRhi. The windows get no device until
// rebuild imports the new one; an empty one meanwhile is safer than the released pointer. Returns the windows whose QRhi
// was dropped.
int dropWindowRhisOn(const std::vector<void*>& devices);

// Releases the references of on12 and empties it; returns the references left on the D3D12 device (> 0 = leak).
unsigned long release(On12& on12);

// Makes the next creation of the shared device fail once (vlqt_test_device_lost REMOVE_FAIL_ONCE), so device.cpp has to
// retry it (device.h). Main thread.
void failNextCreation();
// True once after failNextCreation, which it clears: device.cpp then fails this creation. Main thread.
bool takeCreationFailure();

// Removes dev12 (ID3D12Device5::RemoveDevice): the D3D11 device on top is lost from now on. 0, VLQT_ERR_UNSUPPORTED
// without ID3D12Device5 (vlqt_last_error set).
int removeDevice(ID3D12Device* dev12);

} // namespace vlqt::device::test

#endif // _WIN32
