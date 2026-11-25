using System;
using System.Runtime.Versioning;
using Stride.Core.Mathematics;
using Stride.Graphics;
using VL.Core;
using VL.Lang;
using VL.Model;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;

namespace VL.Qt.Stride
{
    /// <summary>
    /// What QtTexture and the Stride layer share: an offscreen Qt scene that renders into a Stride texture shared with
    /// QtNative: texture creation, bind and rebind, device loss, input queue, error text and scheduling in
    /// <see cref="QtTextureRenderer"/>. The caller passes size and format with every <see cref="Update"/>. Main thread only.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class SharedTextureScene : IDisposable
    {
        private readonly NodeErrors _errors;
        private readonly NodeMessage _sceneError;
        private readonly QtTextureRenderer _renderer;
        private readonly OffscreenScene _scene;
        private readonly NotificationQueue _input;
        private readonly RenderThreadRequest _renderThreads;
        private Texture? _texture;
        private GraphicsDevice? _device;
        private Int2 _size;
        private PixelFormat _format;
        private MultisampleCount _samples;
        private string _textureError = "", _batchError = "";
        // _created: the texture state matches the inputs seen last (also when it is an error); _hasContent: the scene was
        // bound to the current texture once, so it holds content; _scheduled: the scene is in the batch of QtTextureRenderer
        // until the draw takes it out.
        private bool _created, _hasContent, _scheduled, _enabled, _disposed;
        private const string SamplesPhase = "Multisample Count";
        private const string BuildPhase = "Build";
        private const string ReconcilePhase = "Reconcile";
        private const string SceneErrorPhase = "Scene";
        private readonly ElementInput _element;

        /// <param name="nodeContext">Context of the node that owns the scene.</param>
        /// <param name="renderThreads">The node's value of Render Threads when the scene is created: QtNative gives the scene
        /// its executor (a render thread, or the main thread) at creation and the scene keeps it.</param>
        public SharedTextureScene(NodeContext nodeContext, int renderThreads)
        {
            _errors = new NodeErrors(nodeContext);
            _sceneError = new NodeMessage(_errors, SceneErrorPhase, MessageSeverity.Error);
            _element = new ElementInput(_errors);
            // Starts the Qt app, which the render-thread setting and the scene need.
            _renderer = QtTextureRenderer.ForApp(nodeContext.AppHost);
            _renderThreads = new RenderThreadRequest(renderThreads);
            _scene = new OffscreenScene(OccurrenceRegistry.ForApp(nodeContext.AppHost), InteractionRegistry.ForApp(nodeContext.AppHost), 1920, 1080);
            _input = new NotificationQueue(_scene.Input.Notify);
            _renderer.StrideDeviceGone += OnStrideDeviceGone;
        }

        /// <summary>
        /// Notifications for the scene (positions in texture pixels): subscribe to a source or feed it with OnNext. They are
        /// handed to the scene in <see cref="Update"/> (dropped while it is disabled).
        /// </summary>
        public NotificationQueue Input => _input;

        /// <summary>The messages at the node of this scene: the node adds its own phases.</summary>
        public NodeErrors Messages => _errors;

        /// <summary>
        /// The texture Qt renders into, null while it cannot be used: not created or bound (see <see cref="Error"/>), or, when
        /// enabled, the scene is not bound. Disabled it keeps the last content, also after QtNative unbound the scene.
        /// </summary>
        public Texture? Texture => _texture is not null && _textureError.Length == 0 && (_enabled ? _scene.IsBound : _hasContent) ? _texture : null;

        /// <summary>
        /// Sets the node's value of Render Threads (the largest value of all nodes sizes QtNative's pool). It applies to the
        /// scenes created afterwards; this scene keeps its executor.
        /// </summary>
        public void SetRenderThreads(int renderThreads) => _renderThreads.Set(renderThreads);

        /// <summary>True if the scene is bound to the texture, so it renders into it.</summary>
        public bool IsBound => _scene.IsBound;

        /// <summary>Last error (texture, binding, apply, poll or render in QtNative), empty if ok.</summary>
        public string Error => _textureError.Length > 0 ? _textureError
            : _scene.Error.Length > 0 ? _scene.Error
            : _batchError;

        /// <summary>
        /// Updates the scene and schedules its render for the next draw; enabled = false freezes it (nothing is created, recreated,
        /// rendered or fed with input; the texture keeps its content). Shows <see cref="Error"/> at the node and logs it once per distinct text.
        /// A new <paramref name="samples"/> rebinds the scene to the same texture (QtNative renders with the new MSAA count and
        /// resolves into the texture); a count the device does not support gives a warning at the node.
        /// </summary>
        public void Update(IElement? input, Int2 size, PixelFormat format, Space space, Color4 clearColor, bool clear, bool enabled, bool debug, MultisampleCount samples)
        {
            _enabled = enabled;
            // The previous frame had no draw (game not drawing): send its commands now, so they do not pile up. The scene
            // stays in the batch of QtTextureRenderer (_scheduled stays true) until the draw takes it out.
            if (_scheduled)
                Apply();

            // Input acts on the items of the last apply, before this frame's update and before the render in DrawStarted;
            // a disabled (frozen) scene takes no input.
            if (enabled)
                _input.Drain();
            else
                _input.Clear();

            // Disabled: nothing is created, recreated or rebound; the texture keeps its content.
            if (enabled)
                EnsureTexture(size, format, samples);

            _scene.Debug = debug;
            // Qt does not premultiply the clear colour itself (render-texture.md, task 1.8).
            var premultiplied = new Color4(clearColor.R * clearColor.A, clearColor.G * clearColor.A, clearColor.B * clearColor.A, clearColor.A);
            _scene.Update(_element.Update(input), space, premultiplied, clear, enabled);
            // A stylable linked to Input failed to build (exception, cycle, foreign result): warning until a Build succeeds.
            if (_scene.Reconciler.RootBuildError is { } buildError)
                _errors.Warn(buildError, BuildPhase);
            else
                _errors.Clear(BuildPhase);
            // The scene could not be written (e.g. a value too large for a command): logged, shown until a reconcile works.
            _errors.Report(_scene.ReconcileFault, ReconcilePhase);

            if (enabled && _texture is not null && !_scheduled)
            {
                _renderer.Schedule(this);
                _scheduled = true;
            }

            _sceneError.Set(Error);
        }

        // Creates the shared texture and binds the scene when size, format or Stride's device change (a Stride reset releases
        // the texture in OnStrideDeviceGone and clears _created); rebinds it after QtNative unbound the scene.
        private void EnsureTexture(Int2 size, PixelFormat format, MultisampleCount samples)
        {
            var device = _renderer.GraphicsDevice;
            var max = device is null ? int.MaxValue : StrideInterop.MaxTextureSize(device);
            size = new Int2(Math.Clamp(size.X, 1, max), Math.Clamp(size.Y, 1, max));
            if (_created && size == _size && format == _format && device == _device)
            {
                if (_texture is not null)
                {
                    if (samples != _samples)
                    {
                        // The texture stays, the scene renders into it with the new count (unbind, bind).
                        _samples = samples;
                        _scene.Unbind();
                        TryBind();
                    }
                    else
                        RebindIfUnbound();
                }
                else
                    _samples = samples;
                return;
            }

            ReleaseTexture();
            _created = true;
            _size = size;
            _format = format;
            _samples = samples;
            _device = device;
            _textureError = "";

            if (device is null)
            {
                _textureError = "Stride has no graphics device yet";
                return;
            }
            if (!TextureFormats.TryGetTextureFormat(format, out var textureFormat))
            {
                _textureError = $"{TextureFormats.FormatError} (got {format})";
                return;
            }
            if (StrideInterop.GetNativeDevice(device) == IntPtr.Zero)
            {
                _textureError = "QtTexture needs Stride on Direct3D 11";
                return;
            }
            try
            {
                _texture = StrideInterop.CreateSharedTexture(device, size, textureFormat, format);
            }
            catch (Exception e)
            {
                _textureError = $"Could not create the shared texture: {e.Message}";
                ReleaseTexture();
                return;
            }
            // A failed bind keeps the texture: Texture is null and Error shows the text of the scene.
            TryBind();
        }

        // QtNative unbinds a scene when its device is lost (the Stride texture itself is fine): bind again with a new handle.
        // OffscreenScene.ShouldRetryBind decides when (right away after a loss, now and then after a structural failure).
        private void RebindIfUnbound()
        {
            if (_scene.IsBound)
                return;
            if (_scene.ShouldRetryBind())
                TryBind();
        }

        private void TryBind()
        {
            try
            {
                var handle = StrideInterop.CreateNtHandle(_texture!);
                try
                {
                    _scene.Bind(StrideInterop.GetNativeDevice(_device!), handle, _size, (int)_format, _samples);
                }
                finally
                {
                    // QtNative holds its own reference to the opened texture.
                    StrideInterop.CloseHandle(handle);
                }
                _hasContent |= _scene.LastBindSucceeded;
                ReportSamples();
            }
            catch (Exception e)
            {
                _textureError = $"Could not bind the shared texture: {e.Message}";
                ReleaseTexture();
            }
        }

        // Stride resets or disposes its device: the native device and the texture go. Qt side first (render target, opened
        // texture and fences hold the old device), then the texture; the next Update creates a new one.
        private void OnStrideDeviceGone()
        {
            ReleaseTexture();
            _created = false;
            _device = null;
        }

        // Order: Qt side (render target, opened texture, fences) -> Stride texture. D3D11 keeps what the GPU still uses alive.
        private void ReleaseTexture()
        {
            _scene.Unbind();
            _texture?.Dispose();
            _texture = null;
            _hasContent = false;
            _batchError = "";
        }

        // The count QtNative used is lower than requested (device limit): warning at the node until the next bind fits.
        private void ReportSamples()
        {
            var warning = _scene.LastBindSucceeded ? Multisample.ClampWarning(_samples, _scene.UsedSamples) : null;
            if (warning is not null)
                _errors.Warn(warning, SamplesPhase);
            else
                _errors.Clear(SamplesPhase);
        }

        // Called by QtTextureRenderer at the start of the draw, before the batch: sends this frame's commands to the native scene.
        // The batch takes the scene out of its list, so the next Update schedules it again.
        internal SceneHandle ApplyCommands()
        {
            _scheduled = false;
            Apply();
            return _scene.Handle;
        }

        private void Apply()
        {
            try
            {
                _scene.ApplyCommands();
                _errors.Clear(nameof(ApplyCommands));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _errors.Report(e, nameof(ApplyCommands));
            }
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
            _sceneError.Dispose();
            _errors.Dispose();
            _element.Dispose();
            _renderThreads.Dispose();
        }
    }
}
