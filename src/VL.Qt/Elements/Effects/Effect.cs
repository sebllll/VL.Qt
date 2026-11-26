using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace VL.Qt.Elements
{
    /// <summary>
    /// Immutable chain of effects for the <c>Effect</c> pin of an element: each effect node adds one link, keeps the rest of
    /// the chain in <see cref="Previous"/> and creates a new instance only when an input changes (the reconciler compares by reference).
    /// <see cref="Segment"/> cuts the chain at every <see cref="ShaderEffect"/> into runs of merged effects (Blur, DropShadow,
    /// ColorAdjust, Colorize, Mask), in application order: the start of the chain first, the link at the Effect pin last.
    /// Each run becomes one Qt MultiEffect layer, in which the order has no meaning and a kind counts once (the link nearest
    /// to the Effect pin wins, with a warning). The effect area of a run follows the padding rule of docs/effects-and-shaders.md.
    /// </summary>
    public abstract class Effect
    {
        private protected Effect(Effect? previous) => Previous = previous;

        /// <summary>The rest of the chain (the effects connected before this one), null at the start.</summary>
        public Effect? Previous { get; }

        /// <summary>Kind of the link; used to find duplicates in a run of <see cref="Segment"/>.</summary>
        internal abstract EffectKind Kind { get; }

        /// <summary>Writes the values of this link into <paramref name="desc"/>.</summary>
        internal abstract EffectDesc Apply(in EffectDesc desc);

        /// <summary>
        /// Splits <paramref name="chain"/> into the links of SET_EFFECT_CHAIN in application order: the start of the chain
        /// (<see cref="Previous"/> == null) first, the link at the Effect pin last. Consecutive merged effects form one
        /// MultiEffect run (a kind counts once, the link nearest to the Effect pin wins); every <see cref="ShaderEffect"/> is
        /// a link of its own and ends a run. <paramref name="links"/> and <paramref name="warnings"/> are cleared and refilled
        /// (the caller keeps them, so a changed chain allocates nothing); no chain gives no links. <paramref name="warnings"/>
        /// has at most one duplicate warning per run, in application order.
        /// </summary>
        internal static void Segment(Effect? chain, List<EffectChainLink> links, List<string> warnings)
        {
            links.Clear();
            warnings.Clear();
            // Walks from the Effect pin to the start: a run is the stretch up to the next shader, nearest to the pin first, as FoldRun wants it.
            for (var e = chain; e is not null;)
            {
                if (e is ShaderEffect shader)
                {
                    links.Add(new EffectChainLink(default, shader));
                    e = e.Previous;
                    continue;
                }
                links.Add(new EffectChainLink(FoldRun(e, out e, out var warning), null));
                if (warning is not null)
                    warnings.Add(warning);
            }
            links.Reverse();
            warnings.Reverse();
        }

        /// <summary>
        /// Folds the merged effects of the run that starts at <paramref name="nearest"/> (the one nearest to the Effect pin wins for
        /// a duplicate kind) and ends before the next <see cref="ShaderEffect"/>, which is <paramref name="rest"/> (null at the start of the chain).
        /// Effect area: Qt's automatic padding unless an <see cref="IEffectPadding"/> link has Auto Padding off; then the
        /// side-wise maximum of the Padding of those links.
        /// </summary>
        private static EffectDesc FoldRun(Effect nearest, out Effect? rest, out string? warning)
        {
            warning = null;
            var desc = EffectDesc.Default;
            var seen = EffectKind.None;
            var hasManual = false;
            var manualPadding = RectangleF.Empty;
            for (rest = nearest; rest is not null and not ShaderEffect; rest = rest.Previous)
            {
                var e = rest;
                if ((seen & e.Kind) != 0)
                {
                    warning ??= $"{e.Kind} is used more than once in the effect chain; only the one nearest to the element counts.";
                    continue;
                }
                seen |= e.Kind;
                desc = e.Apply(desc);
                if (e is IEffectPadding { AutoPadding: false } manual)
                {
                    var p = manual.Padding;
                    if (!hasManual)
                        manualPadding = p;
                    else
                        manualPadding = new RectangleF(
                            Math.Max(manualPadding.X, p.X), Math.Max(manualPadding.Y, p.Y),
                            Math.Max(manualPadding.Width, p.Width), Math.Max(manualPadding.Height, p.Height));
                    hasManual = true;
                }
            }
            return hasManual ? desc with { AutoPadding = false, PaddingRect = manualPadding } : desc;
        }
    }
}
