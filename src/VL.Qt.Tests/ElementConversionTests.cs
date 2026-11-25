using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using VL.Qt.Styles;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>
    /// The conversion of a pin value (<see cref="IElement"/>) to the internal <see cref="Element"/>, the warning for a
    /// foreign implementation, and stylables that return stylables.
    /// </summary>
    public class ElementConversionTests
    {
        /// <summary>Returns what <see cref="Next"/> says; counts its builds.</summary>
        private sealed class Delegating : IStylable
        {
            public IElement? Next { get; set; }
            public int Builds { get; private set; }

            public IElement? Build(Style style)
            {
                Builds++;
                return Next;
            }
        }

        private sealed class Foreign : IElement
        {
        }

        private sealed class Warnings
        {
            public List<string> Shown { get; } = new();
            public int Removed { get; private set; }

            public IDisposable Add(string text)
            {
                Shown.Add(text);
                return new Remover(this);
            }

            private sealed class Remover : IDisposable
            {
                private readonly Warnings _owner;
                public Remover(Warnings owner) => _owner = owner;
                public void Dispose() => _owner.Removed++;
            }
        }

        private RecordingSink _sink = null!;
        private Reconciler _rec = null!;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _rec = new Reconciler(_sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
        }

        private void Update(Element? root) => _rec.Update(root, Space.DIPTopLeft);

        // Conversion ---------------------------------------------------------

        [Test]
        public void Element_IsReturnedAsItIs()
        {
            var r = new Rectangle();
            Assert.That(ElementConversion.ToElement(r), Is.SameAs(r));
            Assert.That(ElementConversion.ToElement(null), Is.Null);
        }

        [Test]
        public void Stylable_BecomesOnePlaceholder_SameInstanceEveryTime()
        {
            var s = new Delegating();
            var first = ElementConversion.ToElement(s);

            Assert.That(first, Is.TypeOf<StylableElement>());
            Assert.That(((StylableElement)first!).Stylable, Is.SameAs(s));
            Assert.That(ElementConversion.ToElement(s), Is.SameAs(first));
            Assert.That(ElementConversion.ToElement(new Delegating()), Is.Not.SameAs(first));
        }

        [Test]
        public void Stylable_SteadyState_DoesNotAllocate()
        {
            var s = new Delegating();
            _ = ElementConversion.ToElement(s);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
                _ = ElementConversion.ToElement(s);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(256));
        }

        [Test]
        public void Foreign_IsNull_AndForeign()
        {
            var f = new Foreign();
            Assert.That(ElementConversion.ToElement(f), Is.Null);
            Assert.That(ElementConversion.IsForeign(f), Is.True);
            Assert.That(ElementConversion.IsForeign(new Rectangle()), Is.False);
            Assert.That(ElementConversion.IsForeign(new Delegating()), Is.False);
            Assert.That(ElementConversion.IsForeign(null), Is.False);
        }

        [Test]
        public void ElementInput_Foreign_WarnsOnce_RemovedWhenGone()
        {
            var warnings = new Warnings();
            using var input = new ElementInput(NodeErrors.ShownBy(warnings.Add));

            Assert.That(input.Update(new Foreign()), Is.Null);
            Assert.That(input.Update(new Foreign()), Is.Null);
            Assert.That(warnings.Shown, Has.Count.EqualTo(1));
            Assert.That(warnings.Shown[0], Does.Contain(nameof(Foreign)));
            Assert.That(input.Warning, Is.EqualTo(warnings.Shown[0]));

            var r = new Rectangle();
            Assert.That(input.Update(r), Is.SameAs(r));
            Assert.That(input.Warning, Is.Null);
            Assert.That(warnings.Removed, Is.EqualTo(1));
        }

        [Test]
        public void ChildrenInput_ForeignChild_Warns()
        {
            var warnings = new Warnings();
            using var input = new ChildrenInput(NodeErrors.ShownBy(warnings.Add));
            var r = new Rectangle();

            var children = input.Update(new IElement?[] { r, new Foreign() }.ToSpread());

            Assert.That(children, Is.EqualTo(new Element[] { r }));
            Assert.That(input.Warning, Does.Contain(nameof(Foreign)));

            input.Update(new IElement?[] { r }.ToSpread());
            Assert.That(input.Warning, Is.Null);
            Assert.That(warnings.Removed, Is.EqualTo(1));
        }

        // Stylables returning stylables ---------------------------------------

        [Test]
        public void NestedStylable_IsBuiltInTurn()
        {
            var r = new Rectangle();
            var inner = new Delegating { Next = r };
            var outer = new Delegating { Next = inner };

            Update(new Group { Children = ImmutableArray.Create(ElementConversion.ToElement(outer)!) });

            Assert.That(outer.Builds, Is.EqualTo(1));
            Assert.That(inner.Builds, Is.EqualTo(1));
            Assert.That(_rec.Root!.Children[0].Element, Is.SameAs(r));
            Assert.That(((StylableElement)ElementConversion.ToElement(outer)!).Built, Is.SameAs(r));
        }

        [Test]
        public void SelfReturningStylable_ShowsNothing_ErrorAtParent()
        {
            var feedback = new ElementFeedback();
            _ = feedback[0];
            var self = new Delegating();
            self.Next = self;

            Update(new Group { Feedback = new FeedbackSlot(feedback, 0), Children = ImmutableArray.Create(ElementConversion.ToElement(self)!) });

            Assert.That(self.Builds, Is.EqualTo(1));
            Assert.That(feedback[0].BuildError, Does.Contain("itself"));
            Assert.That(_rec.Root!.Children[0].Element, Is.TypeOf<Group>());
        }

        [Test]
        public void CycleOfStylables_Stops()
        {
            var a = new Delegating();
            var b = new Delegating { Next = a };
            var c = new Delegating { Next = b };
            a.Next = c;
            var start = new Delegating { Next = a };

            Update(ElementConversion.ToElement(start));

            Assert.That(_rec.RootBuildError, Does.Contain("cycle"));
            Assert.That(a.Builds + b.Builds + c.Builds, Is.LessThan(100));
            Assert.That(_rec.Root!.Element, Is.TypeOf<Group>());
        }

        [Test]
        public void ForeignResultOfBuild_IsAnError()
        {
            var s = new Delegating { Next = new Foreign() };

            Update(ElementConversion.ToElement(s));

            Assert.That(_rec.RootBuildError, Does.Contain(nameof(Foreign)));
        }

        [Test]
        public void ExceptionAtRoot_IsRootBuildError_ClearedBySuccess()
        {
            var thrower = new ThrowingStylable();
            var root = ElementConversion.ToElement(thrower);

            Update(root);
            Assert.That(_rec.RootBuildError, Does.Contain("boom"));

            thrower.Throw = false;
            Update(root);
            Assert.That(_rec.RootBuildError, Is.Null);
            Assert.That(_rec.Root!.Element, Is.TypeOf<Rectangle>());
        }

        [Test]
        public void PlainRoot_HasNoRootBuildError()
        {
            Update(new Rectangle());
            Assert.That(_rec.RootBuildError, Is.Null);
        }

        [Test]
        public void StylableAsMaskSource_IsBuiltEveryUpdate()
        {
            var s = new Delegating { Next = new Rectangle() };
            var root = new Rectangle
            {
                Effect = new MaskEffect(null, ElementConversion.ToElement(s), 0, 0, 1, 0, false),
            };

            Update(root);
            Update(root);
            Update(root);

            Assert.That(s.Builds, Is.EqualTo(3));
        }

        [Test]
        public void StylableAsMaskSource_DeepInsideUnchangedTree_IsBuiltEveryUpdate()
        {
            var s = new Delegating { Next = new Rectangle() };
            var host = new Rectangle { Effect = new MaskEffect(null, ElementConversion.ToElement(s), 0, 0, 1, 0, false) };
            var root = new Group { Children = ImmutableArray.Create<Element>(new Row { Children = ImmutableArray.Create<Element>(host) }) };

            Update(root);
            _sink.Clear();
            Update(root);
            Update(root);

            Assert.That(s.Builds, Is.EqualTo(3));
            Assert.That(_sink.Commands, Is.Empty);
        }

        [Test]
        public void StylableInsideMaskSource_IsBuiltEveryUpdate()
        {
            var s = new Delegating { Next = new Rectangle() };
            var source = new Group { Children = ImmutableArray.Create<Element>(new Group { Children = ImmutableArray.Create(ElementConversion.ToElement(s)!) }) };
            var root = new Rectangle { Effect = new MaskEffect(null, source, 0, 0, 1, 0, false) };

            Update(root);
            Update(root);

            Assert.That(s.Builds, Is.EqualTo(2));
        }

        [Test]
        public void StylableAsUniformTexture_IsBuiltEveryUpdate()
        {
            var s = new Delegating { Next = new Rectangle() };
            var root = new Rectangle { Effect = new TextureEffect(Uniform.FromTexture("map", ElementConversion.ToElement(s))) };

            Update(root);
            Update(root);
            Update(root);

            Assert.That(s.Builds, Is.EqualTo(3));
        }

        [Test]
        public void MaskSourceReplacedByPlainElement_StopsBuilding()
        {
            var s = new Delegating { Next = new Rectangle() };
            var mask = new MaskEffect(null, ElementConversion.ToElement(s), 0, 0, 1, 0, false);

            Update(new Rectangle { Effect = mask });
            Update(new Rectangle { Effect = new MaskEffect(null, new Rectangle(), 0, 0, 1, 0, false) });
            var after = s.Builds;
            Update(_rec.Root!.Element);
            Update(_rec.Root!.Element);

            Assert.That(s.Builds, Is.EqualTo(after));
            Assert.That(_rec.Root!.ContainsStylable, Is.False);
        }

        private sealed class TextureEffect : ShaderEffect
        {
            public TextureEffect(params Uniform[] uniforms)
                : base(null, Wire.Protocol.EffectTemplate.Single, ImmutableArray.Create(7UL), uniforms.ToSpread(), default, 1)
            {
            }
        }

        private sealed class ThrowingStylable : IStylable
        {
            private readonly Rectangle _rectangle = new();
            public bool Throw { get; set; } = true;

            public IElement? Build(Style style) => Throw ? throw new InvalidOperationException("boom") : _rectangle;
        }
    }
}
