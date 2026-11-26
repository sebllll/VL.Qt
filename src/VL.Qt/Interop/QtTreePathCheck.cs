using System;
using System.IO;

namespace VL.Qt.Interop
{
    /// <summary>
    /// Windows MAX_PATH diagnostic of the Qt tree: Qt does not load plugins from paths that reach the limit and reports
    /// them as "not found" although the file is there.
    /// </summary>
    internal static class QtTreePathCheck
    {
        /// <summary>Windows MAX_PATH (incl. the terminating NUL): Qt does not load plugins from longer paths.</summary>
        public const int windowsMaxPath = 260;

        private static readonly Lazy<(int Length, string Path)> s_longestPath = new(FindLongestPath);

        /// <summary>Longest file path in the Qt tree under <see cref="QtNativeLibrary.Directory"/> (computed once, enumerates the tree).</summary>
        public static (int Length, string Path) LongestPath => s_longestPath.Value;

        /// <summary>
        /// True on Windows when a file in the Qt tree reaches MAX_PATH. Qt then fails to load such plugins and reports them
        /// as "not found" (e.g. module "QtQuick.Controls" plugin ... not found), although the file is there.
        /// </summary>
        public static bool PathsTooLong => OperatingSystem.IsWindows() && LongestPath.Length >= windowsMaxPath;

        /// <summary>Hint for messages about Qt plugins that were not found while <see cref="PathsTooLong"/>.</summary>
        public static string PathTooLongHint
            => $"The VL.Qt files are installed under a path that is too long for Windows ({LongestPath.Length} >= {windowsMaxPath} characters). Move the package or exported app to a shorter path.";

        private static (int Length, string Path) FindLongestPath()
        {
            var longest = (Length: 0, Path: "");
            try
            {
                foreach (var file in Directory.EnumerateFiles(QtNativeLibrary.Directory, "*", SearchOption.AllDirectories))
                {
                    if (file.Length > longest.Length)
                        longest = (file.Length, file);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // diagnostics only
            }
            return longest;
        }
    }
}
