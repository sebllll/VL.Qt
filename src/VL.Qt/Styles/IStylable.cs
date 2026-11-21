using VL.Core.Import;
using VL.Qt.Elements;

namespace VL.Qt.Styles
{
    /// <summary>
    /// A self-made widget that reads the style of its surroundings. Connect it to a Group, Row or Column like an
    /// element: when the scene is built, the container calls <see cref="Build"/> with the style resolved down to this
    /// position (base style, then every Group's style on the way), and shows the returned element in its place.
    /// <para>
    /// Build is called once per frame while the widget is part of a scene, after the patch was updated in that frame.
    /// Return the same element instance as long as nothing changed (element nodes do this by themselves), so the scene
    /// stays untouched. A returned stylable is built in turn with the same style, so a widget may hand over to another;
    /// one that returns itself or a cycle shows nothing. An exception is shown at the container node (at the Renderer
    /// for a widget linked to it) and the element of the last successful call stays.
    /// </para>
    /// </summary>
    [Category("Qt.Style")]
    public interface IStylable : IElement
    {
        /// <summary>Returns the element to show for the resolved style; null shows nothing.</summary>
        /// <param name="style">Resolved style at this position: every field is set (unset fields come from the base style).</param>
        IElement? Build(Style style);
    }
}
