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
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>Filter shader files as links of the effect chain (#489): scan, @padding, pins, link, SET_EFFECT_CHAIN.</summary>
    public class ShaderLinkNodeTests
    {
        const string filterSource = @"// @summary Splits the channels.
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

        const string filterReflection = "ok\nuniform qt_Matrix mat4 0\nuniform qt_Opacity float 0\nuniform amount float 0\nuniform texelSize vec2 0\nuniform weight float 0\nuniform glow float 0\nsampler source 1\nsampler map 2\n";

        const string generatorSource = @"#version 440
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf { mat4 qt_Matrix; float qt_Opacity; float phase; };
// uniform sampler2D source; (only a comment)
void main() { fragColor = vec4(phase); }
";

        string _dir = null!;

        [SetUp]
        public void CreateDir()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vlqt-shaderlinks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void DeleteDir() => Directory.Delete(_dir, recursive: true);

        string Write(string name, string content)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        ShaderNodeLayout Load(string source, string reflectionText)
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

        static ShaderAnnotations Annotations(string head) => ShaderAnnotations.Parse(head + "\n#version 440\nvoid main() {}\n");

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

        [TestCase("// @padding abc")]
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
            using var instance = new ShaderLinkNodeInstance((ResourceClient?)null, layout);

            var inputs = ShaderLinkDescription.Inputs(layout);
            Assert.That(inputs.Select(d => d.Name), Is.EqualTo(new[] { "Input", "Map", "Map Sampler", "Amount" }));
            Assert.That(inputs[0].Type, Is.EqualTo(typeof(Effect)));
            Assert.That(instance.Inputs.Select(PinType), Is.EqualTo(inputs.Select(d => d.Type)));
            Assert.That(ShaderLinkDescription.Outputs.Select(d => d.Name), Is.EqualTo(new[] { "Output", "Error" }));
            Assert.That(ShaderLinkDescription.Outputs[0].Type, Is.EqualTo(typeof(Effect)));
            Assert.That(instance.Outputs.Select(PinType), Is.EqualTo(ShaderLinkDescription.Outputs.Select(d => d.Type)));

            static Type PinType(IVLPin pin) => pin.GetType().GetInterfaces()
                .Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IVLPin<>)).GetGenericArguments()[0];
        }

        // --- the link ---

        FakeResourceApi _api = null!;
        ResourceClient _client = null!;

        [SetUp]
        public void CreateClient()
        {
            _api = new FakeResourceApi();
            _client = new ResourceClient(_api, Environment.CurrentManagedThreadId, static () => null);
        }

        static Effect? Output(ShaderLinkNodeInstance instance) => (Effect?)instance.Outputs.First().Value;

        [Test]
        public void Update_NewLinkOnlyOnChange()
        {
            var layout = Load(filterSource, filterReflection).ForChain(NullLogger.Instance, "Test.frag");
            using var instance = new ShaderLinkNodeInstance(_client, layout);
            var input = new BlurEffect(null, 0.5f, 32, 0);
            ((IVLPin)instance.Inputs.First()).Value = input;

            instance.Update();
            var first = Output(instance);
            Assert.That(first, Is.InstanceOf<ShaderLink>());
            var link = (ShaderLink)first!;
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
            Assert.That(((ShaderLink)Output(instance)!).Previous, Is.Null);
        }

        [Test]
        public void Dispose_ReleasesTheShader()
        {
            var layout = Load(filterSource, filterReflection).ForChain(NullLogger.Instance, "Test.frag");
            var instance = new ShaderLinkNodeInstance(_client, layout);
            instance.Update();
            Assert.That(_api.Released, Is.Empty);
            instance.Dispose();
            Assert.That(_api.Released, Has.Count.EqualTo(1));
        }

        [Test]
        public void Update_WithoutShaderId_PassesTheChainOn()
        {
            var layout = Load(filterSource, filterReflection).ForChain(NullLogger.Instance, "Test.frag") with { FragmentSource = string.Empty };
            using var instance = new ShaderLinkNodeInstance(_client, layout);
            var input = new BlurEffect(null, 0.5f, 32, 0);
            ((IVLPin)instance.Inputs.First()).Value = input;
            instance.Update();
            Assert.That(Output(instance), Is.SameAs(input));
        }

        [Test]
        public void Link_SendsSingleShaderLinkInSetEffectChain()
        {
            var layout = Load(filterSource, filterReflection).ForChain(NullLogger.Instance, "Test.frag");
            using var instance = new ShaderLinkNodeInstance(_client, layout);
            ((IVLPin)instance.Inputs.First()).Value = new BlurEffect(null, 0.5f, 32, 0);
            instance.Update();
            var id = ((ShaderLink)Output(instance)!).ShaderIds[0];

            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new SceneRegistry());
            rec.Update(new Rectangle { Effect = Output(instance) }, Space.DIPTopLeft);

            var chain = sink.OfOp("CHAIN").Single().Split(" | ").Skip(1).ToArray();
            Assert.That(chain, Has.Length.EqualTo(2));
            Assert.That(chain[0], Does.StartWith("multi"));
            Assert.That(chain[1], Does.StartWith($"shader {Protocol.EffectTemplate.Single} [{id}] pad=(4,8,12,16) strength=0"));
            Assert.That(chain[1], Does.Contain("amount=f32(0.5)").And.Contain("map=u64(0)"));
        }

        sealed class FakeFactory : IVLNodeDescriptionFactory
        {
            public string Identifier => "Test";
            public System.Collections.Immutable.ImmutableArray<IVLNodeDescription> NodeDescriptions => default;
            public IObservable<object> Invalidated => System.Reactive.Linq.Observable.Never<object>();
            public IVLNodeDescriptionFactory? ForPath(string path) => null;
            public void Export(ExportContext exportContext) { }
        }
    }
}
