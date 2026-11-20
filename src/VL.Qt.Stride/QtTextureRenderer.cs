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
    /// Renders the QtTexture scenes of one app host (service of the <see cref="AppHost"/>, disposed with it): the scenes
    /// schedule themselves in Update; at the start of Stride's draw (a game system with the lowest DrawOrder, after all
    /// updates, before any other system draws) this applies their commands, renders all of them in one native call
    /// (<see cref="OffscreenRenderer.RenderBatch"/>, ordered against Stride's immediate context by fences) and polls
    /// their events. Like SkiaTexture.DefaultRenderer, which hooks the internal VLGame.DrawStarted, raised right before
    /// the game systems draw. Also tells the scenes when Stride's device is reset or disposed. Main thread only.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class QtTextureRenderer : IDisposable
    {
        private readonly VLGame _game;
        private readonly IFrameClock _clock;
        private readonly QtApp _app;
        private readonly ILogger _logger;
        private readonly DrawStartSystem _system;
        private readonly IGraphicsDeviceService? _deviceService;
        private readonly List<SharedTextureScene> _scheduled = new();
        // Renderers of one batch: reused, so a frame allocates nothing.
        private OffscreenRenderer[] _batch = new OffscreenRenderer[8];
        private bool _disposed;

        private QtTextureRenderer(AppHost appHost)
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
        /// Raised before Stride resets or disposes its device: the native device and the textures of the scenes are about to
        /// go, so the scenes release their texture and bind a new one with the next Update.
        /// </summary>
        public event Action? StrideDeviceGone;

        /// <summary>The renderer of the app host of <paramref name="appHost"/>, created on first use.</summary>
        public static QtTextureRenderer ForApp(AppHost appHost)
            => appHost.Services.GetOrAddService(_ => new QtTextureRenderer(appHost), allowToAskParent: false);

        /// <summary>Stride's graphics device, null before the game has one (or after it was disposed).</summary>
        public GraphicsDevice? GraphicsDevice => _game.GraphicsDevice;

        /// <summary>Renders <paramref name="scene"/> at the start of the next draw (once, however often it is called).</summary>
        public void Schedule(SharedTextureScene scene)
        {
            if (!_disposed && !_scheduled.Contains(scene))
                _scheduled.Add(scene);
        }

        /// <summary>Removes <paramref name="scene"/> from the next batch (scene disposed).</summary>
        public void Unschedule(SharedTextureScene scene) => _scheduled.Remove(scene);

        private void OnDeviceGone(object? sender, EventArgs e) => StrideDeviceGone?.Invoke();

        private void OnDrawStarted()
        {
            // Task 3.4 (frame order): the Qt pump (GetFrameFinished) has already run in this frame if the driver time it set
            // is not behind this frame's clock. Expected: false (draw start comes first).
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("QtTexture: draw start at {Timestamp}, clock {Seconds:F4} s, pump driver {Driver:F4} s, pump already ran {PumpFirst}, {Count} scheduled",
                    Stopwatch.GetTimestamp(), _clock.Time.Seconds, _app.DriverSeconds, _app.DriverSeconds >= _clock.Time.Seconds, _scheduled.Count);
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

        // The process-wide animation driver must never run backwards (QtApp.PumpFrame): the frame time of this
        // app, or the time the pump already set if that is later (pump offset after an app restart). With the pump at
        // GetFrameFinished after the draw start this is the current frame's time; the order is task 3.4 of #116.
        private double DriverSeconds()
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
        private sealed class DrawStartSystem : GameSystemBase
        {
            private readonly Action _onDraw;

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
