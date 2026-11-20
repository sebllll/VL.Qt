using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;

namespace VL.Qt.Tests
{
    /// <summary>vlqt_shader_reflect (#488): the real QtNative, text format of vlqt.h. Needs a built vlqt.dll (tools/build.ps1).</summary>
    public class ShaderReflectNativeTests
    {
        private const string good = @"#version 440
layout(location = 0) in vec2 qt_TexCoord0;
layout(location = 0) out vec4 fragColor;
layout(std140, binding = 0) uniform buf {
    mat4 qt_Matrix;
    float qt_Opacity;
    float amount;
    vec2 center;
    vec4 tint;
    int mode;
    bool invert;
    float arr[4];
};
layout(binding = 1) uniform sampler2D source;
layout(binding = 2) uniform sampler2D mask;
void main() {
    vec4 c = texture(source, qt_TexCoord0) * texture(mask, qt_TexCoord0);
    fragColor = (invert ? 1.0 - c : c) * tint * amount * arr[mode] * qt_Opacity + vec4(center, 0.0, 0.0);
}
";

        // The test output has no runtimes/<rid>/native (the repo's deploy folder is not copied, 128 MB): link it in.
        [OneTimeSetUp]
        public void LinkNativeDir() => LinkNative();

        // Also used by tests that reflect real shaders (EffectTemplateShadersTests); ignores the test without a built vlqt.
        internal static void LinkNative()
        {
            var rid = (OperatingSystem.IsWindows() ? "win-" : "linux-")
                + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");
            var lib = OperatingSystem.IsWindows() ? "vlqt.dll" : "libvlqt.so";
            var target = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native");
            if (File.Exists(Path.Combine(target, lib)))
                return;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var source = Path.Combine(dir.FullName, "runtimes", rid, "native");
                if (!File.Exists(Path.Combine(source, lib)))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (Directory.Exists(target))
                    Directory.Delete(target);
                if (OperatingSystem.IsWindows())
                {
                    using var p = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{target}\" \"{source}\"")
                        { CreateNoWindow = true, RedirectStandardOutput = true })!;
                    p.WaitForExit();
                }
                else
                    Directory.CreateSymbolicLink(target, source);
                return;
            }
            // On the CI a missing library is a broken build, not a reason to skip; locally the test is ignored with the reason
            // (tools/build.ps1 prints it in its summary).
            var reason = $"{lib} for {rid} not built (tools/build.ps1)";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
                Assert.Fail(reason);
            Assert.Ignore(reason);
        }

        private static string Reflect(string source, int stage)
        {
            var buf = new byte[4096];
            var n = Native.vlqt_shader_reflect(source, stage, ref buf[0], buf.Length);
            Assert.That(n, Is.GreaterThan(0));
            return Encoding.UTF8.GetString(buf, 0, Math.Min(n, buf.Length));
        }

        [Test]
        public void Fragment_ListsUniformsInOffsetOrderAndSamplers()
        {
            var text = Reflect(good, Protocol.ShaderStage.Fragment);
            Assert.That(text.Split('\n'), Is.EqualTo(new[]
            {
                "ok",
                "uniform qt_Matrix mat4 0",
                "uniform qt_Opacity float 0",
                "uniform amount float 0",
                "uniform center vec2 0",
                "uniform tint vec4 0",
                "uniform mode int 0",
                "uniform invert uint 0",
                "uniform arr float 4",
                "sampler source 1",
                "sampler mask 2",
            }));
        }

        [Test]
        public void SmallBuffer_ReturnsNeededLength()
        {
            var full = Reflect(good, Protocol.ShaderStage.Fragment);
            var buf = new byte[8];
            var n = Native.vlqt_shader_reflect(good, Protocol.ShaderStage.Fragment, ref buf[0], buf.Length);
            Assert.That(n, Is.EqualTo(Encoding.UTF8.GetByteCount(full)));
            Assert.That(Encoding.UTF8.GetString(buf, 0, 2), Is.EqualTo("ok"));
        }

        [Test]
        public void BrokenShader_ReportsErrorLineWithStageName()
        {
            var text = Reflect(good.Replace("float amount;", "float amount"), Protocol.ShaderStage.Fragment);
            var lines = text.Split('\n');
            Assert.That(lines[0], Is.EqualTo("error"));
            Assert.That(lines[1], Does.StartWith("fragment code, line "));
        }

        [Test]
        public void BadArguments_Return_Minus7()
        {
            var buf = new byte[16];
            Assert.That(Native.vlqt_shader_reflect("", Protocol.ShaderStage.Fragment, ref buf[0], buf.Length), Is.EqualTo(-7));
            Assert.That(Native.vlqt_shader_reflect(good, 99, ref buf[0], buf.Length), Is.EqualTo(-7));
            Assert.That(Native.vlqt_shader_reflect(good, Protocol.ShaderStage.Fragment, ref buf[0], -1), Is.EqualTo(-7));
        }
    }
}
