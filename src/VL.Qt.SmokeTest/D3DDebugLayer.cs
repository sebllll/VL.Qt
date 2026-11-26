using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>
/// ID3D11Debug::ReportLiveDeviceObjects of a device with the debug layer (QtNative's shared device when
/// QSG_RHI_DEBUG_LAYER=1 was set before it was created), read from the info queue instead of the debugger output:
/// the number of live objects per object type. Raw COM calls through the vtables, no D3D11 binding needed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class D3DDebugLayer
{
    private static readonly Guid s_iidDebug = new("79cf2233-7536-4948-9d36-1e4692dc5760");
    private static readonly Guid s_iidInfoQueue = new("6543dbb6-1b48-42f5-ab82-e97ec74326f6");

    // vtable slots (IUnknown = 0..2)
    private const int reportLiveDeviceObjects = 10; // ID3D11Debug
    private const int setMessageCountLimit = 3, clearStoredMessages = 4, getMessage = 5, getNumStoredMessages = 8; // ID3D11InfoQueue
    private const int rldoDetail = 2, rldoIgnoreInternal = 4;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int ReportDelegate(IntPtr self, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SetLimitDelegate(IntPtr self, ulong limit);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ClearDelegate(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate ulong CountDelegate(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int GetMessageDelegate(IntPtr self, ulong index, IntPtr message, ref nuint length);

    private static T Slot<T>(IntPtr com, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), slot * IntPtr.Size));

    /// <summary>
    /// Live objects of the device per type ("Texture2D" ...), or null when the device has no debug layer. The report
    /// is logged to the info queue of the device, which is read and cleared here.
    /// </summary>
    public static Dictionary<string, int>? LiveObjects(IntPtr device)
    {
        if (device == IntPtr.Zero)
            return null;
        var iidDebug = s_iidDebug;
        var iidQueue = s_iidInfoQueue;
        int hr0 = Marshal.QueryInterface(device, in iidDebug, out var debug);
        if (hr0 != 0)
            return null;
        IntPtr queue = IntPtr.Zero;
        try
        {
            int hr1 = Marshal.QueryInterface(device, in iidQueue, out queue);
            if (hr1 != 0)
                return null;
            Slot<SetLimitDelegate>(queue, setMessageCountLimit)(queue, 1_000_000);
            Slot<ClearDelegate>(queue, clearStoredMessages)(queue);
            int hr2 = Slot<ReportDelegate>(debug, reportLiveDeviceObjects)(debug, rldoDetail | rldoIgnoreInternal);
            if (hr2 < 0)
                return null;
            var live = new Dictionary<string, int>();
            ulong count = Slot<CountDelegate>(queue, getNumStoredMessages)(queue);
            var get = Slot<GetMessageDelegate>(queue, getMessage);
            for (ulong i = 0; i < count; i++)
            {
                nuint length = 0;
                get(queue, i, IntPtr.Zero, ref length);
                if (length == 0)
                    continue;
                var buffer = Marshal.AllocHGlobal((int)length);
                try
                {
                    if (get(queue, i, buffer, ref length) < 0)
                        continue;
                    // D3D11_MESSAGE: Category, Severity, ID (3 x int), pDescription, DescriptionByteLength.
                    var description = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(buffer, 16)) ?? "";
                    // "Live ID3D11Device at 0x..., Refcount: 5" and "	Live ID3D11Context at 0x..., Refcount: 2, IntRef: 1" (one
                    // line per object, indented below its parent): count per type, and the device's own reference count.
                    var text = description.TrimStart();
                    if (text.StartsWith("Live ", StringComparison.Ordinal))
                    {
                        var type = text.Split(' ')[1];
                        live[type] = live.GetValueOrDefault(type) + 1;
                        if (type == "ID3D11Device" && text.Contains("Refcount: ", StringComparison.Ordinal)
                            && int.TryParse(text.AsSpan(text.IndexOf("Refcount: ", StringComparison.Ordinal) + 10).ToString().Split(',')[0], out var refs))
                            live["ID3D11Device.Refcount"] = refs;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            Slot<ClearDelegate>(queue, clearStoredMessages)(queue);
            return live;
        }
        finally
        {
            if (queue != IntPtr.Zero)
                Marshal.Release(queue);
            Marshal.Release(debug);
        }
    }
}
