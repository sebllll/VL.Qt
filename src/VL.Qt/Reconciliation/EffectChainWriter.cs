using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// Writes the Effect pin of one item as one SET_EFFECT_CHAIN: the links of <see cref="EffectChainMirror.Links"/> in
    /// application order, each with the values that differ from Qt's defaults; lengths are in DIP and need no conversion.
    /// The elements the links refer to are referenced by the ids of their objects (<see cref="EffectChainMirror.Objects"/>).
    /// </summary>
    internal static class EffectChainWriter
    {
        /// <summary>Sends the chain of <paramref name="effect"/> for the item <paramref name="id"/> and records the object ids it refers to.</summary>
        public static void Send(ICommandSink sink, uint id, EffectChainMirror effect)
        {
            sink.BeginEffectChain(id);
            foreach (var link in effect.Links)
            {
                if (link.Shader is { } shader)
                    WriteShaderLink(sink, effect, shader);
                else
                    WriteMultiLink(sink, effect, link.Multi);
                sink.EndLink();
            }
            sink.EndEffectChain();

            var objects = effect.Objects;
            effect.SentIds.Clear();
            for (int i = 0; i < objects.Count; i++)
                effect.SentIds.Add(objects[i].Id);
        }

        /// <summary>Writes the values of one run that differ from Qt's defaults (a run is applied whole in QtNative).</summary>
        private static void WriteMultiLink(ICommandSink sink, EffectChainMirror effect, in EffectDesc n)
        {
            var p = EffectDesc.Default;
            sink.BeginMultiLink();

            if (p.BlurEnabled != n.BlurEnabled)
                sink.MultiEffectBool(Protocol.MultiEffectKey.BlurEnabled, n.BlurEnabled);
            if (p.Blur != n.Blur)
                sink.MultiEffectF32(Protocol.MultiEffectKey.Blur, n.Blur);
            if (p.BlurMax != n.BlurMax)
                sink.MultiEffectI32(Protocol.MultiEffectKey.BlurMax, n.BlurMax);
            if (p.BlurMultiplier != n.BlurMultiplier)
                sink.MultiEffectF32(Protocol.MultiEffectKey.BlurMultiplier, n.BlurMultiplier);

            if (p.ShadowEnabled != n.ShadowEnabled)
                sink.MultiEffectBool(Protocol.MultiEffectKey.ShadowEnabled, n.ShadowEnabled);
            if (p.ShadowColor != n.ShadowColor)
            {
                var c = n.ShadowColor;
                sink.MultiEffectColor(Protocol.MultiEffectKey.ShadowColor, c.R, c.G, c.B, c.A);
            }
            if (p.ShadowOffset.X != n.ShadowOffset.X)
                sink.MultiEffectF32(Protocol.MultiEffectKey.ShadowHorizontalOffset, n.ShadowOffset.X);
            if (p.ShadowOffset.Y != n.ShadowOffset.Y)
                sink.MultiEffectF32(Protocol.MultiEffectKey.ShadowVerticalOffset, n.ShadowOffset.Y);
            if (p.ShadowBlur != n.ShadowBlur)
                sink.MultiEffectF32(Protocol.MultiEffectKey.ShadowBlur, n.ShadowBlur);
            if (p.ShadowScale != n.ShadowScale)
                sink.MultiEffectF32(Protocol.MultiEffectKey.ShadowScale, n.ShadowScale);

            if (p.Brightness != n.Brightness)
                sink.MultiEffectF32(Protocol.MultiEffectKey.Brightness, n.Brightness);
            if (p.Contrast != n.Contrast)
                sink.MultiEffectF32(Protocol.MultiEffectKey.Contrast, n.Contrast);
            if (p.Saturation != n.Saturation)
                sink.MultiEffectF32(Protocol.MultiEffectKey.Saturation, n.Saturation);

            if (p.Colorization != n.Colorization)
                sink.MultiEffectF32(Protocol.MultiEffectKey.Colorization, n.Colorization);
            if (p.ColorizationColor != n.ColorizationColor)
            {
                var c = n.ColorizationColor;
                sink.MultiEffectColor(Protocol.MultiEffectKey.ColorizationColor, c.R, c.G, c.B, c.A);
            }

            if (p.AutoPadding != n.AutoPadding)
                sink.MultiEffectBool(Protocol.MultiEffectKey.AutoPadding, n.AutoPadding);
            if (p.PaddingRect != n.PaddingRect)
            {
                var r = n.PaddingRect;
                sink.MultiEffectRect(Protocol.MultiEffectKey.PaddingRect, r.X, r.Y, r.Width, r.Height);
            }

            if (n.MaskSource is { } mask)
            {
                // A placeholder (the mask is a video shown elsewhere) has no object: no mask until a retry finds one.
                var maskId = effect.ObjectIdOf(mask);
                if (maskId != 0)
                    sink.MultiEffectRef(Protocol.MultiEffectKey.MaskSource, maskId);
            }
            if (p.MaskThresholdMin != n.MaskThresholdMin)
                sink.MultiEffectF32(Protocol.MultiEffectKey.MaskThresholdMin, n.MaskThresholdMin);
            if (p.MaskSpreadAtMin != n.MaskSpreadAtMin)
                sink.MultiEffectF32(Protocol.MultiEffectKey.MaskSpreadAtMin, n.MaskSpreadAtMin);
            if (p.MaskThresholdMax != n.MaskThresholdMax)
                sink.MultiEffectF32(Protocol.MultiEffectKey.MaskThresholdMax, n.MaskThresholdMax);
            if (p.MaskSpreadAtMax != n.MaskSpreadAtMax)
                sink.MultiEffectF32(Protocol.MultiEffectKey.MaskSpreadAtMax, n.MaskSpreadAtMax);
            if (p.MaskInverted != n.MaskInverted)
                sink.MultiEffectBool(Protocol.MultiEffectKey.MaskInverted, n.MaskInverted);
        }

        /// <summary>
        /// Padding, strength and the uniforms of a shader link are in DIP and go to QtNative as they are; the padding is rounded
        /// up to whole DIP. The uniforms follow like SET_UNIFORMS.
        /// </summary>
        private static void WriteShaderLink(ICommandSink sink, EffectChainMirror effect, ShaderEffect shader)
        {
            var pad = shader.Padding;
            var padX = EffectPadding.Ceil(pad.X);
            var padY = EffectPadding.Ceil(pad.Y);
            var padW = EffectPadding.Ceil(pad.Width);
            var padH = EffectPadding.Ceil(pad.Height);
            sink.BeginEffectChainLink(shader.Template, shader.ShaderIds.AsSpan(), padX, padY, padW, padH, shader.Strength);
            foreach (var u in shader.Uniforms)
            {
                if (u is null || !Protocol.IsValidName(u.Name))
                    continue;
                ulong handle = 0;
                if (u.Texture is { } texture)
                    handle = effect.ObjectIdOf(texture);
                else if (u.Image is { } image)
                    handle = image.Id;
                if (u.ValueType != Protocol.ValueType.Sampler)
                {
                    UniformWriter.WriteEntry(sink, u.Name, new UniformValue(u.ValueType, u.Numbers, u.Integer, handle));
                }
                if (u.Sampler is { } sampler)
                    sink.UniformSampler(u.Name, UniformWriter.PackSampler(sampler));
            }
        }
    }
}
