using VL.Lib.IO;

namespace VL.Qt.Elements
{
    /// <summary>Values are Qt VideoOutput.FillMode (Stretch, PreserveAspectFit, PreserveAspectCrop).</summary>
    public enum VideoFillMode
    {
        Stretch = 0,
        Fit = 1,
        Crop = 2,
    }

    /// <summary>
    /// Video from a file (Qt MediaPlayer + VideoOutput, FFmpeg backend). v1: file path only, own clock (no seek,
    /// no sync to vvvv). Position and duration come back through the feedback, 0 until the media is loaded.
    /// </summary>
    public sealed class Video : Element
    {
        public Video() : base(ElementType.Video) { }

        public Path Path { get; init; } = Path.Default;
        /// <summary>Plays while true, pauses (keeps the current frame) while false. Qt default: paused.</summary>
        public bool Playing { get; init; }
        /// <summary>Restarts at the end (MediaPlayer.Infinite); otherwise playback stops at the last frame.</summary>
        public bool Loop { get; init; }
        /// <summary>At the end (Loop off) the last frame stays visible; otherwise the output is cleared. Qt default: clear.</summary>
        public bool KeepLastFrame { get; init; }
        /// <summary>Playback speed factor (1 = normal), must be &gt; 0.</summary>
        public float Rate { get; init; } = 1;
        /// <summary>Linear volume 0..1 (Qt AudioOutput default 1).</summary>
        public float Volume { get; init; } = 1;
        /// <summary>Qt VideoOutput default: Fit.</summary>
        public VideoFillMode FillMode { get; init; } = VideoFillMode.Fit;
        /// <summary>
        /// Audio tap resource id (Video (VL.Audio) only), 0 = none: Qt's own output plays. A tap turns Qt's output
        /// off and sends the sound to the tap (protocol.h "Audio tap"). Not a pin of Video [VL.Qt].
        /// </summary>
        internal ulong AudioTap { get; init; }

        /// <summary>Smallest rate sent to Qt (0 and reverse playback are not supported, use Playing = false to stop).</summary>
        public const float MinRate = 0.01f;

        /// <summary>Rate as sent: &lt; <see cref="MinRate"/> → MinRate, NaN/∞ → 1.</summary>
        public static float ClampRate(float rate) => !float.IsFinite(rate) ? 1f : rate < MinRate ? MinRate : rate;

        /// <summary>Volume as sent: clamped to 0..1, NaN → 0.</summary>
        public static float ClampVolume(float volume) => float.IsNaN(volume) ? 0f : volume < 0 ? 0f : volume > 1 ? 1f : volume;
    }
}
