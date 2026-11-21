namespace VL.Qt.Audio
{
    /// <summary>
    /// Read side of a QtNative audio tap (protocol.h "Audio tap", vlqt.h vlqt_audio_tap_read), as an interface so
    /// that unit tests can feed <see cref="VideoAudioSignal"/> simulated clocks and buffers without Qt. Every member runs
    /// on the audio thread and allocates nothing.
    /// </summary>
    internal interface IAudioTapReader
    {
        /// <summary>Now on the tap clock in ns (vlqt_audio_tap_now_ns: steady clock, QPC on Windows).</summary>
        long NowNs();

        /// <summary>
        /// Copies and consumes up to dst.Length / channels frames of one segment (interleaved floats). An empty dst only
        /// fills <paramref name="state"/>. Returns the frames copied, -7 (channels mismatch, not initialised) or -9 (id
        /// released or freed: output silence).
        /// </summary>
        int Read(ulong tapId, System.Span<float> dst, int channels, out Native.vlqt_audio_tap_state state);

        /// <summary>Drops up to maxFrames frames of one segment without copying (catching up). Returns the frames dropped, -7 or -9.</summary>
        int Drop(ulong tapId, int maxFrames, int channels, out Native.vlqt_audio_tap_state state);
    }

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
