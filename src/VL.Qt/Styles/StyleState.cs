using System;
using System.Collections.Generic;
using System.Text;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Wire;

namespace VL.Qt.Styles
{
    /// <summary>One style state: used by getters and by <see cref="StateValues{T}.Get"/>.</summary>
    [Category("Qt.Style")]
    public enum StyleState : byte
    {
        Normal = Protocol.StyleState.Normal,
        Hovered = Protocol.StyleState.Hovered,
        Pressed = Protocol.StyleState.Pressed,
        Disabled = Protocol.StyleState.Disabled,
        Checked = Protocol.StyleState.Checked,
    }
}
