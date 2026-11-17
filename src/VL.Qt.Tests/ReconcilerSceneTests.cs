using System;
using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>Occurrences and duplicates (SceneRegistry, #455) and sink binding (BIND without flags) of the reconciler (04d).</summary>
    public class ReconcilerSceneTests
    {
        RecordingSink _sink = null!;
        SceneRegistry _scene = null!;
        Reconciler _rec = null!;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _scene = new SceneRegistry();
            _rec = new Reconciler(_sink, new IdAllocator(), _scene);
        }

        static Group G(params Element[] children) => new() { Children = ImmutableArray.Create(children) };
        static Rectangle R(float w = 1, SinkSlot sink = default)
            => new() { Size = new Vector2(w, 1), Sink = sink };

        string[] Structural => _sink.Commands.Where(c => !c.StartsWith("SET ", StringComparison.Ordinal)).ToArray();

        void Frame(Element? root, Reconciler? rec = null)
        {
            _sink.Clear();
            (rec ?? _rec).Update(root, Space.DIPTopLeft);
        }

﻿        // --- several occurrences in one tree (#455) ---

        static Video V(SinkSlot sink = default) => new() { Size = new Vector2(1, 1), Sink = sink };

        bool IsPrimaryHandle(Node n) => _rec.Sinks.IsPrimary(n.BoundHandle);

        [Test]
        public void TwoOccurrences_TwoObjects_OwnHandlesOnSameSlot()
        {
            var sink = new ElementSink();
            var e = R(1, new SinkSlot(sink, 0));
            var x = R(2);
            Frame(G(e, e, x));

            var c = _rec.Root!.Children;
            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(4)); // root, e twice, x
            Assert.That(c.Any(n => n.IsIgnored), Is.False);
            Assert.That(c[0].Id, Is.Not.EqualTo(c[1].Id));
            Assert.That(c[0].BoundHandle, Is.Not.EqualTo(c[1].BoundHandle));
            Assert.That(_sink.OfOp("BIND"), Is.EqualTo(new[] { $"BIND {c[0].Id} {c[0].BoundHandle} 0", $"BIND {c[1].Id} {c[1].BoundHandle} 0" }));
            Assert.That(_rec.Sinks.TryResolve(c[1].BoundHandle, out var slot) && slot == new SinkSlot(sink, 0), Is.True);
            Assert.That(_rec.Bindings.Count, Is.EqualTo(2));
            Assert.That(Structural, Does.Contain($"REPARENT {c[2].Id} {_rec.Root.Id} 2"));
            Assert.That(sink[0].Warning, Is.Null);
            Assert.That(_scene.OccurrencesOf(e), Is.EqualTo(2));
            Assert.That(_scene.OwnerOf(e), Is.SameAs(c[0]), "first in traversal order is primary");
            Assert.That(IsPrimaryHandle(c[0]) && !IsPrimaryHandle(c[1]), Is.True);

            // Same tree again → nothing.
            Frame(G(e, e, x));
            Assert.That(_sink.Commands, Is.Empty);

            // Second occurrence removed → one object left, nothing else.
            Frame(G(e, x));
            Assert.That(_scene.OccurrencesOf(e), Is.EqualTo(1));
            Assert.That(_scene.OwnerOf(e), Is.SameAs(_rec.Root.Children[0]));
        }

        [Test]
        public void Nested_BothShown()
        {
            var e = R();
            Frame(G(e, G(e)));
            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(4)); // root, e, inner group, e
            Assert.That(_rec.Root!.Children[1].Children[0].IsIgnored, Is.False);
            Assert.That(_scene.OwnerOf(e), Is.SameAs(_rec.Root.Children[0]));
        }

        [Test]
        public void StateOnlyFromPrimary_EventsFromEvery()
        {
            var sink = new ElementSink();
            var e = R(1, new SinkSlot(sink, 0));
            Frame(G(e, e));
            uint h0 = _rec.Root!.Children[0].BoundHandle, h1 = _rec.Root.Children[1].BoundHandle;

            var res = _rec.Sinks.Dispatch(new SinkRegistryTests.Poll()
                .Measured(h1, 9, 9).Bounds(h1, 9, 9, 9, 9).Error(h1, "second")
                .Measured(h0, 1, 2).Error(h0, "first")
                .Click(h0, 1).Click(h1, 2).Bytes);
            Assert.That(res.Applied, Is.EqualTo(4));
            Assert.That(res.Ignored, Is.EqualTo(3));
            Assert.That(sink[0].MeasuredSize, Is.EqualTo(SpaceMapping.Default.FromQt(new Vector2(1, 2))));
            Assert.That(sink[0].SceneBounds, Is.EqualTo(default(RectangleF)));
            Assert.That(sink[0].Error, Is.EqualTo("first"));
            Assert.That(sink[0].PendingClicks, Is.EqualTo(3), "clicks of both occurrences add up");

            // Removing the second occurrence leaves the state of the first alone.
            Frame(G(e));
            Assert.That(sink[0].Error, Is.EqualTo("first"));
        }

        [Test]
        public void FirstRemoved_SecondBecomesPrimary_ReboundWithNewHandle()
        {
            var sink = new ElementSink();
            var e = R(1, new SinkSlot(sink, 0));
            Frame(G(e, e));
            var second = _rec.Root!.Children[1];
            uint old = second.BoundHandle;
            _rec.Sinks.Dispatch(new SinkRegistryTests.Poll().Error(_rec.Root.Children[0].BoundHandle, "x").Bytes);

            // Position 0 gets another element; position 1 is skipped (reference-equal) but takes over.
            Frame(G(R(5), e));
            Assert.That(_scene.OwnerOf(e), Is.SameAs(second));
            Assert.That(second.BoundHandle, Is.Not.EqualTo(old));
            Assert.That(IsPrimaryHandle(second), Is.True);
            Assert.That(_rec.Sinks.TryResolve(old, out _), Is.False, "old handle released");
            Assert.That(_sink.OfOp("BIND"), Does.Contain($"BIND {second.Id} {second.BoundHandle} 0"), "QtNative sends its state again");
            Assert.That(sink[0].Error, Is.Null, "reset with the old primary; the new one reports its own");

            Frame(G(R(5), e));
            Assert.That(_sink.Commands, Is.Empty);
        }

        [Test]
        public void FirstDestroyed_SecondBecomesPrimary()
        {
            var e = R(1, new SinkSlot(new ElementSink(), 0));
            var tail = G(e);
            Frame(G(G(e), tail));
            var second = _rec.Root!.Children[1].Children[0];
            Assert.That(IsPrimaryHandle(second), Is.False);

            Frame(G(R(5), tail)); // type change at 0 destroys the first occurrence
            Assert.That(_scene.OwnerOf(e), Is.SameAs(second));
            Assert.That(IsPrimaryHandle(second), Is.True);
            Assert.That(_sink.OfOp("BIND").Count(), Is.EqualTo(1));
        }

        [Test]
        public void NewOccurrenceBeforeSkippedSubtree_TakesPrimary_NoDestroy()
        {
            var sink = new ElementSink();
            var e = R(1, new SinkSlot(sink, 0));
            var s = G(e);
            Frame(G(R(5), s));
            var inS = _rec.Root!.Children[1].Children[0];
            uint inSHandle = inS.BoundHandle;

            // e appears in front of the unchanged (reference-equal, skipped) subtree s, which still contains e.
            Frame(G(e, s));
            var first = _rec.Root.Children[0];
            Assert.That(_scene.OwnerOf(e), Is.SameAs(first));
            Assert.That(inS.IsIgnored, Is.False);
            Assert.That(_sink.OfOp("DESTROY"), Is.Empty);
            Assert.That(inS.BoundHandle, Is.EqualTo(inSHandle), "demoted without BIND");
            Assert.That(IsPrimaryHandle(inS), Is.False);
            Assert.That(IsPrimaryHandle(first), Is.True);
            Assert.That(sink[0].Warning, Is.Null);

            // Stable afterwards.
            Frame(G(e, s));
            Assert.That(_sink.Commands, Is.Empty);

            // And back: the occurrence in the skipped subtree is primary again (new handle + BIND).
            Frame(G(R(5), s));
            Assert.That(_scene.OwnerOf(e), Is.SameAs(inS));
            Assert.That(IsPrimaryHandle(inS), Is.True);
            Assert.That(_sink.OfOp("BIND"), Does.Contain($"BIND {inS.Id} {inS.BoundHandle} 0"));
        }

        [Test]
        public void MaskCountsAtItsPlace_BeforeChildren()
        {
            var e = R(1, new SinkSlot(new ElementSink(), 0));
            var inner = new Group { Effect = new MaskEffect(null, e, 0, 0, 1, 0, false), Children = ImmutableArray.Create<Element>(e) };
            Frame(G(e, inner));
            Assert.That(_scene.OccurrencesOf(e), Is.EqualTo(3));

            Frame(G(R(5), inner));
            var innerNode = _rec.Root!.Children[1];
            Assert.That(_scene.OwnerOf(e), Is.SameAs(innerNode.EffectObjects![0]));
        }

        [Test]
        public void NewInstanceEveryFrame_NoRebind()
        {
            var slot = new SinkSlot(new ElementSink(), 0);
            var e = R(1, slot);
            Frame(G(e, e));
            for (int i = 2; i < 5; i++)
            {
                var n = R(i, slot);
                Frame(G(n, n));
                Assert.That(_sink.OfOp("BIND"), Is.Empty);
                Assert.That(_scene.OwnerOf(n), Is.SameAs(_rec.Root!.Children[0]));
                Assert.That(IsPrimaryHandle(_rec.Root.Children[0]) && !IsPrimaryHandle(_rec.Root.Children[1]), Is.True);
            }
            Assert.That(_scene.Count, Is.EqualTo(2)); // root group + current n
        }

        // --- video: shown once only ---

        [Test]
        public void VideoTwiceInTree_SecondIgnored_WarningInSink()
        {
            var sink = new ElementSink();
            var v = V(new SinkSlot(sink, 0));
            var x = R(2);
            Frame(G(v, v, x));

            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(3)); // root, v, x
            Assert.That(_rec.Root!.Children[1].IsIgnored, Is.True);
            Assert.That(sink[0].Warning, Is.EqualTo(SceneRegistry.VideoDuplicateWarning));
            Assert.That(Structural, Does.Contain($"REPARENT {_rec.Root.Children[2].Id} {_rec.Root.Id} 1"));
            Assert.That(_sink.OfOp("BIND").Count(), Is.EqualTo(1));

            Frame(G(v, v, x));
            Assert.That(Structural, Is.Empty);

            Frame(G(v, x));
            Assert.That(sink[0].Warning, Is.Null);
            Assert.That(_scene.DuplicatesOf(v), Is.EqualTo(0));
        }

        [Test]
        public void Video_FirstOccurrenceWins_TakesOverFromSkippedSubtree()
        {
            var sink = new ElementSink();
            var v = V(new SinkSlot(sink, 0));
            var s = G(v);
            Frame(G(R(5), s));
            uint oldId = _rec.Root!.Children[1].Children[0].Id;

            Frame(G(v, s));
            Assert.That(_scene.OwnerOf(v), Is.SameAs(_rec.Root.Children[0]));
            Assert.That(_rec.Root.Children[1].Children[0].IsIgnored, Is.True);
            Assert.That(Structural, Does.Contain($"DESTROY {oldId}"));
            Assert.That(sink[0].Warning, Is.EqualTo(SceneRegistry.VideoDuplicateWarning));

            Frame(G(v, s));
            Assert.That(Structural, Is.Empty);
        }

        // --- across renderers (#126): every renderer shows the element, the first one has the primary occurrence ---

        [Test]
        public void TwoReconcilers_BothCreate_PrimaryMovesToSecondWhenFirstLetsGo()
        {
            var sink = new ElementSink();
            var e = R(1, new SinkSlot(sink, 0));
            var sink2 = new RecordingSink();
            var rec2 = new Reconciler(sink2, new IdAllocator(), _scene);

            Frame(G(e, e));
            var root2 = G(e);
            rec2.Update(root2, Space.DIPTopLeft);

            var n2 = rec2.Root!.Children[0];
            Assert.That(sink2.OfOp("CREATE").Count(), Is.EqualTo(2)); // group and e
            Assert.That(n2.IsIgnored, Is.False);
            Assert.That(sink[0].Warning, Is.Null);
            Assert.That(_scene.OccurrencesOf(e), Is.EqualTo(3));
            Assert.That(_scene.OwnerOf(e), Is.SameAs(_rec.Root!.Children[0]));
            Assert.That(_scene.OwnerReconcilerOf(e), Is.SameAs(_rec));
            Assert.That(n2.IsPrimary || rec2.Sinks.IsPrimary(n2.BoundHandle), Is.False);

            // Steady: nothing to send, regardless of order.
            sink2.Clear();
            rec2.Update(root2, Space.DIPTopLeft);
            Frame(G(e, e));
            Assert.That(sink2.Count, Is.EqualTo(0));
            Assert.That(Structural, Is.Empty);

            // One occurrence in the first renderer left → still the primary there.
            Frame(G(e));
            Assert.That(_scene.OwnerOf(e), Is.SameAs(_rec.Root.Children[0]));

            // The first renderer lets go → the second one owns it and picks its primary in its next update.
            Frame(null);
            Assert.That(_scene.OwnerReconcilerOf(e), Is.SameAs(rec2));
            Assert.That(_scene.OwnerOf(e), Is.Null);
            sink2.Clear();
            rec2.Update(root2, Space.DIPTopLeft);
            Assert.That(sink2.OfOp("CREATE").Count(), Is.EqualTo(0));
            Assert.That(sink2.OfOp("BIND").Count(), Is.EqualTo(1)); // re-bound: QtNative sends its state again
            Assert.That(_scene.OwnerOf(e), Is.SameAs(n2));
            Assert.That(n2.IsPrimary && rec2.Sinks.IsPrimary(n2.BoundHandle), Is.True);

            // The first renderer shows it again → a further occurrence there (registration order).
            Frame(G(e));
            Assert.That(_scene.OwnerOf(e), Is.SameAs(n2));
            Assert.That(_rec.Root.Children[0].IsPrimary || IsPrimaryHandle(_rec.Root.Children[0]), Is.False);
            Assert.That(_scene.OccurrencesOf(e), Is.EqualTo(2));

            _rec.Clear();
            rec2.Clear();
            Assert.That(_scene.Count, Is.EqualTo(0));
            Assert.That(_scene.SlotCount, Is.EqualTo(0));
        }

        [Test]
        public void TwoReconcilers_MoveInsideOwnerTree_KeepsPrimaryThere()
        {
            var e = R(1, new SinkSlot(new ElementSink(), 0));
            var x = R(2);
            var rec2 = new Reconciler(new RecordingSink(), new IdAllocator(), _scene);
            Frame(G(e, x));
            rec2.Update(G(e), Space.DIPTopLeft);

            // e leaves position 0 before it reaches position 1 in the same pass: no hand-over to the second renderer.
            Frame(G(x, e));
            Assert.That(_scene.OwnerReconcilerOf(e), Is.SameAs(_rec));
            Assert.That(_scene.OwnerOf(e), Is.SameAs(_rec.Root!.Children[1]));
            Assert.That(IsPrimaryHandle(_rec.Root.Children[1]), Is.True);
        }

        [Test]
        public void TwoReconcilers_ClearOfOwner_HandsOver()
        {
            var e = R(1, new SinkSlot(new ElementSink(), 0));
            var rec2 = new Reconciler(new RecordingSink(), new IdAllocator(), _scene);
            var root2 = G(e);
            Frame(G(e));
            rec2.Update(root2, Space.DIPTopLeft);

            _rec.Clear();
            rec2.Update(root2, Space.DIPTopLeft);
            Assert.That(_scene.OwnerOf(e), Is.SameAs(rec2.Root!.Children[0]));
            Assert.That(rec2.Sinks.IsPrimary(rec2.Root.Children[0].BoundHandle), Is.True);
        }

        [Test]
        public void VideoAcrossTwoReconcilers_FirstWins_SecondTakesOverWhenFree()
        {
            var sink = new ElementSink();
            var v = V(new SinkSlot(sink, 0));
            var sink2 = new RecordingSink();
            var rec2 = new Reconciler(sink2, new IdAllocator(), _scene);

            _rec.Update(G(v), Space.DIPTopLeft);
            var root2 = G(v);
            rec2.Update(root2, Space.DIPTopLeft);

            Assert.That(sink2.OfOp("CREATE").Count(), Is.EqualTo(1)); // only the group
            Assert.That(rec2.Root!.Children[0].IsIgnored, Is.True);
            Assert.That(sink[0].Warning, Is.EqualTo(SceneRegistry.VideoDuplicateWarning));

            // Steady: the first owner keeps it, regardless of order.
            sink2.Clear();
            rec2.Update(root2, Space.DIPTopLeft);
            _rec.Update(G(v), Space.DIPTopLeft);
            Assert.That(sink2.Count, Is.EqualTo(0));
            Assert.That(rec2.Root.Children[0].IsIgnored, Is.True);

            // The first renderer drops it → the second one shows it next frame (unchanged tree).
            _rec.Update(null, Space.DIPTopLeft);
            sink2.Clear();
            rec2.Update(root2, Space.DIPTopLeft);
            Assert.That(sink2.OfOp("CREATE").Count(), Is.EqualTo(1));
            Assert.That(rec2.Root.Children[0].IsIgnored, Is.False);
            Assert.That(sink[0].Warning, Is.Null);
            Assert.That(_scene.OwnerOf(v), Is.SameAs(rec2.Root.Children[0]));
        }

        [Test]
        public void AppRestart_NewRegistry_StaleOwnerIsNoDuplicate()
        {
            // App 1: a renderer owns the video e and is never disposed/cleared (unclean F8).
            var sink = new ElementSink();
            var e = V(new SinkSlot(sink, 0));
            Frame(G(e));
            Assert.That(_scene.OwnerOf(e), Is.Not.Null);

            // App 2 (F5): new app host → new registry; the same element instance is shown normally.
            var scene2 = new SceneRegistry();
            var sink2 = new RecordingSink();
            var rec2 = new Reconciler(sink2, new IdAllocator(), scene2);
            rec2.Update(G(e), Space.DIPTopLeft);
            Assert.That(sink2.OfOp("CREATE").Count(), Is.EqualTo(2));
            Assert.That(rec2.Root!.Children[0].IsIgnored, Is.False);
            Assert.That(scene2.OwnerOf(e), Is.SameAs(rec2.Root.Children[0]));
            Assert.That(sink[0].Warning, Is.Null);
        }

        [Test]
        public void ClearOfOwner_ReleasesElements()
        {
            var e = R();
            Frame(G(e, R(2), e));
            Assert.That(_scene.Count, Is.EqualTo(3));
            _rec.Clear();
            Assert.That(_scene.Count, Is.EqualTo(0));
        }

        [Test]
        public void RemoveInMiddle_NeighboursTakeOver_NoFalseDuplicates()
        {
            var a = R(1); var b = R(2); var c = R(3);
            Frame(G(a, b, c));
            Frame(G(a, c));
            Assert.That(Structural, Is.EqualTo(new[] { "DESTROY 4" }));
            Assert.That(_rec.Root!.Children.Any(n => n.IsIgnored), Is.False);
            Assert.That(_scene.OwnerOf(c), Is.SameAs(_rec.Root.Children[1]));

            // Insert in the middle (everything shifts back).
            Frame(G(a, b, c));
            Assert.That(_rec.Root.Children.Any(n => n.IsIgnored), Is.False);
            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(1));
        }

        // --- binding ---

        [Test]
        public void SinkBoundOnCreate_ChangeSendsNoFlags_UnbindSendsZero()
        {
            var sinkA = new ElementSink();
            var sinkB = new ElementSink();
            Frame(R(1, new SinkSlot(sinkA, 0)));
            uint id = _rec.Root!.Id;
            uint h = _rec.Root!.BoundHandle;
            Assert.That(h, Is.Not.Zero);
            Assert.That(Structural, Is.EqualTo(new[] { $"CREATE {id} {Protocol.Type.Rectangle}", $"REPARENT {id} 0 0", $"BIND {id} {h} 0" }));
            Assert.That(_rec.Bindings[id], Is.EqualTo(new SinkSlot(sinkA, 0)));

            // Same sink on a new description → no BIND.
            Frame(R(2, new SinkSlot(sinkA, 0)));
            Assert.That(_sink.OfOp("BIND"), Is.Empty);

            // Sink change → new BIND without flags (CancelPress is reserved, #320), old sink loses hover/press.
            sinkA[0].IsHovered = true;
            sinkA[0].IsPressed = true;
            Frame(R(2, new SinkSlot(sinkB, 0)));
            Assert.That(_rec.Root!.BoundHandle, Is.Not.EqualTo(h)); // new handle per slot change
            h = _rec.Root!.BoundHandle;
            Assert.That(_sink.OfOp("BIND"), Is.EqualTo(new[] { $"BIND {id} {h} 0" }));
            Assert.That(_rec.Bindings[id], Is.EqualTo(new SinkSlot(sinkB, 0)));
            Assert.That(sinkA[0].IsHovered || sinkA[0].IsPressed, Is.False);

            // Other slot of the same sink is a sink change too.
            Frame(R(2, new SinkSlot(sinkB, 1)));
            h = _rec.Root!.BoundHandle;
            Assert.That(_sink.OfOp("BIND"), Is.EqualTo(new[] { $"BIND {id} {h} 0" }));

            // No sink → unbind.
            Frame(R(2));
            Assert.That(_sink.OfOp("BIND"), Is.EqualTo(new[] { $"BIND {id} 0 0" }));
            Assert.That(_rec.Bindings, Is.Empty);
            Assert.That(_rec.Sinks.Count, Is.Zero);
        }

        [Test]
        public void SinksFollowPositions_InsertInMiddle_RebindWithoutFlags()
        {
            var sink = new ElementSink();
            Element E(int i) => R(i, new SinkSlot(sink, i));
            Frame(G(E(0), E(2)));
            Frame(G(E(0), E(1), E(2)));
            var c = _rec.Root!.Children;
            // Position 1 keeps its object, gets slot 1 (was 2) → new BIND without flags (CancelPress is reserved, #320); position 2 is new → plain BIND.
            Assert.That(_sink.OfOp("BIND"), Is.EqualTo(new[] { $"BIND {c[1].Id} {c[1].BoundHandle} 0", $"BIND {c[2].Id} {c[2].BoundHandle} 0" }));
            Assert.That(_rec.Bindings.Count, Is.EqualTo(3));
        }

        [Test]
        public void DestroyRemovesBindings()
        {
            var sink = new ElementSink();
            Frame(G(R(1, new SinkSlot(sink, 0)), G(R(2, new SinkSlot(sink, 1)))));
            Assert.That(_rec.Bindings.Count, Is.EqualTo(2));

            sink[1].IsHovered = true;
            Frame(G(R(1, new SinkSlot(sink, 0))));
            Assert.That(_rec.Bindings.Count, Is.EqualTo(1));
            Assert.That(_sink.OfOp("BIND"), Is.Empty); // QtNative drops bindings of destroyed items itself
            Assert.That(sink[1].IsHovered, Is.False);

            Frame(null);
            Assert.That(_rec.Bindings, Is.Empty);
        }

﻿        [Test]
        public void EveryOccurrenceIsBound()
        {
            var sink = new ElementSink();
            var e = R(1, new SinkSlot(sink, 0));
            Frame(G(e, e));
            Assert.That(_sink.OfOp("BIND").Count(), Is.EqualTo(2));
            Assert.That(_rec.Bindings.Count, Is.EqualTo(2));
            Assert.That(_rec.Sinks.Count, Is.EqualTo(2));
        }

        [Test]
        public void SteadyState_1000BoundChildren_OneChangePerFrame_NoAllocations()
        {
            const int n = 1000;
            var sink = new ElementSink();
            var children = new Element[n];
            for (int i = 0; i < n; i++)
                children[i] = R(1, new SinkSlot(sink, i));
            var alt = new Element[n]; // alternative description per child, same sink slot
            for (int i = 0; i < n; i++)
                alt[i] = R(2, new SinkSlot(sink, i));
            var rec = new Reconciler(new RecordingSinkNull(), new IdAllocator(), new SceneRegistry());
            var arr = ImmutableArray.Create(children);
            rec.Update(new Group { Children = arr }, Space.DIPTopLeft);

            var builder = arr.ToBuilder();
            void Step(int f)
            {
                int i = f % n;
                builder[i] = ReferenceEquals(builder[i], children[i]) ? alt[i] : children[i];
                rec.Update(new Group { Children = builder.ToImmutable() }, Space.DIPTopLeft);
            }
            for (int f = 0; f < 2 * n; f++)
                Step(f); // warm-up: every registry slot used once

            const int frames = 1000;
            // Element allocations of the test itself (Group + ImmutableArray) are measured separately.
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < frames; f++)
                new Group { Children = builder.ToImmutable() };
            long testOverhead = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int f = 0; f < frames; f++)
                Step(f);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before - testOverhead;
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds / frames;
            TestContext.Out.WriteLine($"1000 bound children, 1 change/frame: {ms:F4} ms/frame, {allocated} bytes (reconciler)");
            Assert.That(allocated, Is.LessThanOrEqualTo(0));
            // CI runners are slow and noisy (0.32 ms seen on Linux); this still catches a per-frame rebuild of all children.
            Assert.That(ms, Is.LessThan(1.0));
        }

        sealed class RecordingSinkNull : ICommandSink
        {
            public void Create(uint id, ushort type) { }
            public void Destroy(uint id) { }
            public void Reparent(uint id, uint parent, uint index) { }
            public void Bind(uint id, uint sinkHandle, ushort flags) { }
            public void SetF32(uint id, ushort prop, float value) { }
            public void SetI32(uint id, ushort prop, int value) { }
            public void SetBool(uint id, ushort prop, bool value) { }
            public void SetColor(uint id, ushort prop, float r, float g, float b, float a) { }
            public void SetVec2(uint id, ushort prop, float x, float y) { }
            public void SetRect(uint id, ushort prop, float x, float y, float width, float height) { }
            public void SetMat4(uint id, ushort prop, in Matrix m) { }
            public void SetString(uint id, ushort prop, ReadOnlySpan<char> value) { }
            public void SetRef(uint id, ushort prop, uint refId) { }
            public void SetU64(uint id, ushort prop, ulong value) { }
            public void BeginStyle(uint id, ushort flags) { }
            public void StyleColor(ushort field, byte state, float r, float g, float b, float a) { }
            public void StyleF32(ushort field, byte state, float value) { }
            public void StyleI32(ushort field, byte state, int value) { }
            public void StyleString(ushort field, byte state, ReadOnlySpan<char> value) { }
            public void EndStyle() { }
            public void SetIvec2(uint id, ushort prop, int x, int y) { }
            public void BeginUniforms(uint id, ushort flags) { }
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
