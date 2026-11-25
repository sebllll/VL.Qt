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
    /// <summary>Occurrences and placeholders (OccurrenceRegistry) and feedback binding (BIND without flags) of the reconciler.</summary>
    public class ReconcilerSceneTests
    {
        private RecordingSink _sink = null!;
        private OccurrenceRegistry _scene = null!;
        private InteractionRegistry _interactions = null!;
        private Reconciler _rec = null!;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _scene = new OccurrenceRegistry();
            _interactions = new InteractionRegistry();
            _rec = new Reconciler(_sink, new IdAllocator(), _scene, _interactions);
        }

        private static Group G(params Element[] children) => new() { Children = ImmutableArray.Create(children) };
        private static Rectangle R(float w = 1, FeedbackSlot feedback = default)
            => new() { Size = new Vector2(w, 1), Feedback = feedback };

        private string[] Structural => _sink.Commands.Where(c => !c.StartsWith("SET ", StringComparison.Ordinal)).ToArray();

        private void Frame(Element? root, Reconciler? rec = null)
        {
            _sink.Clear();
            (rec ?? _rec).Update(root, Space.DIPTopLeft);
        }

        // --- several occurrences in one tree ---

        private static Video V(FeedbackSlot feedback = default) => new() { Size = new Vector2(1, 1), Feedback = feedback };

        private bool IsPrimaryHandle(ItemMirror n) => _rec.FeedbackRouter.IsPrimary(n.BoundFeedbackId);

        [Test]
        public void TwoOccurrences_TwoObjects_OwnHandlesOnSameSlot()
        {
            var feedback = new ElementFeedback();
            var e = R(1, new FeedbackSlot(feedback, 0));
            var x = R(2);
            Frame(G(e, e, x));

            var c = _rec.Root!.Children;
            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(4)); // root, e twice, x
            Assert.That(c.Any(n => n.IsPlaceholder), Is.False);
            Assert.That(c[0].Id, Is.Not.EqualTo(c[1].Id));
            Assert.That(c[0].BoundFeedbackId, Is.Not.EqualTo(c[1].BoundFeedbackId));
            Assert.That(_sink.OfOp("BIND"), Is.EqualTo(new[] { $"BIND {c[0].Id} {c[0].BoundFeedbackId}", $"BIND {c[1].Id} {c[1].BoundFeedbackId}" }));
            Assert.That(_rec.FeedbackRouter.TryResolve(c[1].BoundFeedbackId, out var slot) && slot == new FeedbackSlot(feedback, 0), Is.True);
            Assert.That(_rec.Bindings.Count, Is.EqualTo(2));
            Assert.That(Structural, Does.Contain($"REPARENT {c[2].Id} {_rec.Root.Id} 2"));
            Assert.That(feedback[0].Warning, Is.Null);
            Assert.That(_scene.OccurrencesOf(e), Is.EqualTo(2));
            Assert.That(_scene.PrimaryOf(e), Is.SameAs(c[0]), "first in traversal order is primary");
            Assert.That(IsPrimaryHandle(c[0]) && !IsPrimaryHandle(c[1]), Is.True);

            // Same tree again → nothing.
            Frame(G(e, e, x));
            Assert.That(_sink.Commands, Is.Empty);

            // Second occurrence removed → one object left, nothing else.
            Frame(G(e, x));
            Assert.That(_scene.OccurrencesOf(e), Is.EqualTo(1));
            Assert.That(_scene.PrimaryOf(e), Is.SameAs(_rec.Root.Children[0]));
        }

        [Test]
        public void Nested_BothShown()
        {
            var e = R();
            Frame(G(e, G(e)));
            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(4)); // root, e, inner group, e
            Assert.That(_rec.Root!.Children[1].Children[0].IsPlaceholder, Is.False);
            Assert.That(_scene.PrimaryOf(e), Is.SameAs(_rec.Root.Children[0]));
        }

        [Test]
        public void StateOnlyFromPrimary_EventsFromEvery()
        {
            var feedback = new ElementFeedback();
            var e = R(1, new FeedbackSlot(feedback, 0));
            Frame(G(e, e));
            uint h0 = _rec.Root!.Children[0].BoundFeedbackId, h1 = _rec.Root.Children[1].BoundFeedbackId;

            var res = _rec.FeedbackRouter.Dispatch(new FeedbackRouterTests.Poll()
                .Measured(h1, 9, 9).Bounds(h1, 9, 9, 9, 9).Error(h1, "second")
                .Measured(h0, 1, 2).Error(h0, "first")
                .Click(h0, 1).Click(h1, 2).Bytes);
            Assert.That(res.Applied, Is.EqualTo(4));
            Assert.That(res.Ignored, Is.EqualTo(3));
            Assert.That(feedback[0].MeasuredSize, Is.EqualTo(SpaceMapping.Default.FromQt(new Vector2(1, 2))));
            Assert.That(feedback[0].SceneBounds, Is.EqualTo(default(RectangleF)));
            Assert.That(feedback[0].Error, Is.EqualTo("first"));
            Assert.That(feedback[0].PendingClicks, Is.EqualTo(3), "clicks of both occurrences add up");

            // Removing the second occurrence leaves the state of the first alone.
            Frame(G(e));
            Assert.That(feedback[0].Error, Is.EqualTo("first"));
        }

        [Test]
        public void FirstRemoved_SecondBecomesPrimary_ReboundWithNewHandle()
        {
            var feedback = new ElementFeedback();
            var e = R(1, new FeedbackSlot(feedback, 0));
            Frame(G(e, e));
            var second = _rec.Root!.Children[1];
            uint old = second.BoundFeedbackId;
            _rec.FeedbackRouter.Dispatch(new FeedbackRouterTests.Poll().Error(_rec.Root.Children[0].BoundFeedbackId, "x").Bytes);

            // Position 0 gets another element; position 1 is skipped (reference-equal) but takes over.
            Frame(G(R(5), e));
            Assert.That(_scene.PrimaryOf(e), Is.SameAs(second));
            Assert.That(second.BoundFeedbackId, Is.Not.EqualTo(old));
            Assert.That(IsPrimaryHandle(second), Is.True);
            Assert.That(_rec.FeedbackRouter.TryResolve(old, out _), Is.False, "old handle released");
            Assert.That(_sink.OfOp("BIND"), Does.Contain($"BIND {second.Id} {second.BoundFeedbackId}"), "QtNative sends its state again");
            Assert.That(feedback[0].Error, Is.Null, "reset with the old primary; the new one reports its own");

            Frame(G(R(5), e));
            Assert.That(_sink.Commands, Is.Empty);
        }

        /// <summary>Registry, mirror flag and feedback id agree on the primary of every bound mirror of <paramref name="rec"/>.</summary>
        private static void AssertPrimaryInOnePlace(Reconciler rec, OccurrenceRegistry scene)
        {
            void Walk(ItemMirror n)
            {
                if (!n.IsPlaceholder && n.Element is { } e && !n.BoundSlot.IsNone)
                {
                    var primary = ReferenceEquals(scene.PrimaryOf(e), n);
                    Assert.That(n.IsPrimary, Is.EqualTo(primary), $"flag of mirror {n.Id}");
                    Assert.That(rec.FeedbackRouter.IsPrimary(n.BoundFeedbackId), Is.EqualTo(primary), $"feedback id of mirror {n.Id}");
                }
                foreach (var c in n.Children)
                    Walk(c);
                if (n.Effect is { } effect)
                    foreach (var o in effect.Objects)
                        Walk(o);
            }
            Walk(rec.Root!);
        }

        [Test]
        public void PrimaryFlag_AgreesWithRegistryAndFeedbackId_AfterEveryUpdate()
        {
            var e = R(1, new FeedbackSlot(new ElementFeedback(), 0));
            var s = G(e);
            // Promotions of bound mirrors (a skipped subtree takes over, an earlier position takes over, the first lets go) and demotions.
            foreach (var root in new[] { G(R(5), s, e), G(e, s, e), G(R(5), s, e), G(R(5), s), G(s, R(5)), G(R(5), R(6)), G(e) })
            {
                Frame(root);
                AssertPrimaryInOnePlace(_rec, _scene);
            }
        }

        [Test]
        public void FurtherOccurrenceChangesSlot_BecomesPrimaryOfTheNewSlot_FormerSlotKeepsItsState()
        {
            var feedback = new ElementFeedback();
            var a = R(1, new FeedbackSlot(feedback, 0));
            var b = R(1, new FeedbackSlot(feedback, 1));
            Frame(G(a, a));
            var kids = _rec.Root!.Children;
            _rec.FeedbackRouter.Dispatch(new FeedbackRouterTests.Poll().Error(kids[0].BoundFeedbackId, "x").Bytes);

            Frame(G(a, b));
            Assert.That(_sink.OfOp("BIND"), Is.EqualTo(new[] { $"BIND {kids[1].Id} {kids[1].BoundFeedbackId}" }), "one BIND, for the new slot");
            Assert.That(feedback[0].Error, Is.EqualTo("x"), "the primary of the former slot is not touched");
            Assert.That(_scene.PrimaryOf(b), Is.SameAs(kids[1]));
            AssertPrimaryInOnePlace(_rec, _scene);
        }

        [Test]
        public void SpreadReordered_SlotDropsTheErrorOfItsFormerObject()
        {
            // Position 0 takes slot 1 before position 1 lets go of it: the demoted id clears the slot, the new BIND resends.
            var feedback = new ElementFeedback();
            Frame(G(R(1, new FeedbackSlot(feedback, 0)), R(2, new FeedbackSlot(feedback, 1))));
            var kids = _rec.Root!.Children;
            _rec.FeedbackRouter.Dispatch(new FeedbackRouterTests.Poll().Error(kids[1].BoundFeedbackId, "old").Bytes);
            Assert.That(feedback[1].Error, Is.EqualTo("old"));

            Frame(G(R(1, new FeedbackSlot(feedback, 1)), R(2, new FeedbackSlot(feedback, 0))));
            Assert.That(feedback[1].Error, Is.Null);
            Assert.That(_scene.PrimaryOf(R(1, new FeedbackSlot(feedback, 1))), Is.SameAs(kids[0]));
            Assert.That(IsPrimaryHandle(kids[0]) && IsPrimaryHandle(kids[1]), Is.True);
            Assert.That(_sink.OfOp("BIND").Count(), Is.EqualTo(2));
        }

        [Test]
        public void FirstDestroyed_SecondBecomesPrimary()
        {
            var e = R(1, new FeedbackSlot(new ElementFeedback(), 0));
            var tail = G(e);
            Frame(G(G(e), tail));
            var second = _rec.Root!.Children[1].Children[0];
            Assert.That(IsPrimaryHandle(second), Is.False);

            Frame(G(R(5), tail)); // type change at 0 destroys the first occurrence
            Assert.That(_scene.PrimaryOf(e), Is.SameAs(second));
            Assert.That(IsPrimaryHandle(second), Is.True);
            Assert.That(_sink.OfOp("BIND").Count(), Is.EqualTo(1));
        }

        [Test]
        public void NewOccurrenceBeforeSkippedSubtree_TakesPrimary_NoDestroy()
        {
            var feedback = new ElementFeedback();
            var e = R(1, new FeedbackSlot(feedback, 0));
            var s = G(e);
            Frame(G(R(5), s));
            var inS = _rec.Root!.Children[1].Children[0];
            uint inSHandle = inS.BoundFeedbackId;

            // e appears in front of the unchanged (reference-equal, skipped) subtree s, which still contains e.
            Frame(G(e, s));
            var first = _rec.Root.Children[0];
            Assert.That(_scene.PrimaryOf(e), Is.SameAs(first));
            Assert.That(inS.IsPlaceholder, Is.False);
            Assert.That(_sink.OfOp("DESTROY"), Is.Empty);
            Assert.That(inS.BoundFeedbackId, Is.EqualTo(inSHandle), "demoted without BIND");
            Assert.That(IsPrimaryHandle(inS), Is.False);
            Assert.That(IsPrimaryHandle(first), Is.True);
            Assert.That(feedback[0].Warning, Is.Null);

            // Stable afterwards.
            Frame(G(e, s));
            Assert.That(_sink.Commands, Is.Empty);

            // And back: the occurrence in the skipped subtree is primary again (new handle + BIND).
            Frame(G(R(5), s));
            Assert.That(_scene.PrimaryOf(e), Is.SameAs(inS));
            Assert.That(IsPrimaryHandle(inS), Is.True);
            Assert.That(_sink.OfOp("BIND"), Does.Contain($"BIND {inS.Id} {inS.BoundFeedbackId}"));
        }

        [Test]
        public void MaskCountsAtItsPlace_BeforeChildren()
        {
            var e = R(1, new FeedbackSlot(new ElementFeedback(), 0));
            var inner = new Group { Effect = new MaskEffect(null, e, 0, 0, 1, 0, false), Children = ImmutableArray.Create<Element>(e) };
            Frame(G(e, inner));
            Assert.That(_scene.OccurrencesOf(e), Is.EqualTo(3));

            Frame(G(R(5), inner));
            var innerNode = _rec.Root!.Children[1];
            Assert.That(_scene.PrimaryOf(e), Is.SameAs(innerNode.Effect!.Objects![0]));
        }

        [Test]
        public void NewInstanceEveryFrame_NoRebind()
        {
            var slot = new FeedbackSlot(new ElementFeedback(), 0);
            var e = R(1, slot);
            Frame(G(e, e));
            for (int i = 2; i < 5; i++)
            {
                var n = R(i, slot);
                Frame(G(n, n));
                Assert.That(_sink.OfOp("BIND"), Is.Empty);
                Assert.That(_scene.PrimaryOf(n), Is.SameAs(_rec.Root!.Children[0]));
                Assert.That(IsPrimaryHandle(_rec.Root.Children[0]) && !IsPrimaryHandle(_rec.Root.Children[1]), Is.True);
            }
            Assert.That(_scene.Count, Is.EqualTo(2)); // root group + current n
        }

        // --- video: shown once only ---

        [Test]
        public void VideoTwiceInTree_SecondIsPlaceholder_WarningInFeedback()
        {
            var feedback = new ElementFeedback();
            var v = V(new FeedbackSlot(feedback, 0));
            var x = R(2);
            Frame(G(v, v, x));

            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(3)); // root, v, x
            Assert.That(_rec.Root!.Children[1].IsPlaceholder, Is.True);
            Assert.That(feedback[0].Warning, Is.EqualTo(OccurrenceRegistry.VideoPlaceholderWarning));
            Assert.That(Structural, Does.Contain($"REPARENT {_rec.Root.Children[2].Id} {_rec.Root.Id} 1"));
            Assert.That(_sink.OfOp("BIND").Count(), Is.EqualTo(1));

            Frame(G(v, v, x));
            Assert.That(Structural, Is.Empty);

            Frame(G(v, x));
            Assert.That(feedback[0].Warning, Is.Null);
            Assert.That(_scene.PlaceholdersOf(v), Is.EqualTo(0));
        }

        [Test]
        public void Video_FirstOccurrenceWins_TakesOverFromSkippedSubtree()
        {
            var feedback = new ElementFeedback();
            var v = V(new FeedbackSlot(feedback, 0));
            var s = G(v);
            Frame(G(R(5), s));
            uint oldId = _rec.Root!.Children[1].Children[0].Id;

            Frame(G(v, s));
            Assert.That(_scene.PrimaryOf(v), Is.SameAs(_rec.Root.Children[0]));
            Assert.That(_rec.Root.Children[1].Children[0].IsPlaceholder, Is.True);
            Assert.That(Structural, Does.Contain($"DESTROY {oldId}"));
            Assert.That(feedback[0].Warning, Is.EqualTo(OccurrenceRegistry.VideoPlaceholderWarning));

            Frame(G(v, s));
            Assert.That(Structural, Is.Empty);
        }

        // --- across renderers: every renderer shows the element, the first one has the primary occurrence ---

        [Test]
        public void TwoReconcilers_BothCreate_PrimaryMovesToSecondWhenFirstLetsGo()
        {
            var feedback = new ElementFeedback();
            var e = R(1, new FeedbackSlot(feedback, 0));
            var sink2 = new RecordingSink();
            var rec2 = new Reconciler(sink2, new IdAllocator(), _scene, _interactions);

            Frame(G(e, e));
            var root2 = G(e);
            rec2.Update(root2, Space.DIPTopLeft);

            var n2 = rec2.Root!.Children[0];
            Assert.That(sink2.OfOp("CREATE").Count(), Is.EqualTo(2)); // group and e
            Assert.That(n2.IsPlaceholder, Is.False);
            Assert.That(feedback[0].Warning, Is.Null);
            Assert.That(_scene.OccurrencesOf(e), Is.EqualTo(3));
            Assert.That(_scene.PrimaryOf(e), Is.SameAs(_rec.Root!.Children[0]));
            Assert.That(_scene.OwnerOf(e), Is.SameAs(_rec));
            Assert.That(n2.IsPrimary || rec2.FeedbackRouter.IsPrimary(n2.BoundFeedbackId), Is.False);

            // Steady: nothing to send, regardless of order.
            sink2.Clear();
            rec2.Update(root2, Space.DIPTopLeft);
            Frame(G(e, e));
            Assert.That(sink2.Count, Is.EqualTo(0));
            Assert.That(Structural, Is.Empty);

            // One occurrence in the first renderer left → still the primary there.
            Frame(G(e));
            Assert.That(_scene.PrimaryOf(e), Is.SameAs(_rec.Root.Children[0]));

            // The first renderer lets go → the second one owns it and picks its primary in its next update.
            Frame(null);
            Assert.That(_scene.OwnerOf(e), Is.SameAs(rec2));
            Assert.That(_scene.PrimaryOf(e), Is.Null);
            sink2.Clear();
            rec2.Update(root2, Space.DIPTopLeft);
            Assert.That(sink2.OfOp("CREATE").Count(), Is.EqualTo(0));
            Assert.That(sink2.OfOp("BIND").Count(), Is.EqualTo(1)); // re-bound: QtNative sends its state again
            Assert.That(_scene.PrimaryOf(e), Is.SameAs(n2));
            Assert.That(n2.IsPrimary && rec2.FeedbackRouter.IsPrimary(n2.BoundFeedbackId), Is.True);

            // The first renderer shows it again → a further occurrence there (registration order).
            Frame(G(e));
            Assert.That(_scene.PrimaryOf(e), Is.SameAs(n2));
            Assert.That(_rec.Root.Children[0].IsPrimary || IsPrimaryHandle(_rec.Root.Children[0]), Is.False);
            Assert.That(_scene.OccurrencesOf(e), Is.EqualTo(2));

            _rec.Clear();
            rec2.Clear();
            Assert.That(_scene.Count, Is.EqualTo(0));
        }

        [Test]
        public void TwoReconcilers_MoveInsideOwnerTree_KeepsPrimaryThere()
        {
            var e = R(1, new FeedbackSlot(new ElementFeedback(), 0));
            var x = R(2);
            var rec2 = new Reconciler(new RecordingSink(), new IdAllocator(), _scene, _interactions);
            Frame(G(e, x));
            rec2.Update(G(e), Space.DIPTopLeft);

            // e leaves position 0 before it reaches position 1 in the same pass: no hand-over to the second renderer.
            Frame(G(x, e));
            Assert.That(_scene.OwnerOf(e), Is.SameAs(_rec));
            Assert.That(_scene.PrimaryOf(e), Is.SameAs(_rec.Root!.Children[1]));
            Assert.That(IsPrimaryHandle(_rec.Root.Children[1]), Is.True);
        }

        [Test]
        public void TwoReconcilers_ClearOfOwner_HandsOver()
        {
            var e = R(1, new FeedbackSlot(new ElementFeedback(), 0));
            var rec2 = new Reconciler(new RecordingSink(), new IdAllocator(), _scene, _interactions);
            var root2 = G(e);
            Frame(G(e));
            rec2.Update(root2, Space.DIPTopLeft);

            _rec.Clear();
            rec2.Update(root2, Space.DIPTopLeft);
            Assert.That(_scene.PrimaryOf(e), Is.SameAs(rec2.Root!.Children[0]));
            Assert.That(rec2.FeedbackRouter.IsPrimary(rec2.Root.Children[0].BoundFeedbackId), Is.True);
        }

        [Test]
        public void VideoAcrossTwoReconcilers_FirstWins_SecondTakesOverWhenFree()
        {
            var feedback = new ElementFeedback();
            var v = V(new FeedbackSlot(feedback, 0));
            var sink2 = new RecordingSink();
            var rec2 = new Reconciler(sink2, new IdAllocator(), _scene, _interactions);

            _rec.Update(G(v), Space.DIPTopLeft);
            var root2 = G(v);
            rec2.Update(root2, Space.DIPTopLeft);

            Assert.That(sink2.OfOp("CREATE").Count(), Is.EqualTo(1)); // only the group
            Assert.That(rec2.Root!.Children[0].IsPlaceholder, Is.True);
            Assert.That(feedback[0].Warning, Is.EqualTo(OccurrenceRegistry.VideoPlaceholderWarning));

            // Steady: the first owner keeps it, regardless of order.
            sink2.Clear();
            rec2.Update(root2, Space.DIPTopLeft);
            _rec.Update(G(v), Space.DIPTopLeft);
            Assert.That(sink2.Count, Is.EqualTo(0));
            Assert.That(rec2.Root.Children[0].IsPlaceholder, Is.True);

            // The first renderer drops it → the second one shows it next frame (unchanged tree).
            _rec.Update(null, Space.DIPTopLeft);
            sink2.Clear();
            rec2.Update(root2, Space.DIPTopLeft);
            Assert.That(sink2.OfOp("CREATE").Count(), Is.EqualTo(1));
            Assert.That(rec2.Root.Children[0].IsPlaceholder, Is.False);
            Assert.That(feedback[0].Warning, Is.Null);
            Assert.That(_scene.PrimaryOf(v), Is.SameAs(rec2.Root.Children[0]));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void VideoNewInstanceEveryFrame_AcrossTwoReconcilers_FirstKeepsThePlayer(bool firstUpdatesFirst)
        {
            // The key is the slot: whichever renderer updates first after a change, the first one keeps the player.
            var feedback = new ElementFeedback();
            var slot = new FeedbackSlot(feedback, 0);
            var sink2 = new RecordingSink();
            var rec2 = new Reconciler(sink2, new IdAllocator(), _scene, _interactions);
            Frame(G(V(slot)));
            rec2.Update(G(V(slot)), Space.DIPTopLeft);
            var player = _rec.Root!.Children[0];
            var placeholder = rec2.Root!.Children[0];

            for (int i = 0; i < 4; i++)
            {
                var next = new Video { Size = new Vector2(2 + i, 1), Feedback = slot };
                _sink.Clear();
                sink2.Clear();
                if (firstUpdatesFirst)
                {
                    _rec.Update(G(next), Space.DIPTopLeft);
                    rec2.Update(G(next), Space.DIPTopLeft);
                }
                else
                {
                    rec2.Update(G(next), Space.DIPTopLeft);
                    _rec.Update(G(next), Space.DIPTopLeft);
                }
                Assert.That(Structural, Is.Empty, $"frame {i}: no player destroyed or created");
                Assert.That(sink2.OfOp("CREATE"), Is.Empty, $"frame {i}");
                Assert.That(_rec.Root.Children[0], Is.SameAs(player));
                Assert.That(rec2.Root.Children[0], Is.SameAs(placeholder));
                Assert.That(_scene.OwnerOf(next), Is.SameAs(_rec));
                Assert.That(feedback[0].Warning, Is.EqualTo(OccurrenceRegistry.VideoPlaceholderWarning));
            }
        }

        [Test]
        public void VideoNewInstanceEveryFrame_TwiceInTree_OnePlayer_PlaceholderKept()
        {
            // Last frame's instance next to the current one (a FrameDelay): one element, one player.
            var feedback = new ElementFeedback();
            var slot = new FeedbackSlot(feedback, 0);
            var previous = V(slot);
            Frame(G(previous, previous));
            var player = _rec.Root!.Children[0];
            var placeholder = _rec.Root.Children[1];
            Assert.That(placeholder.IsPlaceholder, Is.True);

            for (int i = 0; i < 4; i++)
            {
                var current = new Video { Size = new Vector2(2 + i, 1), Feedback = slot };
                Frame(G(current, previous));
                Assert.That(Structural, Is.Empty, $"frame {i}");
                Assert.That(_rec.Root.Children[0], Is.SameAs(player));
                Assert.That(_rec.Root.Children[1], Is.SameAs(placeholder), "not rebuilt: the reconciler was not invalidated");
                Assert.That(_scene.PlaceholdersOf(current), Is.EqualTo(1));
                Assert.That(feedback[0].Warning, Is.EqualTo(OccurrenceRegistry.VideoPlaceholderWarning));
                previous = current;
            }
        }

        [Test]
        public void VideoDuplicate_DoesNotHideEffectWarning_WhichComesBackWhenTheDuplicateGoes()
        {
            var feedback = new ElementFeedback();
            var slot = new FeedbackSlot(feedback, 0);
            var v = new Video { Size = new Vector2(1, 1), Feedback = slot, Effect = new BlurEffect(new BlurEffect(null, 0.5f, 32, 0), 0.5f, 32, 0) };
            var sink2 = new RecordingSink();
            var rec2 = new Reconciler(sink2, new IdAllocator(), _scene, _interactions);

            _rec.Update(G(v), Space.DIPTopLeft);
            Assert.That(feedback[0].Warning, Does.Contain("Blur is used more than once").And.Not.Contain("Video"));

            rec2.Update(G(v), Space.DIPTopLeft);
            Assert.That(feedback[0].Warning, Does.Contain("Blur is used more than once").And.Contain(OccurrenceRegistry.VideoPlaceholderWarning));

            rec2.Update(null, Space.DIPTopLeft);
            Assert.That(feedback[0].Warning, Does.Contain("Blur is used more than once").And.Not.Contain("Video"), "the effect warning is shown again");
        }

        [Test]
        public void AppRestart_NewRegistry_StaleOwnerIsNoDuplicate()
        {
            // App 1: a renderer owns the video e and is never disposed/cleared (unclean F8).
            var feedback = new ElementFeedback();
            var e = V(new FeedbackSlot(feedback, 0));
            Frame(G(e));
            Assert.That(_scene.PrimaryOf(e), Is.Not.Null);

            // App 2 (F5): new app host → new registry; the same element instance is shown normally.
            var scene2 = new OccurrenceRegistry();
            var interactions2 = new InteractionRegistry();
            var sink2 = new RecordingSink();
            var rec2 = new Reconciler(sink2, new IdAllocator(), scene2, interactions2);
            rec2.Update(G(e), Space.DIPTopLeft);
            Assert.That(sink2.OfOp("CREATE").Count(), Is.EqualTo(2));
            Assert.That(rec2.Root!.Children[0].IsPlaceholder, Is.False);
            Assert.That(scene2.PrimaryOf(e), Is.SameAs(rec2.Root.Children[0]));
            Assert.That(feedback[0].Warning, Is.Null);
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
            Assert.That(_rec.Root!.Children.Any(n => n.IsPlaceholder), Is.False);
            Assert.That(_scene.PrimaryOf(c), Is.SameAs(_rec.Root.Children[1]));

            // Insert in the middle (everything shifts back).
            Frame(G(a, b, c));
            Assert.That(_rec.Root.Children.Any(n => n.IsPlaceholder), Is.False);
            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(1));
        }

        // --- binding ---

        [Test]
        public void FeedbackBoundOnCreate_ChangeSendsNoFlags_UnbindSendsZero()
        {
            var feedbackA = new ElementFeedback();
            var feedbackB = new ElementFeedback();
            Frame(R(1, new FeedbackSlot(feedbackA, 0)));
            uint id = _rec.Root!.Id;
            uint h = _rec.Root!.BoundFeedbackId;
            Assert.That(h, Is.Not.Zero);
            Assert.That(Structural, Is.EqualTo(new[] { $"CREATE {id} Rectangle", $"REPARENT {id} 0 0", $"BIND {id} {h}" }));
            Assert.That(_rec.Bindings[id], Is.EqualTo(new FeedbackSlot(feedbackA, 0)));

            // Same feedback on a new description → no BIND.
            Frame(R(2, new FeedbackSlot(feedbackA, 0)));
            Assert.That(_sink.OfOp("BIND"), Is.Empty);

            // Feedback change → new BIND , old feedback loses hover/press.
            feedbackA[0].IsHovered = true;
            feedbackA[0].IsPressed = true;
            Frame(R(2, new FeedbackSlot(feedbackB, 0)));
            Assert.That(_rec.Root!.BoundFeedbackId, Is.Not.EqualTo(h)); // new handle per slot change
            h = _rec.Root!.BoundFeedbackId;
            Assert.That(_sink.OfOp("BIND"), Is.EqualTo(new[] { $"BIND {id} {h}" }));
            Assert.That(_rec.Bindings[id], Is.EqualTo(new FeedbackSlot(feedbackB, 0)));
            Assert.That(feedbackA[0].IsHovered || feedbackA[0].IsPressed, Is.False);

            // Other slot of the same feedback is a feedback change too.
            Frame(R(2, new FeedbackSlot(feedbackB, 1)));
            h = _rec.Root!.BoundFeedbackId;
            Assert.That(_sink.OfOp("BIND"), Is.EqualTo(new[] { $"BIND {id} {h}" }));

            // No feedback → unbind.
            Frame(R(2));
            Assert.That(_sink.OfOp("BIND"), Is.EqualTo(new[] { $"BIND {id} 0" }));
            Assert.That(_rec.Bindings, Is.Empty);
            Assert.That(_rec.FeedbackRouter.Count, Is.Zero);
        }

        [Test]
        public void FeedbackFollowsPositions_InsertInMiddle_RebindWithoutFlags()
        {
            var feedback = new ElementFeedback();
            Element E(int i) => R(i, new FeedbackSlot(feedback, i));
            Frame(G(E(0), E(2)));
            Frame(G(E(0), E(1), E(2)));
            var c = _rec.Root!.Children;
            // Position 1 keeps its object, gets slot 1 (was 2) → new BIND without flags; position 2 is new → plain BIND.
            Assert.That(_sink.OfOp("BIND"), Is.EqualTo(new[] { $"BIND {c[1].Id} {c[1].BoundFeedbackId}", $"BIND {c[2].Id} {c[2].BoundFeedbackId}" }));
            Assert.That(_rec.Bindings.Count, Is.EqualTo(3));
        }

        [Test]
        public void DestroyRemovesBindings()
        {
            var feedback = new ElementFeedback();
            Frame(G(R(1, new FeedbackSlot(feedback, 0)), G(R(2, new FeedbackSlot(feedback, 1)))));
            Assert.That(_rec.Bindings.Count, Is.EqualTo(2));

            feedback[1].IsHovered = true;
            Frame(G(R(1, new FeedbackSlot(feedback, 0))));
            Assert.That(_rec.Bindings.Count, Is.EqualTo(1));
            Assert.That(_sink.OfOp("BIND"), Is.Empty); // QtNative drops bindings of destroyed items itself
            Assert.That(feedback[1].IsHovered, Is.False);

            Frame(null);
            Assert.That(_rec.Bindings, Is.Empty);
        }

        [Test]
        public void EveryOccurrenceIsBound()
        {
            var feedback = new ElementFeedback();
            var e = R(1, new FeedbackSlot(feedback, 0));
            Frame(G(e, e));
            Assert.That(_sink.OfOp("BIND").Count(), Is.EqualTo(2));
            Assert.That(_rec.Bindings.Count, Is.EqualTo(2));
            Assert.That(_rec.FeedbackRouter.Count, Is.EqualTo(2));
        }

        [Test]
        public void SteadyState_1000BoundChildren_OneChangePerFrame_NoAllocations()
        {
            const int n = 1000;
            var feedback = new ElementFeedback();
            var children = new Element[n];
            for (int i = 0; i < n; i++)
                children[i] = R(1, new FeedbackSlot(feedback, i));
            var alt = new Element[n]; // alternative description per child, same feedback slot
            for (int i = 0; i < n; i++)
                alt[i] = R(2, new FeedbackSlot(feedback, i));
            var rec = new Reconciler(new RecordingSinkNull(), new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
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
            long testOverhead = Allocations.Fewest(() => new Group { Children = builder.ToImmutable() }, frames);

            int frame = 0;
            long allocated = Allocations.Fewest(() => Step(frame++), frames) - testOverhead;
            TestContext.Out.WriteLine($"1000 bound children, 1 change/frame: {allocated} bytes (reconciler)");
            Assert.That(allocated, Is.LessThanOrEqualTo(0));
            // The timing of this path is ReconcilerPerfTests.Group_1000BoundChildren_OneChangePerFrame_Fast (category Perf).
        }

        // --- exception inside Update (REC-01, fault injection with ThrowingSink) ---

        private static int LiveObjects(RecordingSink sink)
        {
            var live = 0;
            foreach (var c in sink.Commands)
            {
                if (c.StartsWith("CREATE ", StringComparison.Ordinal)) live++;
                else if (c.StartsWith("DESTROY ", StringComparison.Ordinal)) live--;
            }
            return live;
        }

        [Test]
        public void Fault_InUpdate_PropagatesToTheCaller_AndTheNextUpdateDoesNotThrow()
        {
            var sink = new ThrowingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            rec.Update(G(R(1)), Space.DIPTopLeft);
            sink.Restart();
            sink.ThrowAt = 1; // the first command of the next frame: the Size SET of the changed rectangle

            Assert.Throws<ThrowingSink.InjectedFault>(() => rec.Update(G(R(2)), Space.DIPTopLeft));
            Assert.That(sink.Recorded.Commands, Is.Empty, "the throwing command is not recorded");

            sink.ThrowAt = 0;
            Assert.DoesNotThrow(() => rec.Update(G(R(2)), Space.DIPTopLeft));
        }

        // The resync of Scene after a reconcile that threw; its RESET wipes QtNative, so the recording starts again.
        private static ResyncController ResyncOf(Reconciler rec, ThrowingSink sink) => new(rec, () =>
        {
            sink.Restart();
            return 0;
        });

        [Test]
        public void Fault_AtSetOfAChangedElement_AfterTheResync_OneOccurrenceStaysPrimary()
        {
            var sink = new ThrowingSink();
            var scene = new OccurrenceRegistry();
            var interactions = new InteractionRegistry();
            var ids = new IdAllocator();
            var rec = new Reconciler(sink, ids, scene, interactions);
            var resync = ResyncOf(rec, sink);
            var slot = new FeedbackSlot(new ElementFeedback(), 0);
            rec.Update(G(R(1, slot)), Space.DIPTopLeft);

            var after = R(2, slot); // a new instance of the same node
            sink.Restart();
            sink.ThrowAt = 1; // its Size SET: the instance is already registered at the old mirror
            Assert.Throws<ThrowingSink.InjectedFault>(() => rec.Update(G(after), Space.DIPTopLeft));
            resync.OnReconcileFailed();

            Assert.That(scene.Count, Is.Zero, "nothing of the cut-short visit stays registered");
            Assert.That(ids.Count, Is.Zero);
            Assert.That(rec.FeedbackRouter.Count, Is.Zero);

            // The same tree again: built from scratch, one occurrence that reports state.
            sink.ThrowAt = 0;
            var tree = G(after);
            rec.Update(tree, Space.DIPTopLeft);
            var node = rec.Root!.Children[0];
            Assert.That(sink.Recorded.OfOp("CREATE").Select(c => c.Split(' ')[2]).ToArray(), Is.EqualTo(new[] { "Group", "Rectangle" }), sink.Recorded.ToString());
            Assert.That(scene.OccurrencesOf(after), Is.EqualTo(1));
            Assert.That(scene.PrimaryOf(after), Is.SameAs(node));
            Assert.That(rec.FeedbackRouter.IsPrimary(node.BoundFeedbackId), Is.True);
            Assert.That(node.IsPrimary, Is.True);

            // And stays stable.
            sink.Restart();
            rec.Update(tree, Space.DIPTopLeft);
            Assert.That(sink.Recorded.Commands, Is.Empty);
        }

        [Test]
        public void Fault_AtANewElement_AfterTheResync_LeavesNoOrphanObject()
        {
            var sink = new ThrowingSink();
            var scene = new OccurrenceRegistry();
            var interactions = new InteractionRegistry();
            var ids = new IdAllocator();
            var rec = new Reconciler(sink, ids, scene, interactions);
            var resync = ResyncOf(rec, sink);
            rec.Update(G(), Space.DIPTopLeft);
            var added = R(1, new FeedbackSlot(new ElementFeedback(), 0));
            sink.Restart();
            sink.ThrowAt = 2; // CREATE of the rectangle is written, its first SET throws: it never reaches its parent

            Assert.Throws<ThrowingSink.InjectedFault>(() => rec.Update(G(added), Space.DIPTopLeft));
            resync.OnReconcileFailed();
            Assert.That(scene.Count, Is.Zero);
            Assert.That(ids.Count, Is.Zero);

            sink.ThrowAt = 0;
            var tree = G(added);
            for (var frame = 0; frame < 3; frame++)
                rec.Update(tree, Space.DIPTopLeft);

            // Root and the rectangle are alive in QtNative, nothing else.
            Assert.That(LiveObjects(sink.Recorded), Is.EqualTo(2), sink.Recorded.ToString());
            Assert.That(scene.OccurrencesOf(added), Is.EqualTo(1));
            Assert.That(scene.Count, Is.EqualTo(2));
        }

        [Test]
        public void Fault_AtANewHandler_AfterTheResync_TheHandlerIsCreatedAgain()
        {
            var sink = new ThrowingSink();
            var scene = new OccurrenceRegistry();
            var interactions = new InteractionRegistry();
            var rec = new Reconciler(sink, new IdAllocator(), scene, interactions);
            var resync = ResyncOf(rec, sink);
            var hover = new FeedbackSlot(new ElementFeedback(), 0);
            var tree = G(new Rectangle { Size = new Vector2(1, 1), Interaction = new HoverInteraction(null, hover) });
            // 1 CREATE Group, 2 REPARENT, 3 CREATE Rectangle, 4 SET Size, 5 REPARENT, 6 CREATE handler (owner registered), 7 its REPARENT
            sink.ThrowAt = 7;

            Assert.Throws<ThrowingSink.InjectedFault>(() => rec.Update(tree, Space.DIPTopLeft));
            Assert.That(interactions.ActiveHandlerOf(hover), Is.Not.Null, "the cut-short visit registered the handler");
            resync.OnReconcileFailed();
            Assert.That(interactions.Count, Is.Zero);

            // Not a placeholder of an owner nobody reaches: the handler exists in QtNative again.
            sink.ThrowAt = 0;
            rec.Update(tree, Space.DIPTopLeft);
            Assert.That(sink.Recorded.OfOp("CREATE").Select(c => c.Split(' ')[2]).ToArray(),
                Is.EqualTo(new[] { "Group", "Rectangle", nameof(Protocol.ElementType.HoverHandler) }), sink.Recorded.ToString());
            Assert.That(interactions.ActiveHandlerOf(hover), Is.Not.Null);
        }

        [Test]
        public void LongString_WithARealCommandWriter_DoesNotThrowOutOfUpdate()
        {
            // 100,000 characters do not fit a u16 payload length; the u32 length of the command header carries them (PROTO-02).
            var rec = new Reconciler(new CommandWriter(), new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            var text = new Text { Content = new string('x', 100_000) };
            Assert.DoesNotThrow(() => rec.Update(G(text), Space.DIPTopLeft));
        }

        [Test]
        public void Fault_AtCreate_NextUpdate_BuildsTheTree()
        {
            var sink = new ThrowingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            sink.ThrowAt = 1; // CREATE of the root

            Assert.Throws<ThrowingSink.InjectedFault>(() => rec.Update(G(R(1)), Space.DIPTopLeft));

            sink.ThrowAt = 0;
            sink.Restart();
            rec.Update(G(R(1)), Space.DIPTopLeft);
            Assert.That(sink.Recorded.OfOp("CREATE").Select(c => c.Split(' ')[2]).ToArray(), Is.EqualTo(new[] { "Group", "Rectangle" }), sink.Recorded.ToString());
        }

        private sealed class RecordingSinkNull : NullCommandSink { }
    }
}
