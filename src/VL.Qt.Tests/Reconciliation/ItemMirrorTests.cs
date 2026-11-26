using System;
using System.Collections.Generic;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;

namespace VL.Qt.Tests.Reconciliation
{
    public class ItemMirrorTests
    {
        [Test]
        public void NewMirrorIsEmptyAndUnbound()
        {
            var n = new ItemMirror(3, ElementType.Rectangle);
            Assert.That(n.Id, Is.EqualTo(3u));
            Assert.That(n.Type, Is.EqualTo(ElementType.Rectangle));
            Assert.That(n.Element, Is.Null);
            Assert.That(n.Children, Is.Empty);
            Assert.That(n.BoundSlot.IsNone, Is.True);
        }
    }
}
