using System.Collections.Generic;
using VL.Qt.Elements;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// State of the Effect pin of one item as QtNative holds it: the chain last sent, its links, and the objects of the
    /// elements the links refer to. The lists are kept and refilled when the chain instance changes (an animated effect
    /// makes a new chain every frame).
    /// </summary>
    internal sealed class EffectChainMirror
    {
        /// <summary>Effect chain QtNative holds (last SET_EFFECT_CHAIN); compared by reference.</summary>
        public Effect? Chain { get; set; }

        /// <summary>The links of <see cref="Chain"/> as sent (<see cref="Effect.Segment"/>).</summary>
        public List<EffectChainLink> Links { get; } = new();

        /// <summary>Elements the links refer to (mask of a MULTI run, texture of a shader uniform), each once; parallel to <see cref="Objects"/>.</summary>
        public List<Element> Elements { get; } = new();

        /// <summary>
        /// Objects of <see cref="Elements"/> (hidden texture sources, REF of the chain); not part of
        /// <see cref="ItemMirror.Children"/>, no REPARENT. A placeholder (id 0) is a video that is shown elsewhere.
        /// </summary>
        public List<ItemMirror> Objects { get; private set; } = new();

        /// <summary>An empty list for the next <see cref="Objects"/>: <see cref="ReplaceObjects"/> takes it over, the old list takes its place.</summary>
        public List<ItemMirror> SpareObjects { get; private set; } = new();

        /// <summary>Ids of <see cref="Objects"/> as the last SET_EFFECT_CHAIN refers to them; a changed id sends the chain again.</summary>
        public List<uint> SentIds { get; } = new();

        /// <summary>Fold warning currently written into the element's feedback slot, null if none.</summary>
        public string? Warning { get; set; }

        /// <summary>Makes <paramref name="next"/> (from <see cref="SpareObjects"/>) the <see cref="Objects"/>; the caller clears the old
        /// list, now the spare, when it is done with it.</summary>
        public void ReplaceObjects(List<ItemMirror> next)
        {
            SpareObjects = Objects;
            Objects = next;
        }

        /// <summary>Id of the object of <paramref name="element"/> (0 = placeholder or unknown).</summary>
        public uint ObjectIdOf(Element element)
        {
            for (int i = 0; i < Elements.Count; i++)
                if (ReferenceEquals(Elements[i], element))
                    return Objects[i].Id;
            return 0;
        }
    }
}
