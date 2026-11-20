using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Stride.Core.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Styles;
using P = VL.Qt.Protocol.Prop;

namespace VL.Qt
{
    /// <summary>
    /// Effect chain (plan effects/08): the Effect chain of an element is segmented (<see cref="Effect.Segment"/>) into
    /// links in application order and sent as one SET_EFFECT_CHAIN, only when the chain instance changed (reference
    /// comparison, a new instance sends the whole chain again, no chain = linkCount 0). Elements the links refer to (the mask
    /// of a MULTI run, the texture of a shader uniform) are objects of their own, reconciled without REPARENT before the chain
    /// is sent and destroyed after it (<see cref="Node.EffectObjects"/>).
    /// </summary>
    internal sealed partial class Reconciler
    {
        private void ReconcileEffect(Node node, Element next, Style inherited)
        {
            var chain = next.Effect;
            if (chain is null && node.EffectChain is null)
                return;

            if (ReferenceEquals(chain, node.EffectChain))
            {
                var sent = RetryEffectObjects(node, inherited);
                // A new unit scale changes the lengths of the chain.
                if (_rescale && !sent && chain is not null)
                    SendEffectChain(node);
                return;
            }

            ChangeEffectChain(node, next, chain, inherited);
        }

        /// <summary>The chain instance changed: segments it, reconciles the objects, sends it and destroys the objects it no longer uses.</summary>
        private void ChangeEffectChain(Node node, Element next, Effect? chain, Style inherited)
        {
            var links = Effect.Segment(chain, out var warnings);
            node.EffectChain = chain;
            node.EffectLinks = links;
            SetEffectWarning(node, next.Feedback, warnings.IsEmpty ? null : string.Join(" ", warnings));

            var elements = CollectEffectElements(links);
            var old = node.EffectObjects;
            var used = old is { Count: > 0 } ? new bool[old.Count] : null;
            var matches = new Node?[elements.Count];
            if (used is not null)
            {
                // The same element keeps its object; leftovers of the same type are updated in place.
                for (int i = 0; i < elements.Count; i++)
                    matches[i] = TakeObject(old!, used, elements[i], sameElement: true);
                for (int i = 0; i < elements.Count; i++)
                    matches[i] ??= TakeObject(old!, used, elements[i], sameElement: false);
            }

            var objects = new List<Node>(elements.Count);
            for (int i = 0; i < elements.Count; i++)
                objects.Add(Reconcile(matches[i], elements[i], null, node, 0, inherited, attached: true));
            node.EffectElements = objects.Count > 0 ? elements : null;
            node.EffectObjects = objects.Count > 0 ? objects : null;

            SendEffectChain(node);

            // The chain no longer refers to the objects it did not reuse.
            if (used is not null)
            {
                for (int i = 0; i < old!.Count; i++)
                    if (!used[i])
                        DestroySubtree(old[i]);
            }
        }

        /// <summary>
        /// Unchanged chain: the objects are reconciled again (their element may have changed inside, and a video shown
        /// elsewhere stays a placeholder that is retried every pass); the chain is sent again when an object got
        /// another id (returns true then).
        /// </summary>
        private bool RetryEffectObjects(Node node, Style inherited)
        {
            if (node.EffectObjects is not { } objects || node.EffectElements is not { } elements)
                return false;

            var resend = false;
            for (int i = 0; i < objects.Count; i++)
            {
                var o = Reconcile(objects[i], elements[i], null, node, 0, inherited, attached: true);
                objects[i] = o;
                if (o.Id != node.EffectSentIds![i])
                    resend = true;
            }
            if (resend)
                SendEffectChain(node);
            return resend;
        }

        /// <summary>First unused old object of <paramref name="element"/> itself (<paramref name="sameElement"/>) or of its type.</summary>
        private static Node? TakeObject(List<Node> old, bool[] used, Element element, bool sameElement)
        {
            for (int i = 0; i < old.Count; i++)
            {
                if (used[i])
                    continue;
                if (sameElement ? ReferenceEquals(old[i].Last, element) : old[i].Type == element.Type)
                {
                    used[i] = true;
                    return old[i];
                }
            }
            return null;
        }

        /// <summary>The elements the links refer to (mask sources and uniform textures), each once by reference, in chain order.</summary>
        private static List<Element> CollectEffectElements(ImmutableArray<EffectChainLink> links)
        {
            var elements = new List<Element>();
            foreach (var link in links)
            {
                if (link.Shader is { } shader)
                {
                    foreach (var u in shader.Uniforms)
                        if (u?.Texture is { } texture)
                            AddOnce(elements, texture);
                }
                else if (link.Multi.MaskSource is { } mask)
                {
                    AddOnce(elements, mask);
                }
            }
            return elements;
        }

        private static void AddOnce(List<Element> elements, Element element)
        {
            foreach (var e in elements)
                if (ReferenceEquals(e, element))
                    return;
            elements.Add(element);
        }

        /// <summary>Id of the object of <paramref name="element"/> (0 = placeholder or unknown).</summary>
        private static uint EffectObjectId(Node node, Element element)
        {
            if (node.EffectElements is { } elements && node.EffectObjects is { } objects)
            {
                for (int i = 0; i < elements.Count; i++)
                    if (ReferenceEquals(elements[i], element))
                        return objects[i].Id;
            }
            return 0;
        }

        private void SendEffectChain(Node node)
        {
            var sink = _sink;
            sink.BeginEffectChain(node.Id);
            foreach (var link in node.EffectLinks)
            {
                if (link.Shader is { } shader)
                    WriteShaderLink(node, shader);
                else
                    WriteMultiLink(node, link.Multi);
                sink.EndLink();
            }
            sink.EndEffectChain();

            if (node.EffectObjects is { } objects)
            {
                var ids = node.EffectSentIds ??= new List<uint>(objects.Count);
                ids.Clear();
                for (int i = 0; i < objects.Count; i++)
                    ids.Add(objects[i].Id);
            }
            else
            {
                node.EffectSentIds = null;
            }
        }

        /// <summary>Writes the values of one run that differ from Qt's defaults (a run is applied whole in QtNative).</summary>
        private void WriteMultiLink(Node node, in EffectDesc n)
        {
            var sink = _sink;
            var p = EffectDesc.Default;
            sink.BeginMultiLink();

            if (p.BlurEnabled != n.BlurEnabled)
                sink.EffectBool(P.EffectBlurEnabled, n.BlurEnabled);
            if (p.Blur != n.Blur)
                sink.EffectF32(P.EffectBlur, n.Blur);
            if (p.BlurMax != n.BlurMax)
                sink.EffectI32(P.EffectBlurMax, n.BlurMax);
            if (p.BlurMultiplier != n.BlurMultiplier)
                sink.EffectF32(P.EffectBlurMultiplier, n.BlurMultiplier);

            if (p.ShadowEnabled != n.ShadowEnabled)
                sink.EffectBool(P.EffectShadowEnabled, n.ShadowEnabled);
            if (p.ShadowColor != n.ShadowColor)
            {
                var c = n.ShadowColor;
                sink.EffectColor(P.EffectShadowColor, c.R, c.G, c.B, c.A);
            }
            if (p.ShadowOffset.X != n.ShadowOffset.X)
                sink.EffectF32(P.EffectShadowHorizontalOffset, _mapping.ToQt(n.ShadowOffset.X));
            if (p.ShadowOffset.Y != n.ShadowOffset.Y)
                sink.EffectF32(P.EffectShadowVerticalOffset, _mapping.ToQt(n.ShadowOffset.Y));
            if (p.ShadowBlur != n.ShadowBlur)
                sink.EffectF32(P.EffectShadowBlur, n.ShadowBlur);
            if (p.ShadowScale != n.ShadowScale)
                sink.EffectF32(P.EffectShadowScale, n.ShadowScale);

            if (p.Brightness != n.Brightness)
                sink.EffectF32(P.EffectBrightness, n.Brightness);
            if (p.Contrast != n.Contrast)
                sink.EffectF32(P.EffectContrast, n.Contrast);
            if (p.Saturation != n.Saturation)
                sink.EffectF32(P.EffectSaturation, n.Saturation);

            if (p.Colorization != n.Colorization)
                sink.EffectF32(P.EffectColorization, n.Colorization);
            if (p.ColorizationColor != n.ColorizationColor)
            {
                var c = n.ColorizationColor;
                sink.EffectColor(P.EffectColorizationColor, c.R, c.G, c.B, c.A);
            }

            if (p.AutoPadding != n.AutoPadding)
                sink.EffectBool(P.EffectAutoPadding, n.AutoPadding);
            if (p.PaddingRect != n.PaddingRect)
            {
                var r = n.PaddingRect;
                sink.EffectRect(P.EffectPaddingRect, _mapping.ToQt(r.X), _mapping.ToQt(r.Y), _mapping.ToQt(r.Width), _mapping.ToQt(r.Height));
            }

            if (n.MaskSource is { } mask)
            {
                // A placeholder (the mask is a video shown elsewhere) has no object: no mask until a retry finds one.
                var maskId = EffectObjectId(node, mask);
                if (maskId != 0)
                    sink.EffectRef(P.EffectMaskSource, maskId);
            }
            if (p.MaskThresholdMin != n.MaskThresholdMin)
                sink.EffectF32(P.EffectMaskThresholdMin, n.MaskThresholdMin);
            if (p.MaskSpreadAtMin != n.MaskSpreadAtMin)
                sink.EffectF32(P.EffectMaskSpreadAtMin, n.MaskSpreadAtMin);
            if (p.MaskThresholdMax != n.MaskThresholdMax)
                sink.EffectF32(P.EffectMaskThresholdMax, n.MaskThresholdMax);
            if (p.MaskSpreadAtMax != n.MaskSpreadAtMax)
                sink.EffectF32(P.EffectMaskSpreadAtMax, n.MaskSpreadAtMax);
            if (p.MaskInverted != n.MaskInverted)
                sink.EffectBool(P.EffectMaskInverted, n.MaskInverted);
        }

        /// <summary>
        /// Padding, strength and the length uniforms (<see cref="Uniform.IsLength"/>) of a shader link are in units, converted
        /// here; the padding is rounded up to whole DIP. The other uniforms follow like SET_UNIFORMS.
        /// </summary>
        private void WriteShaderLink(Node node, ShaderLink shader)
        {
            var sink = _sink;
            var pad = shader.Padding;
            var padX = EffectPadding.Ceil(_mapping.ToQt(pad.X));
            var padY = EffectPadding.Ceil(_mapping.ToQt(pad.Y));
            var padW = EffectPadding.Ceil(_mapping.ToQt(pad.Width));
            var padH = EffectPadding.Ceil(_mapping.ToQt(pad.Height));
            sink.BeginShaderLink(shader.Template, shader.ShaderIds.AsSpan(), padX, padY, padW, padH, _mapping.ToQt(shader.Strength));
            foreach (var u in shader.Uniforms)
            {
                if (u is null || !UniformWriter.IsValidName(u.Name))
                    continue;
                ulong handle = 0;
                if (u.Texture is { } texture)
                    handle = EffectObjectId(node, texture);
                else if (u.Image is { } image)
                    handle = image.Id;
                if (u.ValueType != Protocol.Vt.Sampler)
                    UniformWriter.WriteEntry(sink, u.Name, new UniformValue(u.ValueType, u.IsLength ? ScaleLength(u.Numbers, _mapping.UnitScale) : u.Numbers, u.Integer, handle));
                if (u.Sampler is { } sampler)
                    sink.UniformSampler(u.Name, sampler.Pack());
            }
        }

        /// <summary>Float and vector components of a length uniform (M11..M14) × <paramref name="scale"/>.</summary>
        private static Matrix ScaleLength(Matrix m, float scale)
        {
            m.M11 *= scale; m.M12 *= scale; m.M13 *= scale; m.M14 *= scale;
            return m;
        }

        /// <summary>Writes the fold warning of <paramref name="node"/> into <paramref name="slot"/> (null clears what this node wrote).</summary>
        private static void SetEffectWarning(Node node, FeedbackSlot slot, string? warning)
        {
            if (warning == node.EffectWarning)
                return;
            if (slot.State is { } state)
            {
                if (warning is not null && state.Warning is null)
                    state.Warning = warning;
                else if (warning is null && state.Warning == node.EffectWarning)
                    state.Warning = null;
            }
            node.EffectWarning = warning;
        }
    }

    /// <summary>Padding sums up in float: values like 60.000004 DIP (after the conversion from units) must not grow by a whole DIP.</summary>
    internal static class EffectPadding
    {
        /// <summary>Rounds up to whole DIP, ignoring float noise below 0.001.</summary>
        public static float Ceil(float dip) => MathF.Ceiling(dip - 0.001f);
    }
}
