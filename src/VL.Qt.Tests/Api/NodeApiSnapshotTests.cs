using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Qt.Elements;
using VL.Qt.Qml;
using VL.Qt.Shaders;

namespace VL.Qt.Tests.Api
{
    /// <summary>
    /// Node-API snapshot (TEST-01): every public type and member of VL.Qt, VL.Qt.Stride and VL.Qt.Audio with the attributes
    /// vvvv reads, plus the pins of the nodes the shader and QML node factories generate, compared with Api/node-api.txt.
    /// The assemblies are read as metadata (System.Reflection.Metadata), so nothing is loaded or resolved. One fact per
    /// line, sorted ordinally: a git diff of the file reads as a rename list. VLQT_UPDATE_SNAPSHOT=1 rewrites the file.
    /// </summary>
    [TestFixture]
    public sealed class NodeApiSnapshotTests
    {
        private const string updateVariable = "VLQT_UPDATE_SNAPSHOT";
        private static readonly string[] s_windowsOnlyAssemblies = { "VL.Qt.Stride", "VL.Qt.Audio" };

        [Test]
        public void NodeApi_MatchesSnapshot()
        {
            var snapshotPath = SnapshotPath();
            var actual = Generate(out var missing);
            var update = Environment.GetEnvironmentVariable(updateVariable) == "1";

            if (update)
            {
                if (missing.Count > 0)
                    Assert.Fail($"{updateVariable}=1 needs all three assemblies; missing: {string.Join(", ", missing)} (Windows only).");
                File.WriteAllText(snapshotPath, string.Join("\n", actual) + "\n", new UTF8Encoding(false));
                TestContext.Out.WriteLine($"Rewrote {snapshotPath} ({actual.Count} lines).");
                return;
            }

            Assert.That(File.Exists(snapshotPath), $"Snapshot {snapshotPath} not found; create it with {updateVariable}=1.");
            var expected = File.ReadAllText(snapshotPath).Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                // Without the Windows-only assemblies (Linux) their lines are not compared.
                .Where(l => !missing.Any(a => l.StartsWith(a + " | ", StringComparison.Ordinal)))
                .ToList();

            var diff = Diff(expected, actual);
            if (diff.Count > 0)
            {
                Assert.Fail($"The node API differs from {snapshotPath} ({diff.Count} lines, - snapshot, + now). If the change is intended, " +
                    $"run the test with {updateVariable}=1 and commit the file.\n" + string.Join("\n", diff.Take(400)) +
                    (diff.Count > 400 ? $"\n... {diff.Count - 400} more" : ""));
            }
        }

        [Test]
        public void NodeApi_IsStable()
        {
            var first = Generate(out _);
            var second = Generate(out _);
            Assert.That(second, Is.EqualTo(first));
        }

        [TestCase("fillColor", "Fill Color")]
        [TestCase("vSync", "V Sync")]
        [TestCase("input2", "Input2")]
        [TestCase("rgbValue", "Rgb Value")]
        [TestCase("RGBValue", "RGB Value")]
        [TestCase("x", "X")]
        public void PinName_FollowsPinCatalogRule(string csharp, string pin)
        {
            Assert.That(TitleCase(csharp), Is.EqualTo(pin));
        }

        // --- generation ---

        internal static List<string> Generate(out List<string> missing)
        {
            missing = new List<string>();
            var lines = new List<string>();
            var dir = AppContext.BaseDirectory;
            lines.AddRange(new AssemblyApi(typeof(Element).Assembly.Location).Lines());
            foreach (var name in s_windowsOnlyAssemblies)
            {
                var path = Path.Combine(dir, "node-api", name + ".dll");
                if (File.Exists(path))
                    lines.AddRange(new AssemblyApi(path).Lines());
                else if (OperatingSystem.IsWindows())
                    throw new FileNotFoundException($"{path} is missing: the test project builds it on Windows (see VL.Qt.Tests.csproj).", path);
                else
                    missing.Add(name);
            }
            lines.AddRange(ShaderFactoryLines());
            lines.AddRange(QmlFactoryLines());
            var sorted = lines.Distinct().ToList();
            sorted.Sort(StringComparer.Ordinal);
            return sorted;
        }

        private static string SnapshotPath()
        {
            var path = typeof(NodeApiSnapshotTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == "NodeApiSnapshot").Value;
            return Path.GetFullPath(path!);
        }

        /// <summary>Line diff (longest common subsequence) of two sorted lists: "- line" only in the snapshot, "+ line" only now.</summary>
        private static List<string> Diff(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
        {
            // Both lists are sorted ordinally: a merge walk gives the exact set difference in order.
            var result = new List<string>();
            int i = 0, j = 0;
            while (i < expected.Count || j < actual.Count)
            {
                var c = i == expected.Count ? 1 : j == actual.Count ? -1 : string.CompareOrdinal(expected[i], actual[j]);
                if (c == 0)
                {
                    i++;
                    j++;
                }
                else if (c < 0)
                    result.Add("- " + expected[i++]);
                else
                    result.Add("+ " + actual[j++]);
            }
            return result;
        }

        /// <summary>C# name to vvvv pin name, the rule of tools/vl_pin_catalog.py (title_case): `fillColor` → `Fill Color`.</summary>
        internal static string TitleCase(string name)
        {
            if (name.Length == 0)
                return name;
            name = char.ToUpperInvariant(name[0]) + name.Substring(1);
            return Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        }

        // --- generated nodes ---

        private const string testShader = @"// @category Qt.Test
// @summary Snapshot shader.
#version 440
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    float amount; // @default 0.5 @max 1
    vec2 offset;
    vec4 tint; // @color
};
layout(binding = 1) uniform sampler2D source;
layout(binding = 2) uniform sampler2D mask;
void main() { fragColor = texture(source, qt_TexCoord0) * texture(mask, qt_TexCoord0) * tint * amount * qt_Opacity; }
";

        // What QtNative's reflection reports for testShader (the unit tests run without QtNative).
        private const string testShaderReflection = "ok\nuniform qt_Matrix mat4 0\nuniform qt_Opacity float 0\nuniform amount float 0\nuniform offset vec2 0\nuniform tint vec4 0\nsampler source 1\nsampler mask 2\n";

        private const string testQml = @"// @summary Snapshot component.
import QtQuick
Item {
    property real level: 0.5
    property color accent: ""red""
    property string label: ""x""
    property bool active
    readonly property int count: 3
    property real derived: level * 2
    signal clicked()
    signal moved(real value)
}
";

        private static IEnumerable<string> ShaderFactoryLines()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Snapshot_Variant.frag"), testShader);
                File.WriteAllText(Path.Combine(dir, "Plain.frag"), "#version 440\nvoid main() {}\n");
                var lines = new List<string> { $"factory Shader | default category {ShaderNodeFactory.DefaultCategory}" };
                foreach (var file in ShaderNodeFactory.Scan(dir).OrderBy(f => f.FullName, StringComparer.Ordinal))
                {
                    var layout = ShaderLayout.From(ShaderSource.Load(file, (_, _) => ShaderReflection.Parse(file.Name == "Plain" ? "ok\n" : testShaderReflection)), file);
                    lines.AddRange(GeneratedNodeLines("Shader", file.FullName, file.Category, ShaderDescription.Inputs(layout), ShaderDescription.Outputs));
                }
                return lines;
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        private static IEnumerable<string> QmlFactoryLines()
        {
            var dir = CreateTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Snapshot_Variant.qml"), testQml);
                var lines = new List<string> { $"factory Qml | default category {QmlNodeFactory.DefaultCategory}" };
                foreach (var file in QmlNodeFactory.Scan(dir).OrderBy(f => f.FullName, StringComparer.Ordinal))
                {
                    var layout = QmlNodeLayout.Load(file, NullLogger.Instance);
                    lines.AddRange(GeneratedNodeLines("Qml", file.FullName, file.Category, layout.InputDescriptions(), layout.OutputDescriptions()));
                }
                return lines;
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "vlqt-nodeapi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static IEnumerable<string> GeneratedNodeLines(string factory, string name, string category, IEnumerable<IVLPinDescription> inputs, IEnumerable<IVLPinDescription> outputs)
        {
            var prefix = $"factory {factory} | node {name}";
            yield return $"{prefix} | category {category}";
            var order = new List<string>();
            foreach (var (pin, direction) in inputs.Select(p => (p, "in")).Concat(outputs.Select(p => (p, "out"))))
            {
                order.Add(pin.Name);
                var visible = pin is IVLPinDescriptionWithVisibility v && !v.IsVisible ? "optional" : "visible";
                var value = pin.DefaultValue is null ? "" : " = " + FormatValue(pin.DefaultValue);
                yield return $"{prefix} | pin {pin.Name} | {direction} {TypeName(pin.Type)}{value} | {visible}";
            }
            yield return $"{prefix} | order {string.Join(", ", order)}";
        }

        private static string TypeName(Type type)
        {
            if (type.IsGenericType)
            {
                var name = type.GetGenericTypeDefinition().FullName!;
                name = name.Substring(0, name.IndexOf('`'));
                return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
            }
            return Keyword(type.FullName!.Replace('+', '.'));
        }

        private static string FormatValue(object value) => value switch
        {
            string s => Quote(s),
            bool b => b ? "true" : "false",
            float f => f.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            IFormattable when value.GetType().IsPrimitive => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
            Enum e => e.ToString(),
            Vector2 v => Numbers(v.X, v.Y),
            Vector3 v => Numbers(v.X, v.Y, v.Z),
            Vector4 v => Numbers(v.X, v.Y, v.Z, v.W),
            Int2 v => Numbers(v.X, v.Y),
            Color4 c => Numbers(c.R, c.G, c.B, c.A),
            RectangleF r => Numbers(r.X, r.Y, r.Width, r.Height),
            Matrix m => Numbers(m.ToArray()),
            _ => "<" + TypeName(value.GetType()) + ">",
        };

        private static string Numbers(params float[] values) => string.Join(", ", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

        private static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                sb.Append(c switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ when c < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                    _ => c.ToString(),
                });
            }
            return sb.Append('"').ToString();
        }

        private static readonly Dictionary<string, string> s_keywords = new()
        {
            ["System.Void"] = "void", ["System.Boolean"] = "bool", ["System.Char"] = "char", ["System.SByte"] = "sbyte",
            ["System.Byte"] = "byte", ["System.Int16"] = "short", ["System.UInt16"] = "ushort", ["System.Int32"] = "int",
            ["System.UInt32"] = "uint", ["System.Int64"] = "long", ["System.UInt64"] = "ulong", ["System.Single"] = "float",
            ["System.Double"] = "double", ["System.String"] = "string", ["System.Object"] = "object",
            ["System.IntPtr"] = "nint", ["System.UIntPtr"] = "nuint", ["System.Decimal"] = "decimal",
        };

        private static string Keyword(string fullName) => s_keywords.TryGetValue(fullName, out var k) ? k : fullName;

        // --- metadata reading ---

        /// <summary>Enum facts of one type: underlying type and member names by value.</summary>
        private sealed record EnumInfo(PrimitiveTypeCode Underlying, IReadOnlyDictionary<long, string> Names, bool Flags);

        /// <summary>The public surface of one assembly, read from its metadata.</summary>
        private sealed class AssemblyApi : ICustomAttributeTypeProvider<string>, ISignatureTypeProvider<string, AssemblyApi.GenericContext>
        {
            internal readonly record struct GenericContext(ImmutableArray<string> TypeParameters, ImmutableArray<string> MethodParameters);

            // Attributes vvvv reads, by full name (other VL.* attributes are kept too, see IsRelevant).
            private static readonly HashSet<string> s_systemAttributes = new(StringComparer.Ordinal)
            {
                "System.ComponentModel.DefaultValueAttribute", "System.ComponentModel.BrowsableAttribute", "System.FlagsAttribute",
                "System.ObsoleteAttribute", "System.ParamArrayAttribute",
            };

            private readonly MetadataReader _reader;
            private readonly string _assembly;
            private readonly Dictionary<string, EnumInfo> _localEnums = new(StringComparer.Ordinal);
            private readonly Dictionary<string, EnumInfo?> _externalEnums = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _externalEnumAssemblies = new(StringComparer.Ordinal);
            private readonly List<string> _lines = new();

            internal AssemblyApi(string path)
            {
                var bytes = File.ReadAllBytes(path);
                var pe = new PEReader(ImmutableArray.Create(bytes));
                _reader = pe.GetMetadataReader();
                _assembly = _reader.GetString(_reader.GetAssemblyDefinition().Name);
                foreach (var handle in _reader.TypeDefinitions)
                {
                    var info = ReadEnum(_reader.GetTypeDefinition(handle));
                    if (info != null)
                        _localEnums[FullName(handle)] = info;
                }
                foreach (var handle in _reader.TypeReferences)
                {
                    var reference = _reader.GetTypeReference(handle);
                    if (reference.ResolutionScope.Kind == HandleKind.AssemblyReference)
                    {
                        var asm = _reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope);
                        _externalEnumAssemblies[FullName(handle)] = _reader.GetString(asm.Name);
                    }
                }
            }

            internal List<string> Lines()
            {
                _lines.Clear();
                foreach (var attribute in Attributes(_reader.GetAssemblyDefinition().GetCustomAttributes()))
                    Add("assembly", $"attr {attribute}");
                foreach (var handle in _reader.TypeDefinitions)
                {
                    if (IsPublic(handle))
                        AddType(handle);
                }
                return _lines;
            }

            private void Add(string owner, string fact) => _lines.Add($"{_assembly} | {owner} | {fact}");

            private bool IsPublic(TypeDefinitionHandle handle)
            {
                var type = _reader.GetTypeDefinition(handle);
                var visibility = type.Attributes & TypeAttributes.VisibilityMask;
                if (visibility == TypeAttributes.Public)
                    return true;
                return visibility == TypeAttributes.NestedPublic && IsPublic(type.GetDeclaringType());
            }

            private void AddType(TypeDefinitionHandle handle)
            {
                var type = _reader.GetTypeDefinition(handle);
                var name = FullName(handle);
                var context = new GenericContext(type.GetGenericParameters().Select(p => _reader.GetString(_reader.GetGenericParameter(p).Name)).ToImmutableArray(), ImmutableArray<string>.Empty);
                if (context.TypeParameters.Length > 0)
                    name += "<" + string.Join(", ", context.TypeParameters) + ">";

                var baseType = type.BaseType.IsNil ? null : TypeName(type.BaseType, context);
                string kind;
                if ((type.Attributes & TypeAttributes.Interface) != 0)
                    kind = "interface";
                else if (baseType == "System.Enum")
                    kind = "enum";
                else if (baseType == "System.ValueType")
                    kind = "struct";
                else if (baseType == "System.MulticastDelegate")
                    kind = "delegate";
                else if ((type.Attributes & (TypeAttributes.Abstract | TypeAttributes.Sealed)) == (TypeAttributes.Abstract | TypeAttributes.Sealed))
                    kind = "static class";
                else if ((type.Attributes & TypeAttributes.Abstract) != 0)
                    kind = "abstract class";
                else if ((type.Attributes & TypeAttributes.Sealed) != 0)
                    kind = "sealed class";
                else
                    kind = "class";
                var extends = kind is "class" or "abstract class" or "sealed class" && baseType != "object" ? " : " + baseType : "";
                Add(name, $"type {kind}{extends}");

                foreach (var i in type.GetInterfaceImplementations())
                    Add(name, $"implements {TypeName(_reader.GetInterfaceImplementation(i).Interface, context)}");
                foreach (var attribute in Attributes(type.GetCustomAttributes()))
                    Add(name, $"attr {attribute}");

                if (kind == "enum")
                {
                    AddEnumMembers(name, type);
                    return;
                }
                if (kind == "delegate")
                {
                    var invoke = type.GetMethods().Select(_reader.GetMethodDefinition).First(m => _reader.GetString(m.Name) == "Invoke");
                    AddMethod(name, "delegate Invoke", invoke, context);
                    return;
                }

                var accessors = new HashSet<MethodDefinitionHandle>();
                foreach (var p in type.GetProperties())
                    AddProperty(name, _reader.GetPropertyDefinition(p), context, accessors);
                foreach (var e in type.GetEvents())
                    AddEvent(name, _reader.GetEventDefinition(e), context, accessors);
                foreach (var f in type.GetFields())
                    AddField(name, _reader.GetFieldDefinition(f), context);

                var methods = type.GetMethods()
                    .Where(m => !accessors.Contains(m))
                    .Select(m => (Handle: m, Method: _reader.GetMethodDefinition(m)))
                    .Where(m => (m.Method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public)
                    .Where(m => _reader.GetString(m.Method.Name) is var n && n != ".cctor" && !n.StartsWith('<'))
                    .ToList();
                var overloaded = methods.GroupBy(m => _reader.GetString(m.Method.Name)).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
                foreach (var (_, method) in methods)
                {
                    var methodName = _reader.GetString(method.Name);
                    var methodContext = context with
                    {
                        MethodParameters = method.GetGenericParameters().Select(p => _reader.GetString(_reader.GetGenericParameter(p).Name)).ToImmutableArray(),
                    };
                    var key = methodName == ".ctor" ? "ctor" : "method " + methodName;
                    if (methodContext.MethodParameters.Length > 0)
                        key += "<" + string.Join(", ", methodContext.MethodParameters) + ">";
                    if (overloaded.Contains(methodName))
                    {
                        var signature = method.DecodeSignature(this, methodContext);
                        key += "(" + string.Join(", ", signature.ParameterTypes) + ")";
                    }
                    AddMethod(name, key, method, methodContext);
                }
            }

            private void AddEnumMembers(string owner, TypeDefinition type)
            {
                foreach (var f in type.GetFields())
                {
                    var field = _reader.GetFieldDefinition(f);
                    if ((field.Attributes & FieldAttributes.Static) == 0)
                        continue;
                    var value = field.GetDefaultValue().IsNil ? "?" : FormatConstant(_reader.GetConstant(field.GetDefaultValue()), null);
                    Add(owner, $"member {_reader.GetString(field.Name)} = {value}");
                    foreach (var attribute in Attributes(field.GetCustomAttributes()))
                        Add(owner, $"member {_reader.GetString(field.Name)} | attr {attribute}");
                }
            }

            private void AddProperty(string owner, PropertyDefinition property, GenericContext context, HashSet<MethodDefinitionHandle> accessors)
            {
                var a = property.GetAccessors();
                if (!a.Getter.IsNil)
                    accessors.Add(a.Getter);
                if (!a.Setter.IsNil)
                    accessors.Add(a.Setter);
                var parts = new List<string>();
                MethodDefinition? anyPublic = null;
                foreach (var (handle, word) in new[] { (a.Getter, "get"), (a.Setter, "set") })
                {
                    if (handle.IsNil)
                        continue;
                    var accessor = _reader.GetMethodDefinition(handle);
                    if ((accessor.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
                        continue;
                    anyPublic ??= accessor;
                    // An init-only setter has the modreq IsExternalInit on its return type.
                    parts.Add(word == "set" && IsInitOnly(accessor) ? "init" : word);
                }
                if (anyPublic is not { } first)
                    return;
                var signature = property.DecodeSignature(this, context);
                var name = _reader.GetString(property.Name);
                var isStatic = (first.Attributes & MethodAttributes.Static) != 0 ? "static " : "";
                var parameters = signature.ParameterTypes.Length > 0 ? "[" + string.Join(", ", signature.ParameterTypes) + "]" : "";
                var modifiers = Modifiers(first);
                Add(owner, $"property {name}{parameters} | {isStatic}{modifiers}{signature.ReturnType} {{ {string.Join("; ", parts)} }}");
                foreach (var attribute in Attributes(property.GetCustomAttributes()))
                    Add(owner, $"property {name} | attr {attribute}");
            }

            private bool IsInitOnly(MethodDefinition setter)
            {
                var blob = _reader.GetBlobReader(setter.Signature);
                blob.ReadSignatureHeader();
                blob.ReadCompressedInteger();
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.RequiredModifier)
                    return false;
                var handle = blob.ReadTypeHandle();
                return handle.Kind == HandleKind.TypeReference && FullName((TypeReferenceHandle)handle) == "System.Runtime.CompilerServices.IsExternalInit";
            }

            private void AddEvent(string owner, EventDefinition e, GenericContext context, HashSet<MethodDefinitionHandle> accessors)
            {
                var a = e.GetAccessors();
                if (!a.Adder.IsNil)
                    accessors.Add(a.Adder);
                if (!a.Remover.IsNil)
                    accessors.Add(a.Remover);
                if (!a.Raiser.IsNil)
                    accessors.Add(a.Raiser);
                if (a.Adder.IsNil)
                    return;
                var adder = _reader.GetMethodDefinition(a.Adder);
                if ((adder.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
                    return;
                var name = _reader.GetString(e.Name);
                var isStatic = (adder.Attributes & MethodAttributes.Static) != 0 ? "static " : "";
                Add(owner, $"event {name} | {isStatic}{TypeName(e.Type, context)}");
                foreach (var attribute in Attributes(e.GetCustomAttributes()))
                    Add(owner, $"event {name} | attr {attribute}");
            }

            private void AddField(string owner, FieldDefinition field, GenericContext context)
            {
                if ((field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public)
                    return;
                var name = _reader.GetString(field.Name);
                if (name.StartsWith('<'))
                    return;
                var type = field.DecodeSignature(this, context);
                string modifiers;
                var value = "";
                if ((field.Attributes & FieldAttributes.Literal) != 0)
                {
                    modifiers = "const ";
                    value = " = " + FormatConstant(_reader.GetConstant(field.GetDefaultValue()), type);
                }
                else
                {
                    modifiers = ((field.Attributes & FieldAttributes.Static) != 0 ? "static " : "") + ((field.Attributes & FieldAttributes.InitOnly) != 0 ? "readonly " : "");
                }
                Add(owner, $"field {name} | {modifiers}{type}{value}");
                foreach (var attribute in Attributes(field.GetCustomAttributes()))
                    Add(owner, $"field {name} | attr {attribute}");
            }

            private static string Modifiers(MethodDefinition method)
            {
                var a = method.Attributes;
                if ((a & MethodAttributes.Abstract) != 0)
                    return "abstract ";
                if ((a & MethodAttributes.Virtual) == 0)
                    return "";
                if ((a & MethodAttributes.Final) != 0)
                    return "";
                return (a & MethodAttributes.NewSlot) != 0 ? "virtual " : "override ";
            }

            private void AddMethod(string owner, string key, MethodDefinition method, GenericContext context)
            {
                var signature = method.DecodeSignature(this, context);
                var isStatic = (method.Attributes & MethodAttributes.Static) != 0 ? "static " : "";
                var returns = key.StartsWith("ctor", StringComparison.Ordinal) ? "new" : signature.ReturnType;
                Add(owner, $"{key} | {isStatic}{Modifiers(method)}{returns}");
                foreach (var attribute in Attributes(method.GetCustomAttributes()))
                    Add(owner, $"{key} | attr {attribute}");

                var parameters = method.GetParameters().Select(_reader.GetParameter).ToDictionary(p => p.SequenceNumber);
                var order = new List<string>();
                for (var i = 0; i < signature.ParameterTypes.Length; i++)
                {
                    var found = parameters.TryGetValue(i + 1, out var parameter);
                    var csharp = found && !parameter.Name.IsNil ? _reader.GetString(parameter.Name) : $"arg{i}";
                    // [DefaultValue] is shown as "= value".
                    var attributes = found ? Attributes(parameter.GetCustomAttributes()).Where(a => !a.StartsWith("DefaultValue(", StringComparison.Ordinal)).ToList() : new List<string>();
                    var pinName = found ? PinName(parameter, TitleCase(csharp)) : TitleCase(csharp);
                    var type = signature.ParameterTypes[i];
                    var direction = "in";
                    if (type.EndsWith('&'))
                    {
                        type = type.Substring(0, type.Length - 1);
                        direction = (parameter.Attributes & ParameterAttributes.Out) != 0 ? "out" : "ref";
                    }
                    var value = found ? DefaultValue(parameter, type) : null;
                    var tail = attributes.Count > 0 ? " | " + string.Join(", ", attributes) : "";
                    Add(owner, $"{key} | pin {pinName} | {direction} {type}{(value is null ? "" : " = " + value)}{tail} | ({csharp})");
                    order.Add(pinName);
                }
                if (signature.ReturnType != "void" && !key.StartsWith("ctor", StringComparison.Ordinal))
                {
                    var found = parameters.TryGetValue(0, out var returnParameter);
                    var attributes = found ? Attributes(returnParameter.GetCustomAttributes()).ToList() : new List<string>();
                    var pinName = found ? PinName(returnParameter, "Output") : "Output";
                    var tail = attributes.Count > 0 ? " | " + string.Join(", ", attributes) : "";
                    Add(owner, $"{key} | pin {pinName} | out {signature.ReturnType}{tail} | (return)");
                    order.Add(pinName);
                }
                if (order.Count > 0)
                    Add(owner, $"{key} | order {string.Join(", ", order)}");
            }

            /// <summary>Name = "..." of [Pin] on the parameter, else <paramref name="fallback"/>.</summary>
            private string PinName(Parameter parameter, string fallback)
            {
                foreach (var handle in parameter.GetCustomAttributes())
                {
                    var attribute = _reader.GetCustomAttribute(handle);
                    if (AttributeTypeName(attribute) != "VL.Core.Import.PinAttribute")
                        continue;
                    var value = attribute.DecodeValue(this);
                    foreach (var named in value.NamedArguments)
                    {
                        if (named.Name == "Name" && named.Value is string s)
                            return s;
                    }
                }
                return fallback;
            }

            /// <summary>Default of a pin: [DefaultValue] wins over the C# parameter default; null if neither.</summary>
            private string? DefaultValue(Parameter parameter, string type)
            {
                foreach (var handle in parameter.GetCustomAttributes())
                {
                    var attribute = _reader.GetCustomAttribute(handle);
                    if (AttributeTypeName(attribute) != "System.ComponentModel.DefaultValueAttribute")
                        continue;
                    var value = attribute.DecodeValue(this);
                    // [DefaultValue(typeof(T), "text")]: the text is the value.
                    var arg = value.FixedArguments.Length == 2 ? value.FixedArguments[1] : value.FixedArguments[0];
                    // Text for a non-string pin ("1, 1" for a Vector2) is written as vvvv parses it, without quotes.
                    if (arg.Value is string text && arg.Type != "System.Type" && type != "string")
                        return text;
                    return FormatArgument(arg.Type, arg.Value);
                }
                if ((parameter.Attributes & ParameterAttributes.HasDefault) != 0 && !parameter.GetDefaultValue().IsNil)
                    return FormatConstant(_reader.GetConstant(parameter.GetDefaultValue()), type);
                return null;
            }

            private IEnumerable<string> Attributes(CustomAttributeHandleCollection handles)
            {
                var result = new List<string>();
                foreach (var handle in handles)
                {
                    var attribute = _reader.GetCustomAttribute(handle);
                    var typeName = AttributeTypeName(attribute);
                    if (!typeName.StartsWith("VL.", StringComparison.Ordinal) && !s_systemAttributes.Contains(typeName))
                        continue;
                    var value = attribute.DecodeValue(this);
                    var args = value.FixedArguments.Select(a => FormatArgument(a.Type, a.Value))
                        .Concat(value.NamedArguments.Select(n => $"{n.Name} = {FormatArgument(n.Type, n.Value)}"));
                    var shortName = typeName.Substring(typeName.LastIndexOf('.') + 1);
                    if (shortName.EndsWith("Attribute", StringComparison.Ordinal))
                        shortName = shortName.Substring(0, shortName.Length - "Attribute".Length);
                    var text = string.Join(", ", args);
                    result.Add(text.Length > 0 ? $"{shortName}({text})" : shortName);
                }
                result.Sort(StringComparer.Ordinal);
                return result;
            }

            private string AttributeTypeName(CustomAttribute attribute)
            {
                switch (attribute.Constructor.Kind)
                {
                    case HandleKind.MemberReference:
                        var parent = _reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                        return parent.Kind switch
                        {
                            HandleKind.TypeReference => FullName((TypeReferenceHandle)parent),
                            HandleKind.TypeDefinition => FullName((TypeDefinitionHandle)parent),
                            _ => "?",
                        };
                    case HandleKind.MethodDefinition:
                        return FullName(_reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType());
                    default:
                        return "?";
                }
            }

            private string FormatArgument(string type, object? value)
            {
                switch (value)
                {
                    case null:
                        return "null";
                    case ImmutableArray<CustomAttributeTypedArgument<string>> array:
                        return "[" + string.Join(", ", array.Select(a => FormatArgument(a.Type, a.Value))) + "]";
                    case string s when type == "System.Type":
                        return $"typeof({StripAssembly(s)})";
                    case string s:
                        return Quote(s);
                }
                var info = Enum(type);
                if (info != null)
                    return EnumName(type, info, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return FormatValue(value);
            }

            private string FormatConstant(Constant constant, string? type)
            {
                var blob = _reader.GetBlobReader(constant.Value);
                object? value = constant.TypeCode switch
                {
                    ConstantTypeCode.Boolean => blob.ReadBoolean(),
                    ConstantTypeCode.Char => blob.ReadChar(),
                    ConstantTypeCode.SByte => blob.ReadSByte(),
                    ConstantTypeCode.Byte => blob.ReadByte(),
                    ConstantTypeCode.Int16 => blob.ReadInt16(),
                    ConstantTypeCode.UInt16 => blob.ReadUInt16(),
                    ConstantTypeCode.Int32 => blob.ReadInt32(),
                    ConstantTypeCode.UInt32 => blob.ReadUInt32(),
                    ConstantTypeCode.Int64 => blob.ReadInt64(),
                    ConstantTypeCode.UInt64 => blob.ReadUInt64(),
                    ConstantTypeCode.Single => blob.ReadSingle(),
                    ConstantTypeCode.Double => blob.ReadDouble(),
                    ConstantTypeCode.String => ReadConstantString(constant),
                    _ => null,
                };
                if (value is null)
                    return "null";
                if (type != null && value is not string && value is not bool && Enum(type) is { } info)
                    return EnumName(type, info, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return FormatValue(value);
            }

            private string ReadConstantString(Constant constant)
            {
                // String constants are UTF-16 without a length prefix.
                var bytes = _reader.GetBlobBytes(constant.Value);
                return Encoding.Unicode.GetString(bytes);
            }

            private static string EnumName(string type, EnumInfo info, long value)
            {
                var shortType = type.Substring(type.LastIndexOf('.') + 1);
                if (info.Names.TryGetValue(value, out var name))
                    return $"{shortType}.{name}";
                if (info.Flags && value != 0)
                {
                    var parts = new List<string>();
                    var rest = value;
                    foreach (var (bit, bitName) in info.Names.Where(n => n.Key != 0).OrderByDescending(n => n.Key))
                    {
                        if ((rest & bit) == bit)
                        {
                            parts.Add($"{shortType}.{bitName}");
                            rest &= ~bit;
                        }
                    }
                    if (rest == 0)
                        return string.Join(" | ", parts.OrderBy(p => p, StringComparer.Ordinal));
                }
                return $"({shortType}){value.ToString(CultureInfo.InvariantCulture)}";
            }

            private static string StripAssembly(string serializedName)
            {
                // "Ns.Type, Assembly, Version=..." -> "Ns.Type" (generic arguments in [[...]] keep their brackets).
                var depth = 0;
                for (var i = 0; i < serializedName.Length; i++)
                {
                    var c = serializedName[i];
                    if (c == '[')
                        depth++;
                    else if (c == ']')
                        depth--;
                    else if (c == ',' && depth == 0)
                        return Keyword(serializedName.Substring(0, i).Trim().Replace('+', '.'));
                }
                return Keyword(serializedName.Trim().Replace('+', '.'));
            }

            // --- enums ---

            private EnumInfo? ReadEnum(TypeDefinition type)
            {
                if (type.BaseType.IsNil || TypeName(type.BaseType, default) != "System.Enum")
                    return null;
                var underlying = PrimitiveTypeCode.Int32;
                var names = new Dictionary<long, string>();
                foreach (var f in type.GetFields())
                {
                    var field = _reader.GetFieldDefinition(f);
                    if ((field.Attributes & FieldAttributes.Static) == 0)
                    {
                        underlying = UnderlyingCode(field.DecodeSignature(this, default));
                        continue;
                    }
                    if (field.GetDefaultValue().IsNil)
                        continue;
                    var constant = _reader.GetConstant(field.GetDefaultValue());
                    var blob = _reader.GetBlobReader(constant.Value);
                    long value = constant.TypeCode switch
                    {
                        ConstantTypeCode.SByte => blob.ReadSByte(),
                        ConstantTypeCode.Byte => blob.ReadByte(),
                        ConstantTypeCode.Int16 => blob.ReadInt16(),
                        ConstantTypeCode.UInt16 => blob.ReadUInt16(),
                        ConstantTypeCode.Int32 => blob.ReadInt32(),
                        ConstantTypeCode.UInt32 => blob.ReadUInt32(),
                        ConstantTypeCode.Int64 => blob.ReadInt64(),
                        ConstantTypeCode.UInt64 => (long)blob.ReadUInt64(),
                        _ => 0,
                    };
                    names.TryAdd(value, _reader.GetString(field.Name));
                }
                var flags = type.GetCustomAttributes().Any(h => AttributeTypeName(_reader.GetCustomAttribute(h)) == "System.FlagsAttribute");
                return new EnumInfo(underlying, names, flags);
            }

            private static PrimitiveTypeCode UnderlyingCode(string keyword) => keyword switch
            {
                "sbyte" => PrimitiveTypeCode.SByte,
                "byte" => PrimitiveTypeCode.Byte,
                "short" => PrimitiveTypeCode.Int16,
                "ushort" => PrimitiveTypeCode.UInt16,
                "uint" => PrimitiveTypeCode.UInt32,
                "long" => PrimitiveTypeCode.Int64,
                "ulong" => PrimitiveTypeCode.UInt64,
                _ => PrimitiveTypeCode.Int32,
            };

            /// <summary>Enum facts of a type by full name: local from this metadata, external by loading the referenced assembly; null if not an enum or not found.</summary>
            private EnumInfo? Enum(string type)
            {
                if (_localEnums.TryGetValue(type, out var local))
                    return local;
                if (_externalEnums.TryGetValue(type, out var cached))
                    return cached;
                EnumInfo? info = null;
                var candidates = _externalEnumAssemblies.TryGetValue(type, out var assemblyName)
                    ? new[] { assemblyName }
                    : _reader.AssemblyReferences.Select(h => _reader.GetString(_reader.GetAssemblyReference(h).Name)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                foreach (var candidate in candidates)
                {
                    try
                    {
                        var runtimeType = Assembly.Load(new AssemblyName(candidate)).GetType(type);
                        if (runtimeType is { IsEnum: true })
                        {
                            var names = new Dictionary<long, string>();
                            foreach (var v in System.Enum.GetValues(runtimeType))
                                names.TryAdd(Convert.ToInt64(v, CultureInfo.InvariantCulture), System.Enum.GetName(runtimeType, v)!);
                            info = new EnumInfo(UnderlyingCode(Keyword(System.Enum.GetUnderlyingType(runtimeType).FullName!)), names, runtimeType.IsDefined(typeof(FlagsAttribute), false));
                            break;
                        }
                    }
                    catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
                    {
                        // Not available to the test host (e.g. VL.Audio): values stay numbers.
                    }
                }
                _externalEnums[type] = info;
                return info;
            }

            // --- names ---

            private string FullName(TypeDefinitionHandle handle)
            {
                var type = _reader.GetTypeDefinition(handle);
                var name = StripArity(_reader.GetString(type.Name));
                if (type.IsNested)
                    return FullName(type.GetDeclaringType()) + "." + name;
                var ns = _reader.GetString(type.Namespace);
                return ns.Length > 0 ? ns + "." + name : name;
            }

            private string FullName(TypeReferenceHandle handle)
            {
                var type = _reader.GetTypeReference(handle);
                var name = StripArity(_reader.GetString(type.Name));
                if (type.ResolutionScope.Kind == HandleKind.TypeReference)
                    return FullName((TypeReferenceHandle)type.ResolutionScope) + "." + name;
                var ns = _reader.GetString(type.Namespace);
                return ns.Length > 0 ? ns + "." + name : name;
            }

            private static string StripArity(string name)
            {
                var tick = name.IndexOf('`');
                return tick < 0 ? name : name.Substring(0, tick);
            }

            private string TypeName(EntityHandle handle, GenericContext context) => handle.Kind switch
            {
                HandleKind.TypeDefinition => Keyword(FullName((TypeDefinitionHandle)handle)),
                HandleKind.TypeReference => Keyword(FullName((TypeReferenceHandle)handle)),
                HandleKind.TypeSpecification => _reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, context),
                _ => "?",
            };

            // --- ISignatureTypeProvider / ICustomAttributeTypeProvider ---

            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
            {
                PrimitiveTypeCode.Void => "void",
                PrimitiveTypeCode.Boolean => "bool",
                PrimitiveTypeCode.Char => "char",
                PrimitiveTypeCode.SByte => "sbyte",
                PrimitiveTypeCode.Byte => "byte",
                PrimitiveTypeCode.Int16 => "short",
                PrimitiveTypeCode.UInt16 => "ushort",
                PrimitiveTypeCode.Int32 => "int",
                PrimitiveTypeCode.UInt32 => "uint",
                PrimitiveTypeCode.Int64 => "long",
                PrimitiveTypeCode.UInt64 => "ulong",
                PrimitiveTypeCode.Single => "float",
                PrimitiveTypeCode.Double => "double",
                PrimitiveTypeCode.String => "string",
                PrimitiveTypeCode.Object => "object",
                PrimitiveTypeCode.IntPtr => "nint",
                PrimitiveTypeCode.UIntPtr => "nuint",
                PrimitiveTypeCode.TypedReference => "System.TypedReference",
                _ => typeCode.ToString(),
            };

            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Keyword(FullName(handle));

            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Keyword(FullName(handle));

            public string GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
                reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

            public string GetSZArrayType(string elementType) => elementType + "[]";

            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

            public string GetByReferenceType(string elementType) => elementType + "&";

            public string GetPointerType(string elementType) => elementType + "*";

            public string GetPinnedType(string elementType) => elementType;

            public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            {
                if (genericType == "System.Nullable" && typeArguments.Length == 1)
                    return typeArguments[0] + "?";
                return genericType + "<" + string.Join(", ", typeArguments) + ">";
            }

            public string GetGenericTypeParameter(GenericContext genericContext, int index) =>
                index < genericContext.TypeParameters.Length ? genericContext.TypeParameters[index] : "!" + index.ToString(CultureInfo.InvariantCulture);

            public string GetGenericMethodParameter(GenericContext genericContext, int index) =>
                index < genericContext.MethodParameters.Length ? genericContext.MethodParameters[index] : "!!" + index.ToString(CultureInfo.InvariantCulture);

            public string GetFunctionPointerType(MethodSignature<string> signature) =>
                "delegate*<" + string.Join(", ", signature.ParameterTypes.Append(signature.ReturnType)) + ">";

            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

            public string GetSystemType() => "System.Type";

            public bool IsSystemType(string type) => type == "System.Type";

            public string GetTypeFromSerializedName(string name)
            {
                var type = StripAssembly(name);
                var comma = name.IndexOf(',', name.LastIndexOf(']') + 1);
                if (comma > 0)
                    _externalEnumAssemblies.TryAdd(type, name.Substring(comma + 1).Split(',')[0].Trim());
                return type;
            }

            public PrimitiveTypeCode GetUnderlyingEnumType(string type) => Enum(type)?.Underlying ?? PrimitiveTypeCode.Int32;
        }
    }
}
