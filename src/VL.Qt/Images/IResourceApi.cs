using System;
using VL.Core;
using VL.Qt.Wire;
using VL.Qt.Interop;

namespace VL.Qt
{
    /// <summary>The resource C-API of QtNative (vlqt_res_*); a fake in unit tests, which have no Qt.</summary>
    internal interface IResourceApi
    {
        Native.Result Create(int kind, out ulong id);
        Native.Result Release(ulong id);
        Native.Result Poll(Span<byte> buffer, out int written, out int required);
        /// <summary>The state of the resource (<see cref="Protocol.ResourceState"/>) or a negative <see cref="Native.Result"/>.</summary>
        int Wait(ulong id, int timeoutMs);
        /// <summary>upload: <see cref="Protocol.ImageUpload"/>.</summary>
        Native.Result LoadImage(ulong id, string utf8Path, uint flags, int upload);
        Native.Result LoadShader(ulong id, string utf8Path, int stage);
        Native.Result LoadShaderSource(ulong id, string source, int stage);
        /// <summary>upload: <see cref="Protocol.ImageUpload"/>.</summary>
        Native.Result SetPixels(ulong id, in Native.vlqt_pixel_desc desc, ReadOnlySpan<byte> data, int upload);
        int List(Span<Native.vlqt_res_info> buffer);
        void Shutdown();
        Native.Result Snapshot(ulong id, SceneHandle window, uint elementId, float scale);
        Native.Result ReadSnapshot(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int written, out int required);
        Native.Result SnapshotFit(ulong id, SceneHandle window, uint elementId, int maxEdgePx);
        Native.Result ReadPreview(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int written, out int required);
        Native.Result AudioTapInit(ulong id, int channels, int sampleRate);
    }

    internal sealed class NativeResourceApi : IResourceApi
    {
        public Native.Result Create(int kind, out ulong id) => Native.vlqt_res_create(kind, out id);
        public Native.Result Release(ulong id) => Native.vlqt_res_release(id);
        public Native.Result Poll(Span<byte> buffer, out int written, out int required) => Native.ResPoll(buffer, out written, out required);
        public int Wait(ulong id, int timeoutMs) => Native.vlqt_res_wait(id, timeoutMs);
        public Native.Result LoadImage(ulong id, string utf8Path, uint flags, int upload) => Native.vlqt_image_load(id, utf8Path, flags, upload);
        public Native.Result LoadShader(ulong id, string utf8Path, int stage) => Native.vlqt_shader_load(id, utf8Path, stage);
        public Native.Result LoadShaderSource(ulong id, string source, int stage) => Native.vlqt_shader_load_source(id, source, stage);
        public Native.Result SetPixels(ulong id, in Native.vlqt_pixel_desc desc, ReadOnlySpan<byte> data, int upload) => Native.ImageSetPixels(id, in desc, data, upload);
        public int List(Span<Native.vlqt_res_info> buffer) => Native.ResList(buffer);
        public void Shutdown() => Native.vlqt_res_shutdown();
        public Native.Result Snapshot(ulong id, SceneHandle window, uint elementId, float scale) => Native.vlqt_snapshot_start(id, window, elementId, scale);
        public Native.Result ReadSnapshot(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int written, out int required) => Native.SnapshotRead(id, buffer, out desc, out written, out required);
        public Native.Result SnapshotFit(ulong id, SceneHandle window, uint elementId, int maxEdgePx) => Native.vlqt_snapshot_start_fit(id, window, elementId, maxEdgePx);
        public Native.Result ReadPreview(ulong id, Span<byte> buffer, out Native.vlqt_pixel_desc desc, out int written, out int required) => Native.ImageReadPreview(id, buffer, out desc, out written, out required);
        public Native.Result AudioTapInit(ulong id, int channels, int sampleRate) => Native.vlqt_audio_tap_init(id, channels, sampleRate);
    }
}
