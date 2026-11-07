using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>Resync after a failed apply (#76): Reconciler + ResyncController against a fake QtNative that aborts a batch like the real one.</summary>
    public class ResyncTests
    {
        /// <summary>
        /// Interprets the recorded command lines like QtNative: abort at the first bad command (earlier ones stay
        /// applied), unknown id = -4, a negative Radius = -7, DESTROY removes the subtree, RESET everything (#92).
        /// </summary>
        sealed class FakeQtNative
        {
            readonly Dictionary<uint, (uint Parent, float Radius)> _items = new();
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
                            if (t[2] == Protocol.Prop.Radius.ToString(CultureInfo.InvariantCulture))
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

            void Remove(uint id)
            {
                foreach (var child in _items.Where(kv => kv.Value.Parent == id).Select(kv => kv.Key).ToList())
                    Remove(child);
                _items.Remove(id);
            }

            /// <summary>Tree as text: "[radius[children]]" per top-level object, e.g. "[0[1][2]]" = Group with radii 1 and 2.</summary>
            public string Scene() => Describe(0);

            public int Count => _items.Count;

            string Describe(uint parent)
            {
                var kids = _items.Where(kv => kv.Value.Parent == parent).OrderBy(kv => kv.Key)
                    .Select(kv => "[" + (kv.Value.Radius == 0 && _items.Any(x => x.Value.Parent == kv.Key) ? "g" : (kv.Value.Radius / 100f).ToString(CultureInfo.InvariantCulture)) + Describe(kv.Key) + "]");
                return string.Concat(kids);
            }
        }

        RecordingSink _sink = null!;
        FakeQtNative _qtNative = null!;
        Reconciler _rec = null!;
        ResyncController _resync = null!;
        int _lastRc;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _qtNative = new FakeQtNative();
            _rec = new Reconciler(_sink, new IdAllocator(), new SceneRegistry());
            _resync = new ResyncController(_rec, () => _qtNative.Apply(new[] { "RESET" }));
        }

        static Group G(params Element[] children) => new() { Children = ImmutableArray.Create(children) };
        static Rectangle R(float radius) => new() { Size = new Vector2(1, 1), Radius = radius };

        /// <summary>One frame: node Update (reconcile), then the pump (apply + result).</summary>
        void Frame(Element? input)
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
            var reason = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8("property 545: value 4 not allowed");
            try
            {
                var e = new Native.vlqt_apply_error { rc = -7, id = 3, op = Protocol.Op.Set, prop = Protocol.Prop.FillMode, reason = reason };
                Assert.That(WindowRenderer.DescribeApplyError(e), Is.EqualTo("Qt rejected FillMode: property 545: value 4 not allowed"));
                e.op = Protocol.Op.Reparent;
                Assert.That(WindowRenderer.DescribeApplyError(e), Does.StartWith("Qt rejected Reparent: "));
                e.op = Protocol.Op.Set;
                e.prop = 9999;
                Assert.That(WindowRenderer.DescribeApplyError(e), Does.StartWith("Qt rejected property 9999: "));
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(reason);
            }
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
            var scene = new SceneRegistry();
            var ids = new IdAllocator();
            var rec = new Reconciler(_sink, ids, scene);
            var qtNative = new FakeQtNative();
            var resync = new ResyncController(rec, () => qtNative.Apply(new[] { "RESET" }));
            var a = R(1);
            var tree = G(a, R(-1));
            if (resync.ShouldReconcile(tree)) rec.Update(tree, Space.DIPTopLeft);
            Assert.That(scene.OwnerOf(a), Is.Not.Null);
            int rc = qtNative.Apply(_sink.Commands);
            _sink.Clear();
            resync.OnApplyResult(rc);

            Assert.That(scene.OwnerOf(a), Is.Null, "element free again, so the rebuild is not treated as a duplicate");
            Assert.That(ids.Count, Is.EqualTo(0));
            Assert.That(ids.Allocate(), Is.EqualTo(1u), "ids restart");
        }
    }
}
