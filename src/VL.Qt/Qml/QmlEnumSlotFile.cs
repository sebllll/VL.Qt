using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VL.Qt.Qml
{
    /// <summary>
    /// The file <see cref="FileName"/> in a qml folder: one line <c>Gauge.Mode = 7</c> per QML enum, the number of its pool type
    /// (QmlEnum07). Text, so that a diff and a merge read well; <c>#</c> starts a comment line. See <see cref="QmlEnumPool"/>.
    /// </summary>
    internal static class QmlEnumSlotFile
    {
        /// <summary>Name of the file in the qml folder. Not a .qml file, so the node factory and its watchers ignore it.</summary>
        public const string FileName = "vlqt-enum-slots.txt";

        private static readonly string[] s_header =
        {
            "# Pool types of the QML enums of this folder, written by VL.Qt: \"Gauge.Mode = 7\" gives the pins of Gauge.Mode the type QmlEnum07.",
            "# Patches save these types: keep this file with the QML files (version control, package, export). Delete a line to free a type.",
        };

        /// <summary>Comparer of folder paths: the file system of Windows ignores case.</summary>
        public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>
        /// The qml folder of a QML file: the nearest folder named qml above it (the node factory's folder next to a document or in a
        /// package, also next to an exported app), else the folder of the file. Full path without a trailing separator.
        /// </summary>
        public static string RootOf(string qmlFilePath)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(qmlFilePath))!;
            for (var d = directory; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            {
                if (string.Equals(Path.GetFileName(d), QmlNodeFactory.FolderName, StringComparison.OrdinalIgnoreCase))
                    return Path.TrimEndingDirectorySeparator(d);
            }
            return Path.TrimEndingDirectorySeparator(directory);
        }

        /// <summary>
        /// Returns true if the qml folder belongs to an installed package: a folder above it holds .nupkg.metadata (NuGet cache, the
        /// packages of vvvv) or a .nupkg with a .nuspec (a package installed with nuget install). Any folder above, as help patches keep
        /// their qml folder deeper in the package (help/Qt/qml). Such a folder is never written; its package ships the file.
        /// </summary>
        public static bool IsInstalledPackage(string root)
        {
            try
            {
                for (var d = Path.GetDirectoryName(root); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                {
                    if (File.Exists(Path.Combine(d, ".nupkg.metadata"))
                        || (Directory.EnumerateFiles(d, "*.nupkg").Any() && Directory.EnumerateFiles(d, "*.nuspec").Any()))
                        return true;
                }
                return false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// The lines of the file in file order, without the ones that are not valid: no number from 1 to <paramref name="slotCount"/>,
        /// a key or a number a line above already has. Empty if the file does not exist; null if it cannot be read.
        /// </summary>
        public static List<(string Key, int Number)>? Read(string path, int slotCount)
        {
            string[] lines;
            try
            {
                if (!File.Exists(path))
                    return new List<(string, int)>();
                lines = File.ReadAllLines(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            return Parse(lines, slotCount);
        }

        /// <summary>See <see cref="Read"/>.</summary>
        internal static List<(string Key, int Number)> Parse(IEnumerable<string> lines, int slotCount)
        {
            var result = new List<(string, int)>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var numbers = new HashSet<int>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;
                var key = line.Substring(0, equals).Trim();
                if (key.Length == 0
                    || !int.TryParse(line.AsSpan(equals + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    || number < 1 || number > slotCount
                    || !keys.Add(key) || !numbers.Add(number))
                    continue;
                result.Add((key, number));
            }
            return result;
        }

        /// <summary>The text of the file: header, then one line per key in ordinal order; LF, as the repository keeps text.</summary>
        internal static string Format(IReadOnlyDictionary<string, int> map)
        {
            var text = new StringBuilder();
            foreach (var line in s_header)
                text.Append(line).Append('\n');
            foreach (var (key, number) in map.OrderBy(static p => p.Key, StringComparer.Ordinal))
                text.Append(key).Append(" = ").Append(number.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return text.ToString();
        }

        /// <summary>
        /// Writes <paramref name="map"/> over a temporary file, so that a reader never sees half a file. Returns false if the folder
        /// or the file cannot be written.
        /// </summary>
        public static bool TryWrite(string path, IReadOnlyDictionary<string, int> map)
        {
            var temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, Format(map), new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception d) when (d is IOException or UnauthorizedAccessException)
                {
                    // Nothing to clean up, or a folder that cannot be written at all.
                }
                return false;
            }
        }
    }
}
