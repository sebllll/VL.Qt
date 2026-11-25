using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using VL.Qt.Styles;
using VL.Qt.Wire;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>IStylable in the reconciler and in the children input.</summary>
    public class ReconcilerStylableTests
    {
        /// <summary>Behaves like a patched widget: the same instance, a cached element, a new one only when a field or the style changes.</summary>
        private sealed class TestStylable : IStylable
        {
            private Element? _cached;
            private Style? _cachedStyle;
            private Color4 _cachedFill;
            private bool _cachedButton;

            public int Builds { get; private set; }
            public Style? LastStyle { get; private set; }
            public Color4 Fill { get; set; } = Color4.White;
            public bool AsButton { get; set; }
            public bool Throw { get; set; }
            public bool ReturnNull { get; set; }

            public IElement? Build(Style style)
            {
                Builds++;
                LastStyle = style;
                if (Throw)
                    throw new InvalidOperationException("boom");
                if (ReturnNull)
                    return null;
                if (_cached is null || !ReferenceEquals(style, _cachedStyle) && !style.Equals(_cachedStyle)
                    || Fill != _cachedFill || AsButton != _cachedButton)
                {
                    _cached = AsButton ? new Button { Text = "b" } : new Rectangle { Fill = Fill };
                    _cachedStyle = style;
                    _cachedFill = Fill;
                    _cachedButton = AsButton;
                }
                return _cached;
            }
        }

        private sealed class UnknownElement : IElement
        {
        }

        private RecordingSink _sink = null!;
        private Reconciler _rec = null!;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _rec = new Reconciler(_sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
        }

        private static Style Radius(float r) => new() { Frame = new FrameStyle { Radius = default(StateValues<float>).With(StyleStates.Normal, r) } };

        private static ImmutableArray<Element> Kids(params Element[] e) => ImmutableArray.Create(e);

        private static StylableElement Wrap(IStylable s) => new(s);

        private void Update(Element root) => _rec.Update(root, Space.DIPTopLeft);

        // 1 ---------------------------------------------------------------

        [Test]
        public void Build_WithoutGroupStyle_GetsBaseStyle()
        {
            var s = new TestStylable();
            Update(new Group { Children = Kids(Wrap(s)) });

            Assert.That(s.LastStyle, Is.EqualTo(BaseStyle.Default));
        }

        [Test]
        public void Build_GetsGroupStyleMergedOverBase()
        {
            var s = new TestStylable();
            var g = Radius(13);
            Update(new Group { Style = g, Children = Kids(Wrap(s)) });

            Assert.That(s.LastStyle, Is.EqualTo(StyleMerge.Merge(BaseStyle.Default, g)));
        }

        [Test]
        public void Build_NestedGroups_InnerOverridesOuter()
        {
            var s = new TestStylable();
            var outer = Radius(13);
            var inner = Radius(21);
            Update(new Group { Style = outer, Children = Kids(new Group { Style = inner, Children = Kids(Wrap(s)) }) });

            var expected = StyleMerge.Merge(StyleMerge.Merge(BaseStyle.Default, outer), inner);
            Assert.That(s.LastStyle, Is.EqualTo(expected));
            Assert.That(s.LastStyle, Is.Not.EqualTo(StyleMerge.Merge(BaseStyle.Default, outer)));
        }

        [Test]
        public void GroupStyleChange_BuildsAgainWithNewStyle()
        {
            var s = new TestStylable();
            var kids = Kids(Wrap(s));
            Update(new Group { Style = Radius(13), Children = kids });
            Update(new Group { Style = Radius(14), Children = kids });

            Assert.That(s.LastStyle, Is.EqualTo(StyleMerge.Merge(BaseStyle.Default, Radius(14))));
        }

        // 2 ---------------------------------------------------------------

        [Test]
        public void Build_RunsEveryUpdate_SameTree_NoCommands()
        {
            var s = new TestStylable();
            var root = new Group { Children = Kids(Wrap(s)) };
            Update(root);
            _sink.Clear();

            for (int i = 0; i < 5; i++)
                Update(root);

            Assert.That(s.Builds, Is.EqualTo(6));
            Assert.That(_sink.Commands, Is.Empty);
        }

        [Test]
        public void Build_RunsEveryUpdate_DeepInside_AncestorsNotSkipped()
        {
            var s = new TestStylable();
            var root = new Group { Children = Kids(new Row { Children = Kids(new Column { Children = Kids(Wrap(s)) }) }) };
            Update(root);
            Update(root);
            Update(root);

            Assert.That(s.Builds, Is.EqualTo(3));
        }

        // 3 ---------------------------------------------------------------

        [Test]
        public void ChangedElement_SameStylable_SendsSets()
        {
            var s = new TestStylable { Fill = Color4.White };
            var root = new Group { Children = Kids(Wrap(s)) };
            Update(root);
            _sink.Clear();

            s.Fill = new Color4(1, 0, 0, 1);
            Update(root);

            Assert.That(_sink.OfOp("SET").Count(), Is.EqualTo(1));
            Assert.That(_sink.OfOp("CREATE"), Is.Empty);
            Assert.That(_sink.OfOp("DESTROY"), Is.Empty);

            _sink.Clear();
            Update(root);
            Assert.That(_sink.Commands, Is.Empty);
        }

        // 4 ---------------------------------------------------------------

        [Test]
        public void TypeChange_DestroysAndCreatesAtPosition()
        {
            var s = new TestStylable();
            var before = new Rectangle();
            var after = new Rectangle();
            var root = new Group { Children = Kids(before, Wrap(s), after) };
            Update(root);
            var oldId = _rec.Root!.Children[1].Id;
            _sink.Clear();

            s.AsButton = true;
            Update(root);

            Assert.That(_sink.OfOp("DESTROY").Single(), Is.EqualTo($"DESTROY {oldId}"));
            Assert.That(_sink.OfOp("CREATE").Single(), Does.EndWith(" Button"));
            Assert.That(_sink.OfOp("REPARENT").Single(), Does.EndWith(" 1"));
            Assert.That(_rec.Root.Children[1].Type, Is.EqualTo(ElementType.Button));
        }

        // 5 ---------------------------------------------------------------

        [Test]
        public void Exception_SiblingsStillReconciled_ErrorAtParentFeedback_LastElementStays()
        {
            var feedback = new ElementFeedback();
            _ = feedback[0]; // the container node allocates its slots before the reconciler runs
            var s = new TestStylable();
            var sibling1 = new Rectangle { Fill = Color4.White };
            Update(new Group { Feedback = new FeedbackSlot(feedback, 0), Children = Kids(Wrap(s), sibling1) });
            var stylableNode = _rec.Root!.Children[0];
            var shownId = stylableNode.Id;
            var shown = stylableNode.Element;
            Assert.That(feedback.Error, Is.Null);
            _sink.Clear();

            s.Throw = true;
            var sibling2 = new Rectangle { Fill = new Color4(0, 1, 0, 1) };
            Update(new Group { Feedback = new FeedbackSlot(feedback, 0), Children = Kids(Wrap(s), sibling2) });

            Assert.That(feedback[0].BuildError, Is.Not.Null.And.Contains("boom"));
            Assert.That(feedback.Error, Is.EqualTo(feedback[0].BuildError));
            Assert.That(_sink.OfOp("DESTROY"), Is.Empty);
            Assert.That(_rec.Root.Children[0].Id, Is.EqualTo(shownId));
            Assert.That(_rec.Root.Children[0].Element, Is.SameAs(shown));
            Assert.That(_rec.Root.Children[1].Element, Is.SameAs(sibling2));
            Assert.That(_sink.OfOp("SET").Count(), Is.GreaterThan(0), "sibling update was sent");

            // keeps failing: nothing more is sent, error stays
            _sink.Clear();
            Update(new Group { Feedback = new FeedbackSlot(feedback, 0), Children = Kids(Wrap(s), sibling2) });
            Assert.That(_sink.Commands, Is.Empty);
            Assert.That(feedback.Error, Is.Not.Null);
        }

        [Test]
        public void SuccessAfterException_ClearsError()
        {
            var feedback = new ElementFeedback();
            _ = feedback[0]; // the container node allocates its slots before the reconciler runs
            var s = new TestStylable();
            var kids = Kids(Wrap(s));
            s.Throw = true;
            Update(new Group { Feedback = new FeedbackSlot(feedback, 0), Children = kids });
            Assert.That(feedback.Error, Is.Not.Null);

            s.Throw = false;
            Update(new Group { Feedback = new FeedbackSlot(feedback, 0), Children = kids });

            Assert.That(feedback.Error, Is.Null);
            Assert.That(feedback[0].BuildError, Is.Null);
        }

        [Test]
        public void RemovingFailedChild_ClearsError()
        {
            var feedback = new ElementFeedback();
            _ = feedback[0]; // the container node allocates its slots before the reconciler runs
            var s = new TestStylable { Throw = true };
            Update(new Group { Feedback = new FeedbackSlot(feedback, 0), Children = Kids(Wrap(s), new Rectangle()) });
            Assert.That(feedback.Error, Is.Not.Null);

            Update(new Group { Feedback = new FeedbackSlot(feedback, 0), Children = Kids(new Rectangle()) });

            Assert.That(feedback.Error, Is.Null);
        }

        [Test]
        public void TwoFailedSiblings_BothErrorsKept_ClearedOneByOne()
        {
            var feedback = new ElementFeedback();
            _ = feedback[0]; // the container node allocates its slots before the reconciler runs
            var s1 = new TestStylable { Throw = true };
            var s2 = new TestStylable { Throw = true };
            var w1 = Wrap(s1);
            var w2 = Wrap(s2);
            Update(new Group { Feedback = new FeedbackSlot(feedback, 0), Children = Kids(w1, w2) });
            Assert.That(feedback[0].BuildError!.Split("failed").Length - 1, Is.EqualTo(2), feedback[0].BuildError);

            Update(new Group { Feedback = new FeedbackSlot(feedback, 0), Children = Kids(w2) });
            Assert.That(feedback[0].BuildError!.Split("failed").Length - 1, Is.EqualTo(1), "the second sibling's error stays after the first one went");

            s2.Throw = false;
            Update(new Group { Feedback = new FeedbackSlot(feedback, 0), Children = Kids(w2) });
            Assert.That(feedback[0].BuildError, Is.Null);
        }

        [Test]
        public void RemovingParentOfFailedChild_ClearsErrorOfItsFeedback()
        {
            var feedback = new ElementFeedback();
            _ = feedback[0]; // the container node allocates its slots before the reconciler runs
            var s = new TestStylable { Throw = true };
            Update(new Group { Children = Kids(new Group { Feedback = new FeedbackSlot(feedback, 0), Children = Kids(Wrap(s)) }) });
            Assert.That(feedback.Error, Is.Not.Null);

            Update(new Group());

            Assert.That(feedback.Error, Is.Null);
        }

        [Test]
        public void ExceptionOnFirstBuild_ShowsEmptyGroup()
        {
            var feedback = new ElementFeedback();
            _ = feedback[0]; // the container node allocates its slots before the reconciler runs
            var s = new TestStylable { Throw = true };
            Update(new Group { Feedback = new FeedbackSlot(feedback, 0), Children = Kids(Wrap(s)) });

            Assert.That(_rec.Root!.Children, Has.Count.EqualTo(1));
            Assert.That(_rec.Root.Children[0].Type, Is.EqualTo(ElementType.Item));
            Assert.That(_rec.Root.Children[0].Element, Is.TypeOf<Group>());
            Assert.That(feedback.Error, Does.Contain("boom"));
        }

        [Test]
        public void Exception_WithoutParentFeedback_DoesNotThrow()
        {
            var s = new TestStylable { Throw = true };
            Assert.DoesNotThrow(() => Update(new Group { Children = Kids(Wrap(s)) }));
        }

        // 6 ---------------------------------------------------------------

        [Test]
        public void Null_ShowsEmptyGroup_NoError()
        {
            var feedback = new ElementFeedback();
            _ = feedback[0]; // the container node allocates its slots before the reconciler runs
            var s = new TestStylable { ReturnNull = true };
            Update(new Group { Feedback = new FeedbackSlot(feedback, 0), Children = Kids(Wrap(s)) });

            Assert.That(_rec.Root!.Children[0].Type, Is.EqualTo(ElementType.Item));
            Assert.That(feedback.Error, Is.Null);
        }

        [Test]
        public void ElementThenNull_ReplacesWithEmptyGroup()
        {
            var s = new TestStylable();
            var root = new Group { Children = Kids(Wrap(s)) };
            Update(root);
            _sink.Clear();

            s.ReturnNull = true;
            Update(root);

            Assert.That(_sink.OfOp("DESTROY").Count(), Is.EqualTo(1));
            Assert.That(_sink.OfOp("CREATE").Count(), Is.EqualTo(1));
            Assert.That(_rec.Root!.Children[0].Type, Is.EqualTo(ElementType.Item));
        }

        // 7 ---------------------------------------------------------------

        [Test]
        public void SiblingSubtreeWithoutStylable_IsSkipped_PathToStylableVisited()
        {
            var s = new TestStylable();
            var plain = new Column { Children = Kids(new Button(), new Rectangle()) };
            var withStylable = new Column { Children = Kids(Wrap(s)) };
            var root = new Group { Children = Kids(plain, withStylable) };
            Update(root);
            var plainNode = _rec.Root!.Children[0];
            var stylableColumn = _rec.Root.Children[1];
            var plainSkipped = plainNode.SkippedPass;
            var stylableVisited = stylableColumn.VisitedPass;

            Update(root);

            Assert.That(plainNode.SkippedPass, Is.GreaterThan(plainSkipped));
            Assert.That(stylableColumn.VisitedPass, Is.GreaterThan(stylableVisited));
            Assert.That(s.Builds, Is.EqualTo(2));
        }

        // 8 ---------------------------------------------------------------

        private static Spread<IElement?> Input(params IElement?[] items) => items.ToSpread();

        [Test]
        public void ChildrenInput_StylableBecomesStylableElement_SameInstanceWhenUnchanged()
        {
            var input = new ChildrenInput();
            var s = new TestStylable();
            var rect = new Rectangle();

            var first = input.Update(Input(rect, s));
            Assert.That(first, Has.Length.EqualTo(2));
            Assert.That(first[0], Is.SameAs(rect));
            Assert.That(first[1], Is.TypeOf<StylableElement>());
            Assert.That(((StylableElement)first[1]).Stylable, Is.SameAs(s));

            var second = input.Update(Input(rect, s)); // new spread, same content
            Assert.That(second, Is.EqualTo(first));
            Assert.That(second == first, Is.True, "same backing array");
            Assert.That(input.Built, Is.EqualTo(1));
        }

        [Test]
        public void ChildrenInput_KeepsPlaceholderInstance_WhenOtherChildChanges()
        {
            var input = new ChildrenInput();
            var s = new TestStylable();
            var first = input.Update(Input(s));
            var placeholder = first[0];

            var second = input.Update(Input(new Rectangle(), s));

            Assert.That(second[1], Is.SameAs(placeholder));
        }

        [Test]
        public void ChildrenInput_NewStylable_NewPlaceholder()
        {
            var input = new ChildrenInput();
            var first = input.Update(Input(new TestStylable()));
            var second = input.Update(Input(new TestStylable()));

            Assert.That(second[0], Is.Not.SameAs(first[0]));
        }

        [Test]
        public void ChildrenInput_UnknownElementAndNull_AreDropped()
        {
            var input = new ChildrenInput();
            var s = new TestStylable();
            var result = input.Update(Input(new UnknownElement(), null, s, new UnknownElement()));

            Assert.That(result, Has.Length.EqualTo(1));
            Assert.That(result[0], Is.TypeOf<StylableElement>());

            var again = input.Update(Input(null, s, new UnknownElement()));
            Assert.That(again == result, Is.True);
        }

        [Test]
        public void ChildrenInput_SpreadOfElement_ReadWithoutRebuild()
        {
            // What a Spread<Element> (e.g. a ForEach of Rectangles) gives the Sequence pin of the Spectral nodes.
            var input = new ChildrenInput();
            var a = new Rectangle();
            var b = new Rectangle();
            var spread = Spread.Create<Element>(a, b);

            var first = input.Update(spread);
            Assert.That(first, Is.EqualTo(new Element[] { a, b }));
            Assert.That(input.Update(spread) == first, Is.True, "same spread");
            Assert.That(input.Update(Spread.Create<Element>(a, b)) == first, Is.True, "new spread, same children");
            Assert.That(input.Built, Is.EqualTo(1));
        }

        [Test]
        public void ChildrenInput_SpreadOfStylableType_BecomesStylableElements()
        {
            var input = new ChildrenInput();
            var s = new TestStylable();
            var result = input.Update(Spread.Create(s));

            Assert.That(result, Has.Length.EqualTo(1));
            Assert.That(((StylableElement)result[0]).Stylable, Is.SameAs(s));
            Assert.That(input.Update(Spread.Create(s)) == result, Is.True);
        }

        [Test]
        public void ChildrenInput_SpreadOfElementOrStylableType_SteadyStateDoesNotAllocate()
        {
            var elements = new ChildrenInput();
            var stylables = new ChildrenInput();
            var e = Spread.Create<Element>(new Rectangle(), new Rectangle());
            var s = Spread.Create(new TestStylable(), new TestStylable());
            elements.Update(e);
            stylables.Update(s);

            Assert.That(Allocations.Fewest(() =>
            {
                elements.Update(e);
                stylables.Update(s);
            }, frames: 100), Is.Zero);
        }

        [Test]
        public void ChildrenInput_MutableSequence_ChangeBehindSameInstanceIsSeen()
        {
            var input = new ChildrenInput();
            var a = new Rectangle();
            var list = new List<IElement?> { a };
            Assert.That(input.Update(list), Is.EqualTo(new Element[] { a }));

            var b = new Rectangle();
            list.Add(b);
            Assert.That(input.Update(list), Is.EqualTo(new Element[] { a, b }));
        }

        // 9 ---------------------------------------------------------------

        [Test]
        public void SteadyState_DoesNotAllocate()
        {
            var s = new TestStylable();
            var root = new Group { Style = Radius(13), Children = Kids(new Row { Children = Kids(Wrap(s), new Button()) }, new Rectangle()) };
            var writer = new CommandWriter();
            var rec = new Reconciler(writer, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            for (int i = 0; i < 50; i++)
            {
                writer.Clear();
                rec.Update(root, Space.DIPTopLeft);
                writer.EndFrame();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                writer.Clear();
                rec.Update(root, Space.DIPTopLeft);
                writer.EndFrame();
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(writer.Count, Is.EqualTo(0));
            Assert.That(allocated, Is.LessThan(1024), $"{allocated} bytes allocated in 1000 frames");
        }
    }
}
