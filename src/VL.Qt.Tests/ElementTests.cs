using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    public class ElementTests
    {
        private sealed class TestElement : Element
        {
            public TestElement() : base(ElementType.Item) { }
        }

        [Test]
        public void Defaults()
        {
            var e = new TestElement();
            Assert.That(e.Type, Is.EqualTo(ElementType.Item));
            Assert.That(e.Anchor, Is.EqualTo(RectangleAnchor.TopLeft));
            Assert.That(e.Opacity, Is.EqualTo(1f));
            Assert.That(e.Clip, Is.False);
            Assert.That(e.Interaction, Is.Null);
            Assert.That(e.Transform, Is.EqualTo(Matrix.Identity));
            Assert.That(e.Feedback.IsNone, Is.True);
            Assert.That(e.Children, Is.Empty);
        }

        [Test]
        public void Immutable_OnlyInitSetters()
        {
            foreach (var p in typeof(Element).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var set = p.SetMethod;
                if (set is null) continue;
                var isInit = set.ReturnParameter.GetRequiredCustomModifiers()
                    .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
                Assert.That(isInit, Is.True, $"{p.Name} must be init-only");
            }
        }

        [Test]
        public void Init_SetsValues()
        {
            var feedback = new ElementFeedback();
            var e = new TestElement { Opacity = 0.5f, Feedback = new FeedbackSlot(feedback, 2) };
            Assert.That(e.Opacity, Is.EqualTo(0.5f));
            Assert.That(e.Feedback.Index, Is.EqualTo(2));
        }

        [Test]
        public void ElementType_MatchesProtocolIds()
        {
            Assert.That((ushort)ElementType.Item, Is.EqualTo(Protocol.ElementType.Group));
            Assert.That((ushort)ElementType.Rectangle, Is.EqualTo(Protocol.ElementType.Rectangle));
        }

        [Test]
        public void Feedback_SlotsGrowAndShrink()
        {
            var feedback = new ElementFeedback();
            Assert.That(feedback.Count, Is.EqualTo(0));
            feedback[2].PendingClicks = 3;
            Assert.That(feedback.Count, Is.EqualTo(3));
            Assert.That(feedback[2].PendingClicks, Is.EqualTo(3));
            Assert.That(feedback[0].IsHovered, Is.False);
            Assert.That(feedback.TryGet(5, out _), Is.False);
            Assert.That(feedback.Count, Is.EqualTo(3));
            feedback.Resize(1);
            Assert.That(feedback.Count, Is.EqualTo(1));
            Assert.That(new FeedbackSlot(feedback, 0).State, Is.SameAs(feedback[0]));
            Assert.That(default(FeedbackSlot).State, Is.Null);
        }

        [Test]
        public void Elements_HaveNoNativeReferences()
        {
            var types = typeof(Element).Assembly.GetTypes().Where(t => t.Namespace == "VL.Qt.Elements");
            foreach (var t in types)
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    Assert.That(f.FieldType, Is.Not.EqualTo(typeof(System.IntPtr)), $"{t.Name}.{f.Name}");
        }
    }
}
