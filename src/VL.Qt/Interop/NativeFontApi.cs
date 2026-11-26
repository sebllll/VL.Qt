using System;

namespace VL.Qt.Interop
{
    /// <summary>The font API of QtNative for the process-wide <see cref="QtApp"/>.</summary>
    internal sealed class NativeFontApi : IFontApi
    {
        private readonly QtApp _app;

        /// <summary>Creates the QGuiApplication on first use: QFontDatabase needs it, so a font loaded before any renderer works.</summary>
        public NativeFontApi() => _app = QtApp.Get();

        public bool IsQtThread => _app.IsQtThread;
        public Native.Result Add(string path, out ulong handle, out string families) => Native.FontAdd(path, out handle, out families);
        public Native.Result Release(ulong handle) => Native.vlqt_font_release(handle);
        public string LastError() => Native.LastError();
    }
}
