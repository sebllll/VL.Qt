using System.Text;
using VL.Qt.Interop;

namespace VL.Qt.Wire
{
    /// <summary>
    /// Mirror of src/VL.Qt.Native/protocol.h (the wire format, the single source of truth); what the values mean: docs/contributing/protocol.md.
    /// VLQT_&lt;GROUP&gt;_&lt;UPPER_SNAKE&gt; maps to Protocol.&lt;Group&gt;.&lt;PascalCase&gt;, plain defines to Protocol.&lt;PascalCase&gt;.
    /// The unit test ProtocolDriftTests fails on any mismatch in name or value.
    /// </summary>
    internal static class Protocol
    {
        /// <summary>Version of the command protocol and the C ABI of vlqt.h; QtApp refuses a vlqt.dll with another one.</summary>
        public const int AbiVersion = 26;
        public const int CommandAlign = 4;
        /// <summary>Header of a command, a poll record and a resource record: u16 op/event, u16 flags (0, reserved), u32 length.</summary>
        public const int HeaderSize = 8;
        /// <summary>Cap of the payload length of one command (64 MB).</summary>
        public const int MaxCommandPayload = 67108864;
        public const uint RootId = 0;
        /// <summary>Max UTF-8 bytes of a uniform name in <see cref="Op.SetUniforms"/>.</summary>
        public const int UniformNameMax = 255;

        /// <summary>Whether <paramref name="name"/> can be sent as a uniform or QML property name (1..<see cref="UniformNameMax"/> UTF-8 bytes).</summary>
        public static bool IsValidName(string? name)
            => !string.IsNullOrEmpty(name) && Encoding.UTF8.GetByteCount(name) <= UniformNameMax;
        /// <summary>CREATE: u32 id, u16 type (the whole payload).</summary>
        public const int CreatePayload = 6;
        /// <summary>REPARENT: u32 id, u32 parent, u32 index (the whole payload).</summary>
        public const int ReparentPayload = 12;
        /// <summary>BIND: u32 id, u32 feedbackId (the whole payload).</summary>
        public const int BindPayload = 8;
        /// <summary>u32 id, u16 prop, u16 valueType in front of the value of a SET.</summary>
        public const int SetFieldsSize = 8;
        /// <summary>u32 id, u16 count, u16 flags in front of the entries of SET_STYLE, SET_UNIFORMS, SET_QML_PROPERTIES and SET_EFFECT_CHAIN.</summary>
        public const int ListFieldsSize = 8;
        /// <summary>Entry header of SET_STYLE (u16 field, u8 state, u8 valueType), SET_UNIFORMS and SET_QML_PROPERTIES (u16 valueType, u16 nameLen).</summary>
        public const int EntryHeaderSize = 4;
        /// <summary>Header of an effect chain link: u16 kind, u16 count, u32 bodyLen.</summary>
        public const int EffectChainLinkHeaderSize = 8;
        /// <summary>Part of a SHADER link body after u16 template, u16 shaderCount and the shader ids: 4 x f32 padding, f32 strength.</summary>
        public const int EffectChainLinkShaderFixedSize = 20;
        /// <summary>u32 feedbackId after the header of a poll record, in front of the payload.</summary>
        public const int EventFieldsSize = 4;

        /// <summary>Opcodes (u16).</summary>
        internal static class Op
        {
            public const ushort Create = 1;
            public const ushort Destroy = 2;
            public const ushort Reparent = 3;
            public const ushort Set = 4;
            public const ushort Bind = 5;
            public const ushort SetClearColor = 6;
            /// <summary>No payload: destroys every object of the window.</summary>
            public const ushort Reset = 7;
            /// <summary>Resolved widget style: u32 id, u16 count, u16 flags (<see cref="StyleSet"/>), entries.</summary>
            public const ushort SetStyle = 8;
            /// <summary>u32 clear: 1 = clear every frame (the default), 0 = keep the previous frame.</summary>
            public const ushort SetClear = 9;
            /// <summary>u32 <see cref="Space"/> value; C# scales (<see cref="SpaceMapping"/>), QtNative moves the origin.</summary>
            public const ushort SetSpace = 10;
            /// <summary>ShaderEffect uniforms by name: u32 id, u16 count, u16 <see cref="UniformSet"/> flags, entries.</summary>
            public const ushort SetUniforms = 16;
            /// <summary>Effect chain of an element: u32 id, u16 link count, u16 flags (0), then the links (<see cref="EffectChainLink"/>).</summary>
            public const ushort SetEffectChain = 17;
            /// <summary>Properties of a <see cref="ElementType.Qml"/> element's root by name: entries like <see cref="SetUniforms"/> entries,
            /// and <see cref="ValueType.String"/> is allowed.</summary>
            public const ushort SetQmlProperties = 18;
        }

        /// <summary>Value types of SET (u16).</summary>
        internal static class ValueType
        {
            public const ushort F32 = 1;
            public const ushort I32 = 2;
            public const ushort Bool = 3;
            public const ushort Color = 4;
            public const ushort Rectf = 5;
            public const ushort Mat4 = 6;
            public const ushort String = 7;
            public const ushort Ref = 8;
            /// <summary>u64, e.g. a resource id (PropertyId.Image).</summary>
            public const ushort U64 = 9;
            /// <summary>2 x f32 (uniform vec2).</summary>
            public const ushort Vec2 = 16;
            /// <summary>3 x f32 (uniform vec3).</summary>
            public const ushort Vec3 = 17;
            /// <summary>4 x f32 (uniform vec4, not premultiplied, unlike <see cref="Color"/>).</summary>
            public const ushort Vec4 = 18;
            /// <summary>2 x i32 (uniform ivec2, <see cref="PropertyId.MeshResolution"/>).</summary>
            public const ushort Ivec2 = 19;
            /// <summary>8 bytes: sampler state of a sampler2D.</summary>
            public const ushort Sampler = 20;
            /// <summary>u32 count, count x f32 (Shape points and dash pattern).</summary>
            public const ushort F32Array = 10;
            /// <summary>u32 verbCount, u32 pointCount, verbs (u8, zero padded to 4 bytes), points (2 x f32 each).</summary>
            public const ushort Path = 11;
        }

        /// <summary>Path verbs of <see cref="ValueType.Path"/> (u8).</summary>
        internal static class Verb
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

        /// <summary>Element type ids of CREATE (u16). 2, 8 and 16 are removed, keep them unused.</summary>
        internal static class ElementType
        {
            public const ushort Group = 1;
            public const ushort Rectangle = 3;
            public const ushort Text = 4;
            public const ushort Image = 5;
            public const ushort Row = 6;
            public const ushort Column = 7;
            public const ushort Video = 9;
            public const ushort Button = 10;
            /// <summary>QtQuick.Controls SpinBox (Input (Int)): EVENT_VALUE i32 on user changes.</summary>
            public const ushort SpinBox = 11;
            /// <summary>QtQuick.Controls CheckBox (Toggle, look Checkbox): EVENT_VALUE bool on user changes.</summary>
            public const ushort CheckBox = 12;
            /// <summary>QtQuick.Controls Switch (Toggle, look Switch): EVENT_VALUE bool on user changes.</summary>
            public const ushort Switch = 13;
            /// <summary>QtQuick.Controls Slider: EVENT_VALUE f32 on user changes.</summary>
            public const ushort Slider = 14;
            /// <summary>QtQuick.Controls TextField, single line: EVENT_VALUE string on Enter/focus loss if the text changed.</summary>
            public const ushort TextField = 15;
            /// <summary>Own item with .qsb shaders, uniforms by name through <see cref="Op.SetUniforms"/>.</summary>
            public const ushort Shader = 24;
            /// <summary>Loads a user .qml file; properties by name through <see cref="Op.SetQmlProperties"/>, events
            /// <see cref="EventId.QmlProperty"/> and <see cref="EventId.QmlSignal"/>.</summary>
            public const ushort Qml = 25;
            /// <summary>QtQuick.Shapes Shape with one ShapePath: polyline, polygon or path, fill and stroke.</summary>
            public const ushort Shape = 26;
            /// <summary>
            /// Input handlers from 32: protocol objects attached to an element by REPARENT (index ignored).
            /// </summary>
            public const ushort HoverHandler = 32;
            /// <summary>Qt TapHandler (ReleaseWithinBounds): <see cref="EventId.Press"/>, <see cref="EventId.Click"/>,
            /// <see cref="EventId.TapCount"/>, <see cref="EventId.LongPress"/>.</summary>
            public const ushort TapHandler = 33;
            /// <summary>Qt PointHandler: <see cref="EventId.Active"/>, <see cref="EventId.Pointer"/> while active.</summary>
            public const ushort PointHandler = 34;
            /// <summary>Qt WheelHandler (target null): <see cref="EventId.Wheel"/>.</summary>
            public const ushort WheelHandler = 35;
            /// <summary>Own QtNative handler (passive grabs): <see cref="EventId.TouchPoints"/>, all points pressed in the item.</summary>
            public const ushort TouchPointsHandler = 36;
            /// <summary>Qt PinchHandler (target null): <see cref="EventId.Active"/>, <see cref="EventId.Pinch"/>; moves nothing.</summary>
            public const ushort PinchHandler = 37;
            /// <summary>Qt DragHandler (target null): <see cref="EventId.Active"/>, <see cref="EventId.Drag"/>; moves the item by its drag offset.</summary>
            public const ushort DragHandler = 38;
        }

        /// <summary>
        /// Property ids of SET (u16): 1..255 common to all items, the blocks from 256 per element type (block table:
        /// docs/contributing/protocol.md). 832 is <see cref="MultiEffectKey"/>, not settable by SET.
        /// </summary>
        internal static class PropertyId
        {
            /// <summary>vec2 (w, h), logical pixels; position comes from <see cref="Anchor"/> + <see cref="Transform"/>.</summary>
            public const ushort Size = 1;
            public const ushort Transform = 2;
            public const ushort Opacity = 3;
            /// <summary>bool, the Enabled pin (default true): false takes the item out of the scene.</summary>
            public const ushort Visible = 4;
            public const ushort Clip = 5;
            /// <summary>i32 Qt::Alignment: point of the item at its parent's origin, pivot of <see cref="Transform"/>.</summary>
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
            /// <summary>u64 image resource id, 0 = no image.</summary>
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
            /// <summary>u64 audio tap resource id (<see cref="ResourceKind.AudioTap"/>), 0 = none: Qt's own output plays.</summary>
            public const ushort AudioTap = 677;
            public const ushort Enabled = 704;
            public const ushort AutoRepeat = 705;
            /// <summary>Value of a value input in the element's value type (SpinBox i32, CheckBox/Switch bool, Slider f32, TextField string).</summary>
            public const ushort Value = 706;
            /// <summary>SpinBox i32 (default int.MinValue), Slider f32 (default 0).</summary>
            public const ushort Minimum = 707;
            /// <summary>SpinBox i32 (default int.MaxValue), Slider f32 (default 1).</summary>
            public const ushort Maximum = 708;
            /// <summary>SpinBox i32 (default 1), Slider f32 (default 0 = continuous).</summary>
            public const ushort Step = 709;
            /// <summary>bool, SpinBox: the value can be typed; TextField: not read-only (default true).</summary>
            public const ushort Editable = 710;
            /// <summary>i32 Qt::Orientation, Slider: 1 horizontal (default), 2 vertical.</summary>
            public const ushort Orientation = 711;
            /// <summary>string, TextField placeholder text shown while empty (default empty).</summary>
            public const ushort Placeholder = 712;
            // 768 (ReportPosition, Hover handler position) is reserved: positions come from the Point handler only.
            /// <summary>i32 Qt::MouseButtons, Tap + Point + Drag handler (default left button).</summary>
            public const ushort AcceptedButtons = 769;
            /// <summary>i32 QInputDevice::DeviceTypes, Tap + Point + Wheel + Drag + Touch Points + Pinch handler (default: Qt's = all).</summary>
            public const ushort AcceptedDevices = 770;
            /// <summary>bool, Drag handler: moves the item by its drag offset (default true).</summary>
            public const ushort MovesElement = 771;
            /// <summary>i32, Drag handler: 0 both axes (default), 1 x only, 2 y only.</summary>
            public const ushort DragAxis = 772;
            /// <summary>rectf, Drag handler: minX, minY, maxX, maxY of the drag offset in logical pixels, ±infinity = no limit.</summary>
            public const ushort DragLimits = 773;
            /// <summary>i32 &gt;= 2, Pinch handler (default 2).</summary>
            public const ushort MinimumPointCount = 774;
            /// <summary>i32 &gt;= 2, Pinch handler (default 2).</summary>
            public const ushort MaximumPointCount = 775;
            /// <summary>bool, Touch Points handler: ignore presses an element on top grabs (default true).</summary>
            public const ushort Occlusion = 776;
            /// <summary>u64 shader resource id (<see cref="ResourceKind.Shader"/>, stage fragment), 0 = none.</summary>
            public const ushort FragmentShader = 896;
            /// <summary>u64 shader resource id (stage vertex), 0 = Qt's default vertex shader.</summary>
            public const ushort VertexShader = 897;
            /// <summary>bool, Shader: premultiplied alpha blending (default true).</summary>
            public const ushort Blending = 898;
            /// <summary>ivec2, Shader: grid subdivisions x, y &gt;= 1 (default 1, 1).</summary>
            public const ushort MeshResolution = 899;
            /// <summary>string, QmlElement: absolute path of the .qml file, empty = nothing loaded.</summary>
            public const ushort QmlSource = 928;
            /// <summary>i32, QmlElement: counter, a new value reloads the same file.</summary>
            public const ushort QmlReload = 929;
            /// <summary>f32 array, x,y pairs, logical pixels (Shape).</summary>
            public const ushort ShapePoints = 960;
            /// <summary>bool, close the point list (Shape).</summary>
            public const ushort ShapeClosed = 961;
            /// <summary>path, verbs + points, logical pixels (Shape).</summary>
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

        /// <summary>Keys of the MULTI entries of <see cref="Op.SetEffectChain"/> (u16, 833..855): the values of one MultiEffect run, Qt defaults.</summary>
        internal static class MultiEffectKey
        {
            // 832 unused.
            /// <summary>bool (default false).</summary>
            public const ushort BlurEnabled = 833;
            /// <summary>f32 0..1.</summary>
            public const ushort Blur = 834;
            /// <summary>i32 pixels (default 32); switches shaders.</summary>
            public const ushort BlurMax = 835;
            /// <summary>f32 (default 0).</summary>
            public const ushort BlurMultiplier = 836;
            /// <summary>bool (default false).</summary>
            public const ushort ShadowEnabled = 837;
            /// <summary>color (default black).</summary>
            public const ushort ShadowColor = 838;
            /// <summary>f32 logical pixels.</summary>
            public const ushort ShadowHorizontalOffset = 839;
            /// <summary>f32 logical pixels.</summary>
            public const ushort ShadowVerticalOffset = 840;
            /// <summary>f32 0..1 (default 1).</summary>
            public const ushort ShadowBlur = 841;
            /// <summary>f32 (default 1).</summary>
            public const ushort ShadowScale = 842;
            /// <summary>f32 -1..1.</summary>
            public const ushort Brightness = 843;
            /// <summary>f32 -1..1.</summary>
            public const ushort Contrast = 844;
            /// <summary>f32 -1..1.</summary>
            public const ushort Saturation = 845;
            /// <summary>f32 0..1.</summary>
            public const ushort Colorization = 846;
            /// <summary>color (default red).</summary>
            public const ushort ColorizationColor = 847;
            /// <summary>bool autoPaddingEnabled (default true).</summary>
            public const ushort AutoPadding = 848;
            /// <summary>rectf paddingRect in logical pixels (x left, y top, w right, h bottom), with AutoPadding off.</summary>
            public const ushort PaddingRect = 849;
            /// <summary>ref: mask element object, 0 = no mask.</summary>
            public const ushort MaskSource = 850;
            /// <summary>f32 0..1 (default 0).</summary>
            public const ushort MaskThresholdMin = 851;
            /// <summary>f32 0..1 (default 0).</summary>
            public const ushort MaskSpreadAtMin = 852;
            /// <summary>f32 0..1 (default 1).</summary>
            public const ushort MaskThresholdMax = 853;
            /// <summary>f32 0..1 (default 0).</summary>
            public const ushort MaskSpreadAtMax = 854;
            /// <summary>bool (default false).</summary>
            public const ushort MaskInverted = 855;
        }

        /// <summary>Link kinds of <see cref="Op.SetEffectChain"/> (u16).</summary>
        internal static class EffectChainLink
        {
            /// <summary>One MultiEffect run: entries u16 key
            /// (<see cref="MultiEffectKey.BlurEnabled"/>..<see cref="MultiEffectKey.MaskInverted"/>), u16 value type, value.</summary>
            public const ushort Multi = 1;
            /// <summary>Filter shader link: template, shader ids, padding, strength (Pyramid radius), uniform entries like SET_UNIFORMS.</summary>
            public const ushort Shader = 2;
        }

        /// <summary>Pass templates of a shader link (u16); the passes: docs/contributing/protocol.md.</summary>
        internal static class EffectTemplate
        {
            /// <summary>One pass of the shader over the input; shader ids [fragment].</summary>
            public const ushort Single = 1;
            /// <summary>The shader twice (passIndex 0, then 1); [fragment].</summary>
            public const ushort TwoPass = 2;
            /// <summary>Prefilter, pyramid stages, composite (strength = radius in DIP); [prefilter, composite].</summary>
            public const ushort Pyramid = 3;
        }

        /// <summary>SET_UNIFORMS flags (u16, bit mask).</summary>
        internal static class UniformSet
        {
            /// <summary>Empty the element's uniform table before applying the entries (full send).</summary>
            public const ushort Clear = 1;
        }

        /// <summary>SET_STYLE flags (u16, bit mask).</summary>
        internal static class StyleSet
        {
            /// <summary>Empty the element's style table before applying the entries (full send).</summary>
            public const ushort Clear = 1;
        }

        /// <summary>Style states (u8 of a SET_STYLE entry); rank and fallback: docs/contributing/protocol.md.</summary>
        internal static class StyleState
        {
            public const byte Normal = 0;
            public const byte Hovered = 1;
            public const byte Pressed = 2;
            public const byte Disabled = 3;
            public const byte Checked = 4;
        }

        /// <summary>Style fields (u16 of a SET_STYLE entry). Value type per field: protocol.h.</summary>
        internal static class StyleField
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

        /// <summary>Event ids of the poll buffer (u16). Payloads: protocol.h; meaning: docs/contributing/protocol.md.</summary>
        internal static class EventId
        {
            public const ushort Measured = 1;
            public const ushort SceneBounds = 2;
            public const ushort Hover = 3;
            public const ushort Press = 4;
            public const ushort Click = 5;
            public const ushort Pointer = 6;
            public const ushort Media = 7;
            /// <summary>feedbackId 0 (the window itself); 28 bytes: f32 x, y, w, h, u32 flags (<see cref="WindowState"/>), f32 client x, y.</summary>
            public const ushort WindowGeometry = 8;
            /// <summary>UTF-8 error text of the element (no terminator), empty = cleared.</summary>
            public const ushort Error = 9;
            /// <summary>u32 tapCount, Tap handler.</summary>
            public const ushort TapCount = 10;
            /// <summary>u32 long presses, Tap handler.</summary>
            public const ushort LongPress = 11;
            /// <summary>u8 0/1, Point, Drag and Pinch handler.</summary>
            public const ushort Active = 12;
            /// <summary>2 x f32 notches (angleDelta / 120), Wheel handler.</summary>
            public const ushort Wheel = 13;
            /// <summary>2 x f32 translation of the drag (parent space, logical pixels), Drag handler.</summary>
            public const ushort Drag = 14;
            /// <summary>u32 count, then per point u32 id, f32 x, f32 y (item-local), f32 pressure, f32 w, f32 h; Touch Points handler.</summary>
            public const ushort TouchPoints = 15;
            /// <summary>6 x f32 scale, rotation (degrees, clockwise), translation x, y, pivot x, y (parent space); Pinch handler.</summary>
            public const ushort Pinch = 16;
            /// <summary>User change of a value input: u16 value type (<see cref="ValueType"/>), u16 0, value encoded like SET.</summary>
            public const ushort Value = 17;
            /// <summary>A root property that QML changed: u16 name length, u16 value type, name, padding to 4, value like <see cref="Value"/>.</summary>
            public const ushort QmlProperty = 18;
            /// <summary>A root signal: u16 name length, u16 argument count, name, padding to 4, count x (u16 value type, u16 0, value, padding).</summary>
            public const ushort QmlSignal = 19;
        }

        /// <summary>Flags of EVENT_WINDOW_GEOMETRY (u32 bit mask).</summary>
        internal static class WindowState
        {
            public const uint Minimized = 1;
            public const uint Maximized = 2;
            public const uint Fullscreen = 4;
            public const uint Visible = 8;
        }

        /// <summary>Window modes: bit mask of vlqt_window_set_mode (Borderless, Fullscreen).</summary>
        internal static class WindowMode
        {
            public const uint Borderless = 1;
            public const uint Fullscreen = 2;
        }

        /// <summary>Spaces of <see cref="Op.SetSpace"/>, the order of <see cref="VL.Qt.Space"/>.</summary>
        internal static class Space
        {
            public const uint Normalized = 0;
            public const uint Dip = 1;
            public const uint DipTopLeft = 2;
            public const uint PixelTopLeft = 3;
        }

        /// <summary>Values of <see cref="PropertyId.FillMode"/> (i32), the order of <see cref="VL.Qt.Elements.FillMode"/>.
        /// Video does not take <see cref="Tile"/>.</summary>
        internal static class FillMode
        {
            public const int Stretch = 0;
            public const int Fit = 1;
            public const int Crop = 2;
            public const int Tile = 3;
        }

        /// <summary>Pointer events of vlqt_offscreen_send_pointer (QtTexture input), not part of the command buffer.</summary>
        internal static class Pointer
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
        internal static class PointerDevice
        {
            public const int Mouse = 0;
            public const int Touch = 1;
            public const int Pen = 2;
        }

        /// <summary>Key event types of vlqt_offscreen_send_key.</summary>
        internal static class KeyEvent
        {
            public const int Press = 1;
            public const int Release = 2;
        }

        /// <summary>Resource kinds of vlqt_res_create.</summary>
        internal static class ResourceKind
        {
            /// <summary>Image from a file or pixels (vlqt_image_load / vlqt_image_set_pixels), texture on the process device.</summary>
            public const int Image = 2;
            /// <summary>CPU copy of an element (vlqt_snapshot_start / vlqt_snapshot_read), no texture.</summary>
            public const int Snapshot = 3;
            /// <summary>.qsb shader of one stage (vlqt_shader_load), for Shader.</summary>
            public const int Shader = 4;
            /// <summary>Sound of a Video as float samples (vlqt_audio_tap_init / vlqt_audio_tap_read).</summary>
            public const int AudioTap = 5;
        }

        /// <summary>Audio tap (docs/contributing/protocol.md): flags of <see cref="Native.vlqt_audio_tap_state.flags"/> and the init limits.</summary>
        internal static class AudioTap
        {
            /// <summary>A Video element (player) writes into the tap.</summary>
            public const int Attached = 1;
            /// <summary>That player is playing (buffers are expected).</summary>
            public const int Playing = 2;
            public const int MaxChannels = 8;
            public const int MinSampleRate = 8000;
            public const int MaxSampleRate = 384000;
        }

        /// <summary>Shader stages of vlqt_shader_load.</summary>
        internal static class ShaderStage
        {
            public const int Vertex = 1;
            public const int Fragment = 2;
        }

        /// <summary>Flags of vlqt_image_load.</summary>
        internal static class ImageLoadFlags
        {
            /// <summary>Generate mipmaps after the upload (+33 % VRAM).</summary>
            public const uint Mipmaps = 1;
            /// <summary>The worker also keeps a preview of at most 256 px for editor tooltips (vlqt_image_read_preview).</summary>
            public const uint Preview = 2;
        }

        /// <summary>Upload path of vlqt_image_load / vlqt_image_set_pixels (int).</summary>
        internal static class ImageUpload
        {
            /// <summary>A file takes the worker path, pixels the main-thread path (the default).</summary>
            public const int Auto = 0;
            /// <summary>Stripes within the frame budget through an offscreen frame of the main thread.</summary>
            public const int MainThread = 1;
            /// <summary>Textures created in a worker, pixels copied through a staging texture.</summary>
            public const int Worker = 2;
        }

        /// <summary>Resource states (u8 of ResourceEventId.State, return value of vlqt_res_wait).</summary>
        internal static class ResourceState
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
        internal static class ResourceEventId
        {
            /// <summary>u8 state (<see cref="ResourceState"/>).</summary>
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