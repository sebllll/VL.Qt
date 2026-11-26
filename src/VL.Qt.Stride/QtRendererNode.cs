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
using VL.Lang;
using VL.Lib.IO.Notifications;
using VL.Model;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Stride;
using VL.Stride.Input;

namespace VL.Qt.Stride
{
    /// <summary>
    /// Draws the element tree at Input as a layer into the Stride render target it is drawn into (the window, a render
    /// texture), sized to the viewport, like SkiaRenderer. Into a window or a typeless 8-bit render texture it blends like a
    /// Qt window (gamma space, premultiplied); into other targets it blends like QtTexture (linear light). Takes the window's
    /// mouse, touch and keyboard input. Draws into one render target. Belongs after the 3D scene, not inside it.
    /// </summary>
    [ProcessNode(Name = "QtRenderer", Category = "Stride.Rendering", FragmentSelection = FragmentSelection.Explicit)]
    [Smell(SymbolSmell.Experimental)]
    [SupportedOSPlatform("windows")]
    public sealed class QtRendererNode : RendererBase
    {
        private readonly NodeContext _nodeContext;
        // Created in the first Update, so the scene gets the executor of that Update's Render Threads.
        private SharedTextureScene? _scene;
        private readonly ILogger _logger;
        private const string OneTargetHint = "QtRenderer draws into one target per frame; use one QtRenderer per window";
        private readonly ViewportOffset _offset = new();
        private IInputSource? _inputSource;
        // Size of the last draw's viewport (the scene renders at it from the next Update on); none before the first draw.
        private Int2 _drawSize;
        // Frame of the last draw (Time.FrameCount, -1 before the first one) and the value Update saw last: they differ if a
        // draw happened since that Update. One target per frame: a second, different target in a frame is not drawn.
        private int _lastDrawFrame = -1, _lastDrawFrameSeen = -1;
        private bool _conflict;
        // The target of the last draw: the Stride texture (for Destroyed), its native resource (a swap chain reuses the
        // Stride texture for a new back buffer), the composition decided for it and the UNorm view on it (Direct, if the
        // target's own view is not UNorm).
        private Texture? _target;
        private IntPtr _targetResource;
        private LayerComposition _composition = LayerComposition.Fallback(PixelFormat.B8G8R8A8_UNorm_SRgb);
        private Texture? _view;
        // Set in the draw, shown at the node (and logged once per text) in Update.
        private string _hint = "";
        private NodeMessage? _hintMessage, _conflictMessage;

        /// <param name="nodeContext">Injected by vvvv.</param>
        [Fragment]
        public QtRendererNode(NodeContext nodeContext)
        {
            _nodeContext = nodeContext;
            _logger = nodeContext.GetLogger();
        }

        /// <summary>Renders the element tree in the next draw of the layer.</summary>
        /// <param name="input">Root element (null = empty scene).</param>
        /// <param name="space">Coordinate space of the elements: origin and size of one unit, see <see cref="Space"/>.</param>
        /// <param name="clearColor">Background of the layer (straight alpha, premultiplied here). Alpha 0 shows the target through it.</param>
        /// <param name="enabled">False freezes the layer: no updates of Input, Space or Clear Color, nothing renders and no input reaches
        /// the scene; it keeps drawing the last content.</param>
        /// <param name="debug">Debug overlay for all elements of the scene (same as the Debug pin of Renderer [Qt]).</param>
        /// <param name="renderThreads">Number of Qt render threads that render the QtTextures and QtRenderers of the process (0 ... 16; 0
        /// renders them on the main thread, for diagnosis). The largest value of all these nodes counts. A scene keeps the thread it got
        /// when it was created: a larger value adds threads at once, a smaller value and 0 apply to scenes created afterwards.</param>
        /// <returns>The layer: connect it where a Stride renderer goes, e.g. after the scene of a window.</returns>
        [Fragment]
        [return: Pin(Name = "Output")]
        public IGraphicsRendererBase Update(
            IElement? input,
            [DefaultValue(typeof(Space), nameof(Space.DIPTopLeft))] Space space,
            [DefaultValue("0, 0, 0, 0")] Color4 clearColor,
            [DefaultValue(true)] bool enabled,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(false)] bool debug,
            [Pin(Name = "Render Threads", Visibility = PinVisibility.Optional), DefaultValue(1)] int renderThreads)
        {
            if (_scene is null)
                _scene = new SharedTextureScene(_nodeContext, renderThreads);
            else
                _scene.SetRenderThreads(renderThreads);
            // Size and format come from the last draw: before it nothing renders (render-texture.md, "Timing"). A layer that
            // was not drawn since the last Update (disconnected) freezes: nothing renders for nobody.
            var drawnLastFrame = _lastDrawFrame != _lastDrawFrameSeen;
            _lastDrawFrameSeen = _lastDrawFrame;
            if (_lastDrawFrame >= 0)
                _scene.Update(input, _drawSize, _composition.CoreFormat, space, clearColor, true, enabled && drawnLastFrame, debug, MultisampleCount.None);
            _hintMessage ??= new NodeMessage(_scene.Messages, "Blend", MessageSeverity.Warning);
            _conflictMessage ??= new NodeMessage(_scene.Messages, "Target", MessageSeverity.Warning);
            _hintMessage.Set(_hint);
            _conflictMessage.Set(_conflict ? OneTargetHint : null);
            // Seen again in the next draw if the second target is still there.
            _conflict = false;
            return this;
        }

        protected override void DrawCore(RenderDrawContext context)
        {
            // Drawn before the first Update: no scene yet.
            if (_scene is null)
                return;
            var commandList = context.CommandList;
            var target = commandList.RenderTarget;
            var frame = context.RenderContext.Time.FrameCount;
            // One state for one target: a second, different target in the same frame would rebuild the view and the input
            // subscription on every draw and show the scene stretched to it.
            if (target is not null && frame == _lastDrawFrame && target != _target)
            {
                _conflict = true;
                return;
            }
            SubscribeInput(context, _scene);

            // Destroyed: the back buffer is resized or the target disposed; its view goes with it.
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
            _lastDrawFrame = frame;
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
        }

        // Like SkiaRenderer: the window input source of the render context; a new source re-subscribes.
        private void SubscribeInput(RenderDrawContext context, SharedTextureScene scene)
        {
            var inputSource = context.RenderContext.GetWindowInputSource();
            if (inputSource == _inputSource)
                return;
            _inputSource = inputSource;
            var inputManager = inputSource is null ? null : context.RenderContext.Services.GetService<InputManager>();
            scene.Input.Subscribe(inputManager is null ? null
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
            _hintMessage?.Dispose();
            _conflictMessage?.Dispose();
            _scene?.Dispose();
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
