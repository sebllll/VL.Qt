using System;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using P = VL.Qt.Protocol.Prop;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt
{
    /// <summary>
    /// Property diff (04c): per element type one hand-written method that compares the previous description
    /// with the new one and writes a SET only for values that changed. A just created object is compared
    /// against a default instance of its type, so only non-default values are sent (Qt defaults = element
    /// defaults, see 03c). No reflection, no allocation in the steady state.
    /// <para>
    /// Units → Qt logical pixels only via <see cref="Geometry"/> (v1: DIPTopLeft, lengths × 100; angles,
    /// opacity, scale factors and other ratios unchanged).
    /// </para>
    /// </summary>
    internal sealed partial class Reconciler
    {
        static readonly Group s_group = new();
        static readonly Rectangle s_rectangle = new();
        static readonly Text s_text = new();
        static readonly Image s_image = new();
        static readonly Row s_row = new();
        static readonly Column s_column = new();
        static readonly Video s_video = new();
        static readonly Button s_button = new();
        static readonly SpinBox s_spinBox = new();
        static readonly CheckBox s_checkBox = new();
        static readonly Switch s_switch = new();
        static readonly Slider s_slider = new();
        static readonly TextField s_textField = new();
        static readonly ShaderEffect s_shaderEffect = new();

        static Element DefaultOf(ElementType type) => type switch
        {
            ElementType.Rectangle => s_rectangle,
            ElementType.Text => s_text,
            ElementType.Image => s_image,
            ElementType.Row => s_row,
            ElementType.Column => s_column,
            ElementType.Video => s_video,
            ElementType.Button => s_button,
            ElementType.SpinBox => s_spinBox,
            ElementType.CheckBox => s_checkBox,
            ElementType.Switch => s_switch,
            ElementType.Slider => s_slider,
            ElementType.TextField => s_textField,
            ElementType.ShaderEffect => s_shaderEffect,
            _ => s_group,
        };

        partial void DiffProperties(Node node, Element? previous, Element next, Element? parent)
        {
            bool created = previous is null;
            var prev = previous ?? DefaultOf(next.Type);
            uint id = node.Id;

            DiffCommon(id, prev, next);

            switch (next.Type)
            {
                case ElementType.Rectangle: DiffRectangle(id, (Rectangle)prev, (Rectangle)next); break;
                case ElementType.Text: DiffText(id, created, (Text)prev, (Text)next); break;
                case ElementType.Image: DiffImage(id, (Image)prev, (Image)next); break;
                case ElementType.Video: DiffVideo(id, (Video)prev, (Video)next); break;
                case ElementType.Button:
                    DiffAutoSize(id, ((Button)prev).AutoSize, ((Button)next).AutoSize);
                    DiffButton(id, (Button)prev, (Button)next);
                    break;
                case ElementType.SpinBox:
                    DiffAutoSize(id, ((SpinBox)prev).AutoSize, ((SpinBox)next).AutoSize);
                    DiffSpinBox(id, (SpinBox)prev, (SpinBox)next);
                    break;
                case ElementType.CheckBox:
                    DiffAutoSize(id, ((CheckBox)prev).AutoSize, ((CheckBox)next).AutoSize);
                    DiffToggle(id, ((CheckBox)prev).Value, ((CheckBox)next).Value, ((CheckBox)prev).Enabled, ((CheckBox)next).Enabled);
                    break;
                case ElementType.Switch:
                    DiffAutoSize(id, ((Switch)prev).AutoSize, ((Switch)next).AutoSize);
                    DiffToggle(id, ((Switch)prev).Value, ((Switch)next).Value, ((Switch)prev).Enabled, ((Switch)next).Enabled);
                    break;
                case ElementType.Slider:
                    DiffAutoSize(id, ((Slider)prev).AutoSize, ((Slider)next).AutoSize);
                    DiffSlider(id, (Slider)prev, (Slider)next);
                    break;
                case ElementType.TextField:
                    DiffAutoSize(id, ((TextField)prev).AutoSize, ((TextField)next).AutoSize);
                    DiffTextField(id, (TextField)prev, (TextField)next);
                    break;
                case ElementType.Row:
                    var pr = (Row)prev; var nr = (Row)next;
                    DiffAutoSize(id, pr.AutoSize, nr.AutoSize);
                    DiffLayout(id, pr.Spacing, nr.Spacing, pr.Padding, nr.Padding, (int)pr.ChildAlignment, (int)nr.ChildAlignment);
                    break;
                case ElementType.Column:
                    var pc = (Column)prev; var nc = (Column)next;
                    DiffAutoSize(id, pc.AutoSize, nc.AutoSize);
                    DiffLayout(id, pc.Spacing, nc.Spacing, pc.Padding, nc.Padding, (int)pc.ChildAlignment, (int)nc.ChildAlignment);
                    break;
                case ElementType.ShaderEffect:
                    DiffAutoSize(id, ((ShaderEffect)prev).AutoSize, ((ShaderEffect)next).AutoSize);
                    DiffShaderEffect(id, (ShaderEffect)prev, (ShaderEffect)next);
                    break;
                default:
                    DiffAutoSize(id, ((Group)prev).AutoSize, ((Group)next).AutoSize);
                    break;
            }
        }

        /// <summary>
        /// Common item properties. Size, Anchor and Transform are sent as they are, also for children of Row/Column
        /// (there the layout places them and QtNative uses the anchor only as pivot, protocol.h "Geometry"). Visibility is QtNative's (#474): it hides the
        /// children of a ShaderEffect (drawn through their layers), Opacity 0 = not drawn.
        /// </summary>
        void DiffCommon(uint id, Element p, Element n)
        {
            if (p.Size != n.Size)
                _sink.SetVec2(id, P.Size, Geometry.ToQt(n.Size.X), Geometry.ToQt(n.Size.Y));

            if (p.Opacity != n.Opacity)
                _sink.SetF32(id, P.Opacity, n.Opacity);

            if (p.Clip != n.Clip)
                _sink.SetBool(id, P.Clip, n.Clip);

            if (p.Debug != n.Debug)
                _sink.SetBool(id, P.Debug, n.Debug);

            // Transform and Anchor are sent separately; QtNative resolves the anchor against the item's actual size
            // (follows Auto Size, layout and Size changes without a resend from here). Every TRANSFORM resets a drag
            // offset (#209, pin wins), so it is only sent on change.
            if (!Same(p.Transform, n.Transform))
                _sink.SetMat4(id, P.Transform, Geometry.ToQt(n.Transform));
            if (p.Anchor != n.Anchor)
            {
                int pa = Geometry.ToQtAlignment(p.Anchor), na = Geometry.ToQtAlignment(n.Anchor);
                if (pa != na)
                    _sink.SetI32(id, P.Anchor, na);
            }
        }
        void DiffRectangle(uint id, Rectangle p, Rectangle n)
        {
            if (p.Fill != n.Fill)
                _sink.SetColor(id, P.Color, n.Fill);
            if (p.Radius != n.Radius)
                _sink.SetF32(id, P.Radius, Geometry.ToQt(n.Radius));
            if (p.StrokeColor != n.StrokeColor)
                _sink.SetColor(id, P.BorderColor, n.StrokeColor);
            if (p.StrokeWidth != n.StrokeWidth)
                _sink.SetF32(id, P.BorderWidth, Geometry.ToQt(n.StrokeWidth));
        }

        /// <summary>Font size is always sent on creation: Qt's default font size is platform dependent.</summary>
        void DiffText(uint id, bool created, Text p, Text n)
        {
            if (!string.Equals(p.Content, n.Content))
                _sink.SetString(id, P.Text, n.Content.AsSpan());
            if (!string.Equals(p.FontFamily, n.FontFamily))
                _sink.SetString(id, P.FontFamily, n.FontFamily.AsSpan());
            if (created || Geometry.ToFontPixels(p.FontSize) != Geometry.ToFontPixels(n.FontSize))
                _sink.SetF32(id, P.FontSize, Geometry.ToFontPixels(n.FontSize));
            if (p.Weight != n.Weight)
                _sink.SetI32(id, P.FontWeight, (int)n.Weight);
            if (p.Italic != n.Italic)
                _sink.SetBool(id, P.FontItalic, n.Italic);
            if (p.Color != n.Color)
                _sink.SetColor(id, P.Color, n.Color);
            if (p.HAlign != n.HAlign)
                _sink.SetI32(id, P.HorizontalAlignment, (int)n.HAlign);
            if (p.VAlign != n.VAlign)
                _sink.SetI32(id, P.VerticalAlignment, (int)n.VAlign);
            if (p.Wrap != n.Wrap)
                _sink.SetI32(id, P.WrapMode, (int)n.Wrap);
            if (p.Elide != n.Elide)
                _sink.SetI32(id, P.Elide, (int)n.Elide);
            if (p.LineHeight != n.LineHeight)
                _sink.SetF32(id, P.LineHeight, n.LineHeight);
            if (p.LetterSpacing != n.LetterSpacing)
                _sink.SetF32(id, P.LetterSpacing, Geometry.ToQt(n.LetterSpacing));
            if (p.Autofit != n.Autofit)
                _sink.SetI32(id, P.FontSizeMode, (int)n.Autofit);
        }

        void DiffButton(uint id, Button p, Button n)
        {
            if (!string.Equals(p.Text, n.Text))
                _sink.SetString(id, P.Text, n.Text.AsSpan());
            if (p.Enabled != n.Enabled)
                _sink.SetBool(id, P.Enabled, n.Enabled);
            if (p.AutoRepeat != n.AutoRepeat)
                _sink.SetBool(id, P.AutoRepeat, n.AutoRepeat);
        }

        /// <summary>
        /// SpinBox (#210): Minimum/Maximum before Value, otherwise Qt clamps the new value with the old bounds. Qt also
        /// clamps on every bound change, possibly against an intermediate range (new Minimum with old Maximum), so Value
        /// is resent whenever a bound changed; an equal SET VALUE is a no-op in Qt and sends no EV_VALUE.
        /// The diff is against the last sent description, also after a user change (EV_VALUE): the node then describes
        /// the user's value, the SET fixes a stale SET that Qt applied after the change (see PR #354).
        /// </summary>
        void DiffSpinBox(uint id, SpinBox p, SpinBox n)
        {
            bool bounds = false;
            if (p.Minimum != n.Minimum)
            {
                _sink.SetI32(id, P.Minimum, n.Minimum);
                bounds = true;
            }
            if (p.Maximum != n.Maximum)
            {
                _sink.SetI32(id, P.Maximum, n.Maximum);
                bounds = true;
            }
            if (bounds || p.Value != n.Value)
                _sink.SetI32(id, P.Value, n.Value);
            if (p.Step != n.Step)
                _sink.SetI32(id, P.Step, n.Step);
            if (p.Editable != n.Editable)
                _sink.SetBool(id, P.Editable, n.Editable);
            if (p.Enabled != n.Enabled)
                _sink.SetBool(id, P.Enabled, n.Enabled);
        }

        /// <summary>CheckBox and Switch (#301): the value is only sent after a change; see <see cref="DiffSpinBox"/> for the diff base.</summary>
        void DiffToggle(uint id, bool pValue, bool nValue, bool pEnabled, bool nEnabled)
        {
            if (pValue != nValue)
                _sink.SetBool(id, P.Value, nValue);
            if (pEnabled != nEnabled)
                _sink.SetBool(id, P.Enabled, nEnabled);
        }

        /// <summary>Slider (#301): Minimum/Maximum before Value and Value again after a bound change, like <see cref="DiffSpinBox"/>.</summary>
        void DiffSlider(uint id, Slider p, Slider n)
        {
            bool bounds = false;
            if (p.Minimum != n.Minimum)
            {
                _sink.SetF32(id, P.Minimum, n.Minimum);
                bounds = true;
            }
            if (p.Maximum != n.Maximum)
            {
                _sink.SetF32(id, P.Maximum, n.Maximum);
                bounds = true;
            }
            if (bounds || p.Value != n.Value)
                _sink.SetF32(id, P.Value, n.Value);
            if (p.Step != n.Step)
                _sink.SetF32(id, P.Step, n.Step);
            if (p.Orientation != n.Orientation)
                _sink.SetI32(id, P.Orientation, (int)n.Orientation);
            if (p.Enabled != n.Enabled)
                _sink.SetBool(id, P.Enabled, n.Enabled);
        }

        void DiffTextField(uint id, TextField p, TextField n)
        {
            if (!string.Equals(p.Value, n.Value))
                _sink.SetString(id, P.Value, n.Value);
            if (!string.Equals(p.Placeholder, n.Placeholder))
                _sink.SetString(id, P.Placeholder, n.Placeholder);
            if (p.Editable != n.Editable)
                _sink.SetBool(id, P.Editable, n.Editable);
            if (p.Enabled != n.Enabled)
                _sink.SetBool(id, P.Enabled, n.Enabled);
        }

        /// <summary>
        /// Image: the resource id of the QtImage (#362), 0 = none. Sent when the id changes; a QtImage whose content is
        /// replaced in place (ToQtImage) keeps its id, QtNative swaps the texture itself.
        /// </summary>
        void DiffImage(uint id, Image p, Image n)
        {
            ulong pi = p.Source?.Id ?? 0, ni = n.Source?.Id ?? 0;
            if (pi != ni)
                _sink.SetU64(id, P.Image, ni);
            if (p.FillMode != n.FillMode)
                _sink.SetI32(id, P.FillMode, (int)n.FillMode);
            if (p.Smooth != n.Smooth)
                _sink.SetBool(id, P.Smooth, n.Smooth);
        }

        /// <summary>
        /// Video: Rate is clamped to &gt;= <see cref="Video.MinRate"/> and Volume to 0..1 here (QtNative rejects other
        /// values with -7), so a patch can animate them freely. Values equal after clamping are not resent.
        /// </summary>
        void DiffVideo(uint id, Video p, Video n)
        {
            string pp = p.Path?.Value ?? string.Empty;
            string np = n.Path?.Value ?? string.Empty;
            if (!string.Equals(pp, np))
                _sink.SetString(id, P.Source, np.AsSpan());
            if (p.FillMode != n.FillMode)
                _sink.SetI32(id, P.FillMode, (int)n.FillMode);
            if (p.Loop != n.Loop)
                _sink.SetBool(id, P.Loop, n.Loop);
            if (p.KeepLastFrame != n.KeepLastFrame)
                _sink.SetBool(id, P.KeepLastFrame, n.KeepLastFrame);
            float pr = Video.ClampRate(p.Rate), nr = Video.ClampRate(n.Rate);
            if (pr != nr)
                _sink.SetF32(id, P.PlaybackRate, nr);
            float pv = Video.ClampVolume(p.Volume), nv = Video.ClampVolume(n.Volume);
            if (pv != nv)
                _sink.SetF32(id, P.Volume, nv);
            // Last: QtNative starts playback when Playing arrives (or once the media is loaded).
            if (p.Playing != n.Playing)
                _sink.SetBool(id, P.Playing, n.Playing);
        }

        /// <summary>
        /// Auto Size (containers and Button): QtNative applies the implicit size while on and the last Size while off,
        /// so only the flag is sent here; Size is not resent when it flips.
        /// </summary>
        void DiffAutoSize(uint id, bool p, bool n)
        {
            if (p != n)
                _sink.SetBool(id, P.AutoSize, n);
        }

        void DiffLayout(uint id, float pSpacing, float nSpacing, float pPadding, float nPadding, int pAlign, int nAlign)
        {
            if (pSpacing != nSpacing)
                _sink.SetF32(id, P.Spacing, Geometry.ToQt(nSpacing));
            if (pPadding != nPadding)
                _sink.SetF32(id, P.Padding, Geometry.ToQt(nPadding));
            if (pAlign != nAlign)
                _sink.SetI32(id, P.ChildAlignment, nAlign);
        }

        /// <summary>
        /// ShaderEffect (#437): shader resource ids (0 = none), Blending, MeshResolution (clamped to &gt;= 1, QtNative rejects
        /// smaller values with -7). Source and the uniforms follow in <see cref="ChildrenReconciled"/>.
        /// </summary>
        void DiffShaderEffect(uint id, ShaderEffect p, ShaderEffect n)
        {
            if (p.FragmentShaderId != n.FragmentShaderId)
                _sink.SetU64(id, P.FragmentShader, n.FragmentShaderId);
            if (p.VertexShaderId != n.VertexShaderId)
                _sink.SetU64(id, P.VertexShader, n.VertexShaderId);
            if (p.Blending != n.Blending)
                _sink.SetBool(id, P.Blending, n.Blending);
            int px = Math.Max(1, p.MeshResolution.X), py = Math.Max(1, p.MeshResolution.Y);
            int nx = Math.Max(1, n.MeshResolution.X), ny = Math.Max(1, n.MeshResolution.Y);
            if (px != nx || py != ny)
                _sink.SetIvec2(id, P.MeshResolution, nx, ny);
        }

        /// <summary>
        /// ShaderEffect: SET Source ref(input id) whenever the input child object changed (0 = none), then the uniforms (texture uniforms need the ids of their children).
        /// </summary>
        partial void ChildrenReconciled(Node node, Element next)
        {
            if (node.Type != ElementType.ShaderEffect)
                return;
            {
                var effect = (ShaderEffect)next;
                var kids = node.Children;
                // Input is the first child (ShaderEffect.Children), texture elements follow.
                Node? source = effect.Input is not null && kids.Count > 0 ? kids[0] : null;
                if (!ReferenceEquals(source, node.EffectSource))
                {
                    node.EffectSource = source;
                    _sink.SetRef(node.Id, P.Source, source?.Id ?? IdAllocator.Root);
                }
                node.SentUniforms = UniformWriter.Write(_sink, node.Id, effect, kids, node.SentUniforms);
            }
        }

        /// <summary>Exact component-wise equality (any change, however small, is sent).</summary>
        static bool Same(in Matrix a, in Matrix b) =>
            a.M11 == b.M11 && a.M12 == b.M12 && a.M13 == b.M13 && a.M14 == b.M14 &&
            a.M21 == b.M21 && a.M22 == b.M22 && a.M23 == b.M23 && a.M24 == b.M24 &&
            a.M31 == b.M31 && a.M32 == b.M32 && a.M33 == b.M33 && a.M34 == b.M34 &&
            a.M41 == b.M41 && a.M42 == b.M42 && a.M43 == b.M43 && a.M44 == b.M44;
    }
}
