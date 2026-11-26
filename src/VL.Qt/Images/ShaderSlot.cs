using System;
using VL.Qt.Images;

namespace VL.Qt
{
    /// <summary>
    /// One shader stage of a Shader node: owns the shader resource for the current path or GLSL code. A new source creates
    /// a new resource and queues the release of the old one (QtNative keeps drawing the old shader until the new one is
    /// Ready, the element switches ids first, see <see cref="ResourceClient.Release"/>); the same source never reloads.
    /// Loading is asynchronous, nothing here waits. Main thread only.
    /// </summary>
    internal sealed class ShaderSlot : IDisposable
    {
        /// <summary>Path prefix of a shader shipped with VL.Qt, resolved by QtNative (vlqt_shader_load).</summary>
        internal const string builtinShaderPrefix = "builtin:";

        /// <summary>
        /// Recognizes <c>builtin:&lt;name&gt;</c>, also after vvvv resolved a Path value against the document directory
        /// (<c>C:\doc\dir\builtin:wave</c>): then the last path segment carries the prefix.
        /// </summary>
        internal static bool TryGetBuiltinShader(string path, out string builtin)
        {
            var segment = path.AsSpan();
            var i = path.LastIndexOfAny(new[] { '\\', '/' });
            if (i >= 0)
                segment = segment.Slice(i + 1);
            if (segment.StartsWith(builtinShaderPrefix, StringComparison.Ordinal))
            {
                builtin = segment.ToString();
                return true;
            }
            builtin = path;
            return false;
        }

        private readonly ResourceClient? _client;
        private readonly int _stage;
        private readonly string _label;
        private string? _source;
        private bool _isCode;
        private QtResource? _resource;
        private string _createError = string.Empty;

        /// <param name="client">Creates and releases the shader resources, may be null: then <see cref="Id"/> stays 0 and
        /// <see cref="Error"/> reports it.</param>
        /// <param name="stage"><see cref="Protocol.ShaderStage"/>.</param>
        /// <param name="label">Prefix of the error text, e.g. "Fragment shader".</param>
        public ShaderSlot(ResourceClient? client, int stage, string label)
        {
            _client = client;
            _stage = stage;
            _label = label;
        }

        /// <summary>Resource id for the element, 0 = no path or the resource could not be created.</summary>
        public ulong Id => _resource?.Id ?? 0;

        /// <summary>The resource, null without path (tests, status).</summary>
        public QtResource? Resource => _resource;

        /// <summary>
        /// Why the shader is not (fully) usable ("Fragment shader: ..."): the load error, or for a Ready shader the warning of
        /// QtNative (a uniform the OpenGL backend never sets). Empty if it is loading, fine or not set. Cached, no
        /// allocation while unchanged.
        /// </summary>
        public string Error
        {
            get
            {
                var text = _createError.Length > 0 ? _createError
                    : _resource is { State: QtImageState.Failed } r ? r.Error
                    : _resource is { State: QtImageState.Ready } ready ? ready.Warning
                    : string.Empty;
                if (text.Length == 0)
                    return string.Empty;
                if (text != _errorSource)
                {
                    _errorSource = text;
                    _errorText = $"{_label}: {text}";
                }
                return _errorText;
            }
        }

        private string _errorSource = string.Empty, _errorText = string.Empty;

        /// <summary>
        /// Loads <paramref name="path"/> if set (not empty), otherwise bakes <paramref name="code"/> (full Qt GLSL,
        /// not empty/whitespace); only when the effective source (kind + text) differs from the last call. Both empty = no
        /// shader (id 0). While typing, a new code text waits until the running bake is settled (Ready or Failed), then
        /// the latest text of a later call starts; no pending state needed because the node calls this every frame.
        /// Returns the resource id.
        /// </summary>
        public ulong Update(string? path, string? code)
        {
            string? source;
            bool isCode;
            if (!string.IsNullOrEmpty(path))
            {
                source = path;
                isCode = false;
            }
            else if (!string.IsNullOrWhiteSpace(code))
            {
                source = code;
                isCode = true;
            }
            else
            {
                source = null;
                isCode = false;
            }

            if (isCode == _isCode && (ReferenceEquals(source, _source) || source == _source))
                return Id;

            // Throttle: the last started bake is still running, take the newest text in a later call
            if (isCode && _isCode && _resource is { State: QtImageState.Loading, IsReleased: false })
                return Id;

            _source = source;
            _isCode = isCode;
            _client?.Release(_resource);
            _resource = null;
            _createError = string.Empty;
            if (source is null)
                return 0;
            if (_client is null)
            {
                _createError = "No resource client";
                return 0;
            }
            try
            {
                var resource = _client.CreateShader();
                _resource = resource;
                if (isCode)
                    _client.LoadShaderSource(resource, source, _stage);
                else
                    _client.LoadShader(resource, source, _stage);
            }
            catch (InvalidOperationException e)
            {
                _createError = e.Message;
            }
            return Id;
        }

        public void Dispose()
        {
            _client?.Release(_resource);
            _resource = null;
            _source = null;
            _isCode = false;
        }
    }
}
