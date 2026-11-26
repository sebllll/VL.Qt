using System;
using VL.Core;
using VL.Core.Import;
using Path = VL.Lib.IO.Path;
using VL.Qt.Interop;

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
        private readonly ResourceNodeReport _report;
        private readonly IFontApi _api;
        private string? _path;
        private ulong _handle;
        private string _family = string.Empty;
        private string _error = string.Empty;

        public LoadFontNode(NodeContext nodeContext)
            : this(new NativeFontApi(), new NodeErrors(nodeContext))
        {
        }

        /// <summary>A node on the given font API and message store, in place of the lookup through the <see cref="NodeContext"/>.</summary>
        internal LoadFontNode(IFontApi api, NodeErrors errors)
        {
            _api = api;
            _errors = errors;
            _report = new ResourceNodeReport(_errors, lasting: true);
        }

        /// <summary>Registers the font file when Path changes.</summary>
        /// <param name="path">Font file (TTF, OTF, TTC). Empty = no font.</param>
        /// <returns>The family name of the font (the first one of a font collection), empty when nothing is registered. A file that cannot
        /// be registered is shown at the node and logged.</returns>
        [return: Pin(Name = "Family")]
        public string Update(Path path)
        {
            var p = path?.Value ?? string.Empty;
            if (p != _path)
                Load(p);
            _report.Update(null, _error);
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
                if (!_api.IsQtThread)
                    _error = "LoadFont must run on the thread of the Qt app (the vvvv main thread).";
                // Add before releasing the previous handle: the same file stays registered when only the path spelling changed.
                else if (_api.Add(path, out var handle, out var families) == Native.Result.Ok)
                {
                    _handle = handle;
                    var newline = families.IndexOf('\n');
                    _family = newline < 0 ? families : families[..newline];
                }
                else
                    _error = _api.LastError();
            }
            Release(previous);
        }

        private void Release(ulong handle)
        {
            if (handle != 0 && _api.Release(handle) != Native.Result.Ok)
                _errors.LogOnly(_api.LastError());
        }

        public void Dispose()
        {
            Release(_handle);
            _handle = 0;
            _report.Dispose();
        }
    }
}
