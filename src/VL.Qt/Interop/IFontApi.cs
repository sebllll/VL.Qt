using System;

namespace VL.Qt.Interop
{
    /// <summary>The app font C-API of QtNative (vlqt_font_*) with the thread check of the Qt app; a fake in unit tests, which have no Qt.</summary>
    internal interface IFontApi
    {
        /// <summary>Whether the calling thread is the one of the Qt app; the font calls work there only.</summary>
        bool IsQtThread { get; }

        /// <summary>Registers a font file (or shares its registration) and takes one reference; see <see cref="Native.FontAdd"/>.</summary>
        Native.Result Add(string path, out ulong handle, out string families);

        /// <summary>Drops one reference of a font handle.</summary>
        Native.Result Release(ulong handle);

        /// <summary>The reason of the last failed call.</summary>
        string LastError();
    }
}
