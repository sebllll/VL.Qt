using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Diagnostics;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.NodeFactories;

namespace VL.Qt.Qml
{
    /// <summary>
    /// The exact pins of a generated QML node, built once per node description from the parsed declarations. Both the
    /// description (<see cref="InputDescriptions"/>, <see cref="OutputDescriptions"/>) and the instance
    /// (<see cref="QmlNodeInstance"/>) take the order from here.
    /// <para>Inputs, in this order:</para>
    /// <list type="number">
    /// <item>one pin per <see cref="PropertyInputs"/> (declaration order, inherited first), visible, type from
    /// <see cref="QmlTypes.ClrType"/>, default <see cref="QmlPropertyDecl.Default"/>;</item>
    /// <item>Properties: Dictionary&lt;string, object&gt;, default null, optional (plan B for what has no pin);</item>
    /// <item>Size (Vector2, 1, 1), Auto Size (bool, true, optional), Transform (Matrix, identity), Anchor (RectangleAnchor, TopLeft, optional),
    /// Opacity (float, 1, optional), Clip (bool, optional), Debug (bool, optional), Interaction (optional), Effect (optional): exactly as QmlElement.</item>
    /// </list>
    /// <para>Outputs, in this order:</para>
    /// <list type="number">
    /// <item>Output (Element);</item>
    /// <item>one pin per <see cref="PropertyOutputs"/> (declaration order), visibility <see cref="QmlOutputPin.IsVisible"/>;</item>
    /// <item>one pin per <see cref="SignalOutputs"/> (declaration order), visible;</item>
    /// <item>Measured Size (Vector2, optional), Scene Bounds (RectangleF, optional), Error (string).</item>
    /// </list>
    /// A property or signal whose pin name is taken (by a common pin or an earlier declaration) gets a number, "Size 2", with a warning.
    /// </summary>
    /// <param name="FilePath">Full path of the .qml file (the File input of the QmlElementNode underneath).</param>
    /// <param name="Declarations">What the parser found.</param>
    /// <param name="PropertyInputs">Input pins of the properties.</param>
    /// <param name="PropertyOutputs">Output pins of the properties.</param>
    /// <param name="SignalOutputs">Output pins of the signals.</param>
    /// <param name="Messages">Messages of the node description: errors, then warnings, then one info with all declarations that got no pin.</param>
    /// <param name="Summary">Node help: @summary of the file head, null if none.</param>
    internal sealed record QmlNodeLayout(
        string FilePath,
        QmlDeclarations Declarations,
        ImmutableArray<QmlInputPin> PropertyInputs,
        ImmutableArray<QmlOutputPin> PropertyOutputs,
        ImmutableArray<QmlSignalPin> SignalOutputs,
        ImmutableArray<Message> Messages,
        string? Summary)
    {
        /// <summary>Name of the optional dictionary input.</summary>
        public const string PropertiesPinName = "Properties";

        /// <summary>Pin names of the common inputs, in pin order (after the property inputs and Properties).</summary>
        public static readonly ImmutableArray<string> CommonInputNames = ImmutableArray.Create(
            "Size", "Auto Size", "Transform", "Anchor", "Opacity", "Clip", "Debug", "Interaction", "Effect");

        /// <summary>Pin names a property or signal pin must not take: the common pins of both sides and Properties.</summary>
        public static readonly ImmutableHashSet<string> ReservedPinNames = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            PropertiesPinName, "Size", "Auto Size", "Transform", "Anchor", "Opacity", "Clip", "Debug", "Interaction", "Effect",
            "Output", "Measured Size", "Scene Bounds", "Error");

        // Help texts like QmlElementNode.Update.
        private static readonly IVLPinDescription s_propertiesInput = new FilePinDescription(PropertiesPinName, typeof(Dictionary<string, object>), null,
            "Plan B for properties of the QML root that get no pin (alias, var, list, object types): values by name, as at QmlElement. A pin of the node wins over an entry with the same name.",
            isVisible: false);

        private static readonly IVLPinDescription[] s_commonInputs =
        {
            new FilePinDescription("Size", typeof(Vector2), new Vector2(1f, 1f), "Size of the element (width, height), only with Auto Size off: it sets width and height of the QML root. The position comes from Transform and Anchor."),
            new FilePinDescription("Auto Size", typeof(bool), true, "On: the element takes the implicit size (implicitWidth, implicitHeight) of the QML root. Off: Size counts.", isVisible: false),
            new FilePinDescription("Transform", typeof(Matrix), Matrix.Identity, "Position, rotation and scale; applied around the Anchor (like Skia)."),
            new FilePinDescription("Anchor", typeof(RectangleAnchor), RectangleAnchor.TopLeft, "Point of the element that lies at the origin of its parent space; Transform rotates/scales around it.", isVisible: false),
            new FilePinDescription("Opacity", typeof(float), 1f, "Opacity of the element. 0 = not drawn, but still takes its space and receives input.", isVisible: false),
            new FilePinDescription("Clip", typeof(bool), false, "Clip children to the element's size.", isVisible: false),
            new FilePinDescription("Debug", typeof(bool), false, "Debug overlay for this element only.", isVisible: false),
            new FilePinDescription("Interaction", typeof(Interaction), null, "Chain of interaction nodes (Hover, Click, …). Without interaction the element is transparent for input.", isVisible: false),
            new FilePinDescription("Effect", typeof(Effect), null, "Chain of effect nodes (Blur, DropShadow, …), drawn as one effect layer on the element.", isVisible: false),
        };

        private static readonly IVLPinDescription s_output = new FilePinDescription("Output", typeof(Element), null, null);

        private static readonly IVLPinDescription[] s_trailingOutputs =
        {
            new FilePinDescription("Measured Size", typeof(Vector2), Vector2.Zero, "Implicit size of the QML root reported by Qt, in units (last frame).", isVisible: false),
            new FilePinDescription("Scene Bounds", typeof(RectangleF), RectangleF.Empty, "Bounds in window coordinates after layout and transform, in units (last frame).", isVisible: false),
            new FilePinDescription("Error", typeof(string), string.Empty, "Compile, load and type errors of the QML file (with file and line), properties that could not be set; empty = OK."),
        };

        /// <summary>Input pin descriptions in pin order (see the type summary).</summary>
        public List<IVLPinDescription> InputDescriptions()
        {
            var inputs = new List<IVLPinDescription>(PropertyInputs.Length + 1 + s_commonInputs.Length);
            foreach (var pin in PropertyInputs)
                inputs.Add(new FilePinDescription(pin.Name, pin.Property.ClrType, pin.Property.Default, PinSummary(pin.Property)));
            inputs.Add(s_propertiesInput);
            inputs.AddRange(s_commonInputs);
            return inputs;
        }

        /// <summary>Output pin descriptions in pin order (see the type summary).</summary>
        public List<IVLPinDescription> OutputDescriptions()
        {
            var outputs = new List<IVLPinDescription>(1 + PropertyOutputs.Length + SignalOutputs.Length + s_trailingOutputs.Length) { s_output };
            foreach (var pin in PropertyOutputs)
                outputs.Add(new FilePinDescription(pin.Name, pin.Property.ClrType, pin.Property.Default, PinSummary(pin.Property), pin.IsVisible));
            foreach (var pin in SignalOutputs)
                outputs.Add(new FilePinDescription(pin.Name, pin.Type, null, SignalSummary(pin.Signal)));
            outputs.AddRange(s_trailingOutputs);
            return outputs;
        }

        /// <summary>
        /// Reads and parses the file (siblings from its folder) and builds the layout. A file that cannot be read gives an
        /// error message and only the common pins.
        /// </summary>
        public static QmlNodeLayout Load(QmlFile file, ILogger logger)
        {
            QmlDeclarations declarations;
            string? readError = null;
            try
            {
                var dir = Path.GetDirectoryName(file.FilePath) ?? string.Empty;
                declarations = QmlDeclarations.Parse(File.ReadAllText(file.FilePath), name => ReadSibling(dir, name));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                declarations = QmlDeclarations.Empty;
                readError = $"{file.FilePath} cannot be read: {e.Message}";
            }
            var layout = Build(file.FilePath, declarations, file.DuplicateError ?? readError);
            foreach (var m in layout.Messages)
            {
                if (m.Type == MessageType.Error)
                    logger.LogError("{Qml}: {Error}", file.FilePath, m.Text);
                else if (m.Type == MessageType.Warning)
                    logger.LogWarning("{Qml}: {Warning}", file.FilePath, m.Text);
            }
            return layout;
        }

        /// <summary>Content of &lt;dir&gt;/&lt;typeName&gt;.qml, null if there is none or it cannot be read.</summary>
        internal static string? ReadSibling(string dir, string typeName)
        {
            if (typeName.Length == 0 || typeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return null;
            var path = Path.Combine(dir, typeName + ".qml");
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Builds the pins from <paramref name="declarations"/>; <paramref name="extraError"/> (duplicate file, read error) comes first.</summary>
        public static QmlNodeLayout Build(string filePath, QmlDeclarations declarations, string? extraError = null)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var infos = new List<string>();
            if (extraError != null)
                errors.Add(extraError);
            foreach (var m in declarations.Messages)
            {
                var list = m.Severity switch
                {
                    QmlMessageSeverity.Error => errors,
                    QmlMessageSeverity.Warning => warnings,
                    _ => infos,
                };
                list.Add(m.ToString());
            }

            var used = new HashSet<string>(ReservedPinNames, StringComparer.OrdinalIgnoreCase);
            string Unique(string qmlName, string what)
            {
                var wanted = PinNames.FromIdentifier(qmlName);
                var name = wanted;
                for (var i = 2; !used.Add(name); i++)
                    name = $"{wanted} {i}";
                if (name != wanted)
                    warnings.Add($"{what} '{qmlName}' shows as pin '{name}', '{wanted}' is taken");
                return name;
            }

            var inputs = ImmutableArray.CreateBuilder<QmlInputPin>();
            var outputs = ImmutableArray.CreateBuilder<QmlOutputPin>();
            foreach (var property in declarations.Properties)
            {
                var name = Unique(property.Name, "property");
                if (!property.IsReadOnly)
                    inputs.Add(new QmlInputPin(name, property));
                outputs.Add(new QmlOutputPin(name, property, property.IsReadOnly || property.Initializer == QmlInitializer.Binding));
            }

            var signals = ImmutableArray.CreateBuilder<QmlSignalPin>();
            foreach (var signal in declarations.Signals)
            {
                var argument = signal.Shape == QmlSignalShape.Values ? signal.Args[0].Kind : null;
                signals.Add(new QmlSignalPin(Unique(signal.Name, "signal"), signal, QmlTypes.SignalPinType(signal.Shape, argument)));
            }

            var messages = ImmutableArray.CreateBuilder<Message>(errors.Count + warnings.Count + (infos.Count > 0 ? 1 : 0));
            foreach (var e in errors)
                messages.Add(new Message(MessageType.Error, e));
            foreach (var w in warnings)
                messages.Add(new Message(MessageType.Warning, w));
            if (infos.Count > 0)
                messages.Add(new Message(MessageType.Info, "No pin (use the Properties input):\n" + string.Join('\n', infos)));

            return new QmlNodeLayout(filePath, declarations, inputs.ToImmutable(), outputs.ToImmutable(), signals.ToImmutable(),
                messages.MoveToImmutable(), declarations.Head.Summary);
        }

        private static string PinSummary(QmlPropertyDecl property)
            => property.Summary ?? $"{(property.IsReadOnly ? "readonly " : string.Empty)}{property.QmlType} property '{property.Name}' of the QML root.";

        private static string SignalSummary(QmlSignalDecl signal)
        {
            var help = signal.Shape switch
            {
                QmlSignalShape.Bang => "True in the frame the signal fired.",
                QmlSignalShape.Values => $"The values of '{signal.Args[0].Name}', one per emission this frame.",
                _ => "One entry per emission this frame, with its arguments.",
            };
            return signal.Summary is null ? $"Signal '{signal.Name}' of the QML root. {help}" : $"{signal.Summary} {help}";
        }
    }
}
