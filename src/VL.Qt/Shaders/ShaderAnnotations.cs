using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Stride.Core.Mathematics;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// Comment annotations of a Qt GLSL file (docs/effects-and-shaders.md): only // comments. File head (comments at
    /// the start before/after #version up to the first code line): @category, @summary, @padding, @passes, @chainonly. At a uniform:
    /// @default, @min, @max, @color, @summary. @summary/@category take the rest of the line, following comment lines without @ are appended.
    /// </summary>
    /// <param name="Category">@category of the file head (absolute with "Qt.", else relative to the default category of each node kind), null = default category (Qt.Elements.Shaders).</param>
    /// <param name="Summary">@summary of the file head (node help), null if not given.</param>
    /// <param name="ChainOnly">@chainonly given: the factory creates only the effect chain node of this file, no element node.</param>
    /// <param name="Passes">@passes of the file head: 1 (default, template Single) or 2 (template TwoPass); only the effect chain node uses it.</param>
    /// <param name="Padding">@padding of the file head (only the effect chain node uses it): none, fixed DIP or from a uniform.</param>
    /// <param name="Uniforms">Annotations per uniform name (ordinal), only uniforms that have at least one tag.</param>
    /// <param name="DeclaredTypes">Declared GLSL type per uniform name, from the declaration in the source (QtNative reflects an unknown type as "other" and bool as uint).</param>
    /// <param name="Warnings">Unknown tags, tags in the wrong place, values that do not parse syntactically.</param>
    /// <param name="Errors">
    /// Head tags of the effect chain node that are wrong (@passes not 1 or 2, a broken @padding with a uniform), shown red at
    /// that node.
    /// </param>
    internal sealed record ShaderAnnotations(
        string? Category,
        string? Summary,
        bool ChainOnly,
        int Passes,
        ShaderPadding Padding,
        ImmutableDictionary<string, UniformAnnotation> Uniforms,
        ImmutableDictionary<string, string> DeclaredTypes,
        ImmutableArray<ShaderMessage> Warnings,
        ImmutableArray<ShaderMessage> Errors)
    {
        /// <summary>No annotations (e.g. a file that could not be read).</summary>
        public static readonly ShaderAnnotations Empty = new(
            null, null, false, 1, ShaderPadding.None,
            ImmutableDictionary<string, UniformAnnotation>.Empty,
            ImmutableDictionary<string, string>.Empty,
            ImmutableArray<ShaderMessage>.Empty,
            ImmutableArray<ShaderMessage>.Empty);

        /// <summary>
        /// Parses the annotations of a shader source. Cheap, no bake: the factory calls it at scan time for the NodeBrowser
        /// (Category, Summary) and again in the node's init for the pins.
        /// </summary>
        public static ShaderAnnotations Parse(string source)
        {
            var warnings = ImmutableArray.CreateBuilder<ShaderMessage>();
            var uniforms = ImmutableDictionary.CreateBuilder<string, UniformAnnotation>(StringComparer.Ordinal);
            var declaredTypes = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            var lines = source.Split('\n');

            // File head: comments (and #version/blank lines) before the first code line.
            var firstCode = 0;
            while (firstCode < lines.Length && IsHeadLine(lines[firstCode].Trim()))
                firstCode++;

            string? category = null, summary = null;
            var padding = ShaderPadding.None;
            var passes = 1;
            var chainOnly = false;
            var errors = ImmutableArray.CreateBuilder<ShaderMessage>();
            var head = new TagReader(warnings);
            for (var i = 0; i < firstCode; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith("//", StringComparison.Ordinal))
                    head.Add(t[2..], i + 1);
                else
                    head.EndTag();
            }
            foreach (var tag in head.Tags)
            {
                switch (tag.Name)
                {
                    case "category": category = tag.Value; break;
                    case "summary": summary = tag.Value; break;
                    case "padding":
                        padding = ShaderPadding.Parse(tag.Value, tag.Line, warnings, errors);
                        break;
                    case "passes":
                        if (tag.Value is "1" or "2")
                            passes = tag.Value[0] - '0';
                        else
                            errors.Add(new(tag.Line, $"@passes must be 1 or 2: '{tag.Value}'"));
                        break;
                    case "chainonly": chainOnly = true; break;
                    default: warnings.Add(new(tag.Line, $"@{tag.Name} is only valid at a uniform")); break;
                }
            }

            // Uniform declarations: comment block directly above and/or comment at the end of the line.
            var above = new List<(string Text, int Line)>();
            var inBlock = false;
            var blockPending = false;
            for (var i = firstCode; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                var trimmed = line.Trim();
                var lineNo = i + 1;

                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    above.Add((trimmed[2..], lineNo));
                    continue;
                }

                var commentStart = line.IndexOf("//", StringComparison.Ordinal);
                var code = commentStart >= 0 ? line[..commentStart] : line;
                var trailing = commentStart >= 0 ? line[(commentStart + 2)..] : null;

                if (code.Trim().Length > 0)
                {
                    var isDeclaration = false;
                    var m = declaration.Match(code);
                    if (m.Success)
                    {
                        var type = m.Groups["type"].Value;
                        var name = m.Groups["name"].Value;
                        if (inBlock)
                        {
                            isDeclaration = true;
                            declaredTypes[name] = type;
                        }
                        else if (type == "sampler2D" && uniformWord.IsMatch(code))
                            isDeclaration = true;
                        if (isDeclaration)
                            ReadUniform(name, above, trailing, lineNo, uniforms, warnings);
                    }
                    if (!isDeclaration)
                        ReadOrphans(above, trailing, lineNo, warnings);

                    if (!inBlock && uniformWord.IsMatch(code) && !code.Contains(';'))
                        blockPending = true;
                    if (blockPending && code.Contains('{'))
                    {
                        inBlock = true;
                        blockPending = false;
                    }
                    else if (inBlock && code.Contains('}'))
                        inBlock = false;
                }
                else
                    ReadOrphans(above, trailing, lineNo, warnings);
                above.Clear();
            }
            ReadOrphans(above, null, 0, warnings);

            return new ShaderAnnotations(category, summary, chainOnly, passes, padding, uniforms.ToImmutable(), declaredTypes.ToImmutable(), warnings.ToImmutable(),
                errors.ToImmutable());
        }

        private static readonly Regex declaration = new(
            @"^\s*(?:layout\s*\([^)]*\)\s*)?(?:uniform\s+)?(?:(?:highp|mediump|lowp)\s+)?(?<type>[A-Za-z_]\w*)\s+(?<name>[A-Za-z_]\w*)\s*(?:\[[^\]]*\])?\s*;",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex uniformWord = new(@"\buniform\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static bool IsHeadLine(string trimmed) =>
            trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("#version", StringComparison.Ordinal);

        private static void ReadUniform(
            string name,
            List<(string Text, int Line)> above,
            string? trailing,
            int lineNo,
            ImmutableDictionary<string, UniformAnnotation>.Builder uniforms,
            ImmutableArray<ShaderMessage>.Builder warnings)
        {
            var reader = new TagReader(warnings);
            foreach (var (text, line) in above)
                reader.Add(text, line);
            if (trailing != null)
                reader.Add(trailing, lineNo);

            string? def = null, min = null, max = null, summary = null;
            var isColor = false;
            var any = false;
            foreach (var tag in reader.Tags)
            {
                any = true;
                if (tag.Name is "default" or "min" or "max" && tag.Value.Length == 0)
                {
                    warnings.Add(new(tag.Line, $"@{tag.Name} needs a value"));
                    continue;
                }
                switch (tag.Name)
                {
                    case "default": def = tag.Value; break;
                    case "min": min = tag.Value; break;
                    case "max": max = tag.Value; break;
                    case "color": isColor = true; break;
                    case "summary": summary = tag.Value; break;
                    default: warnings.Add(new(tag.Line, $"@{tag.Name} is only valid in the file head")); break;
                }
            }
            if (any)
                uniforms[name] = new UniformAnnotation(def, min, max, isColor, summary, lineNo);
        }

        // Tags in comments that belong to no uniform and are not in the head.
        private static void ReadOrphans(
            List<(string Text, int Line)> above,
            string? trailing,
            int trailingLine,
            ImmutableArray<ShaderMessage>.Builder warnings)
        {
            if (above.Count == 0 && trailing == null)
                return;
            var reader = new TagReader(warnings);
            foreach (var (text, line) in above)
                reader.Add(text, line);
            if (trailing != null)
                reader.Add(trailing, trailingLine);
            foreach (var tag in reader.Tags)
                warnings.Add(new(tag.Line, $"@{tag.Name} is not above a uniform declaration"));
        }

        private sealed record Tag(string Name, string Value, int Line);

        /// <summary>Collects @tags of consecutive comment lines; summary/category continue on following lines without @.</summary>
        private sealed class TagReader
        {
            private static readonly Regex tagStart = new(@"^\s*@(?<name>\w+)\s*(?<rest>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
            private static readonly Regex nextTag = new(@"(?:^|\s)@\w",RegexOptions.Compiled | RegexOptions.CultureInvariant);
            private static readonly HashSet<string> known = new(StringComparer.Ordinal) { "category", "summary", "padding", "passes", "chainonly", "default", "min", "max", "color" };

            private readonly ImmutableArray<ShaderMessage>.Builder _warnings;
            private readonly List<Tag> _tags = new();
            private int _open = -1; // index of the summary/category tag that accepts continuation lines

            public TagReader(ImmutableArray<ShaderMessage>.Builder warnings) => _warnings = warnings;

            public IReadOnlyList<Tag> Tags => _tags;

            public void EndTag() => _open = -1;

            public void Add(string comment, int line)
            {
                var m = tagStart.Match(comment);
                if (!m.Success)
                {
                    var plain = comment.Trim();
                    if (_open >= 0 && plain.Length > 0)
                    {
                        var t = _tags[_open];
                        _tags[_open] = t with { Value = t.Value.Length > 0 ? t.Value + " " + plain : plain };
                    }
                    return;
                }

                _open = -1;
                while (m.Success)
                {
                    var name = m.Groups["name"].Value;
                    var rest = m.Groups["rest"].Value;
                    var value = rest;
                    string? remainder = null;
                    if (name is not ("summary" or "category"))
                    {
                        var n = nextTag.Match(rest);
                        if (n.Success)
                        {
                            value = rest[..n.Index];
                            remainder = rest[n.Index..];
                        }
                    }
                    if (!known.Contains(name))
                        _warnings.Add(new(line, $"unknown tag @{name}"));
                    else
                    {
                        _tags.Add(new Tag(name, value.Trim(), line));
                        if (name is "summary" or "category")
                            _open = _tags.Count - 1;
                    }
                    m = remainder != null ? tagStart.Match(remainder) : Match.Empty;
                }
            }
        }
    }
}
