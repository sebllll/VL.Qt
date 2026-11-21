using System.Collections.Generic;
using System.Collections.Immutable;
using VL.Qt.Elements;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// State of the Effect pin of one item as QtNative holds it: the chain last sent, its links, and the objects of the
    /// elements the links refer to.
    /// </summary>
    internal sealed class EffectChainMirror
    {
        /// <summary>Effect chain QtNative holds (last SET_EFFECT_CHAIN); compared by reference.</summary>
        public Effect? Chain { get; set; }

        /// <summary>The links of <see cref="Chain"/> as sent (<see cref="Effect.Segment"/>).</summary>
        public ImmutableArray<EffectChainLink> Links { get; set; }

        /// <summary>Elements the links refer to (mask of a MULTI run, texture of a shader uniform), each once; parallel to <see cref="Objects"/>.</summary>
        public List<Element>? Elements { get; set; }

        /// <summary>
        /// Objects of <see cref="Elements"/> (hidden texture sources, REF of the chain); not part of
        /// <see cref="ItemMirror.Children"/>, no REPARENT. A placeholder (id 0) is a video that is shown elsewhere.
        /// </summary>
        public List<ItemMirror>? Objects { get; set; }

        /// <summary>Ids of <see cref="Objects"/> as the last SET_EFFECT_CHAIN refers to them; a changed id sends the chain again.</summary>
        public List<uint>? SentIds { get; set; }

        /// <summary>Fold warning currently written into the element's feedback slot, null if none.</summary>
        public string? Warning { get; set; }

        /// <summary>Id of the object of <paramref name="element"/> (0 = placeholder or unknown).</summary>
        public uint ObjectIdOf(Element element)
        {
            if (Elements is { } elements && Objects is { } objects)
            {
                for (int i = 0; i < elements.Count; i++)
                    if (ReferenceEquals(elements[i], element))
                        return objects[i].Id;
            }
            return 0;
        }
    }
}
