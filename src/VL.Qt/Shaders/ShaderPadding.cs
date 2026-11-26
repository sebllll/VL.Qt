using System;
using System.Collections.Immutable;
using System.Globalization;
using Stride.Core.Mathematics;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// The <c>@padding</c> tag of a shader file head (only the effect chain node uses it): the room the effect needs around
    /// the element, in DIP. None, a fixed rectangle or the value of a float uniform.
    /// </summary>
    internal abstract record ShaderPadding
    {
        /// <summary>No padding.</summary>
        public static readonly ShaderPadding None = new NoPadding();

        public static ShaderPadding Fixed(RectangleF dip) => new FixedPadding(dip);

        public static ShaderPadding FromUniform(string uniform, float factor) => new UniformPadding(uniform, factor);

        /// <summary>No padding.</summary>
        internal sealed record NoPadding : ShaderPadding;

        /// <summary><c>@padding &lt;left, top, right, bottom&gt;</c> or one number for all sides.</summary>
        /// <param name="Dip">x = left, y = top, width = right, height = bottom.</param>
        internal sealed record FixedPadding(RectangleF Dip) : ShaderPadding;

        /// <summary><c>@padding &lt;uniform&gt; [factor]</c>: abs(value of the float uniform) * factor on all sides.</summary>
        internal sealed record UniformPadding(string Uniform, float Factor) : ShaderPadding;

        /// <summary>
        /// Parses the value of <c>@padding</c>: a text starting with an identifier names a uniform (and an optional factor, a finite
        /// number, 0 or more; anything else is an error and gives <see cref="None"/>). Otherwise one number (all sides) or four
        /// (left, top, right, bottom), DIP, not negative; anything else is a warning and gives <see cref="None"/>.
        /// </summary>
        public static ShaderPadding Parse(string text, int line, ImmutableArray<ShaderMessage>.Builder warnings, ImmutableArray<ShaderMessage>.Builder errors)
        {
            if (text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_'))
                return ParseUniform(text, line, errors);
            return ParseFixed(text, line, warnings);
        }

        private static ShaderPadding ParseUniform(string text, int line, ImmutableArray<ShaderMessage>.Builder errors)
        {
            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var factor = 1f;
            if (parts.Length > 2 || (parts.Length == 2
                && !(float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out factor) && float.IsFinite(factor) && factor >= 0)))
            {
                errors.Add(new(line, $"@padding needs a uniform name and an optional factor (0 or more): '{text}'"));
                return None;
            }
            return FromUniform(parts[0], factor);
        }

        private static ShaderPadding ParseFixed(string text, int line, ImmutableArray<ShaderMessage>.Builder warnings)
        {
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            var values = new float[parts.Length];
            var valid = parts.Length is 1 or 4;
            for (var i = 0; valid && i < parts.Length; i++)
                valid = float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                    && float.IsFinite(values[i]) && values[i] >= 0;
            if (!valid)
            {
                warnings.Add(new(line, $"@padding needs one number or four (left, top, right, bottom), 0 or more, in DIP: '{text}'"));
                return None;
            }
            return Fixed(values.Length == 1
                ? new RectangleF(values[0], values[0], values[0], values[0])
                : new RectangleF(values[0], values[1], values[2], values[3]));
        }
    }
}
