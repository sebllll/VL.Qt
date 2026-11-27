using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using VL.Core;
using VL.Core.Diagnostics;
using VL.Lib.Collections;
using VL.Qt.NodeFactories;

namespace VL.Qt.Qml
{
    /// <summary>
    /// The exact pins of a generated QML node, built once per node description from the parsed declarations. Both the
    /// description (<see cref="InputDescriptions"/>, <see cref="OutputDescriptions"/>) and the instance
    /// (<see cref="QmlNodeInstance"/>) take the order from here; the common pins, in the order of QmlElement, come from
    /// <see cref="QmlCommonPins"/>. A property or signal whose pin name is taken gets a number, "Size 2", with a warning.
    /// The pins are listed under "Which declarations give which pins" in docs/user-qml.md.
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
        /// <summary>Input pin descriptions in pin order: the leading common pins, one per property, Properties, the trailing common pins.</summary>
        public List<IVLPinDescription> InputDescriptions()
        {
            var inputs = new List<IVLPinDescription>(QmlCommonPins.LeadingInputs.Length + PropertyInputs.Length + 1 + QmlCommonPins.TrailingInputs.Length);
            foreach (var pin in QmlCommonPins.LeadingInputs)
                inputs.Add(Describe(pin));
            foreach (var pin in PropertyInputs)
                inputs.Add(new FilePinDescription(pin.Name, pin.PinType, pin.PinDefault, PinSummary(pin.Property, pin.EnumSlot)));
            inputs.Add(Describe(QmlCommonPins.Properties));
            foreach (var pin in QmlCommonPins.TrailingInputs)
                inputs.Add(Describe(pin));
            return inputs;
        }

        /// <summary>Output pin descriptions in pin order: Output, one per property, one per signal, the trailing common pins.</summary>
        public List<IVLPinDescription> OutputDescriptions()
        {
            var outputs = new List<IVLPinDescription>(1 + PropertyOutputs.Length + SignalOutputs.Length + QmlCommonPins.TrailingOutputs.Length) { Describe(QmlCommonPins.Output) };
            foreach (var pin in PropertyOutputs)
                outputs.Add(new FilePinDescription(pin.Name, pin.PinType, pin.PinDefault, PinSummary(pin.Property, pin.EnumSlot), pin.IsVisible));
            foreach (var pin in SignalOutputs)
                outputs.Add(new FilePinDescription(pin.Name, pin.Type, null, SignalSummary(pin.Signal)));
            foreach (var pin in QmlCommonPins.TrailingOutputs)
                outputs.Add(Describe(pin));
            return outputs;
        }

        /// <summary>True if a message is an error: the file does not read or parse, or its name exists twice.</summary>
        public bool HasErrors => Messages.Any(static m => m.Type == MessageType.Error);

        /// <summary>
        /// The last good layout <paramref name="good"/> with the errors of the version <paramref name="failed"/> that does not read
        /// or parse, and a note that the pins are the old ones (<see cref="LastGood{T}"/>).
        /// </summary>
        internal static QmlNodeLayout KeepPins(QmlNodeLayout good, QmlNodeLayout failed)
        {
            var messages = ImmutableArray.CreateBuilder<Message>(failed.Messages.Length + good.Messages.Length + 1);
            messages.AddRange(failed.Messages.Where(static m => m.Type == MessageType.Error));
            messages.Add(new Message(MessageType.Warning, LastGood<QmlNodeLayout>.KeptNote));
            messages.AddRange(good.Messages);
            return good with { Messages = messages.ToImmutable() };
        }

        private static IVLPinDescription Describe(QmlCommonPin pin)
            => new FilePinDescription(pin.Name, pin.Type, pin.Default, pin.Summary.Length == 0 ? null : pin.Summary, pin.IsVisible);

        /// <summary>
        /// Reads and parses the file (siblings from its folder) and builds the layout. A file that cannot be read gives an
        /// error message and only the common pins. <paramref name="pool"/>: see <see cref="Build"/>.
        /// </summary>
        public static QmlNodeLayout Load(QmlFile file, ILogger logger, QmlEnumPool? pool = null)
        {
            QmlDeclarations declarations;
            string? readError = null;
            try
            {
                var dir = Path.GetDirectoryName(file.FilePath) ?? string.Empty;
                declarations = QmlDeclarations.Parse(File.ReadAllText(file.FilePath), Path.GetFileNameWithoutExtension(file.FilePath), name => ReadSibling(dir, name));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                declarations = QmlDeclarations.Empty;
                readError = $"{file.FilePath} cannot be read: {e.Message}";
            }
            var layout = Build(file.FilePath, declarations, JoinErrors(file.DuplicateError, readError), pool);
            foreach (var m in layout.Messages)
            {
                if (m.Type == MessageType.Error)
                    logger.LogError("{Qml}: {Error}", file.FilePath, m.Text);
                else if (m.Type == MessageType.Warning)
                    logger.LogWarning("{Qml}: {Warning}", file.FilePath, m.Text);
            }
            return layout;
        }

        private static string? JoinErrors(string? first, string? second)
            => first != null && second != null ? first + "\n" + second : first ?? second;

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

        /// <summary>
        /// Builds the pins from <paramref name="declarations"/>; <paramref name="extraError"/> (duplicate file, read error) comes first.
        /// With a <paramref name="pool"/> the pins of a property with a QML enum get the pool type of the enum; where the pool has
        /// none, or without a pool, they stay int and the reason of the pool is a warning. The node description passes
        /// <see cref="QmlEnumPool.Shared"/>; a caller that only needs the declarations passes none, so it claims nothing.
        /// </summary>
        public static QmlNodeLayout Build(string filePath, QmlDeclarations declarations, string? extraError = null, QmlEnumPool? pool = null)
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

            var used = new HashSet<string>(QmlCommonPins.ReservedNames, StringComparer.OrdinalIgnoreCase);
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

            var folder = BaseFolder(filePath);
            var inputs = ImmutableArray.CreateBuilder<QmlInputPin>();
            var outputs = ImmutableArray.CreateBuilder<QmlOutputPin>();
            foreach (var declared in declarations.Properties)
            {
                var property = ResolveUrls(declared, folder);
                var name = Unique(property.Name, "property");
                QmlEnumSlot? slot = null;
                if (pool != null && property.Enum is { } enumDecl)
                {
                    // Claimed again at every build, so a hot reload keeps the type of the enum and updates its entries.
                    var claim = pool.Claim(filePath, enumDecl);
                    slot = claim.Slot;
                    if (claim.Message is { } text && !warnings.Contains(text))
                        warnings.Add(text);
                }
                if (!property.IsReadOnly)
                    inputs.Add(new QmlInputPin(name, property, slot));
                outputs.Add(new QmlOutputPin(name, property, property.IsReadOnly || property.Initializer == QmlInitializer.Binding, slot));
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

        /// <summary>The folder a relative url of the file is resolved against, null if the path has none.</summary>
        internal static string? BaseFolder(string filePath)
        {
            var folder = Path.GetDirectoryName(filePath);
            return string.IsNullOrEmpty(folder) ? null : folder;
        }

        // A url literal is relative to the file, as in QML; the pin holds it resolved (QmlTypes.UrlToPath).
        private static QmlPropertyDecl ResolveUrls(QmlPropertyDecl property, string? folder) => property.Default switch
        {
            VL.Lib.IO.Path url => property with { Default = QmlTypes.UrlToPath(url.Value ?? string.Empty, folder) },
            Spread<VL.Lib.IO.Path> { Count: > 0 } urls => property with { Default = urls.Select(u => QmlTypes.UrlToPath(u.Value ?? string.Empty, folder)).ToSpread() },
            _ => property,
        };

        private static string PinSummary(QmlPropertyDecl property, QmlEnumSlot? slot)
        {
            var summary = property.Summary ?? (property.AliasOf is { } target
                ? $"{property.QmlType} property '{property.Name}' of the QML root, an alias of {target}."
                : $"{(property.IsReadOnly ? "readonly " : string.Empty)}{property.QmlType} property '{property.Name}' of the QML root.");
            if (property.Enum is not { } values)
                return summary;
            // A dropdown shows the names; the numbers matter only where the pin is an int.
            return $"{summary} {(slot is null ? values.Describe() : values.DescribeNames())}.";
        }

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
