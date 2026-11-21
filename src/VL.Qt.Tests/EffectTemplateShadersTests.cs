using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Core.Diagnostics;
using VL.Qt.Elements;
using VL.Qt.Shaders;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    /// <summary>
    /// The annotated template shaders help/Qt/qtshaders/Example*.frag (#8): the same code as the built-in effect shaders of
    /// QtNative, and the effect chain node the shader node factory makes of each.
    /// </summary>
    public class EffectTemplateShadersTests
    {
        // Template file (without .frag), built-in shader (without .frag).
        private static readonly (string Template, string Builtin)[] s_pairs =
        {
            ("ExampleBlend", "blend"),
            ("ExampleDisplace", "displace"),
            ("ExampleDirectionalBlur", "directional_blur"),
            ("ExampleZoomBlur", "zoom_blur"),
            ("ExampleRadialBlur", "radial_blur"),
        };

        private static readonly Regex s_comments = new(@"//[^\r\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly Regex s_whitespace = new(@"\s+", RegexOptions.CultureInvariant);

        private static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "help", "Qt", "qtshaders"))
                    && Directory.Exists(Path.Combine(dir.FullName, "src", "VL.Qt.Native", "shaders")))
                    return dir.FullName;
            }
            throw new InvalidOperationException("Repository root (help/Qt/qtshaders and src/VL.Qt.Native/shaders) not found above " + AppContext.BaseDirectory);
        }

        private static string TemplatePath(string template) => Path.Combine(RepoRoot(), "help", "Qt", "qtshaders", template + ".frag");

        private static string BuiltinPath(string builtin) => Path.Combine(RepoRoot(), "src", "VL.Qt.Native", "shaders", builtin + ".frag");

        /// <summary>The code of a shader: without // and /* */ comments and without any whitespace.</summary>
        private static string Code(string path) => s_whitespace.Replace(s_comments.Replace(File.ReadAllText(path), string.Empty), string.Empty);

        [TestCaseSource(nameof(s_pairs))]
        public void Template_HasTheCodeOfTheBuiltinShader((string Template, string Builtin) pair)
        {
            var template = Code(TemplatePath(pair.Template));
            var builtin = Code(BuiltinPath(pair.Builtin));
            Assert.That(builtin, Is.Not.Empty);

            var at = 0;
            while (at < template.Length && at < builtin.Length && template[at] == builtin[at])
                at++;
            Assert.That(template, Is.EqualTo(builtin),
                $"help/Qt/qtshaders/{pair.Template}.frag has drifted from src/VL.Qt.Native/shaders/{pair.Builtin}.frag: the code (everything but comments and whitespace) must be the same, " +
                "so both files must change together. First difference at character " + at + ": template '" + Excerpt(template, at) + "' / built-in '" + Excerpt(builtin, at) + "'.");
        }

        private static string Excerpt(string code, int at) => code.Substring(Math.Min(at, code.Length), Math.Min(40, Math.Max(0, code.Length - at)));

        // --- the effect chain nodes the factory makes of the templates ---

        // Pins of the effect chain node (Input first, the sampler pin of an image second, shown only when needed).
        private static readonly (string Template, string[] Pins, bool TwoPass)[] s_nodes =
        {
            ("ExampleBlend", new[] { "Input", "Layer", "Layer Sampler", "Mode", "Layer Opacity" }, false),
            ("ExampleDisplace", new[] { "Input", "Map", "Map Sampler", "Displacement" }, false),
            ("ExampleDirectionalBlur", new[] { "Input", "Angle", "Blur Length" }, true),
            ("ExampleZoomBlur", new[] { "Input", "Center", "Blur Length" }, true),
            ("ExampleRadialBlur", new[] { "Input", "Center", "Angle" }, true),
        };

        private string _dir = null!;
        private FakeResourceApi _api = null!;
        private ResourceClient _client = null!;

        [SetUp]
        public void CreateDirAndClient()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vlqt-templates-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _api = new FakeResourceApi();
            _client = new ResourceClient(_api, Environment.CurrentManagedThreadId, static () => null);
        }

        [TearDown]
        public void DeleteDir() => Directory.Delete(_dir, recursive: true);

        [Test]
        public void Factory_MakesOneEffectChainNodeEach_NoElementNode()
        {
            foreach (var pair in s_pairs)
                File.Copy(TemplatePath(pair.Template), Path.Combine(_dir, pair.Template + ".frag"));

            var files = ShaderNodeFactory.Scan(_dir);
            Assert.That(files.Select(f => f.IsFilter), Is.All.True);
            Assert.That(files.Select(f => f.ChainOnly), Is.All.True);

            var nodes = ShaderNodeFactory.CreateDescriptions(files, new FakeNodeFactory(), NullLogger.Instance);
            Assert.That(nodes.Select(n => (n.Name, n.Category)), Is.EquivalentTo(s_pairs.Select(p => (p.Template, "Qt.Effects.Examples"))),
                "@category of a @chainonly file is the category of its chain node");
        }

        [TestCaseSource(nameof(s_nodes))]
        public void Template_ChainNodeHasThePinsOfTheUniforms((string Template, string[] Pins, bool TwoPass) node)
        {
            ShaderReflectNativeTests.LinkNative();
            var layout = LoadChainLayout(node.Template);

            Assert.That(layout.Messages.Where(m => m.Type == MessageType.Error).Select(m => m.Text), Is.Empty);
            Assert.That(ShaderEffectDescription.Inputs(layout).Select(p => p.Name), Is.EqualTo(node.Pins));
            Assert.That(layout.Pins.Select(p => p.UniformName), Has.None.AnyOf("texelSize", "dpr", "passIndex", "elementRect", "weight"),
                "VL.Qt sets these itself, they get no pin");
            Assert.That(layout.Passes, Is.EqualTo(node.TwoPass ? 2 : 1));
        }

        [TestCaseSource(nameof(s_nodes))]
        public void Template_LinkUsesTheTemplateOfTheNode((string Template, string[] Pins, bool TwoPass) node)
        {
            ShaderReflectNativeTests.LinkNative();
            using var instance = new ShaderEffectInstance(_client, LoadChainLayout(node.Template));
            instance.Update();

            var link = (ShaderEffect)instance.Outputs.First().Value!;
            Assert.That(link.Template, Is.EqualTo(node.TwoPass ? Protocol.EffectTemplate.TwoPass : Protocol.EffectTemplate.Single));
        }

        // Padding at the defaults, in DIP: Blend none; Displace 10; DirectionalBlur 0.52 * 10 (the built-in node: 6 and 0 at angle 0,
        // the tag has one number for all sides); ZoomBlur 0.6 * 40, as the built-in node; RadialBlur 3.75 * 8 = the built-in default.
        [TestCase("ExampleBlend", 0f)]
        [TestCase("ExampleDisplace", 10f)]
        [TestCase("ExampleDirectionalBlur", 5.2f)]
        [TestCase("ExampleZoomBlur", 24f)]
        [TestCase("ExampleRadialBlur", 30f)]
        public void Template_PaddingAtTheDefaults(string template, float dip)
        {
            ShaderReflectNativeTests.LinkNative();
            using var instance = new ShaderEffectInstance(_client, LoadChainLayout(template));
            instance.Update();

            var padding = ((ShaderEffect)instance.Outputs.First().Value!).Padding;
            Assert.That(padding, Is.EqualTo(new RectangleF(dip, dip, dip, dip)).Using<RectangleF>(static (a, b) =>
                MathF.Abs(a.X - b.X) < 1e-4f && MathF.Abs(a.Y - b.Y) < 1e-4f && MathF.Abs(a.Width - b.Width) < 1e-4f && MathF.Abs(a.Height - b.Height) < 1e-4f));
        }

        private ShaderNodeLayout LoadChainLayout(string template)
        {
            var path = Path.Combine(_dir, template + ".frag");
            File.Copy(TemplatePath(template), path, overwrite: true);
            var file = ShaderNodeFactory.Scan(_dir).Single(f => f.Name == template);
            return ShaderNodeLayout.Load(file, ShaderReflection.Reflect, NullLogger.Instance).ForChain(NullLogger.Instance, path);
        }

        private sealed class FakeNodeFactory : VL.Core.IVLNodeDescriptionFactory
        {
            public string Identifier => "Test";
            public System.Collections.Immutable.ImmutableArray<VL.Core.IVLNodeDescription> NodeDescriptions => default;
            public IObservable<object> Invalidated => System.Reactive.Linq.Observable.Never<object>();
            public VL.Core.IVLNodeDescriptionFactory? ForPath(string path) => null;
            public void Export(VL.Core.ExportContext exportContext) { }
        }
    }
}
