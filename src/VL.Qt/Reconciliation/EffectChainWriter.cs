using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// Writes the Effect pin of one item as one SET_EFFECT_CHAIN: the links of <see cref="EffectChainMirror.Links"/> in
    /// application order, each with the values that differ from Qt's defaults, lengths converted with the scene's mapping.
    /// The elements the links refer to are referenced by the ids of their objects (<see cref="EffectChainMirror.Objects"/>).
    /// </summary>
    internal static class EffectChainWriter
    {
        /// <summary>Sends the chain of <paramref name="effect"/> for the item <paramref name="id"/> and records the object ids it refers to.</summary>
        public static void Send(ICommandSink sink, uint id, EffectChainMirror effect, in SpaceMapping mapping)
        {
            sink.BeginEffectChain(id);
            foreach (var link in effect.Links)
            {
                if (link.Shader is { } shader)
                    WriteShaderLink(sink, effect, shader, mapping);
                else
                    WriteMultiLink(sink, effect, link.Multi, mapping);
                sink.EndLink();
            }
            sink.EndEffectChain();

            if (effect.Objects is { } objects)
            {
                var ids = effect.SentIds ??= new List<uint>(objects.Count);
                ids.Clear();
                for (int i = 0; i < objects.Count; i++)
                    ids.Add(objects[i].Id);
            }
            else
            {
                effect.SentIds = null;
            }
        }

        /// <summary>Writes the values of one run that differ from Qt's defaults (a run is applied whole in QtNative).</summary>
        private static void WriteMultiLink(ICommandSink sink, EffectChainMirror effect, in EffectDesc n, in SpaceMapping mapping)
        {
            var p = EffectDesc.Default;
            sink.BeginMultiLink();

            if (p.BlurEnabled != n.BlurEnabled)
                sink.EffectBool(Protocol.MultiEffectKey.BlurEnabled, n.BlurEnabled);
            if (p.Blur != n.Blur)
                sink.EffectF32(Protocol.MultiEffectKey.Blur, n.Blur);
            if (p.BlurMax != n.BlurMax)
                sink.EffectI32(Protocol.MultiEffectKey.BlurMax, n.BlurMax);
            if (p.BlurMultiplier != n.BlurMultiplier)
                sink.EffectF32(Protocol.MultiEffectKey.BlurMultiplier, n.BlurMultiplier);

            if (p.ShadowEnabled != n.ShadowEnabled)
                sink.EffectBool(Protocol.MultiEffectKey.ShadowEnabled, n.ShadowEnabled);
            if (p.ShadowColor != n.ShadowColor)
            {
                var c = n.ShadowColor;
                sink.EffectColor(Protocol.MultiEffectKey.ShadowColor, c.R, c.G, c.B, c.A);
            }
            if (p.ShadowOffset.X != n.ShadowOffset.X)
                sink.EffectF32(Protocol.MultiEffectKey.ShadowHorizontalOffset, mapping.ToQt(n.ShadowOffset.X));
            if (p.ShadowOffset.Y != n.ShadowOffset.Y)
                sink.EffectF32(Protocol.MultiEffectKey.ShadowVerticalOffset, mapping.ToQt(n.ShadowOffset.Y));
            if (p.ShadowBlur != n.ShadowBlur)
                sink.EffectF32(Protocol.MultiEffectKey.ShadowBlur, n.ShadowBlur);
            if (p.ShadowScale != n.ShadowScale)
                sink.EffectF32(Protocol.MultiEffectKey.ShadowScale, n.ShadowScale);

            if (p.Brightness != n.Brightness)
                sink.EffectF32(Protocol.MultiEffectKey.Brightness, n.Brightness);
            if (p.Contrast != n.Contrast)
                sink.EffectF32(Protocol.MultiEffectKey.Contrast, n.Contrast);
            if (p.Saturation != n.Saturation)
                sink.EffectF32(Protocol.MultiEffectKey.Saturation, n.Saturation);

            if (p.Colorization != n.Colorization)
                sink.EffectF32(Protocol.MultiEffectKey.Colorization, n.Colorization);
            if (p.ColorizationColor != n.ColorizationColor)
            {
                var c = n.ColorizationColor;
                sink.EffectColor(Protocol.MultiEffectKey.ColorizationColor, c.R, c.G, c.B, c.A);
            }

            if (p.AutoPadding != n.AutoPadding)
                sink.EffectBool(Protocol.MultiEffectKey.AutoPadding, n.AutoPadding);
            if (p.PaddingRect != n.PaddingRect)
            {
                var r = n.PaddingRect;
                sink.EffectRect(Protocol.MultiEffectKey.PaddingRect, mapping.ToQt(r.X), mapping.ToQt(r.Y), mapping.ToQt(r.Width), mapping.ToQt(r.Height));
            }

            if (n.MaskSource is { } mask)
            {
                // A placeholder (the mask is a video shown elsewhere) has no object: no mask until a retry finds one.
                var maskId = effect.ObjectIdOf(mask);
                if (maskId != 0)
                    sink.EffectRef(Protocol.MultiEffectKey.MaskSource, maskId);
            }
            if (p.MaskThresholdMin != n.MaskThresholdMin)
                sink.EffectF32(Protocol.MultiEffectKey.MaskThresholdMin, n.MaskThresholdMin);
            if (p.MaskSpreadAtMin != n.MaskSpreadAtMin)
                sink.EffectF32(Protocol.MultiEffectKey.MaskSpreadAtMin, n.MaskSpreadAtMin);
            if (p.MaskThresholdMax != n.MaskThresholdMax)
                sink.EffectF32(Protocol.MultiEffectKey.MaskThresholdMax, n.MaskThresholdMax);
            if (p.MaskSpreadAtMax != n.MaskSpreadAtMax)
                sink.EffectF32(Protocol.MultiEffectKey.MaskSpreadAtMax, n.MaskSpreadAtMax);
            if (p.MaskInverted != n.MaskInverted)
                sink.EffectBool(Protocol.MultiEffectKey.MaskInverted, n.MaskInverted);
        }

        /// <summary>
        /// Padding, strength and the length uniforms (<see cref="Uniform.IsLength"/>) of a shader link are in units, converted
        /// here; the padding is rounded up to whole DIP. The other uniforms follow like SET_UNIFORMS.
        /// </summary>
        private static void WriteShaderLink(ICommandSink sink, EffectChainMirror effect, ShaderEffect shader, in SpaceMapping mapping)
        {
            var pad = shader.Padding;
            var padX = EffectPadding.Ceil(mapping.ToQt(pad.X));
            var padY = EffectPadding.Ceil(mapping.ToQt(pad.Y));
            var padW = EffectPadding.Ceil(mapping.ToQt(pad.Width));
            var padH = EffectPadding.Ceil(mapping.ToQt(pad.Height));
            sink.BeginEffectChainLink(shader.Template, shader.ShaderIds.AsSpan(), padX, padY, padW, padH, mapping.ToQt(shader.Strength));
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
                    var numbers = u.IsLength ? ScaleLength(u.Numbers, mapping.UnitScale) : u.Numbers;
                    UniformWriter.WriteEntry(sink, u.Name, new UniformValue(u.ValueType, numbers, u.Integer, handle));
                }
                if (u.Sampler is { } sampler)
                    sink.UniformSampler(u.Name, UniformWriter.PackSampler(sampler));
            }
        }

        /// <summary>Float and vector components of a length uniform (M11..M14) × <paramref name="scale"/>.</summary>
        private static Matrix ScaleLength(Matrix m, float scale)
        {
            m.M11 *= scale; m.M12 *= scale; m.M13 *= scale; m.M14 *= scale;
            return m;
        }
    }
}
