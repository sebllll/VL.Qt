using System;
using System.Collections.Generic;
using System.Text;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Wire;

namespace VL.Qt.Styles
{
    /// <summary>Set of style states for setters, combinable with |. Default of the setters is <see cref="All"/>.</summary>
    [Flags]
    [Category("Qt.Style")]
    public enum StyleStates
    {
        None = 0,
        Normal = 1 << Protocol.StyleState.Normal,
        Hovered = 1 << Protocol.StyleState.Hovered,
        Pressed = 1 << Protocol.StyleState.Pressed,
        Disabled = 1 << Protocol.StyleState.Disabled,
        Checked = 1 << Protocol.StyleState.Checked,
        All = Normal | Hovered | Pressed | Disabled | Checked,
    }
}
