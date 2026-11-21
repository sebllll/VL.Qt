using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace VL.Qt.NodeFactories
{
    /// <summary>Middle click / Definition > Open of a generated node: its files in the default editor, like VL.Stride's shader nodes.</summary>
    internal static class FileEditor
    {
        /// <summary>
        /// Opens every file of <paramref name="paths"/> in the default editor and returns true if one of them opened; a file
        /// that does not open is logged as "Could not open <paramref name="kind"/> file".
        /// </summary>
        public static bool Open(ILogger logger, string kind, params string[] paths)
        {
            var opened = false;
            foreach (var path in paths)
                opened |= Open(logger, kind, path);
            return opened;
        }

        private static bool Open(ILogger logger, string kind, string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not open {Kind} file {Path}", kind, path);
                return false;
            }
        }
    }
}
