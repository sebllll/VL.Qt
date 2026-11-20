using System;
using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Path = VL.Lib.IO.Path;
using P = VL.Qt.Protocol.Prop;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>ShaderEffect (#437): SET_UNIFORMS encoding, uniform diff, properties, shader resources of the node.</summary>
    public class ShaderEffectTests
    {
        private static string U16(int v) => Convert.ToHexString(BitConverter.GetBytes((ushort)v));
        private static string U32(uint v) => Convert.ToHexString(BitConverter.GetBytes(v));
        private static string F32(float v) => Convert.ToHexString(BitConverter.GetBytes(v));

        // --- wire format ---

        [Test]
        public void SetUniforms_Encoding_EntriesAreAligned()
        {
            var w = new CommandWriter();
            w.BeginUniforms(5, Protocol.UniformSet.Clear);
            w.UniformF32("t", 1.5f);       // 4 + 1 -> 8, value 4
            w.UniformBool("on", true);     // 4 + 2 -> 8, value 1 -> 4
            w.UniformVec3("abcd", 1, 2, 3); // 4 + 4 -> 8, value 12
            w.EndUniforms();

            var payload = 8 + (8 + 4) + (8 + 4) + (8 + 12);
            var expected =
                U16(Protocol.Op.SetUniforms) + U16(payload) + U32(5) + U16(3) + U16(Protocol.UniformSet.Clear)
                + U16(Protocol.Vt.F32) + U16(1) + "7400" + "0000" + F32(1.5f)
                + U16(Protocol.Vt.Bool) + U16(2) + "6F6E" + "0000" + "01000000"
                + U16(Protocol.Vt.Vec3) + U16(4) + "61626364" + F32(1) + F32(2) + F32(3);
            Assert.That(Convert.ToHexString(w.Written), Is.EqualTo(expected));
            Assert.That(w.Count, Is.EqualTo(1));
        }

        [Test]
        public void SetUniforms_Utf8NameAndRefAndU64()
        {
            var w = new CommandWriter();
            w.BeginUniforms(1, 0);
            w.UniformRef("é", 9);        // 2 UTF-8 bytes
            w.UniformU64("img", 0x0000000700000003);
            w.EndUniforms();
            var expected =
                U16(Protocol.Op.SetUniforms) + U16(8 + (8 + 4) + (8 + 8)) + U32(1) + U16(2) + U16(0)
                + U16(Protocol.Vt.Ref) + U16(2) + "C3A9" + "0000" + U32(9)
                + U16(Protocol.Vt.U64) + U16(3) + "696D67" + "00" + Convert.ToHexString(BitConverter.GetBytes(0x0000000700000003UL));
            Assert.That(Convert.ToHexString(w.Written), Is.EqualTo(expected));
        }

        [Test]
        public void SetUniforms_InvalidName_Throws_AndMisuseThrows()
        {
            var w = new CommandWriter();
            Assert.Throws<InvalidOperationException>(() => w.UniformF32("a", 1));
            Assert.Throws<InvalidOperationException>(() => w.EndUniforms());
            w.BeginUniforms(1, 0);
            Assert.Throws<ArgumentException>(() => w.UniformF32("", 1));
            Assert.Throws<ArgumentException>(() => w.UniformF32(new string('x', Protocol.UniformNameMax + 1), 1));
            Assert.Throws<InvalidOperationException>(() => w.BeginUniforms(1, 0));
        }

        [Test]
        public void SetIvec2_Encoding()
        {
            var w = new CommandWriter();
            w.SetIvec2(3, P.MeshResolution, 4, -2);
            Assert.That(Convert.ToHexString(w.Written), Is.EqualTo(
                U16(Protocol.Op.Set) + U16(16) + U32(3) + U16(P.MeshResolution) + U16(Protocol.Vt.Ivec2)
                + Convert.ToHexString(BitConverter.GetBytes(4)) + Convert.ToHexString(BitConverter.GetBytes(-2))));
        }

        // --- reconciler ---

        private RecordingSink _sink = null!;
        private Reconciler _rec = null!;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _rec = new Reconciler(_sink, new IdAllocator(), new SceneRegistry());
        }

        private void Frame(Element root)
        {
            _sink.Clear();
            _rec.Update(root, Space.DIPTopLeft);
        }

        private static Spread<Uniform> U(params Uniform[] uniforms) => uniforms.ToSpread();

        [Test]
        public void Create_SendsShadersBlendingMeshSourceAndUniforms()
        {
            var input = new Rectangle();
            Frame(new ShaderEffect
            {
                Input = input, FragmentShaderId = 11, VertexShaderId = 12, Blending = false, MeshResolution = new Int2(4, 3),
                Uniforms = U(Uniform.Float("time", 0.5f), Uniform.Vector2("size", new Vector2(2, 3))),
            });
            var text = _sink.ToString();
            Assert.That(text, Does.Contain($"SET 1 FragmentShader u64(11)"));
            Assert.That(text, Does.Contain($"SET 1 VertexShader u64(12)"));
            Assert.That(text, Does.Contain($"SET 1 Blending bool(0)"));
            Assert.That(text, Does.Contain($"SET 1 MeshResolution ivec2(4,3)"));
            Assert.That(text, Does.Contain($"SET 1 Source ref(2)"));
            // The input is hidden by QtNative on REPARENT under the ShaderEffect (#474), nothing to send.
            Assert.That(_sink.OfOp("UNIFORMS").Single(), Is.EqualTo("UNIFORMS 1 0 time=f32(0.5) size=vec2(2,3)"));
            Assert.That(text.IndexOf("CREATE 2", StringComparison.Ordinal), Is.LessThan(text.IndexOf("UNIFORMS", StringComparison.Ordinal)));
        }

        [Test]
        public void Create_Defaults_SendNothingExtra()
        {
            Frame(new ShaderEffect());
            Assert.That(_sink.Commands.Where(c => c.StartsWith("SET 1 " + P.FragmentShader) || c.StartsWith("SET 1 " + P.Blending)
                || c.StartsWith("SET 1 " + P.MeshResolution) || c.StartsWith("SET 1 " + P.Source) || c.StartsWith("UNIFORMS")), Is.Empty);
        }

        [Test]
        public void Uniforms_OnlyChangesAreSent()
        {
            Frame(new ShaderEffect { Uniforms = U(Uniform.Float("a", 1), Uniform.Int("b", 2), Uniform.Bool("c", true)) });

            // new description, equal uniforms: nothing
            Frame(new ShaderEffect { Uniforms = U(Uniform.Float("a", 1), Uniform.Int("b", 2), Uniform.Bool("c", true)) });
            Assert.That(_sink.OfOp("UNIFORMS"), Is.Empty);

            // one value changes: only that entry, no clear
            Frame(new ShaderEffect { Uniforms = U(Uniform.Float("a", 1), Uniform.Int("b", 3), Uniform.Bool("c", true)) });
            Assert.That(_sink.OfOp("UNIFORMS").Single(), Is.EqualTo("UNIFORMS 1 0 b=i32(3)"));

            // a new name: only the new one
            Frame(new ShaderEffect { Uniforms = U(Uniform.Float("a", 1), Uniform.Int("b", 3), Uniform.Bool("c", true), Uniform.Color("d", new Color4(1, 0, 0, 0.5f))) });
            Assert.That(_sink.OfOp("UNIFORMS").Single(), Is.EqualTo("UNIFORMS 1 0 d=color(1,0,0,0.5)"));

            // the type of a name changes: sent again by name
            Frame(new ShaderEffect { Uniforms = U(Uniform.Float("a", 1), Uniform.Float("b", 3), Uniform.Bool("c", true), Uniform.Color("d", new Color4(1, 0, 0, 0.5f))) });
            Assert.That(_sink.OfOp("UNIFORMS").Single(), Is.EqualTo("UNIFORMS 1 0 b=f32(3)"));
        }

        [Test]
        public void Uniforms_Removal_SendsFullTableWithClear()
        {
            Frame(new ShaderEffect { Uniforms = U(Uniform.Float("a", 1), Uniform.Float("b", 2)) });
            Frame(new ShaderEffect { Uniforms = U(Uniform.Float("b", 2)) });
            Assert.That(_sink.OfOp("UNIFORMS").Single(), Is.EqualTo($"UNIFORMS 1 {Protocol.UniformSet.Clear} b=f32(2)"));

            Frame(new ShaderEffect());
            Assert.That(_sink.OfOp("UNIFORMS").Single(), Is.EqualTo($"UNIFORMS 1 {Protocol.UniformSet.Clear}"));

            Frame(new ShaderEffect { Blending = false });
            Assert.That(_sink.OfOp("UNIFORMS"), Is.Empty, "nothing left to clear");
        }

        [Test]
        public void Uniforms_DuplicateName_LastCounts_InvalidNamesSkipped()
        {
            Frame(new ShaderEffect { Uniforms = U(Uniform.Float("a", 1), Uniform.Float("", 5), Uniform.Float("a", 2)) });
            Assert.That(_sink.OfOp("UNIFORMS").Single(), Is.EqualTo("UNIFORMS 1 0 a=f32(2)"));
        }

        [Test]
        public void TextureUniforms_AreHiddenChildrenAfterTheInput_WithTheirIds()
        {
            var input = new Rectangle();
            var tex = new Rectangle();
            var tex2 = new Rectangle();
            Frame(new ShaderEffect
            {
                Input = input,
                Uniforms = U(Uniform.FromTexture("mask", tex), Uniform.FromTexture("again", tex), Uniform.FromTexture("other", tex2)),
            });
            var text = _sink.ToString();
            Assert.That(text, Does.Contain($"SET 1 Source ref(2)"));
            Assert.That(_sink.OfOp("UNIFORMS").Single(), Is.EqualTo("UNIFORMS 1 0 mask=ref(3) again=ref(3) other=ref(4)"));
            Assert.That(_sink.OfOp("REPARENT").Count(), Is.EqualTo(4));
        }

        [Test]
        public void ImageUniform_SendsResourceId_ChangeResends()
        {
            var a = new QtImage(new ImageResource(0x100000003));
            var b = new QtImage(new ImageResource(0x200000003));
            Frame(new ShaderEffect { Uniforms = U(Uniform.FromImage("tex", a)) });
            Assert.That(_sink.OfOp("UNIFORMS").Single(), Is.EqualTo($"UNIFORMS 1 0 tex=u64({a.Id})"));
            Frame(new ShaderEffect { Uniforms = U(Uniform.FromImage("tex", b)) });
            Assert.That(_sink.OfOp("UNIFORMS").Single(), Is.EqualTo($"UNIFORMS 1 0 tex=u64({b.Id})"));
            Frame(new ShaderEffect { Uniforms = U(Uniform.FromImage("tex", null)) });
            Assert.That(_sink.OfOp("UNIFORMS").Single(), Is.EqualTo("UNIFORMS 1 0 tex=u64(0)"));
        }

        [Test]
        public void Properties_OnlyChangesAreSent_MeshClamped()
        {
            Frame(new ShaderEffect { FragmentShaderId = 1 });
            Frame(new ShaderEffect { FragmentShaderId = 2, Blending = false, MeshResolution = new Int2(0, 5) });
            Assert.That(_sink.OfOp("SET"), Is.EquivalentTo(new[]
            {
                $"SET 1 FragmentShader u64(2)",
                $"SET 1 Blending bool(0)",
                $"SET 1 MeshResolution ivec2(1,5)",
            }));
            Frame(new ShaderEffect { FragmentShaderId = 2, Blending = false, MeshResolution = new Int2(-3, 5) });
            Assert.That(_sink.Commands, Is.Empty, "clamped value is unchanged");
        }

        [Test]
        public void InputRemoved_SourceReset()
        {
            Frame(new ShaderEffect { Input = new Rectangle() });
            Frame(new ShaderEffect());
            Assert.That(_sink.ToString(), Does.Contain($"SET 1 Source ref(0)"));
        }

        // --- node ---

        private FakeResourceApi _api = null!;
        private ResourceClient _client = null!;
        private ShaderEffectNode _node = null!;

        private void CreateNode()
        {
            _api = new FakeResourceApi();
            _client = new ResourceClient(_api, Environment.CurrentManagedThreadId, static () => null);
            _client.SetPumping(true);
            _node = new ShaderEffectNode(_client, static _ => new Noop());
        }

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }

        private void Pump()
        {
            _client.BeforeEvents();
            _client.AfterEvents();
        }

        private ShaderEffect Update(string? fragment, out string error) => Update(fragment, null, null, out error);

        private ShaderEffect Update(string? fragment, string? vertex, Spread<Uniform>? uniforms, out string error)
        {
            var element = _node.Update(null, null, null, fragment is null ? null : new Path(fragment), null, vertex is null ? null : new Path(vertex), uniforms, true,
                default, new Vector2(1, 1), true, Matrix.Identity, RectangleAnchor.Center, 1f, false, false, null, null,
                out _, out _, out error);
            return (ShaderEffect)element;
        }

        [Test]
        public void Path_BuiltinPrefixSurvivesVlPath() => Assert.That(new Path("builtin:glow").Value, Is.EqualTo("builtin:glow"));

        [TestCase("builtin:wave", true, "builtin:wave")]
        [TestCase(@"C:\foo\help\Qt\builtin:wave", true, "builtin:wave")]
        [TestCase("C:/foo/help/Qt/builtin:wave", true, "builtin:wave")]
        [TestCase(@"C:\foo\wave.frag.qsb", false, @"C:\foo\wave.frag.qsb")]
        [TestCase("rel/wave.qsb", false, "rel/wave.qsb")]
        public void TryGetBuiltinShader_RecognizesResolvedPaths(string path, bool expected, string normalized)
        {
            Assert.That(ResourceClient.TryGetBuiltinShader(path, out var builtin), Is.EqualTo(expected));
            Assert.That(builtin, Is.EqualTo(normalized));
        }

        [Test]
        public void Node_LoadsOnPathChangeOnly_BuiltinUnchanged_OtherPathsAbsolute()
        {
            CreateNode();
            var e1 = Update("builtin:glow", "rel/wave.vert", null, out var error);
            Assert.That(error, Is.Empty);
            Assert.That(_api.Calls.Count(c => c.StartsWith("create 4")), Is.EqualTo(2), "two shader resources");
            Assert.That(_api.Calls, Does.Contain($"shader {e1.FragmentShaderId:X} builtin:glow {Protocol.ShaderStage.Fragment}"));
            Assert.That(_api.Calls, Does.Contain($"shader {e1.VertexShaderId:X} {System.IO.Path.GetFullPath("rel/wave.vert")} {Protocol.ShaderStage.Vertex}"));
            Assert.That(e1.FragmentShaderId, Is.Not.EqualTo(0UL));

            var calls = _api.Calls.Count;
            var e2 = Update("builtin:glow", "rel/wave.vert", null, out _);
            Assert.That(_api.Calls.Count, Is.EqualTo(calls), "same paths do not reload");
            Assert.That(e2, Is.SameAs(e1));
        }

        [Test]
        public void Node_PathChange_NewResource_OldReleasedInNextPump()
        {
            CreateNode();
            var e1 = Update("a.qsb", out _);
            var e2 = Update("b.qsb", out _);
            Assert.That(e2.FragmentShaderId, Is.Not.EqualTo(e1.FragmentShaderId));
            Assert.That(_api.Released, Is.Empty, "the element switches first");
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { e1.FragmentShaderId }));

            var e3 = Update(null, out _);
            Assert.That(e3.FragmentShaderId, Is.EqualTo(0UL));
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { e1.FragmentShaderId, e2.FragmentShaderId }));
        }

        [Test]
        public void Node_Dispose_ReleasesBothShaders()
        {
            CreateNode();
            var e = Update("a.qsb", "a.vert", null, out _);
            _node.Dispose();
            Pump();
            Assert.That(_api.Released, Is.EquivalentTo(new[] { e.FragmentShaderId, e.VertexShaderId }));
        }

        [Test]
        public void Node_FailedShader_ShowsAtErrorOutput_ClearsOnNewPath()
        {
            CreateNode();
            var e = Update("broken.qsb", out var error);
            Assert.That(error, Is.Empty, "loading is not an error");
            _api.Error(e.FragmentShaderId, "not a .qsb file");
            _api.State(e.FragmentShaderId, Protocol.ResState.Failed);
            Pump();
            Update("broken.qsb", out error);
            Assert.That(error, Is.EqualTo("Fragment shader: not a .qsb file"));

            var e2 = Update("good.qsb", out error);
            _api.State(e2.FragmentShaderId, Protocol.ResState.Ready);
            Pump();
            Update("good.qsb", out error);
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void Node_QtNativeRefusesLoad_ErrorOutput()
        {
            CreateNode();
            _api.LoadRc = -7;
            Update("a.qsb", out _);
            Update("a.qsb", out var error);
            Assert.That(error, Does.StartWith("Fragment shader: Loading shader"));
        }

        [Test]
        public void Node_CreateRefused_ErrorOutput_NoId()
        {
            CreateNode();
            _api.CreateRc = -9;
            var e = Update("a.qsb", out var error);
            Assert.That(e.FragmentShaderId, Is.EqualTo(0UL));
            Assert.That(error, Does.StartWith("Fragment shader: vlqt_res_create failed"));
        }

        [Test]
        public void Node_UniformNameProblems_AtErrorOutput()
        {
            CreateNode();
            Update("a.qsb", null, U(Uniform.Float("a", 1), Uniform.Float("a", 2), Uniform.Float("", 3)), out var error);
            Assert.That(error, Does.Contain("'a' is set more than once"));
            Assert.That(error, Does.Contain("the name is empty"));
            Update("a.qsb", null, U(Uniform.Float("a", 1)), out error);
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void Node_UnchangedInputs_SameDescription()
        {
            CreateNode();
            var uniforms = U(Uniform.Float("a", 1));
            var e1 = Update("a.qsb", null, uniforms, out _);
            var e2 = Update("a.qsb", null, uniforms, out _);
            Assert.That(e2, Is.SameAs(e1));
            var e3 = Update("a.qsb", null, U(Uniform.Float("a", 2)), out _);
            Assert.That(e3, Is.Not.SameAs(e1));
        }

        // --- GLSL code (#483) ---

        private ShaderEffect UpdateCode(string? fragmentCode, string? fragmentPath, string? vertexCode, out string error)
        {
            var element = _node.Update(null, null, fragmentCode, fragmentPath is null ? null : new Path(fragmentPath), vertexCode, null, null, true,
                default, new Vector2(1, 1), true, Matrix.Identity, RectangleAnchor.Center, 1f, false, false, null, null,
                out _, out _, out error);
            return (ShaderEffect)element;
        }

        [Test]
        public void Code_BakesViaLoadShaderSource_SameTextDoesNotReload()
        {
            CreateNode();
            var e1 = UpdateCode("void main() {}", null, null, out var error);
            Assert.That(error, Is.Empty);
            Assert.That(e1.FragmentShaderId, Is.Not.EqualTo(0UL));
            Assert.That(_api.Calls, Does.Contain($"source {e1.FragmentShaderId:X} 14 {Protocol.ShaderStage.Fragment}"));
            Assert.That(e1.VertexShaderId, Is.EqualTo(0UL), "empty vertex code = Qt default");

            var calls = _api.Calls.Count;
            var e2 = UpdateCode(new string("void main() {}".ToCharArray()), null, "  	 ", out _);
            Assert.That(_api.Calls.Count, Is.EqualTo(calls));
            Assert.That(e2, Is.SameAs(e1));
        }

        [Test]
        public void Code_PathWinsOverCode_AndCodeIgnoredWhilePathSet()
        {
            CreateNode();
            var e1 = UpdateCode("void main() {}", "a.qsb", null, out _);
            Assert.That(_api.Calls.Any(c => c.StartsWith("source")), Is.False);
            Assert.That(_api.Calls, Does.Contain($"shader {e1.FragmentShaderId:X} {System.IO.Path.GetFullPath("a.qsb")} {Protocol.ShaderStage.Fragment}"));
            var calls = _api.Calls.Count;
            var e2 = UpdateCode("other text", "a.qsb", null, out _);
            Assert.That(_api.Calls.Count, Is.EqualTo(calls), "code change under a path does nothing");
            Assert.That(e2, Is.SameAs(e1));

            // path cleared: the code is baked now
            var e3 = UpdateCode("other text", null, null, out _);
            Assert.That(_api.Calls, Does.Contain($"source {e3.FragmentShaderId:X} 10 {Protocol.ShaderStage.Fragment}"));
            Assert.That(e3.FragmentShaderId, Is.Not.EqualTo(e1.FragmentShaderId));
        }

        [Test]
        public void Code_Change_NewResource_OldReleasedAfterSwitch()
        {
            CreateNode();
            var e1 = UpdateCode("a", null, null, out _);
            _api.State(e1.FragmentShaderId, Protocol.ResState.Ready);
            Pump();
            var e2 = UpdateCode("bb", null, null, out _);
            Assert.That(e2.FragmentShaderId, Is.Not.EqualTo(e1.FragmentShaderId));
            Assert.That(_api.Released, Is.Empty, "the element switches first");
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { e1.FragmentShaderId }));
        }

        [Test]
        public void Code_Empty_NoResource_ReleasesOld()
        {
            CreateNode();
            var e1 = UpdateCode("a", null, null, out _);
            var e2 = UpdateCode("  ", null, null, out var error);
            Assert.That(e2.FragmentShaderId, Is.EqualTo(0UL));
            Assert.That(error, Is.Empty);
            Pump();
            Assert.That(_api.Released, Is.EqualTo(new[] { e1.FragmentShaderId }));
        }

        [Test]
        public void Code_BakeError_ShowsWithPrefix_ClearsOnNewText()
        {
            CreateNode();
            var e = UpdateCode("bad", null, null, out var error);
            Assert.That(error, Is.Empty, "baking is not an error");
            _api.Failed(e.FragmentShaderId, "0:3: syntax error");
            Pump();
            UpdateCode("bad", null, null, out error);
            Assert.That(error, Is.EqualTo("Fragment shader: 0:3: syntax error"));
            UpdateCode("good", null, null, out error);
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void Code_Throttled_WhileBakeRuns_LatestTextStartsAfterwards()
        {
            CreateNode();
            var e1 = UpdateCode("a", null, null, out _);
            var calls = _api.Calls.Count;
            var e2 = UpdateCode("ab", null, null, out _);
            var e3 = UpdateCode("abc", null, null, out _);
            Assert.That(_api.Calls.Count, Is.EqualTo(calls), "no new bake while the first runs");
            Assert.That(e3.FragmentShaderId, Is.EqualTo(e1.FragmentShaderId));

            _api.State(e1.FragmentShaderId, Protocol.ResState.Ready);
            Pump();
            var e4 = UpdateCode("abc", null, null, out _);
            Assert.That(_api.Calls.Count(c => c.StartsWith("source")), Is.EqualTo(2));
            Assert.That(_api.Calls.Last(), Does.StartWith($"source {e4.FragmentShaderId:X} 3 "), "the newest text, not the skipped one");
            Assert.That(e4.FragmentShaderId, Is.Not.EqualTo(e1.FragmentShaderId));
        }

        [Test]
        public void Code_Throttled_AlsoAfterFailedBakeNotBlocked()
        {
            CreateNode();
            var e1 = UpdateCode("a", null, null, out _);
            _api.Failed(e1.FragmentShaderId, "err");
            Pump();
            var e2 = UpdateCode("b", null, null, out _);
            Assert.That(e2.FragmentShaderId, Is.Not.EqualTo(e1.FragmentShaderId), "Failed is settled: next text starts at once");
        }

        [Test]
        public void ShaderTemplate_HasStdBlockAndSampler()
        {
            Assert.That(ShaderTemplates.Fragment, Does.StartWith("#version 440"));
            Assert.That(ShaderTemplates.Fragment, Does.Contain("mat4 qt_Matrix;").And.Contain("float qt_Opacity;").And.Contain("sampler2D source"));
        }
    }
}
