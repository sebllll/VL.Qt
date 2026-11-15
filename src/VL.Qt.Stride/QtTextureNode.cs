using System;
using System.ComponentModel;
using System.Runtime.Versioning;
using Stride.Core.Mathematics;
using Stride.Graphics;
using VL.Core;
using VL.Core.Import;
using VL.Lang;
using VL.Lib.IO.Notifications;
using VL.Model;
using VL.Qt.Elements;

[assembly: ImportAsIs]

namespace VL.Qt.Stride
{
    /// <summary>
    /// Renders the element tree at Input offscreen into a Stride texture (no window), on the GPU and in the same frame:
    /// Stride waits GPU-side until Qt has written the texture. The output is premultiplied alpha; 1 DIP = 1 pixel.
    /// </summary>
    [ProcessNode(Name = "QtTexture", Category = Categories.Qt)]
    [Smell(SymbolSmell.Experimental)]
    [SupportedOSPlatform("windows")]
    public sealed class QtTextureNode : IDisposable
    {
        readonly NodeContext _nodeContext;
        readonly NodeErrors _errors;
        readonly QtTextureRenderer _renderer;
        readonly OffscreenRenderer _scene;
        readonly NotificationQueue _input = new();
        Texture? _texture;
        GraphicsDevice? _device;
        Int2 _size;
        PixelFormat _format;
        string _textureError = "", _batchError = "";
        // _created: the texture state matches the inputs seen last (also when it is an error); _hasContent: the scene was
        // bound to the current texture once, so it holds content; _lastBindCode: result of the last Bind, 0 = ok.
        bool _created, _hasContent, _scheduled, _debug, _disposed;
        int _lastBindCode, _rebindWait;
        IDisposable? _spaceWarning;

        /// <param name="nodeContext">Injected by vvvv.</param>
        public QtTextureNode([Pin(Visibility = PinVisibility.Hidden)] NodeContext nodeContext)
        {
            _nodeContext = nodeContext;
            _errors = new NodeErrors(nodeContext);
            _renderer = QtTextureRenderer.ForApp(nodeContext.AppHost);
            _scene = new OffscreenRenderer(SceneRegistry.ForApp(nodeContext.AppHost), 1920, 1080);
            _renderer.StrideDeviceGone += OnStrideDeviceGone;
        }

        /// <summary>Renders the element tree into the texture at Output.</summary>
        /// <param name="output">The texture Qt renders into (premultiplied alpha). The same instance until Size or Format change; do not dispose it. Null while the texture cannot be created or bound (see Error).</param>
        /// <param name="error">Last error (texture, binding, apply, poll or render in QtNative), empty if ok. Also logged once per distinct error. An invalid Format or a texture that cannot be created gives the error and a null Output.</param>
        /// <param name="perf">Timings of the scene. Not measured yet for textures: always empty.</param>
        /// <param name="input">Root element (null = empty scene).</param>
        /// <param name="size">Size of the texture in pixels (1 DIP = 1 pixel), clamped to 1x1 ... the largest Texture2D of Stride's device (16384 on feature level 11).</param>
        /// <param name="space">Coordinate space of the elements. v1 implements DIPTopLeft only (1 unit = 100 DIP); other values warn and behave like DIPTopLeft.</param>
        /// <param name="clearColor">Background of the texture (straight alpha, premultiplied here). Alpha 0 gives a transparent texture.</param>
        /// <param name="format">Pixel format of the texture: R8G8B8A8 or B8G8R8A8 (UNorm or SRgb) or R16G16B16A16_Float; other formats give an error. With SRgb Stride samples linear values of what Qt renders in gamma space, like in a Qt window.</param>
        /// <param name="enabled">False freezes the node: no updates of Input, Space, Clear Color, Size or Format, nothing is created or recreated and nothing renders; Output keeps the last content (null if there is none yet).</param>
        /// <param name="inputNotifications">Mouse, touch and keyboard input for the scene (vvvv notifications) with positions in texture pixels, top-left origin. Map a window's input with ToTexturePixels. A press inside the texture gives the scene keyboard focus, a press outside takes it.</param>
        /// <param name="debug">Debug overlay for all elements of the scene (same as the Debug pin of Renderer [Qt]).</param>
        public void Update(
            out Texture? output,
            out string error,
            [Pin(Visibility = PinVisibility.Optional)] out PerfStats perf,
            Element? input,
            [DefaultValue(typeof(Int2), "1920, 1080")] Int2 size,
            [DefaultValue(typeof(Space), nameof(Space.DIPTopLeft))] Space space,
            [DefaultValue("0, 0, 0, 0")] Color4 clearColor,
            [DefaultValue(typeof(PixelFormat), nameof(PixelFormat.B8G8R8A8_UNorm_SRgb))] PixelFormat format,
            [DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional)] IObservable<INotification>? inputNotifications,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(false)] bool debug)
        {
            // The previous frame had no draw (game not drawing): send its commands now, so they do not pile up.
            if (_scheduled)
                ApplyCommands();

            // Input acts on the items of the last apply, before this frame's update and before the render in DrawStarted;
            // a disabled (frozen) scene takes no input.
            _input.Subscribe(inputNotifications);
            if (enabled)
                _input.Drain(_scene);
            else
                _input.Clear();

            // Disabled: nothing is created, recreated or rebound; the texture keeps its content.
            if (enabled)
                EnsureTexture(size, format);

            if (debug != _debug && _scene.Scene != IntPtr.Zero)
            {
                _debug = debug;
                Native.vlqt_window_set_debug(_scene.Scene, debug ? 1 : 0);
            }
            if (enabled)
                UpdateSpaceWarning(space);
            // Qt does not premultiply the clear colour itself (render-texture.md, task 1.8).
            var premultiplied = new Color4(clearColor.R * clearColor.A, clearColor.G * clearColor.A, clearColor.B * clearColor.A, clearColor.A);
            _scene.Update(input, Geometry.Resolve(space), premultiplied, enabled);

            if (enabled && _texture is not null)
            {
                _renderer.Schedule(this);
                _scheduled = true;
            }

            // Enabled: only a bound scene fills the texture. Disabled: the content of before stays valid, also after QtNative
            // unbound the scene (device loss).
            output = _texture is not null && _textureError.Length == 0 && (enabled ? _scene.IsBound : _hasContent) ? _texture : null;
            var text = _textureError.Length > 0 ? _textureError
                : _scene.Error.Length > 0 ? _scene.Error
                : _batchError;
            _errors.LogOnly(text);
            error = text;
            // TODO: Perf of offscreen scenes: QtNative has no perf ring for them (vlqt_window_get_perf needs a window).
            perf = PerfStats.Empty;
        }

        // Creates the shared texture and binds the scene when size, format or Stride's device change (a Stride reset releases
        // the texture in OnStrideDeviceGone and clears _created); rebinds it after QtNative unbound the scene.
        void EnsureTexture(Int2 size, PixelFormat format)
        {
            var device = _renderer.GraphicsDevice;
            var max = device is null ? int.MaxValue : StrideInterop.MaxTextureSize(device);
            size = new Int2(Math.Clamp(size.X, 1, max), Math.Clamp(size.Y, 1, max));
            if (_created && size == _size && format == _format && device == _device)
            {
                if (_texture is not null)
                    RebindIfUnbound();
                return;
            }

            ReleaseTexture();
            _created = true;
            _size = size;
            _format = format;
            _device = device;
            _textureError = "";

            if (device is null)
            {
                _textureError = "Stride has no graphics device yet";
                return;
            }
            if (!StrideInterop.TryGetFormats(format, out var textureFormat, out var viewFormat, out _))
            {
                _textureError = $"{StrideInterop.FormatError} (got {format})";
                return;
            }
            if (StrideInterop.GetNativeDevice(device) == IntPtr.Zero)
            {
                _textureError = "QtTexture needs Stride on Direct3D 11";
                return;
            }
            try
            {
                _texture = StrideInterop.CreateSharedTexture(device, size, textureFormat, viewFormat);
            }
            catch (Exception e)
            {
                _textureError = $"Could not create the shared texture: {e.Message}";
                ReleaseTexture();
                return;
            }
            // A failed bind keeps the texture: Output is null and Error shows the text of the scene.
            TryBind();
        }

        // QtNative unbinds a scene when its device is lost (the Stride texture itself is fine): bind again with a new handle.
        // Right away after a loss (the last bind was fine) and while QtNative rebuilds its device (DeviceLost); other failures
        // (adapter, open, fences) are structural and tried again only now and then, so a failing bind costs nothing per frame.
        void RebindIfUnbound()
        {
            if (_scene.IsBound)
                return;
            if (_lastBindCode is Protocol.Offscreen.Ok or Protocol.Offscreen.DeviceLost || ++_rebindWait >= 60)
                TryBind();
        }

        void TryBind()
        {
            _rebindWait = 0;
            StrideInterop.TryGetFormats(_format, out _, out _, out var rtvFormat);
            try
            {
                var handle = StrideInterop.CreateNtHandle(_texture!);
                try
                {
                    _lastBindCode = _scene.Bind(StrideInterop.GetNativeDevice(_device!), handle, _size, rtvFormat);
                }
                finally
                {
                    // QtNative holds its own reference to the opened texture.
                    StrideInterop.CloseHandle(handle);
                }
                _hasContent |= _lastBindCode == Protocol.Offscreen.Ok;
            }
            catch (Exception e)
            {
                _textureError = $"Could not bind the shared texture: {e.Message}";
                ReleaseTexture();
            }
        }

        // Stride resets or disposes its device: the native device and the texture go. Qt side first (render target, opened
        // texture and fences hold the old device), then the texture; the next Update creates a new one.
        void OnStrideDeviceGone()
        {
            ReleaseTexture();
            _created = false;
            _device = null;
        }

        // Order: Qt side (render target, opened texture, fences) -> Stride texture. D3D11 keeps what the GPU still uses alive.
        void ReleaseTexture()
        {
            _scene.Unbind();
            _texture?.Dispose();
            _texture = null;
            _hasContent = false;
            _lastBindCode = 0;
            _batchError = "";
        }

        void UpdateSpaceWarning(Space space)
        {
            bool unsupported = !Geometry.IsSupported(space);
            if (unsupported && _spaceWarning is null)
                _spaceWarning = _nodeContext.AddPersistentMessage(MessageSeverity.Warning, Renderer.SpaceWarning);
            else if (!unsupported && _spaceWarning is not null)
            {
                _spaceWarning.Dispose();
                _spaceWarning = null;
            }
        }

        // Called by QtTextureRenderer at the start of the draw, before the batch: sends this frame's commands to QtNative.
        internal OffscreenRenderer ApplyCommands()
        {
            _scheduled = false;
            try
            {
                _scene.ApplyCommands();
                _errors.Clear(nameof(ApplyCommands));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _errors.Report(e, nameof(ApplyCommands));
            }
            return _scene;
        }

        // Called by QtTextureRenderer after the batch: its error (empty if ok) and the events of the scene.
        internal void AfterRender(string batchError)
        {
            _batchError = batchError;
            try
            {
                _scene.PollEvents();
                _errors.Clear(nameof(AfterRender));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _errors.Report(e, nameof(AfterRender));
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _renderer.StrideDeviceGone -= OnStrideDeviceGone;
            _renderer.Unschedule(this);
            _input.Dispose();
            // Dispose unbinds first, then frees the scene; the Stride texture last.
            _scene.Dispose();
            _texture?.Dispose();
            _texture = null;
            _spaceWarning?.Dispose();
            _errors.Dispose();
        }
    }
}
