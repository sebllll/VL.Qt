using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Stride.Core.Mathematics;
using Stride.Graphics;
// VL.Qt.MultisampleCount (the namespace of this file) would win over the one of Stride.Graphics.
using StrideMultisampleCount = global::Stride.Graphics.MultisampleCount;

namespace VL.Qt.Stride
{
    /// <summary>
    /// The Stride side of a QtTexture: the shared texture and the native pointers and formats QtNative and QtRenderer need
    /// (docs/decisions/render-texture.md; the format mapping is in <see cref="TextureFormats"/>). All D3D11 work on the texture
    /// itself (open, fences, render) is in QtNative.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class StrideInterop
    {
        /// <summary>
        /// Returns the largest width and height of a Texture2D at the device's feature level (D3D11 limits per level;
        /// the QtNative bind accepts up to 16384).
        /// </summary>
        public static int MaxTextureSize(GraphicsDevice device) => device.Features.CurrentProfile switch
        {
            < GraphicsProfile.Level_9_3 => 2048,
            < GraphicsProfile.Level_10_0 => 4096,
            < GraphicsProfile.Level_11_0 => 8192,
            _ => 16384,
        };

        /// <summary>
        /// Creates the texture Qt renders into: shader resource and render target, shared with an NT handle
        /// (D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE). The caller owns it.
        /// </summary>
        public static Texture CreateSharedTexture(GraphicsDevice device, Int2 size, PixelFormat textureFormat, PixelFormat viewFormat)
        {
            var texture = Texture.New(device,
                description: new TextureDescription()
                {
                    Width = size.X,
                    Height = size.Y,
                    ArraySize = 1,
                    MipLevels = 1,
                    Depth = 1,
                    MultisampleCount = StrideMultisampleCount.None,
                    Format = textureFormat,
                    Dimension = TextureDimension.Texture2D,
                    Usage = GraphicsResourceUsage.Default,
                    Flags = TextureFlags.ShaderResource | TextureFlags.RenderTarget,
                    // SharedNthandle alone is not a valid D3D11 combination: it needs Shared (or SharedKeyedmutex).
                    Options = TextureOptions.Shared | TextureOptions.SharedNthandle,
                },
                viewDescription: new TextureViewDescription()
                {
                    Flags = TextureFlags.ShaderResource | TextureFlags.RenderTarget,
                    Format = viewFormat,
                    Type = ViewType.Full,
                });
            // Stride 4.3 creates a named NT handle for SharedNthandle (Texture.SharedHandle) and never closes it: close it
            // here, every bind gets a handle of its own (CreateNtHandle). Texture.SharedHandle is stale afterwards.
            if (texture.SharedHandle != IntPtr.Zero)
                CloseHandle(texture.SharedHandle);
            return texture;
        }

        /// <summary>Returns a new NT handle (read and write access) of the shared texture; close it with <see cref="CloseHandle"/>.</summary>
        public static IntPtr CreateNtHandle(Texture texture)
        {
            var resource = (SharpDX.Direct3D11.Resource)SharpDXInterop.GetNativeResource(texture);
            using var resource1 = resource.QueryInterface<SharpDX.DXGI.Resource1>();
            return resource1.CreateSharedHandle(null, SharpDX.DXGI.SharedResourceFlags.Read | SharpDX.DXGI.SharedResourceFlags.Write, null);
        }

        /// <summary>
        /// Returns the ID3D11Resource* of <paramref name="texture"/> (borrowed; IntPtr.Zero if Stride does not run on Direct3D 11)
        /// and the format of that resource. Stride hides it: a swap chain back buffer has a UNorm resource and an SRgb view
        /// (flip model), a typeless texture a typeless resource (as GetResourceFormat in VL.Stride's SkiaRenderer).
        /// </summary>
        public static IntPtr GetNativeResource(Texture texture, out PixelFormat resourceFormat)
        {
            if (SharpDXInterop.GetNativeResource(texture) is SharpDX.Direct3D11.Texture2D native)
            {
                resourceFormat = (PixelFormat)native.Description.Format;
                return native.NativePointer;
            }
            resourceFormat = texture.Format;
            return IntPtr.Zero;
        }

        /// <summary>Returns Stride's ID3D11Device* (borrowed, no reference added), IntPtr.Zero if Stride does not run on Direct3D 11.</summary>
        public static IntPtr GetNativeDevice(GraphicsDevice device) =>
            SharpDXInterop.GetNativeDevice(device) is SharpDX.Direct3D11.Device d ? d.NativePointer : IntPtr.Zero;

        /// <summary>Returns Stride's immediate ID3D11DeviceContext* (borrowed), IntPtr.Zero if Stride does not run on Direct3D 11.</summary>
        public static IntPtr GetNativeImmediateContext(GraphicsDevice device) =>
            SharpDXInterop.GetNativeDeviceContext(device) is SharpDX.Direct3D11.DeviceContext c ? c.NativePointer : IntPtr.Zero;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
