using System;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.Paths;
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
    /// Units → Qt logical pixels only via the <see cref="SpaceMapping"/> of the scene (<c>_mapping</c>; angles,
    /// opacity, scale factors and other ratios unchanged). A length is written again when the unit scale changed
    /// (<c>_rescale</c>), whether or not its value did.
    /// </para>
    /// </summary>
    internal sealed partial class Reconciler
    {
        private static readonly Group s_group = new();
        private static readonly Rectangle s_rectangle = new();
        private static readonly Text s_text = new();
        private static readonly Image s_image = new();
        private static readonly Row s_row = new();
        private static readonly Column s_column = new();
        private static readonly Video s_video = new();
        private static readonly Button s_button = new();
        private static readonly SpinBox s_spinBox = new();
        private static readonly CheckBox s_checkBox = new();
        private static readonly Switch s_switch = new();
        private static readonly Slider s_slider = new();
        private static readonly TextField s_textField = new();
        private static readonly ShaderEffect s_shaderEffect = new();
        private static readonly QmlElement s_qml = new();
        private static readonly Shape s_shape = new();

        private static Element DefaultOf(ElementType type) => type switch
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
            ElementType.Qml => s_qml,
            ElementType.Shape => s_shape,
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
                case ElementType.Shape: DiffShape(id, (Shape)prev, (Shape)next); break;
                case ElementType.Qml:
                    DiffAutoSize(id, ((QmlElement)prev).AutoSize, ((QmlElement)next).AutoSize);
                    DiffQml(id, (QmlElement)prev, (QmlElement)next);
                    break;
                default:
                    DiffAutoSize(id, ((Group)prev).AutoSize, ((Group)next).AutoSize);
                    break;
            }
        }

        /// <summary>
        /// QmlElement: Source, the reload counter, then the properties by name: only the entries that changed since the
        /// previous description. A new Source or Reload sends all of them again (QtNative has a new instance, protocol.h "QmlElement");
        /// a key that is gone from the dictionary is not reset in QML.
        /// </summary>
        private void DiffQml(uint id, QmlElement p, QmlElement n)
        {
            bool sourceChanged = p.Source != n.Source;
            if (sourceChanged)
                _sink.SetString(id, P.QmlSource, n.Source);
            bool reloaded = p.Reload != n.Reload;
            if (reloaded)
                _sink.SetI32(id, P.QmlReload, n.Reload);
            QmlPropertyWriter.Write(_sink, id, n.Properties, sourceChanged || reloaded ? null : p.Properties);
        }

        /// <summary>
        /// Common item properties. Size, Anchor and Transform are sent as they are, also for children of Row/Column
        /// (there the layout places them and QtNative uses the anchor only as pivot, protocol.h "Geometry"). Visibility is QtNative's: it hides the
        /// children of a ShaderEffect (drawn through their layers), Opacity 0 = not drawn.
        /// </summary>
        private void DiffCommon(uint id, Element p, Element n)
        {
            if (_rescale || p.Size != n.Size)
                _sink.SetVec2(id, P.Size, _mapping.ToQt(n.Size.X), _mapping.ToQt(n.Size.Y));

            if (p.Opacity != n.Opacity)
                _sink.SetF32(id, P.Opacity, n.Opacity);

            if (p.Clip != n.Clip)
                _sink.SetBool(id, P.Clip, n.Clip);

            if (p.Debug != n.Debug)
                _sink.SetBool(id, P.Debug, n.Debug);

            // Transform and Anchor are sent separately; QtNative resolves the anchor against the item's actual size
            // (follows Auto Size, layout and Size changes without a resend from here). Every TRANSFORM resets a drag
            // offset (pin wins), so it is only sent on change.
            if (_rescale || !Same(p.Transform, n.Transform))
                _sink.SetMat4(id, P.Transform, _mapping.ToQt(n.Transform));
            if (p.Anchor != n.Anchor)
            {
                int pa = Geometry.ToQtAlignment(p.Anchor), na = Geometry.ToQtAlignment(n.Anchor);
                if (pa != na)
                    _sink.SetI32(id, P.Anchor, na);
            }
        }
        private void DiffRectangle(uint id, Rectangle p, Rectangle n)
        {
            if (p.Fill != n.Fill)
                _sink.SetColor(id, P.Color, n.Fill);
            if (_rescale || p.Radius != n.Radius)
                _sink.SetF32(id, P.Radius, _mapping.ToQt(n.Radius));
            if (p.StrokeColor != n.StrokeColor)
                _sink.SetColor(id, P.BorderColor, n.StrokeColor);
            if (_rescale || p.StrokeWidth != n.StrokeWidth)
                _sink.SetF32(id, P.BorderWidth, _mapping.ToQt(n.StrokeWidth));
        }

        // Reused for the converted points and dash pattern of DiffShape, so a changing shape allocates nothing per frame.
        private float[] _shapeBuffer = Array.Empty<float>();

        /// <summary>
        /// Shape. The geometry (points or path) is sent when its reference and its content changed or the unit scale did; every
        /// coordinate goes through the mapping. A point list or path that does not fit one command is cut to the part that does (the node warns).
        /// QtNative takes dash and dash offset in stroke-width units, so both are divided by the stroke width and the dash is sent again
        /// when the stroke width changes. A stroke width of 0 is sent as -1 (no stroke).
        /// </summary>
        private void DiffShape(uint id, Shape p, Shape n)
        {
            if (n.Path is { } path)
            {
                if (_rescale || p.Path is null || (!ReferenceEquals(p.Path, path) && !p.Path.ContentEquals(path)))
                    SendShapePath(id, path);
            }
            else if (_rescale || p.Path is not null
                || (!ReferenceEquals(p.Points, n.Points) && !SameContent(p.Points, n.Points)))
                SendShapePoints(id, n.Points);

            if (p.Closed != n.Closed)
                _sink.SetBool(id, P.ShapeClosed, n.Closed);
            if (p.Fill != n.Fill)
                _sink.SetColor(id, P.ShapeFill, n.Fill);
            if (p.StrokeColor != n.StrokeColor)
                _sink.SetColor(id, P.ShapeStrokeColor, n.StrokeColor);

            bool widthChanged = p.StrokeWidth != n.StrokeWidth;
            if (_rescale || widthChanged)
                _sink.SetF32(id, P.ShapeStrokeWidth, n.StrokeWidth > 0 ? _mapping.ToQt(n.StrokeWidth) : -1);

            if (p.Cap != n.Cap)
                _sink.SetI32(id, P.ShapeCap, (int)n.Cap);
            if (p.Join != n.Join)
                _sink.SetI32(id, P.ShapeJoin, (int)n.Join);
            if (p.MiterLimit != n.MiterLimit)
                _sink.SetF32(id, P.ShapeMiterLimit, n.MiterLimit);

            // Without a stroke the divisor is irrelevant; it is 1 so that the pattern stays finite.
            float unit = n.StrokeWidth > 0 ? n.StrokeWidth : 1;
            if ((widthChanged && (n.Dash.Count > 0 || p.Dash.Count > 0))
                || (!ReferenceEquals(p.Dash, n.Dash) && !SameContent(p.Dash, n.Dash)))
                SendShapeDash(id, n.Dash, unit);
            if (widthChanged || p.DashOffset != n.DashOffset)
                _sink.SetF32(id, P.ShapeDashOffset, n.DashOffset / unit);

            if (p.TrimStart != n.TrimStart)
                _sink.SetF32(id, P.ShapeTrimStart, n.TrimStart);
            if (p.TrimEnd != n.TrimEnd)
                _sink.SetF32(id, P.ShapeTrimEnd, n.TrimEnd);
            if (p.TrimOffset != n.TrimOffset)
                _sink.SetF32(id, P.ShapeTrimOffset, n.TrimOffset);
            if (p.FillRule != n.FillRule)
                _sink.SetI32(id, P.ShapeFillRule, (int)n.FillRule);
        }

        private void SendShapePoints(uint id, Spread<Vector2> points)
        {
            int count = Math.Min(points.Count * 2, Protocol.MaxF32ArrayCount);
            var buffer = ShapeBuffer(count);
            for (int i = 0; i < count; i += 2)
            {
                var v = points[i / 2];
                buffer[i] = _mapping.ToQt(v.X);
                buffer[i + 1] = _mapping.ToQt(v.Y);
            }
            _sink.SetF32Array(id, P.ShapePoints, buffer.AsSpan(0, count));
        }

        private void SendShapePath(uint id, QtPath path)
        {
            var flat = path.Flatten();
            var verbs = flat.Verbs.AsSpan();
            var coordinates = flat.Points.AsSpan();
            int verbCount = FitPath(verbs, coordinates.Length / 2, out int pointCount);
            var buffer = ShapeBuffer(pointCount * 2);
            for (int i = 0; i < pointCount * 2; i++)
                buffer[i] = _mapping.ToQt(coordinates[i]);
            _sink.SetPath(id, P.ShapePath, verbs.Slice(0, verbCount), buffer.AsSpan(0, pointCount * 2));
        }

        /// <summary>Returns the number of verbs of the longest prefix that fits one command and the points that prefix takes.</summary>
        private static int FitPath(ReadOnlySpan<byte> all, int available, out int pointCount)
        {
            pointCount = available;
            if (Protocol.PathFits(all.Length, available))
                return all.Length;
            int points = 0;
            int count = 0;
            for (; count < all.Length; count++)
            {
                int next = points + all[count] switch
                {
                    Protocol.Verb.Quad => 2,
                    Protocol.Verb.Cubic => 3,
                    Protocol.Verb.Close => 0,
                    _ => 1,
                };
                if (!Protocol.PathFits(count + 1, next))
                    break;
                points = next;
            }
            pointCount = points;
            return count;
        }

        private void SendShapeDash(uint id, Spread<float> dash, float unit)
        {
            int count = Math.Min(dash.Count, Protocol.MaxF32ArrayCount);
            var buffer = ShapeBuffer(count);
            for (int i = 0; i < count; i++)
                buffer[i] = dash[i] / unit;
            _sink.SetF32Array(id, P.ShapeDash, buffer.AsSpan(0, count));
        }

        private float[] ShapeBuffer(int length)
        {
            if (_shapeBuffer.Length < length)
                _shapeBuffer = new float[Math.Max(length, _shapeBuffer.Length * 2)];
            return _shapeBuffer;
        }

        // float.Equals instead of ==, so that NaN equals itself and an unchanged list is not sent again.
        private static bool SameContent(Spread<Vector2> a, Spread<Vector2> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!a[i].X.Equals(b[i].X) || !a[i].Y.Equals(b[i].Y))
                    return false;
            }
            return true;
        }

        private static bool SameContent(Spread<float> a, Spread<float> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!a[i].Equals(b[i]))
                    return false;
            }
            return true;
        }

        /// <summary>Font size is always sent on creation: Qt's default font size is platform dependent.</summary>
        private void DiffText(uint id, bool created, Text p, Text n)
        {
            if (!string.Equals(p.Content, n.Content))
                _sink.SetString(id, P.Text, n.Content.AsSpan());
            if (!string.Equals(p.FontFamily, n.FontFamily))
                _sink.SetString(id, P.FontFamily, n.FontFamily.AsSpan());
            if (created || _rescale || _mapping.ToFontPixels(p.FontSize) != _mapping.ToFontPixels(n.FontSize))
                _sink.SetF32(id, P.FontSize, _mapping.ToFontPixels(n.FontSize));
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
            if (_rescale || p.LetterSpacing != n.LetterSpacing)
                _sink.SetF32(id, P.LetterSpacing, _mapping.ToQt(n.LetterSpacing));
            if (p.Autofit != n.Autofit)
                _sink.SetI32(id, P.FontSizeMode, (int)n.Autofit);
        }

        private void DiffButton(uint id, Button p, Button n)
        {
            if (!string.Equals(p.Text, n.Text))
                _sink.SetString(id, P.Text, n.Text.AsSpan());
            if (p.Enabled != n.Enabled)
                _sink.SetBool(id, P.Enabled, n.Enabled);
            if (p.AutoRepeat != n.AutoRepeat)
                _sink.SetBool(id, P.AutoRepeat, n.AutoRepeat);
        }

        /// <summary>
        /// SpinBox: Minimum/Maximum before Value, otherwise Qt clamps the new value with the old bounds. Qt also
        /// clamps on every bound change, possibly against an intermediate range (new Minimum with old Maximum), so Value
        /// is resent whenever a bound changed; an equal SET VALUE is a no-op in Qt and sends no EV_VALUE.
        /// The diff is against the last sent description, also after a user change (EV_VALUE): the node then describes
        /// the user's value, the SET fixes a stale SET that Qt applied after the change.
        /// </summary>
        private void DiffSpinBox(uint id, SpinBox p, SpinBox n)
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

        /// <summary>CheckBox and Switch: the value is only sent after a change; see <see cref="DiffSpinBox"/> for the diff base.</summary>
        private void DiffToggle(uint id, bool pValue, bool nValue, bool pEnabled, bool nEnabled)
        {
            if (pValue != nValue)
                _sink.SetBool(id, P.Value, nValue);
            if (pEnabled != nEnabled)
                _sink.SetBool(id, P.Enabled, nEnabled);
        }

        /// <summary>Slider: Minimum/Maximum before Value and Value again after a bound change, like <see cref="DiffSpinBox"/>.</summary>
        private void DiffSlider(uint id, Slider p, Slider n)
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

        private void DiffTextField(uint id, TextField p, TextField n)
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
        /// Image: the resource id of the QtImage, 0 = none. Sent when the id changes; a QtImage whose content is
        /// replaced in place (ToQtImage) keeps its id, QtNative swaps the texture itself.
        /// </summary>
        private void DiffImage(uint id, Image p, Image n)
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
        private void DiffVideo(uint id, Video p, Video n)
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
            // Before Playing, so Qt's output is off before the first buffer plays; Video [VL.Qt] keeps 0 and never sends it.
            if (p.AudioTap != n.AudioTap)
                _sink.SetU64(id, P.AudioTap, n.AudioTap);
            // Last: QtNative starts playback when Playing arrives (or once the media is loaded).
            if (p.Playing != n.Playing)
                _sink.SetBool(id, P.Playing, n.Playing);
        }

        /// <summary>
        /// Auto Size (containers and Button): QtNative applies the implicit size while on and the last Size while off,
        /// so only the flag is sent here; Size is not resent when it flips.
        /// </summary>
        private void DiffAutoSize(uint id, bool p, bool n)
        {
            if (p != n)
                _sink.SetBool(id, P.AutoSize, n);
        }

        private void DiffLayout(uint id, float pSpacing, float nSpacing, float pPadding, float nPadding, int pAlign, int nAlign)
        {
            if (_rescale || pSpacing != nSpacing)
                _sink.SetF32(id, P.Spacing, _mapping.ToQt(nSpacing));
            if (_rescale || pPadding != nPadding)
                _sink.SetF32(id, P.Padding, _mapping.ToQt(nPadding));
            if (pAlign != nAlign)
                _sink.SetI32(id, P.ChildAlignment, nAlign);
        }

        /// <summary>
        /// ShaderEffect: shader resource ids (0 = none), Blending, MeshResolution (clamped to &gt;= 1, QtNative rejects
        /// smaller values with -7). Source and the uniforms follow in <see cref="ChildrenReconciled"/>.
        /// </summary>
        private void DiffShaderEffect(uint id, ShaderEffect p, ShaderEffect n)
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
        private static bool Same(in Matrix a, in Matrix b) =>
            a.M11 == b.M11 && a.M12 == b.M12 && a.M13 == b.M13 && a.M14 == b.M14 &&
            a.M21 == b.M21 && a.M22 == b.M22 && a.M23 == b.M23 && a.M24 == b.M24 &&
            a.M31 == b.M31 && a.M32 == b.M32 && a.M33 == b.M33 && a.M34 == b.M34 &&
            a.M41 == b.M41 && a.M42 == b.M42 && a.M43 == b.M43 && a.M44 == b.M44;
    }
}
