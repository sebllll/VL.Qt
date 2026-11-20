using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Model;
using VL.Qt.Elements;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Loads a user .qml file into the element tree: an escape hatch for existing QML components and for things VL.Qt has no
    /// element for, not a second UI model. The root of the file must be an Item. Properties sets properties of the root by name,
    /// the outputs report the properties QML changed and the signals it emitted. Only what the file declares in its root
    /// (<c>property …</c>, <c>signal …</c>) comes back, not what Item inherits. Value types both ways: float, int, bool, string,
    /// Color4, Vector2/3/4, RectangleF. The file is reloaded when it, another *.qml next to it or qmldir changes.
    /// QML animations follow the frame clock of the renderer, a QML Timer runs on wall time. Only the LGPL Qt modules are shipped:
    /// an import of any other module (e.g. QtQuick3D, Charts) fails and shows at Error.
    /// Lengths of Size in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP); property values are passed on unchanged (QML units).
    /// </summary>
    // The reconciler sends the properties (SET_QML_PROPERTIES, only what changed) and the reload counter; this node owns the
    // file watcher (hot reload) and the feedback that receives the events of QtNative (1 frame latency).
    [ProcessNode(Name = "QmlElement", Category = Categories.Elements), Smell(SymbolSmell.Experimental)]
    public sealed class QmlElementNode : IDisposable
    {
        internal readonly record struct Props(string Source, int Reload, QmlProperties Properties, bool AutoSize);

        private readonly ElementNode<QmlElement, Props> _node;
        private readonly QmlFileWatcher _watcher;
        private readonly NodeErrors? _errors;
        private string? _file, _fileResolved;
        private string _source = string.Empty;
        private int _reload;
        private QmlProperties _properties = QmlProperties.Empty;
        private string _shownProblems = string.Empty, _elementError = string.Empty, _error = string.Empty;
        private Dictionary<string, object> _changedProperties = new();
        private int _seenPropertyUpdates;

        public QmlElementNode(NodeContext nodeContext)
        {
            _node = new ElementNode<QmlElement, Props>(nodeContext, Create);
            _watcher = new QmlFileWatcher();
            _errors = new NodeErrors(nodeContext);
        }

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal QmlElementNode(Func<string, IDisposable> addWarning)
            : this(addWarning, null)
        {
        }

        /// <summary>For tests: the folder watch comes from <paramref name="watchDir"/> (null = the real one).</summary>
        internal QmlElementNode(Func<string, IDisposable> addWarning, Func<string, IObservable<FileSystemEventArgs>>? watchDir)
        {
            _node = new ElementNode<QmlElement, Props>(addWarning, Create);
            _watcher = watchDir is null ? new QmlFileWatcher() : new QmlFileWatcher(watchDir);
        }

        private static readonly Func<CommonInputs, Props, FeedbackSlot, QmlElement> Create = static (c, p, slot) => new QmlElement
            {
                Size = c.Size,
                Transform = c.Transform,
                Anchor = c.Anchor,
                Opacity = c.Opacity,
                Clip = c.Clip,
                Debug = c.Debug,
                Interaction = c.Interaction,
                Effect = c.Effect,
                Feedback = slot,
                AutoSize = p.AutoSize,
                Source = p.Source,
                Reload = p.Reload,
                Properties = p.Properties,
            };

        internal ElementNode<QmlElement, Props> Core => _node;

        /// <summary>Describes the QML element. A new description is only created when an input changes (also when the file was reloaded).</summary>
        /// <param name="file">The .qml file, its root must be an Item. A relative path is taken from the working directory of the app (vvvv resolves a Path IOBox against the document). Empty = nothing loaded. The file and the *.qml and qmldir next to it are watched: saving one reloads the element and sends all properties again; the outputs start empty again.</param>
        /// <param name="properties">Values for properties of the QML root by name (float, double, int, bool, string, Color4, Vector2/3/4, RectangleF). Only entries that changed are sent. A key that is removed is not reset in QML. Unknown names and values QML cannot convert show at Error and are ignored; a float for an int property is converted by Qt.</param>
        /// <param name="size">Size of the element (width, height), only with Auto Size off: it sets width and height of the QML root. The position comes from Transform and Anchor.</param>
        /// <param name="autoSize">On: the element takes the implicit size (implicitWidth, implicitHeight) of the QML root. Off: Size counts.</param>
        /// <param name="transform">Position, rotation and scale; applied around the Anchor (like Skia). Inside Row/Column the layout places the element and Transform is only visual.</param>
        /// <param name="anchor">Point of the element that lies at the origin of its parent space (default TopLeft: the top-left corner; Center: the element is centered on it), resolved against the actual size; Transform rotates/scales around it. Inside Row/Column only the pivot of Transform.</param>
        /// <param name="opacity">Opacity of the element. 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas).</param>
        /// <param name="clip">Clip children to the element's size.</param>
        /// <param name="debug">Debug overlay for this element only. Does not affect layout or hit-testing. Cyan frame = actual size (solid: Size, dashed: Auto Size on), orange = implicit size, i.e. what Auto Size would give (only where it differs), magenta cross = origin, yellow diamond = anchor.</param>
        /// <param name="interaction">Chain of interaction nodes (Hover, Click, …). Without interaction the element is transparent for input.</param>
        /// <param name="effect">Chain of effect nodes (Blur, DropShadow, ColorAdjust, Colorize, Mask), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer.</param>
        /// <param name="changedProperties">Last value of each root property that QML changed (a change the patch caused with Properties is not reported back). A new dictionary instance whenever something changed; reset when the file is reloaded.</param>
        /// <param name="signals">Signals of the QML root emitted since the last frame, in order. Each has its name and its arguments.</param>
        /// <param name="measuredSize">Implicit size of the QML root reported by Qt, in units.</param>
        /// <param name="sceneBounds">Bounds in window coordinates after layout and transform, in units (last frame).</param>
        /// <param name="error">Compile, load and type errors of the QML file (with file and line), properties that could not be set; empty = OK.</param>
        [return: Pin(Name = "Output")]
        public Element Update(
            Path? file,
            Dictionary<string, object>? properties,
            [DefaultValue("1, 1")] Vector2 size,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Name = "Properties")] out Dictionary<string, object> changedProperties,
            out Spread<QmlSignal> signals,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds,
            out string error)
        {
            var feedback = _node.Feedback;
            var source = ResolveSource(file?.Value);
            bool sourceChanged = !string.Equals(source, _source, StringComparison.Ordinal);
            _watcher.Update(source);
            bool reloaded = !sourceChanged && _watcher.TakeChanged();
            if (reloaded)
                unchecked { _reload++; }
            if (sourceChanged || reloaded)
                feedback.ResetQml(); // outputs belong to the old instance
            _source = source;

            _properties = QmlProperties.Update(_properties, properties);
            var element = _node.Describe(
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect),
                new Props(source, _reload, _properties, autoSize));
            _node.ReadOutputs(out measuredSize, out sceneBounds);

            int updates = feedback.QmlPropertyUpdates();
            if (updates != _seenPropertyUpdates)
            {
                _seenPropertyUpdates = updates;
                _changedProperties = feedback.QmlPropertyValues() is { Count: > 0 } values ? new Dictionary<string, object>(values, StringComparer.Ordinal) : new Dictionary<string, object>();
            }
            changedProperties = _changedProperties;
            signals = feedback.TakeQmlSignals();
            error = UpdateError();
            return element;
        }

        /// <summary>The full path of <paramref name="value"/>, cached for the last input (no work per frame); empty for none.</summary>
        private string ResolveSource(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            if (ReferenceEquals(value, _file) || value == _file)
                return _fileResolved!;
            _file = value;
            try
            {
                _fileResolved = System.IO.Path.GetFullPath(value);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _fileResolved = value; // QtNative reports a file it cannot open
            }
            return _fileResolved;
        }

        /// <summary>Joins the property problems and the error Qt reported for the element, one line each; recomputed only when a part changes.</summary>
        private string UpdateError()
        {
            string problems = _properties.Problems, element = _node.Error ?? string.Empty;
            if (problems != _shownProblems || element != _elementError)
            {
                _shownProblems = problems;
                _elementError = element;
                _error = problems.Length == 0 ? element : element.Length == 0 ? problems : problems + "\n" + element;
                // Qt's element error is already logged and shown as warning by the element node.
                _errors?.LogOnly(problems);
            }
            return _error;
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _errors?.Dispose();
            _node.Dispose();
        }
    }
}
