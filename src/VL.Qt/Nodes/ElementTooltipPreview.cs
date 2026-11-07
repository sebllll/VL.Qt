using System;
using System.Diagnostics;
using System.Globalization;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Basics.Imaging;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Editor tooltip preview of an element (used by the link tooltip in the vvvv editor, #543): a small live image of
    /// the element as a renderer shows it, plus a text. Grabs only while it is called, at most ~5 times per second and
    /// only when the scene changed or the element is a new instance. The element has to be shown in a renderer.
    /// The identity of the preview is the place of the element (renderer window and element id), not the instance: an
    /// animated element is a new instance every frame at the same place and keeps its last image until the next one is done.
    /// Elements that contain a Video give only text: grabbing a running hardware-decoded video costs the frame 25–57 ms.
    /// </summary>
    [ProcessNode(Name = "TooltipPreview", Category = Categories.Qt), Smell(SymbolSmell.Advanced)]
    public sealed class ElementTooltipPreview : IDisposable
    {
        /// <summary>Longest edge of the preview in pixels.</summary>
        internal const int maxEdgePx = 256;

        /// <summary>Minimum time between two grabs in seconds (about 5 per second).</summary>
        internal const double minGrabInterval = 0.2;

        /// <summary>Text suffix for an element that no renderer shows.</summary>
        internal const string NotShownSuffix = "not shown in a renderer";

        /// <summary>Text suffix for an element tree that contains a Video (never grabbed).</summary>
        internal const string ContainsVideoSuffix = "contains video, no preview";

        readonly ResourceClient _client;
        readonly SceneRegistry _scene;
        readonly Func<IntPtr, long> _changeCount;
        readonly Func<double> _clockSeconds;
        ImageResource? _resource;
        IImage? _image;
        Element? _lastElement;      // the instance of the last grab (a new instance at the same place counts as a change)
        Element? _videoChecked;     // the instance whose tree was searched for a Video
        bool _hasVideo;
        IntPtr _imageWindow;        // the place the image (and the last grab) belongs to; zero = none
        uint _imageId;
        IntPtr _grabWindow;         // the place the running or finished grab belongs to
        uint _grabId;
        long _lastChangeCount;
        double _lastGrabTime = double.NegativeInfinity;
        bool _grabbing;
        bool _settled;
        string _error = string.Empty;

        public ElementTooltipPreview(NodeContext nodeContext)
            : this(ResourceClient.ForApp(nodeContext.AppHost), SceneRegistry.ForApp(nodeContext.AppHost),
                  static window => Native.vlqt_window_change_count(window), static () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency)
        {
        }

        /// <summary>For tests: a client on a fake API, an own scene registry, and injected change counter and clock.</summary>
        internal ElementTooltipPreview(ResourceClient client, SceneRegistry scene, Func<IntPtr, long> changeCount, Func<double> clockSeconds)
        {
            _client = client;
            _scene = scene;
            _changeCount = changeCount;
            _clockSeconds = clockSeconds;
        }

        /// <summary>Returns the last finished preview of Element and starts a new grab if the scene changed.</summary>
        /// <param name="element">The element to preview; null gives no image and an empty text.</param>
        /// <param name="text">Type and size of the element; says so if no renderer shows it, or why a grab failed.</param>
        /// <returns>The last finished preview (BGRA8 premultiplied, longest edge up to 256 px); null before the first one.</returns>
        [return: Pin(Name = "Image")]
        public IImage? Update(Element? element, out string text)
        {
            if (element is null)
            {
                ForgetPlace();
                text = string.Empty;
                return null;
            }

            var description = Describe(element);
            if (!_scene.TryLocate(element, out var window, out var elementId))
            {
                ForgetPlace();
                text = $"{description} – {NotShownSuffix}";
                return null;
            }

            if (!ReferenceEquals(element, _videoChecked))
            {
                _videoChecked = element;
                _hasVideo = ContainsVideo(element);
            }
            if (_hasVideo)
            {
                // No grab; a grab that still runs finishes unseen (its result is dropped with the place).
                ForgetPlace();
                _settled = false;
                text = $"{description} – {ContainsVideoSuffix}";
                return null;
            }

            if (window != _imageWindow || elementId != _imageId)
            {
                // Another place: the image of the old one is not shown here; the new place gets its own grab below.
                _image = null;
                _lastElement = null;
                _imageWindow = window;
                _imageId = elementId;
            }

            if (_settled)
            {
                _settled = false;
                // A result of another place is dropped.
                if (window == _grabWindow && elementId == _grabId && _resource is { State: QtImageState.Ready })
                    _image = _client.ReadSnapshot(_resource) ?? _image;
            }

            if (!_grabbing && NeedsGrab(element, window))
            {
                var now = _clockSeconds();
                if (now - _lastGrabTime >= minGrabInterval)
                    Grab(element, window, elementId, now);
            }

            var error = _error.Length > 0 ? _error : _resource is { State: QtImageState.Failed } ? _resource.Error : string.Empty;
            text = error.Length > 0 ? $"{description} – {error}" : description;
            return _image;
        }

        /// <summary>The element is gone, not shown or not grabbed: no image, and the next shown place gets a grab.</summary>
        void ForgetPlace()
        {
            _image = null;
            _lastElement = null;
            _imageWindow = IntPtr.Zero;
            _imageId = 0;
        }

        bool NeedsGrab(Element element, IntPtr window)
            => !ReferenceEquals(element, _lastElement) || _changeCount(window) != _lastChangeCount;

        void Grab(Element element, IntPtr window, uint elementId, double now)
        {
            // Remember the state before the grab: a change during the grab counts for the next one.
            _lastElement = element;
            _lastChangeCount = _changeCount(window);
            _lastGrabTime = now;
            _grabWindow = window;
            _grabId = elementId;
            try
            {
                if (_resource is null)
                {
                    _resource = _client.CreateSnapshot();
                    _resource.Settled = _ =>
                    {
                        _grabbing = false;
                        _settled = true;
                    };
                }
                _error = string.Empty;
                _grabbing = true;
                // A refused grab settles at once (Settled runs inside the call and clears the flag again).
                _client.SnapshotFit(_resource, window, elementId, maxEdgePx);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _grabbing = false;
                _error = e.Message;
            }
        }

        /// <summary>Searches the tree (children, mask sources of effects) for a Video; once per new instance; the loops allocate nothing (ImmutableArray struct enumerator).</summary>
        static bool ContainsVideo(Element element)
        {
            if (element is Video)
                return true;
            foreach (var child in element.Children)
                if (child is not null && ContainsVideo(child))
                    return true;
            for (var effect = element.Effect; effect is not null; effect = effect.Previous)
                if (effect is MaskEffect { Source: { } source } && ContainsVideo(source))
                    return true;
            return false;
        }

        static string Describe(Element element)
        {
            var size = element.Size;
            return string.Create(CultureInfo.InvariantCulture, $"{element.Type} {MathF.Round(size.X)} x {MathF.Round(size.Y)}");
        }

        public void Dispose()
        {
            _client.Release(_resource);
            _resource = null;
            ForgetPlace();
            _videoChecked = null;
            _grabWindow = IntPtr.Zero;
            _grabId = 0;
        }
    }
}
