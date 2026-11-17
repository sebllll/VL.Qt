using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Stride.Core.Mathematics;
using VL.Lib.Collections;

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
        /// MultiEffect run, folded like <see cref="Fold"/>; every <see cref="ShaderLink"/> is a link of its own and ends a run.
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
                if (i < links.Count && links[i] is not ShaderLink)
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
                    result.Add(new EffectChainLink(default, (ShaderLink)links[i]));
                runStart = i + 1;
            }
            if (warningList is not null)
                warnings = warningList.ToImmutable();
            return result.ToImmutable();
        }

        /// <summary>Folds built-in links given nearest-to-the-Effect-pin first (that one wins for a duplicate kind).</summary>
        static EffectDesc FoldRun(List<Effect> links, out string? warning)
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
                if (e is IManualPadding { AutoPadding: false } manual)
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

    /// <summary>
    /// A filter shader as link of the Effect chain (#489): driven by QtNative's pass driver with a template (protocol.h
    /// "Effect chain", passes: docs/effects-and-shaders.md). Ends a MultiEffect run in <see cref="Effect.Segment"/>.
    /// QtNative sets texelSize, dpr, weight, elementRect, source and the template's inputs itself; <see cref="Uniforms"/> must not use them.
    /// </summary>
    internal abstract class ShaderLink : Effect
    {
        protected ShaderLink(Effect? previous, ushort template, ImmutableArray<ulong> shaderIds, Spread<Uniform>? uniforms,
            RectangleF padding, float strength)
            : base(previous)
        {
            Template = template;
            ShaderIds = shaderIds.IsDefault ? ImmutableArray<ulong>.Empty : shaderIds;
            Uniforms = uniforms ?? Spread<Uniform>.Empty;
            Padding = padding;
            Strength = strength;
        }

        /// <summary><see cref="Protocol.EffectTemplate"/>: Single, TwoPass or Pyramid.</summary>
        public ushort Template { get; }

        /// <summary>Shader resource ids (stage fragment) owned by the node, by role: Single/TwoPass [fragment], Pyramid [prefilter, composite]; 0 = not loaded.</summary>
        public ImmutableArray<ulong> ShaderIds { get; }

        /// <summary>Uniform values by name, like <see cref="ShaderEffect.Uniforms"/>.</summary>
        public Spread<Uniform> Uniforms { get; }

        /// <summary>Extra effect area in units: x = left, y = top, width = right, height = bottom.</summary>
        public RectangleF Padding { get; }

        /// <summary>Pyramid: radius in units; Single and TwoPass: unused.</summary>
        public float Strength { get; }

        internal sealed override EffectKind Kind => EffectKind.None;

        internal sealed override EffectDesc Apply(in EffectDesc desc) => desc;
    }

    /// <summary>
    /// One link of SET_EFFECT_CHAIN, result of <see cref="Effect.Segment"/>: a folded MultiEffect run (<see cref="Shader"/>
    /// null) or a shader link.
    /// </summary>
    internal readonly record struct EffectChainLink(EffectDesc Multi, ShaderLink? Shader)
    {
        public bool IsShader => Shader is not null;
    }

    /// <summary>Kinds of effect links (bit mask, for the duplicate check of <see cref="Effect.Fold"/>).</summary>
    [Flags]
    internal enum EffectKind
    {
        None = 0,
        Blur = 1,
        DropShadow = 2,
        ColorAdjust = 4,
        Colorize = 8,
        Mask = 16,
    }

    /// <summary>
    /// The folded effect chain: all values of one Qt MultiEffect (the Effect layer properties of protocol.h). Defaults
    /// are Qt's, everything off. Lengths in units of the renderer's space (the reconciler converts them).
    /// </summary>
    internal readonly record struct EffectDesc
    {
        public EffectDesc()
        {
        }

        /// <summary>All parts off, Qt defaults.</summary>
        public static readonly EffectDesc Default = new();

        public bool BlurEnabled { get; init; }
        /// <summary>0..1.</summary>
        public float Blur { get; init; }
        /// <summary>Maximum blur radius in pixels (Qt blurMax, default 32); also limits the shadow blur. Changing it switches shaders.</summary>
        public int BlurMax { get; init; } = 32;
        /// <summary>0..; values above 0 enlarge the blur beyond BlurMax with lower quality (Qt default 0).</summary>
        public float BlurMultiplier { get; init; }

        public bool ShadowEnabled { get; init; }
        public Color4 ShadowColor { get; init; } = Color4.Black;
        /// <summary>Shadow offset in units.</summary>
        public Vector2 ShadowOffset { get; init; }
        /// <summary>0..1 (Qt default 1).</summary>
        public float ShadowBlur { get; init; } = 1;
        /// <summary>Scale of the shadow around the item's center (Qt default 1).</summary>
        public float ShadowScale { get; init; } = 1;

        /// <summary>-1..1.</summary>
        public float Brightness { get; init; }
        /// <summary>-1..1.</summary>
        public float Contrast { get; init; }
        /// <summary>-1..1.</summary>
        public float Saturation { get; init; }

        /// <summary>0..1, strength of the colorization (0 = off).</summary>
        public float Colorization { get; init; }
        /// <summary>Colorization color (Qt default red).</summary>
        public Color4 ColorizationColor { get; init; } = new Color4(1, 0, 0, 1);

        /// <summary>On (Qt default): the effect area grows by what blur and shadow need. Off: <see cref="PaddingRect"/> applies.</summary>
        public bool AutoPadding { get; init; } = true;
        /// <summary>Extra effect area in units with AutoPadding off: x = left, y = top, width = right, height = bottom.</summary>
        public RectangleF PaddingRect { get; init; }

        /// <summary>Element used as alpha mask (its own description, reconciled as hidden texture source); null = no mask.</summary>
        public Element? MaskSource { get; init; }
        /// <summary>0..1, mask alpha below this is fully masked (Qt default 0).</summary>
        public float MaskThresholdMin { get; init; }
        /// <summary>0..1, softness at the lower threshold (Qt default 0).</summary>
        public float MaskSpreadAtMin { get; init; }
        /// <summary>0..1, mask alpha above this is fully masked (Qt default 1).</summary>
        public float MaskThresholdMax { get; init; } = 1;
        /// <summary>0..1, softness at the upper threshold (Qt default 0).</summary>
        public float MaskSpreadAtMax { get; init; }
        /// <summary>Inverts the mask.</summary>
        public bool MaskInverted { get; init; }
    }

    /// <summary>A link with Auto Padding / Padding pins; <see cref="Effect.Fold"/> merges them into the one effect area.</summary>
    internal interface IManualPadding
    {
        bool AutoPadding { get; }
        /// <summary>Extra area in units: x = left, y = top, width = right, height = bottom (used with AutoPadding off).</summary>
        RectangleF Padding { get; }
    }

    /// <summary>Blur link (MultiEffect blur, blurMax, blurMultiplier).</summary>
    internal sealed class BlurEffect : Effect, IManualPadding
    {
        public BlurEffect(Effect? previous, float amount, int max, float multiplier, bool autoPadding = true, RectangleF padding = default)
            : base(previous)
        {
            Amount = amount;
            Max = max;
            Multiplier = multiplier;
            AutoPadding = autoPadding;
            Padding = padding;
        }

        public bool AutoPadding { get; }
        public RectangleF Padding { get; }

        public float Amount { get; }
        public int Max { get; }
        public float Multiplier { get; }

        internal override EffectKind Kind => EffectKind.Blur;

        internal override EffectDesc Apply(in EffectDesc desc)
            => desc with { BlurEnabled = true, Blur = Amount, BlurMax = Max, BlurMultiplier = Multiplier };
    }

    /// <summary>Drop shadow link (MultiEffect shadow*).</summary>
    internal sealed class DropShadowEffect : Effect, IManualPadding
    {
        public DropShadowEffect(Effect? previous, Color4 color, Vector2 offset, float blur, float scale, bool autoPadding = true, RectangleF padding = default)
            : base(previous)
        {
            Color = color;
            Offset = offset;
            Blur = blur;
            Scale = scale;
            AutoPadding = autoPadding;
            Padding = padding;
        }

        public bool AutoPadding { get; }
        public RectangleF Padding { get; }

        public Color4 Color { get; }
        /// <summary>In units.</summary>
        public Vector2 Offset { get; }
        public float Blur { get; }
        public float Scale { get; }

        internal override EffectKind Kind => EffectKind.DropShadow;

        internal override EffectDesc Apply(in EffectDesc desc)
            => desc with { ShadowEnabled = true, ShadowColor = Color, ShadowOffset = Offset, ShadowBlur = Blur, ShadowScale = Scale };
    }

    /// <summary>Color adjustment link (MultiEffect brightness, contrast, saturation).</summary>
    internal sealed class ColorAdjustEffect : Effect
    {
        public ColorAdjustEffect(Effect? previous, float brightness, float contrast, float saturation)
            : base(previous)
        {
            Brightness = brightness;
            Contrast = contrast;
            Saturation = saturation;
        }

        public float Brightness { get; }
        public float Contrast { get; }
        public float Saturation { get; }

        internal override EffectKind Kind => EffectKind.ColorAdjust;

        internal override EffectDesc Apply(in EffectDesc desc)
            => desc with { Brightness = Brightness, Contrast = Contrast, Saturation = Saturation };
    }

    /// <summary>Colorize link (MultiEffect colorization, colorizationColor).</summary>
    internal sealed class ColorizeEffect : Effect
    {
        public ColorizeEffect(Effect? previous, Color4 color, float amount)
            : base(previous)
        {
            Color = color;
            Amount = amount;
        }

        public Color4 Color { get; }
        public float Amount { get; }

        internal override EffectKind Kind => EffectKind.Colorize;

        internal override EffectDesc Apply(in EffectDesc desc)
            => desc with { ColorizationColor = Color, Colorization = Amount };
    }

    /// <summary>Mask link (MultiEffect maskSource, thresholds, spreads, maskInverted); holds the mask element.</summary>
    internal sealed class MaskEffect : Effect
    {
        public MaskEffect(Effect? previous, Element? source, float thresholdMin, float spreadAtMin, float thresholdMax,
            float spreadAtMax, bool inverted)
            : base(previous)
        {
            Source = source;
            ThresholdMin = thresholdMin;
            SpreadAtMin = spreadAtMin;
            ThresholdMax = thresholdMax;
            SpreadAtMax = spreadAtMax;
            Inverted = inverted;
        }

        /// <summary>The mask element; null = no mask (the link changes nothing).</summary>
        public Element? Source { get; }
        public float ThresholdMin { get; }
        public float SpreadAtMin { get; }
        public float ThresholdMax { get; }
        public float SpreadAtMax { get; }
        public bool Inverted { get; }

        internal override EffectKind Kind => EffectKind.Mask;

        internal override EffectDesc Apply(in EffectDesc desc)
            => Source is null ? desc : desc with
            {
                MaskSource = Source,
                MaskThresholdMin = ThresholdMin,
                MaskSpreadAtMin = SpreadAtMin,
                MaskThresholdMax = ThresholdMax,
                MaskSpreadAtMax = SpreadAtMax,
                MaskInverted = Inverted,
            };
    }
}
