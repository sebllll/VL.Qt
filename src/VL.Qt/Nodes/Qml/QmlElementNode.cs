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
    /// an import of any other module (e.g. QtQuick3D, Charts) fails and shows at the node and in the log.
    /// Lengths of Size in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP); property values are passed on unchanged (QML units).
    /// </summary>
    // The reconciler sends the properties (SET_QML_PROPERTIES, only what changed) and the reload counter; this node owns the
    // file watcher (hot reload) and the feedback that receives the events of QtNative (1 frame latency).
    [ProcessNode(Name = "QmlElement", Category = Categories.Qml), Smell(SymbolSmell.Experimental)]
    public sealed class QmlElementNode : IDisposable
    {
        internal readonly record struct Props(string Source, int Reload, QmlProperties Properties, bool AutoSize);

        private readonly ElementNodeCore<QmlElement, Props> _node;
        private readonly QmlFileWatcher _watcher;
        private string? _file, _fileResolved;
        private string _source = string.Empty;
        private int _reload;
        private QmlProperties _properties = QmlProperties.Empty;
        private string _shownProblems = string.Empty;
        private Dictionary<string, object> _changedProperties = new();
        private int _seenPropertyUpdates;

        public QmlElementNode(NodeContext nodeContext)
        {
            _node = new ElementNodeCore<QmlElement, Props>(nodeContext, s_create);
            _watcher = new QmlFileWatcher();
        }

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal QmlElementNode(Func<string, IDisposable> addWarning)
            : this(addWarning, null)
        {
        }

        /// <summary>For tests: the folder watch comes from <paramref name="watchDir"/> (null = the real one).</summary>
        internal QmlElementNode(Func<string, IDisposable> addWarning, Func<string, IObservable<FileSystemEventArgs>>? watchDir)
        {
            _node = new ElementNodeCore<QmlElement, Props>(addWarning, s_create);
            _watcher = watchDir is null ? new QmlFileWatcher() : new QmlFileWatcher(watchDir);
        }

        private static readonly Func<CommonInputs, Props, FeedbackSlot, QmlElement> s_create = static (c, p, slot) => new QmlElement(c, slot)
            {
                AutoSize = p.AutoSize,
                Source = p.Source,
                Reload = p.Reload,
                Properties = p.Properties,
            };

        internal ElementNodeCore<QmlElement, Props> Core => _node;

        /// <param name="file">The .qml file, its root must be an Item. A relative path is taken from the working directory of the app (vvvv
        /// resolves a Path IOBox against the document). Empty = nothing loaded. The file and the *.qml and qmldir next to it are watched:
        /// saving one reloads the element and sends all properties again; the outputs start empty again.</param>
        /// <param name="properties">Values for properties of the QML root by name (float, double, int, bool, string, Color4, Vector2/3/4,
        /// RectangleF). Only entries that changed are sent. A key that is removed is not reset in QML. Unknown names and values QML cannot
        /// convert show at the node and in the log and are ignored; a float for an int property is converted by Qt.</param>
        /// <param name="size">Size of the element (width, height), only with Auto Size off: it sets width and height of the QML root. The
        /// position comes from Transform and Anchor.</param>
        /// <param name="autoSize">On: the element takes the implicit size (implicitWidth, implicitHeight) of the QML root. Off: Size counts.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='transform']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='anchor']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='opacity']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='clip']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='debug']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='interaction']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='effect']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='enabled']"/>
        /// <param name="changedProperties">Last value of each root property that QML changed (a change the patch caused with Properties is
        /// not reported back). A new dictionary instance whenever something changed; reset when the file is reloaded.</param>
        /// <param name="signals">Signals of the QML root emitted since the last frame, in order. Each has its name and its arguments.</param>
        /// <param name="measuredSize">Size the QML root takes with Auto Size on, in units.</param>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='sceneBounds']"/>
        /// <include file="../CommonPins.xml" path="docs/set[@name='element']/returns"/>
        [return: Pin(Name = "Output")]
        public IElement Update(
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoSize,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue("1, 1")] Vector2 size,
            Matrix transform,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(typeof(RectangleAnchor), nameof(RectangleAnchor.TopLeft))] RectangleAnchor anchor,
            Path? file,
            Dictionary<string, object>? properties,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)] float opacity,
            [Pin(Visibility = PinVisibility.Optional)] bool clip,
            [Pin(Visibility = PinVisibility.Optional)] bool debug,
            [Pin(Visibility = PinVisibility.Optional)] Interaction? interaction,
            [Pin(Visibility = PinVisibility.Optional)] Effect? effect,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool enabled,
            [Pin(Name = "Properties")] out Dictionary<string, object> changedProperties,
            out Spread<QmlSignal> signals,
            [Pin(Visibility = PinVisibility.Optional)] out Vector2 measuredSize,
            [Pin(Visibility = PinVisibility.Optional)] out RectangleF sceneBounds)
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
                new CommonInputs(size, transform, anchor, opacity, clip, debug, interaction, effect, enabled),
                new Props(source, _reload, _properties, autoSize),
                out measuredSize, out sceneBounds);

            int updates = feedback.QmlPropertyUpdates();
            if (updates != _seenPropertyUpdates)
            {
                _seenPropertyUpdates = updates;
                _changedProperties = feedback.QmlPropertyValues() is { Count: > 0 } values ? new Dictionary<string, object>(values, StringComparer.Ordinal) : new Dictionary<string, object>();
            }
            changedProperties = _changedProperties;
            signals = feedback.TakeQmlSignals();
            UpdateProblems();
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

        /// <summary>Shows the problems of the property values (unsupported type) as a warning at the node; an error Qt reported for the
        /// element is shown by the element node.</summary>
        private void UpdateProblems()
        {
            var problems = _properties.Problems;
            if (problems != _shownProblems)
            {
                _shownProblems = problems;
                _node.Messages.Warn(problems, "Properties");
            }
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _node.Dispose();
        }
    }
}
