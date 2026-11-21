using System;
using VL.Core;

namespace VL.Qt.NodeFactories
{
    /// <summary>Pin description with help text and visibility (optional pins), like VL.Stride's EffectPinDescription.</summary>
    internal sealed class FilePinDescription : IVLPinDescription, IInfo, IVLPinDescriptionWithVisibility
    {
        public FilePinDescription(string name, Type type, object? defaultValue, string? summary, bool isVisible = true)
        {
            Name = name;
            Type = type;
            DefaultValue = defaultValue;
            Summary = summary;
            IsVisible = isVisible;
        }

        public string Name { get; }

        public Type Type { get; }

        public object? DefaultValue { get; }

        public string? Summary { get; }

        public string? Remarks => null;

        public bool IsVisible { get; }
    }
}
