using System;
using VL.Qt.Wire;

namespace VL.Qt.Interop
{
    /// <summary>
    /// Compares the ABI version of vlqt.dll with the one of this VL.Qt.dll (<see cref="Protocol.AbiVersion"/>, PROTO-01).
    /// A mismatch (a stale native build, a package mixed from two builds) is refused up front: later it shows up as frames
    /// that fail in a loop or as a property that is set silently on the wrong target.
    /// </summary>
    internal static class AbiCheck
    {
        /// <summary>Message template for the log; <see cref="Describe"/> fills the same placeholders.</summary>
        internal const string Template = "vlqt.dll {Path} has ABI {Actual}, VL.Qt.dll expects {Expected}: rebuild QtNative or reinstall the package";

        /// <summary>The text of <see cref="Template"/>, or null if <paramref name="actual"/> equals <paramref name="expected"/>.</summary>
        internal static string? Describe(int? actual, int expected, string path)
            => actual == expected ? null : $"vlqt.dll {path} has ABI {ActualText(actual)}, VL.Qt.dll expects {expected}: rebuild QtNative or reinstall the package";

        /// <summary>The actual version for the message; a null (the export is missing) is a library older than the check.</summary>
        internal static string ActualText(int? actual) => actual?.ToString() ?? "none (no vlqt_app_get_abi_version)";
    }
}
