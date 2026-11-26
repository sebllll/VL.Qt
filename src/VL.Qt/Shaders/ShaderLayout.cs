using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using VL.Core.Diagnostics;
using VL.Qt.NodeFactories;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// Everything the init of a generated shader node needs, built once per node description: the file sources, the pin model
    /// (with pin names made unique against the fixed pins) and the messages shown at the node. Errors (red): file not readable,
    /// compile error (then the pins of the last version that compiled, or only the fixed pins if there was none), duplicate file name.
    /// Warnings (orange): the pin model's warnings.
    /// </summary>
    /// <param name="HasInput">Node has an Input pin (sampler2D source).</param>
    /// <param name="Pins">Image and uniform pins in pin order, names unique.</param>
    /// <param name="Messages">Messages of the node description (errors first).</param>
    /// <param name="Summary">@summary of the file head, null if not given.</param>
    /// <param name="Remarks">@remarks of the file head, null if not given.</param>
    /// <param name="FragmentSource">Content of the .frag (empty if not readable).</param>
    /// <param name="VertexSource">Content of the .vert, null without .vert.</param>
    internal sealed record ShaderLayout(
        bool HasInput,
        ImmutableArray<ShaderPin> Pins,
        ImmutableArray<Message> Messages,
        string? Summary,
        string? Remarks,
        string FragmentSource,
        string? VertexSource)
    {
        /// <summary>The layout of the shader node of <paramref name="file"/>, made from its loaded <paramref name="source"/>.</summary>
        public static ShaderLayout From(ShaderSource source, ShaderFile file)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var pins = Common(source, file, errors, warnings, out var hasInput);
            AddKeptError(source, errors, warnings);
            // Input and Input Sampler are pins of the node only with an Input.
            var reserved = hasInput ? ShaderElementPins.Names : ShaderElementPins.Names.Remove(ShaderElementPins.Input.Name).Remove(ShaderElementPins.InputSampler.Name);
            return new ShaderLayout(hasInput, UniquePinNames(pins, reserved, warnings), ToMessages(errors, warnings), source.FragmentAnnotations.Summary, source.FragmentAnnotations.Remarks, source.FragmentSource, source.VertexSource);
        }

        /// <summary>
        /// The errors, warnings and pins that the shader node and the effect chain node of a file have in common: the pins with
        /// the names of the file, which each node makes unique against its own fixed pins.
        /// </summary>
        internal static ImmutableArray<ShaderPin> Common(ShaderSource source, ShaderFile file, List<string> errors, List<string> warnings, out bool hasInput)
        {
            if (file.DuplicateError != null)
                errors.Add(file.DuplicateError); // logged once per scan by the factory
            if (source.ReadError != null)
                errors.Add(source.ReadError);
            if (source.PinModel.Error.Length > 0)
                errors.Add(source.PinModel.Error);
            hasInput = source.HasInput;

            foreach (var w in source.PinModel.Warnings)
                warnings.Add(w.ToString());
            return source.PinModel.Pins;
        }

        /// <summary>The error of a file that fails now, with the note that the node keeps its last good pins (<see cref="ShaderSource.KeptError"/>).</summary>
        internal static void AddKeptError(ShaderSource source, List<string> errors, List<string> warnings)
        {
            if (source.KeptError is null)
                return;
            errors.Add(source.KeptError);
            warnings.Add(LastGood<ShaderSource>.KeptNote);
        }

        /// <summary>
        /// A uniform whose pin name is taken (by <paramref name="reserved"/>, the fixed pins of the node, or by another pin) gets a
        /// number: "Size 2". The name of a sampler pin follows the final name of its image pin ("{image} Sampler").
        /// </summary>
        internal static ImmutableArray<ShaderPin> UniquePinNames(ImmutableArray<ShaderPin> pins, ImmutableHashSet<string> reserved, List<string> warnings)
        {
            var used = new HashSet<string>(reserved, StringComparer.OrdinalIgnoreCase);
            var result = ImmutableArray.CreateBuilder<ShaderPin>(pins.Length);
            string? imageName = null;
            foreach (var pin in pins)
            {
                var wanted = pin.Type == ShaderPinType.Sampler && imageName != null ? imageName + " Sampler" : pin.Name;
                var name = wanted;
                for (var i = 2; !used.Add(name); i++)
                    name = $"{wanted} {i}";
                if (pin.Type == ShaderPinType.Image)
                    imageName = name;
                if (name != pin.Name)
                {
                    // The sampler pin of a renamed image pin follows it: the message is about the image pin.
                    if (pin.Type != ShaderPinType.Sampler || name != wanted)
                        warnings.Add($"uniform '{pin.UniformName}' shows as pin '{name}', '{wanted}' is taken");
                    result.Add(pin with { Name = name });
                }
                else
                    result.Add(pin);
            }
            return result.MoveToImmutable();
        }

        internal static ImmutableArray<Message> ToMessages(List<string> errors, List<string> warnings)
        {
            var messages = ImmutableArray.CreateBuilder<Message>(errors.Count + warnings.Count);
            foreach (var e in errors)
                messages.Add(new Message(MessageType.Error, e));
            foreach (var w in warnings)
                messages.Add(new Message(MessageType.Warning, w));
            return messages.MoveToImmutable();
        }
    }
}
