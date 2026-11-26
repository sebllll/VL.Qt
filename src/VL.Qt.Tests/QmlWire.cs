using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Text;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using VL.Qt.Wire;
using Path = VL.Lib.IO.Path;
using P = VL.Qt.Wire.Protocol.PropertyId;

namespace VL.Qt.Tests
{
    /// <summary>Wire helpers shared by the QmlElement tests: values and events encoded like protocol.h "QmlElement".</summary>
    internal static class QmlWire
    {
        public static byte[] F(params float[] v)
        {
            var b = new byte[v.Length * 4];
            for (int i = 0; i < v.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), v[i]);
            return b;
        }

        public static byte[] I32(int v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, v);
            return b;
        }

        public static byte[] Str(string s)
        {
            var utf8 = Encoding.UTF8.GetBytes(s);
            var b = new byte[4 + utf8.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)utf8.Length);
            utf8.CopyTo(b, 4);
            return b;
        }

        public static byte[] Bool(bool v) => new[] { (byte)(v ? 1 : 0) };

        public static byte[] Pad(byte[] b) => b.Concat(new byte[(4 - b.Length % 4) % 4]).ToArray();

        private static byte[] U16(int v) => new[] { (byte)(v & 0xFF), (byte)(v >> 8) };

        /// <summary>EVENT_QML_PROPERTY payload: u16 name length, u16 value type, name, padding to 4, value.</summary>
        public static byte[] Property(string name, ushort type, byte[] value)
        {
            var n = Encoding.UTF8.GetBytes(name);
            return U16(n.Length).Concat(U16(type)).Concat(Pad(n)).Concat(value).ToArray();
        }

        /// <summary>EVENT_QML_SIGNAL payload: u16 name length, u16 argument count, name, padding to 4, per argument u16 type, u16 0, value, padding.</summary>
        public static byte[] Signal(string name, params (ushort Type, byte[] Value)[] args)
        {
            var n = Encoding.UTF8.GetBytes(name);
            var p = U16(n.Length).Concat(U16(args.Length)).Concat(Pad(n));
            foreach (var (type, value) in args)
                p = p.Concat(U16(type)).Concat(U16(0)).Concat(Pad(value));
            return p.ToArray();
        }
    }
}
