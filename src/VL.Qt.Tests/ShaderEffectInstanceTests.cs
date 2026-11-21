using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Diagnostics;
using VL.Qt.Elements;
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

        private ShaderNodeLayout Load(string source, string reflectionText)
        {
            var path = Write("Test.frag", source);
            var file = new ShaderFile(path, null, "Test", null, ShaderNodeFactory.DefaultCategory, null, null, true);
            return ShaderNodeLayout.Load(file, (_, _) => ShaderReflection.Parse(reflectionText), NullLogger.Instance);
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

        // --- @padding ---

        private static ShaderAnnotations Annotations(string head) => ShaderAnnotations.Parse(head + "\n#version 440\nvoid main() {}\n");

        [Test]
        public void Padding_OneNumber_AllSides()
        {
            var a = Annotations("// @padding 6");
            Assert.That(a.Padding, Is.EqualTo(new RectangleF(6, 6, 6, 6)));
            Assert.That(a.Warnings, Is.Empty);
        }

        [Test]
        public void Padding_FourNumbers_LeftTopRightBottom()
        {
            var a = Annotations("// @padding 1, 2.5, 3, 4");
            Assert.That(a.Padding, Is.EqualTo(new RectangleF(1, 2.5f, 3, 4)));
            Assert.That(a.Warnings, Is.Empty);
        }

        [Test]
        public void Padding_Missing_IsZero()
        {
            var a = Annotations("// @summary x");
            Assert.That(a.Padding, Is.EqualTo(RectangleF.Empty));
            Assert.That(a.Warnings, Is.Empty);
        }

        [TestCase("// @padding 1, 2")]
        [TestCase("// @padding 1, 2, 3")]
        [TestCase("// @padding -1")]
        [TestCase("// @padding ")]
        public void Padding_Broken_WarnsWithLineAndIsZero(string head)
        {
            var a = Annotations("// first line\n" + head);
            Assert.That(a.Padding, Is.EqualTo(RectangleF.Empty));
            var warning = a.Warnings.Single();
            Assert.That(warning.Line, Is.EqualTo(2));
            Assert.That(warning.Text, Does.StartWith("@padding"));
        }

        [Test]
        public void Padding_AtUniform_Warns()
        {
            var a = ShaderAnnotations.Parse("#version 440\nlayout(std140, binding = 0) uniform buf {\n    float x; // @padding 3\n};\n");
            Assert.That(a.Padding, Is.EqualTo(RectangleF.Empty));
            Assert.That(a.Warnings.Single().Text, Does.Contain("@padding").And.Contain("file head"));
        }

        // --- layout and pins ---

        [Test]
        public void Layout_CarriesPadding_ForChainDropsQtNativeUniforms()
        {
            var layout = Load(filterSource, filterReflection);
            Assert.That(layout.Padding, Is.EqualTo(new RectangleF(4, 8, 12, 16)));

            var chain = layout.ForChain(NullLogger.Instance, "Test.frag");
            // map (image) and its sampler pin first, then the uniforms; texelSize and weight are QtNative's, glow is reserved.
            Assert.That(chain.Pins.Select(p => p.UniformName), Is.EqualTo(new[] { "map", "map", "amount" }));
            Assert.That(chain.Padding, Is.EqualTo(layout.Padding));
            var warnings = chain.Messages.Where(m => m.Type == MessageType.Warning).Select(m => m.Text).ToArray();
            Assert.That(warnings.Single(), Does.Contain("'glow'").And.Contain("reserved"));
            Assert.That(chain.Messages.Any(m => m.Type == MessageType.Error), Is.False);
        }

        [Test]
        public void ForChain_NotAFilterAnyMore_IsError()
        {
            var layout = Load(generatorSource, "ok\nuniform qt_Matrix mat4 0\nuniform qt_Opacity float 0\nuniform phase float 0\n");
            var chain = layout.ForChain(NullLogger.Instance, "Test.frag");
            Assert.That(chain.Messages.Single(m => m.Type == MessageType.Error).Text, Does.Contain("sampler2D source"));
        }

        [Test]
        public void ForChain_Vertex_IsWarning()
        {
            var layout = Load(filterSource, filterReflection) with { VertexSource = "#version 440\n" };
            var chain = layout.ForChain(NullLogger.Instance, "Test.frag");
            Assert.That(chain.Messages.Select(m => m.Text), Has.Some.Contain(".vert"));
        }

        [Test]
        public void Instance_PinsMatchDescription_InputIsEffect()
        {
            var layout = Load(filterSource, filterReflection).ForChain(NullLogger.Instance, "Test.frag");
            using var instance = new ShaderEffectInstance((ResourceClient?)null, layout);

            var inputs = ShaderEffectDescription.Inputs(layout);
            Assert.That(inputs.Select(d => d.Name), Is.EqualTo(new[] { "Input", "Map", "Map Sampler", "Amount" }));
            Assert.That(inputs[0].Type, Is.EqualTo(typeof(Effect)));
            Assert.That(instance.Inputs.Select(PinType), Is.EqualTo(inputs.Select(d => d.Type)));
            Assert.That(ShaderEffectDescription.Outputs.Select(d => d.Name), Is.EqualTo(new[] { "Output", "Error" }));
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
            var layout = Load(filterSource, filterReflection).ForChain(NullLogger.Instance, "Test.frag");
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
        public void Dispose_ReleasesTheShader()
        {
            var layout = Load(filterSource, filterReflection).ForChain(NullLogger.Instance, "Test.frag");
            var instance = new ShaderEffectInstance(_client, layout);
            instance.Update();
            Assert.That(_api.Released, Is.Empty);
            instance.Dispose();
            Assert.That(_api.Released, Has.Count.EqualTo(1));
        }

        [Test]
        public void Update_WithoutShaderId_PassesTheChainOn()
        {
            var layout = Load(filterSource, filterReflection).ForChain(NullLogger.Instance, "Test.frag") with { FragmentSource = string.Empty };
            using var instance = new ShaderEffectInstance(_client, layout);
            var input = new BlurEffect(null, 0.5f, 32, 0);
            ((IVLPin)instance.Inputs.First()).Value = input;
            instance.Update();
            Assert.That(Output(instance), Is.SameAs(input));
        }

        [Test]
        public void Link_SendsSingleShaderEffectInSetEffectChain()
        {
            var layout = Load(filterSource, filterReflection).ForChain(NullLogger.Instance, "Test.frag");
            using var instance = new ShaderEffectInstance(_client, layout);
            ((IVLPin)instance.Inputs.First()).Value = new BlurEffect(null, 0.5f, 32, 0);
            instance.Update();
            var id = ((ShaderEffect)Output(instance)!).ShaderIds[0];

            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new SceneRegistry());
            rec.Update(new Rectangle { Effect = Output(instance) }, Space.DIPTopLeft);

            var chain = sink.OfOp("CHAIN").Single().Split(" | ").Skip(1).ToArray();
            Assert.That(chain, Has.Length.EqualTo(2));
            Assert.That(chain[0], Does.StartWith("multi"));
            Assert.That(chain[1], Does.StartWith($"shader {Protocol.EffectTemplate.Single} [{id}] pad=(400,800,1200,1600) strength=0"));
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

        private ShaderNodeLayout LoadBlur(string head) =>
            Load(head + blurBody, blurReflection).ForChain(NullLogger.Instance, "Test.frag");

        private static string[] Errors(ShaderNodeLayout layout) =>
            layout.Messages.Where(m => m.Type == MessageType.Error).Select(m => m.Text).ToArray();

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
            Assert.That((a.PaddingUniform, a.PaddingFactor), Is.EqualTo(("blurLength", 1f)));
            Assert.That(a.Padding, Is.EqualTo(RectangleF.Empty));
            Assert.That(a.Errors, Is.Empty);
            Assert.That(a.Warnings, Is.Empty);

            var b = Annotations("// @padding blurLength 0.5");
            Assert.That((b.PaddingUniform, b.PaddingFactor), Is.EqualTo(("blurLength", 0.5f)));

            var fixedPadding = Annotations("// @padding 6");
            Assert.That(fixedPadding.PaddingUniform, Is.Null);
            Assert.That(fixedPadding.Padding, Is.EqualTo(new RectangleF(6, 6, 6, 6)));
        }

        [TestCase("// @padding blurLength abc")]
        [TestCase("// @padding blurLength -1")]
        [TestCase("// @padding blurLength 1 2")]
        public void PaddingUniform_BrokenFactor_IsError(string head)
        {
            var a = Annotations("// first line\n" + head);
            Assert.That(a.PaddingUniform, Is.Null);
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
            Assert.That((chain.Passes, chain.PaddingUniform, chain.PaddingFactor), Is.EqualTo((2, "blurLength", 0.5f)));
            // passIndex and weight are QtNative's, no pins.
            Assert.That(chain.Pins.Select(p => p.UniformName), Is.EqualTo(new[] { "blurLength", "offset" }));
        }

        [Test]
        public void ForChain_BadPassesAndBadPaddingFactor_AreRedAtTheLinkNode()
        {
            Assert.That(Errors(LoadBlur("// @passes 3\n")).Single(), Does.Contain("@passes"));
            var broken = LoadBlur("// @padding blurLength x\n");
            Assert.That(Errors(broken).Single(), Does.Contain("@padding"));
            Assert.That(broken.PaddingUniform, Is.Null);
        }

        [Test]
        public void ForChain_PaddingUniformUnknownNotFloatOrQtNativeOwned_IsErrorAndNoPadding()
        {
            foreach (var (name, expected) in new[] { ("nothing", "does not have"), ("offset", "not a float"), ("weight", "does not have"), ("source", "does not have") })
            {
                var chain = LoadBlur($"// @padding {name}\n");
                Assert.That(Errors(chain).Single(), Does.Contain(name).And.Contain(expected), name);
                Assert.That(chain.PaddingUniform, Is.Null, name);
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
