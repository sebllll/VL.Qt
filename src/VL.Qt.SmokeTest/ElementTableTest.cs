using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using VL.Qt.Interop;
using VL.Qt.Wire;
using VL.Qt;

// NELM-12: every property of the element table (elements.cpp) resolves to a QML property or a setter and takes a valid
// value. A typo in a qmlName only logs a warning at the first CREATE and then makes every SET of that property fail with -6.
// The types and properties come from Protocol.cs by reflection, so a new constant cannot be skipped silently: a Type
// without a row in s_typeProps or a Prop that is in no row and not exempt fails the test.
[TestFixture, NonParallelizable]
public static class ElementTableTest
{
    // Block 1..255 of Protocol.PropertyId: on every element type (commonProps() in elements.cpp).
    private const ushort commonMax = 255;

    // Type-specific properties per element type, mirrored from the tables in elements.cpp and the protocol.h comments.
    private static readonly Dictionary<ushort, ushort[]> s_typeProps = new()
    {
        [Protocol.ElementType.Group] = [Protocol.PropertyId.AutoSize],
        [Protocol.ElementType.Rectangle] = [Protocol.PropertyId.Color, Protocol.PropertyId.Radius, Protocol.PropertyId.BorderColor, Protocol.PropertyId.BorderWidth],
        [Protocol.ElementType.Text] =
        [
            Protocol.PropertyId.Text, Protocol.PropertyId.FontFamily, Protocol.PropertyId.FontSize, Protocol.PropertyId.FontWeight, Protocol.PropertyId.FontItalic,
            Protocol.PropertyId.Color, Protocol.PropertyId.HorizontalAlignment, Protocol.PropertyId.VerticalAlignment, Protocol.PropertyId.WrapMode,
            Protocol.PropertyId.Elide, Protocol.PropertyId.LineHeight, Protocol.PropertyId.LetterSpacing, Protocol.PropertyId.FontSizeMode,
        ],
        [Protocol.ElementType.Image] = [Protocol.PropertyId.Image, Protocol.PropertyId.FillMode, Protocol.PropertyId.Smooth],
        [Protocol.ElementType.Row] = [Protocol.PropertyId.Spacing, Protocol.PropertyId.Padding, Protocol.PropertyId.ChildAlignment, Protocol.PropertyId.AutoSize],
        [Protocol.ElementType.Column] = [Protocol.PropertyId.Spacing, Protocol.PropertyId.Padding, Protocol.PropertyId.ChildAlignment, Protocol.PropertyId.AutoSize],
        [Protocol.ElementType.Video] =
        [
            Protocol.PropertyId.Source, Protocol.PropertyId.FillMode, Protocol.PropertyId.Playing, Protocol.PropertyId.Loop, Protocol.PropertyId.KeepLastFrame,
            Protocol.PropertyId.PlaybackRate, Protocol.PropertyId.Volume, Protocol.PropertyId.AudioTap,
        ],
        [Protocol.ElementType.Button] = [Protocol.PropertyId.Text, Protocol.PropertyId.Enabled, Protocol.PropertyId.AutoRepeat, Protocol.PropertyId.AutoSize],
        [Protocol.ElementType.SpinBox] =
        [
            Protocol.PropertyId.Value, Protocol.PropertyId.Minimum, Protocol.PropertyId.Maximum, Protocol.PropertyId.Step, Protocol.PropertyId.Editable,
            Protocol.PropertyId.Enabled, Protocol.PropertyId.AutoSize,
        ],
        [Protocol.ElementType.CheckBox] = [Protocol.PropertyId.Value, Protocol.PropertyId.Enabled, Protocol.PropertyId.AutoSize],
        [Protocol.ElementType.Switch] = [Protocol.PropertyId.Value, Protocol.PropertyId.Enabled, Protocol.PropertyId.AutoSize],
        [Protocol.ElementType.Slider] =
        [
            Protocol.PropertyId.Value, Protocol.PropertyId.Minimum, Protocol.PropertyId.Maximum, Protocol.PropertyId.Step, Protocol.PropertyId.Orientation,
            Protocol.PropertyId.Enabled, Protocol.PropertyId.AutoSize,
        ],
        [Protocol.ElementType.TextField] =
            [Protocol.PropertyId.Value, Protocol.PropertyId.Placeholder, Protocol.PropertyId.Editable, Protocol.PropertyId.Enabled, Protocol.PropertyId.AutoSize],
        [Protocol.ElementType.Shader] =
        [
            Protocol.PropertyId.FragmentShader, Protocol.PropertyId.VertexShader, Protocol.PropertyId.Blending, Protocol.PropertyId.MeshResolution,
            Protocol.PropertyId.Source, Protocol.PropertyId.AutoSize,
        ],
        [Protocol.ElementType.Qml] = [Protocol.PropertyId.QmlSource, Protocol.PropertyId.QmlReload, Protocol.PropertyId.AutoSize],
        [Protocol.ElementType.Shape] =
        [
            Protocol.PropertyId.ShapePoints, Protocol.PropertyId.ShapeClosed, Protocol.PropertyId.ShapePath, Protocol.PropertyId.ShapeFill,
            Protocol.PropertyId.ShapeStrokeColor, Protocol.PropertyId.ShapeStrokeWidth, Protocol.PropertyId.ShapeCap, Protocol.PropertyId.ShapeJoin,
            Protocol.PropertyId.ShapeMiterLimit, Protocol.PropertyId.ShapeDash, Protocol.PropertyId.ShapeDashOffset, Protocol.PropertyId.ShapeTrimStart,
            Protocol.PropertyId.ShapeTrimEnd, Protocol.PropertyId.ShapeTrimOffset, Protocol.PropertyId.ShapeFillRule,
        ],
    };

    // Default value type of each property (protocol.h comments); TypeValueType has the per-type exceptions.
    private static readonly Dictionary<ushort, ushort> s_valueType = new()
    {
        [Protocol.PropertyId.Size] = Protocol.ValueType.Vec2,
        [Protocol.PropertyId.Transform] = Protocol.ValueType.Mat4,
        [Protocol.PropertyId.Opacity] = Protocol.ValueType.F32,
        [Protocol.PropertyId.Clip] = Protocol.ValueType.Bool,
        [Protocol.PropertyId.Anchor] = Protocol.ValueType.I32,
        [Protocol.PropertyId.Debug] = Protocol.ValueType.Bool,
        [Protocol.PropertyId.Color] = Protocol.ValueType.Color,
        [Protocol.PropertyId.Radius] = Protocol.ValueType.F32,
        [Protocol.PropertyId.BorderColor] = Protocol.ValueType.Color,
        [Protocol.PropertyId.BorderWidth] = Protocol.ValueType.F32,
        [Protocol.PropertyId.Text] = Protocol.ValueType.String,
        [Protocol.PropertyId.FontFamily] = Protocol.ValueType.String,
        [Protocol.PropertyId.FontSize] = Protocol.ValueType.F32,
        [Protocol.PropertyId.FontWeight] = Protocol.ValueType.I32,
        [Protocol.PropertyId.FontItalic] = Protocol.ValueType.Bool,
        [Protocol.PropertyId.HorizontalAlignment] = Protocol.ValueType.I32,
        [Protocol.PropertyId.VerticalAlignment] = Protocol.ValueType.I32,
        [Protocol.PropertyId.WrapMode] = Protocol.ValueType.I32,
        [Protocol.PropertyId.Elide] = Protocol.ValueType.I32,
        [Protocol.PropertyId.LineHeight] = Protocol.ValueType.F32,
        [Protocol.PropertyId.LetterSpacing] = Protocol.ValueType.F32,
        [Protocol.PropertyId.FontSizeMode] = Protocol.ValueType.I32,
        [Protocol.PropertyId.Source] = Protocol.ValueType.String, // ShaderEffect: Ref
        [Protocol.PropertyId.FillMode] = Protocol.ValueType.I32,
        [Protocol.PropertyId.Smooth] = Protocol.ValueType.Bool,
        [Protocol.PropertyId.Image] = Protocol.ValueType.U64,
        [Protocol.PropertyId.Spacing] = Protocol.ValueType.F32,
        [Protocol.PropertyId.Padding] = Protocol.ValueType.F32,
        [Protocol.PropertyId.ChildAlignment] = Protocol.ValueType.I32,
        [Protocol.PropertyId.AutoSize] = Protocol.ValueType.Bool,
        [Protocol.PropertyId.Playing] = Protocol.ValueType.Bool,
        [Protocol.PropertyId.Loop] = Protocol.ValueType.Bool,
        [Protocol.PropertyId.PlaybackRate] = Protocol.ValueType.F32,
        [Protocol.PropertyId.Volume] = Protocol.ValueType.F32,
        [Protocol.PropertyId.KeepLastFrame] = Protocol.ValueType.Bool,
        [Protocol.PropertyId.AudioTap] = Protocol.ValueType.U64,
        [Protocol.PropertyId.Enabled] = Protocol.ValueType.Bool,
        [Protocol.PropertyId.AutoRepeat] = Protocol.ValueType.Bool,
        [Protocol.PropertyId.Value] = Protocol.ValueType.I32, // SpinBox; CheckBox/Switch Bool, Slider F32, TextField String
        [Protocol.PropertyId.Minimum] = Protocol.ValueType.I32, // SpinBox; Slider F32
        [Protocol.PropertyId.Maximum] = Protocol.ValueType.I32,
        [Protocol.PropertyId.Step] = Protocol.ValueType.I32,
        [Protocol.PropertyId.Editable] = Protocol.ValueType.Bool,
        [Protocol.PropertyId.Orientation] = Protocol.ValueType.I32,
        [Protocol.PropertyId.Placeholder] = Protocol.ValueType.String,
        [Protocol.PropertyId.FragmentShader] = Protocol.ValueType.U64,
        [Protocol.PropertyId.VertexShader] = Protocol.ValueType.U64,
        [Protocol.PropertyId.Blending] = Protocol.ValueType.Bool,
        [Protocol.PropertyId.MeshResolution] = Protocol.ValueType.Ivec2,
        [Protocol.PropertyId.QmlSource] = Protocol.ValueType.String,
        [Protocol.PropertyId.QmlReload] = Protocol.ValueType.I32,
        [Protocol.PropertyId.ShapePoints] = Protocol.ValueType.F32Array,
        [Protocol.PropertyId.ShapeClosed] = Protocol.ValueType.Bool,
        [Protocol.PropertyId.ShapePath] = Protocol.ValueType.Path,
        [Protocol.PropertyId.ShapeFill] = Protocol.ValueType.Color,
        [Protocol.PropertyId.ShapeStrokeColor] = Protocol.ValueType.Color,
        [Protocol.PropertyId.ShapeStrokeWidth] = Protocol.ValueType.F32,
        [Protocol.PropertyId.ShapeCap] = Protocol.ValueType.I32,
        [Protocol.PropertyId.ShapeJoin] = Protocol.ValueType.I32,
        [Protocol.PropertyId.ShapeMiterLimit] = Protocol.ValueType.F32,
        [Protocol.PropertyId.ShapeDash] = Protocol.ValueType.F32Array,
        [Protocol.PropertyId.ShapeDashOffset] = Protocol.ValueType.F32,
        [Protocol.PropertyId.ShapeTrimStart] = Protocol.ValueType.F32,
        [Protocol.PropertyId.ShapeTrimEnd] = Protocol.ValueType.F32,
        [Protocol.PropertyId.ShapeTrimOffset] = Protocol.ValueType.F32,
        [Protocol.PropertyId.ShapeFillRule] = Protocol.ValueType.I32,
    };

    // Not element table properties: input handler properties (only on handler objects) and the keys of the effect chain
    // (SET of them is UnknownProperty, protocol.h "Effect chain").
    private static bool IsExempt(ushort prop) =>
        prop >= Protocol.PropertyId.AcceptedButtons && prop <= Protocol.PropertyId.Occlusion
        || prop >= Protocol.MultiEffectKey.BlurEnabled && prop <= Protocol.MultiEffectKey.MaskInverted;

    private static ushort TypeValueType(ushort type, ushort prop)
    {
        if (type == Protocol.ElementType.Shader && prop == Protocol.PropertyId.Source)
            return Protocol.ValueType.Ref;
        if ((type == Protocol.ElementType.CheckBox || type == Protocol.ElementType.Switch) && prop == Protocol.PropertyId.Value)
            return Protocol.ValueType.Bool;
        if (type == Protocol.ElementType.TextField && prop == Protocol.PropertyId.Value)
            return Protocol.ValueType.String;
        if (type == Protocol.ElementType.Slider
            && (prop == Protocol.PropertyId.Value || prop == Protocol.PropertyId.Minimum || prop == Protocol.PropertyId.Maximum || prop == Protocol.PropertyId.Step))
            return Protocol.ValueType.F32;
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
        var typeNames = Constants(typeof(Protocol.ElementType)).ToDictionary(c => c.Value, c => c.Name);
        var propNames = Constants(typeof(Protocol.PropertyId)).ToDictionary(c => c.Value, c => c.Name);

        // Completeness: a new element type or property must get a row here.
        foreach (var (name, typeId) in Constants(typeof(Protocol.ElementType)).Where(c => c.Value < Protocol.ElementType.HoverHandler))
            s.Check(s_typeProps.ContainsKey(typeId), $"element-table: type {name} ({typeId}) has a row in s_typeProps");
        var covered = s_typeProps.Values.SelectMany(p => p).ToHashSet();
        foreach (var (name, propId) in Constants(typeof(Protocol.PropertyId)))
        {
            if (propId <= commonMax)
                s.Check(s_valueType.ContainsKey(propId), $"element-table: common property {name} ({propId}) has a value type");
            else if (!IsExempt(propId))
                s.Check(covered.Contains(propId) && s_valueType.ContainsKey(propId),
                    $"element-table: property {name} ({propId}) is in a type row and has a value type");
        }

        var w = NativeCalls.CreateWindow(400, 300, "VL.Qt element table test");
        s.Check(w != SceneHandle.Null, "element-table: window_create");
        if (w == SceneHandle.Null)
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
                var rc = Native.Apply(w, buf.Clear().Create(helper, Protocol.ElementType.Rectangle).Reparent(helper, 0, 0)
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
            Native.vlqt_scene_destroy(w);
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
            case Protocol.ValueType.F32: F(prop == Protocol.PropertyId.FontSize ? 12f : 1f); break;
            case Protocol.ValueType.I32:
                switch (prop)
                {
                    case Protocol.PropertyId.Anchor: I(GeometryExtensions.AlignTopLeft); break;
                    case Protocol.PropertyId.FontWeight: I(400); break;
                    case Protocol.PropertyId.HorizontalAlignment: I(0x1); break; // AlignLeft
                    case Protocol.PropertyId.VerticalAlignment: I(0x20); break; // AlignTop
                    case Protocol.PropertyId.ChildAlignment: I(type == Protocol.ElementType.Row ? 0x20 : 0x1); break;
                    case Protocol.PropertyId.Orientation: I(1); break;
                    case Protocol.PropertyId.Maximum: I(10); break;
                    case Protocol.PropertyId.Value: case Protocol.PropertyId.Step: I(1); break;
                    default: I(0); break; // enums: first value
                }
                break;
            case Protocol.ValueType.Bool: b.Add(prop == Protocol.PropertyId.Debug ? (byte)0 : (byte)1); break;
            case Protocol.ValueType.Color: F(1, 0, 0, 1); break;
            case Protocol.ValueType.Mat4: F(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1); break;
            case Protocol.ValueType.Vec2: F(100, 50); break;
            case Protocol.ValueType.Ivec2: I(1, 1); break;
            case Protocol.ValueType.U64: b.AddRange(new byte[8]); break; // 0 = none
            case Protocol.ValueType.Ref: I(2); break;
            case Protocol.ValueType.String:
                var text = prop == Protocol.PropertyId.Source || prop == Protocol.PropertyId.QmlSource ? ""
                    : prop == Protocol.PropertyId.FontFamily ? "Arial" : "x";
                var utf8 = Encoding.UTF8.GetBytes(text);
                I(utf8.Length);
                b.AddRange(utf8);
                break;
            case Protocol.ValueType.F32Array: I(4); F(0, 0, 10, 10); break;
            case Protocol.ValueType.Path:
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
            _b.AddRange(BitConverter.GetBytes((ushort)0)); // flags (reserved)
            _b.AddRange(BitConverter.GetBytes((uint)len));
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
