using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Diagnostics;
using VL.Qt.Elements;
using VL.Qt.Reconciliation;
using VL.Qt.Shaders;
using VL.Qt.Wire;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>Filter shader files as links of the effect chain: scan, @padding, pins, link, SET_EFFECT_CHAIN.</summary>
    public class ShaderEffectInstanceTests
    {
        private const string filterSource = @"// @summary Splits the channels.
// @padding 4, 8, 12, 16
#version 440
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    float amount; // @default 0.5
    vec2 texelSize;
    float weight;
    float glow;
};
layout(binding = 1) uniform sampler2D source;
layout(binding = 2) uniform sampler2D map;
void main() { fragColor = texture(source, qt_TexCoord0) * amount * qt_Opacity; }
";

        private const string filterReflection = "ok\nuniform qt_Matrix mat4 0\nuniform qt_Opacity float 0\nuniform amount float 0\nuniform texelSize vec2 0\nuniform weight float 0\nuniform glow float 0\nsampler source 1\nsampler map 2\n";

        private const string generatorSource = @"#version 440
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf { mat4 qt_Matrix; float qt_Opacity; float phase; };
// uniform sampler2D source; (only a comment)
void main() { fragColor = vec4(phase); }
";

        private string _dir = null!;

        [SetUp]
        public void CreateDir()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vlqt-shaderlinks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void DeleteDir() => Directory.Delete(_dir, recursive: true);

        private string Write(string name, string content)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        private (ShaderSource Source, ShaderFile File) LoadSource(string source, string reflectionText)
        {
            var path = Write("Test.frag", source);
            var file = new ShaderFile(path, null, "Test", null, ShaderNodeFactory.DefaultCategory, null, null, true);
            return (ShaderSource.Load(file, (_, _) => ShaderReflection.Parse(reflectionText)), file);
        }

        private ShaderLayout Load(string source, string reflectionText)
        {
            var (loaded, file) = LoadSource(source, reflectionText);
            return ShaderLayout.From(loaded, file);
        }

        private ShaderEffectLayout LoadChain(string source, string reflectionText, string? vertexSource = null)
        {
            var (loaded, file) = LoadSource(source, reflectionText);
            return ShaderEffectLayout.From(vertexSource is null ? loaded : loaded with { VertexSource = vertexSource }, file);
        }

        // --- scan and descriptions ---

        [Test]
        public void Scan_FilterIsMarked_GeneratorAndCommentAreNot()
        {
            Write("Split.frag", filterSource);
            Write("Stripes.frag", generatorSource);

            var files = ShaderNodeFactory.Scan(_dir);

            Assert.That(files.Single(f => f.Name == "Split").IsFilter);
            Assert.That(files.Single(f => f.Name == "Stripes").IsFilter, Is.False);
        }

        [Test]
        public void Descriptions_FilterGivesElementAndLink_GeneratorOnlyElement()
        {
            Write("Split.frag", filterSource);
            Write("Stripes.frag", generatorSource);

            var nodes = ShaderNodeFactory.CreateDescriptions(ShaderNodeFactory.Scan(_dir), new FakeFactory(), NullLogger.Instance);

            var named = nodes.Select(n => (n.Name, n.Category)).ToArray();
            Assert.That(named, Is.EquivalentTo(new[]
            {
                ("Split", "Qt.Elements.Shaders"),
                ("Split", "Qt.Effects"),
                ("Stripes", "Qt.Elements.Shaders"),
            }));
        }

        [Test]
        public void Descriptions_CategoryAnnotationMovesOnlyTheElementNode()
        {
            Write("Split.frag", "// @category Qt.Test\n" + filterSource);
            var nodes = ShaderNodeFactory.CreateDescriptions(ShaderNodeFactory.Scan(_dir), new FakeFactory(), NullLogger.Instance);
            Assert.That(nodes.Select(n => n.Category), Is.EquivalentTo(new[] { "Qt.Test", "Qt.Effects" }));
        }

        [Test]
        public void Scan_FilterWithLinkCategory_ElementNodeFallsBackToDefault()
        {
            Write("Split.frag", "// @category Qt.Effects\n" + filterSource);
            var nodes = ShaderNodeFactory.CreateDescriptions(ShaderNodeFactory.Scan(_dir), new FakeFactory(), NullLogger.Instance);
            Assert.That(nodes.Select(n => n.Category), Is.EquivalentTo(new[] { "Qt.Elements.Shaders", "Qt.Effects" }));
        }

        [Test]
        public void Scan_RelativeCategory_IsAppendedToTheDefaultOfEachNodeKind()
        {
            Write("Split.frag", "// @category Examples\n" + filterSource);
            Write("Stripes.frag", "// @category Examples.Deep\n" + generatorSource);
            Write("Chain.frag", "// @category Examples\n// @chainonly\n" + filterSource);

            var nodes = ShaderNodeFactory.CreateDescriptions(ShaderNodeFactory.Scan(_dir), new FakeFactory(), NullLogger.Instance);
            Assert.That(nodes.Select(n => (n.Name, n.Category)), Is.EquivalentTo(new[]
            {
                ("Split", "Qt.Elements.Shaders.Examples"),
                ("Split", "Qt.Effects.Examples"),
                ("Stripes", "Qt.Elements.Shaders.Examples.Deep"),
                ("Chain", "Qt.Effects.Examples"),
            }));
        }

        [Test]
        public void HeadChanged_CategoryEdit_IsReported()
        {
            var path = Write("Split.frag", "// @category Qt.Test\n" + filterSource);
            var file = ShaderNodeFactory.Scan(_dir).Single();
            Assert.That(ShaderNodeFactory.HeadChanged(file), Is.False);

            File.WriteAllText(path, "// @category Qt.Other\n" + filterSource);
            Assert.That(ShaderNodeFactory.HeadChanged(file), "absolute category changed");

            File.WriteAllText(path, "// @category Qt.Test\n// @summary only the summary\n" + filterSource);
            Assert.That(ShaderNodeFactory.HeadChanged(file), Is.False, "a summary change goes to the node itself");

            File.WriteAllText(path, "// @category Examples\n" + filterSource);
            var relative = ShaderNodeFactory.Scan(_dir).Single();
            File.WriteAllText(path, "// @category Mine\n" + filterSource);
            Assert.That(ShaderNodeFactory.HeadChanged(relative), "relative category changed");
        }

        [Test]
        public void Scan_SameFileNameInDifferentCategories_IsNoDuplicate()
        {
            Write("Split.frag", "// @category Qt.A\n" + generatorSource);
            Directory.CreateDirectory(Path.Combine(_dir, "sub"));
            Write(Path.Combine("sub", "Split.frag"), "// @category Qt.B\n" + generatorSource);
            Assert.That(ShaderNodeFactory.Scan(_dir).Select(f => f.DuplicateError), Is.All.Null);
        }

        [Test]
        public void Scan_SameNodeFromTwoFileNames_IsDuplicate()
        {
            // "Glow_.frag" and "Glow.frag" both give the node "Glow".
            Write("Glow.frag", generatorSource);
            Write("Glow_.frag", generatorSource);
            var files = ShaderNodeFactory.Scan(_dir);
            Assert.That(files.Select(f => f.FullName), Is.EqualTo(new[] { "Glow", "Glow" }));
            Assert.That(files.Select(f => f.DuplicateError), Is.All.Not.Null);
            Assert.That(files[0].DuplicateError, Does.Contain("Rename"));
        }

        [Test]
        public void Scan_FilterNamedLikeBuiltInEffect_IsReported()
        {
            Write("Blur.frag", filterSource);
            Write("Fine.frag", filterSource);
            Write("BlurGen.frag", generatorSource);
            var files = ShaderNodeFactory.Scan(_dir);
            Assert.That(files.Single(f => f.Name == "Blur").DuplicateError, Does.Contain("built-in").And.Contain("Rename"));
            Assert.That(files.Single(f => f.Name == "Fine").DuplicateError, Is.Null);
            Assert.That(files.Single(f => f.Name == "BlurGen").DuplicateError, Is.Null);
        }

        // --- @padding ---

        private static ShaderAnnotations Annotations(string head) => ShaderAnnotations.Parse(head + "\n#version 440\nvoid main() {}\n");

        [Test]
        public void Padding_OneNumber_AllSides()
        {
            var a = Annotations("// @padding 6");
            Assert.That(a.Padding, Is.EqualTo(ShaderPadding.Fixed(new RectangleF(6, 6, 6, 6))));
            Assert.That(a.Warnings, Is.Empty);
        }

        [Test]
        public void Padding_FourNumbers_LeftTopRightBottom()
        {
            var a = Annotations("// @padding 1, 2.5, 3, 4");
            Assert.That(a.Padding, Is.EqualTo(ShaderPadding.Fixed(new RectangleF(1, 2.5f, 3, 4))));
            Assert.That(a.Warnings, Is.Empty);
        }

        [Test]
        public void Padding_Missing_IsZero()
        {
            var a = Annotations("// @summary x");
            Assert.That(a.Padding, Is.EqualTo(ShaderPadding.None));
            Assert.That(a.Warnings, Is.Empty);
        }

        [TestCase("// @padding 1, 2")]
        [TestCase("// @padding 1, 2, 3")]
        [TestCase("// @padding -1")]
        [TestCase("// @padding ")]
        public void Padding_Broken_WarnsWithLineAndIsZero(string head)
        {
            var a = Annotations("// first line\n" + head);
            Assert.That(a.Padding, Is.EqualTo(ShaderPadding.None));
            var warning = a.Warnings.Single();
            Assert.That(warning.Line, Is.EqualTo(2));
            Assert.That(warning.Text, Does.StartWith("@padding"));
        }

        [Test]
        public void Padding_AtUniform_Warns()
        {
            var a = ShaderAnnotations.Parse("#version 440\nlayout(std140, binding = 0) uniform buf {\n    float x; // @padding 3\n};\n");
            Assert.That(a.Padding, Is.EqualTo(ShaderPadding.None));
            Assert.That(a.Warnings.Single().Text, Does.Contain("@padding").And.Contain("file head"));
        }

        // --- layout and pins ---

        [Test]
        public void Layout_CarriesPadding_ForChainDropsQtNativeUniforms()
        {
            var chain = LoadChain(filterSource, filterReflection);
            Assert.That(chain.Padding, Is.EqualTo(ShaderPadding.Fixed(new RectangleF(4, 8, 12, 16))));

            // map (image) and its sampler pin first, then the uniforms; texelSize and weight are QtNative's, glow is reserved (an error at the node).
            Assert.That(chain.Pins.Select(p => p.UniformName), Is.EqualTo(new[] { "map", "map", "amount" }));
            Assert.That(chain.Messages.Where(m => m.Type == MessageType.Warning), Is.Empty);
            Assert.That(chain.Messages.Single(m => m.Type == MessageType.Error).Text, Does.Contain("'glow'").And.Contain("reserved"));
        }

        [Test]
        public void ForChain_NotAFilterAnyMore_IsError()
        {
            var chain = LoadChain(generatorSource, "ok\nuniform qt_Matrix mat4 0\nuniform qt_Opacity float 0\nuniform phase float 0\n");
            Assert.That(chain.Messages.Single(m => m.Type == MessageType.Error).Text, Does.Contain("sampler2D source"));
        }

        [Test]
        public void ForChain_Vertex_IsWarning()
        {
            var chain = LoadChain(filterSource, filterReflection, "#version 440\n");
            Assert.That(chain.Messages.Select(m => m.Text), Has.Some.Contain(".vert"));
        }

        [Test]
        public void Instance_PinsMatchDescription_InputIsEffect()
        {
            var layout = LoadChain(filterSource, filterReflection);
            using var instance = new ShaderEffectInstance((ResourceClient?)null, layout);

            var inputs = ShaderEffectDescription.Inputs(layout);
            Assert.That(inputs.Select(d => d.Name), Is.EqualTo(new[] { "Input", "Map", "Map Sampler", "Amount", "Apply" }));
            Assert.That(inputs[0].Type, Is.EqualTo(typeof(Effect)));
            Assert.That(instance.Inputs.Select(PinType), Is.EqualTo(inputs.Select(d => d.Type)));
            Assert.That(ShaderEffectDescription.Outputs.Select(d => d.Name), Is.EqualTo(new[] { "Output" }), "no Error output: shader problems show at the node");
            Assert.That(ShaderEffectDescription.Outputs[0].Type, Is.EqualTo(typeof(Effect)));
            Assert.That(instance.Outputs.Select(PinType), Is.EqualTo(ShaderEffectDescription.Outputs.Select(d => d.Type)));

            static Type PinType(IVLPin pin) => pin.GetType().GetInterfaces()
                .Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IVLPin<>)).GetGenericArguments()[0];
        }

        // --- the link ---

        private FakeResourceApi _api = null!;
        private ResourceClient _client = null!;

        [SetUp]
        public void CreateClient()
        {
            _api = new FakeResourceApi();
            _client = new ResourceClient(_api, Environment.CurrentManagedThreadId, static () => null);
        }

        private static Effect? Output(ShaderEffectInstance instance) => (Effect?)instance.Outputs.First().Value;

        [Test]
        public void Update_NewLinkOnlyOnChange()
        {
            var layout = LoadChain(filterSource, filterReflection);
            using var instance = new ShaderEffectInstance(_client, layout);
            var input = new BlurEffect(null, 0.5f, 32, 0);
            ((IVLPin)instance.Inputs.First()).Value = input;

            instance.Update();
            var first = Output(instance);
            Assert.That(first, Is.InstanceOf<ShaderEffect>());
            var link = (ShaderEffect)first!;
            Assert.That(link.Previous, Is.SameAs(input));
            Assert.That(link.Template, Is.EqualTo(Protocol.EffectTemplate.Single));
            Assert.That(link.ShaderIds, Has.Length.EqualTo(1));
            Assert.That(link.ShaderIds[0], Is.Not.EqualTo(0UL));
            Assert.That(link.Padding, Is.EqualTo(new RectangleF(4, 8, 12, 16)), "DIP, not converted");
            Assert.That(link.Uniforms.Select(u => u.Name), Is.EqualTo(new[] { "map", "map", "amount" }));
            Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.EqualTo(1));

            instance.Update();
            instance.Update();
            Assert.That(Output(instance), Is.SameAs(first));
            Assert.That(instance.Created, Is.EqualTo(1));
            Assert.That(_api.Calls.Count(c => c.StartsWith("create")), Is.EqualTo(1), "the shader is baked once");

            ((IVLPin<float>)instance.Uniforms.Pins[2]).Value = 0.25f;
            instance.Update();
            Assert.That(Output(instance), Is.Not.SameAs(first));
            Assert.That(instance.Created, Is.EqualTo(2));

            var second = Output(instance);
            ((IVLPin)instance.Inputs.First()).Value = null;
            instance.Update();
            Assert.That(Output(instance), Is.Not.SameAs(second));
            Assert.That(((ShaderEffect)Output(instance)!).Previous, Is.Null);
        }

        [Test]
        public void Apply_OffPassesTheChainOn_AndTheTextIsTheSharedOne()
        {
            var layout = LoadChain(filterSource, filterReflection);
            using var instance = new ShaderEffectInstance(_client, layout);
            var inputs = ShaderEffectDescription.Inputs(layout);
            Assert.That(inputs[^1].Name, Is.EqualTo("Apply"), "last input pin");
            Assert.That(inputs[^1].Type, Is.EqualTo(typeof(bool)));
            Assert.That(inputs[^1].DefaultValue, Is.EqualTo(true));
            Assert.That(((IVLPinDescriptionWithVisibility)inputs[^1]).IsVisible, Is.False, "optional pin");
            Assert.That(((IInfo)inputs[^1]).Summary, Is.EqualTo(CommonPinsXml.Param("effect", "apply")));

            var input = new BlurEffect(null, 0.5f, 32, 0);
            ((IVLPin)instance.Inputs.First()).Value = input;
            var apply = (IVLPin)instance.Inputs.Last();
            Assert.That(apply.Value, Is.EqualTo(true), "default");
            apply.Value = false;
            instance.Update();
            Assert.That(Output(instance), Is.SameAs(input));
            Assert.That(instance.Created, Is.Zero);
            apply.Value = true;
            instance.Update();
            Assert.That(Output(instance), Is.InstanceOf<ShaderEffect>());
        }

        [Test]
        public void Dispose_ReleasesTheShader()
        {
            var layout = LoadChain(filterSource, filterReflection);
            var instance = new ShaderEffectInstance(_client, layout);
            instance.Update();
            Assert.That(_api.Released, Is.Empty);
            instance.Dispose();
            Assert.That(_api.Released, Has.Count.EqualTo(1));
        }

        [Test]
        public void Update_WithoutShaderId_PassesTheChainOn()
        {
            var layout = LoadChain(filterSource, filterReflection) with { FragmentSource = string.Empty };
            using var instance = new ShaderEffectInstance(_client, layout);
            var input = new BlurEffect(null, 0.5f, 32, 0);
            ((IVLPin)instance.Inputs.First()).Value = input;
            instance.Update();
            Assert.That(Output(instance), Is.SameAs(input));
        }

        [Test]
        public void Link_SendsSingleShaderEffectInSetEffectChain()
        {
            var layout = LoadChain(filterSource, filterReflection);
            using var instance = new ShaderEffectInstance(_client, layout);
            ((IVLPin)instance.Inputs.First()).Value = new BlurEffect(null, 0.5f, 32, 0);
            instance.Update();
            var id = ((ShaderEffect)Output(instance)!).ShaderIds[0];

            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            rec.Update(new Rectangle { Effect = Output(instance) }, Space.DIPTopLeft);

            var chain = sink.OfOp("CHAIN").Single().Split(" | ").Skip(1).ToArray();
            Assert.That(chain, Has.Length.EqualTo(2));
            Assert.That(chain[0], Does.StartWith("multi"));
            Assert.That(chain[1], Does.StartWith($"shader {Protocol.EffectTemplate.Single} [{id}] pad=(4,8,12,16) strength=0"));
            Assert.That(chain[1], Does.Contain("amount=f32(0.5)").And.Contain("map=u64(0)"));
        }

        // --- @passes, @padding <uniform> [factor], @chainonly ---

        private const string blurHead = "// @passes 2\n// @padding blurLength 0.5\n";

        private const string blurBody = @"#version 440
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    float blurLength; // @default 0
    vec2 offset;
    float passIndex;
    float weight;
};
layout(binding = 1) uniform sampler2D source;
void main() { fragColor = texture(source, qt_TexCoord0) * blurLength; }
";

        private const string blurReflection = "ok\nuniform qt_Matrix mat4 0\nuniform qt_Opacity float 0\nuniform blurLength float 0\nuniform offset vec2 0\nuniform passIndex float 0\nuniform weight float 0\nsampler source 1\n";

        private ShaderEffectLayout LoadBlur(string head) => LoadChain(head + blurBody, blurReflection);

        private static string[] Errors(ShaderLayout layout) => Errors(layout.Messages);

        private static string[] Errors(ShaderEffectLayout layout) => Errors(layout.Messages);

        private static string[] Errors(ImmutableArray<Message> messages) =>
            messages.Where(m => m.Type == MessageType.Error).Select(m => m.Text).ToArray();

        [Test]
        public void Passes_DefaultOne_TwoAndOneAreAccepted()
        {
            Assert.That(Annotations("// @summary x").Passes, Is.EqualTo(1));
            var two = Annotations("// @passes 2");
            Assert.That(two.Passes, Is.EqualTo(2));
            Assert.That(two.Errors, Is.Empty);
            Assert.That(Annotations("// @passes 1").Passes, Is.EqualTo(1));
        }

        [TestCase("// @passes 3")]
        [TestCase("// @passes 0")]
        [TestCase("// @passes two")]
        [TestCase("// @passes ")]
        public void Passes_Other_IsErrorWithLine(string head)
        {
            var a = Annotations("// first line\n" + head);
            Assert.That(a.Passes, Is.EqualTo(1));
            var error = a.Errors.Single();
            Assert.That(error.Line, Is.EqualTo(2));
            Assert.That(error.Text, Does.StartWith("@passes"));
        }

        [Test]
        public void Passes_AtUniform_Warns()
        {
            var a = ShaderAnnotations.Parse("#version 440\nlayout(std140, binding = 0) uniform buf {\n    float x; // @passes 2\n};\n");
            Assert.That(a.Passes, Is.EqualTo(1));
            Assert.That(a.Warnings.Single().Text, Does.Contain("@passes").And.Contain("file head"));
        }

        [Test]
        public void PaddingUniform_FactorDefaultsToOne_FixedFormStays()
        {
            var a = Annotations("// @padding blurLength");
            Assert.That(a.Padding, Is.EqualTo(ShaderPadding.FromUniform("blurLength", 1f)));
            Assert.That(a.Errors, Is.Empty);
            Assert.That(a.Warnings, Is.Empty);

            var b = Annotations("// @padding blurLength 0.5");
            Assert.That(b.Padding, Is.EqualTo(ShaderPadding.FromUniform("blurLength", 0.5f)));

            var fixedPadding = Annotations("// @padding 6");
            Assert.That(fixedPadding.Padding, Is.EqualTo(ShaderPadding.Fixed(new RectangleF(6, 6, 6, 6))));
        }

        [TestCase("// @padding blurLength abc")]
        [TestCase("// @padding blurLength -1")]
        [TestCase("// @padding blurLength 1 2")]
        public void PaddingUniform_BrokenFactor_IsError(string head)
        {
            var a = Annotations("// first line\n" + head);
            Assert.That(a.Padding, Is.EqualTo(ShaderPadding.None));
            var error = a.Errors.Single();
            Assert.That(error.Line, Is.EqualTo(2));
            Assert.That(error.Text, Does.StartWith("@padding"));
            Assert.That(a.Warnings, Is.Empty);
        }

        [Test]
        public void ChainOnly_IsAFlagOfTheHead()
        {
            Assert.That(Annotations("// @chainonly").ChainOnly, Is.True);
            Assert.That(Annotations("// @summary x").ChainOnly, Is.False);
            var several = Annotations("// @passes 2 @chainonly");
            Assert.That((several.Passes, several.ChainOnly), Is.EqualTo((2, true)));
            var atUniform = ShaderAnnotations.Parse("#version 440\nlayout(std140, binding = 0) uniform buf {\n    float x; // @chainonly\n};\n");
            Assert.That(atUniform.ChainOnly, Is.False);
            Assert.That(atUniform.Warnings.Single().Text, Does.Contain("@chainonly").And.Contain("file head"));
        }

        [Test]
        public void ForChain_ValidTags_NoErrorAndLayoutCarriesThem()
        {
            var chain = LoadBlur(blurHead);
            Assert.That(Errors(chain), Is.Empty);
            Assert.That((chain.Template, chain.Padding), Is.EqualTo((Protocol.EffectTemplate.TwoPass, ShaderPadding.FromUniform("blurLength", 0.5f))));
            // passIndex and weight are QtNative's, no pins.
            Assert.That(chain.Pins.Select(p => p.UniformName), Is.EqualTo(new[] { "blurLength", "offset" }));
        }

        [Test]
        public void ForChain_BadPassesAndBadPaddingFactor_AreRedAtTheLinkNode()
        {
            Assert.That(Errors(LoadBlur("// @passes 3\n")).Single(), Does.Contain("@passes"));
            var broken = LoadBlur("// @padding blurLength x\n");
            Assert.That(Errors(broken).Single(), Does.Contain("@padding"));
            Assert.That(broken.Padding, Is.EqualTo(ShaderPadding.None));
        }

        [Test]
        public void ForChain_PaddingUniformUnknownNotFloatOrQtNativeOwned_IsErrorAndNoPadding()
        {
            foreach (var (name, expected) in new[] { ("nothing", "does not have"), ("offset", "not a float"), ("weight", "does not have"), ("source", "does not have") })
            {
                var chain = LoadBlur($"// @padding {name}\n");
                Assert.That(Errors(chain).Single(), Does.Contain(name).And.Contain(expected), name);
                Assert.That(chain.Padding, Is.EqualTo(ShaderPadding.None), name);
            }
        }

        [Test]
        public void ElementNodeLayout_ShowsNoChainErrors()
        {
            var layout = Load("// @passes 3\n" + blurBody, blurReflection);
            Assert.That(Errors(layout), Is.Empty);
        }

        [Test]
        public void Update_PassesTwoUsesTwoPassTemplate_DefaultIsSingle()
        {
            using var two = new ShaderEffectInstance(_client, LoadBlur("// @passes 2\n"));
            two.Update();
            Assert.That(((ShaderEffect)Output(two)!).Template, Is.EqualTo(Protocol.EffectTemplate.TwoPass));

            using var one = new ShaderEffectInstance(_client, LoadBlur(""));
            one.Update();
            Assert.That(((ShaderEffect)Output(one)!).Template, Is.EqualTo(Protocol.EffectTemplate.Single));
        }

        [Test]
        public void Update_PaddingFollowsTheUniformAbsTimesFactor_RecomputedOnlyOnChange()
        {
            using var instance = new ShaderEffectInstance(_client, LoadBlur(blurHead));
            var pin = (IVLPin<float>)instance.Uniforms.Pins[0];

            instance.Update();
            var first = (ShaderEffect)Output(instance)!;
            Assert.That(first.Padding, Is.EqualTo(RectangleF.Empty), "default 0");

            pin.Value = -40f;
            instance.Update();
            var second = (ShaderEffect)Output(instance)!;
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.Padding, Is.EqualTo(new RectangleF(20, 20, 20, 20)), "abs(-40) * 0.5, all sides");

            instance.Update();
            instance.Update();
            Assert.That(Output(instance), Is.SameAs(second));
            Assert.That(instance.Created, Is.EqualTo(2));

            pin.Value = 40f;
            instance.Update();
            Assert.That(((ShaderEffect)Output(instance)!).Padding, Is.EqualTo(new RectangleF(20, 20, 20, 20)));

            pin.Value = float.NaN;
            instance.Update();
            Assert.That(((ShaderEffect)Output(instance)!).Padding, Is.EqualTo(RectangleF.Empty), "not finite: no padding");
        }

        [Test]
        public void Update_FactorDefaultsToOne_FixedPaddingKeptWithoutUniform()
        {
            using var instance = new ShaderEffectInstance(_client, LoadBlur("// @padding blurLength\n"));
            ((IVLPin<float>)instance.Uniforms.Pins[0]).Value = 7f;
            instance.Update();
            Assert.That(((ShaderEffect)Output(instance)!).Padding, Is.EqualTo(new RectangleF(7, 7, 7, 7)));

            using var fixedInstance = new ShaderEffectInstance(_client, LoadBlur("// @padding 3\n"));
            ((IVLPin<float>)fixedInstance.Uniforms.Pins[0]).Value = 7f;
            fixedInstance.Update();
            Assert.That(((ShaderEffect)Output(fixedInstance)!).Padding, Is.EqualTo(new RectangleF(3, 3, 3, 3)));
        }

        [Test]
        public void Update_InvalidPaddingUniform_KeepsZeroPadding()
        {
            using var instance = new ShaderEffectInstance(_client, LoadBlur("// @padding offset\n"));
            instance.Update();
            Assert.That(((ShaderEffect)Output(instance)!).Padding, Is.EqualTo(RectangleF.Empty));
        }

        [Test]
        public void Scan_ChainOnlyIsMarked_NodeSetHasOnlyTheLink()
        {
            Write("Both.frag", filterSource);
            Write("Chain.frag", "// @chainonly\n" + filterSource);
            Write("Gen.frag", "// @chainonly\n" + generatorSource);

            var files = ShaderNodeFactory.Scan(_dir);
            Assert.That(files.Single(f => f.Name == "Chain").ChainOnly);
            Assert.That(files.Single(f => f.Name == "Both").ChainOnly, Is.False);

            var nodes = ShaderNodeFactory.CreateDescriptions(files, new FakeFactory(), NullLogger.Instance);
            Assert.That(nodes.Select(n => (n.Name, n.Category)), Is.EquivalentTo(new[]
            {
                ("Both", "Qt.Elements.Shaders"),
                ("Both", "Qt.Effects"),
                ("Chain", "Qt.Effects"),
            }), "a chainonly generator has no node at all");
        }

        [Test]
        public void Scan_ChainOnlyCategory_IsTheCategoryOfTheChainNode()
        {
            Write("Own.frag", "// @category Qt.Effects.Mine\n// @chainonly\n" + filterSource);
            Write("Plain.frag", "// @chainonly\n" + filterSource);
            Write("Both.frag", "// @category Qt.Effects.Mine\n" + filterSource);

            var nodes = ShaderNodeFactory.CreateDescriptions(ShaderNodeFactory.Scan(_dir), new FakeFactory(), NullLogger.Instance);
            Assert.That(nodes.Select(n => (n.Name, n.Category)), Is.EquivalentTo(new[]
            {
                ("Own", "Qt.Effects.Mine"),
                ("Plain", "Qt.Effects"),
                ("Both", "Qt.Effects.Mine"),
                ("Both", "Qt.Effects"),
            }), "with @chainonly @category moves the chain node, without it only the element node");
        }

        private sealed class FakeFactory : IVLNodeDescriptionFactory
        {
            public string Identifier => "Test";
            public System.Collections.Immutable.ImmutableArray<IVLNodeDescription> NodeDescriptions => default;
            public IObservable<object> Invalidated => System.Reactive.Linq.Observable.Never<object>();
            public IVLNodeDescriptionFactory? ForPath(string path) => null;
            public void Export(ExportContext exportContext) { }
        }
    }
}
