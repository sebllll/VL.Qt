using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Qml
{
    /// <summary>
    /// The properties of built-in Qt Quick types that a <c>property alias</c> of the root can point to and get a pin
    /// (docs/user-qml.md, "Aliases"): their QML type and the value Qt gives them when the file sets nothing. A small, fixed table: the
    /// parser runs without a Qt engine, so it cannot ask Qt for the type of any other property.
    /// </summary>
    internal static class QmlBuiltinProperties
    {
        private static readonly Dictionary<string, (string Type, object Default)> s_item = new(StringComparer.Ordinal)
        {
            ["x"] = ("real", 0f),
            ["y"] = ("real", 0f),
            ["z"] = ("real", 0f),
            ["width"] = ("real", 0f),
            ["height"] = ("real", 0f),
            ["opacity"] = ("real", 1f),
            ["rotation"] = ("real", 0f),
            ["scale"] = ("real", 1f),
            ["visible"] = ("bool", true),
            ["enabled"] = ("bool", true),
        };

        private static readonly Dictionary<string, (string Type, object Default)> s_rectangle = new(StringComparer.Ordinal)
        {
            ["color"] = ("color", new Color4(1f, 1f, 1f, 1f)),
            ["radius"] = ("real", 0f),
        };

        private static readonly Dictionary<string, (string Type, object Default)> s_text = new(StringComparer.Ordinal)
        {
            ["text"] = ("string", string.Empty),
            ["color"] = ("color", new Color4(0f, 0f, 0f, 1f)),
        };

        private static readonly Dictionary<string, (string Type, object Default)> s_image = new(StringComparer.Ordinal)
        {
            ["source"] = ("url", new Path(string.Empty)),
        };

        private static readonly Dictionary<string, (string Type, object Default)> s_none = new(StringComparer.Ordinal);

        // Types derived from Item; their own properties come before those of Item.
        private static readonly Dictionary<string, Dictionary<string, (string Type, object Default)>> s_items = new(StringComparer.Ordinal)
        {
            ["Item"] = s_none,
            ["Rectangle"] = s_rectangle,
            ["Text"] = s_text,
            ["TextInput"] = s_text,
            ["TextEdit"] = s_text,
            ["Image"] = s_image,
            ["AnimatedImage"] = s_image,
            ["BorderImage"] = s_image,
            ["Row"] = s_none,
            ["Column"] = s_none,
            ["Grid"] = s_none,
            ["Flow"] = s_none,
            ["MouseArea"] = s_none,
            ["Flickable"] = s_none,
            ["ListView"] = s_none,
            ["GridView"] = s_none,
            ["Loader"] = s_none,
            ["Canvas"] = s_none,
            ["ShaderEffect"] = s_none,
        };

        /// <summary>Returns true if the built-in <paramref name="type"/> has <paramref name="property"/> in the table.</summary>
        /// <param name="type">Type name as written in the file ("Text").</param>
        /// <param name="property">Property name ("text").</param>
        /// <param name="qmlType">Its QML type ("string").</param>
        /// <param name="defaultValue">The value Qt gives it, of the pin type of <paramref name="qmlType"/>.</param>
        public static bool TryGet(string type, string property, out string qmlType, out object defaultValue)
        {
            if (s_items.TryGetValue(type, out var own) && (own.TryGetValue(property, out var p) || s_item.TryGetValue(property, out p)))
            {
                (qmlType, defaultValue) = p;
                return true;
            }
            qmlType = string.Empty;
            defaultValue = string.Empty;
            return false;
        }
    }
}
