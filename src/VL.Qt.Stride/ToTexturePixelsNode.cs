using System;
using System.ComponentModel;
using System.Threading;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.IO.Notifications;

namespace VL.Qt.Stride
{
    /// <summary>
    /// Maps the input of a window (vvvv notifications, positions in client pixels) to the pixels of a QtTexture drawn on a
    /// quad, for its Input Notifications pin. The quad is Stride's unit quad (-0.5 ... 0.5 in its local XY plane, +Y up)
    /// placed by World and seen through View and Projection; fullscreen: World = Scaling (2, 2, 1), View and Projection identity.
    /// </summary>
    [ProcessNode(Name = "ToTexturePixels", Category = "Stride.Input")]
    [Smell(SymbolSmell.Experimental)]
    public sealed class ToTexturePixelsNode : IDisposable
    {
        // Read by the thread the notifications arrive on, replaced by Update only when an input changed.
        private TexturePixelMapping? _mapping;
        private Matrix _world, _view, _projection;
        private Int2 _textureSize;
        private IObservable<INotification>? _input;
        private IObservable<INotification>? _output;

        /// <param name="nodeContext">Injected by vvvv.</param>
        public ToTexturePixelsNode(NodeContext nodeContext)
        {
        }

        /// <summary>Maps the positions of the notifications at Input to texture pixels (top-left origin).</summary>
        /// <param name="input">Notifications of the window the texture is drawn in (e.g. of its Input Source).</param>
        /// <param name="world">Transformation of the quad the texture is drawn on (a zero matrix counts as identity).</param>
        /// <param name="view">View of the camera (a zero matrix counts as identity).</param>
        /// <param name="projection">Projection of the camera (a zero matrix counts as identity).</param>
        /// <param name="textureSize">Size of the texture in pixels (Size of the QtTexture).</param>
        /// <returns>The notifications with positions in texture pixels; positions off the quad lie outside 0 ... Texture Size, keyboard
        /// notifications pass unchanged. The same observable while Input stays the same.</returns>
        [return: Pin(Name = "Output")]
        public IObservable<INotification>? Update(
            IObservable<INotification>? input,
            Matrix world,
            Matrix view,
            Matrix projection,
            [DefaultValue(typeof(Int2), "1920, 1080")] Int2 textureSize)
        {
            if (_mapping is null || world != _world || view != _view || projection != _projection || textureSize != _textureSize)
            {
                _world = world;
                _view = view;
                _projection = projection;
                _textureSize = textureSize;
                Volatile.Write(ref _mapping, new TexturePixelMapping(world, view, projection, new Vector2(textureSize.X, textureSize.Y)));
            }
            if (!ReferenceEquals(input, _input))
            {
                _input = input;
                _output = input is null ? null : new Mapped(input, this);
            }
            return _output;
        }

        public void Dispose()
        {
        }

        private TexturePixelMapping Mapping => Volatile.Read(ref _mapping)!;

        private sealed class Mapped : IObservable<INotification>
        {
            private readonly IObservable<INotification> _source;
            private readonly ToTexturePixelsNode _owner;

            public Mapped(IObservable<INotification> source, ToTexturePixelsNode owner)
            {
                _source = source;
                _owner = owner;
            }

            public IDisposable Subscribe(IObserver<INotification> observer) => _source.Subscribe(new Mapper(observer, _owner));
        }

        // One per subscription: OnNext is sequential, so the client area of the current notification lives in a field.
        private sealed class Mapper : IObserver<INotification>, INotificationSpaceTransformer
        {
            private readonly IObserver<INotification> _observer;
            private readonly ToTexturePixelsNode _owner;
            private TexturePixelMapping _mapping;
            private Vector2 _clientArea;

            public Mapper(IObserver<INotification> observer, ToTexturePixelsNode owner)
            {
                _observer = observer;
                _owner = owner;
                _mapping = owner.Mapping;
            }

            public void OnNext(INotification value)
            {
                if (value is INotificationWithPosition positioned)
                    _clientArea = positioned.ClientArea;
                else if (value is NotificationWithClientArea withArea)
                    _clientArea = withArea.ClientArea;
                else
                {
                    // Keyboard, focus: nothing to map.
                    _observer.OnNext(value);
                    return;
                }
                _mapping = _owner.Mapping;
                // Transform builds the same notification with its positions and sizes mapped.
                _observer.OnNext(value.Transform(this));
            }

            public void OnError(Exception error) => _observer.OnError(error);

            public void OnCompleted() => _observer.OnCompleted();

            Vector2 INotificationSpaceTransformer.TransformPosition(Vector2 point) => _mapping.Map(point, _clientArea);

            // The client area becomes the texture size, a touch's contact area scales with it.
            Vector2 INotificationSpaceTransformer.TransformSize(Vector2 size)
                => _clientArea.X <= 0 || _clientArea.Y <= 0 ? size : size * _mapping.TextureSize / _clientArea;
        }
    }
}
