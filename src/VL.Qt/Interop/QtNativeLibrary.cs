using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VL.Qt.Interop
{
    /// <summary>
    /// Finds and loads QtNative (vlqt.dll / libvlqt.so) and makes the P/Invokes of <see cref="Native"/> resolve to it.
    /// The static constructor does the work once; <see cref="EnsureLoaded"/> forces it.
    /// </summary>
    internal static class QtNativeLibrary
    {
        /// <summary>Folder of vlqt.dll / libvlqt.so and the Qt tree.</summary>
        public static readonly string Directory;

        /// <summary>Full path of the loaded vlqt.dll / libvlqt.so.</summary>
        public static readonly string LibraryPath;

        /// <summary>Handle of the loaded vlqt.dll / libvlqt.so, for the DllImport resolver of the test support.</summary>
        public static readonly IntPtr Handle;

        /// <summary>File name of QtNative: vlqt.dll (Windows) or libvlqt.so (Linux).</summary>
        private static readonly string s_fileName = OperatingSystem.IsWindows() ? "vlqt.dll" : "libvlqt.so";

        static QtNativeLibrary()
        {
            Directory = FindDirectory();
            if (OperatingSystem.IsWindows())
            {
                // Qt DLLs are resolved from Directory, not from next to vvvv.exe.
                // Process-local PATH prepend instead of SetDefaultDllDirectories, which would drop PATH
                // from the DLL search of the whole process (other vvvv packages rely on it).
                // QML plugins in qml/** need this to find Qt6Quick*.dll in Directory.
                // Linux: libvlqt.so finds Qt via its RUNPATH ($ORIGIN, L2b/L3b); PATH plays no role there.
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (!path.StartsWith(Directory + ";", StringComparison.OrdinalIgnoreCase))
                    Environment.SetEnvironmentVariable("PATH", Directory + ";" + path);
            }
            LibraryPath = Path.Combine(Directory, s_fileName);
            Handle = NativeLibrary.Load(LibraryPath);
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly,
                (name, _, _) => name == Native.Lib ? Handle : IntPtr.Zero);
        }

        /// <summary>Forces the static constructor (DLL setup) to run.</summary>
        public static void EnsureLoaded() { }

        private static string FindDirectory()
        {
            var os = OperatingSystem.IsWindows() ? "win"
                : OperatingSystem.IsLinux() ? "linux"
                : throw new PlatformNotSupportedException($"VL.Qt: unsupported OS {RuntimeInformation.OSDescription}");
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                var a => throw new PlatformNotSupportedException($"VL.Qt: unsupported architecture {a}")
            };
            var rid = os + "-" + arch;
            var asmDir = Path.GetDirectoryName(typeof(Native).Assembly.Location)!;
            // Layouts (docs/decisions/qtnative-deployment.md): the assembly sits in lib/net10.0/ in the repo and the package.
            // The package keeps the Qt tree in qt/<rid>/, outside runtimes/<rid>/native, which NuGet would flatten.
            foreach (var candidate in new[] {
                Path.Combine(asmDir, "runtimes", rid, "native"),               // app output (SmokeTest, export)
                Path.Combine(asmDir, "..", "..", "qt", rid),                   // package (lib/net10.0/ + qt/<rid>/)
                Path.Combine(asmDir, "..", "..", "runtimes", rid, "native") }) // repo sources (lib/net10.0/ + runtimes/)
            {
                if (File.Exists(Path.Combine(candidate, s_fileName)))
                    return Path.GetFullPath(candidate);
            }
            throw new DllNotFoundException($"VL.Qt: {s_fileName} for {rid} not found relative to {asmDir}");
        }
    }
}
