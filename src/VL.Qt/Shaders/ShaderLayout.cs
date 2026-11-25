using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using VL.Core.Diagnostics;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// Everything the init of a generated shader node needs, built once per node description: the file sources, the pin model
    /// (with pin names made unique against the fixed pins) and the messages shown at the node. Errors (red): file not readable,
    /// compile error (then only the fixed pins), duplicate file name. Warnings (orange): the pin model's warnings.
    /// </summary>
    /// <param name="HasInput">Node has an Input pin (sampler2D source).</param>
    /// <param name="Pins">Image and uniform pins in pin order, names unique.</param>
    /// <param name="Messages">Messages of the node description (errors first).</param>
    /// <param name="Summary">@summary of the file head, null if not given.</param>
    /// <param name="FragmentSource">Content of the .frag (empty if not readable).</param>
    /// <param name="VertexSource">Content of the .vert, null without .vert.</param>
    internal sealed record ShaderLayout(
        bool HasInput,
        ImmutableArray<ShaderPin> Pins,
        ImmutableArray<Message> Messages,
        string? Summary,
        string FragmentSource,
        string? VertexSource)
    {
        /// <summary>Pin names a uniform pin must not take (the Input pin, the Apply pin of the effect chain node and the fixed Shader pins).</summary>
        public static readonly ImmutableHashSet<string> ReservedPinNames = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "Input", "Blending", "Mesh Resolution", "Size", "Auto Size", "Transform", "Anchor", "Opacity", "Clip", "Debug",
            "Interaction", "Effect", "Enabled", "Apply", "Output", "Measured Size", "Scene Bounds", "Error");

        /// <summary>The layout of the shader node of <paramref name="file"/>, made from its loaded <paramref name="source"/>.</summary>
        public static ShaderLayout From(ShaderSource source, ShaderFile file)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var pins = Common(source, file, errors, warnings, out var hasInput);
            return new ShaderLayout(hasInput, pins, ToMessages(errors, warnings), source.FragmentAnnotations.Summary, source.FragmentSource, source.VertexSource);
        }

        /// <summary>
        /// The errors, warnings and unique pins that the shader node and the effect chain node of a file have in common (the
        /// effect chain node starts from them).
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
            return UniquePinNames(source.PinModel.Pins, hasInput, warnings);
        }

        /// <summary>A uniform whose pin name is taken (by a fixed pin, Input or another uniform) gets a number: "Size 2".</summary>
        internal static ImmutableArray<ShaderPin> UniquePinNames(ImmutableArray<ShaderPin> pins, bool hasInput, List<string> warnings)
        {
            var used = new HashSet<string>(ReservedPinNames, StringComparer.OrdinalIgnoreCase);
            if (!hasInput)
                used.Remove("Input");
            var result = ImmutableArray.CreateBuilder<ShaderPin>(pins.Length);
            foreach (var pin in pins)
            {
                var name = pin.Name;
                for (var i = 2; !used.Add(name); i++)
                    name = $"{pin.Name} {i}";
                if (name != pin.Name)
                {
                    warnings.Add($"uniform '{pin.UniformName}' shows as pin '{name}', '{pin.Name}' is taken");
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
