using VL.Qt.Interop;

namespace VL.Qt.Audio
{
    /// <summary>The reader on QtNative (P/Invoke, blittable, no allocation).</summary>
    internal sealed class NativeAudioTapReader : IAudioTapReader
    {
        public static readonly NativeAudioTapReader Instance = new();

        public long NowNs() => Native.vlqt_audio_tap_now_ns();

        public int Read(ulong tapId, System.Span<float> dst, int channels, out Native.vlqt_audio_tap_state state)
            => Native.AudioTapRead(tapId, dst, channels, out state);

        public int Drop(ulong tapId, int maxFrames, int channels, out Native.vlqt_audio_tap_state state)
            => Native.AudioTapDrop(tapId, maxFrames, channels, out state);
    }
}
