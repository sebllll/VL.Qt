using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Interop;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>
    /// Resync after a failed apply: Reconciler + ResyncController against a fake QtNative that aborts a batch like the real
    /// one; and after a reconcile that throws, through Scene without a native scene.
    /// </summary>
    public class ResyncTests
    {
        /// <summary>
        /// Interprets the recorded command lines like QtNative: abort at the first bad command (earlier ones stay
        /// applied), unknown id = -4, a negative Radius = -7, DESTROY removes the subtree, RESET everything.
        /// </summary>
        private sealed class FakeQtNative
        {
            private readonly Dictionary<uint, (uint Parent, float Radius)> _items = new();
            public int Applies;

            public int Apply(IEnumerable<string> commands)
            {
                Applies++;
                foreach (var c in commands)
                {
                    if (c == "RESET")
                    {
                        _items.Clear();
                        continue;
                    }
                    var t = c.Split(' ');
                    uint id = uint.Parse(t[1], CultureInfo.InvariantCulture);
                    switch (t[0])
                    {
                        case "CREATE":
                            if (id == 0 || _items.ContainsKey(id)) return -8;
                            _items[id] = (0, 0);
                            break;
                        case "DESTROY":
                            if (!_items.ContainsKey(id)) return -4;
                            Remove(id);
                            break;
                        case "REPARENT":
                            if (!_items.TryGetValue(id, out var it)) return -4;
                            _items[id] = (uint.Parse(t[2], CultureInfo.InvariantCulture), it.Radius);
                            break;
                        case "SET":
                            if (!_items.TryGetValue(id, out var s)) return -4;
                            if (t[2] == nameof(Protocol.PropertyId.Radius))
                            {
                                float v = float.Parse(t[3][4..^1], CultureInfo.InvariantCulture);
                                if (v < 0) return -7;
                                _items[id] = (s.Parent, v);
                            }
                            break;
                    }
                }
                return 0;
            }

            private void Remove(uint id)
            {
                foreach (var child in _items.Where(kv => kv.Value.Parent == id).Select(kv => kv.Key).ToList())
                    Remove(child);
                _items.Remove(id);
            }

            /// <summary>Tree as text: "[radius[children]]" per top-level object, e.g. "[0[1][2]]" = Group with radii 1 and 2.</summary>
            public string Scene() => Describe(0);

            public int Count => _items.Count;

            private string Describe(uint parent)
            {
                var kids = _items.Where(kv => kv.Value.Parent == parent).OrderBy(kv => kv.Key)
                    .Select(kv => "[" + (kv.Value.Radius == 0 && _items.Any(x => x.Value.Parent == kv.Key) ? "g" : (kv.Value.Radius / 100f).ToString(CultureInfo.InvariantCulture)) + Describe(kv.Key) + "]");
                return string.Concat(kids);
            }
        }

        private RecordingSink _sink = null!;
        private FakeQtNative _qtNative = null!;
        private Reconciler _rec = null!;
        private ResyncController _resync = null!;
        private int _lastRc;
        private int _resets;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _qtNative = new FakeQtNative();
            _rec = new Reconciler(_sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            _resets = 0;
            _resync = new ResyncController(_rec, () =>
            {
                _resets++;
                return _qtNative.Apply(new[] { "RESET" });
            });
        }

        private static Group G(params Element[] children) => new() { Children = ImmutableArray.Create(children) };
        private static Rectangle R(float radius) => new() { Size = new Vector2(1, 1), Radius = radius };

        /// <summary>One frame: node Update (reconcile), then the pump (apply + result).</summary>
        private void Frame(Element? input)
        {
            if (_resync.ShouldReconcile(input))
                _rec.Update(input, Space.DIPTopLeft);
            _lastRc = _sink.Count == 0 ? 0 : _qtNative.Apply(_sink.Commands);
            _sink.Clear();
            _resync.OnApplyResult(_lastRc);
        }

        [Test]
        public void FailedApply_NextFrameRebuildsScene()
        {
            var bad = G(R(1), R(-1), R(2));
            Frame(bad);
            Assert.That(_lastRc, Is.EqualTo(-7));
            Assert.That(_resync.Resyncs, Is.EqualTo(1));
            Assert.That(_qtNative.Count, Is.EqualTo(0), "QtNative wiped, no leftovers of the aborted batch");

            // The user fixes the value in the frame after the error: full rebuild with the new tree.
            Frame(G(R(1), R(3), R(2)));
            Assert.That(_lastRc, Is.EqualTo(0));
            Assert.That(_qtNative.Scene(), Is.EqualTo("[g[1][3][2]]"));
            Assert.That(_resync.Resyncs, Is.EqualTo(1));
            Assert.That(_resync.GaveUp, Is.False);
        }

        [Test]
        public void FailedApply_SameInput_OneRetryThenGivesUp_NoEndlessLoop()
        {
            var bad = G(R(1), R(-1));
            Frame(bad);
            Assert.That(_resync.Resyncs, Is.EqualTo(1));

            Frame(bad); // the one retry, fails again
            Assert.That(_lastRc, Is.EqualTo(-7));
            Assert.That(_resync.Resyncs, Is.EqualTo(2));
            Assert.That(_resync.GaveUp, Is.True);

            int applies = _qtNative.Applies;
            for (int i = 0; i < 20; i++)
                Frame(bad);
            Assert.That(_qtNative.Applies, Is.EqualTo(applies), "no more commands while the input stays the failing one");
            Assert.That(_resync.Resyncs, Is.EqualTo(2));
            Assert.That(_qtNative.Count, Is.EqualTo(0), "scene stays empty rather than half built");

            Frame(G(R(1), R(4))); // a changed input ends the pause
            Assert.That(_lastRc, Is.EqualTo(0));
            Assert.That(_qtNative.Scene(), Is.EqualTo("[g[1][4]]"));
            Assert.That(_resync.GaveUp, Is.False);
        }

        [Test]
        public void FailingEveryApply_NewInputEveryFrame_PauseDoubles()
        {
            // Animated input: a new tree every frame, and Qt rejects every one of them.
            for (int i = 0; i < 100; i++)
                Frame(G(R(1), R(-1 - i)));
            Assert.That(_resets, Is.InRange(5, 8), "no rebuild every second frame; pauses of 2, 4, ... 64 frames");
            Assert.That(_resync.GaveUp, Is.True);
        }

        [Test]
        public void Pause_StartsAtTwoAgainAfterASuccessfulApply()
        {
            Frame(G(R(-1)));
            Frame(G(R(-2))); // the retry fails too: gives up, the next pause will be 4 frames
            Frame(G(R(1))); // 1st frame of the pause of 2
            Frame(G(R(2))); // new input after the pause: applied
            Assert.That(_lastRc, Is.EqualTo(0));
            Assert.That(_resync.GaveUp, Is.False);

            Frame(G(R(-3)));
            Frame(G(R(-4))); // gives up again: the pause is 2 frames, not 4
            Frame(G(R(3)));
            Frame(G(R(4)));
            Assert.That(_lastRc, Is.EqualTo(0));
            Assert.That(_qtNative.Scene(), Is.EqualTo("[g[4]]"));
        }

        [Test]
        public void Scene_ReconcileThrows_DropsTheFrameAndRebuildsNextFrame()
        {
            var registry = new OccurrenceRegistry();
            var interactions = new InteractionRegistry();
            var scene = new Scene(registry, interactions, SceneHandle.Null);
            // Above the size limit of one command (PROTO-02, B7): a failed reconcile, not a truncation.
            var tooLong = new Text { Content = new string('€', Protocol.MaxCommandPayload / 3 + 1) };

            Assert.DoesNotThrow(() => scene.Update(G(R(1), tooLong), Space.DIPTopLeft, default, true));
            Assert.That(scene.ReconcileFault, Is.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(scene.Commands.Length, Is.Zero, "nothing of the failed frame goes to QtNative");
            Assert.That(scene.Resyncs, Is.EqualTo(1));
            Assert.That(registry.Count, Is.Zero);
            Assert.That(scene.Reconciler.Root, Is.Null);

            scene.Update(G(R(1), new Text { Content = "ok" }), Space.DIPTopLeft, default, true);
            Assert.That(scene.ReconcileFault, Is.Null);
            Assert.That(registry.Count, Is.EqualTo(3));
            Assert.That(scene.Reconciler.Root!.Children, Has.Count.EqualTo(2));
            Assert.That(scene.Commands.Length, Is.GreaterThan(0), "clear color, Clear flag, Space and the scene, sent again");
        }

        [Test]
        public void Scene_ReconcileThrowsTwiceForTheSameInput_GivesUpUntilTheInputChanges()
        {
            var registry = new OccurrenceRegistry();
            var interactions = new InteractionRegistry();
            var scene = new Scene(registry, interactions, SceneHandle.Null);
            var bad = G(new Text { Content = new string('€', Protocol.MaxCommandPayload / 3 + 1) });

            scene.Update(bad, Space.DIPTopLeft, default, true);
            scene.Update(bad, Space.DIPTopLeft, default, true); // the one retry
            Assert.That(scene.Resyncs, Is.EqualTo(2));
            var fault = scene.ReconcileFault;
            Assert.That(fault, Is.Not.Null);

            for (int i = 0; i < 5; i++)
                scene.Update(bad, Space.DIPTopLeft, default, true);
            Assert.That(scene.Resyncs, Is.EqualTo(2), "no more attempts while the input stays the failing one");
            Assert.That(scene.ReconcileFault, Is.SameAs(fault), "the fault stays shown meanwhile");

            scene.Update(G(R(1)), Space.DIPTopLeft, default, true);
            Assert.That(scene.ReconcileFault, Is.Null);
            Assert.That(registry.Count, Is.EqualTo(2));
        }

        [Test]
        public void FailureInSteadyState_ChangedTree_ResyncsAndMatchesAfterwards()
        {
            Frame(G(R(1), R(2), R(3)));
            Assert.That(_qtNative.Scene(), Is.EqualTo("[g[1][2][3]]"));

            // Several structural changes in one batch; the bad SET aborts it in the middle.
            var bad = G(R(5), R(-2), R(3), R(6));
            Frame(bad);
            Assert.That(_lastRc, Is.EqualTo(-7));

            var good = G(R(5), R(2), R(3), R(6));
            Frame(good);
            Assert.That(_lastRc, Is.EqualTo(0));
            Assert.That(_qtNative.Scene(), Is.EqualTo("[g[5][2][3][6]]"));
            Assert.That(_rec.Root!.Children, Has.Count.EqualTo(4));

            // Back in normal operation: a small change is a single SET again.
            _sink.Clear();
            _rec.Update(G(R(5), R(2), R(3), R(7)), Space.DIPTopLeft);
            Assert.That(_sink.Commands, Has.Count.EqualTo(1));
        }

        [Test]
        public void DescribeApplyError_NamesPropertyOrOp()
        {
            var e = new ApplyError(-7, 0, 3, Protocol.Op.Set, Protocol.PropertyId.FillMode, "property 545: value 4 not allowed");
            Assert.That(Scene.DescribeApplyError(e), Is.EqualTo("Qt rejected FillMode: property 545: value 4 not allowed"));
            Assert.That(Scene.DescribeApplyError(e with { Op = Protocol.Op.Reparent }), Does.StartWith("Qt rejected Reparent: "));
            Assert.That(Scene.DescribeApplyError(e with { Prop = 9999 }), Does.StartWith("Qt rejected property 9999: "));
        }

        [Test]
        public void CopyApplyError_CopiesTheReasonBeforeTheNativeTextIsFreed()
        {
            var reason = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8("property 545: value 4 not allowed");
            var raw = new Native.vlqt_apply_error { rc = -7, command = 2, id = 3, op = Protocol.Op.Set, prop = Protocol.PropertyId.FillMode, reason = reason };
            var copy = Native.CopyApplyError(in raw);
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(reason);
            Assert.That(copy, Is.EqualTo(new ApplyError(-7, 2, 3, Protocol.Op.Set, Protocol.PropertyId.FillMode, "property 545: value 4 not allowed")));
            Assert.That(Native.CopyApplyError(default(Native.vlqt_apply_error)).Reason, Is.SameAs(""), "no reason, no allocation");
        }

        [Test]
        public void SuccessfulFramesNeverResync()
        {
            for (int i = 0; i < 10; i++)
                Frame(G(R(i), R(2)));
            Assert.That(_resync.Resyncs, Is.EqualTo(0));
        }

        [Test]
        public void Resync_ReleasesElementsAndIds()
        {
            var scene = new OccurrenceRegistry();
            var interactions = new InteractionRegistry();
            var ids = new IdAllocator();
            var rec = new Reconciler(_sink, ids, scene, interactions);
            var qtNative = new FakeQtNative();
            var resync = new ResyncController(rec, () => qtNative.Apply(new[] { "RESET" }));
            var a = R(1);
            var tree = G(a, R(-1));
            if (resync.ShouldReconcile(tree)) rec.Update(tree, Space.DIPTopLeft);
            Assert.That(scene.PrimaryOf(a), Is.Not.Null);
            int rc = qtNative.Apply(_sink.Commands);
            _sink.Clear();
            resync.OnApplyResult(rc);

            Assert.That(scene.PrimaryOf(a), Is.Null, "element free again, so the rebuild is not treated as a duplicate");
            Assert.That(ids.Count, Is.EqualTo(0));
            Assert.That(ids.Allocate(), Is.EqualTo(1u), "ids restart");
        }
    }
}
