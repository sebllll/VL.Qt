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

namespace VL.Qt.Tests
{
    /// <summary>Shader node factory: names, scan of qtshaders/, layout of a node, uniform spread. No QtNative, no vvvv.</summary>
    public class ShaderNodeFactoryTests
    {
        private const string fragmentSource = @"// @category Qt.Test
// @summary Splits the channels.
#version 440
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    float amount; // @default 0.5 @max 1
    vec2 size;
    vec4 tint; // @color
};
layout(binding = 1) uniform sampler2D source;
void main() { fragColor = texture(source, qt_TexCoord0) * tint * amount * qt_Opacity; }
";

        private const string reflection = "ok\nuniform qt_Matrix mat4 0\nuniform qt_Opacity float 0\nuniform amount float 0\nuniform size vec2 0\nuniform tint vec4 0\nsampler source 1\n";

        private string _dir = null!;

        [SetUp]
        public void CreateDir()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vlqt-shadernodes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void DeleteDir() => Directory.Delete(_dir, recursive: true);

        private string Write(string relativePath, string content)
        {
            var path = Path.Combine(_dir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        // --- names ---

        [Test]
        public void FullName_HasVariantInParentheses()
        {
            var f = new ShaderFile("x", null, "Glitch", "RGB", ShaderNodeFactory.DefaultCategory, null, null);
            Assert.That(f.FullName, Is.EqualTo("Glitch (RGB)"));
            Assert.That((f with { Version = null }).FullName, Is.EqualTo("Glitch"));
        }

        // --- scan ---

        [Test]
        public void Scan_PairsVertex_ReadsHead_MarksDuplicates()
        {
            Write("Glitch_RGB.frag", fragmentSource);
            Write("Glitch_RGB.vert", "#version 440\n");
            Write("Lonely.vert", "#version 440\n");
            Write("notes.txt", "x");
            Write("Plain.frag", "#version 440\nvoid main() {}\n");
            var a = Write("Twice.frag", "#version 440\n");
            var b = Write(Path.Combine("sub", "deeper", "Twice.frag"), "#version 440\n");

            var files = ShaderNodeFactory.Scan(_dir);

            Assert.That(files.Select(f => f.FullName), Is.EquivalentTo(new[] { "Glitch (RGB)", "Plain", "Twice", "Twice" }));
            var glitch = files.Single(f => f.Name == "Glitch");
            Assert.That(glitch.VertexPath, Is.EqualTo(Path.Combine(_dir, "Glitch_RGB.vert")));
            Assert.That(glitch.Category, Is.EqualTo("Qt.Test"));
            Assert.That(glitch.Summary, Is.EqualTo("Splits the channels."));
            Assert.That(glitch.DuplicateError, Is.Null);

            var plain = files.Single(f => f.Name == "Plain");
            Assert.That(plain.VertexPath, Is.Null);
            Assert.That(plain.Category, Is.EqualTo("Qt.Elements.Shaders"));

            foreach (var twice in files.Where(f => f.Name == "Twice"))
            {
                Assert.That(twice.DuplicateError, Does.Contain(a));
                Assert.That(twice.DuplicateError, Does.Contain(b));
            }
        }

        [Test]
        public void Watcher_CountsOnlyShaderFiles()
        {
            static FileSystemEventArgs E(WatcherChangeTypes t, string name) => new(t, "c:\\x", name);
            Assert.That(ShaderNodeFactory.IsNewOrDeletedShaderFile(E(WatcherChangeTypes.Created, "a.frag")));
            Assert.That(ShaderNodeFactory.IsNewOrDeletedShaderFile(E(WatcherChangeTypes.Deleted, "sub\\a.vert")));
            Assert.That(ShaderNodeFactory.IsNewOrDeletedShaderFile(E(WatcherChangeTypes.Created, "a.frag~tmp")), Is.False);
            Assert.That(ShaderNodeFactory.IsNewOrDeletedShaderFile(E(WatcherChangeTypes.Changed, "a.frag")), Is.False);
            Assert.That(ShaderNodeFactory.IsNewOrDeletedShaderFile(new RenamedEventArgs(WatcherChangeTypes.Renamed, "c:\\x", "a.frag", "New.txt")));
            Assert.That(ShaderNodeFactory.IsNewOrDeletedShaderFile(new RenamedEventArgs(WatcherChangeTypes.Renamed, "c:\\x", "a.bak", "a.frag")));
            Assert.That(ShaderNodeFactory.IsNewOrDeletedShaderFile(new RenamedEventArgs(WatcherChangeTypes.Renamed, "c:\\x", "a.bak", "a.tmp")), Is.False);
        }

        // --- layout (fake reflection) ---

        private ShaderNodeLayout Load(string source, string reflectionText)
        {
            var path = Write("Test.frag", source);
            var file = new ShaderFile(path, null, "Test", null, ShaderNodeFactory.DefaultCategory, null, null);
            return ShaderNodeLayout.Load(file, (_, _) => ShaderReflection.Parse(reflectionText), NullLogger.Instance);
        }

        [Test]
        public void Layout_PinsFromModel_TakenNameGetsNumber()
        {
            var layout = Load(fragmentSource, reflection);

            Assert.That(layout.HasInput);
            Assert.That(layout.Pins.Select(p => p.Name), Is.EqualTo(new[] { "Amount", "Size 2", "Tint" }));
            Assert.That(layout.Pins[1].UniformName, Is.EqualTo("size"));
            Assert.That(layout.Summary, Is.EqualTo("Splits the channels."));
            Assert.That(layout.FragmentSource, Is.EqualTo(fragmentSource));
            var warning = layout.Messages.Single();
            Assert.That(warning.Type, Is.EqualTo(MessageType.Warning));
            Assert.That(warning.Text, Does.Contain("'size'").And.Contain("Size 2"));
        }

        [Test]
        public void Layout_CompileError_OnlyFixedPins_KeepsInput()
        {
            var layout = Load(fragmentSource, "error\nfragment code, line 9: 'amount' : syntax error\n");

            Assert.That(layout.Pins, Is.Empty);
            Assert.That(layout.HasInput, "Input survives a typo (regex on the source)");
            var error = layout.Messages.Single();
            Assert.That(error.Type, Is.EqualTo(MessageType.Error));
            Assert.That(error.Text, Does.Contain("line 9"));
        }

        [Test]
        public void Layout_ReflectThrows_IsNodeError()
        {
            var path = Write("Test.frag", "#version 440\n");
            var file = new ShaderFile(path, null, "Test", null, ShaderNodeFactory.DefaultCategory, null, null);
            var layout = ShaderNodeLayout.Load(file, (_, _) => throw new DllNotFoundException("vlqt.dll"), NullLogger.Instance);
            Assert.That(layout.Messages.Single().Type, Is.EqualTo(MessageType.Error));
            Assert.That(layout.HasInput, Is.False);
        }

        [Test]
        public void Layout_MissingFile_IsNodeError()
        {
            var file = new ShaderFile(Path.Combine(_dir, "Gone.frag"), null, "Gone", null, ShaderNodeFactory.DefaultCategory, "s", "dup");
            var layout = ShaderNodeLayout.Load(file, (_, _) => throw new InvalidOperationException("not reached"), NullLogger.Instance);
            Assert.That(layout.Messages.Select(m => m.Type), Is.EqualTo(new[] { MessageType.Error, MessageType.Error }));
            Assert.That(layout.Messages[0].Text, Is.EqualTo("dup"));
            Assert.That(layout.Pins, Is.Empty);
        }

        // --- uniforms ---

        [Test]
        public void Uniforms_FromDefaults_RebuiltOnlyOnChange()
        {
            var layout = Load(fragmentSource, reflection);
            var uniforms = new ShaderUniforms(layout.Pins);

            var first = uniforms.Update();
            Assert.That(first.Select(u => u.Name), Is.EqualTo(new[] { "amount", "size", "tint" }));
            Assert.That(first[0].Numbers.M11, Is.EqualTo(0.5f));
            Assert.That(first[2].ValueType, Is.EqualTo(Protocol.ValueType.Color));
            Assert.That(first[2].Numbers.M14, Is.EqualTo(1f), "@color without @default is white");
            Assert.That(uniforms.Update(), Is.SameAs(first), "nothing changed");

            ((IVLPin<float>)uniforms.Pins[0]).Value = 0.5f;
            Assert.That(uniforms.Update(), Is.SameAs(first), "same value");

            ((IVLPin<Vector2>)uniforms.Pins[1]).Value = new Vector2(3, 4);
            var second = uniforms.Update();
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second[1].Numbers.M12, Is.EqualTo(4f));
            Assert.That(second[0], Is.SameAs(first[0]), "unchanged uniforms are reused");
        }

        [Test]
        public void Instance_InputPinsMatchDescriptionOrder()
        {
            var layout = Load(fragmentSource, reflection);
            using var instance = new ShaderNodeInstance(new Nodes.ShaderNode(_ => System.Reactive.Disposables.Disposable.Empty), layout);
            // Same count, order and type as the description (Input + Input Sampler + 3 uniforms + 12 fixed pins; 4 outputs).
            Assert.That(instance.Inputs.Select(PinType), Is.EqualTo(ShaderNodeDescription.Inputs(layout).Select(d => d.Type)));
            Assert.That(instance.Outputs.Select(PinType), Is.EqualTo(ShaderNodeDescription.Outputs.Select(d => d.Type)));
            Assert.That(ShaderNodeDescription.Inputs(layout).Count, Is.EqualTo(1 + 1 + 3 + 12));
            Assert.That(ShaderNodeDescription.Inputs(layout).Select(d => d.Name).Take(5), Is.EqualTo(new[] { "Input", "Input Sampler", "Amount", "Size 2", "Tint" }));

            static Type PinType(IVLPin pin) => pin.GetType().GetInterfaces()
                .Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IVLPin<>)).GetGenericArguments()[0];
        }

        [Test]
        public void Description_IsNotFragmented_SoVvvvCallsUpdate()
        {
            // vvvv calls IVLNode.Update only for non-fragmented descriptions; fragmented ones never update and Output stays null.
            var path = Write("Glitch.frag", fragmentSource);
            var file = new ShaderFile(path, null, "Glitch", null, ShaderNodeFactory.DefaultCategory, null, null);
            var description = ShaderNodeDescription.New(new FakeFactory(), file, NullLogger.Instance);
            Assert.That(description.Fragmented, Is.False);
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
