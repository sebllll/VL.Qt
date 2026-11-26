using System;
using Stride.Core.Mathematics;

namespace VL.Qt
{
    /// <summary>
    /// Save Bounds, pure part: decides when a reported geometry is written to the Bounds pin. Like VL.Skia
    /// (BoundsChanged.Throttle(0.5 s), Rx Throttle = debounce): a value is taken once the window has been still for
    /// <see cref="Delay"/>. Only normal geometry counts (minimized/maximized/fullscreen/hidden records are skipped, a
    /// pending normal value survives them), values are rounded to whole DIP, and nothing is written while the geometry
    /// equals the pin value (initially the create value, so opening a patch does not mark it changed).
    /// </summary>
    internal sealed class BoundsSaver
    {
        public static readonly TimeSpan Delay = TimeSpan.FromSeconds(0.5);

        private RectangleF _saved;
        private RectangleF? _pending;
        private TimeSpan _pendingSince;

        public BoundsSaver(RectangleF pinValue) => _saved = pinValue;

        public void OnGeometry(WindowGeometry g, TimeSpan now)
        {
            if (!g.IsNormal)
                return;
            var r = Round(g.Bounds);
            if (r == _saved)
                _pending = null; // moved back before the delay ran out
            else if (r != _pending)
            {
                _pending = r;
                _pendingSince = now;
            }
        }

        /// <summary>True (once) if a pending value has been still for <see cref="Delay"/>; it becomes the pin value.</summary>
        public bool TryTake(TimeSpan now, out RectangleF bounds)
        {
            bounds = default;
            if (_pending is not { } p || now - _pendingSince < Delay)
                return false;
            _saved = bounds = p;
            _pending = null;
            return true;
        }

        // Whole DIP: clean pin values; the round trip is stable (placing at an integer DIP reports it back).
        private static RectangleF Round(RectangleF r) =>
            new(MathF.Round(r.X), MathF.Round(r.Y), MathF.Round(r.Width), MathF.Round(r.Height));
    }
}
