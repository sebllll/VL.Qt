using System;
using Stride.Core.Mathematics;

namespace VL.Qt.Wire
{
    /// <summary>Stride-typed convenience overloads for any <see cref="ICommandSink"/>.</summary>
    internal static class CommandSinkExtensions
    {
        public static void SetColor(this ICommandSink sink, uint id, ushort prop, in Color4 c) => sink.SetColor(id, prop, c.R, c.G, c.B, c.A);

        public static void SetRect(this ICommandSink sink, uint id, ushort prop, in RectangleF r) => sink.SetRect(id, prop, r.X, r.Y, r.Width, r.Height);

        public static void SetString(this ICommandSink sink, uint id, ushort prop, string? value) => sink.SetString(id, prop, value.AsSpan());
    }
}
