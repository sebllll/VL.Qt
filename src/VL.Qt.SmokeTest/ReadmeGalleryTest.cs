using System;
using System.Drawing;
using System.IO;
using Stride.Core.Mathematics;
using VL.Qt;
using VL.Qt.Styles;

// README pictures of the other sections (not a test of behaviour): styles, layout, images and the basic effects, rendered
// with raw commands and copied to docs/images/ like ReadmeImagesTest. Dark ground 0x16, one window per picture in DIP;
// the grab is scaled to the target width on Windows. Checks only that the picture is not empty.
//   styles.png         the same widgets in the base style and in a light style (Style pin on a Group in the patch)
//   layout.png         Column (centered), Row (bottom aligned) and nested Row/Column with spacing and padding
//   images.png         one image in the four fill modes Stretch, PreserveAspectFit, PreserveAspectCrop, Tile
//   effects-basic.png  Blur, DropShadow, ColorAdjust, Colorize and Mask on the same element
static class ReadmeGalleryTest
{
    const float Bg = 0x16 / 255f;

    static Color4 C(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    static StateValues<Color4> Colors(uint normal, uint hovered, uint pressed, uint disabled, uint @checked = 0)
    {
        var v = default(StateValues<Color4>).With(StyleStates.Normal, C(normal)).With(StyleStates.Hovered, C(hovered))
            .With(StyleStates.Pressed, C(pressed)).With(StyleStates.Disabled, C(disabled));
        return @checked == 0 ? v : v.With(StyleStates.Checked, C(@checked));
    }

    // The base style with light colors: dimensions, fonts and states stay.
    static Style Light()
    {
        var b = BaseStyle.Default;
        return b with
        {
            Frame = b.Frame! with
            {
                Color = Colors(0xE8E8E8, 0xDADADA, 0xCCCCCC, 0xEFEFEF),
                BorderColor = default(StateValues<Color4>).With(StyleStates.Normal, C(0xB4B4B4)).With(StyleStates.Hovered, C(0x8A8A8A)).With(StyleStates.Disabled, C(0xD0D0D0)),
            },
            Text = b.Text! with { Color = default(StateValues<Color4>).With(StyleStates.Normal, C(0x202020)).With(StyleStates.Disabled, C(0xA0A0A0)) },
            Indicator = b.Indicator! with { Color = Colors(0x707070, 0x505050, 0x202020, 0xB0B0B0, 0x2F6FBF) },
            Track = b.Track! with
            {
                Color = default(StateValues<Color4>).With(StyleStates.Normal, C(0xD0D0D0)).With(StyleStates.Disabled, C(0xE0E0E0)),
                FillColor = default(StateValues<Color4>).With(StyleStates.Normal, C(0x3B7DD8)).With(StyleStates.Hovered, C(0x2F6FBF)).With(StyleStates.Disabled, C(0x9DB8DC)),
            },
        };
    }

    public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
    {
        Program.NeedsRhi(check, "the effects are ShaderEffects");
        Directory.CreateDirectory(outDir);
        var imagesDir = ReadmeImagesTest.FindUp("docs", "images");
        check(imagesDir != null, $"readme-gallery: docs/images found ({imagesDir})");
        if (imagesDir == null) return;

        var cmd = new CommandWriter();
        uint next = 1, order = 0;

        uint Rect(float x, float y, float w, float h, uint rgb, float radius = 0, uint parent = 0)
        {
            var id = next++;
            cmd.Create(id, Protocol.Type.Rectangle);
            cmd.SetPlace(id, x, y, w, h);
            var c = C(rgb);
            cmd.SetColor(id, Protocol.Prop.Color, c.R, c.G, c.B, 1);
            if (radius > 0) cmd.SetF32(id, Protocol.Prop.Radius, radius);
            cmd.Reparent(id, parent, order++);
            return id;
        }
        uint Label(string text, float x, float y, float w, float h, float size, uint rgb, int weight = 400, int align = 1, bool reparent = true, uint parent = 0)
        {
            var id = next++;
            cmd.Create(id, Protocol.Type.Text);
            cmd.SetPlace(id, x, y, w, h);
            cmd.SetString(id, Protocol.Prop.Text, text);
            cmd.SetF32(id, Protocol.Prop.FontSize, size);
            cmd.SetI32(id, Protocol.Prop.FontWeight, weight);
            cmd.SetI32(id, Protocol.Prop.HorizontalAlignment, align);
            cmd.SetI32(id, Protocol.Prop.VerticalAlignment, 128);
            var c = C(rgb);
            cmd.SetColor(id, Protocol.Prop.Color, c.R, c.G, c.B, 1);
            if (reparent) cmd.Reparent(id, parent, order++);
            return id;
        }
        uint Container(ushort type, float x, float y, float w, float h, float spacing, float padding, int align, uint parent = 0)
        {
            var id = next++;
            cmd.Create(id, type);
            cmd.SetBool(id, Protocol.Prop.AutoSize, false);
            cmd.SetPlace(id, x, y, w, h);
            cmd.SetF32(id, Protocol.Prop.Spacing, spacing);
            cmd.SetF32(id, Protocol.Prop.Padding, padding);
            if (align != 0) cmd.SetI32(id, Protocol.Prop.ChildAlignment, align);
            cmd.Reparent(id, parent, order++);
            return id;
        }

        void Render(string name, int width, float winW, float winH, Action build)
        {
            cmd.Reset();
            next = 1;
            order = 0;
            var w = Native.vlqt_window_create((int)MathF.Round(winW), (int)MathF.Round(winH), "VL.Qt readme gallery");
            check(w != IntPtr.Zero, $"readme-gallery {name}: window_create");
            Native.vlqt_window_show(w);
            pump(4);
            cmd.ClearColor(Bg, Bg, Bg, 1);
            build();
            var rc = Native.Apply(w, cmd.EndFrame());
            check(rc == 0, $"readme-gallery {name}: apply ({rc}) {(rc != 0 ? Native.LastError() : "")}");
            cmd.Reset();
            pump(10);
            var grabPath = Path.Combine(outDir, $"readme-gallery_{name}.png");
            using (var a = grab(w, grabPath))
            {
                var ink = ReadmeImagesTest.Ink(a);
                check(ink > 0.02, $"readme-gallery {name}: picture not empty (ink {ink:P1}, {a.Width} x {a.Height})");
            }
            var target = ReadmeImagesTest.Save(grabPath, Path.Combine(imagesDir!, name + ".png"), width);
            Console.WriteLine($"INFO readme-gallery: {target} ({new FileInfo(target).Length / 1024} KB)");
            Native.vlqt_window_destroy(w);
            pump(1);
        }

        // --- styles.png: the same panel twice
        void Panel(float ox, string title, Style? style, uint ground, uint textRgb)
        {
            Rect(ox, 0, 400, 250, ground, 10);
            Label(title, ox + 20, 10, 360, 30, 16, textRgb, 700);
            uint Add(ushort type, float x, float y, float w = 0, float h = 0, bool enabled = true)
            {
                var id = next++;
                cmd.Create(id, type);
                if (style == null) BaseStyle.Write(cmd, id); else StyleWriter.Write(cmd, id, style, null);
                cmd.SetPlace(id, ox + x, y, w, h);
                if (!enabled) cmd.SetBool(id, Protocol.Prop.Enabled, false);
                cmd.Reparent(id, 0, order++);
                return id;
            }
            var b = Add(Protocol.Type.Button, 20, 50);
            cmd.SetString(b, Protocol.Prop.Text, "Button");
            b = Add(Protocol.Type.Button, 110, 50, enabled: false);
            cmd.SetString(b, Protocol.Prop.Text, "Disabled");
            var c = Add(Protocol.Type.CheckBox, 215, 52);
            cmd.SetBool(c, Protocol.Prop.Value, true);
            c = Add(Protocol.Type.Switch, 255, 50);
            cmd.SetBool(c, Protocol.Prop.Value, true);
            c = Add(Protocol.Type.Switch, 320, 50);
            cmd.SetBool(c, Protocol.Prop.Value, false);
            var s = Add(Protocol.Type.Slider, 20, 110, 360, 30);
            cmd.SetF32(s, Protocol.Prop.Value, 0.35f);
            var t = Add(Protocol.Type.TextField, 20, 170);
            cmd.SetString(t, Protocol.Prop.Value, "Some text");
            var n = Add(Protocol.Type.SpinBox, 250, 170);
            cmd.SetI32(n, Protocol.Prop.Value, 42);
            cmd.SetI32(n, Protocol.Prop.Minimum, 0);
            cmd.SetI32(n, Protocol.Prop.Maximum, 100);
        }
        Render("styles", 820, 820, 250, () =>
        {
            Panel(0, "VLQt (base style)", null, 0x1E2128, 0xE0E0E0);
            Panel(420, "Light style", Light(), 0xF6F6F6, 0x202020);
        });

        // --- layout.png
        const uint orange = 0xFF8C33, cyan = 0x26C7FF, pink = 0xFF5C9E, green = 0x7CD66B;
        Render("layout", 820, 820, 250, () =>
        {
            const float panelW = 260;
            for (var i = 0; i < 3; i++)
                Rect(i * 280, 0, panelW, 250, 0x1E2128, 10);
            // Column, children centered
            Label("Column", 14, 8, 232, 26, 15, 0x8591A8);
            var col = Container(Protocol.Type.Column, 0, 40, panelW, 200, 10, 14, 0x4);
            foreach (var (w, rgb) in new[] { (120f, orange), (180f, cyan), (80f, pink) })
                Rect(0, 0, w, 44, rgb, 6, col);
            // Row, children at the bottom
            Label("Row", 294, 8, 232, 26, 15, 0x8591A8);
            var row = Container(Protocol.Type.Row, 280, 40, panelW, 200, 10, 14, 0x40);
            foreach (var (h, rgb) in new[] { (60f, orange), (120f, cyan), (90f, pink), (150f, green) })
                Rect(0, 0, 44, h, rgb, 6, row);
            // Nested: Column with a Text, a Row and a Text
            Label("Nested", 574, 8, 232, 26, 15, 0x8591A8);
            var outer = Container(Protocol.Type.Column, 560, 40, panelW, 200, 8, 14, 0);
            Label("Title", 0, 0, 200, 34, 24, 0xF0EDE6, 700, 1, true, outer);
            var inner = Container(Protocol.Type.Row, 0, 0, 232, 60, 8, 0, 0, outer);
            foreach (var rgb in new[] { orange, cyan, pink, green })
                Rect(0, 0, 50, 50, rgb, 25, inner);
            Label("Spacing and Padding", 0, 0, 232, 40, 13, 0x8591A8, 400, 1, true, outer);
        });

        // --- images.png
        var asset = ImageTest.FindAsset();
        check(asset != null, "readme-gallery: stripes asset found");
        if (asset != null)
        {
            var stripes = ImageTest.Load(asset, check, "stripes");
            Render("images", 820, 810, 230, () =>
            {
                var modes = new[] { ("Stretch", 0), ("PreserveAspectFit", 1), ("PreserveAspectCrop", 2), ("Tile", 3) };
                for (var i = 0; i < modes.Length; i++)
                {
                    var x = i * 200;
                    Rect(x, 0, 190, 230, 0x1E2128, 10);
                    var id = next++;
                    cmd.Create(id, Protocol.Type.Image);
                    cmd.SetPlace(id, x + 20, 20, 150, 150);
                    cmd.SetU64(id, Protocol.Prop.Image, stripes);
                    if (modes[i].Item2 != 0) cmd.SetI32(id, Protocol.Prop.FillMode, modes[i].Item2);
                    cmd.SetBool(id, Protocol.Prop.Smooth, false);
                    cmd.Reparent(id, 0, order++);
                    Label(modes[i].Item1, x + 10, 185, 170, 30, 14, 0x8591A8, 400, 4);
                }
            });
        }

        // --- effects-basic.png: 3 x 2 cells of 410 x 230
        static void Blur(CommandWriter c)
        {
            c.EffectBool(Protocol.Prop.EffectBlurEnabled, true);
            c.EffectF32(Protocol.Prop.EffectBlur, 0.3f);
            c.EffectI32(Protocol.Prop.EffectBlurMax, 24);
        }
        static void Shadow(CommandWriter c)
        {
            c.EffectBool(Protocol.Prop.EffectShadowEnabled, true);
            c.EffectColor(Protocol.Prop.EffectShadowColor, 0, 0, 0, 0.9f);
            c.EffectF32(Protocol.Prop.EffectShadowHorizontalOffset, 10);
            c.EffectF32(Protocol.Prop.EffectShadowVerticalOffset, 12);
            c.EffectF32(Protocol.Prop.EffectShadowBlur, 0.45f);
        }
        static void Adjust(CommandWriter c)
        {
            c.EffectF32(Protocol.Prop.EffectSaturation, -1);
            c.EffectF32(Protocol.Prop.EffectContrast, 0.25f);
        }
        static void Colorize(CommandWriter c)
        {
            c.EffectF32(Protocol.Prop.EffectColorization, 0.75f);
            c.EffectColor(Protocol.Prop.EffectColorizationColor, 0.2f, 0.45f, 1, 1);
        }
        Render("effects-basic", 820, 1230, 460, () =>
        {
            var cells = new[] { "Original", "Blur", "DropShadow", "ColorAdjust", "Colorize", "Mask" };
            for (var i = 0; i < cells.Length; i++)
            {
                float ox = i % 3 * 410, oy = i / 3 * 230;
                Rect(ox + 5, oy + 5, 400, 220, 0x30364A, 10);
                Label(cells[i], ox + 22, oy + 188, 366, 26, 15, 0x9AA5BA, 400, 1);
                if (i == 5)
                {
                    var masked = Rect(ox + 55, oy + 45, 300, 110, cyan, 12);
                    var mask = Label("VL.Qt", ox + 55, oy + 45, 300, 110, 90, 0xFFFFFF, 700, 4, reparent: false);
                    cmd.Chain(masked, c => c.EffectRef(Protocol.Prop.EffectMaskSource, mask));
                    continue;
                }
                var target = Container(Protocol.Type.Row, ox + 75, oy + 55, 260, 100, 10, 0, 0);
                foreach (var rgb in new[] { orange, cyan, pink })
                    Rect(0, 0, 80, 80, rgb, 14, target);
                switch (i)
                {
                    case 1: cmd.Chain(target, Blur); break;
                    case 2: cmd.Chain(target, Shadow); break;
                    case 3: cmd.Chain(target, Adjust); break;
                    case 4: cmd.Chain(target, Colorize); break;
                }
            }
        });
    }
}
