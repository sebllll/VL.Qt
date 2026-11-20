using System;
using VL.Core;
using VL.Core.Import;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Loads a font file (TTF, OTF, TTC) for this app without installing it and returns its family name for the FontFamily
    /// pin of Text or Style. Several LoadFont nodes with the same file share one registration; the font is removed when
    /// the last of them is disposed or changes its path.
    /// </summary>
    [ProcessNode(Name = "LoadFont", Category = Categories.Qt)]
    public sealed class LoadFontNode : IDisposable
    {
        private readonly NodeErrors _errors;
        private readonly QtApp _app;
        private string? _path;
        private ulong _handle;
        private string _family = string.Empty;
        private string _error = string.Empty;

        public LoadFontNode(NodeContext nodeContext)
        {
            _errors = new NodeErrors(nodeContext);
            // Creates the QGuiApplication on first use: QFontDatabase needs it, so a LoadFont before any renderer works.
            _app = QtApp.Get(nodeContext.AppHost.LoggerFactory);
        }

        /// <summary>Registers the font file when Path changes.</summary>
        /// <param name="path">Font file (TTF, OTF, TTC). Empty = no font.</param>
        /// <param name="error">Why the file could not be registered, empty otherwise.</param>
        /// <returns>The family name of the font (the first one of a font collection), empty when nothing is registered.</returns>
        [return: Pin(Name = "Family")]
        public string Update(Path path, out string error)
        {
            var p = path?.Value ?? string.Empty;
            if (p != _path)
                Load(p);
            error = _error;
            return _family;
        }

        private void Load(string path)
        {
            _path = path;
            var previous = _handle;
            _handle = 0;
            _family = string.Empty;
            _error = string.Empty;
            if (path.Length > 0)
            {
                if (!_app.IsQtThread)
                    _error = "LoadFont must run on the thread of the Qt app (the vvvv main thread).";
                // Add before releasing the previous handle: the same file stays registered when only the path spelling changed.
                else if (Native.FontAdd(path, out var handle, out var families) == 0)
                {
                    _handle = handle;
                    var newline = families.IndexOf('\n');
                    _family = newline < 0 ? families : families[..newline];
                }
                else
                    _error = Native.LastError();
            }
            Release(previous);
            if (_error.Length > 0)
                _errors.Warn(_error, "LoadFont");
            else
                _errors.Clear("LoadFont");
        }

        private void Release(ulong handle)
        {
            if (handle != 0 && Native.vlqt_font_release(handle) != 0)
                _errors.LogOnly(Native.LastError());
        }

        public void Dispose()
        {
            Release(_handle);
            _handle = 0;
            _errors.Dispose();
        }
    }
}
