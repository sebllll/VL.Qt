using System;
using VL.Lib.Basics.Imaging;
using VL.Qt.Elements;
using VL.Qt.Reconciliation;

namespace VL.Qt.Images
{
    /// <summary>
    /// The grab of Snapshot and FileWriter (Element): locates the element in its renderer, starts the asynchronous snapshot
    /// into one resource per node and says when it settled. Main thread.
    /// </summary>
    internal sealed class SnapshotGrab : IDisposable
    {
        /// <summary>Error returned when the element is shown in no renderer.</summary>
        internal const string NotShownError = "Element is not shown in a renderer";

        private readonly ResourceClient _client;
        private readonly OccurrenceRegistry _scene;
        private bool _settled;
        // Grabs started and not settled yet: QtNative does not cancel a grab that a newer one supersedes, each settles on its own.
        private int _outstanding;

        public SnapshotGrab(ResourceClient client, OccurrenceRegistry scene)
        {
            _client = client;
            _scene = scene;
        }

        /// <summary>The snapshot resource, null until the first <see cref="Start"/> got that far.</summary>
        public QtResource? Resource { get; private set; }

        /// <summary>
        /// Starts a snapshot of <paramref name="element"/> (a stylable: the element of its last Build); returns the error at
        /// once (foreign IElement, not shown, refused by QtNative), empty on success.
        /// </summary>
        public string Start(IElement? element, float scale)
        {
            if (ElementConversion.IsForeign(element))
                return ElementConversion.ForeignWarning(element!.GetType());
            if (ElementConversion.Shown(ElementConversion.ToElement(element)) is not { } shown || !_scene.TryLocate(shown, out var window, out var elementId))
                return NotShownError;
            try
            {
                if (Resource is null)
                {
                    Resource = _client.CreateSnapshot();
                    Resource.Settled = _ =>
                    {
                        // Only the newest grab counts: the settle of a superseded one is not the element the caller wants.
                        if (_outstanding > 0)
                            _outstanding--;
                        if (_outstanding == 0)
                            _settled = true;
                    };
                }
                // Counted before the call: a refused start settles too.
                _outstanding++;
                _client.Snapshot(Resource, window, elementId, scale);
                return string.Empty;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (_outstanding > 0)
                    _outstanding--;
                return e.Message;
            }
        }

        /// <summary>True once after the newest snapshot settled (finished or failed); the settles of superseded ones are skipped.</summary>
        public bool TakeSettled()
        {
            var settled = _settled;
            _settled = false;
            return settled;
        }

        /// <summary>The finished snapshot as an own copy; null if the resource is not Ready or has none.</summary>
        public IImage? Read() => Resource is { State: QtImageState.Ready } ? _client.ReadSnapshot(Resource) : null;

        public void Dispose()
        {
            _client.Release(Resource);
            Resource = null;
        }
    }
}
