using VL.Qt.Wire;
namespace VL.Qt.Elements
{
    /// <summary>
    /// Element kinds. Values are the CREATE type ids of the command protocol
    /// (<see cref="Protocol.ElementType"/>); the compiler keeps both in sync. <see cref="Item"/> is the protocol's Group.
    /// </summary>
    public enum ElementType : ushort
    {
        Item = Protocol.ElementType.Group,
        Rectangle = Protocol.ElementType.Rectangle,
        Text = Protocol.ElementType.Text,
        Image = Protocol.ElementType.Image,
        Row = Protocol.ElementType.Row,
        Column = Protocol.ElementType.Column,
        Video = Protocol.ElementType.Video,
        Button = Protocol.ElementType.Button,
        SpinBox = Protocol.ElementType.SpinBox,
        CheckBox = Protocol.ElementType.CheckBox,
        Switch = Protocol.ElementType.Switch,
        Slider = Protocol.ElementType.Slider,
        TextField = Protocol.ElementType.TextField,
        Shader = Protocol.ElementType.Shader,
        Qml = Protocol.ElementType.Qml,
        Shape = Protocol.ElementType.Shape,
    }
}
