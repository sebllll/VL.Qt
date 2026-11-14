using System;
using System.ComponentModel;
using System.Runtime.Versioning;
using Stride.Core.Mathematics;
using Stride.Graphics;
using VL.Core;
using VL.Core.Import;
using VL.Lang;
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
        Texture? _texture;
        GraphicsDevice? _device;
        Int2 _size;
        PixelFormat _format;
        string _textureError = "", _batchError = "";
        bool _created, _scheduled, _debug, _disposed;
        IDisposable? _spaceWarning;

        /// <param name="nodeContext">Injected by vvvv.</param>
        public QtTextureNode([Pin(Visibility = PinVisibility.Hidden)] NodeContext nodeContext)
        {
            _nodeContext = nodeContext;
            _errors = new NodeErrors(nodeContext);
            _renderer = QtTextureRenderer.ForApp(nodeContext.AppHost);
            _scene = new OffscreenRenderer(SceneRegistry.ForApp(nodeContext.AppHost), 1920, 1080);
        }

        /// <summary>Renders the element tree into the texture at Output.</summary>
        /// <param name="output">The texture Qt renders into (premultiplied alpha). The same instance until Size or Format change; do not dispose it. Null while the texture cannot be created or bound (see Error).</param>
        /// <param name="error">Last error (texture, binding, apply, poll or render in QtNative), empty if ok. Also logged once per distinct error.</param>
        /// <param name="perf">Timings of the scene. Not measured yet for textures: always empty.</param>
        /// <param name="input">Root element (null = empty scene).</param>
        /// <param name="size">Size of the texture in pixels (1 DIP = 1 pixel).</param>
        /// <param name="space">Coordinate space of the elements. v1 implements DIPTopLeft only (1 unit = 100 DIP); other values warn and behave like DIPTopLeft.</param>
        /// <param name="clearColor">Background of the texture (straight alpha, premultiplied here). Alpha 0 gives a transparent texture.</param>
        /// <param name="format">Pixel format of the texture: R8G8B8A8 or B8G8R8A8 (UNorm or SRgb) or R16G16B16A16_Float; other formats give an error. With SRgb Stride samples linear values of what Qt renders in gamma space, like in a Qt window.</param>
        /// <param name="enabled">False freezes the scene (no updates of Input, Space, Clear Color); the texture keeps the last content.</param>
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
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(false)] bool debug)
        {
            // The previous frame had no DrawStarted (game not drawing): send its commands now, so they do not pile up.
            if (_scheduled)
                ApplyCommands();

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

            if (_texture is not null)
            {
                _renderer.Schedule(this);
                _scheduled = true;
            }

            var bound = _textureError.Length == 0 && _texture is not null && _scene.IsBound;
            output = bound ? _texture : null;
            var text = !_renderer.IsHooked ? "This vvvv version has no VLGame.DrawStarted: QtTexture cannot render"
                : _textureError.Length > 0 ? _textureError
                : _scene.Error.Length > 0 ? _scene.Error
                : _batchError;
            _errors.LogOnly(text);
            error = text;
            // TODO: Perf of offscreen scenes: QtNative has no perf ring for them (vlqt_window_get_perf needs a window).
            perf = PerfStats.Empty;
        }

        // Creates the shared texture and binds the scene to it when size, format or Stride's device change.
        // TODO: 3.3 lifetime: clamp the size to the device limit, rebind after "unbound" (QtNative device loss), Stride
        // device change and loss, Enabled false without recreating.
        void EnsureTexture(Int2 size, PixelFormat format)
        {
            size = new Int2(Math.Max(1, size.X), Math.Max(1, size.Y));
            var device = _renderer.GraphicsDevice;
            if (_created && size == _size && format == _format && device == _device)
                return;

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
            if (!StrideInterop.TryGetFormats(format, out var textureFormat, out var viewFormat, out var rtvFormat))
            {
                _textureError = $"{StrideInterop.FormatError} (got {format})";
                return;
            }
            var nativeDevice = StrideInterop.GetNativeDevice(device);
            if (nativeDevice == IntPtr.Zero)
            {
                _textureError = "QtTexture needs Stride on Direct3D 11";
                return;
            }
            try
            {
                _texture = StrideInterop.CreateSharedTexture(device, size, textureFormat, viewFormat);
                var handle = StrideInterop.CreateNtHandle(_texture);
                try
                {
                    // A failed bind keeps the texture: Output is null and Error shows the text of the scene.
                    _scene.Bind(nativeDevice, handle, size, rtvFormat);
                }
                finally
                {
                    // QtNative holds its own reference to the opened texture.
                    StrideInterop.CloseHandle(handle);
                }
            }
            catch (Exception e)
            {
                _textureError = $"Could not create the shared texture: {e.Message}";
                ReleaseTexture();
            }
        }

        // Order: Qt side (render target, opened texture, fences) → Stride texture. D3D11 keeps what the GPU still uses alive.
        void ReleaseTexture()
        {
            _scene.Unbind();
            _texture?.Dispose();
            _texture = null;
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

        // Called by QtTextureRenderer in DrawStarted, before the batch: sends this frame's commands to QtNative.
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
            _renderer.Unschedule(this);
            // Dispose unbinds first, then frees the scene; the Stride texture last.
            _scene.Dispose();
            _texture?.Dispose();
            _texture = null;
            _spaceWarning?.Dispose();
            _errors.Dispose();
        }
    }
}
