using System;
using VL.Qt.Elements;
using VL.Qt.Reconciliation;

namespace VL.Qt
{
    /// <summary>
    /// Resync after a failed <c>vlqt_scene_apply</c> or a reconcile that threw. QtNative aborts a batch at the first bad
    /// command, and an exception leaves the mirror half updated, so mirror and QtNative disagree. Recovery is always
    /// complete: one RESET wipes the window's scene in QtNative, the mirror is dropped without commands
    /// (<see cref="Reconciler.Discard"/>) and the next <c>Update</c> creates the scene from scratch, i.e. one frame later.
    /// (QtNative reports the failing id, vlqt_scene_get_apply_error; it is used to show the error at the element node,
    /// not for a partial rebuild.)
    /// <para>
    /// No endless loop: if the rebuild fails as well (the first attempt after a resync), the controller gives up and
    /// leaves the scene empty; <see cref="ShouldReconcile"/> stays false until the input changes (new reference), which
    /// allows exactly one new attempt, and not before a pause of 2 frames that doubles with every give-up up to 64 frames
    /// (animated input is new every frame) and starts at 2 again after a successful apply. The error stays visible
    /// meanwhile (the caller keeps it).
    /// </para>
    /// Main thread only; one per window.
    /// </summary>
    internal sealed class ResyncController
    {
        // Frames between give-up and the next attempt: from firstPause, doubled per give-up, at most maxPause.
        private const int firstPause = 2;
        private const int maxPause = 64;

        private readonly Reconciler _reconciler;
        private readonly Func<int> _resetInQtNative;
        private Element? _lastInput;
        private bool _justResynced;
        private bool _gaveUp;
        private Element? _gaveUpInput;
        private int _pause = firstPause; // pause after the next give-up
        private int _wait;               // frames left of the current pause

        /// <param name="reconciler">Mirror of the window.</param>
        /// <param name="resetInQtNative">Sends one RESET to QtNative and returns the apply result (errors are ignored here).</param>
        public ResyncController(Reconciler reconciler, Func<int> resetInQtNative)
        {
            _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
            _resetInQtNative = resetInQtNative ?? throw new ArgumentNullException(nameof(resetInQtNative));
        }

        /// <summary>Number of resyncs performed so far.</summary>
        public int Resyncs { get; private set; }

        /// <summary>True after giving up: the scene is empty until the input changes.</summary>
        public bool GaveUp => _gaveUp;

        /// <summary>
        /// Call once per frame before reconciling <paramref name="input"/> in Update. False = skip the reconcile (gave up,
        /// and the pause is not over or the input is still the one that failed). A changed input after the pause counts as
        /// a fresh attempt.
        /// </summary>
        public bool ShouldReconcile(Element? input)
        {
            if (_gaveUp)
            {
                if (_wait > 0)
                    _wait--;
                if (_wait > 0 || ReferenceEquals(input, _gaveUpInput))
                    return false;
                _gaveUp = false;
                _gaveUpInput = null;
                _justResynced = true;
            }
            _lastInput = input;
            return true;
        }

        /// <summary>
        /// Call with every apply result. rc != 0: resyncs (<see cref="OnReconcileFailed"/>) and returns true (the caller
        /// must also forget what it sent besides the scene, e.g. the clear color). rc == 0 ends the "first attempt after a
        /// resync" state and, unless the controller gave up, the doubling of the pause.
        /// </summary>
        public bool OnApplyResult(int rc)
        {
            if (rc == 0)
            {
                _justResynced = false;
                if (!_gaveUp)
                    _pause = firstPause;
                return false;
            }
            Resync();
            return true;
        }

        /// <summary>
        /// Call when the reconcile of the input passed to <see cref="ShouldReconcile"/> threw, after dropping the commands it
        /// wrote: wipes QtNative and drops the mirror like a failed apply (the caller must also forget what it sent besides
        /// the scene, e.g. the clear color).
        /// </summary>
        public void OnReconcileFailed() => Resync();

        private void Resync()
        {
            _resetInQtNative();
            _reconciler.Discard();
            Resyncs++;

            if (_justResynced)
            {
                _gaveUp = true;
                _gaveUpInput = _lastInput;
                _justResynced = false;
                _wait = _pause;
                _pause = Math.Min(_pause * 2, maxPause);
            }
            else
                _justResynced = true;
        }
    }
}
