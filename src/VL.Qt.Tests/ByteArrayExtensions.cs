using System;
using System.Buffers.Binary;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    internal static class ByteArrayExtensions
    {
        public static byte[] Concat(this byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            a.CopyTo(r, 0);
            b.CopyTo(r, a.Length);
            return r;
        }
    }
}
