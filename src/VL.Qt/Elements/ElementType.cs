namespace VL.Qt.Elements
{
    /// <summary>
    /// Element kinds. Values are the CREATE type ids of the command protocol
    /// (<see cref="Protocol.Type"/>); the compiler keeps both in sync. Group maps to <see cref="Item"/>.
    /// </summary>
    public enum ElementType : ushort
    {
        Item = Protocol.Type.Item,
        Rectangle = Protocol.Type.Rectangle,
        Text = Protocol.Type.Text,
        Image = Protocol.Type.Image,
        Row = Protocol.Type.Row,
        Column = Protocol.Type.Column,
        Video = Protocol.Type.Video,
        Button = Protocol.Type.Button,
        SpinBox = Protocol.Type.SpinBox,
        CheckBox = Protocol.Type.CheckBox,
        Switch = Protocol.Type.Switch,
        Slider = Protocol.Type.Slider,
        TextField = Protocol.Type.TextField,
        ShaderEffect = Protocol.Type.ShaderEffect,
        Qml = Protocol.Type.Qml,
    }
}
