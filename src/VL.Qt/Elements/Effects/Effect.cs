using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
        /// a link of its own and ends a run. Empty for no chain. <paramref name="warnings"/> has at most one duplicate warning per run.
        /// </summary>
        internal static ImmutableArray<EffectChainLink> Segment(Effect? chain, out ImmutableArray<string> warnings)
        {
            warnings = ImmutableArray<string>.Empty;
            if (chain is null)
                return ImmutableArray<EffectChainLink>.Empty;
            var links = new List<Effect>();
            for (var e = chain; e is not null; e = e.Previous)
                links.Add(e);
            links.Reverse(); // application order
            var result = ImmutableArray.CreateBuilder<EffectChainLink>();
            ImmutableArray<string>.Builder? warningList = null;
            var runStart = 0;
            for (var i = 0; i <= links.Count; i++)
            {
                if (i < links.Count && links[i] is not ShaderEffect)
                    continue;
                if (i > runStart)
                {
                    // FoldRun expects the run nearest-to-the-pin first.
                    var run = links.GetRange(runStart, i - runStart);
                    run.Reverse();
                    result.Add(new EffectChainLink(FoldRun(run, out var warning), null));
                    if (warning is not null)
                        (warningList ??= ImmutableArray.CreateBuilder<string>()).Add(warning);
                }
                if (i < links.Count)
                    result.Add(new EffectChainLink(default, (ShaderEffect)links[i]));
                runStart = i + 1;
            }
            if (warningList is not null)
                warnings = warningList.ToImmutable();
            return result.ToImmutable();
        }

        /// <summary>
        /// Folds the merged effects of one run, given nearest to the Effect pin first (that one wins for a duplicate kind).
        /// Effect area: Qt's automatic padding unless an <see cref="IEffectPadding"/> link has Auto Padding off; then the
        /// side-wise maximum of the Padding of those links.
        /// </summary>
        private static EffectDesc FoldRun(List<Effect> nearestFirst, out string? warning)
        {
            warning = null;
            var desc = EffectDesc.Default;
            var seen = EffectKind.None;
            var hasManual = false;
            var manualPadding = RectangleF.Empty;
            foreach (var e in nearestFirst)
            {
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
