using System;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Input;
using Stride.Rendering;
using VL.Core;
using VL.Core.Import;
using VL.Lib.IO.Notifications;
using VL.Model;
using VL.Qt.Elements;
using VL.Stride;
using VL.Stride.Input;

namespace VL.Qt.Stride
{
    /// <summary>
    /// Draws the element tree at Input as a layer into the Stride render target it is drawn into (the window, a render
    /// texture), sized to the viewport, like SkiaRenderer. Into a window or a typeless 8-bit render texture it blends like a
    /// Qt window (gamma space, premultiplied); into other targets it blends like QtTexture (linear light). Takes the window's
    /// mouse, touch and keyboard input. Belongs after the 3D scene, not inside it.
    /// </summary>
    [ProcessNode(Name = "QtRenderer", Category = "Stride.Rendering", FragmentSelection = FragmentSelection.Explicit)]
    [Smell(SymbolSmell.Experimental)]
    [SupportedOSPlatform("windows")]
    public sealed class QtRendererNode : RendererBase
    {
        private readonly SharedTextureScene _scene;
        private readonly ILogger _logger;
        private readonly ViewportOffset _offset = new();
        private IInputSource? _inputSource;
        // Size of the last draw's viewport (the scene renders at it from the next Update on); none before the first draw.
        private Int2 _drawSize;
        private bool _drawn;
        // The target of the last draw: the Stride texture (for Destroyed), its native resource (a swap chain reuses the
        // Stride texture for a new back buffer), the composition decided for it and the UNorm view on it (Direct, if the
        // target's own view is not UNorm).
        private Texture? _target;
        private IntPtr _targetResource;
        private LayerComposition _composition = LayerComposition.Fallback(PixelFormat.B8G8R8A8_UNorm_SRgb);
        private Texture? _view;
        private string _hint = "", _loggedHint = "";

        /// <param name="nodeContext">Injected by vvvv.</param>
        [Fragment]
        public QtRendererNode(NodeContext nodeContext)
        {
            _scene = new SharedTextureScene(nodeContext);
            _logger = nodeContext.GetLogger();
        }

        /// <summary>Renders the element tree in the next draw of the layer.</summary>
        /// <param name="error">Last error (texture, binding, apply, poll or render in QtNative), empty if ok; otherwise the hint when the target has no UNorm view and the layer blends like QtTexture.</param>
        /// <param name="input">Root element (null = empty scene).</param>
        /// <param name="space">Coordinate space of the elements: origin and size of one unit, see <see cref="Space"/>.</param>
        /// <param name="clearColor">Background of the layer (straight alpha, premultiplied here). Alpha 0 shows the target through it.</param>
        /// <param name="enabled">False freezes the layer: no updates of Input, Space or Clear Color, nothing renders and no input reaches the scene; it keeps drawing the last content.</param>
        /// <param name="debug">Debug overlay for all elements of the scene (same as the Debug pin of Renderer [Qt]).</param>
        /// <returns>The layer: connect it where a Stride renderer goes, e.g. after the scene of a window.</returns>
        [Fragment]
        [return: Pin(Name = "Output")]
        public IGraphicsRendererBase Update(
            out string error,
            Element? input,
            [DefaultValue(typeof(Space), nameof(Space.DIPTopLeft))] Space space,
            [DefaultValue("0, 0, 0, 0")] Color4 clearColor,
            [DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(false)] bool debug)
        {
            // Size and format come from the last draw: before it nothing renders (render-texture.md, "Timing").
            if (_drawn)
                _scene.Update(input, _drawSize, _composition.CoreFormat, space, clearColor, true, enabled, debug, MultisampleCount.None);
            error = _scene.Error.Length > 0 ? _scene.Error : _hint;
            return this;
        }

        protected override void DrawCore(RenderDrawContext context)
        {
            var commandList = context.CommandList;
            SubscribeInput(context);

            // Destroyed: the back buffer is resized or the target disposed; its view goes with it.
            var target = commandList.RenderTarget;
            if (target != _target)
            {
                _target?.Destroyed -= OnTargetDestroyed;
                _target = target;
                _target?.Destroyed += OnTargetDestroyed;
            }
            if (target is null)
            {
                ReleaseView();
                return;
            }

            var viewport = context.RenderContext.ViewportState.Viewport0;
            _drawSize = LayerComposition.TextureSize(viewport.Width, viewport.Height);
            _drawn = true;
            _offset.Set(new Vector2(viewport.X, viewport.Y));

            var resource = StrideInterop.GetNativeResource(target, out var resourceFormat);
            if (resource != _targetResource)
            {
                ReleaseView();
                _targetResource = resource;
                ChooseComposition(target, resourceFormat);
            }

            // The scene's texture renders at the start of this draw (QtTextureRenderer, DrawStarted), fenced on Stride's
            // immediate context, which this draw follows. Drawn only in the format chosen for this target: after a target
            // change the texture of the old format is skipped until the next Update recreated it.
            var texture = _scene.Texture;
            if (texture is null || texture.ViewFormat != _composition.CoreFormat)
                return;
            using (context.PushRenderTargetsAndRestore())
            {
                // No depth buffer: the quad must neither be tested nor write depth.
                commandList.SetRenderTarget(null, _view ?? target);
                commandList.SetViewport(viewport);
                context.GraphicsContext.DrawTexture(texture, context.GraphicsDevice.SamplerStates.PointClamp, BlendStates.AlphaBlend);
            }
        }

        // Direct needs a UNorm view on the target's resource: the target itself if its view is UNorm, else a view of our own.
        // If Stride cannot create one (e.g. a format the device does not support for a multisampled view), fallback.
        private void ChooseComposition(Texture target, PixelFormat resourceFormat)
        {
            var composition = LayerComposition.Choose(resourceFormat, target.ViewFormat);
            if (composition.Direct && composition.ViewFormat != target.ViewFormat)
            {
                try
                {
                    _view = target.ToTextureView(new TextureViewDescription()
                    {
                        Format = composition.ViewFormat,
                        Flags = TextureFlags.RenderTarget,
                        Type = ViewType.Single,
                        ArraySlice = target.ArraySlice,
                        MipLevel = target.MipLevel,
                    });
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "QtRenderer: no {Format} view on the render target, drawing as QtTexture", composition.ViewFormat);
                    composition = LayerComposition.Fallback(target.ViewFormat);
                }
            }
            _composition = composition;
            _hint = composition.Hint(resourceFormat);
            if (_hint != _loggedHint)
            {
                _loggedHint = _hint;
                if (_hint.Length > 0)
                    _logger.LogInformation("{Hint}", _hint);
            }
        }

        // Like SkiaRenderer: the window input source of the render context; a new source re-subscribes.
        private void SubscribeInput(RenderDrawContext context)
        {
            var inputSource = context.RenderContext.GetWindowInputSource();
            if (inputSource == _inputSource)
                return;
            _inputSource = inputSource;
            var inputManager = inputSource is null ? null : context.RenderContext.Services.GetService<InputManager>();
            _scene.Input.Subscribe(inputManager is null ? null
                : inputManager.GetNotifications(inputSource, sender: this).Select(n => n.Transform(_offset)));
        }

        private void OnTargetDestroyed(object? sender, EventArgs e) => ReleaseView();

        private void ReleaseView()
        {
            _targetResource = IntPtr.Zero;
            _view?.Dispose();
            _view = null;
        }

        protected override void Destroy()
        {
            _target?.Destroyed -= OnTargetDestroyed;
            _target = null;
            ReleaseView();
            _scene.Dispose();
            base.Destroy();
        }

        // Window positions to texture pixels: minus the origin of the last draw's viewport. Read on the thread the input
        // arrives on, set in the draw.
        private sealed class ViewportOffset : INotificationSpaceTransformer
        {
            private Vector2 _origin;
            private readonly object syncRoot = new();

            public void Set(Vector2 origin)
            {
                lock (syncRoot)
                    _origin = origin;
            }

            public Vector2 TransformPosition(Vector2 point)
            {
                lock (syncRoot)
                    return LayerComposition.ToTexturePixels(point, _origin);
            }

            // 1 DIP = 1 pixel: sizes (client area, touch contact) stay.
            public Vector2 TransformSize(Vector2 size) => size;
        }
    }
}
