using System;
using System.Linq;
using NUnit.Framework;
using VL.Qt.Elements;
using VL.Qt.Reconciliation;

namespace VL.Qt.Tests
{
    /// <summary>The list of elements that take part in the style (<see cref="StylableElements"/>).</summary>
    public class StylableElementsTests
    {
        private static readonly Type[] s_widgets = { typeof(Button), typeof(SpinBox), typeof(CheckBox), typeof(Switch), typeof(Slider), typeof(TextField) };
        private static readonly Type[] s_resolveOnly = { typeof(Group), typeof(Row), typeof(Column) };

        // Every other concrete element class passes the inherited style on. A new element class goes into one of the three lists.
        private static readonly Type[] s_passThrough =
            { typeof(Rectangle), typeof(Text), typeof(Image), typeof(Video), typeof(Shader), typeof(QmlElement), typeof(Shape), typeof(StylableElement) };

        private static Element Create(Type type) => type == typeof(StylableElement)
            ? new StylableElement(new NoStylable())
            : (Element)Activator.CreateInstance(type, nonPublic: true)!;

        private sealed class NoStylable : Styles.IStylable
        {
            public IElement? Build(Styles.Style style) => null;
        }

        [Test]
        public void EveryElementClass_IsClassified()
        {
            var concrete = typeof(Element).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(Element).IsAssignableFrom(t));
            var listed = s_widgets.Concat(s_resolveOnly).Concat(s_passThrough);

            Assert.That(listed, Is.EquivalentTo(concrete),
                "A new element class must be added to StylableElements (widget or container) or to s_passThrough of this test.");
        }

        [TestCaseSource(nameof(Widgets))]
        public void Widget_ResolvesAndSendsStyle(Type type)
        {
            var element = Create(type);
            Assert.That(StylableElements.IsWidget(element), Is.True);
            Assert.That(StylableElements.ResolvesStyle(element), Is.True);
        }

        [Test]
        [TestCaseSource(nameof(ResolveOnly))]
        public void Container_ResolvesStyleButSendsNothing(Type type)
        {
            var container = Create(type);
            Assert.That(StylableElements.ResolvesStyle(container), Is.True);
            Assert.That(StylableElements.IsWidget(container), Is.False);
        }

        [TestCaseSource(nameof(PassThrough))]
        public void OtherElement_PassesStyleOn(Type type)
        {
            var element = Create(type);
            Assert.That(StylableElements.ResolvesStyle(element), Is.False);
            Assert.That(StylableElements.IsWidget(element), Is.False);
        }

        private static Type[] Widgets() => s_widgets;

        private static Type[] ResolveOnly() => s_resolveOnly;

        private static Type[] PassThrough() => s_passThrough;
    }
}
