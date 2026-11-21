using System;
using System.Runtime.InteropServices;
using VL.Qt.Wire;

namespace VL.Qt.Interop
{
    internal static partial class Native
    {
        /// <summary>
        /// State of an audio tap (vlqt.h vlqt_audio_tap_state, 96 bytes). Times in ns of <see cref="vlqt_audio_tap_now_ns"/>,
        /// media times in us on Qt's audio axis (continues across loop wraps). "First frame" = the first frame the read
        /// delivered or dropped, else the next one in the ring. Contract: protocol.h "Audio tap".
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct vlqt_audio_tap_state
        {
            /// <summary>vlqt_audio_tap_now_ns at the call.</summary>
            public long now_ns;
            /// <summary>When the first frame is due (emit time + k / (sample_rate * rate)); 0 = no frame.</summary>
            public long due_ns;
            /// <summary>Media time of the first frame; 0 = no frame.</summary>
            public long media_us;
            /// <summary>Diagnostics: startTime of the last video frame at the sink (restarts at a loop wrap).</summary>
            public long video_media_us;
            /// <summary>Diagnostics: vlqt_audio_tap_now_ns when that frame reached the sink; 0 = none yet.</summary>
            public long video_ns;
            public ulong overrun_frames;
            public ulong underrun_frames;
            /// <summary>Playback rate of the first frame's segment; 0 = no frame.</summary>
            public double rate;
            /// <summary>Segment counter of the first frame; a change means flush and re-lock.</summary>
            public uint discontinuity;
            /// <summary>Frames of the current segment left in the ring after the call.</summary>
            public int available;
            public int capacity;
            public int channels;
            public int sample_rate;
            /// <summary><see cref="Protocol.AudioTap"/> flags.</summary>
            public int flags;
            private int _reserved0, _reserved1;
        }

        // Audio taps: init on the main thread; read and now on any thread (VL.Audio's audio thread), no allocation.
        /// <summary>Fixes the format of a Pending audio tap (float, interleaved) and makes it Ready.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_audio_tap_init(ulong id, int channels, int sampleRate);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vlqt_audio_tap_read(ulong id, ref float dst, int maxFrames, int channels, out vlqt_audio_tap_state state);

        [DllImport(Lib, EntryPoint = "vlqt_audio_tap_read", CallingConvention = CallingConvention.Cdecl)]
        private static extern int vlqt_audio_tap_read_drop(ulong id, IntPtr dst, int maxFrames, int channels, out vlqt_audio_tap_state state);

        /// <summary>The clock of the audio tap times in ns (steady clock: QPC on Windows = Stopwatch.GetTimestamp converted to ns).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long vlqt_audio_tap_now_ns();

        /// <summary>
        /// Copies and consumes up to dst.Length / channels frames of one segment (interleaved). Returns the frames copied,
        /// <see cref="Result.BadArgument"/> (channels mismatch, not initialised) or <see cref="Result.UnknownId"/> (released id). An empty dst only fills the state.
        /// </summary>
        public static int AudioTapRead(ulong id, Span<float> dst, int channels, out vlqt_audio_tap_state state)
            => vlqt_audio_tap_read(id, ref MemoryMarshal.GetReference(dst), channels > 0 ? dst.Length / channels : 0, channels, out state);

        /// <summary>Drops up to maxFrames frames of one segment without copying (catching up). Returns the frames dropped, <see cref="Result.BadArgument"/> or <see cref="Result.UnknownId"/>.</summary>
        public static int AudioTapDrop(ulong id, int maxFrames, int channels, out vlqt_audio_tap_state state)
            => vlqt_audio_tap_read_drop(id, IntPtr.Zero, maxFrames, channels, out state);
    }
}
