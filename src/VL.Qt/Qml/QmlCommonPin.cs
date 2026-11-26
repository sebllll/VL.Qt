using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt.Elements;

namespace VL.Qt.Qml
{
    /// <summary>One common pin of <c>QmlElement</c> and of every generated QML node.</summary>
    /// <param name="Name">Pin name.</param>
    /// <param name="Type">Pin type.</param>
    /// <param name="Default">Default value, null for none.</param>
    /// <param name="IsVisible">False for an optional pin.</param>
    /// <param name="Summary">Pin help.</param>
    internal sealed record QmlCommonPin(string Name, Type Type, object? Default, bool IsVisible, string Summary);
}
