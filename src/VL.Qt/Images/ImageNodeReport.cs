using System;

namespace VL.Qt
{
    /// <summary>
    /// Errors and warnings of the image nodes (#362): the Error output is logged once per distinct text (the node shows
    /// it at its output, not as a message); a resource warning (e.g. 16 bit reduced to 8 bit) is logged and shown at the
    /// node until the image changes. Without a <see cref="NodeErrors"/> (unit tests) nothing is reported.
    /// </summary>
    internal sealed class ImageNodeReport : IDisposable
    {
        private const string phase = "Image";

        private readonly NodeErrors? _errors;
        private string _error = string.Empty, _warning = string.Empty;

        public ImageNodeReport(NodeErrors? errors) => _errors = errors;

        /// <summary>Main thread, once per Update. No allocation while nothing changes.</summary>
        public void Update(ImageResource? resource, string error)
        {
            if (!ReferenceEquals(error, _error) && error != _error)
            {
                _error = error;
                _errors?.LogOnly(error);
            }
            var warning = resource?.Warning ?? string.Empty;
            if (!ReferenceEquals(warning, _warning) && warning != _warning)
            {
                _warning = warning;
                if (warning.Length == 0)
                    _errors?.Clear(phase);
                else
                    _errors?.Warn(warning, phase);
            }
        }

        public void Dispose() => _errors?.Dispose();
    }
}
