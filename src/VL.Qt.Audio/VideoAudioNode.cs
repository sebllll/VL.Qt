using System;
using System.Collections.Generic;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Audio;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Mathematics;
using VL.Model;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Path = VL.Lib.IO.Path;

[assembly: ImportAsIs]

namespace VL.Qt.Audio
{
    /// <summary>
    /// Video from a file whose sound goes to VL.Audio instead of Qt's own output. Same picture and pins as Video
    /// [Qt.Elements]; the sound comes out at Audio, one signal per channel, synchronized to the picture. Connect Output to
    /// Renderer [Qt] or a Group and Audio to AudioOut. Windows only.
    /// </summary>
    [ProcessNode(Name = "Video (VL.Audio)", Category = Categories.Elements)]
    public sealed class VideoAudioNode : IDisposable
    {
        private readonly ElementNode<Video, VideoNode.Props> _node;
        private readonly VideoAudioSignal _signal;
        private readonly ResourceClient _client;
        private readonly NodeErrors _errors;
        private ImageResource? _tap;
        private int _tapSampleRate;
        private int _failedSampleRate; // a refused rate is not retried every frame

        public VideoAudioNode(NodeContext nodeContext)
        {
            _node = new ElementNode<Video, VideoNode.Props>(nodeContext, VideoNode.Create);
            _client = ResourceClient.ForApp(nodeContext.AppHost);
            _errors = new NodeErrors(nodeContext);
            _signal = new VideoAudioSignal(NativeAudioTapReader.Instance);
        }

        /// <summary>Describes the video and returns its sound. A new description is only created when an input changes.</summary>
        /// <param name="size">Size of the element (width, height). The position comes from Transform and Anchor. Size 0 = not drawn.</param>
        /// <param name="transform">Position, rotation and scale; applied around the Anchor (like Skia). Inside Row/Column the layout places the element and Transform is only visual.</param>
        /// <param name="anchor">Point of the element that lies at the origin of its parent space (default TopLeft: the top-left corner; Center: the element is centered on it), resolved against the actual size; Transform rotates/scales around it. Inside Row/Column only the pivot of Transform.</param>
        /// <param name="path">Video file (H.264 MP4 and what FFmpeg decodes). Empty = nothing; errors are logged, nothing is shown.</param>
        /// <param name="play">Plays while true, pauses at the current frame while false.</param>
        /// <param name="loop">Restarts at the end; otherwise playback stops at the end. The sound continues across the restart without a gap.</param>
        /// <param name="keepLastFrame">When playback ends (Loop off), the last frame stays visible; otherwise the element is cleared.</param>
        /// <param name="rate">Playback speed (1 = normal). Values below 0.01 are clamped (no reverse playback). The sound follows like tape: its pitch changes with the rate; a change of the rate may glitch once.</param>
        /// <param name="volume">Volume of the sound at Audio, 0..1 (linear, clamped).</param>
        /// <param name="audioOffset">Delay of the sound against the picture in ms (positive: the sound comes later). Calibrates the latency of display and audio device that VL.Qt cannot know. The sound cannot come earlier than VL.Audio's output latency (buffer + driver) allows; smaller values play as early as possible.</param>
        /// <param name="fillMode">How the video fills Size: Stretch, Fit (keep aspect), Crop (keep aspect, fill).</param>
        /// <param name="opacity">Opacity of the element. 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas).</param>
        /// <param name="clip">Clip children to the element's size.</param>
        /// <param name="debug">Debug overlay for this element only (not its children; the Renderer's Debug shows all). Does not affect layout or hit-testing.</param>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …). Without interaction the element is transparent for input.</param>
        /// <param name="effect">Chain of effect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask), drawn as one effect layer on the element. Without effect the element has no layer.</param>
        /// <param name="audio">The sound of the video, one signal per channel (stereo), at the sample rate of the VL.Audio engine. Silent without a file, without an audio track and while paused.</param>
        /// <param name="position">Playback position in seconds (0 until loaded; last frame).</param>
        /// <param name="duration">Duration of the media in seconds (0 until loaded or without a file; last frame).</param>
        /// <param name="syncError">Measured offset of the sound against the frame at Qt's video sink in ms (Audio Offset and VL.Audio's output latency included, display latency not; positive: the sound is late). NaN while the sound is not locked yet (start, after a jump, pause or underrun). Diagnostics.</param>
        /// <param name="measuredSize">Natural size of the video, in units (0, 0 until the first frame; last frame).</param>
        /// <param name="sceneBounds">Bounds in window coordinates after layout and transform, in units (last frame).</param>
        /// <returns>The video element.</returns>
        // Pin defaults: see RectangleNode (text form, typeof(T) defaults are ignored by vvvvc).
        [return: Pin(Name = "Output")]
        public Element Update(
            [DefaultValue("1.6, 0.9")] Vector2 size,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            Path path,
            [DefaultValue(true)] bool play,
            [DefaultValue(true)] bool loop,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool keepLastFrame,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float rate,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float volume,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(0f)] float audioOffset,
            [DefaultValue(typeof(VideoFillMode), nameof(VideoFillMode.Fit))] VideoFillMode fillMode,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            out IReadOnlyList<AudioSignal> audio,
            out float position,
            out float duration,
            [Pin(Visibility = PinVisibility.Optional)] out float syncError,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            UpdateTap();
            _signal.UpdateOutputLatency();
            _signal.Volume = Video.ClampVolume(volume);
            _signal.AudioOffsetMs = float.IsFinite(audioOffset) ? audioOffset : 0f;

            // Volume goes to the signal; Qt's own output is off while a tap is set. Without a tap (refused) the element is
            // muted, so the sound never comes out of two places.
            var element = _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect),
                new VideoNode.Props(path?.Value ?? string.Empty, play, loop, keepLastFrame, rate, _tap is null ? 0f : 1f, fillMode, _tap?.Id ?? 0));
            _node.ReadOutputs(out measuredSize, out sceneBounds);
            position = _node.Feedback.MediaPosition();
            duration = _node.Feedback.MediaDuration();
            audio = _signal.Outputs;
            syncError = _signal.SyncErrorMs;
            return element;
        }

        // A tap in the engine's sample rate: created on the first Update, replaced when the rate changes. The element
        // switches straight to the new id (Qt never plays in between); the old tap is released after that switch
        // (ResourceClient releases in the next pump, after the commands are applied).
        private void UpdateTap()
        {
            var sampleRate = _signal.EngineSampleRate;
            if ((_tap is not null && sampleRate == _tapSampleRate) || sampleRate == _failedSampleRate)
                return;
            ImageResource tap;
            try
            {
                tap = _client.CreateAudioTap(VideoAudioSignal.Channels, sampleRate);
            }
            catch (InvalidOperationException e)
            {
                _failedSampleRate = sampleRate;
                _errors.Report(e, "Audio tap");
                return;
            }
            _failedSampleRate = 0;
            _errors.Clear("Audio tap");
            var old = _tap;
            _tap = tap;
            _tapSampleRate = sampleRate;
            _signal.TapId = tap.Id;
            _client.Release(old);
        }

        public void Dispose()
        {
            _signal.TapId = 0;
            _node.Dispose();
            _client.Release(_tap);
            _tap = null;
            _signal.Dispose();
            _errors.Dispose();
        }
    }
}
