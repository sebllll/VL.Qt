using System.Collections.Generic;
using System.Collections.Immutable;
using VL.Qt.Elements;

namespace VL.Qt.Tests.Support
{
    /// <summary><see cref="Effect.Segment"/> with fresh lists, for tests that look at the result as a whole.</summary>
    internal static class EffectSegments
    {
        public static ImmutableArray<EffectChainLink> Of(Effect? chain, out ImmutableArray<string> warnings)
        {
            var links = new List<EffectChainLink>();
            var list = new List<string>();
            Effect.Segment(chain, links, list);
            warnings = list.ToImmutableArray();
            return links.ToImmutableArray();
        }
    }
}
