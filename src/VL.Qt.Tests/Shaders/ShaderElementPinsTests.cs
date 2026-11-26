using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using NUnit.Framework;
using VL.Core.Diagnostics;
using VL.Qt.Nodes;
using VL.Qt.NodeFactories;
using VL.Qt.Shaders;
using VL.Qt.Tests.Support;

namespace VL.Qt.Tests.Shaders
{
    /// <summary>The fixed pins of the generated shader nodes (<see cref="ShaderElementPins"/>) are those of Shader, and each node
    /// makes its uniform pins unique against its own pins.</summary>
    [TestFixture]
    public sealed class ShaderElementPinsTests
    {
        private static readonly MethodInfo s_update = typeof(ShaderNode).GetMethod(nameof(ShaderNode.Update))!;

        private static ParameterInfo Parameter(string pin) =>
            s_update.GetParameters().Single(p => PinReflection.PinName(p) == pin);

        private static ShaderElementPin[] InputPins =>
            new[] { ShaderElementPins.Input, ShaderElementPins.InputSampler }.Concat(ShaderElementPins.FixedInputs).ToArray();

        [Test]
        public void Inputs_AreTheInputsOfShader()
        {
            foreach (var pin in InputPins)
            {
                var p = Parameter(pin.Name);
                var type = p.ParameterType;
                // Mesh Resolution is an Optional<Int2> on Shader (unconnected = 1, 1), an Int2 with its default on a generated node.
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(VL.Core.Optional<>))
                    type = type.GetGenericArguments()[0];
                else
                    Assert.That(pin.Default, Is.EqualTo(PinReflection.DefaultOf(p)), $"default of {pin.Name}");
                Assert.That(pin.Type, Is.EqualTo(type), $"type of {pin.Name}");
                // The one difference: a generated node shows Blending, Shader keeps it optional.
                if (pin.Name != "Blending")
                    Assert.That(pin.IsVisible, Is.EqualTo(PinReflection.IsVisible(p)), $"visibility of {pin.Name}");
            }
        }

        [Test]
        public void Outputs_AreTheOutputsOfShader_WithoutError()
        {
            var outputs = s_update.GetParameters().Where(p => p.IsOut).ToList();
            foreach (var pin in ShaderElementPins.TrailingOutputs)
            {
                var p = outputs.Single(x => PinReflection.PinName(x) == pin.Name);
                Assert.That(p.ParameterType.GetElementType(), Is.EqualTo(pin.Type), $"type of {pin.Name}");
                Assert.That(PinReflection.IsVisible(p), Is.EqualTo(pin.IsVisible), $"visibility of {pin.Name}");
            }
            Assert.That(s_update.ReturnParameter.GetCustomAttribute<VL.Core.Import.PinAttribute>()!.Name, Is.EqualTo(ShaderElementPins.Output.Name));
            Assert.That(outputs.Select(PinReflection.PinName), Is.EqualTo(ShaderElementPins.TrailingOutputs.Select(p => p.Name)), "no Error output");
            Assert.That(ShaderDescription.Outputs.Select(o => o.Name), Is.EqualTo(new[] { "Output", "Measured Size", "Scene Bounds" }));
        }

        [Test]
        public void Order_FollowsShader()
        {
            var names = s_update.GetParameters().Where(p => !p.IsOut).Select(PinReflection.PinName).ToList();
            Assert.That(names.Skip(names.IndexOf("Blending")), Is.EqualTo(ShaderElementPins.FixedInputs.Select(p => p.Name)));
            Assert.That(names.Take(2), Is.EqualTo(new[] { "Input", "Input Sampler" }));
        }

        [Test]
        public void Help_IsTheHelpOfShader()
        {
            var xml = Directory.GetFiles(AppContext.BaseDirectory, "VL.Qt.xml").SingleOrDefault();
            if (xml is null)
                Assert.Inconclusive("VL.Qt.xml (the XML documentation) is not next to the test assembly.");
            var member = XDocument.Load(xml).Descendants("member")
                .Single(m => ((string?)m.Attribute("name") ?? "").StartsWith("M:VL.Qt.Nodes.ShaderNode.Update(", StringComparison.Ordinal));
            string Doc(ParameterInfo p) => string.Join(' ', member.Elements("param").Single(e => (string?)e.Attribute("name") == p.Name).Value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            foreach (var pin in InputPins.Concat(ShaderElementPins.TrailingOutputs))
                Assert.That(pin.Summary, Is.EqualTo(Doc(Parameter(pin.Name))), $"help of {pin.Name}");
        }

        [Test]
        public void ReservedNames_AreAllPinsBesidesTheUniformPins()
        {
            Assert.That(ShaderElementPins.Names, Is.EquivalentTo(new[]
            {
                "Input", "Input Sampler", "Blending", "Mesh Resolution", "Auto Size", "Size", "Transform", "Anchor", "Opacity", "Clip", "Debug",
                "Interaction", "Effect", "Enabled", "Output", "Measured Size", "Scene Bounds",
            }));
            Assert.That(ShaderEffectLayout.ReservedPinNames, Is.EquivalentTo(new[] { "Input", "Apply", "Output" }));
        }

        // --- unique pin names, each node against its own pins ---

        private const string source = @"#version 440
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    float size;
    float inputSampler;
    float apply;
};
layout(binding = 1) uniform sampler2D source;
void main() { fragColor = texture(source, qt_TexCoord0) * size * inputSampler * apply; }
";

        private const string reflection = "ok\nuniform qt_Matrix mat4 0\nuniform qt_Opacity float 0\nuniform size float 0\nuniform inputSampler float 0\nuniform apply float 0\nsampler source 1\n";

        private string _dir = null!;

        [SetUp]
        public void CreateDir()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vlqt-shaderpins-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void DeleteDir() => Directory.Delete(_dir, recursive: true);

        private (ShaderSource Source, ShaderFile File) Load(string text, string reflectionText)
        {
            var path = Path.Combine(_dir, "Test.frag");
            File.WriteAllText(path, text);
            var file = new ShaderFile(path, null, "Test", null, ShaderNodeFactory.DefaultCategory, null, null);
            return (ShaderSource.Load(file, (_, _) => ShaderReflection.Parse(reflectionText)), file);
        }

        [Test]
        public void ElementNode_UniqueAgainstItsFixedPins_IncludingInputSampler()
        {
            var (s, file) = Load(source, reflection);
            var layout = ShaderLayout.From(s, file);
            Assert.That(layout.Pins.Select(p => p.Name), Is.EqualTo(new[] { "Size 2", "Input Sampler 2", "Apply" }));
            Assert.That(layout.Messages.Count(m => m.Type == MessageType.Warning), Is.EqualTo(2));
        }

        [Test]
        public void ElementNode_WithoutInput_InputNamesAreFree()
        {
            const string text = "#version 440\nlayout(std140, binding = 0) uniform buf { mat4 qt_Matrix; float qt_Opacity; float input; float inputSampler; };\nvoid main() {}\n";
            const string refl = "ok\nuniform qt_Matrix mat4 0\nuniform qt_Opacity float 0\nuniform input float 0\nuniform inputSampler float 0\n";
            var (s, file) = Load(text, refl);
            Assert.That(ShaderLayout.From(s, file).Pins.Select(p => p.Name), Is.EqualTo(new[] { "Input", "Input Sampler" }));
        }

        [Test]
        public void EffectNode_UniqueAgainstItsOwnPins_SizeStaysSize()
        {
            var (s, file) = Load(source, reflection);
            Assert.That(ShaderEffectLayout.From(s, file).Pins.Select(p => p.Name), Is.EqualTo(new[] { "Size", "Input Sampler", "Apply 2" }));
        }

        [Test]
        public void SamplerPin_FollowsTheFinalNameOfItsImagePin()
        {
            const string text = "#version 440\nlayout(std140, binding = 0) uniform buf { mat4 qt_Matrix; float qt_Opacity; };\nlayout(binding = 1) uniform sampler2D source;\nlayout(binding = 2) uniform sampler2D size;\nvoid main() {}\n";
            const string refl = "ok\nuniform qt_Matrix mat4 0\nuniform qt_Opacity float 0\nsampler source 1\nsampler size 2\n";
            var (s, file) = Load(text, refl);
            var element = ShaderLayout.From(s, file);
            Assert.That(element.Pins.Select(p => p.Name), Is.EqualTo(new[] { "Size 2", "Size 2 Sampler" }));
            Assert.That(element.Pins.Select(p => p.Type), Is.EqualTo(new[] { ShaderPinType.Image, ShaderPinType.Sampler }));
            Assert.That(ShaderEffectLayout.From(s, file).Pins.Select(p => p.Name), Is.EqualTo(new[] { "Size", "Size Sampler" }));
        }
    }
}
