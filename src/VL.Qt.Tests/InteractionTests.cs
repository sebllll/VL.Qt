using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>
    /// Interaction chain (#319): handler diff of the reconciler (create / unchanged / properties /
    /// remove / duplicates / element destroy / resync) and the Hover node (change detection, pull into channels).
    /// </summary>
    public class InteractionTests
    {
        const ushort hover = Protocol.Type.HoverHandler;     // 32

        RecordingSink _sink = null!;
        IdAllocator _ids = null!;
        SceneRegistry _scene = null!;
        Reconciler _rec = null!;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _ids = new IdAllocator();
            _scene = new SceneRegistry();
            _rec = new Reconciler(_sink, _ids, _scene);
        }

        static Group G(params Element[] children) => new() { Children = ImmutableArray.Create(children) };

        // Default bounds/fill: no element SETs, no element sink: no element BIND (only the handler commands remain).
        static Rectangle R(Interaction? interaction, float radius = 0) => new() { Interaction = interaction, Radius = radius };

        static HoverInteraction H(ElementSink sink, Interaction? previous = null)
            => new(previous, new SinkSlot(sink, 0));

        void Frame(Element? root)
        {
            _sink.Clear();
            _rec.Update(root, Space.DIPTopLeft);
        }

        string[] Commands => _sink.Commands.ToArray();

        [Test]
        public void NewChain_CreateSetReparentBind_AfterTheItem()
        {
            var a = new ElementSink();
            Frame(R(H(a)));

            Assert.That(Commands, Is.EqualTo(new[]
            {
                "CREATE 1 3", "REPARENT 1 0 0",
                $"CREATE 2 {hover}", "REPARENT 2 1 0", "BIND 2 1 0",
            }));
            Assert.That(_rec.Bindings[2], Is.EqualTo(new SinkSlot(a, 0)));
            Assert.That(_rec.Sinks.Count, Is.EqualTo(1));
            Assert.That(_scene.HandlerOwnerOf(new SinkSlot(a, 0)), Is.Not.Null);
        }

        [Test]
        public void DefaultProperties_NotSent()
        {
            Frame(R(H(new ElementSink())));
            Assert.That(_sink.OfOp("SET"), Is.Empty, "Hover has no properties (no position since #320)");
        }

        [Test]
        public void UnchangedFrame_NoOps_AlsoWhenOnlyTheElementChanges()
        {
            var chain = H(new ElementSink());
            var root = R(chain);
            Frame(root);
            Frame(root);
            Assert.That(Commands, Is.Empty, "reference-equal root");

            Frame(R(chain, radius: 0.1f));
            Assert.That(Commands, Is.EqualTo(new[] { $"SET 1 {Protocol.Prop.Radius} f32(10)" }), "same chain instance -> no handler ops");
        }

        [Test]
        public void AddLink_CreatesOnlyTheNewHandler()
        {
            var a = new ElementSink();
            var b = new ElementSink();
            var first = H(a);
            Frame(R(first));

            Frame(R(H(b, first)));
            Assert.That(Commands, Is.EqualTo(new[] { $"CREATE 3 {hover}", "REPARENT 3 1 0", "BIND 3 2 0" }));
            Assert.That(_ids.Count, Is.EqualTo(3));
        }

        [Test]
        public void RemoveLink_DestroysHandler_FreesIdAndHandle_ResetsHover()
        {
            var a = new ElementSink();
            var b = new ElementSink();
            var first = H(a);
            Frame(R(H(b, first)));
            Assert.That(_ids.Count, Is.EqualTo(3));
            b[0].IsHovered = true;

            Frame(R(first));
            Assert.That(Commands, Is.EqualTo(new[] { "DESTROY 3" }));
            Assert.That(_ids.Count, Is.EqualTo(2));
            Assert.That(_rec.Sinks.Count, Is.EqualTo(1));
            Assert.That(_rec.Bindings.ContainsKey(3), Is.False);
            Assert.That(b.IsHovered(), Is.False, "released handle resets the slot");
            Assert.That(_scene.HandlerOwnerOf(new SinkSlot(b, 0)), Is.Null);

            Frame(R(null));
            Assert.That(Commands, Is.EqualTo(new[] { "DESTROY 2" }));
            Assert.That(_scene.HandlerCount, Is.Zero);
        }

        [Test]
        public void ChainOrder_DoesNotMatter()
        {
            var a = new ElementSink();
            var b = new ElementSink();
            Frame(R(H(b, H(a))));
            Frame(R(H(a, H(b))));
            Assert.That(Commands, Is.Empty);
        }

        [Test]
        public void Duplicate_AtTwoElements()
        {
            var a = new ElementSink();
            var chain = H(a);
            Frame(G(R(chain), R(chain, radius: 0.1f)));

            // Group 1, first rectangle 2 + its handler 3, second rectangle 4 without handler.
            Assert.That(_sink.OfOp("CREATE").ToArray(), Is.EqualTo(new[] { "CREATE 1 1", "CREATE 2 3", $"CREATE 3 {hover}", "CREATE 4 3" }));
            Assert.That(Commands, Does.Contain("REPARENT 3 2 0"));
            Assert.That(a.Warning, Is.EqualTo(SceneRegistry.InteractionDuplicateWarning));
            Assert.That(_rec.Sinks.Count, Is.EqualTo(1));

            // The first element lets go: the second takes over one update later (reconciler invalidated).
            var second = R(chain, radius: 0.1f);
            Frame(G(R(null), second));
            Assert.That(Commands, Is.EqualTo(new[] { "DESTROY 3" }));
            Frame(G(R(null), second));
            Assert.That(Commands, Is.EqualTo(new[] { $"CREATE 3 {hover}", "REPARENT 3 4 0", "BIND 3 2 0" }));
            Assert.That(a.Warning, Is.Null, "warning cleared");

            Frame(G(R(null), second));
            Assert.That(Commands, Is.Empty);
        }

        [Test]
        public void Duplicate_InOneChain_OneHandler()
        {
            var a = new ElementSink();
            var slot = new SinkSlot(a, 0);
            var chain = new HoverInteraction(new HoverInteraction(null, slot), slot);
            Frame(R(chain));
            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(2), "item + one handler");
            Assert.That(a.Warning, Is.EqualTo(SceneRegistry.InteractionDuplicateWarning));

            Frame(R(H(a)));
            Assert.That(Commands, Is.Empty, "the remaining link keeps the handler");
            Assert.That(a.Warning, Is.Null);
        }

        [Test]
        public void Duplicate_AcrossRenderers_FirstKeeps()
        {
            var otherSink = new RecordingSink();
            var other = new Reconciler(otherSink, new IdAllocator(), _scene);
            var a = new ElementSink();
            var chain = H(a);
            Frame(R(chain));
            other.Update(R(chain, radius: 0.1f), Space.DIPTopLeft);
            Assert.That(otherSink.OfOp("CREATE").ToArray(), Is.EqualTo(new[] { "CREATE 1 3" }));
            Assert.That(a.Warning, Is.EqualTo(SceneRegistry.InteractionDuplicateWarning));

            Frame(null);
            otherSink.Clear();
            other.Update(R(chain, radius: 0.1f), Space.DIPTopLeft);
            Assert.That(otherSink.OfOp("CREATE").ToArray(), Is.EqualTo(new[] { $"CREATE 2 {hover}" }), "takes over after the owner is gone");
            Assert.That(a.Warning, Is.Null);
        }

        [Test]
        public void ElementDestroy_FreesHandlerIds_NoHandlerDestroy()
        {
            var a = new ElementSink();
            Frame(G(R(H(a)), R(null)));
            Assert.That(_ids.Count, Is.EqualTo(4));
            a[0].IsHovered = true;

            Frame(G(R(null)));
            Assert.That(Commands.Where(c => c.StartsWith("DESTROY")).ToArray(), Is.EqualTo(new[] { "DESTROY 3", "DESTROY 4" }),
                "handler of the first element by the chain diff, second element by the child diff");

            Frame(null);
            Assert.That(Commands, Is.EqualTo(new[] { "DESTROY 1" }));
            Assert.That(_ids.Count, Is.Zero);
            Assert.That(_rec.Sinks.Count, Is.Zero);
            Assert.That(_scene.HandlerCount, Is.Zero);
            Assert.That(a.IsHovered(), Is.False);
        }

        [Test]
        public void ElementDestroy_WithHandler_OnlyItemDestroyed()
        {
            var a = new ElementSink();
            Frame(G(R(null), R(H(a))));
            Assert.That(_ids.Count, Is.EqualTo(4));

            Frame(G(R(null)));
            Assert.That(Commands, Is.EqualTo(new[] { "DESTROY 3" }), "QtNative removes the handler with its item");
            Assert.That(_ids.Count, Is.EqualTo(2));
            Assert.That(_rec.Sinks.Count, Is.Zero);
            Assert.That(_scene.HandlerCount, Is.Zero);
        }

        [Test]
        public void Resync_ReplaysHandlers()
        {
            var a = new ElementSink();
            var root = R(H(a));
            Frame(root);
            var first = Commands;

            _rec.Discard();
            Assert.That(_scene.HandlerCount, Is.Zero);
            Assert.That(_rec.Sinks.Count, Is.Zero);
            Frame(root);
            Assert.That(Commands.Select(c => c.Replace("BIND 2 2 0", "BIND 2 1 0")), Is.EqualTo(first), "same commands, new sink handle");
        }

        [Test]
        public void HandlerTypeChange_DestroyAndCreate()
        {
            var a = new ElementSink();
            Frame(R(H(a)));
            Frame(R(new FakeInteraction(null, new SinkSlot(a, 0))));
            var c = Commands;
            Assert.That(c.Length, Is.EqualTo(4), string.Join(" | ", c));
            Assert.That(c[0], Is.EqualTo("DESTROY 2"));
            Assert.That(c[1], Does.StartWith("CREATE ").And.EndWith(" 99"));
            Assert.That(c[2], Does.EndWith(" 1 0").And.StartWith("REPARENT "));
            Assert.That(c[3], Does.EndWith(" 2 0").And.StartWith("BIND "));
        }

        // --- Interaction pin at every element node (pkg 03) ---

        static IEnumerable<Type> ElementNodeTypes() => typeof(RectangleNode).Assembly.GetTypes()
            .Where(t => t.IsPublic && t.Name.EndsWith("Node", StringComparison.Ordinal)
                && t.GetMethod("Update") is { } m && typeof(Element).IsAssignableFrom(m.ReturnType));

        [Test]
        public void ElementNodes_AllHaveInteractionPin_NoIsHoveredPins()
        {
            var types = ElementNodeTypes().ToList();
            Assert.That(types.Select(t => t.Name), Is.SupersetOf(new[]
            {
                "TextNode", "ImageNode", "GroupNode", "GroupSpectralNode", "RowNode", "RowSpectralNode", "ColumnNode",
                "ColumnSpectralNode", "VideoNode", "ButtonNode", "RectangleNode",
            }));
            foreach (var t in types)
            {
                var names = t.GetMethod("Update")!.GetParameters().Select(p => p.Name).ToList();
                Assert.That(names, Does.Contain("interaction"), t.Name);
                Assert.That(names, Does.Not.Contain("isHovered").And.Not.Contain("isPressed").And.Not.Contain("pointerPosition"), t.Name);
                Assert.That(names.Contains("isClicked"), Is.EqualTo(t == typeof(ButtonNode)), t.Name + ": IsClicked only at the Button");
            }
        }

        [Test]
        public void ElementNodes_ChainReachesTheDescription_AndCreatesHandler()
        {
            var chain = new HoverNode(_ => new Removal()).Update(null, null);
            foreach (var t in ElementNodeTypes())
            {
                using var node = (IDisposable)Activator.CreateInstance(t, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                    null, new object[] { (Func<string, IDisposable>)(_ => new Removal()) }, null)!;
                var method = t.GetMethod("Update")!;
                var ps = method.GetParameters();
                var args = new object?[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    var pt = ps[i].ParameterType;
                    if (ps[i].Name == "interaction") args[i] = chain;
                    else if (pt.IsByRef) args[i] = null;
                    else if (pt == typeof(string)) args[i] = "";
                    else if (pt.IsGenericType && pt.GetGenericTypeDefinition().Name.StartsWith("Spread", StringComparison.Ordinal))
                        args[i] = pt.GetProperty("Empty")?.GetValue(null) ?? pt.GetField("Empty")!.GetValue(null);
                    else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                }
                var element = (Element)method.Invoke(node, args)!;
                Assert.That(element.Interaction, Is.SameAs(chain), t.Name);

                var sink = new RecordingSink();
                var rec = new Reconciler(sink, new IdAllocator(), new SceneRegistry());
                rec.Update(element, Space.DIPTopLeft);
                Assert.That(sink.OfOp("CREATE").Count(c => c.EndsWith(" " + hover, StringComparison.Ordinal)), Is.EqualTo(1), t.Name + ": one HoverHandler");
                Assert.That(sink.OfOp("BIND").Count(), Is.GreaterThanOrEqualTo(1), t.Name);
            }
        }

        sealed class FakeInteraction(Interaction? previous, SinkSlot sink) : Interaction(99, previous, sink)
        {
            internal override void WriteProperties(ICommandSink sink, uint id, Interaction? previous) { }
        }

        [Test]
        public void SteadyState_NoAllocation()
        {
            var hoverNode = new HoverNode(_ => new Removal());
            var rect = new RectangleNode(_ => new Removal());
            var hovered = Channel.Create(false);
            var writer = new CommandWriter();
            var rec = new Reconciler(writer, new IdAllocator(), new SceneRegistry());

            void Run()
            {
                var chain = hoverNode.Update(null, hovered);
                var e = rect.Update(new Vector2(1, 1), Matrix.Identity, RectangleAnchor.Center, Color4.White, 0, Color4.Black, 0, 1, false, false, chain, null, out _, out _);
                writer.Reset();
                rec.Update(e, Space.DIPTopLeft);
                writer.EndFrame();
            }

            Run();
            Assert.That(rec.Sinks.Count, Is.EqualTo(2), "element and handler bound");
            for (int i = 0; i < 100; i++) Run();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) Run();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.EqualTo(0), "Hover + Rectangle + reconciler unchanged must not allocate");
            hoverNode.Dispose();
            rect.Dispose();
        }

        sealed class Removal : IDisposable { public void Dispose() { } }

        // --- Hover node ---

        [Test]
        public void HoverNode_ChangeDetection()
        {
            using var node = new HoverNode(_ => new Removal());
            var a = node.Update(null, null);
            Assert.That(node.Update(null, null), Is.SameAs(a), "unchanged -> same instance");
            Assert.That(a, Is.InstanceOf<HoverInteraction>());
            Assert.That(a.Sink, Is.EqualTo(node.Core.Slot));
            Assert.That(node.Update(null, Channel.Create(false)), Is.SameAs(a), "a channel does not change the handler");

            var previous = new HoverNode(_ => new Removal()).Update(null, null);
            var c = node.Update(previous, null);
            Assert.That(c, Is.Not.SameAs(a));
            Assert.That(c.Previous, Is.SameAs(previous), "chained");
            Assert.That(node.Core.Created, Is.EqualTo(2));
        }

        [Test]
        public void HoverNode_PullsIntoChannels_OnChange_AuthorQt()
        {
            using var node = new HoverNode(_ => new Removal());
            var hovered = Channel.Create(false);
            int writes = 0;
            using var sub = hovered.Subscribe(_ => writes++);
            writes = 0;

            node.Update(null, hovered);
            Assert.That(writes, Is.Zero, "unchanged -> no write");

            var s = node.Core.Sink[0];
            s.IsHovered = true;
            node.Update(null, hovered);
            Assert.That(hovered.Value, Is.True);
            Assert.That(hovered.LatestAuthor, Is.EqualTo("Qt"));

            node.Update(null, hovered);
            Assert.That(writes, Is.EqualTo(1), "no second write");
        }

        [Test]
        public void HoverNode_ShowsDuplicateWarning()
        {
            var warnings = new List<string>();
            using var node = new HoverNode(w => { warnings.Add(w); return new Removal(); });
            var chain = node.Update(null, null);
            Frame(G(R(chain), R(chain, radius: 0.1f)));
            node.Update(null, null);
            Assert.That(warnings, Is.EqualTo(new[] { SceneRegistry.InteractionDuplicateWarning }));
            Assert.That(node.Core.Warning, Is.Not.Null);
        }

        [Test]
        public void RectangleNode_InteractionPin_ChangeByReference()
        {
            using var rect = new RectangleNode(_ => new Removal());
            using var hoverNode = new HoverNode(_ => new Removal());
            Element Run(Interaction? i) => rect.Update(new Vector2(1, 1), Matrix.Identity, RectangleAnchor.Center, Color4.White, 0,
                Color4.Black, 0, 1, false, false, i, null, out _, out _);

            var chain = hoverNode.Update(null, null);
            var e1 = Run(chain);
            Assert.That(e1.Interaction, Is.SameAs(chain));
            Assert.That(Run(chain), Is.SameAs(e1));
            Assert.That(Run(null).Interaction, Is.Null);
        }

        // --- Click, Point, Wheel nodes (#320, pkg 05) ---

        const ushort tap = Protocol.Type.TapHandler;       // 33
        const ushort point = Protocol.Type.PointHandler;   // 34
        const ushort wheel = Protocol.Type.WheelHandler;   // 35
        const ushort acceptedButtons = Protocol.Prop.AcceptedButtons; // 769
        const ushort acceptedDevices = Protocol.Prop.AcceptedDevices; // 770

        static ClickInteraction C(ElementSink sink, Interaction? previous = null, PointerButtons buttons = PointerButtons.Left, PointerDevices devices = PointerDevices.All)
            => new(previous, new SinkSlot(sink, 0), buttons, devices);

        [Test]
        public void ClickPointWheel_DefaultProperties_NotSent()
        {
            var a = new ElementSink();
            Frame(R(new WheelInteraction(new PointInteraction(C(a), new SinkSlot(a, 0), PointerButtons.Left, PointerDevices.All),
                new SinkSlot(new ElementSink(), 0), PointerDevices.All)));
            Assert.That(_sink.OfOp("SET"), Is.Empty, "Left / All = QtNative defaults");
        }

        [Test]
        public void Chain_HoverClickPoint_ThreeHandlers()
        {
            var h = new ElementSink();
            var c = new ElementSink();
            var p = new ElementSink();
            var chain = new PointInteraction(C(c, H(h)), new SinkSlot(p, 0), PointerButtons.Left, PointerDevices.All);
            Frame(R(chain));
            Assert.That(_sink.OfOp("CREATE").ToArray(), Is.EqualTo(new[] { "CREATE 1 3", $"CREATE 2 {hover}", $"CREATE 3 {tap}", $"CREATE 4 {point}" }));
            Assert.That(_sink.OfOp("BIND").Count(), Is.EqualTo(3));
            Assert.That(_rec.Sinks.Count, Is.EqualTo(3));
        }

        [Test]
        public void ClickProperties_OnlySetWhenDifferent()
        {
            var a = new ElementSink();
            Frame(R(C(a, buttons: PointerButtons.Right | PointerButtons.Middle, devices: PointerDevices.TouchScreen)));
            Assert.That(_sink.OfOp("SET").ToArray(), Is.EqualTo(new[] { $"SET 2 {acceptedButtons} i32(6)", $"SET 2 {acceptedDevices} i32(2)" }));

            Frame(R(C(a, buttons: PointerButtons.Right | PointerButtons.Middle, devices: PointerDevices.All)));
            Assert.That(Commands, Is.EqualTo(new[] { $"SET 2 {acceptedDevices} i32(2147483647)" }), "All = Qt's AllDevices");

            Frame(R(C(a, buttons: PointerButtons.None, devices: PointerDevices.Mouse | PointerDevices.Pen)));
            Assert.That(Commands, Is.EqualTo(new[] { $"SET 2 {acceptedButtons} i32(0)", $"SET 2 {acceptedDevices} i32({0x1 | 0x8 | 0x10 | 0x20})" }));
        }

        [Test]
        public void WheelProperties_DevicesOnly()
        {
            var a = new ElementSink();
            Frame(R(new WheelInteraction(null, new SinkSlot(a, 0), PointerDevices.Mouse)));
            Assert.That(_sink.OfOp("SET").ToArray(), Is.EqualTo(new[] { $"SET 2 {acceptedDevices} i32(1)" }));
        }

        [Test]
        public void ClickNode_ChangeDetection()
        {
            using var node = new ClickNode(_ => new Removal());
            var a = node.Update(null, null, null, null, null, PointerButtons.Left, PointerDevices.All);
            Assert.That(node.Update(null, null, null, null, null, PointerButtons.Left, PointerDevices.All), Is.SameAs(a));
            Assert.That(a.HandlerType, Is.EqualTo(tap));
            var b = node.Update(null, null, null, null, null, PointerButtons.Right, PointerDevices.All);
            Assert.That(b, Is.Not.SameAs(a));
            Assert.That(((ClickInteraction)b).Buttons, Is.EqualTo(PointerButtons.Right));
            var c = node.Update(null, null, null, null, null, PointerButtons.Right, PointerDevices.Mouse);
            Assert.That(c, Is.Not.SameAs(b));
            Assert.That(node.Core.Created, Is.EqualTo(3));
        }

        [Test]
        public void ClickNode_PullsEventsOnceAndStatesOnChange()
        {
            using var node = new ClickNode(_ => new Removal());
            var pressed = Channel.Create(false);
            var clicked = Channel.Create(Unit.Default);
            var tapCount = Channel.Create(0);
            var longPressed = Channel.Create(Unit.Default);
            int clicks = 0, longs = 0, taps = 0, presses = 0;
            using var s1 = clicked.Subscribe(_ => clicks++);
            using var s2 = longPressed.Subscribe(_ => longs++);
            using var s3 = tapCount.Subscribe(_ => taps++);
            using var s4 = pressed.Subscribe(_ => presses++);
            clicks = longs = taps = presses = 0;

            void Update() => node.Update(null, pressed, clicked, tapCount, longPressed, PointerButtons.Left, PointerDevices.All);
            Update();
            Assert.That(clicks + longs + taps + presses, Is.Zero, "nothing happened -> nothing written");

            var s = node.Core.Sink[0];
            s.IsPressed = true;
            s.PendingClicks = 2;
            s.PendingLongPresses = 1;
            s.TapCount = 2;
            Update();
            Assert.That(pressed.Value, Is.True);
            Assert.That(pressed.LatestAuthor, Is.EqualTo("Qt"));
            Assert.That(tapCount.Value, Is.EqualTo(2));
            Assert.That((clicks, longs, taps, presses), Is.EqualTo((2, 1, 1, 1)), "one value per click / long press");
            Assert.That(clicked.LatestAuthor, Is.EqualTo("Qt"));

            Update();
            Assert.That((clicks, longs, taps, presses), Is.EqualTo((2, 1, 1, 1)), "no second write");
        }

        [Test]
        public void ClickNode_Unconnected_DropsEvents_NoBacklog()
        {
            using var node = new ClickNode(_ => new Removal());
            var s = node.Core.Sink[0];
            s.PendingClicks = 5;
            s.PendingLongPresses = 3;
            node.Update(null, null, null, null, null, PointerButtons.Left, PointerDevices.All);
            Assert.That(s.PendingClicks, Is.Zero);
            Assert.That(s.PendingLongPresses, Is.Zero);

            var clicked = Channel.Create(Unit.Default);
            int clicks = 0;
            using var sub = clicked.Subscribe(_ => clicks++);
            clicks = 0;
            node.Update(null, null, clicked, null, null, PointerButtons.Left, PointerDevices.All);
            Assert.That(clicks, Is.Zero, "no flood of old clicks when connected later");
        }

        [Test]
        public void PointNode_ChangeDetection_AndPull()
        {
            using var node = new PointNode(_ => new Removal());
            var a = node.Update(null, null, null, PointerButtons.Left, PointerDevices.All);
            Assert.That(node.Update(null, null, null, PointerButtons.Left, PointerDevices.All), Is.SameAs(a));
            Assert.That(node.Update(null, null, null, PointerButtons.Left, PointerDevices.TouchScreen), Is.Not.SameAs(a));

            var active = Channel.Create(false);
            var position = Channel.Create(Vector2.Zero);
            int writes = 0;
            using var s1 = active.Subscribe(_ => writes++);
            using var s2 = position.Subscribe(_ => writes++);
            writes = 0;
            node.Update(null, active, position, PointerButtons.Left, PointerDevices.All);
            Assert.That(writes, Is.Zero);

            var s = node.Core.Sink[0];
            s.IsActive = true;
            s.PointerPosition = new Vector2(0.25f, 0.5f);
            node.Update(null, active, position, PointerButtons.Left, PointerDevices.All);
            Assert.That(active.Value, Is.True);
            Assert.That(position.Value, Is.EqualTo(new Vector2(0.25f, 0.5f)));
            Assert.That(active.LatestAuthor, Is.EqualTo("Qt"));
            Assert.That(position.LatestAuthor, Is.EqualTo("Qt"));
            Assert.That(writes, Is.EqualTo(2));

            node.Update(null, active, position, PointerButtons.Left, PointerDevices.All);
            Assert.That(writes, Is.EqualTo(2));
            s.IsActive = false; // the position stays
            node.Update(null, active, position, PointerButtons.Left, PointerDevices.All);
            Assert.That(active.Value, Is.False);
            Assert.That(position.Value, Is.EqualTo(new Vector2(0.25f, 0.5f)));
            Assert.That(writes, Is.EqualTo(3));
        }

        [Test]
        public void WheelNode_OncePerFrame_ThenOnceZero()
        {
            using var node = new WheelNode(_ => new Removal());
            var a = node.Update(null, null, null, PointerDevices.All);
            Assert.That(node.Update(null, null, null, PointerDevices.All), Is.SameAs(a));
            Assert.That(node.Update(null, null, null, PointerDevices.Mouse), Is.Not.SameAs(a));

            var delta = Channel.Create(Vector2.Zero);
            var values = new List<Vector2>();
            using var sub = delta.Subscribe(values.Add);
            values.Clear();
            var s = node.Core.Sink[0];

            node.Update(null, delta, null, PointerDevices.All);
            Assert.That(values, Is.Empty, "no motion at rest: nothing written");

            s.PendingWheel = new Vector2(0, 1);
            node.Update(null, delta, null, PointerDevices.All);
            s.PendingWheel = new Vector2(0, 1);
            node.Update(null, delta, null, PointerDevices.All);
            Assert.That(values, Is.EqualTo(new[] { new Vector2(0, 1), new Vector2(0, 1) }), "equal values in a row are written every frame");
            Assert.That(delta.LatestAuthor, Is.EqualTo("Qt"));

            node.Update(null, delta, null, PointerDevices.All);
            node.Update(null, delta, null, PointerDevices.All);
            Assert.That(values.Skip(2).ToArray(), Is.EqualTo(new[] { Vector2.Zero }), "once (0, 0) after motion");
        }

        [Test]
        public void WheelNode_Unconnected_DropsMotion()
        {
            using var node = new WheelNode(_ => new Removal());
            node.Core.Sink[0].PendingWheel = new Vector2(1, 1);
            node.Update(null, null, null, PointerDevices.All);
            Assert.That(node.Core.Sink[0].PendingWheel, Is.EqualTo(Vector2.Zero));
        }

        [Test]
        public void WheelNode_Value_AddsMotion_CanBeReset()
        {
            using var node = new WheelNode(_ => new Removal());
            var value = Channel.Create(Vector2.Zero);
            int writes = 0;
            using var sub = value.Subscribe(_ => writes++);
            writes = 0;
            var s = node.Core.Sink[0];

            node.Update(null, null, value, PointerDevices.All);
            Assert.That(writes, Is.Zero, "no motion: nothing written");

            s.PendingWheel = new Vector2(0, 1);
            node.Update(null, null, value, PointerDevices.All);
            s.PendingWheel = new Vector2(0.5f, 2);
            node.Update(null, null, value, PointerDevices.All);
            Assert.That(value.Value, Is.EqualTo(new Vector2(0.5f, 3)), "sums up the deltas");
            Assert.That(value.LatestAuthor, Is.EqualTo("Qt"));
            Assert.That(writes, Is.EqualTo(2));

            node.Update(null, null, value, PointerDevices.All);
            Assert.That(writes, Is.EqualTo(2), "no write without motion (also not the (0, 0) of Delta)");

            value.Value = new Vector2(10, -1); // set / reset from outside
            s.PendingWheel = new Vector2(0, 1);
            node.Update(null, null, value, PointerDevices.All);
            Assert.That(value.Value, Is.EqualTo(new Vector2(10, 0)), "adds to the value set from outside");
        }

        [Test]
        public void ClickNode_TapCount_KeepsLastValue_NoResetToZero()
        {
            using var node = new ClickNode(_ => new Removal());
            var tapCount = Channel.Create(0);
            var values = new List<int>();
            using var sub = tapCount.Subscribe(values.Add);
            values.Clear();
            void Update() => node.Update(null, null, null, tapCount, null, PointerButtons.Left, PointerDevices.All);
            var s = node.Core.Sink[0];

            s.TapCount = 2;
            Update();
            s.TapCount = 0; // released handle / new handler (SinkRegistry resets the slot)
            Update();
            Update();
            Assert.That(tapCount.Value, Is.EqualTo(2), "0 is not written: the count of the last click stays");
            s.TapCount = 1;
            Update();
            Assert.That(values, Is.EqualTo(new[] { 2, 1 }));

            // A new node (e.g. re-created by vvvv) on the same channel starts at 0: writes nothing until its first tap.
            using var fresh = new ClickNode(_ => new Removal());
            fresh.Update(null, null, null, tapCount, null, PointerButtons.Left, PointerDevices.All);
            Assert.That(tapCount.Value, Is.EqualTo(1));
        }

        // --- Touch Points and Pinch nodes (#321, pkg 09) ---

        [Test]
        public void TouchPointsProperties_DefaultsNotSent_OnlyChangesSet()
        {
            var a = new ElementSink();
            Frame(R(new TouchPointsInteraction(null, new SinkSlot(a, 0), true, PointerMasks.DefaultDevices)));
            Assert.That(Commands.Where(c => c.StartsWith("CREATE")), Is.EqualTo(new[] { "CREATE 1 3", $"CREATE 2 {Protocol.Type.TouchPointsHandler}" }));
            Assert.That(_sink.OfOp("SET"), Is.Empty, "occlusion on / All = QtNative defaults");

            var chain = new TouchPointsInteraction(null, new SinkSlot(a, 0), false, TouchPointsNode_DefaultDevices);
            Frame(R(chain));
            Assert.That(Commands, Is.EqualTo(new[]
            {
                $"SET 2 {Protocol.Prop.Occlusion} bool(0)",
                $"SET 2 {acceptedDevices} i32({0x2 | 0x8 | 0x10 | 0x20})",
            }));
        }

        static readonly PointerDevices TouchPointsNode_DefaultDevices = PointerDevices.TouchScreen | PointerDevices.Pen;

        [Test]
        public void PinchProperties_DefaultsNotSent_OnlyChangesSet()
        {
            var a = new ElementSink();
            Frame(R(new PinchInteraction(null, new SinkSlot(a, 0), 2, 2, PointerDevices.All)));
            Assert.That(_sink.OfOp("CREATE").ToArray(), Is.EqualTo(new[] { "CREATE 1 3", $"CREATE 2 {Protocol.Type.PinchHandler}" }));
            Assert.That(_sink.OfOp("SET"), Is.Empty);

            Frame(R(new PinchInteraction(null, new SinkSlot(a, 0), 3, 4, PointerDevices.TouchScreen)));
            Assert.That(Commands, Is.EqualTo(new[]
            {
                $"SET 2 {Protocol.Prop.MinimumPointCount} i32(3)",
                $"SET 2 {Protocol.Prop.MaximumPointCount} i32(4)",
                $"SET 2 {acceptedDevices} i32(2)",
            }));
        }

        [Test]
        public void TouchPointsNode_DefaultsAndChangeDetection()
        {
            using var node = new TouchPointsNode(_ => new Removal());
            var a = (TouchPointsInteraction)node.Update(null, null, PointerMasks.DefaultDevices, true);
            Assert.That(node.Update(null, null, PointerMasks.DefaultDevices, true), Is.SameAs(a));
            Assert.That(node.Update(null, null, PointerMasks.DefaultDevices, false), Is.Not.SameAs(a));
        }

        [Test]
        public void TouchPointsNode_WritesSpreadOnlyOnChange()
        {
            using var node = new TouchPointsNode(_ => new Removal());
            var points = Channel.Create(Spread<Elements.TouchPoint>.Empty);
            var writes = new List<Spread<Elements.TouchPoint>?>();
            using var sub = points.Subscribe(writes.Add);
            writes.Clear();
            void Update() => node.Update(null, points, TouchPointsNode_DefaultDevices, true);

            Update();
            Assert.That(writes, Is.Empty, "no event yet: nothing written");

            var reg = new SinkRegistry();
            uint h = reg.Acquire(node.Core.Slot);
            reg.Dispatch(new SinkRegistryTests.Poll().TouchPoints(h, (1, 50, 0, 0.5f, 0, 0), (2, 100, 0, 0.5f, 0, 0)).Bytes);
            Update();
            Assert.That(writes, Has.Count.EqualTo(1));
            Assert.That(points.Value!.Select(p => p.Id), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(points.Value![1].Position, Is.EqualTo(new Vector2(1, 0)), "pixels -> units");
            Assert.That(points.LatestAuthor, Is.EqualTo("Qt"));

            for (int i = 0; i < 3; i++) Update();
            Assert.That(writes, Has.Count.EqualTo(1), "unchanged: no write");

            reg.Dispatch(new SinkRegistryTests.Poll().TouchPoints(h).Bytes);
            Update();
            Assert.That(writes, Has.Count.EqualTo(2));
            Assert.That(points.Value, Is.Empty);
        }

        [Test]
        public void TouchPointsNode_SteadyState_NoAllocation()
        {
            using var node = new TouchPointsNode(_ => new Removal());
            var points = Channel.Create(Spread<Elements.TouchPoint>.Empty);
            var reg = new SinkRegistry();
            uint h = reg.Acquire(node.Core.Slot);
            reg.Dispatch(new SinkRegistryTests.Poll().TouchPoints(h, (1, 50, 0, 0.5f, 0, 0)).Bytes);
            for (int i = 0; i < 100; i++) node.Update(null, points, TouchPointsNode_DefaultDevices, true);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) node.Update(null, points, TouchPointsNode_DefaultDevices, true);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0));
        }

        [Test]
        public void PinchNode_PullsValues_KeepsLastAfterTheEnd()
        {
            using var node = new PinchNode(_ => new Removal());
            var active = Channel.Create(false);
            var scale = Channel.Create(1f);
            var rotation = Channel.Create(0f);
            var translation = Channel.Create(Vector2.Zero);
            var scales = new List<float>();
            using var sub = scale.Subscribe(scales.Add);
            scales.Clear();
            void Update() => node.Update(null, active, scale, rotation, translation, 2, 2, PointerDevices.All);

            scale.Value = 3f; // set by the patch: not overwritten before a gesture
            Update();
            Assert.That(scale.Value, Is.EqualTo(3f));
            scales.Clear();

            var reg = new SinkRegistry();
            uint h = reg.Acquire(node.Core.Slot);
            reg.Dispatch(new SinkRegistryTests.Poll().Active(h, true).Pinch(h, 2f, 180, 10, 20).Bytes);
            Update();
            Assert.That(active.Value, Is.True);
            Assert.That(scale.Value, Is.EqualTo(2f));
            Assert.That(rotation.Value, Is.EqualTo(0.5f), "180 degrees = half a cycle");
            Assert.That(translation.Value, Is.EqualTo(new Vector2(0.1f, 0.2f)));
            Assert.That(scale.LatestAuthor, Is.EqualTo("Qt"));

            for (int i = 0; i < 3; i++) Update();
            Assert.That(scales, Has.Count.EqualTo(1), "only on change");

            reg.Dispatch(new SinkRegistryTests.Poll().Active(h, false).Bytes);
            Update();
            Assert.That(active.Value, Is.False);
            Assert.That(scale.Value, Is.EqualTo(2f), "keeps the last value");
            Assert.That(rotation.Value, Is.EqualTo(0.5f));
        }

        [Test]
        public void PinchNode_InvalidPointCounts_ErrorAtTheNode_DefaultsSent()
        {
            var shown = new List<string>();
            int removed = 0;
            using var node = new PinchNode(m => { shown.Add(m); return new Disposer(() => removed++); });
            var a = (PinchInteraction)node.Update(null, null, null, null, null, 1, 2, PointerDevices.All);
            Assert.That(node.InputError, Is.Not.Null);
            Assert.That((a.MinimumPointCount, a.MaximumPointCount), Is.EqualTo((2, 2)), "nothing invalid goes to QtNative");
            node.Update(null, null, null, null, null, 1, 2, PointerDevices.All);
            Assert.That(shown, Has.Count.EqualTo(1), "shown once");

            var b = (PinchInteraction)node.Update(null, null, null, null, null, 4, 3, PointerDevices.All);
            Assert.That(node.InputError, Is.Not.Null, "minimum above maximum");
            Assert.That(b.MinimumPointCount, Is.EqualTo(2));

            var c = (PinchInteraction)node.Update(null, null, null, null, null, 2, 3, PointerDevices.All);
            Assert.That(node.InputError, Is.Null);
            Assert.That(removed, Is.EqualTo(1));
            Assert.That((c.MinimumPointCount, c.MaximumPointCount), Is.EqualTo((2, 3)));
        }

        // --- Drag node (#209, pkg 12) ---

        const ushort drag = Protocol.Type.DragHandler; // 38

        static DragInteraction D(ElementSink sink, bool moves = true, DragAxis axis = DragAxis.Both, DragLimits? limits = null,
            PointerButtons buttons = PointerButtons.Left, PointerDevices devices = PointerDevices.All)
            => new(null, new SinkSlot(sink, 0), moves, axis, limits ?? DragLimits.None, buttons, devices);

        static Rectangle RB(Interaction? interaction, Matrix transform) => new() { Interaction = interaction, Transform = transform };

        [Test]
        public void DragProperties_DefaultsNotSent_OnlyChangesSet()
        {
            var a = new ElementSink();
            Frame(R(D(a)));
            Assert.That(_sink.OfOp("CREATE").ToArray(), Is.EqualTo(new[] { "CREATE 1 3", $"CREATE 2 {drag}" }));
            Assert.That(_sink.OfOp("SET"), Is.Empty, "moves on / both axes / no limits / Left / All = QtNative defaults");

            Frame(R(D(a, moves: false, axis: DragAxis.Vertical, limits: new DragLimits(-100, 0, 100, 0), buttons: PointerButtons.Right)));
            Assert.That(Commands, Is.EqualTo(new[]
            {
                $"SET 2 {Protocol.Prop.MovesElement} bool(0)",
                $"SET 2 {Protocol.Prop.DragAxis} i32(2)",
                $"SET 2 {Protocol.Prop.DragLimits} rect(-100,0,100,0)",
                $"SET 2 {acceptedButtons} i32(2)",
            }));

            Frame(R(D(a, moves: false, axis: DragAxis.Vertical, limits: new DragLimits(float.NegativeInfinity, 0, 100, float.PositiveInfinity), buttons: PointerButtons.Right)));
            Assert.That(Commands, Is.EqualTo(new[] { $"SET 2 {Protocol.Prop.DragLimits} rect(-Infinity,0,100,Infinity)" }));
        }

        [Test]
        public void DragNode_Pins_ToProperties_InPixels()
        {
            using var node = new DragNode(_ => new Removal());
            var a = (DragInteraction)node.Update(null, null, null, true, DragAxis.Both, default, default, PointerButtons.Left, PointerDevices.All);
            Assert.That(a.MovesElement, Is.True);
            Assert.That(a.Limits, Is.EqualTo(DragLimits.None));
            Assert.That(node.Update(null, null, null, true, DragAxis.Both, default, default, PointerButtons.Left, PointerDevices.All), Is.SameAs(a));

            var b = (DragInteraction)node.Update(null, null, null, true, DragAxis.Horizontal, new Vector2(-1, 0), default, PointerButtons.Left, PointerDevices.All);
            Assert.That(b, Is.Not.SameAs(a));
            Assert.That(b.Axis, Is.EqualTo(DragAxis.Horizontal));
            Assert.That(b.Limits, Is.EqualTo(new DragLimits(-100, 0, float.PositiveInfinity, float.PositiveInfinity)), "units -> pixels, missing max = +inf");
            Assert.That(node.InputError, Is.Null);
        }

        [Test]
        public void DragNode_MinGreaterMax_ErrorAtTheNode_LimitsIgnored()
        {
            var shown = new List<string>();
            int removed = 0;
            using var node = new DragNode(m => { shown.Add(m); return new Disposer(() => removed++); });
            var a = (DragInteraction)node.Update(null, null, null, true, DragAxis.Both, new Vector2(1, 0), new Vector2(0, 0), PointerButtons.Left, PointerDevices.All);
            Assert.That(node.InputError, Is.Not.Null);
            Assert.That(shown, Has.Count.EqualTo(1));
            Assert.That(a.Limits, Is.EqualTo(DragLimits.None), "nothing invalid goes to QtNative");

            node.Update(null, null, null, true, DragAxis.Both, new Vector2(1, 0), new Vector2(0, 0), PointerButtons.Left, PointerDevices.All);
            Assert.That(shown, Has.Count.EqualTo(1), "shown once");

            node.Update(null, null, null, true, DragAxis.Both, new Vector2(0, 0), new Vector2(1, 0), PointerButtons.Left, PointerDevices.All);
            Assert.That(node.InputError, Is.Null);
            Assert.That(removed, Is.EqualTo(1));
        }

        sealed class Disposer(Action dispose) : IDisposable { public void Dispose() => dispose(); }

        [Test]
        public void DragNode_PullsDraggingAndDelta_EveryDragUpdate()
        {
            using var node = new DragNode(_ => new Removal());
            var dragging = Channel.Create(false);
            var delta = Channel.Create(Vector2.Zero);
            var deltas = new List<Vector2>();
            using var sub = delta.Subscribe(deltas.Add);
            deltas.Clear();
            void Update() => node.Update(null, dragging, delta, true, DragAxis.Both, default, default, PointerButtons.Left, PointerDevices.All);

            Update();
            Assert.That(deltas, Is.Empty, "no drag yet: nothing written");

            var reg = new SinkRegistry();
            uint h = reg.Acquire(node.Core.Slot);
            reg.Dispatch(new SinkRegistryTests.Poll().Active(h, true).Drag(h, 50, 0).Bytes);
            Update();
            Assert.That(dragging.Value, Is.True);
            Assert.That(dragging.LatestAuthor, Is.EqualTo("Qt"));
            Assert.That(delta.Value, Is.EqualTo(new Vector2(0.5f, 0)), "pixels -> units");
            Assert.That(delta.LatestAuthor, Is.EqualTo("Qt"));

            reg.Dispatch(new SinkRegistryTests.Poll().Active(h, false).Bytes);
            Update();
            Update();
            Assert.That(dragging.Value, Is.False);
            Assert.That(delta.Value, Is.EqualTo(new Vector2(0.5f, 0)), "keeps the last drag's value");

            // A new drag with the same movement is still reported (QtNative sends it once per drag).
            reg.Dispatch(new SinkRegistryTests.Poll().Active(h, true).Drag(h, 50, 0).Bytes);
            Update();
            Assert.That(deltas, Is.EqualTo(new[] { new Vector2(0.5f, 0), new Vector2(0.5f, 0) }));
        }

        [Test]
        public void Drag_UnchangedTransformPin_SendsNoTransform_ChangedSends()
        {
            // QtNative resets the drag offset on every SET TRANSFORM: only a changed Transform pin may send it (#209, #469).
            var a = new ElementSink();
            var chain = D(a);
            var transform = Matrix.Translation(0, 0, 0) * Matrix.Scaling(2);
            Frame(RB(chain, transform));
            Assert.That(Commands.Count(c => c.StartsWith($"SET 1 {Protocol.Prop.Transform} ")), Is.EqualTo(1));

            for (int i = 0; i < 3; i++)
            {
                Frame(RB(chain, transform)); // new description, same value
                Assert.That(Commands, Is.Empty, "unchanged Transform: no TRANSFORM, no reset of the drag offset");
            }

            // Animated Transform pin: every changed value is sent, so it overrules the drag each frame.
            Frame(RB(chain, Matrix.Translation(0.1f, 0, 0)));
            Assert.That(Commands.Count(c => c.StartsWith($"SET 1 {Protocol.Prop.Transform} ")), Is.EqualTo(1));
            Frame(RB(chain, Matrix.Translation(0.2f, 0, 0)));
            Assert.That(Commands.Count(c => c.StartsWith($"SET 1 {Protocol.Prop.Transform} ")), Is.EqualTo(1));
        }

        [Test]
        public void SteadyState_ClickPointWheel_NoAllocation()
        {
            var click = new ClickNode(_ => new Removal());
            var pointNode = new PointNode(_ => new Removal());
            var wheelNode = new WheelNode(_ => new Removal());
            var dragNode = new DragNode(_ => new Removal());
            var dragging = Channel.Create(false);
            var dragDelta = Channel.Create(Vector2.Zero);
            var rect = new RectangleNode(_ => new Removal());
            var pressed = Channel.Create(false);
            var clicked = Channel.Create(Unit.Default);
            var tapCount = Channel.Create(0);
            var position = Channel.Create(Vector2.Zero);
            var delta = Channel.Create(Vector2.Zero);
            var wheelValue = Channel.Create(Vector2.Zero);
            var writer = new CommandWriter();
            var rec = new Reconciler(writer, new IdAllocator(), new SceneRegistry());

            void Run()
            {
                var chain = click.Update(null, pressed, clicked, tapCount, null, PointerButtons.Left, PointerDevices.All);
                chain = pointNode.Update(chain, null, position, PointerButtons.Left, PointerDevices.All);
                chain = wheelNode.Update(chain, delta, wheelValue, PointerDevices.All);
                chain = dragNode.Update(chain, dragging, dragDelta, true, DragAxis.Horizontal, new Vector2(-1, 0), new Vector2(1, 0),
                    PointerButtons.Left, PointerDevices.All);
                var e = rect.Update(new Vector2(1, 1), Matrix.Identity, RectangleAnchor.Center, Color4.White, 0, Color4.Black, 0, 1, false, false, chain, null, out _, out _);
                writer.Reset();
                rec.Update(e, Space.DIPTopLeft);
                writer.EndFrame();
            }

            Run();
            Assert.That(rec.Sinks.Count, Is.EqualTo(5), "element + four handlers bound");
            for (int i = 0; i < 100; i++) Run();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) Run();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.EqualTo(0), "Click + Point + Wheel + Drag + Rectangle + reconciler unchanged must not allocate");
            click.Dispose();
            pointNode.Dispose();
            wheelNode.Dispose();
            dragNode.Dispose();
            rect.Dispose();
        }
    }
}
