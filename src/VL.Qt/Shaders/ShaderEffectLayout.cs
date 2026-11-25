using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using VL.Core.Diagnostics;
using VL.Qt.Wire;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// Everything the init of a generated shader effect node (the effect chain node of a filter file) needs: the layout of the
    /// shader node without the uniforms QtNative sets itself (<see cref="ReservedUniforms.ChainAutomatic"/>: texelSize, dpr,
    /// weight, ...), plus an error for each uniform of a name the chain reserves (<see cref="ReservedUniforms.ChainReserved"/>:
    /// base, glow, shadow; no pin), messages for a file that is no filter (any more) and for a .vert, which an effect does not
    /// use. Errors too: a wrong @passes or @padding tag, and an @padding uniform the shader does not have or that is not a
    /// float (then <see cref="Padding"/> is <see cref="ShaderPadding.None"/>).
    /// </summary>
    /// <param name="Pins">Image and uniform pins in pin order, names unique.</param>
    /// <param name="Messages">Messages of the node description (errors first).</param>
    /// <param name="Summary">@summary of the file head, null if not given.</param>
    /// <param name="Remarks">@remarks of the file head, null if not given.</param>
    /// <param name="FragmentSource">Content of the .frag (empty if not readable).</param>
    /// <param name="Template"><see cref="Protocol.EffectTemplate"/> of @passes: Single for 1, TwoPass for 2.</param>
    /// <param name="Padding">@padding of the file head; a uniform only if it is a valid float pin of this layout.</param>
    internal sealed record ShaderEffectLayout(
        ImmutableArray<ShaderPin> Pins,
        ImmutableArray<Message> Messages,
        string? Summary,
        string? Remarks,
        string FragmentSource,
        ushort Template,
        ShaderPadding Padding)
    {
        /// <summary>Pin names a uniform pin must not take: the Input pin (the chain), the Apply pin and the Output.</summary>
        public static readonly ImmutableHashSet<string> ReservedPinNames = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "Input", "Apply", "Output");

        /// <summary>The layout for the effect chain node of <paramref name="file"/>, made from its loaded <paramref name="source"/>.</summary>
        public static ShaderEffectLayout From(ShaderSource source, ShaderFile file)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var allPins = ShaderLayout.Common(source, file, errors, warnings, out var hasInput);
            var compiled = errors.Count == 0;

            var wanted = ImmutableArray.CreateBuilder<ShaderPin>(allPins.Length);
            foreach (var pin in allPins)
            {
                if (ReservedUniforms.ChainAutomatic.Contains(pin.UniformName))
                    continue;
                if (ReservedUniforms.IsChainReserved(pin.UniformName))
                {
                    var text = $"uniform '{pin.UniformName}' is reserved in an effect chain (set by VL.Qt), no pin";
                    if (!errors.Contains(text))
                        errors.Add(text);
                }
                else
                    wanted.Add(pin);
            }
            var pins = ShaderLayout.UniquePinNames(wanted.ToImmutable(), ReservedPinNames, warnings);
            if (source.VertexSource != null)
                warnings.Add("the vertex shader (.vert) is not used by an effect chain link");

            var annotations = source.FragmentAnnotations;
            foreach (var e in annotations.Errors)
                errors.Add(e.ToString());
            ShaderLayout.AddKeptError(source, errors, warnings);
            if (!hasInput && source.FragmentSource.Length > 0 && compiled)
                errors.Add("the shader has no 'uniform sampler2D source' (any more), it is not a filter and cannot be a link of the effect chain");

            var padding = annotations.Padding;
            if (padding is ShaderPadding.UniformPadding uniformPadding)
            {
                // The pins the node gets (not the uniforms QtNative sets itself); a shader that does not compile has none and already shows its error.
                if (!compiled)
                    padding = ShaderPadding.None;
                else if (pins.FirstOrDefault(p => p.Type != ShaderPinType.Sampler && p.UniformName == uniformPadding.Uniform) is not { } pin)
                {
                    errors.Add($"@padding names the uniform '{uniformPadding.Uniform}', which the shader does not have as an input");
                    padding = ShaderPadding.None;
                }
                else if (pin.Type != ShaderPinType.Float)
                {
                    errors.Add($"@padding names the uniform '{uniformPadding.Uniform}', which is not a float");
                    padding = ShaderPadding.None;
                }
            }

            var template = annotations.Passes == 2 ? Protocol.EffectTemplate.TwoPass : Protocol.EffectTemplate.Single;
            return new ShaderEffectLayout(pins, ShaderLayout.ToMessages(errors, warnings), annotations.Summary, annotations.Remarks, source.FragmentSource, template, padding);
        }
    }
}
