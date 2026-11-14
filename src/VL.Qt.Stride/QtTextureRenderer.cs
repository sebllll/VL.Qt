using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Stride.Games;
using Stride.Graphics;
using VL.Core;
using VL.Lib.Animation;
using VL.Stride.Games;
using IServiceRegistry = global::Stride.Core.IServiceRegistry;

namespace VL.Qt.Stride
{
    /// <summary>
    /// Renders the QtTexture nodes of one app host (service of the <see cref="AppHost"/>, disposed with it): the nodes
    /// schedule themselves in Update; at the start of Stride's draw (a game system with the lowest DrawOrder, after all
    /// updates, before any other system draws) this applies their commands, renders all of them in one native call
    /// (<see cref="OffscreenRenderer.RenderBatch"/>, ordered against Stride's immediate context by fences) and polls
    /// their events. Like SkiaTexture.DefaultRenderer, which hooks the internal VLGame.DrawStarted, raised right before
    /// the game systems draw. Also tells the nodes when Stride's device is reset or disposed. Main thread only.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class QtTextureRenderer : IDisposable
    {
        readonly VLGame _game;
        readonly IFrameClock _clock;
        readonly QtApp _app;
        readonly ILogger _logger;
        readonly DrawStartSystem _system;
        readonly IGraphicsDeviceService? _deviceService;
        readonly List<QtTextureNode> _scheduled = new();
        // Renderers of one batch: reused, so a frame allocates nothing.
        OffscreenRenderer[] _batch = new OffscreenRenderer[8];
        bool _disposed;

        QtTextureRenderer(AppHost appHost)
        {
            _game = appHost.Services.GetService(typeof(VLGame)) as VLGame
                ?? throw new InvalidOperationException("VL.Qt.Stride: the app host has no VLGame (VL.Stride not loaded)");
            _clock = appHost.Services.GetService(typeof(IFrameClock)) as IFrameClock
                ?? throw new InvalidOperationException("VL.Qt.Stride: the app host has no IFrameClock");
            _app = QtApp.Get(appHost.LoggerFactory);
            // The window pump also serves the scenes: it processes Qt events, pumps image resources and advances the
            // animation driver once per frame, also when the patch has no Renderer [Qt].
            QtPump.ForApp(appHost);
            _logger = appHost.LoggerFactory.CreateLogger("VL.Qt.QtTexture");
            // Stride's draw runs the game systems ordered by DrawOrder, VLGame.DrawStarted fires right before that. The lowest
            // DrawOrder is the same moment, through the public API (no reflection on the internal event).
            _system = new DrawStartSystem(_game.Services, OnDrawStarted);
            _game.GameSystems.Add(_system);
            // A reset recreates the device in place (same GraphicsDevice instance, new native device, textures destroyed).
            _deviceService = _game.Services.GetService<IGraphicsDeviceService>();
            if (_deviceService is not null)
            {
                _deviceService.DeviceResetting += OnDeviceGone;
                _deviceService.DeviceDisposing += OnDeviceGone;
            }
        }

        /// <summary>
        /// Raised before Stride resets or disposes its device: the native device and the textures of the nodes are about to
        /// go, so the nodes release their texture and bind a new one with the next Update.
        /// </summary>
        public event Action? StrideDeviceGone;

        /// <summary>The renderer of the app host of <paramref name="appHost"/>, created on first use.</summary>
        public static QtTextureRenderer ForApp(AppHost appHost)
            => appHost.Services.GetOrAddService(_ => new QtTextureRenderer(appHost), allowToAskParent: false);

        /// <summary>Stride's graphics device, null before the game has one (or after it was disposed).</summary>
        public GraphicsDevice? GraphicsDevice => _game.GraphicsDevice;

        /// <summary>Renders <paramref name="node"/> at the start of the next draw (once, however often it is called).</summary>
        public void Schedule(QtTextureNode node)
        {
            if (!_disposed && !_scheduled.Contains(node))
                _scheduled.Add(node);
        }

        /// <summary>Removes <paramref name="node"/> from the next batch (node disposed).</summary>
        public void Unschedule(QtTextureNode node) => _scheduled.Remove(node);

        void OnDeviceGone(object? sender, EventArgs e) => StrideDeviceGone?.Invoke();

        void OnDrawStarted()
        {
            // Task 3.4 (frame order): compare with the pump's "frame finished" debug line of VL.Qt.
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("QtTexture: draw start at {Timestamp}, clock {Seconds:F4} s, {Count} scheduled",
                    Stopwatch.GetTimestamp(), _clock.Time.Seconds, _scheduled.Count);
            if (_disposed || _scheduled.Count == 0)
                return;

            var count = _scheduled.Count;
            try
            {
                if (_batch.Length < count)
                    _batch = new OffscreenRenderer[Math.Max(count, _batch.Length * 2)];
                for (int i = 0; i < count; i++)
                    _batch[i] = _scheduled[i].ApplyCommands();

                var device = _game.GraphicsDevice;
                var context = device is null ? IntPtr.Zero : StrideInterop.GetNativeImmediateContext(device);
                var rc = context == IntPtr.Zero
                    ? Protocol.Offscreen.BadArgument
                    : OffscreenRenderer.RenderBatch(context, new ReadOnlySpan<OffscreenRenderer>(_batch, 0, count), DriverSeconds());
                var batchError = rc < 0 ? OffscreenRenderer.Describe(rc) : "";
                for (int i = 0; i < count; i++)
                    _scheduled[i].AfterRender(batchError);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QtTexture: rendering failed");
            }
            finally
            {
                Array.Clear(_batch, 0, count);
                _scheduled.Clear();
            }
        }

        // The process-wide animation driver must never run backwards (QtApp.PumpFrame, #314): the frame time of this
        // app, or the time the pump already set if that is later (pump offset after an app restart). With the pump at
        // GetFrameFinished after the draw start this is the current frame's time; the order is task 3.4 of #116.
        double DriverSeconds()
        {
            var seconds = _clock.Time.Seconds;
            var driver = _app.DriverSeconds;
            return double.IsNaN(driver) || seconds > driver ? seconds : driver;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_deviceService is not null)
            {
                _deviceService.DeviceResetting -= OnDeviceGone;
                _deviceService.DeviceDisposing -= OnDeviceGone;
            }
            // The game disposes its systems when it is destroyed, which may come first.
            if (!_system.IsDisposed)
            {
                _game.GameSystems.Remove(_system);
                _system.Dispose();
            }
            _scheduled.Clear();
        }

        // Draws first (lowest DrawOrder) and calls back; the rendering itself is in QtTextureRenderer.
        sealed class DrawStartSystem : GameSystemBase
        {
            readonly Action _onDraw;

            public DrawStartSystem(IServiceRegistry services, Action onDraw)
                : base(services)
            {
                _onDraw = onDraw;
                DrawOrder = int.MinValue;
                Visible = true;
            }

            public override void Draw(GameTime gameTime) => _onDraw();
        }
    }
}
