using NUnit.Framework;
using VL.Lib.IO;

namespace VL.Qt.Tests
{
    /// <summary>InputMapping: vvvv Keys / MouseButtons to Qt values.</summary>
    public class InputMappingTests
    {
        [Test]
        public void Buttons_MapEachFlag()
        {
            Assert.That(InputMapping.ToQtButtons(MouseButtons.None), Is.EqualTo(0));
            Assert.That(InputMapping.ToQtButtons(MouseButtons.Left), Is.EqualTo(1));
            Assert.That(InputMapping.ToQtButtons(MouseButtons.Right), Is.EqualTo(2));
            Assert.That(InputMapping.ToQtButtons(MouseButtons.Middle), Is.EqualTo(4));
            Assert.That(InputMapping.ToQtButtons(MouseButtons.XButton1), Is.EqualTo(8));
            Assert.That(InputMapping.ToQtButtons(MouseButtons.XButton2), Is.EqualTo(0x10));
            Assert.That(InputMapping.ToQtButtons(MouseButtons.Left | MouseButtons.Right), Is.EqualTo(3));
        }

        [Test]
        public void Modifiers_MapShiftControlAlt()
        {
            Assert.That(InputMapping.ToQtModifiers(Keys.None), Is.EqualTo(0));
            Assert.That(InputMapping.ToQtModifiers(Keys.Shift), Is.EqualTo(0x02000000));
            Assert.That(InputMapping.ToQtModifiers(Keys.Control), Is.EqualTo(0x04000000));
            Assert.That(InputMapping.ToQtModifiers(Keys.Alt), Is.EqualTo(0x08000000));
            Assert.That(InputMapping.ToQtModifiers(Keys.Control | Keys.Shift), Is.EqualTo(0x06000000));
            // The key code part is not a modifier.
            Assert.That(InputMapping.ToQtModifiers(Keys.A), Is.EqualTo(0));
        }

        [TestCase(Keys.A, 0x41)]
        [TestCase(Keys.Z, 0x5a)]
        [TestCase(Keys.D0, 0x30)]
        [TestCase(Keys.D9, 0x39)]
        [TestCase(Keys.NumPad0, 0x30)]
        [TestCase(Keys.NumPad9, 0x39)]
        [TestCase(Keys.F1, 0x01000030)]
        [TestCase(Keys.F12, 0x0100003b)]
        [TestCase(Keys.F24, 0x01000047)]
        [TestCase(Keys.Escape, 0x01000000)]
        [TestCase(Keys.Tab, 0x01000001)]
        [TestCase(Keys.Back, 0x01000003)]
        [TestCase(Keys.Return, 0x01000004)]
        [TestCase(Keys.Delete, 0x01000007)]
        [TestCase(Keys.Left, 0x01000012)]
        [TestCase(Keys.Down, 0x01000015)]
        [TestCase(Keys.PageDown, 0x01000017)]
        [TestCase(Keys.ShiftKey, 0x01000020)]
        [TestCase(Keys.LControlKey, 0x01000021)]
        [TestCase(Keys.Menu, 0x01000023)]
        [TestCase(Keys.Space, 0x20)]
        [TestCase(Keys.Add, 0x2b)]
        [TestCase(Keys.OemMinus, 0x2d)]
        public void Key_Maps(Keys key, int qt)
            => Assert.That(InputMapping.ToQtKey(key), Is.EqualTo(qt));

        [Test]
        public void Key_IgnoresModifierBits()
            => Assert.That(InputMapping.ToQtKey(Keys.A | Keys.Control | Keys.Shift), Is.EqualTo(0x41));

        [Test]
        public void Key_UnknownGivesQtKeyUnknown()
        {
            Assert.That(InputMapping.ToQtKey(Keys.None), Is.EqualTo(InputMapping.QtKeyUnknown));
            Assert.That(InputMapping.ToQtKey(Keys.ProcessKey), Is.EqualTo(InputMapping.QtKeyUnknown));
        }

        [Test]
        public void Keypad_OnlyNumPadKeysAndOperators()
        {
            Assert.That(InputMapping.IsKeypad(Keys.NumPad5), Is.True);
            Assert.That(InputMapping.IsKeypad(Keys.Divide), Is.True);
            Assert.That(InputMapping.IsKeypad(Keys.D5), Is.False);
            Assert.That(InputMapping.IsKeypad(Keys.F1), Is.False);
        }
    }
}
