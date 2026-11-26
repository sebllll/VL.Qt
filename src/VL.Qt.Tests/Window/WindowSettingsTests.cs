using System;
using System.IO;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Qt.Nodes;
using VL.Qt.Tests.Support;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Tests.Window
{
    /// <summary>Window settings: icon path resolved once, bounds on create, node identity.</summary>
    public class WindowSettingsTests
    {
        private static WindowSettings Settings(Optional<string> icon = default, Optional<RectangleF> bounds = default)
            => new(icon, bounds);

        [Test]
        public void Bounds_SettingsWinOnCreate()
        {
            var pin = new RectangleF(1, 2, 3, 4);
            var set = new RectangleF(10, 20, 300, 400);
            Assert.That(WindowSettings.ResolveBounds(null, pin), Is.EqualTo(pin));
            Assert.That(WindowSettings.ResolveBounds(Settings(), pin), Is.EqualTo(pin));
            Assert.That(WindowSettings.ResolveBounds(Settings(bounds: set), pin), Is.EqualTo(set));
        }

        [Test]
        public void Icon_EmptyOrUnset_IsDefault()
        {
            Assert.That(Settings(icon: "").IconPath, Is.Null);
            Assert.That(Settings(icon: "  ").IconPath, Is.Null);
            Assert.That(Settings().IconPath, Is.Null);
        }

        [Test]
        public void Icon_RelativePath_BecomesFullPath()
        {
            var full = Settings(icon: System.IO.Path.Combine("img", "app.ico")).IconPath;
            Assert.That(full, Is.EqualTo(System.IO.Path.GetFullPath(System.IO.Path.Combine("img", "app.ico"))));
            Assert.That(System.IO.Path.IsPathRooted(full), Is.True);
        }

        [Test]
        public void Icon_InvalidPath_StaysAsGiven()
            => Assert.That(WindowSettings.IconFullPath("bad\0path"), Is.EqualTo("bad\0path"));

        [Test]
        public void IconPathAndBounds_ReadPerFrame_AllocateNothing()
        {
            // The icon path is resolved in the constructor: reading it every frame costs nothing.
            var settings = Settings(icon: System.IO.Path.Combine("img", "app.ico"), bounds: new RectangleF(1, 2, 3, 4));
            var pin = new RectangleF(5, 6, 7, 8);
            string? path = null;
            RectangleF bounds = default;
            Action frame = () => { path = settings.IconPath; bounds = WindowSettings.ResolveBounds(settings, pin); };
            Assert.That(Allocations.Fewest(frame, 100), Is.EqualTo(0));
            Assert.That(path, Is.Not.Null);
        }

        [Test]
        public void Node_SameInputs_SameInstance()
        {
            var node = new WindowSettingsNode();
            var a = node.Update(new Path("a.png"), default);
            Assert.That(node.Update(new Path("a.png"), default), Is.SameAs(a));
            Assert.That(node.Update(new Path("b.png"), default), Is.Not.SameAs(a));
        }

        [Test]
        public void Node_UnsetPins_StayUnset()
        {
            var node = new WindowSettingsNode();
            var s = node.Update(default, default);
            Assert.That(s.Icon.HasValue, Is.False);
            Assert.That(s.Bounds.HasValue, Is.False);
            Assert.That(node.Update(default, default), Is.SameAs(s));
        }
    }
}
