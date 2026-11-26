using System;
using VL.Qt.Images;
using VL.Qt.Nodes;

namespace VL.Qt
{
    /// <summary>
    /// The one rule for how the resource and file nodes (LoadImage, LoadImage (Async), LoadFont, ToQtImage,
    /// VideoSourceToQtImage, Snapshot, both FileWriters) report errors; they have no Error output. Every error goes to the
    /// log. A lasting failure (a load or upload that stays failed until an input changes) also marks the node as a warning
    /// until it is gone; a one-shot failure (a grab or a write) is only logged. A resource warning (e.g. 16 bit reduced to
    /// 8 bit) is logged and shown at the node until the resource changes. Without a <see cref="NodeErrors"/> nothing
    /// is reported.
    /// </summary>
    internal sealed class ResourceNodeReport : IDisposable
    {
        private readonly NodeErrors? _errors;
        private readonly bool _lasting;
        private readonly NodeMessage _error;
        private readonly NodeMessage _warning;

        /// <param name="errors">Store of the node, disposed with this report; null reports nothing.</param>
        /// <param name="lasting">True if an error stays until an input changes (marks the node), false for one-shot failures (log only).</param>
        public ResourceNodeReport(NodeErrors? errors, bool lasting)
        {
            _errors = errors;
            _lasting = lasting;
            _error = new NodeMessage(errors, "Resource");
            _warning = new NodeMessage(errors, "Resource Warning");
        }

        /// <summary>Reports the current error (empty if none) and the warning of <paramref name="resource"/>. Main thread, once per Update;
        /// no allocation while nothing changes.</summary>
        public void Update(QtResource? resource, string error)
        {
            if (_lasting)
                _error.Set(error);
            else
                _errors?.LogOnly(error);
            _warning.Set(resource?.Warning);
        }

        public void Dispose()
        {
            _error.Dispose();
            _warning.Dispose();
            _errors?.Dispose();
        }
    }
}
