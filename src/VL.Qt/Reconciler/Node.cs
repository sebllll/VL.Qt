using System.Collections.Generic;
using System.Collections.Immutable;
using VL.Qt.Elements;
using VL.Qt.Styles;

namespace VL.Qt
{
    /// <summary>
    /// Mirror of one Qt item as the reconciler last sent it: its id, type, the description it was last
    /// updated from (for the reference-equality shortcut and the property diff), its children in order
    /// and the sink slot currently bound to it.
    /// </summary>
    internal sealed class Node
    {
        public Node(uint id, ElementType type)
        {
            Id = id;
            Type = type;
        }

        /// <summary>
        /// Object id in QtNative; 0 (the window root id) marks a placeholder for an ignored occurrence (a video shown
        /// elsewhere), which has no Qt object, no children and no binding (04d).
        /// </summary>
        public uint Id { get; }

        /// <summary>Placeholder for a video that is shown elsewhere (<see cref="SceneRegistry"/>).</summary>
        public bool IsIgnored => Id == 0;

        /// <summary>CREATE type; a different type at this position means destroy + create.</summary>
        public ElementType Type { get; }

        /// <summary>Description this node was last reconciled from; null right after CREATE.</summary>
        public Element? Last { get; set; }

        /// <summary>Child nodes in reconciled order (index = REPARENT index).</summary>
        public List<Node> Children { get; } = new();

        /// <summary>Sink slot bound via BIND; <see cref="SinkSlot.IsNone"/> = unbound.</summary>
        public SinkSlot BoundSink { get; set; }

        /// <summary>Sink handle sent with BIND (<see cref="SinkRegistry"/>); 0 = unbound.</summary>
        public uint BoundHandle { get; set; }

        /// <summary>
        /// This node is the primary occurrence of its element (first in traversal order of the owner renderer,
        /// see <see cref="SceneRegistry"/>): only its sink handle reports state values.
        /// </summary>
        public bool IsPrimary { get; set; }

        /// <summary>Whether <see cref="BoundHandle"/> is registered as primary (<see cref="Elements.SinkRegistry"/>); follows <see cref="IsPrimary"/>.</summary>
        public bool BoundPrimary { get; set; }

        /// <summary>Parent node (null under the window root). Nodes never change their parent.</summary>
        public Node? Parent { get; init; }

        /// <summary>Update pass in which this node was last reconciled (not skipped); see Reconciler.</summary>
        public int VisitedPass { get; set; }

        /// <summary>Update pass in which this node's subtree was last skipped (reference-equal).</summary>
        public int SkippedPass { get; set; }

        /// <summary>
        /// Set when a video shown inside this subtree was taken over by an earlier occurrence: the
        /// subtree must not be skipped on its next visit, so the old owner turns into a placeholder.
        /// </summary>
        public bool Dirty { get; set; }

        /// <summary>Effect chain (Effect pin) QtNative holds (last SET_EFFECT_CHAIN); compared by reference.</summary>
        public Effect? EffectChain { get; set; }

        /// <summary>The links of <see cref="EffectChain"/> as sent (<see cref="Effect.Segment"/>).</summary>
        public ImmutableArray<EffectChainLink> EffectLinks { get; set; }

        /// <summary>Elements the links refer to (mask of a MULTI run, texture of a shader uniform), each once; parallel to <see cref="EffectObjects"/>.</summary>
        public List<Element>? EffectElements { get; set; }

        /// <summary>
        /// Objects of <see cref="EffectElements"/> (hidden texture sources, REF of the chain); not part of <see cref="Children"/>,
        /// no REPARENT. A placeholder (id 0) is a video that is shown elsewhere.
        /// </summary>
        public List<Node>? EffectObjects { get; set; }

        /// <summary>Ids of <see cref="EffectObjects"/> as the last SET_EFFECT_CHAIN refers to them; a changed id sends the chain again.</summary>
        public List<uint>? EffectSentIds { get; set; }

        /// <summary>Fold warning currently written into the element's sink slot, null if none.</summary>
        public string? EffectWarning { get; set; }

        /// <summary>ShaderEffect only: the input child node last sent as SET Source (null = none sent).</summary>
        public Node? EffectSource { get; set; }

        /// <summary>ShaderEffect only: the uniform table QtNative holds (last SET_UNIFORMS), null = nothing sent.</summary>
        public Dictionary<string, UniformValue>? SentUniforms { get; set; }

        /// <summary>
        /// Input handlers attached to this item, one per link of the element's interaction chain; not part of
        /// <see cref="Children"/> (layouts never see them). Null until the element first has an interaction.
        /// </summary>
        public List<HandlerNode>? Handlers { get; set; }

        /// <summary>Resolved style of the parent chain this node was last reconciled with (reference; part of the skip test).</summary>
        public Style? InheritedStyle { get; set; }

        /// <summary>Resolved style of this node (Group, widgets): kept as the same reference while it stays equal.</summary>
        public Style? ResolvedStyle { get; set; }

        /// <summary>Widgets: resolved style QtNative holds (last SET_STYLE); null = nothing sent.</summary>
        public Style? SentStyle { get; set; }

        /// <summary>
        /// Position holds an <see cref="Styles.IStylable"/>: its placeholder this node was reconciled from; null for
        /// plain elements. <see cref="Last"/> is then the element its Build returned.
        /// </summary>
        public StylableElement? Stylable { get; set; }

        /// <summary>Error text of the last failed Build at this position, shown at the parent's sink; null if none.</summary>
        public string? BuildError { get; set; }

        /// <summary>
        /// This subtree contains an <see cref="Styles.IStylable"/>: never skipped, so every Build runs once per pass.
        /// </summary>
        public bool ContainsStylable { get; set; }
    }

    /// <summary>
    /// Mirror of one input handler object as the reconciler last sent it: id, type, key (the sink of the
    /// interaction node), the description it was last updated from and its sink handle. Id 0 marks a placeholder for
    /// an ignored occurrence (the interaction is active elsewhere, see <see cref="SceneRegistry"/>).
    /// </summary>
    internal sealed class HandlerNode
    {
        public HandlerNode(uint id, ushort type, SinkSlot key)
        {
            Id = id;
            Type = type;
            Key = key;
        }

        /// <summary>Handler id in QtNative (same id space as the items); 0 = placeholder without a QtNative object.</summary>
        public uint Id { get; }

        /// <summary>Placeholder for an interaction that is active at another handler.</summary>
        public bool IsIgnored => Id == 0;

        /// <summary>CREATE type (<see cref="Protocol.Type"/>, from 32).</summary>
        public ushort Type { get; }

        /// <summary>Sink of the interaction node; identifies the handler within the element's chain.</summary>
        public SinkSlot Key { get; }

        /// <summary>Description this handler was last updated from.</summary>
        public Interaction? Last { get; set; }

        /// <summary>Sink handle sent with BIND (<see cref="SinkRegistry"/>); 0 = unbound or placeholder.</summary>
        public uint BoundHandle { get; set; }

        /// <summary>Update pass in which the chain last contained this handler (see Reconciler.Handlers).</summary>
        public int SeenPass { get; set; }
    }
}
