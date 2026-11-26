using System;
using VL.Core;
using VL.Qt.Wire;
using VL.Qt.Interop;

namespace VL.Qt
{
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
