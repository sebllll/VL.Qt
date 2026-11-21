using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Stride.Core.Mathematics;

namespace VL.Qt.Elements
{
    /// <summary>
    /// Immutable chain of effect descriptions (Blur, DropShadow, ColorAdjust, Colorize, Mask) for the <c>Effect</c> pin of an
    /// element. Each effect node adds one link and keeps a reference to the rest of the chain in <see cref="Previous"/>.
    /// The whole chain is folded (<see cref="Fold"/>) into one <see cref="EffectDesc"/>, which becomes one Qt Quick
    /// MultiEffect as <c>layer.effect</c> of the element's item (Effect layer properties, see protocol.h).
    /// <para>
    /// Create a new instance only when an input of the node changes; an unchanged chain is the same instance (the
    /// reconciler compares by reference). The order of the chain has no meaning: MultiEffect applies its parts in a
    /// fixed order. Each kind may appear once; for a duplicate the link nearest to the Effect pin wins.
    /// </para>
    /// <para>
    /// Qt knows only one effect area per element. Padding: if every Blur/DropShadow link has Auto Padding on, Qt grows the
    /// area by what blur and shadow need. If at least one of them has it off, Auto Padding is off for the whole layer and
    /// the area is the side-wise maximum of the Padding of all links with Auto Padding off (so it suffices for all).
    /// </para>
    /// </summary>
    public abstract class Effect
    {
        private protected Effect(Effect? previous) => Previous = previous;

        /// <summary>The rest of the chain (the effects connected before this one), null at the start.</summary>
        public Effect? Previous { get; }

        /// <summary>Kind of the link; used to find duplicates in <see cref="Fold"/>.</summary>
        internal abstract EffectKind Kind { get; }

        /// <summary>Writes the values of this link into <paramref name="desc"/>.</summary>
        internal abstract EffectDesc Apply(in EffectDesc desc);

        /// <summary>
        /// Folds <paramref name="chain"/> into one description; null for no chain. A kind that appears more than once is
        /// applied only for the link nearest to the Effect pin (the head of the chain); <paramref name="warning"/> names it.
        /// </summary>
        internal static EffectDesc? Fold(Effect? chain, out string? warning)
        {
            warning = null;
            if (chain is null)
                return null;
            var links = new List<Effect>();
            for (var e = chain; e is not null; e = e.Previous)
                links.Add(e);
            return FoldRun(links, out warning);
        }

        /// <summary>
        /// Splits <paramref name="chain"/> into the links of SET_EFFECT_CHAIN in application order: the start of the chain
        /// (<see cref="Previous"/> == null) first, the link at the Effect pin last. Consecutive built-in links form one
        /// MultiEffect run, folded like <see cref="Fold"/>; every <see cref="ShaderEffect"/> is a link of its own and ends a run.
        /// Empty for no chain. <paramref name="warnings"/> has at most one duplicate warning per run.
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

        /// <summary>Folds built-in links given nearest-to-the-Effect-pin first (that one wins for a duplicate kind).</summary>
        private static EffectDesc FoldRun(List<Effect> links, out string? warning)
        {
            warning = null;
            var desc = EffectDesc.Default;
            var seen = EffectKind.None;
            var hasManual = false;
            var manualPadding = RectangleF.Empty;
            foreach (var e in links)
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
