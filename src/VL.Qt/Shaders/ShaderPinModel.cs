using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Stride.Core.Mathematics;
using VL.Qt.NodeFactories;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// Pins of a generated shader node from the reflection of .frag (and optional .vert) plus their annotations.
    /// Holds all pin rules of the shader node factory (docs/effects-and-shaders.md): filter qt_Matrix/qt_Opacity, sampler2D source →
    /// Input, merge vert/frag (same name once), type mapping incl. @color and bool regex, skipped types and arrays as
    /// warnings, defaults. Order: further samplers (Image) first, then uniforms, each in source order.
    /// </summary>
    /// <param name="HasInput">The fragment (or vertex) shader declares sampler2D source → node has an Input pin.</param>
    /// <param name="Pins">Uniform pins in pin order, without Input and without the fixed Shader pins.</param>
    /// <param name="Warnings">Skipped uniforms, bad annotation values, plus the warnings of the annotations (orange).</param>
    /// <param name="Error">Compile error of frag or vert (red), empty if none; Pins is then empty.</param>
    internal sealed record ShaderPinModel(
        bool HasInput,
        ImmutableArray<ShaderPin> Pins,
        ImmutableArray<ShaderMessage> Warnings,
        string Error)
    {
        /// <summary>Builds the pin model. vert/vertA null if there is no .vert.</summary>
        public static ShaderPinModel Build(
            ShaderReflection frag,
            ShaderReflection? vert,
            ShaderAnnotations fragA,
            ShaderAnnotations? vertA)
        {
            var warnings = ImmutableArray.CreateBuilder<ShaderMessage>();
            warnings.AddRange(fragA.Warnings);
            if (vertA != null)
                warnings.AddRange(vertA.Warnings);

            if (!frag.Ok || vert is { Ok: false })
            {
                var errors = new List<string>();
                if (!frag.Ok)
                    errors.Add(frag.Error);
                if (vert is { Ok: false })
                    errors.Add(vert.Error);
                return new ShaderPinModel(false, ImmutableArray<ShaderPin>.Empty, warnings.ToImmutable(), string.Join('\n', errors));
            }

            var declared = fragA.DeclaredTypes;
            if (vertA != null)
                declared = declared.SetItems(vertA.DeclaredTypes);

            var hasInput = false;
            var pins = ImmutableArray.CreateBuilder<ShaderPin>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Samplers: source → Input, others → Image pins.
            var stages = vert is null ? new[] { frag } : new[] { frag, vert };
            foreach (var reflection in stages)
            {
                foreach (var sampler in reflection.Samplers)
                {
                    if (sampler.Name.StartsWith("qt_", StringComparison.Ordinal))
                        continue;
                    if (sampler.Name == ReservedUniforms.InputSampler)
                    {
                        hasInput = true;
                        continue;
                    }
                    if (!seen.Add(sampler.Name))
                        continue;
                    var imageAnnotation = FindAnnotation(sampler.Name, fragA, vertA);
                    pins.Add(new ShaderPin(PinNames.FromIdentifier(sampler.Name, splitDigits: true), sampler.Name, ShaderPinType.Image, null, null, null, imageAnnotation?.Summary, imageAnnotation?.IsOptional ?? false));
                    pins.Add(new ShaderPin(PinNames.FromIdentifier(sampler.Name, splitDigits: true) + " Sampler", sampler.Name, ShaderPinType.Sampler, null, null, null,
                        $"How the shader reads '{sampler.Name}' (filter, wrap, mipmaps), from the Sampler node. Unconnected: as the image is."));
                }
            }

            // Uniforms in block order: frag first, then those only in vert.
            foreach (var reflection in stages)
            {
                foreach (var uniform in reflection.Uniforms)
                {
                    if (uniform.Name.StartsWith("qt_", StringComparison.Ordinal) || ReservedUniforms.IsItemReserved(uniform.Name))
                        continue;
                    if (!seen.Add(uniform.Name))
                        continue;
                    var annotation = FindAnnotation(uniform.Name, fragA, vertA);
                    var line = annotation?.Line ?? 0;

                    if (uniform.ArrayLength > 0)
                    {
                        warnings.Add(new(line, $"uniform '{uniform.Name}' is an array, not supported (skipped)"));
                        continue;
                    }
                    var type = MapType(uniform.Type, uniform.Name, annotation, declared);
                    if (type is null)
                    {
                        warnings.Add(new(line, $"uniform '{uniform.Name}': type '{DeclaredType(uniform, declared)}' is not supported (skipped); use float, vec2, vec3, vec4, int, bool or mat4"));
                        continue;
                    }

                    if (annotation is { IsColor: true } && type != ShaderPinType.Color)
                    {
                        warnings.Add(new(line, $"@color on uniform '{uniform.Name}' of type '{uniform.Type}' is ignored (needs vec4)"));
                    }
                    pins.Add(BuildPin(uniform, type.Value, annotation, warnings));
                }
            }

            return new ShaderPinModel(hasInput, pins.ToImmutable(), warnings.ToImmutable(), string.Empty);
        }

        private static UniformAnnotation? FindAnnotation(string name, ShaderAnnotations fragA, ShaderAnnotations? vertA)
        {
            if (fragA.Uniforms.TryGetValue(name, out var a))
                return a;
            if (vertA != null && vertA.Uniforms.TryGetValue(name, out a))
                return a;
            return null;
        }

        // QtNative reports every type it does not know as "other"; the declaration in the source says which one it is.
        private static string DeclaredType(ReflectedUniform uniform, ImmutableDictionary<string, string> declared) =>
            uniform.Type == "other" && declared.TryGetValue(uniform.Name, out var type) ? type : uniform.Type;

        private static ShaderPinType? MapType(string glslType, string name, UniformAnnotation? annotation, ImmutableDictionary<string, string> declared) => glslType switch
        {
            "float" => ShaderPinType.Float,
            "vec2" => ShaderPinType.Vector2,
            "vec3" => ShaderPinType.Vector3,
            "vec4" => annotation is { IsColor: true } ? ShaderPinType.Color : ShaderPinType.Vector4,
            "int" => ShaderPinType.Int,
            "bool" => ShaderPinType.Bool,
            "uint" when declared.TryGetValue(name, out var declaredType) && declaredType == "bool" => ShaderPinType.Bool,
            "mat4" => ShaderPinType.Matrix,
            _ => null,
        };

        private static ShaderPin BuildPin(ReflectedUniform uniform, ShaderPinType type, UniformAnnotation? annotation, ImmutableArray<ShaderMessage>.Builder warnings)
        {
            var line = annotation?.Line ?? 0;
            var name = uniform.Name;

            object? Read(string tag, string? text, bool allowSingle)
            {
                if (text is null)
                    return null;
                if (TryParseValue(type, text, allowSingle, out var value))
                    return value;
                warnings.Add(new(line, $"@{tag} '{text}' does not fit uniform '{name}' ({uniform.Type})"));
                return null;
            }

            var def = Read("default", annotation?.Default, false) ?? ZeroValue(type);
            if (annotation?.Default is null && type == ShaderPinType.Color)
                def = Color4.White;
            var min = Read("min", annotation?.Min, true);
            var max = Read("max", annotation?.Max, true);
            return new ShaderPin(PinNames.FromIdentifier(name, splitDigits: true), name, type, def, min, max, annotation?.Summary, annotation?.IsOptional ?? false);
        }

        private static object? ZeroValue(ShaderPinType type) => type switch
        {
            ShaderPinType.Float => 0f,
            ShaderPinType.Vector2 => Vector2.Zero,
            ShaderPinType.Vector3 => Vector3.Zero,
            ShaderPinType.Vector4 => Vector4.Zero,
            ShaderPinType.Color => new Color4(0f, 0f, 0f, 0f),
            ShaderPinType.Int => 0,
            ShaderPinType.Bool => false,
            ShaderPinType.Matrix => Matrix.Identity, // VL default of Matrix (CreateDefault), not the GLSL zero
            _ => null,
        };

        private static bool TryParseValue(ShaderPinType type, string text, bool allowSingle, out object? value)
        {
            value = null;
            switch (type)
            {
                case ShaderPinType.Bool:
                    if (allowSingle) // min/max make no sense for bool
                        return false;
                    if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
                    if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
                    return false;
                case ShaderPinType.Int:
                    if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) { value = i; return true; }
                    return false;
                case ShaderPinType.Matrix:
                case ShaderPinType.Image:
                case ShaderPinType.Sampler:
                    return false;
            }

            var count = type switch
            {
                ShaderPinType.Float => 1,
                ShaderPinType.Vector2 => 2,
                ShaderPinType.Vector3 => 3,
                _ => 4,
            };
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            var c = new float[count];
            if (parts.Length == count)
            {
                for (var k = 0; k < count; k++)
                    if (!TryParseFloat(parts[k], out c[k]))
                        return false;
            }
            else if (parts.Length == 1 && allowSingle)
            {
                if (!TryParseFloat(parts[0], out var single))
                    return false;
                Array.Fill(c, single);
            }
            else
                return false;

            value = type switch
            {
                ShaderPinType.Float => c[0],
                ShaderPinType.Vector2 => new Vector2(c[0], c[1]),
                ShaderPinType.Vector3 => new Vector3(c[0], c[1], c[2]),
                ShaderPinType.Vector4 => new Vector4(c[0], c[1], c[2], c[3]),
                _ => new Color4(c[0], c[1], c[2], c[3]),
            };
            return true;
        }

        private static bool TryParseFloat(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
    }
}
