using System;
using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>Structure diff of the reconciler (04b): identity, create/destroy/reparent, clear.</summary>
    public class ReconcilerStructureTests
    {
        private RecordingSink _sink = null!;
        private IdAllocator _ids = null!;
        private Reconciler _rec = null!;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _ids = new IdAllocator();
            _rec = new Reconciler(_sink, _ids, new SceneRegistry());
        }

        private static Group G(params Element[] children) => new() { Children = ImmutableArray.Create(children) };
        private static Column C(params Element[] children) => new() { Children = ImmutableArray.Create(children) };
        private static Rectangle R(float w = 1) => new() { Size = new Vector2(w, 1) };
        private static Text T(string s = "a") => new() { Content = s };

        /// <summary>Recorded commands without SETs (the property diff is covered by ReconcilerPropertyTests).</summary>
        private string[] Structural => _sink.Commands.Where(c => !c.StartsWith("SET ", StringComparison.Ordinal)).ToArray();

        private void Frame(Element? root)
        {
            _sink.Clear();
            _rec.Update(root, Space.DIPTopLeft);
        }

        [Test]
        public void FirstFrame_CreatesWholeTree_ParentBeforeChild()
        {
            Frame(G(R(), C(T(), R())));

            Assert.That(Structural, Is.EqualTo(new[]
            {
                "CREATE 1 Item", "REPARENT 1 0 0",
                "CREATE 2 Rectangle", "REPARENT 2 1 0",
                "CREATE 3 Column", "REPARENT 3 1 1",
                "CREATE 4 Text", "REPARENT 4 3 0",
                "CREATE 5 Rectangle", "REPARENT 5 3 1",
            }));
            Assert.That(_ids.Count, Is.EqualTo(5));
        }

        [Test]
        public void ReferenceEqual_NoCommands()
        {
            var tree = G(R(), C(T(), R()));
            Frame(tree);
            Frame(tree);
            Assert.That(_sink.Count, Is.EqualTo(0));
        }

        [Test]
        public void SameStructureRebuilt_NoStructuralCommands()
        {
            Frame(G(R(), C(T(), R())));
            Frame(G(R(), C(T(), R())));
            Assert.That(_sink.Count, Is.EqualTo(0), _sink.ToString());
            Assert.That(_ids.Count, Is.EqualTo(5));
        }

        [Test]
        public void ReferenceEqualSubtree_IsSkipped()
        {
            var shared = C(T(), R());
            Frame(G(R(), shared));
            var lastBefore = _rec.Root!.Children[1].Children[0].Last;
            Frame(G(R(), shared));
            Assert.That(_sink.Count, Is.EqualTo(0));
            Assert.That(_rec.Root!.Children[1].Last, Is.SameAs(shared));
            Assert.That(_rec.Root!.Children[1].Children[0].Last, Is.SameAs(lastBefore));
        }

        [Test]
        public void TypeChange_DestroyThenCreateAtSameIndex()
        {
            Frame(G(R(), C(T(), R()), R()));          // ids 1..6, Column = 3, its children 4, 5
            Frame(G(R(), T(), R()));                  // Column -> Text at index 1

            Assert.That(Structural, Is.EqualTo(new[]
            {
                "DESTROY 3",                          // top of the subtree only
                "CREATE 3 Text", "REPARENT 3 1 1",       // freed ids reused (LIFO)
            }));
            Assert.That(_ids.Count, Is.EqualTo(4));
            Assert.That(_rec.Root!.Children[1].Type, Is.EqualTo(ElementType.Text));
        }

        [Test]
        public void TypeChange_AtRoot()
        {
            Frame(G(R()));
            Frame(C(R()));
            Assert.That(Structural, Is.EqualTo(new[]
            {
                "DESTROY 1", "CREATE 1 Column", "REPARENT 1 0 0", "CREATE 2 Rectangle", "REPARENT 2 1 0",
            }));
            Assert.That(_ids.Count, Is.EqualTo(2));
        }

        [Test]
        public void ChildRemovedAtEnd_OneDestroy()
        {
            Frame(G(R(), R(), R()));
            Frame(G(R(), R()));
            Assert.That(Structural, Is.EqualTo(new[] { "DESTROY 4" }));
            Assert.That(_ids.Count, Is.EqualTo(3));
        }

        [Test]
        public void SubtreeRemovedAtEnd_OneDestroy_AllIdsFreed()
        {
            Frame(G(R(), C(T(), G(R()))));
            Assert.That(_ids.Count, Is.EqualTo(6));
            Frame(G(R()));
            Assert.That(Structural, Is.EqualTo(new[] { "DESTROY 3" }));
            Assert.That(_ids.Count, Is.EqualTo(2));
        }

        [Test]
        public void ChildInsertedInMiddle_OneCreateAtEnd()
        {
            Frame(G(R(1), R(2)));
            Frame(G(R(1), R(5), R(2)));
            Assert.That(Structural, Is.EqualTo(new[] { "CREATE 4 Rectangle", "REPARENT 4 1 2" }));
            Assert.That(_rec.Root!.Children.Select(n => n.Id), Is.EqualTo(new uint[] { 2, 3, 4 }));
        }

        [Test]
        public void ChildRemovedInMiddle_OneDestroyAtEnd()
        {
            Frame(G(R(1), R(2), R(3)));
            Frame(G(R(1), R(3)));
            Assert.That(Structural, Is.EqualTo(new[] { "DESTROY 4" }));
        }

        [Test]
        public void MovedToOtherParent_IsNewObject()
        {
            var rect = R();
            Frame(G(G(rect), G()));                    // 1 { 2 { 3 }, 4 }
            Frame(G(G(), G(rect)));
            Assert.That(Structural, Is.EqualTo(new[] { "DESTROY 3", "CREATE 3 Rectangle", "REPARENT 3 4 0" }));
        }

        [Test]
        public void NullRoot_DestroysScene_ThenRecreates()
        {
            Frame(G(R()));
            Frame(null);
            Assert.That(Structural, Is.EqualTo(new[] { "DESTROY 1" }));
            Assert.That(_ids.Count, Is.EqualTo(0));
            Assert.That(_rec.Root, Is.Null);

            Frame(null);
            Assert.That(_sink.Count, Is.EqualTo(0));

            Frame(G(R()));
            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(2));
        }

        [Test]
        public void ShaderEffectInput_IsChildAtIndexZero()
        {
            Frame(new ShaderEffect { Input = R() });
            Assert.That(Structural, Is.EqualTo(new[] { "CREATE 1 ShaderEffect", "REPARENT 1 0 0", "CREATE 2 Rectangle", "REPARENT 2 1 0" }));
            Frame(new ShaderEffect { Input = T() });
            Assert.That(Structural, Is.EqualTo(new[] { "DESTROY 2", "CREATE 2 Text", "REPARENT 2 1 0" }));
            Frame(new ShaderEffect());
            Assert.That(Structural, Is.EqualTo(new[] { "DESTROY 2" }));
        }

        [Test]
        public void Clear_OnlyTopLevelDestroy_AllIdsFreed()
        {
            Frame(G(R(), C(T(), R()), G(R())));
            _sink.Clear();
            _rec.Clear();
            Assert.That(Structural, Is.EqualTo(new[] { "DESTROY 1" }));
            Assert.That(_ids.Count, Is.EqualTo(0));

            _sink.Clear();
            _rec.Clear();
            Assert.That(_sink.Count, Is.EqualTo(0));
        }

        private sealed class CountingSink : ICommandSink
        {
            public int Count;
            public void Create(uint id, ushort type) => Count++;
            public void Destroy(uint id) => Count++;
            public void Reparent(uint id, uint parent, uint index) => Count++;
            public void Bind(uint id, uint feedbackId, ushort flags) => Count++;
            public void SetF32(uint id, ushort prop, float value) => Count++;
            public void SetI32(uint id, ushort prop, int value) => Count++;
            public void SetBool(uint id, ushort prop, bool value) => Count++;
            public void SetColor(uint id, ushort prop, float r, float g, float b, float a) => Count++;
            public void SetVec2(uint id, ushort prop, float x, float y) => Count++;
            public void SetRect(uint id, ushort prop, float x, float y, float width, float height) => Count++;
            public void SetMat4(uint id, ushort prop, in Matrix m) => Count++;
            public void SetString(uint id, ushort prop, ReadOnlySpan<char> value) => Count++;
            public void SetF32Array(uint id, ushort prop, ReadOnlySpan<float> values) => Count++;
            public void SetPath(uint id, ushort prop, ReadOnlySpan<byte> verbs, ReadOnlySpan<float> points) => Count++;
            public void SetRef(uint id, ushort prop, uint refId) => Count++;
            public void SetU64(uint id, ushort prop, ulong value) => Count++;
            public void BeginStyle(uint id, ushort flags) { }
            public void StyleColor(ushort field, byte state, float r, float g, float b, float a) { }
            public void StyleF32(ushort field, byte state, float value) { }
            public void StyleI32(ushort field, byte state, int value) { }
            public void StyleString(ushort field, byte state, ReadOnlySpan<char> value) { }
            public void EndStyle() { }
            public void SetIvec2(uint id, ushort prop, int x, int y) => Count++;
            public void BeginUniforms(uint id, ushort flags) => Count++;
            public void UniformF32(ReadOnlySpan<char> name, float value) { }
            public void UniformI32(ReadOnlySpan<char> name, int value) { }
            public void UniformBool(ReadOnlySpan<char> name, bool value) { }
            public void UniformVec2(ReadOnlySpan<char> name, float x, float y) { }
            public void UniformVec3(ReadOnlySpan<char> name, float x, float y, float z) { }
            public void UniformVec4(ReadOnlySpan<char> name, float x, float y, float z, float w) { }
            public void UniformIvec2(ReadOnlySpan<char> name, int x, int y) { }
            public void UniformColor(ReadOnlySpan<char> name, float r, float g, float b, float a) { }
            public void UniformMat4(ReadOnlySpan<char> name, in Matrix m) { }
            public void UniformRef(ReadOnlySpan<char> name, uint refId) { }
            public void UniformU64(ReadOnlySpan<char> name, ulong value) { }
            public void UniformSampler(ReadOnlySpan<char> name, ulong packed) { }
            public void EndUniforms() { }
            public void BeginEffectChain(uint id) { }
            public void BeginMultiLink() { }
            public void EffectBool(ushort prop, bool value) { }
            public void EffectF32(ushort prop, float value) { }
            public void EffectI32(ushort prop, int value) { }
            public void EffectColor(ushort prop, float r, float g, float b, float a) { }
            public void EffectRect(ushort prop, float x, float y, float width, float height) { }
            public void EffectRef(ushort prop, uint refId) { }
            public void BeginShaderLink(ushort template, ReadOnlySpan<ulong> shaderIds, float paddingLeft, float paddingTop,
                float paddingRight, float paddingBottom, float strength) { }
            public void EndLink() { }
            public void EndEffectChain() { }
        }
    }
}
