using System;
using System.Linq;

namespace VL.Qt.NodeFactories
{
    /// <summary>Node name and variant of a generated node from its file name, like VL.Stride's shader nodes.</summary>
    internal static class FileNodeNames
    {
        /// <summary>Returns the node name and variant of a file name without extension: "Glitch_RGB" → ("Glitch", "RGB").</summary>
        /// <remarks>
        /// Further parts are joined with spaces ("Levels_Clamp_Both" → ("Levels", "Clamp Both")); an empty name part keeps the
        /// whole name.
        /// </remarks>
        public static (string Name, string? Version) Split(string fileNameWithoutExtension)
        {
            var name = fileNameWithoutExtension;
            var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || name.StartsWith('_'))
                return (name, null);
            var version = string.Join(' ', parts.Skip(1));
            return (parts[0], version.Length > 0 ? version : null);
        }

        /// <summary>Returns the name as vvvv shows it: "Glitch (RGB)", or just "Glitch" without variant.</summary>
        public static string FullName(string name, string? version) => version is null ? name : $"{name} ({version})";
    }
}
