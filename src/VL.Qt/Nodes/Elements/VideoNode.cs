using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Model;
using VL.Qt.Elements;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Video from a file (Qt MediaPlayer + VideoOutput, FFmpeg backend). Connect to Renderer [Qt] or a Group.
    /// Plays on its own clock: no seek, no frame accuracy, no sync to vvvv (use VL.Video for that).
    /// Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP).
    /// Video (VL.Audio) in VL.Qt.Audio mirrors these pins (test VideoAudioNodePinTests).
    /// </summary>
    [ProcessNode(Name = "Video", Category = Categories.Elements)]
    public sealed class VideoNode : IDisposable
    {
        // The path is compared as string (value equality); the Path object is rebuilt only when it changes.
        // Shared with Video (VL.Audio) in VL.Qt.Audio: it sets AudioTap, this node never does.
        internal readonly record struct Props(string Path, bool Play, bool Loop, bool KeepLastFrame, float Rate, float Volume, FillMode FillMode, ulong AudioTap = 0);

        private readonly ElementNodeCore<Video, Props> _node;

        public VideoNode(NodeContext nodeContext)
            => _node = new ElementNodeCore<Video, Props>(nodeContext, Create);

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal VideoNode(Func<string, IDisposable> addWarning)
            => _node = new ElementNodeCore<Video, Props>(addWarning, Create);

        /// <summary>Builds the element from the inputs; also used by Video (VL.Audio) in VL.Qt.Audio.</summary>
        internal static readonly Func<CommonInputs, Props, FeedbackSlot, Video> Create = static (c, p, slot) => new Video(c, slot)
            {
                Path = p.Path.Length == 0 ? Path.Default : new Path(p.Path),
                Playing = p.Play,
                Loop = p.Loop,
                KeepLastFrame = p.KeepLastFrame,
                Rate = p.Rate,
                Volume = p.Volume,
                FillMode = p.FillMode,
                AudioTap = p.AudioTap,
            };

        internal ElementNodeCore<Video, Props> Core => _node;

        private const string FillModePhase = "Fill Mode";

        /// <summary>
        /// Returns the fill mode to send: a video cannot tile, so Tile becomes Stretch and the node shows a warning until
        /// another mode is set. Also used by Video (VL.Audio) in VL.Qt.Audio.
        /// </summary>
        internal static FillMode ResolveFillMode(FillMode fillMode, NodeErrors errors)
        {
            if (fillMode != FillMode.Tile)
            {
                errors.Clear(FillModePhase);
                return fillMode;
            }
            errors.Warn("Fill Mode Tile is not supported by a video, it is drawn as Stretch.", FillModePhase);
            return FillMode.Stretch;
        }

        /// <summary>Describes the video. A new description is only created when an input changes.</summary>
        /// <param name="size">Size of the element (width, height). The position comes from Transform and Anchor. Size 0 = not drawn.</param>
        /// <param name="transform">Position, rotation and scale; applied around the Anchor (like Skia). Inside Row/Column the layout places the element and Transform is only visual.</param>
        /// <param name="anchor">Point of the element that lies at the origin of its parent space (default TopLeft: the top-left corner; Center: the element is centered on it), resolved against the actual size; Transform rotates/scales around it. Inside Row/Column only the pivot of Transform.</param>
        /// <param name="path">Video file (H.264 MP4 and what FFmpeg decodes). Empty = nothing; errors are logged, nothing is shown.</param>
        /// <param name="play">Plays while true, pauses at the current frame while false.</param>
        /// <param name="loop">Restarts at the end; otherwise playback stops at the end.</param>
        /// <param name="keepLastFrame">When playback ends (Loop off), the last frame stays visible; otherwise the element is cleared.</param>
        /// <param name="rate">Playback speed (1 = normal). Values below 0.01 are clamped (no reverse playback).</param>
        /// <param name="volume">Audio volume 0..1 (linear, clamped).</param>
        /// <param name="fillMode">How the video fills Size: Stretch, Fit (keep aspect), Crop (keep aspect, fill). Tile is not supported by a video: it is drawn as Stretch and the node shows a warning.</param>
        /// <param name="opacity">Opacity of the element. 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas).</param>
        /// <param name="clip">Clip children to the element's size.</param>
        /// <param name="debug">Debug overlay for this element only (not its children; the Renderer's Debug shows all). Does not affect layout or hit-testing. Cyan frame = actual size (solid: Size, dashed: Auto Size on), orange = implicit size, i.e. what Auto Size would give (only where it differs), blue dotted = children extent, translucent red = children outside the frame (overflow), translucent green = padding and hatched violet = Row/Column spacing (both inside the frame), magenta cross = origin, yellow diamond = anchor. Text: green line = baseline, dashed green = ascent, dashed red = descent.</param>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …). Without interaction the element is transparent for input.</param>
        /// <param name="effect">Chain of effect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer.</param>
        /// <param name="enabled">False takes the element out of the scene: not drawn, no input and no space in Row/Column (the siblings move up), while it keeps its state. Opacity 0 keeps the gap instead.</param>
        /// <param name="position">Playback position in seconds (0 until loaded; last frame).</param>
        /// <param name="duration">Duration of the media in seconds (0 until loaded or without a file; last frame).</param>
        /// <param name="measuredSize">Natural size of the video, in units (0, 0 until the first frame; last frame).</param>
        /// <param name="sceneBounds">Bounds in window coordinates after layout and transform, in units (last frame).</param>
        // Pin defaults: see RectangleNode (text form, typeof(T) defaults are ignored by vvvvc).
        [return: Pin(Name = "Output")]
        public IElement Update(
            [DefaultValue("1.6, 0.9")] Vector2 size,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            Path path,
            [DefaultValue(true)] bool play,
            [DefaultValue(true)] bool loop,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool keepLastFrame,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float rate,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float volume,
            [DefaultValue(typeof(FillMode), nameof(FillMode.Fit))] FillMode fillMode,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            out float position,
            out float duration,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
        {
            var element = _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect, enabled),
                new Props(path?.Value ?? string.Empty, play, loop, keepLastFrame, rate, volume, ResolveFillMode(fillMode, _node.Messages)),
                out measuredSize, out sceneBounds);
            position = _node.Feedback.MediaPosition();
            duration = _node.Feedback.MediaDuration();
            return element;
        }

        public void Dispose() => _node.Dispose();
    }
}
