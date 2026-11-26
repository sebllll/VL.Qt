using System;
using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;
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
            _rec = new Reconciler(_sink, _ids, new OccurrenceRegistry(), new InteractionRegistry());
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
            _rec.Update(root, SpaceMapping.Default);
        }

        [Test]
        public void FirstFrame_CreatesWholeTree_ParentBeforeChild()
        {
            Frame(G(R(), C(T(), R())));

            Assert.That(Structural, Is.EqualTo(new[]
            {
                "CREATE 1 Group", "REPARENT 1 0 0",
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
            var lastBefore = _rec.Root!.Children[1].Children[0].Element;
            Frame(G(R(), shared));
            Assert.That(_sink.Count, Is.EqualTo(0));
            Assert.That(_rec.Root!.Children[1].Element, Is.SameAs(shared));
            Assert.That(_rec.Root!.Children[1].Children[0].Element, Is.SameAs(lastBefore));
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
        public void ShaderInput_IsChildAtIndexZero()
        {
            Frame(new Shader { Input = R() });
            Assert.That(Structural, Is.EqualTo(new[] { "CREATE 1 Shader", "REPARENT 1 0 0", "CREATE 2 Rectangle", "REPARENT 2 1 0" }));
            Frame(new Shader { Input = T() });
            Assert.That(Structural, Is.EqualTo(new[] { "DESTROY 2", "CREATE 2 Text", "REPARENT 2 1 0" }));
            Frame(new Shader());
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

        private sealed class CountingSink : NullCommandSink
        {
            public int Count;
            public override void Create(uint id, ushort type) => Count++;
            public override void Destroy(uint id) => Count++;
            public override void Reparent(uint id, uint parent, uint index) => Count++;
            public override void Bind(uint id, uint feedbackId) => Count++;
            public override void SetF32(uint id, ushort prop, float value) => Count++;
            public override void SetI32(uint id, ushort prop, int value) => Count++;
            public override void SetBool(uint id, ushort prop, bool value) => Count++;
            public override void SetColor(uint id, ushort prop, float r, float g, float b, float a) => Count++;
            public override void SetVec2(uint id, ushort prop, float x, float y) => Count++;
            public override void SetRect(uint id, ushort prop, float x, float y, float width, float height) => Count++;
            public override void SetMat4(uint id, ushort prop, in Matrix m) => Count++;
            public override void SetString(uint id, ushort prop, ReadOnlySpan<char> value) => Count++;
            public override void SetF32Array(uint id, ushort prop, ReadOnlySpan<float> values) => Count++;
            public override void SetPath(uint id, ushort prop, ReadOnlySpan<byte> verbs, ReadOnlySpan<float> points) => Count++;
            public override void SetRef(uint id, ushort prop, uint refId) => Count++;
            public override void SetU64(uint id, ushort prop, ulong value) => Count++;
            public override void SetIvec2(uint id, ushort prop, int x, int y) => Count++;
            public override void BeginUniforms(uint id, ushort flags) => Count++;
        }
    }
}
