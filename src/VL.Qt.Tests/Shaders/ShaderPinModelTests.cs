using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Shaders;

namespace VL.Qt.Tests
{
    /// <summary>Shader node factory: reflection text parser, annotations, pin rules. No QtNative needed.</summary>
    public class ShaderPinModelTests
    {
        private const string head = "ok\nuniform qt_Matrix mat4 0\nuniform qt_Opacity float 0\n";

        private static ShaderReflection R(string body) => ShaderReflection.Parse(head + body);

        private static ShaderPinModel Build(string fragReflection, string fragSource, string? vertReflection = null, string? vertSource = null) =>
            ShaderPinModel.Build(
                R(fragReflection),
                vertReflection is null ? null : R(vertReflection),
                ShaderAnnotations.Parse(fragSource),
                vertSource is null ? null : ShaderAnnotations.Parse(vertSource));

        // --- reflection text ---

        [Test]
        public void Reflection_ParsesUniformsAndSamplers()
        {
            var r = ShaderReflection.Parse("ok\nuniform qt_Matrix mat4 0\nuniform amount float 0\nuniform cols vec4 3\nsampler source 1\nsampler mask 2\n");
            Assert.That(r.Ok);
            Assert.That(r.Uniforms.Select(u => u.Name), Is.EqualTo(new[] { "qt_Matrix", "amount", "cols" }));
            Assert.That(r.Uniforms[2], Is.EqualTo(new ReflectedUniform("cols", "vec4", 3)));
            Assert.That(r.Samplers, Is.EqualTo(new[] { new ReflectedSampler("source", 1), new ReflectedSampler("mask", 2) }));
        }

        [Test]
        public void Reflection_Error_KeepsLinesAndHasNoUniforms()
        {
            var r = ShaderReflection.Parse("error\nfragment code, line 7: 'x' : undeclared identifier\r\nfragment code, line 8: oops\n");
            Assert.That(r.Ok, Is.False);
            Assert.That(r.Error, Is.EqualTo("fragment code, line 7: 'x' : undeclared identifier\nfragment code, line 8: oops"));
            Assert.That(r.Uniforms, Is.Empty);
        }

        [Test]
        public void Reflection_EmptyOrGarbage_Fails()
        {
            Assert.That(ShaderReflection.Parse("").Ok, Is.False);
            Assert.That(ShaderReflection.Parse("hello").Ok, Is.False);
        }

        [Test]
        public void Reflection_IgnoresMalformedLines()
        {
            var r = ShaderReflection.Parse("ok\nuniform a float\nuniform b float x\nfoo bar\nuniform c int 0\n");
            Assert.That(r.Uniforms.Select(u => u.Name), Is.EqualTo(new[] { "c" }));
        }

        // --- annotations ---

        [Test]
        public void Annotations_FileHead_BeforeAndAfterVersion()
        {
            var a = ShaderAnnotations.Parse("// @category Qt.Effects\n#version 440\n// @summary Splits the channels\n// and shifts them.\n\nlayout(location = 0) in vec2 qt_TexCoord0;\n// @summary not head\n");
            Assert.That(a.Category, Is.EqualTo("Qt.Effects"));
            Assert.That(a.Summary, Is.EqualTo("Splits the channels and shifts them."));
        }

        [Test]
        public void Annotations_TrailingAndAbove()
        {
            var src = @"#version 440
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    // @summary Amount in pixels
    // continued here
    // @min 0
    float amount; // @max 100 @default 5
    vec4 tint; // @color
    float plain;
};
";
            var a = ShaderAnnotations.Parse(src);
            var u = a.Uniforms["amount"];
            Assert.That(u.Summary, Is.EqualTo("Amount in pixels continued here"));
            Assert.That(u.Min, Is.EqualTo("0"));
            Assert.That(u.Max, Is.EqualTo("100"));
            Assert.That(u.Default, Is.EqualTo("5"));
            Assert.That(u.Line, Is.EqualTo(8));
            Assert.That(a.Uniforms["tint"].IsColor);
            Assert.That(a.Uniforms.ContainsKey("plain"), Is.False);
            Assert.That(a.Warnings, Is.Empty);
        }

        [Test]
        public void Annotations_BlankLineDetachesCommentFromUniform()
        {
            var src = "#version 440\nlayout(std140, binding = 0) uniform buf {\n    float a;\n    // @min 1\n\n    float b;\n};\n";
            var a = ShaderAnnotations.Parse(src);
            Assert.That(a.Uniforms.ContainsKey("b"), Is.False);
            Assert.That(a.Warnings.Single().Line, Is.EqualTo(4));
        }

        [Test]
        public void Annotations_FileHead_TagsAndRemarks()
        {
            var a = ShaderAnnotations.Parse("// @tags rgb, split,\n// glitch ,, \n// @remarks First line\n// second line.\n#version 440\n");
            Assert.That(a.Tags, Is.EqualTo(new[] { "rgb", "split", "glitch" }));
            Assert.That(a.Remarks, Is.EqualTo("First line second line."));
            Assert.That(a.Warnings, Is.Empty);
        }

        [Test]
        public void Annotations_TagsAndRemarksAtUniform_Warn_OptionalInHead_Warns()
        {
            var a = ShaderAnnotations.Parse("// @optional\n#version 440\nlayout(std140, binding = 0) uniform buf {\n    float a; // @tags x\n};\n");
            Assert.That(a.Warnings.Select(w => w.Line), Is.EqualTo(new[] { 1, 4 }));
        }

        [Test]
        public void Annotations_Optional_AtUniform()
        {
            var a = ShaderAnnotations.Parse("#version 440\nlayout(std140, binding = 0) uniform buf {\n    // @optional\n    float a;\n    float b; // @optional @min 1\n    float c;\n};\n");
            Assert.That(a.Warnings, Is.Empty);
            Assert.That(a.Uniforms["a"].IsOptional, Is.True);
            Assert.That(a.Uniforms["b"].IsOptional, Is.True);
            Assert.That(a.Uniforms["b"].Min, Is.EqualTo("1"));
            Assert.That(a.Uniforms.ContainsKey("c"), Is.False);
        }

        [Test]
        public void Pins_Optional_MarksImageAndUniformPins_PinNamesSplitDigits()
        {
            var m = Build("uniform pass1 float 0\nuniform b float 0\nsampler lut 2\n", blockStart + "    float pass1; // @optional\n    float b;\n};\nlayout(binding = 2) uniform sampler2D lut; // @optional\n");
            Assert.That(m.Pins.Where(p => p.Type != ShaderPinType.Sampler).Select(p => (p.Name, p.IsOptional)), Is.EqualTo(new[] { ("Lut", true), ("Pass 1", true), ("B", false) }));
        }

        [Test]
        public void Annotations_UnknownTag_WarnsWithLine()
        {
            var a = ShaderAnnotations.Parse("#version 440\nlayout(std140, binding = 0) uniform buf {\n    float a; // @foo 1\n};\n");
            Assert.That(a.Warnings.Single().Line, Is.EqualTo(3));
            Assert.That(a.Warnings.Single().Text, Does.Contain("@foo"));
        }

        [Test]
        public void Annotations_UniformTagInHead_Warns()
        {
            var a = ShaderAnnotations.Parse("// @min 3\n#version 440\n");
            Assert.That(a.Warnings.Single().Line, Is.EqualTo(1));
        }

        [Test]
        public void Annotations_BoolRegex_OnlyInUniformBlock()
        {
            var src = @"#version 440
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    bool scanlines;
    highp bool flip; // @default true
    float x;
};
void main() {
    bool local;
}
";
            var a = ShaderAnnotations.Parse(src);
            Assert.That(a.DeclaredTypes.Where(d => d.Value == "bool").Select(d => d.Key), Is.EquivalentTo(new[] { "scanlines", "flip" }));
            Assert.That(a.DeclaredTypes["x"], Is.EqualTo("float"));
        }

        [Test]
        public void Annotations_SamplerDeclaration_CanCarrySummary()
        {
            var a = ShaderAnnotations.Parse("#version 440\nlayout(binding = 1) uniform sampler2D mask; // @summary Mask image\n");
            Assert.That(a.Uniforms["mask"].Summary, Is.EqualTo("Mask image"));
        }

        [Test]
        public void Annotations_CRLF_Works()
        {
            var a = ShaderAnnotations.Parse("// @category A.B\r\n#version 440\r\nlayout(std140, binding = 0) uniform buf {\r\n    float a; // @max 2\r\n};\r\n");
            Assert.That(a.Category, Is.EqualTo("A.B"));
            Assert.That(a.Uniforms["a"].Max, Is.EqualTo("2"));
        }

        // --- pin model ---

        private const string blockStart = "#version 440\nlayout(std140, binding = 0) uniform buf {\n    mat4 qt_Matrix;\n    float qt_Opacity;\n";

        [Test]
        public void Pins_FiltersQtAndSource_SetsHasInput()
        {
            var m = Build("uniform amount float 0\nsampler source 1\n", blockStart + "    float amount;\n};\n");
            Assert.That(m.HasInput);
            Assert.That(m.Pins.Select(p => p.UniformName), Is.EqualTo(new[] { "amount" }));
        }

        [Test]
        public void Pins_Generator_HasNoInput()
        {
            var m = Build("uniform phase float 0\n", blockStart + "    float phase;\n};\n");
            Assert.That(m.HasInput, Is.False);
        }

        [Test]
        public void Pins_ImagesFirst_ThenUniforms_InOrder()
        {
            var m = Build("uniform b float 0\nuniform a vec2 0\nsampler source 1\nsampler lutImage 2\n", "");
            Assert.That(m.Pins.Select(p => (p.UniformName, p.Type)), Is.EqualTo(new[]
            {
                ("lutImage", ShaderPinType.Image), ("lutImage", ShaderPinType.Sampler), ("b", ShaderPinType.Float), ("a", ShaderPinType.Vector2),
            }));
            Assert.That(m.Pins[0].Name, Is.EqualTo("Lut Image"));
            Assert.That(m.Pins[0].Default, Is.Null);
        }

        [Test]
        public void Pins_TypeRules()
        {
            var src = blockStart + "    vec3 v3;\n    vec4 v4;\n    vec4 col; // @color\n    int count;\n    bool on;\n    mat4 m;\n};\n";
            var m = Build("uniform v3 vec3 0\nuniform v4 vec4 0\nuniform col vec4 0\nuniform count int 0\nuniform on uint 0\nuniform m mat4 0\n", src);
            Assert.That(m.Pins.Select(p => p.Type), Is.EqualTo(new[]
            {
                ShaderPinType.Vector3, ShaderPinType.Vector4, ShaderPinType.Color, ShaderPinType.Int, ShaderPinType.Bool, ShaderPinType.Matrix,
            }));
            Assert.That(m.Warnings, Is.Empty);
        }

        [Test]
        public void Pins_UintWithoutBoolDeclaration_Skipped()
        {
            var m = Build("uniform u uint 0\nuniform iv ivec2 0\nuniform m3 mat3 0\nuniform ok float 0\n", blockStart + "    uint u;\n    float ok;\n};\n");
            Assert.That(m.Pins.Select(p => p.UniformName), Is.EqualTo(new[] { "ok" }));
            Assert.That(m.Warnings.Count, Is.EqualTo(3));
        }

        [Test]
        public void Pins_UnsupportedType_MessageNamesTheDeclaredType()
        {
            // QtNative reflects uvec2 as "other"; the message shows what the file declares.
            var m = Build("uniform offsets other 0\nuniform m3 mat3 0\n", blockStart + "    uvec2 offsets;\n    mat3 m3;\n};\n");
            Assert.That(m.Warnings[0].Text, Does.Contain("type 'uvec2' is not supported").And.Contain("use float, vec2, vec3, vec4, int, bool or mat4"));
            Assert.That(m.Warnings[1].Text, Does.Contain("type 'mat3' is not supported"));
        }

        [Test]
        public void Pins_Array_SkippedWithWarning()
        {
            var m = Build("uniform cols vec4 4\nuniform ok float 0\n", "");
            Assert.That(m.Pins.Select(p => p.UniformName), Is.EqualTo(new[] { "ok" }));
            Assert.That(m.Warnings.Single().Text, Does.Contain("cols").And.Contain("array"));
        }

        [Test]
        public void Pins_VertAndFrag_MergeSameNameOnce()
        {
            var m = Build(
                "uniform amount float 0\nuniform shift vec2 0\nsampler source 1\n",
                blockStart + "    float amount;\n    vec2 shift; // @default 1, 2\n};\n",
                "uniform amount float 0\nuniform wobble float 0\n",
                blockStart + "    float amount; // @default 3\n    float wobble;\n};\n");
            Assert.That(m.Pins.Select(p => p.UniformName), Is.EqualTo(new[] { "amount", "shift", "wobble" }));
            // frag has no annotation on amount, vert's applies
            Assert.That(m.Pins[0].Default, Is.EqualTo(3f));
            Assert.That(m.Pins[1].Default, Is.EqualTo(new Vector2(1, 2)));
        }

        [Test]
        public void Pins_Defaults_GlslZeroMatrixIdentityColorWhite()
        {
            var src = blockStart + "    float f;\n    vec3 v;\n    bool b;\n    int i;\n    mat4 m;\n    vec4 c; // @color\n};\n";
            var m = Build("uniform f float 0\nuniform v vec3 0\nuniform b uint 0\nuniform i int 0\nuniform m mat4 0\nuniform c vec4 0\n", src);
            Assert.That(m.Pins.Select(p => p.Default), Is.EqualTo(new object[]
            {
                0f, Vector3.Zero, false, 0, Matrix.Identity, Color4.White,
            }));
            Assert.That(m.Pins.All(p => p.Min is null && p.Max is null));
        }

        [Test]
        public void Pins_AnnotationValues_ParsedAgainstType()
        {
            var src = blockStart +
                "    float a; // @default 0.5 @min 0 @max 1\n" +
                "    vec2 d; // @default 1.5, -2 @min 0 @max 10, 20\n" +
                "    vec4 c; // @color @default 1, 0, 0, 0.5\n" +
                "    int n; // @default 4 @min 1 @max 8\n" +
                "    bool t; // @default true\n};\n";
            var m = Build("uniform a float 0\nuniform d vec2 0\nuniform c vec4 0\nuniform n int 0\nuniform t uint 0\n", src);
            Assert.That(m.Warnings, Is.Empty);
            Assert.That((m.Pins[0].Default, m.Pins[0].Min, m.Pins[0].Max), Is.EqualTo((0.5f, 0f, 1f)));
            Assert.That((m.Pins[1].Default, m.Pins[1].Min, m.Pins[1].Max), Is.EqualTo((new Vector2(1.5f, -2), new Vector2(0, 0), new Vector2(10, 20))));
            Assert.That(m.Pins[2].Default, Is.EqualTo(new Color4(1, 0, 0, 0.5f)));
            Assert.That((m.Pins[3].Default, m.Pins[3].Min, m.Pins[3].Max), Is.EqualTo((4, 1, 8)));
            Assert.That(m.Pins[4].Default, Is.EqualTo(true));
        }

        [Test]
        public void Pins_SingleNumber_ForMinMaxAppliesToAllComponents()
        {
            var m = Build("uniform d vec3 0\n", blockStart + "    vec3 d; // @min -1 @max 1\n};\n");
            Assert.That(m.Pins[0].Min, Is.EqualTo(new Vector3(-1)));
            Assert.That(m.Pins[0].Max, Is.EqualTo(new Vector3(1)));
        }

        [Test]
        public void Pins_BadValue_WarnsAndUsesDefault()
        {
            var src = blockStart + "    vec2 d; // @default 1\n    float f; // @default abc\n    int n; // @default 1.5 @min x\n    bool b; // @default 1\n};\n";
            var m = Build("uniform d vec2 0\nuniform f float 0\nuniform n int 0\nuniform b uint 0\n", src);
            Assert.That(m.Pins[0].Default, Is.EqualTo(Vector2.Zero));
            Assert.That(m.Pins[1].Default, Is.EqualTo(0f));
            Assert.That(m.Pins[2].Default, Is.EqualTo(0));
            Assert.That(m.Pins[2].Min, Is.Null);
            Assert.That(m.Pins[3].Default, Is.EqualTo(false));
            Assert.That(m.Warnings.Count, Is.EqualTo(5));
            Assert.That(m.Warnings.All(w => w.Line > 0));
        }

        [Test]
        public void Pins_ColorOnNonVec4_WarnsAndIsNormalPin()
        {
            var m = Build("uniform v vec3 0\n", blockStart + "    vec3 v; // @color\n};\n");
            Assert.That(m.Pins[0].Type, Is.EqualTo(ShaderPinType.Vector3));
            Assert.That(m.Pins[0].Default, Is.EqualTo(Vector3.Zero));
            Assert.That(m.Warnings.Single().Text, Does.Contain("@color"));
        }

        [Test]
        public void Pins_Summary_FromAnnotation()
        {
            var m = Build("uniform a float 0\nsampler lut 2\n", blockStart + "    float a; // @summary The amount\n};\nlayout(binding = 2) uniform sampler2D lut; // @summary Lookup\n");
            Assert.That(m.Pins.Where(p => p.Type != ShaderPinType.Sampler).Select(p => p.Summary), Is.EqualTo(new[] { "Lookup", "The amount" }));
        }

        [Test]
        public void Pins_CompileError_SetsErrorAndNoPins()
        {
            var bad = ShaderReflection.Parse("error\nfragment code, line 3: broken\n");
            var m = ShaderPinModel.Build(bad, null, ShaderAnnotations.Parse("// @foo\n"), null);
            Assert.That(m.Error, Does.Contain("line 3"));
            Assert.That(m.Pins, Is.Empty);
            Assert.That(m.Warnings, Is.Not.Empty); // annotation warning is kept
        }

        [Test]
        public void Pins_VertCompileError_AlsoErrors()
        {
            var bad = ShaderReflection.Parse("error\nvertex code, line 1: x\n");
            var m = ShaderPinModel.Build(R("uniform a float 0\n"), bad, ShaderAnnotations.Empty, ShaderAnnotations.Empty);
            Assert.That(m.Error, Does.Contain("vertex"));
            Assert.That(m.Pins, Is.Empty);
        }
    }
}
