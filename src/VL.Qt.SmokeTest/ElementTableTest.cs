using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using VL.Qt.Wire;
using VL.Qt;

// NELM-12: every property of the element table (elements.cpp) resolves to a QML property or a setter and takes a valid
// value. A typo in a qmlName only logs a warning at the first CREATE and then makes every SET of that property fail with -6.
// The types and properties come from Protocol.cs by reflection, so a new constant cannot be skipped silently: a Type
// without a row in s_typeProps or a Prop that is in no row and not exempt fails the test.
[TestFixture, NonParallelizable]
public static class ElementTableTest
{
    // Block 1..255 of Protocol.Prop: on every element type (commonProps() in elements.cpp).
    private const ushort commonMax = 255;

    // Type-specific properties per element type, mirrored from the tables in elements.cpp and the protocol.h comments.
    private static readonly Dictionary<ushort, ushort[]> s_typeProps = new()
    {
        [Protocol.Type.Item] = [Protocol.Prop.AutoSize],
        [Protocol.Type.Rectangle] = [Protocol.Prop.Color, Protocol.Prop.Radius, Protocol.Prop.BorderColor, Protocol.Prop.BorderWidth],
        [Protocol.Type.Text] =
        [
            Protocol.Prop.Text, Protocol.Prop.FontFamily, Protocol.Prop.FontSize, Protocol.Prop.FontWeight, Protocol.Prop.FontItalic,
            Protocol.Prop.Color, Protocol.Prop.HorizontalAlignment, Protocol.Prop.VerticalAlignment, Protocol.Prop.WrapMode,
            Protocol.Prop.Elide, Protocol.Prop.LineHeight, Protocol.Prop.LetterSpacing, Protocol.Prop.FontSizeMode,
        ],
        [Protocol.Type.Image] = [Protocol.Prop.Image, Protocol.Prop.FillMode, Protocol.Prop.Smooth],
        [Protocol.Type.Row] = [Protocol.Prop.Spacing, Protocol.Prop.Padding, Protocol.Prop.ChildAlignment, Protocol.Prop.AutoSize],
        [Protocol.Type.Column] = [Protocol.Prop.Spacing, Protocol.Prop.Padding, Protocol.Prop.ChildAlignment, Protocol.Prop.AutoSize],
        [Protocol.Type.Video] =
        [
            Protocol.Prop.Source, Protocol.Prop.FillMode, Protocol.Prop.Playing, Protocol.Prop.Loop, Protocol.Prop.KeepLastFrame,
            Protocol.Prop.PlaybackRate, Protocol.Prop.Volume, Protocol.Prop.AudioTap,
        ],
        [Protocol.Type.Button] = [Protocol.Prop.Text, Protocol.Prop.Enabled, Protocol.Prop.AutoRepeat, Protocol.Prop.AutoSize],
        [Protocol.Type.SpinBox] =
        [
            Protocol.Prop.Value, Protocol.Prop.Minimum, Protocol.Prop.Maximum, Protocol.Prop.Step, Protocol.Prop.Editable,
            Protocol.Prop.Enabled, Protocol.Prop.AutoSize,
        ],
        [Protocol.Type.CheckBox] = [Protocol.Prop.Value, Protocol.Prop.Enabled, Protocol.Prop.AutoSize],
        [Protocol.Type.Switch] = [Protocol.Prop.Value, Protocol.Prop.Enabled, Protocol.Prop.AutoSize],
        [Protocol.Type.Slider] =
        [
            Protocol.Prop.Value, Protocol.Prop.Minimum, Protocol.Prop.Maximum, Protocol.Prop.Step, Protocol.Prop.Orientation,
            Protocol.Prop.Enabled, Protocol.Prop.AutoSize,
        ],
        [Protocol.Type.TextField] =
            [Protocol.Prop.Value, Protocol.Prop.Placeholder, Protocol.Prop.Editable, Protocol.Prop.Enabled, Protocol.Prop.AutoSize],
        [Protocol.Type.ShaderEffect] =
        [
            Protocol.Prop.FragmentShader, Protocol.Prop.VertexShader, Protocol.Prop.Blending, Protocol.Prop.MeshResolution,
            Protocol.Prop.Source, Protocol.Prop.AutoSize,
        ],
        [Protocol.Type.Qml] = [Protocol.Prop.QmlSource, Protocol.Prop.QmlReload, Protocol.Prop.AutoSize],
        [Protocol.Type.Shape] =
        [
            Protocol.Prop.ShapePoints, Protocol.Prop.ShapeClosed, Protocol.Prop.ShapePath, Protocol.Prop.ShapeFill,
            Protocol.Prop.ShapeStrokeColor, Protocol.Prop.ShapeStrokeWidth, Protocol.Prop.ShapeCap, Protocol.Prop.ShapeJoin,
            Protocol.Prop.ShapeMiterLimit, Protocol.Prop.ShapeDash, Protocol.Prop.ShapeDashOffset, Protocol.Prop.ShapeTrimStart,
            Protocol.Prop.ShapeTrimEnd, Protocol.Prop.ShapeTrimOffset, Protocol.Prop.ShapeFillRule,
        ],
    };

    // Default value type of each property (protocol.h comments); TypeValueType has the per-type exceptions.
    private static readonly Dictionary<ushort, ushort> s_valueType = new()
    {
        [Protocol.Prop.Size] = Protocol.Vt.Vec2,
        [Protocol.Prop.Transform] = Protocol.Vt.Mat4,
        [Protocol.Prop.Opacity] = Protocol.Vt.F32,
        [Protocol.Prop.Clip] = Protocol.Vt.Bool,
        [Protocol.Prop.Anchor] = Protocol.Vt.I32,
        [Protocol.Prop.Debug] = Protocol.Vt.Bool,
        [Protocol.Prop.Color] = Protocol.Vt.Color,
        [Protocol.Prop.Radius] = Protocol.Vt.F32,
        [Protocol.Prop.BorderColor] = Protocol.Vt.Color,
        [Protocol.Prop.BorderWidth] = Protocol.Vt.F32,
        [Protocol.Prop.Text] = Protocol.Vt.String,
        [Protocol.Prop.FontFamily] = Protocol.Vt.String,
        [Protocol.Prop.FontSize] = Protocol.Vt.F32,
        [Protocol.Prop.FontWeight] = Protocol.Vt.I32,
        [Protocol.Prop.FontItalic] = Protocol.Vt.Bool,
        [Protocol.Prop.HorizontalAlignment] = Protocol.Vt.I32,
        [Protocol.Prop.VerticalAlignment] = Protocol.Vt.I32,
        [Protocol.Prop.WrapMode] = Protocol.Vt.I32,
        [Protocol.Prop.Elide] = Protocol.Vt.I32,
        [Protocol.Prop.LineHeight] = Protocol.Vt.F32,
        [Protocol.Prop.LetterSpacing] = Protocol.Vt.F32,
        [Protocol.Prop.FontSizeMode] = Protocol.Vt.I32,
        [Protocol.Prop.Source] = Protocol.Vt.String, // ShaderEffect: Ref
        [Protocol.Prop.FillMode] = Protocol.Vt.I32,
        [Protocol.Prop.Smooth] = Protocol.Vt.Bool,
        [Protocol.Prop.Image] = Protocol.Vt.U64,
        [Protocol.Prop.Spacing] = Protocol.Vt.F32,
        [Protocol.Prop.Padding] = Protocol.Vt.F32,
        [Protocol.Prop.ChildAlignment] = Protocol.Vt.I32,
        [Protocol.Prop.AutoSize] = Protocol.Vt.Bool,
        [Protocol.Prop.Playing] = Protocol.Vt.Bool,
        [Protocol.Prop.Loop] = Protocol.Vt.Bool,
        [Protocol.Prop.PlaybackRate] = Protocol.Vt.F32,
        [Protocol.Prop.Volume] = Protocol.Vt.F32,
        [Protocol.Prop.KeepLastFrame] = Protocol.Vt.Bool,
        [Protocol.Prop.AudioTap] = Protocol.Vt.U64,
        [Protocol.Prop.Enabled] = Protocol.Vt.Bool,
        [Protocol.Prop.AutoRepeat] = Protocol.Vt.Bool,
        [Protocol.Prop.Value] = Protocol.Vt.I32, // SpinBox; CheckBox/Switch Bool, Slider F32, TextField String
        [Protocol.Prop.Minimum] = Protocol.Vt.I32, // SpinBox; Slider F32
        [Protocol.Prop.Maximum] = Protocol.Vt.I32,
        [Protocol.Prop.Step] = Protocol.Vt.I32,
        [Protocol.Prop.Editable] = Protocol.Vt.Bool,
        [Protocol.Prop.Orientation] = Protocol.Vt.I32,
        [Protocol.Prop.Placeholder] = Protocol.Vt.String,
        [Protocol.Prop.FragmentShader] = Protocol.Vt.U64,
        [Protocol.Prop.VertexShader] = Protocol.Vt.U64,
        [Protocol.Prop.Blending] = Protocol.Vt.Bool,
        [Protocol.Prop.MeshResolution] = Protocol.Vt.Ivec2,
        [Protocol.Prop.QmlSource] = Protocol.Vt.String,
        [Protocol.Prop.QmlReload] = Protocol.Vt.I32,
        [Protocol.Prop.ShapePoints] = Protocol.Vt.F32Array,
        [Protocol.Prop.ShapeClosed] = Protocol.Vt.Bool,
        [Protocol.Prop.ShapePath] = Protocol.Vt.Path,
        [Protocol.Prop.ShapeFill] = Protocol.Vt.Color,
        [Protocol.Prop.ShapeStrokeColor] = Protocol.Vt.Color,
        [Protocol.Prop.ShapeStrokeWidth] = Protocol.Vt.F32,
        [Protocol.Prop.ShapeCap] = Protocol.Vt.I32,
        [Protocol.Prop.ShapeJoin] = Protocol.Vt.I32,
        [Protocol.Prop.ShapeMiterLimit] = Protocol.Vt.F32,
        [Protocol.Prop.ShapeDash] = Protocol.Vt.F32Array,
        [Protocol.Prop.ShapeDashOffset] = Protocol.Vt.F32,
        [Protocol.Prop.ShapeTrimStart] = Protocol.Vt.F32,
        [Protocol.Prop.ShapeTrimEnd] = Protocol.Vt.F32,
        [Protocol.Prop.ShapeTrimOffset] = Protocol.Vt.F32,
        [Protocol.Prop.ShapeFillRule] = Protocol.Vt.I32,
    };

    // Not element table properties: input handler properties (only on handler objects) and the keys of the effect chain
    // (SET of them is UnknownProperty, protocol.h "Effect chain").
    private static bool IsExempt(ushort prop) =>
        prop >= Protocol.Prop.AcceptedButtons && prop <= Protocol.Prop.Occlusion
        || prop >= Protocol.Prop.EffectEnabled && prop <= Protocol.Prop.EffectMaskInverted;

    private static ushort TypeValueType(ushort type, ushort prop)
    {
        if (type == Protocol.Type.ShaderEffect && prop == Protocol.Prop.Source)
            return Protocol.Vt.Ref;
        if ((type == Protocol.Type.CheckBox || type == Protocol.Type.Switch) && prop == Protocol.Prop.Value)
            return Protocol.Vt.Bool;
        if (type == Protocol.Type.TextField && prop == Protocol.Prop.Value)
            return Protocol.Vt.String;
        if (type == Protocol.Type.Slider
            && (prop == Protocol.Prop.Value || prop == Protocol.Prop.Minimum || prop == Protocol.Prop.Maximum || prop == Protocol.Prop.Step))
            return Protocol.Vt.F32;
        return s_valueType[prop];
    }

    private static List<(string Name, ushort Value)> Constants(Type holder) =>
        holder.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(ushort))
            .Select(f => (f.Name, (ushort)f.GetRawConstantValue()!))
            .ToList();

    /// <summary>Element table: every property of every element type resolves and takes a valid value (NELM-12).</summary>
    [Test, Category("element-table")]
    public static void ElementTable() => Scenario.Run(Run);

    private static void Run(Scenario s)
    {
        var typeNames = Constants(typeof(Protocol.Type)).ToDictionary(c => c.Value, c => c.Name);
        var propNames = Constants(typeof(Protocol.Prop)).ToDictionary(c => c.Value, c => c.Name);

        // Completeness: a new element type or property must get a row here.
        foreach (var (name, typeId) in Constants(typeof(Protocol.Type)).Where(c => c.Value < Protocol.Type.HoverHandler))
            s.Check(s_typeProps.ContainsKey(typeId), $"element-table: type {name} ({typeId}) has a row in s_typeProps");
        var covered = s_typeProps.Values.SelectMany(p => p).ToHashSet();
        foreach (var (name, propId) in Constants(typeof(Protocol.Prop)))
        {
            if (propId <= commonMax)
                s.Check(s_valueType.ContainsKey(propId), $"element-table: common property {name} ({propId}) has a value type");
            else if (!IsExempt(propId))
                s.Check(covered.Contains(propId) && s_valueType.ContainsKey(propId),
                    $"element-table: property {name} ({propId}) is in a type row and has a value type");
        }

        var w = Native.vlqt_window_create(400, 300, "VL.Qt element table test");
        s.Check(w != IntPtr.Zero, "element-table: window_create");
        if (w == IntPtr.Zero)
            return;
        try
        {
            Native.vlqt_window_show(w);
            var buf = new Buf();
            const uint id = 1, helper = 2;
            foreach (var (type, extra) in s_typeProps.OrderBy(t => t.Key))
            {
                var typeName = typeNames.TryGetValue(type, out var tn) ? tn : type.ToString();
                var logStart = QtHost.LogLength;
                // The helper is the Input of a ShaderEffect (REF value).
                var rc = Native.Apply(w, buf.Clear().Create(helper, Protocol.Type.Rectangle).Reparent(helper, 0, 0)
                    .Create(id, type).Reparent(id, 0, 1).Span);
                s.Check(rc == 0, $"element-table: {typeName} create ({rc}) {(rc != 0 ? Native.LastError() : "")}");
                if (rc != 0)
                    continue;
                foreach (var prop in propNames.Keys.Where(p => p <= commonMax).Concat(extra))
                {
                    var vt = TypeValueType(type, prop);
                    rc = Native.Apply(w, buf.Clear().Set(id, prop, vt, ValueFor(type, prop, vt)).Span);
                    s.Check(rc == 0, $"element-table: {typeName}.{propNames[prop]} (id {prop}, value type {vt}) SET ({rc}) {(rc != 0 ? Native.LastError() : "")}");
                }
                s.Pump(2);
                var bad = QtHost.LogSince(logStart).Split('\n').Where(l => l.Contains("vlqt.elements")).ToList();
                s.Check(bad.Count == 0, $"element-table: {typeName} no vlqt.elements warning ({string.Join(" | ", bad)})");
                Native.Apply(w, buf.Clear().Destroy(id).Destroy(helper).Span);
            }
        }
        finally
        {
            Native.vlqt_window_destroy(w);
            // The Video row starts QtMultimedia (player, audio output) only for a moment. A process that ends right after
            // that stays behind as an unkillable testhost (run alone: element-table is the last scenario). Like the video
            // test, let it settle for a while before the scenario ends.
            for (var t = Stopwatch.StartNew(); t.ElapsedMilliseconds < 2000;)
            {
                s.Pump(1);
                Thread.Sleep(15);
            }
        }
    }

    // A valid value of the property's value type; properties with a narrower range than their type get their own.
    private static byte[] ValueFor(ushort type, ushort prop, ushort vt)
    {
        var b = new List<byte>();
        void F(params float[] f) { foreach (var x in f) b.AddRange(BitConverter.GetBytes(x)); }
        void I(params int[] i) { foreach (var x in i) b.AddRange(BitConverter.GetBytes(x)); }
        switch (vt)
        {
            case Protocol.Vt.F32: F(prop == Protocol.Prop.FontSize ? 12f : 1f); break;
            case Protocol.Vt.I32:
                switch (prop)
                {
                    case Protocol.Prop.Anchor: I(GeometryExtensions.AlignTopLeft); break;
                    case Protocol.Prop.FontWeight: I(400); break;
                    case Protocol.Prop.HorizontalAlignment: I(0x1); break; // AlignLeft
                    case Protocol.Prop.VerticalAlignment: I(0x20); break; // AlignTop
                    case Protocol.Prop.ChildAlignment: I(type == Protocol.Type.Row ? 0x20 : 0x1); break;
                    case Protocol.Prop.Orientation: I(1); break;
                    case Protocol.Prop.Maximum: I(10); break;
                    case Protocol.Prop.Value: case Protocol.Prop.Step: I(1); break;
                    default: I(0); break; // enums: first value
                }
                break;
            case Protocol.Vt.Bool: b.Add(prop == Protocol.Prop.Debug ? (byte)0 : (byte)1); break;
            case Protocol.Vt.Color: F(1, 0, 0, 1); break;
            case Protocol.Vt.Mat4: F(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1); break;
            case Protocol.Vt.Vec2: F(100, 50); break;
            case Protocol.Vt.Ivec2: I(1, 1); break;
            case Protocol.Vt.U64: b.AddRange(new byte[8]); break; // 0 = none
            case Protocol.Vt.Ref: I(2); break;
            case Protocol.Vt.String:
                var text = prop == Protocol.Prop.Source || prop == Protocol.Prop.QmlSource ? ""
                    : prop == Protocol.Prop.FontFamily ? "Arial" : "x";
                var utf8 = Encoding.UTF8.GetBytes(text);
                I(utf8.Length);
                b.AddRange(utf8);
                break;
            case Protocol.Vt.F32Array: I(4); F(0, 0, 10, 10); break;
            case Protocol.Vt.Path:
                I(2, 2);
                b.AddRange(new byte[] { Protocol.Verb.Move, Protocol.Verb.Line, 0, 0 });
                F(0, 0, 10, 10);
                break;
            default: Assert.Fail($"no test value for value type {vt}"); break;
        }
        return b.ToArray();
    }

    // Minimal command encoder (like ApplyTest): the test does not depend on CommandWriter.
    private sealed class Buf
    {
        private readonly List<byte> _b = new();
        public ReadOnlySpan<byte> Span => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_b);
        public Buf Clear() { _b.Clear(); return this; }

        private Buf Cmd(ushort op, params byte[][] parts)
        {
            var len = parts.Sum(p => p.Length);
            _b.AddRange(BitConverter.GetBytes(op));
            _b.AddRange(BitConverter.GetBytes((ushort)len));
            foreach (var p in parts)
                _b.AddRange(p);
            while (_b.Count % 4 != 0)
                _b.Add(0);
            return this;
        }

        private static byte[] U32(uint v) => BitConverter.GetBytes(v);
        private static byte[] U16(ushort v) => BitConverter.GetBytes(v);

        public Buf Create(uint id, ushort type) => Cmd(Protocol.Op.Create, U32(id), U16(type));
        public Buf Destroy(uint id) => Cmd(Protocol.Op.Destroy, U32(id));
        public Buf Reparent(uint id, uint parent, uint index) => Cmd(Protocol.Op.Reparent, U32(id), U32(parent), U32(index));
        public Buf Set(uint id, ushort prop, ushort vt, byte[] value) => Cmd(Protocol.Op.Set, U32(id), U16(prop), U16(vt), value);
    }
}
