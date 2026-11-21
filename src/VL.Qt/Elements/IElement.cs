namespace VL.Qt.Elements
{
    /// <summary>
    /// An element of the scene: the output of every element node and what every element input takes (children of
    /// Group, Row and Column, Renderer, Labeled, Snapshot, the effect and shader inputs). A self-made widget implements
    /// <see cref="Styles.IStylable"/> and builds its element from the style it inherits. Other implementations are
    /// ignored with a warning at the node.
    /// </summary>
    public interface IElement
    {
    }
}
