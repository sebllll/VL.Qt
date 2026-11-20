namespace VL.Qt.Wire
{
    /// <summary>
    /// Mirror of src/VL.Qt.Native/protocol.h (the single source of truth). Keep in sync:
    /// VLQT_&lt;GROUP&gt;_&lt;UPPER_SNAKE&gt; maps to Protocol.&lt;Group&gt;.&lt;PascalCase&gt;, plain defines to Protocol.&lt;PascalCase&gt;.
    /// The SmokeTest drift check (--drift) fails on any mismatch in name or value.
    /// </summary>
    internal static class Protocol
    {
        public const int ProtocolVersion = 14;
        public const int CommandAlign = 4;
        public const int HeaderSize = 4;
        public const uint RootId = 0;
        /// <summary>Max UTF-8 bytes of a uniform name in <see cref="Op.SetUniforms"/>.</summary>
        public const int UniformNameMax = 255;
        /// <summary>u32 id, u16 prop, u16 valueType in front of the value of a SET.</summary>
        internal const int SetFieldsSize = 8;
        /// <summary>Max floats of a <see cref="Vt.F32Array"/> value: the 4-byte count and the floats fit the payload of a SET (8190 points).</summary>
        internal const int MaxF32ArrayCount = (CommandWriter.MaxPayload - SetFieldsSize - 4) / 4;

        /// <summary>Returns true if a <see cref="Vt.Path"/> with this many verbs and points fits the payload of a SET.</summary>
        internal static bool PathFits(int verbs, int points)
        {
            if (verbs < 0 || points < 0)
                return false;
            // u32 verbCount, u32 pointCount, verbs padded to 4 bytes, points as 2 x f32.
            long size = 8 + ((verbs + 3L) & ~3L) + 8L * points;
            return size <= CommandWriter.MaxPayload - SetFieldsSize;
        }

        /// <summary>Opcodes (u16).</summary>
        public static class Op
        {
            public const ushort Create = 1;
            public const ushort Destroy = 2;
            public const ushort Reparent = 3;
            public const ushort Set = 4;
            public const ushort Bind = 5;
            public const ushort ClearColor = 6;
            /// <summary>No payload: destroys every object of the window (resync after a failed apply).</summary>
            public const ushort Reset = 7;
            /// <summary>Resolved widget style: u32 id, u16 count, u16 flags (<see cref="StyleSet"/>), entries.</summary>
            public const ushort SetStyle = 8;
            /// <summary>
            /// Clear pin: u32 clear, 1 = clear every frame (the default), 0 = keep the previous frame and draw the
            /// new one over it. <see cref="Reset"/> leaves it as it is, like <see cref="ClearColor"/>.
            /// </summary>
            public const ushort SetClear = 9;
            /// <summary>
            /// Space pin: u32 <see cref="Space"/> value. QtNative moves only the origin of the window root (top left,
            /// or the centre of the scene for Normalized and DIP); C# scales (<see cref="SpaceMapping"/>). Default
            /// DIPTopLeft; <see cref="Reset"/> leaves it as it is, like <see cref="ClearColor"/>.
            /// </summary>
            public const ushort SetSpace = 10;
            /// <summary>
            /// ShaderEffect uniforms by name: u32 id, u16 count, u16 flags (<see cref="UniformSet"/>), then per
            /// entry (4-byte aligned) u16 value type (<see cref="Vt"/>), u16 name length (1..<see cref="UniformNameMax"/>),
            /// UTF-8 name, padding to 4, value like SET, padding to 4. Diff by name, full send with <see cref="UniformSet.Clear"/>.
            /// Unknown names / wrong types come back as <see cref="Ev.Error"/> text, not as apply error. Details: protocol.h.
            /// Writer API (task 4) like the style: BeginUniforms(id, flags), one Uniform* call per entry, EndUniforms().
            /// </summary>
            public const ushort SetUniforms = 16;
            /// <summary>
            /// Effect chain of an element: u32 id, u16 link count, u16 flags (0), then the links in application order
            /// (first = applied first), each u16 kind (<see cref="EffectLink"/>), u16 entry count, u32 body length, body.
            /// Always the whole chain; 0 links = no chain. Bodies: protocol.h "Effect chain". Writer API:
            /// BeginEffectChain(id), per link BeginMultiLink() + Effect* entries or BeginShaderLink(...) + Uniform* entries,
            /// EndLink(); EndEffectChain().
            /// </summary>
            public const ushort SetEffectChain = 17;
            /// <summary>
            /// Properties of a QmlElement's root by name: u32 id, u16 count, u16 flags (0), then count entries
            /// encoded like <see cref="SetUniforms"/> entries (u16 value type, u16 name length, name, padding, value,
            /// padding). Only on <see cref="Type.Qml"/> elements (else -6); an unknown name or a failed conversion is an
            /// <see cref="Ev.Error"/> line, not a failed apply. See protocol.h "QmlElement".
            /// </summary>
            public const ushort SetQmlProperties = 18;
        }

        /// <summary>Value types of SET (u16).</summary>
        public static class Vt
        {
            public const ushort F32 = 1;
            public const ushort I32 = 2;
            public const ushort Bool = 3;
            public const ushort Color = 4;
            public const ushort Rectf = 5;
            public const ushort Mat4 = 6;
            public const ushort String = 7;
            public const ushort Ref = 8;
            /// <summary>u64, e.g. a resource id (Prop.Image).</summary>
            public const ushort U64 = 9;
            /// <summary>2 x f32 (uniform vec2).</summary>
            public const ushort Vec2 = 16;
            /// <summary>3 x f32 (uniform vec3).</summary>
            public const ushort Vec3 = 17;
            /// <summary>4 x f32 (uniform vec4, not premultiplied, unlike <see cref="Color"/>).</summary>
            public const ushort Vec4 = 18;
            /// <summary>2 x i32 (uniform ivec2, <see cref="Prop.MeshResolution"/>).</summary>
            public const ushort Ivec2 = 19;
            /// <summary>8 bytes: sampler state of a sampler2D (protocol.h "Sampler").</summary>
            public const ushort Sampler = 20;
            /// <summary>u32 count, count x f32 (Shape points and dash pattern).</summary>
            public const ushort F32Array = 10;
            /// <summary>u32 verbCount, u32 pointCount, verbs (u8, zero padded to 4 bytes), points (2 x f32 each, protocol.h "Values").</summary>
            public const ushort Path = 11;
        }

        /// <summary>Path verbs of <see cref="Vt.Path"/> (u8).</summary>
        public static class Verb
        {
            /// <summary>1 point.</summary>
            public const byte Move = 0;
            /// <summary>1 point.</summary>
            public const byte Line = 1;
            /// <summary>2 points: control, end.</summary>
            public const byte Quad = 2;
            /// <summary>3 points: control 1, control 2, end.</summary>
            public const byte Cubic = 3;
            /// <summary>0 points.</summary>
            public const byte Close = 4;
        }

        /// <summary>Element type ids of CREATE (u16). 2 was TestRect (removed in 07a), keep it unused.</summary>
        public static class Type
        {
            public const ushort Item = 1;
            public const ushort Rectangle = 3;
            public const ushort Text = 4;
            public const ushort Image = 5;
            public const ushort Row = 6;
            public const ushort Column = 7;
            // 8 was MultiEffect, 16 RectangularShadow (removed in #459, replaced by the Effect layer); keep unused.
            public const ushort Video = 9;
            public const ushort Button = 10;
            /// <summary>QtQuick.Controls SpinBox (Input (Int)): EV_VALUE i32 on user changes.</summary>
            public const ushort SpinBox = 11;
            /// <summary>QtQuick.Controls CheckBox (Toggle, look Checkbox): EV_VALUE bool on user changes.</summary>
            public const ushort CheckBox = 12;
            /// <summary>QtQuick.Controls Switch (Toggle, look Switch): EV_VALUE bool on user changes.</summary>
            public const ushort Switch = 13;
            /// <summary>QtQuick.Controls Slider: EV_VALUE f32 on user changes.</summary>
            public const ushort Slider = 14;
            /// <summary>QtQuick.Controls TextField, single line: EV_VALUE string on Enter/focus loss if the text changed.</summary>
            public const ushort TextField = 15;
            /// <summary>Own item with .qsb shaders, uniforms by name through <see cref="Op.SetUniforms"/>.</summary>
            public const ushort ShaderEffect = 24;
            /// <summary>Loads a user .qml file; properties by name through <see cref="Op.SetQmlProperties"/>, events <see cref="Ev.QmlProperty"/> and <see cref="Ev.QmlSignal"/>.</summary>
            public const ushort Qml = 25;
            /// <summary>QtQuick.Shapes Shape with one ShapePath: polyline, polygon or path, fill and stroke.</summary>
            public const ushort Shape = 26;
            /// <summary>
            /// Input handlers from 32: protocol objects attached to an element by REPARENT (index ignored).
            /// </summary>
            public const ushort HoverHandler = 32;
            /// <summary>Qt TapHandler (ReleaseWithinBounds): <see cref="Ev.Press"/>, <see cref="Ev.Click"/>, <see cref="Ev.TapCount"/>, <see cref="Ev.LongPress"/>.</summary>
            public const ushort TapHandler = 33;
            /// <summary>Qt PointHandler: <see cref="Ev.Active"/>, <see cref="Ev.Pointer"/> while active.</summary>
            public const ushort PointHandler = 34;
            /// <summary>Qt WheelHandler (target null): <see cref="Ev.Wheel"/>.</summary>
            public const ushort WheelHandler = 35;
            /// <summary>Own QtNative handler (passive grabs): <see cref="Ev.TouchPoints"/>, all points pressed in the item.</summary>
            public const ushort TouchPointsHandler = 36;
            /// <summary>Qt PinchHandler (target null): <see cref="Ev.Active"/>, <see cref="Ev.Pinch"/>; moves nothing.</summary>
            public const ushort PinchHandler = 37;
            /// <summary>Qt DragHandler (target null): <see cref="Ev.Active"/>, <see cref="Ev.Drag"/>; moves the item by its drag offset.</summary>
            public const ushort DragHandler = 38;
        }

        /// <summary>
        /// Property ids of SET (u16). 1..255 common to all items; type-specific ids start at 256, looked up per type
        /// (Color: Rectangle fill + Text; Source: Video string, ShaderEffect ref). Blocks: 256 Rectangle, 512 Text,
        /// 544 Image, 576 Row/Column, 608 reserved (MultiEffect, removed in #459), 640 AutoSize, 672 Video (shares Source/FillMode with Image), 704 Button,
        /// 768 input handlers (only on handler objects), 800 reserved (RectangularShadow, removed in #459),
        /// 832 keys of the Effect chain's MULTI entries (not via SET, see protocol.h "Effect chain"), 896 ShaderEffect (also takes Source ref and AutoSize), 928 QmlElement, 960 Shape.
        /// </summary>
        public static class Prop
        {
            /// <summary>vec2 (w, h): item size; position comes from <see cref="Anchor"/> + <see cref="Transform"/> (see protocol.h "Geometry").</summary>
            public const ushort Size = 1;
            public const ushort Transform = 2;
            public const ushort Opacity = 3;
            // 4 free: was Visible, removed in #474 (Opacity 0 = not drawn, input stays).
            public const ushort Clip = 5;
            /// <summary>i32 Qt::Alignment: point of the item at its parent's origin, pivot of <see cref="Transform"/> (in Row/Column only the pivot).</summary>
            public const ushort Anchor = 6;
            /// <summary>bool: draw the element in the window's debug overlay (not recursive).</summary>
            public const ushort Debug = 7;
            public const ushort Color = 256;
            public const ushort Radius = 257;
            public const ushort BorderColor = 258;
            public const ushort BorderWidth = 259;
            public const ushort Text = 512;
            public const ushort FontFamily = 513;
            public const ushort FontSize = 514;
            public const ushort FontWeight = 515;
            public const ushort FontItalic = 516;
            public const ushort HorizontalAlignment = 517;
            public const ushort VerticalAlignment = 518;
            public const ushort WrapMode = 519;
            public const ushort Elide = 520;
            public const ushort LineHeight = 521;
            public const ushort LetterSpacing = 522;
            public const ushort FontSizeMode = 523;
            public const ushort Source = 544;
            public const ushort FillMode = 545;
            public const ushort Smooth = 546;
            /// <summary>u64 image resource id, 0 = no image; Image has no Source since #361 (Video keeps it).</summary>
            public const ushort Image = 547;
            public const ushort Spacing = 576;
            public const ushort Padding = 577;
            public const ushort ChildAlignment = 578;
            public const ushort AutoSize = 640;
            public const ushort Playing = 672;
            public const ushort Loop = 673;
            public const ushort PlaybackRate = 674;
            public const ushort Volume = 675;
            public const ushort KeepLastFrame = 676;
            /// <summary>u64 audio tap resource id (<see cref="ResKind.AudioTap"/>), 0 = none: Qt's own output plays (protocol.h "Audio tap").</summary>
            public const ushort AudioTap = 677;
            public const ushort Enabled = 704;
            public const ushort AutoRepeat = 705;
            /// <summary>Value of a value input in the element's value type (SpinBox i32, CheckBox/Switch bool, Slider f32, TextField string); never echoed as <see cref="Ev.Value"/>.</summary>
            public const ushort Value = 706;
            /// <summary>SpinBox i32 lower bound (QtNative default int.MinValue), Slider f32 (default 0).</summary>
            public const ushort Minimum = 707;
            /// <summary>SpinBox i32 upper bound (QtNative default int.MaxValue), Slider f32 (default 1).</summary>
            public const ushort Maximum = 708;
            /// <summary>SpinBox i32 step of +/-, arrow keys and wheel (default 1), Slider f32 (default 0 = continuous).</summary>
            public const ushort Step = 709;
            /// <summary>bool, SpinBox: the value can be typed; TextField: not read-only (default true).</summary>
            public const ushort Editable = 710;
            /// <summary>i32 Qt::Orientation, Slider: 1 horizontal (default), 2 vertical.</summary>
            public const ushort Orientation = 711;
            /// <summary>string, TextField placeholder text shown while empty (default empty).</summary>
            public const ushort Placeholder = 712;
            // 768 (ReportPosition, Hover handler position) is reserved: positions come from the Point handler only.
            /// <summary>i32 Qt::MouseButtons, Tap + Point + Drag handler (default left button). Point with 0: the mouse is active while hovering.</summary>
            public const ushort AcceptedButtons = 769;
            /// <summary>i32 QInputDevice::DeviceTypes, Tap + Point + Wheel + Drag + Touch Points + Pinch handler (default: Qt's = all).</summary>
            public const ushort AcceptedDevices = 770;
            /// <summary>bool, Drag handler: the drag moves the item by its drag offset (default true; false: report only).</summary>
            public const ushort MovesElement = 771;
            /// <summary>i32, Drag handler: 0 both axes (default), 1 x only, 2 y only.</summary>
            public const ushort DragAxis = 772;
            /// <summary>rectf, Drag handler: minX, minY, maxX, maxY of the drag offset in logical pixels, ±infinity = no limit.</summary>
            public const ushort DragLimits = 773;
            /// <summary>i32 &gt;= 2, Pinch handler (default 2).</summary>
            public const ushort MinimumPointCount = 774;
            /// <summary>i32 &gt;= 2, Pinch handler (default 2); with more points the gesture pauses.</summary>
            public const ushort MaximumPointCount = 775;
            /// <summary>bool, Touch Points handler: ignore presses that an element on top grabs (default true).</summary>
            public const ushort Occlusion = 776;
            // 832.. keys of MULTI entries of SET_EFFECT_CHAIN, values of one MultiEffect run, Qt defaults. Not settable
            // by SET (-6) once plan effects/08 task 2 lands; until then the old single layer still takes them.
            /// <summary>Reserved (was: layer on/off of the single Effect layer, plan effects/01).</summary>
            public const ushort EffectEnabled = 832;
            /// <summary>bool (default false).</summary>
            public const ushort EffectBlurEnabled = 833;
            /// <summary>f32 0..1.</summary>
            public const ushort EffectBlur = 834;
            /// <summary>i32 pixels (default 32); switches shaders.</summary>
            public const ushort EffectBlurMax = 835;
            /// <summary>f32 (default 0).</summary>
            public const ushort EffectBlurMultiplier = 836;
            /// <summary>bool (default false).</summary>
            public const ushort EffectShadowEnabled = 837;
            /// <summary>color (default black).</summary>
            public const ushort EffectShadowColor = 838;
            /// <summary>f32 logical pixels.</summary>
            public const ushort EffectShadowHorizontalOffset = 839;
            /// <summary>f32 logical pixels.</summary>
            public const ushort EffectShadowVerticalOffset = 840;
            /// <summary>f32 0..1 (default 1).</summary>
            public const ushort EffectShadowBlur = 841;
            /// <summary>f32 (default 1).</summary>
            public const ushort EffectShadowScale = 842;
            /// <summary>f32 -1..1.</summary>
            public const ushort EffectBrightness = 843;
            /// <summary>f32 -1..1.</summary>
            public const ushort EffectContrast = 844;
            /// <summary>f32 -1..1.</summary>
            public const ushort EffectSaturation = 845;
            /// <summary>f32 0..1.</summary>
            public const ushort EffectColorization = 846;
            /// <summary>color (default red).</summary>
            public const ushort EffectColorizationColor = 847;
            /// <summary>bool autoPaddingEnabled (default true).</summary>
            public const ushort EffectAutoPadding = 848;
            /// <summary>rectf paddingRect in logical pixels (x left, y top, w right, h bottom), with EffectAutoPadding off.</summary>
            public const ushort EffectPaddingRect = 849;
            /// <summary>ref: mask element object (not reparented by the host), 0 = no mask; see protocol.h.</summary>
            public const ushort EffectMaskSource = 850;
            /// <summary>f32 0..1 (default 0).</summary>
            public const ushort EffectMaskThresholdMin = 851;
            /// <summary>f32 0..1 (default 0).</summary>
            public const ushort EffectMaskSpreadAtMin = 852;
            /// <summary>f32 0..1 (default 1).</summary>
            public const ushort EffectMaskThresholdMax = 853;
            /// <summary>f32 0..1 (default 0).</summary>
            public const ushort EffectMaskSpreadAtMax = 854;
            /// <summary>bool (default false).</summary>
            public const ushort EffectMaskInverted = 855;
            /// <summary>u64 shader resource id (<see cref="ResKind.Shader"/>, stage fragment), 0 = none: the ShaderEffect draws nothing.</summary>
            public const ushort FragmentShader = 896;
            /// <summary>u64 shader resource id (stage vertex), 0 = Qt's default vertex shader.</summary>
            public const ushort VertexShader = 897;
            /// <summary>bool, ShaderEffect: premultiplied alpha blending (default true).</summary>
            public const ushort Blending = 898;
            /// <summary>ivec2, ShaderEffect: grid subdivisions x, y &gt;= 1 (default 1, 1).</summary>
            public const ushort MeshResolution = 899;
            /// <summary>string, QmlElement: absolute path of the .qml file, empty = nothing loaded.</summary>
            public const ushort QmlSource = 928;
            /// <summary>i32, QmlElement: counter, a new value reloads the same file (cache cleared, properties re-applied).</summary>
            public const ushort QmlReload = 929;
            /// <summary>f32 array, x,y pairs, logical pixels (Shape).</summary>
            public const ushort ShapePoints = 960;
            /// <summary>bool, close the point list (Shape).</summary>
            public const ushort ShapeClosed = 961;
            /// <summary>path, verbs + points, logical pixels; replaces <see cref="ShapePoints"/> (Shape).</summary>
            public const ushort ShapePath = 962;
            /// <summary>color, fill color (Shape).</summary>
            public const ushort ShapeFill = 963;
            /// <summary>color, stroke color (Shape).</summary>
            public const ushort ShapeStrokeColor = 964;
            /// <summary>f32, &lt; 0 = no stroke (Shape).</summary>
            public const ushort ShapeStrokeWidth = 965;
            /// <summary>i32, 0 Flat, 1 Square, 2 Round (Shape).</summary>
            public const ushort ShapeCap = 966;
            /// <summary>i32, 0 Miter, 1 Bevel, 2 Round (Shape).</summary>
            public const ushort ShapeJoin = 967;
            /// <summary>f32, miter limit (Shape).</summary>
            public const ushort ShapeMiterLimit = 968;
            /// <summary>f32 array, empty = solid; stroke-width units (Shape).</summary>
            public const ushort ShapeDash = 969;
            /// <summary>f32, stroke-width units (Shape).</summary>
            public const ushort ShapeDashOffset = 970;
            /// <summary>f32, 0..1 (Shape).</summary>
            public const ushort ShapeTrimStart = 971;
            /// <summary>f32, 0..1 (Shape).</summary>
            public const ushort ShapeTrimEnd = 972;
            /// <summary>f32, trim offset (Shape).</summary>
            public const ushort ShapeTrimOffset = 973;
            /// <summary>i32, 0 NonZero, 1 EvenOdd (Shape).</summary>
            public const ushort ShapeFillRule = 974;
        }

        /// <summary>
        /// BIND flags (u16, bit mask). 1 (Interactive) and 4 (Pointer) of #208 are reserved, as is 2
        /// (CancelPress): no effect in QtNative any more (a new feedback of a Tap handler cancels its press itself), the
        /// reconciler does not send it. Keep unused.
        /// </summary>
        public static class Bind
        {
            /// <summary>Reserved: no effect, not sent. Kept for the writer tests and the drift check.</summary>
            public const ushort CancelPress = 2;
        }

        /// <summary>Link kinds of <see cref="Op.SetEffectChain"/> (u16).</summary>
        public static class EffectLink
        {
            /// <summary>One MultiEffect run: entries u16 prop (EffectBlurEnabled..EffectMaskInverted), u16 value type, value.</summary>
            public const ushort Multi = 1;
            /// <summary>Filter shader link: template, shader ids, padding, strength (Pyramid radius), uniform entries like SET_UNIFORMS.</summary>
            public const ushort Shader = 2;
        }

        /// <summary>Pass templates of a shader link (u16); passes: docs/effects-and-shaders.md.</summary>
        public static class EffectTemplate
        {
            /// <summary>One pass of the shader over the input; shader ids [fragment].</summary>
            public const ushort Single = 1;
            /// <summary>The shader twice, float uniform passIndex 0 then 1 (if declared; motion blurs); [fragment].</summary>
            public const ushort TwoPass = 2;
            /// <summary>Prefilter, blur pyramid, composite (Glow, InnerShadow), strength = radius in DIP; [prefilter, composite].</summary>
            public const ushort Pyramid = 3;
        }

        /// <summary>SET_UNIFORMS flags (u16, bit mask).</summary>
        public static class UniformSet
        {
            /// <summary>Empty the element's uniform table before applying the entries (full send).</summary>
            public const ushort Clear = 1;
        }

        /// <summary>SET_STYLE flags (u16, bit mask).</summary>
        public static class StyleSet
        {
            /// <summary>Empty the element's style table before applying the entries (full send).</summary>
            public const ushort Clear = 1;
        }

        /// <summary>Style states (u8 of a SET_STYLE entry). Rank when several apply: Disabled &gt; Pressed &gt; Hovered &gt; Checked &gt; Normal.</summary>
        public static class StyleState
        {
            public const byte Normal = 0;
            public const byte Hovered = 1;
            public const byte Pressed = 2;
            public const byte Disabled = 3;
            public const byte Checked = 4;
        }

        /// <summary>Style fields (u16 of a SET_STYLE entry). Value type per field: see protocol.h.</summary>
        public static class StyleField
        {
            public const ushort FrameColor = 1;
            public const ushort BorderColor = 2;
            public const ushort TextColor = 3;
            public const ushort IndicatorColor = 4;
            public const ushort TrackColor = 5;
            public const ushort TrackFillColor = 6;
            public const ushort FrameRadius = 32;
            public const ushort FrameBorderWidth = 33;
            public const ushort FramePaddingX = 34;
            public const ushort FramePaddingY = 35;
            public const ushort FrameMinWidth = 36;
            public const ushort FrameMinHeight = 37;
            public const ushort TextFontFamily = 64;
            public const ushort TextFontSize = 65;
            public const ushort TextFontWeight = 66;
            public const ushort IndicatorSize = 96;
            public const ushort IndicatorRadius = 97;
            public const ushort IndicatorBorderWidth = 98;
            public const ushort TrackThickness = 128;
            public const ushort TrackRadius = 129;
        }

        /// <summary>Event ids of the poll buffer (u16). Payloads: events.h (plan 06).</summary>
        public static class Ev
        {
            public const ushort Measured = 1;
            public const ushort SceneBounds = 2;
            public const ushort Hover = 3;
            public const ushort Press = 4;
            public const ushort Click = 5;
            public const ushort Pointer = 6;
            public const ushort Media = 7;
            /// <summary>Handle 0 (the window itself): f32 x, f32 y, f32 w, f32 h, u32 flags (<see cref="Window"/>).</summary>
            public const ushort WindowGeometry = 8;
            /// <summary>UTF-8 error text of the element (no terminator), empty = cleared.</summary>
            public const ushort Error = 9;
            /// <summary>u32 tapCount of the last tap, on change (2 = double click), Tap handler.</summary>
            public const ushort TapCount = 10;
            /// <summary>u32 long presses since the last poll, Tap handler.</summary>
            public const ushort LongPress = 11;
            /// <summary>u8 0/1 on change, Point handler.</summary>
            public const ushort Active = 12;
            /// <summary>2 x f32 notches (angleDelta / 120) since the last poll, once (0, 0) after motion, Wheel handler.</summary>
            public const ushort Wheel = 13;
            /// <summary>
            /// 2 x f32 translation of the running drag since the press (parent space, logical pixels, after axis and
            /// limits), on change; keeps the last value after the release until the next drag. Drag handler.
            /// </summary>
            public const ushort Drag = 14;
            /// <summary>
            /// u32 count, then per point u32 id, f32 x, f32 y (item-local, logical pixels), f32 pressure, f32 w, f32 h
            /// (ellipse diameters), sorted by id, on change; count 0 once when the last point goes; at most 64 points.
            /// Touch Points handler.
            /// </summary>
            public const ushort TouchPoints = 15;
            /// <summary>
            /// 6 x f32 scale, rotation (degrees, clockwise), translation x, y, pivot x, y (parent space) of the running gesture, on
            /// change; the last one comes before <see cref="Active"/> 0. Pinch handler.
            /// </summary>
            public const ushort Pinch = 16;
            /// <summary>User change of a value input: u16 value type (<see cref="Vt"/>), u16 0, value encoded like SET.</summary>
            public const ushort Value = 17;
            /// <summary>A root property that QML changed (no echo of SET_QML_PROPERTIES): u16 name length, u16 value type, name, padding to 4, value like <see cref="Value"/>.</summary>
            public const ushort QmlProperty = 18;
            /// <summary>A root signal: u16 name length, u16 argument count, name, padding to 4, count x (u16 value type, u16 0, value, padding).</summary>
            public const ushort QmlSignal = 19;
        }

        /// <summary>Flags of EV_WINDOW_GEOMETRY (u32 bit mask).</summary>
        public static class Window
        {
            public const uint Minimized = 1;
            public const uint Maximized = 2;
            public const uint Fullscreen = 4;
            public const uint Visible = 8;
        }

        /// <summary>Window modes: bit mask of vlqt_window_set_mode (Borderless, Fullscreen).</summary>
        public static class WindowMode
        {
            public const uint Borderless = 1;
            public const uint Fullscreen = 2;
        }

        /// <summary>Spaces of <see cref="Op.SetSpace"/>, the order of <see cref="VL.Qt.Space"/>.</summary>
        public static class Space
        {
            public const uint Normalized = 0;
            public const uint Dip = 1;
            public const uint DipTopLeft = 2;
            public const uint PixelTopLeft = 3;
        }

        /// <summary>Modes of the TEST export vlqt_test_device_lost, see protocol.h.</summary>
        public static class DeviceLost
        {
            /// <summary>Real removal of the shared device; needs VLQT_TEST_D3D11ON12=1 before the device is created.</summary>
            public const int Remove = 1;
            /// <summary>Real loss through a GPU hang (TDR, whole GPU); manual checks only.</summary>
            public const int Hang = 2;
        }

        /// <summary>Return codes of the offscreen exports vlqt_offscreen_* (QtTexture), see protocol.h.</summary>
        public static class Offscreen
        {
            public const int Ok = 0;
            /// <summary>Null handle, or a window handle that is no offscreen scene.</summary>
            public const int NullScene = -1;
            /// <summary>Null device, handle or context, or a size out of range or not the texture's.</summary>
            public const int BadArgument = -7;
            /// <summary>The scene has no texture.</summary>
            public const int Unbound = -20;
            /// <summary>The foreign device is on another adapter than QtNative's device.</summary>
            public const int AdapterMismatch = -21;
            /// <summary>OpenSharedResource1 failed or the handle is no usable Texture2D.</summary>
            public const int OpenFailed = -22;
            /// <summary>The render target view format is not allowed or does not fit the texture.</summary>
            public const int FormatUnsupported = -23;
            /// <summary>No ID3D11Device5/ID3D11DeviceContext4, or a fence could not be created, shared or opened.</summary>
            public const int FenceFailed = -24;
            /// <summary>QtNative's device is lost or being rebuilt; try again next frame.</summary>
            public const int DeviceLost = -25;
            /// <summary>No D3D11 (Linux, other graphics API) or not available on an offscreen scene.</summary>
            public const int NotSupported = -26;
        }

        /// <summary>Mouse event types of the TEST export vlqt_test_send_mouse, not part of the command buffer.</summary>
        public static class Mouse
        {
            public const int Move = 1;
            public const int Press = 2;
            public const int Release = 3;
            public const int Leave = 4;
            public const int PressRight = 5;
            public const int ReleaseRight = 6;
            /// <summary>Right button synthesized by the OS from touch (Windows press-and-hold).</summary>
            public const int PressRightSystem = 7;
            public const int ReleaseRightSystem = 8;
        }

        /// <summary>Touch point events of the TEST export vlqt_test_send_touch, not part of the command buffer.</summary>
        public static class Touch
        {
            public const int Press = 1;
            public const int Move = 2;
            public const int Release = 3;
        }

        /// <summary>Pointer events of vlqt_offscreen_send_pointer (QtTexture input), not part of the command buffer.</summary>
        public static class Pointer
        {
            public const int Press = 1;
            public const int Move = 2;
            public const int Release = 3;
            /// <summary>The pointer left the texture or its window (hover ends); x/y ignored.</summary>
            public const int Leave = 4;
            /// <summary>Touch/pen: the system cancelled the point (TouchCancel).</summary>
            public const int Cancel = 5;
        }

        /// <summary>Pointing devices of vlqt_offscreen_send_pointer. Pen is delivered as a stylus, as in a window on Windows.</summary>
        public static class PointerDevice
        {
            public const int Mouse = 0;
            public const int Touch = 1;
            public const int Pen = 2;
        }

        /// <summary>Key event types of vlqt_offscreen_send_key.</summary>
        public static class KeyEvent
        {
            public const int Press = 1;
            public const int Release = 2;
        }

        /// <summary>Resource kinds of vlqt_res_create.</summary>
        public static class ResKind
        {
            /// <summary>Test only: decode sleeps in a worker, upload uses budget (vlqt_test_res_start).</summary>
            public const int Test = 1;
            /// <summary>Image from a file or pixels (vlqt_res_load_image / vlqt_res_set_pixels), texture on the process device.</summary>
            public const int Image = 2;
            /// <summary>CPU copy of an element (vlqt_res_snapshot / vlqt_res_read_pixels), no texture.</summary>
            public const int Snapshot = 3;
            /// <summary>.qsb shader of one stage (vlqt_res_load_shader), for ShaderEffect.</summary>
            public const int Shader = 4;
            /// <summary>Sound of a Video as float samples (vlqt_res_audio_tap_init / vlqt_res_read_audio).</summary>
            public const int AudioTap = 5;
        }

        /// <summary>Audio tap (protocol.h "Audio tap"): flags of <see cref="Native.vlqt_audio_tap_state.flags"/> and the limits of vlqt_res_audio_tap_init.</summary>
        public static class AudioTap
        {
            /// <summary>A Video element (player) writes into the tap.</summary>
            public const int Attached = 1;
            /// <summary>That player is playing (buffers are expected).</summary>
            public const int Playing = 2;
            public const int MaxChannels = 8;
            public const int MinSampleRate = 8000;
            public const int MaxSampleRate = 384000;
        }

        /// <summary>Shader stages of vlqt_res_load_shader.</summary>
        public static class ShaderStage
        {
            public const int Vertex = 1;
            public const int Fragment = 2;
        }

        /// <summary>Flags of vlqt_res_load_image.</summary>
        public static class Img
        {
            /// <summary>Generate mipmaps after the upload (+33 % VRAM).</summary>
            public const uint Mipmaps = 1;
            /// <summary>The worker also keeps a preview of at most 256 px for editor tooltips (vlqt_res_read_preview).</summary>
            public const uint Preview = 2;
        }

        /// <summary>Resource states (u8 of ResEv.State, return value of vlqt_res_wait).</summary>
        public static class ResState
        {
            public const byte Pending = 1;
            public const byte Decoding = 2;
            public const byte Uploading = 3;
            public const byte Ready = 4;
            public const byte Failed = 5;
            /// <summary>Never sent: the host released the id before.</summary>
            public const byte Released = 6;
        }

        /// <summary>Event ids of the resource status queue (u16), vlqt_res_poll: u16 event, u16 len, u64 id, payload.</summary>
        public static class ResEv
        {
            /// <summary>u8 state (<see cref="ResState"/>).</summary>
            public const ushort State = 1;
            /// <summary>UTF-8 error text, before STATE Failed.</summary>
            public const ushort Error = 2;
            /// <summary>u32 width, u32 height, u32 format, u64 uploaded bytes, before STATE Ready.</summary>
            public const ushort Info = 3;
            /// <summary>UTF-8 warning text, at most one per resource (e.g. 16 bit / float reduced to 8 bit).</summary>
            public const ushort Warning = 4;
        }
    }
}
