using VL.Qt.Elements;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// The one list of element classes that take part in the style (not to be confused with <see cref="Styles.IStylable"/>,
    /// the self-made widget). A new widget class is added to <see cref="IsWidget"/>; <c>StylableElementsTests</c> fails
    /// for an element class that is in neither list of the test.
    /// </summary>
    internal static class StylableElements
    {
        /// <summary>
        /// Elements that resolve a style: a <see cref="Group"/> merges its own style over the inherited one and passes
        /// the result on; a widget does so and also sends it to QtNative. Every other element passes the inherited style on.
        /// </summary>
        public static bool ResolvesStyle(Element element) => element is Group || IsWidget(element);

        /// <summary>Widgets (Qt Quick Controls): the resolved style is sent to QtNative as SET_STYLE.</summary>
        public static bool IsWidget(Element element) => element is Button or SpinBox or CheckBox or Switch or Slider or TextField;
    }
}
