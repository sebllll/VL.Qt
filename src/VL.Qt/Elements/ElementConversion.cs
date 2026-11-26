using System;
using System.Runtime.CompilerServices;
using VL.Qt.Styles;

namespace VL.Qt.Elements
{
    /// <summary>
    /// The one conversion from what a pin carries (<see cref="IElement"/>) to what the reconciler takes
    /// (<see cref="Element"/>): an element of VL.Qt as it is, an <see cref="IStylable"/> as its
    /// <see cref="StylableElement"/> placeholder, anything else (null, a foreign implementation) as null.
    /// <para>
    /// A stylable keeps one placeholder as long as it lives, shared by every input it reaches, so a stylable that stays
    /// linked converts to the same instance every frame and the change detection of the nodes and the reconciler sees
    /// no change. Allocates only on the first conversion of a stylable. Thread-safe.
    /// </para>
    /// </summary>
    internal static class ElementConversion
    {
        private static readonly ConditionalWeakTable<IStylable, StylableElement> s_placeholders = new();
        private static readonly ConditionalWeakTable<IStylable, StylableElement>.CreateValueCallback s_create = static s => new StylableElement(s);

        /// <summary>Returns the element for <paramref name="input"/>, null for null and for a foreign implementation of <see cref="IElement"/>.</summary>
        public static Element? ToElement(IElement? input) => input switch
        {
            null => null,
            Element element => element,
            IStylable stylable => s_placeholders.GetValue(stylable, s_create),
            _ => null,
        };

        /// <summary>Returns true if <paramref name="input"/> is an <see cref="IElement"/> that VL.Qt does not know and ignores.</summary>
        public static bool IsForeign(IElement? input) => input is not null and not Element and not IStylable;

        /// <summary>Text of the warning for an ignored implementation of <see cref="IElement"/>.</summary>
        public static string ForeignWarning(Type type)
            => $"{type.Name} implements IElement but is neither an element of VL.Qt nor an IStylable; it is ignored.";

        /// <summary>
        /// Returns the element that shows <paramref name="element"/> in a scene: for the placeholder of a stylable the
        /// element of its last Build (null before the first), otherwise <paramref name="element"/> itself.
        /// </summary>
        public static Element? Shown(Element? element) => element is StylableElement s ? s.Built : element;
    }
}
