using System;

namespace VL.Qt.Reconciliation
{
    /// <summary>Padding sums up in float: values like 60.000004 DIP (after the conversion from units) must not grow by a whole DIP.</summary>
    internal static class EffectPadding
    {
        /// <summary>Rounds up to whole DIP, ignoring float noise below 0.001.</summary>
        public static float Ceil(float dip) => MathF.Ceiling(dip - 0.001f);
    }
}
