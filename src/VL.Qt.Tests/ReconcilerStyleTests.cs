using System;
using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using VL.Qt.Elements;
using VL.Qt.Styles;

namespace VL.Qt.Tests
{
    /// <summary>Style resolution in the reconciler: base style → Group → … → widget, SET_STYLE diffs only for widgets.</summary>
    public class ReconcilerStyleTests
    {
        private RecordingSink _sink = null!;
        private Reconciler _rec = null!;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _rec = new Reconciler(_sink, new IdAllocator(), new SceneRegistry());
        }

        private static Style Radius(float r) => new() { Frame = new FrameStyle { Radius = default(StateValues<float>).With(StyleStates.Normal, r) } };

        private static ImmutableArray<Element> Kids(params Element[] e) => ImmutableArray.Create(e);

        private static string Full(uint id, Style resolved)
        {
            var s = new RecordingSink();
            StyleWriter.Write(s, id, resolved, null);
            return s.Commands.Single();
        }

        private static string RadiusEntry(float r) => $" {Protocol.StyleField.FrameRadius}/0=f32({r})";

        private void Update(Element root) => _rec.Update(root, Space.DIPTopLeft);

        [Test]
        public void WidgetWithoutStyle_GetsBaseStyle_FullWithClear_RightAfterCreate()
        {
            Update(new Button());

            Assert.That(_sink.Commands[0], Is.EqualTo("CREATE 1 Button"));
            Assert.That(_sink.Commands[1], Is.EqualTo(Full(1, BaseStyle.Default)));
            Assert.That(_sink.Commands[1], Does.StartWith($"STYLE 1 {Protocol.StyleSet.Clear} "));
            Assert.That(_sink.OfOp("STYLE").Count(), Is.EqualTo(1));
        }

        [Test]
        public void GroupStyle_IsInherited_ThroughRowAndColumn()
        {
            var g = Radius(13);
            Update(new Group { Style = g, Children = Kids(new Row { Children = Kids(new Column { Children = Kids(new Button()) }) }) });

            var styles = _sink.OfOp("STYLE").ToArray();
            Assert.That(styles, Has.Length.EqualTo(1));
            Assert.That(styles[0], Is.EqualTo(Full(4, StyleMerge.Merge(BaseStyle.Default, g))));
            Assert.That(styles[0], Does.Contain(RadiusEntry(13)));
        }

        [Test]
        public void WidgetStyle_OverridesGroupField()
        {
            Update(new Group { Style = Radius(13), Children = Kids(new Button { Style = Radius(21) }) });

            var line = _sink.OfOp("STYLE").Single();
            Assert.That(line, Does.Contain(RadiusEntry(21)));
            Assert.That(line, Does.Not.Contain(RadiusEntry(13)));
        }

        [Test]
        public void GroupStyleChange_WithSameChildren_SendsOnlyStyleDiffs()
        {
            var children = Kids(new Column { Children = Kids(new Button(), new Slider(), new Rectangle()) },
                new Button { Interaction = new HoverInteraction(null, new FeedbackSlot(new ElementFeedback(), 0)) });
            Update(new Group { Style = Radius(13), Children = children });
            _sink.Clear();

            Update(new Group { Style = Radius(14), Children = children });

            Assert.That(_sink.Commands, Has.Count.EqualTo(3));
            Assert.That(_sink.Commands, Has.All.StartWith("STYLE ").And.All.Contains(" 0" + RadiusEntry(14)));
        }

        [Test]
        public void NewButEqualStyle_SendsNothing()
        {
            var children = Kids(new Button { Style = Radius(21) }, new Slider());
            Update(new Group { Style = Radius(13), Children = children });
            _sink.Clear();

            Update(new Group { Style = Radius(13), Children = children });
            Update(new Group { Style = Radius(13), Children = Kids(new Button { Style = Radius(21) }, new Slider()) });

            Assert.That(_sink.Commands, Is.Empty);
        }

        [Test]
        public void EqualResolvedStyle_KeepsReference_ChildrenAreSkipped()
        {
            var button = new Button();
            Update(new Group { Style = Radius(13), Children = Kids(button) });
            var resolved = _rec.Root!.ResolvedStyle;
            var buttonNode = _rec.Root.Children[0];
            var skipped = buttonNode.SkippedPass;

            Update(new Group { Style = Radius(13), Children = Kids(button) });

            Assert.That(_rec.Root.ResolvedStyle, Is.SameAs(resolved));
            Assert.That(buttonNode.SkippedPass, Is.GreaterThan(skipped));
        }

        [Test]
        public void StyleBackToNull_ChangedField_SendsDiff()
        {
            Update(new Button { Style = Radius(21) });
            _sink.Clear();

            Update(new Button());

            Assert.That(_sink.Commands.Single(), Does.StartWith("STYLE 1 0 ").And.Not.Contains(RadiusEntry(21)));
        }

        [Test]
        public void StyleBackToNull_RemovedEntry_SendsFullWithClear()
        {
            var hovered = new Style
            {
                Frame = new FrameStyle { Radius = default(StateValues<float>).With(StyleStates.Normal, 21).With(StyleStates.Hovered, 30) },
            };
            Update(new Button { Style = hovered });
            _sink.Clear();

            Update(new Button());

            Assert.That(_sink.Commands.Single(), Is.EqualTo(Full(1, BaseStyle.Default)));
        }

        [Test]
        public void Primitives_NeverGetStyle()
        {
            var children = Kids(new Rectangle(), new Text(), new Column { Children = Kids(new Rectangle()) });
            Update(new Group { Style = Radius(13), Children = children });
            Update(new Group { Style = Radius(14), Children = children });
            Update(new Group { Children = children });

            Assert.That(_sink.OfOp("STYLE"), Is.Empty);
        }

        [Test]
        public void AfterDiscard_FullSendAgain()
        {
            var root = new Group { Style = Radius(13), Children = Kids(new Button()) };
            Update(root);
            _rec.Discard();
            _sink.Clear();

            Update(root);

            Assert.That(_sink.OfOp("STYLE").Single(), Does.StartWith($"STYLE 2 {Protocol.StyleSet.Clear} "));
        }

        [Test]
        public void SteadyState_NoCommands_NoAllocations()
        {
            var style = Radius(13);
            var children = Kids(new Column { Children = Kids(new Button { Style = Radius(21) }, new Slider(), new Text()) }, new Button());
            var a = new Group { Style = style, Children = children };
            var b = new Group { Style = style, Children = children }; // other instance, same style reference
            Update(a);
            for (int i = 0; i < 100; i++) // warm-up
                Update((i & 1) == 0 ? b : a);
            _sink.Clear();

            int frame = 0;
            long allocated = Allocations.Fewest(() => Update((frame++ & 1) == 0 ? b : a));

            Assert.That(_sink.Commands, Is.Empty);
            Assert.That(allocated, Is.EqualTo(0));
        }
    }
}
