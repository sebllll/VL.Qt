using System.IO;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Qt.Nodes;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Tests
{
    /// <summary>Window settings (#517, #518): precedence over the Renderer pins, icon path, node identity.</summary>
    public class WindowSettingsTests
    {
        static WindowSettings Settings(Optional<string> icon = default, Optional<string> title = default, Optional<bool> borderless = default,
            Optional<bool> fullscreen = default, Optional<RectangleF> bounds = default)
            => new(icon, title, borderless, fullscreen, bounds);

        [Test]
        public void NoSettings_UsesRendererPins()
        {
            var r = WindowSettings.Resolve(null, "pin", true, false);
            Assert.That(r, Is.EqualTo(new WindowSettings.Resolved("pin", true, false, null)));
        }

        [Test]
        public void UnsetValues_UsePins()
        {
            var r = WindowSettings.Resolve(Settings(), "pin", true, true);
            Assert.That(r, Is.EqualTo(new WindowSettings.Resolved("pin", true, true, null)));
        }

        [Test]
        public void SetValues_WinOverPins()
        {
            var s = Settings(title: "settings", borderless: false, fullscreen: true);
            var r = WindowSettings.Resolve(s, "pin", true, false);
            Assert.That(r.Title, Is.EqualTo("settings"));
            Assert.That(r.Borderless, Is.False); // false wins over a pin of true
            Assert.That(r.Fullscreen, Is.True);
        }

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
            Assert.That(WindowSettings.Resolve(Settings(icon: ""), "t", false, false).IconPath, Is.Null);
            Assert.That(WindowSettings.Resolve(Settings(icon: "  "), "t", false, false).IconPath, Is.Null);
            Assert.That(WindowSettings.Resolve(Settings(), "t", false, false).IconPath, Is.Null);
        }

        [Test]
        public void Icon_RelativePath_BecomesFullPath()
        {
            var full = WindowSettings.Resolve(Settings(icon: System.IO.Path.Combine("img", "app.ico")), "t", false, false).IconPath;
            Assert.That(full, Is.EqualTo(System.IO.Path.GetFullPath(System.IO.Path.Combine("img", "app.ico"))));
            Assert.That(System.IO.Path.IsPathRooted(full), Is.True);
        }

        [Test]
        public void Icon_InvalidPath_StaysAsGiven()
            => Assert.That(WindowSettings.IconFullPath("bad\0path"), Is.EqualTo("bad\0path"));

        [Test]
        public void Node_SameInputs_SameInstance()
        {
            var node = new WindowSettingsNode();
            var a = node.Update(new Path("a.png"), "T", default, default, default);
            Assert.That(node.Update(new Path("a.png"), "T", default, default, default), Is.SameAs(a));
            Assert.That(node.Update(new Path("b.png"), "T", default, default, default), Is.Not.SameAs(a));
        }

        [Test]
        public void Node_UnsetPins_StayUnset()
        {
            var node = new WindowSettingsNode();
            var s = node.Update(default, default, default, default, default);
            Assert.That(s.Icon.HasValue, Is.False);
            Assert.That(s.Title.HasValue, Is.False);
            Assert.That(s.Borderless.HasValue, Is.False);
            Assert.That(s.Fullscreen.HasValue, Is.False);
            Assert.That(s.Bounds.HasValue, Is.False);
            Assert.That(node.Update(default, default, default, default, default), Is.SameAs(s));
        }
    }
}
