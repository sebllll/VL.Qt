using System;
using VL.Qt.Elements;

namespace VL.Qt
{
    /// <summary>
    /// Resync after a failed <c>vlqt_scene_apply</c>. QtNative aborts a batch at the first bad command, so
    /// the reconciler's mirror is ahead of QtNative. Recovery is always complete: one RESET wipes the window's
    /// scene in QtNative, the mirror is dropped without commands (<see cref="Reconciler.Discard"/>) and the
    /// next <c>Update</c> creates the scene from scratch, i.e. one frame later. (QtNative reports the failing id,
    /// vlqt_scene_get_apply_error; it is used to show the error at the element node, not for a partial rebuild.)
    /// <para>
    /// No endless loop: if the rebuild fails as well (first apply after a resync), the controller gives up and
    /// leaves the scene empty; <see cref="ShouldReconcile"/> stays false until the input changes (new reference),
    /// which allows exactly one new attempt. The error stays visible meanwhile (the caller keeps it).
    /// </para>
    /// Main thread only; one per window.
    /// </summary>
    internal sealed class ResyncController
    {
        private readonly Reconciler _reconciler;
        private readonly Func<int> _resetInQtNative;
        private Element? _lastInput;
        private bool _justResynced;
        private bool _gaveUp;
        private Element? _gaveUpInput;

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
        /// Call before reconciling <paramref name="input"/> in Update. False = skip the reconcile (gave up and the
        /// input is still the one that failed). A changed input ends the pause and counts as a fresh attempt.
        /// </summary>
        public bool ShouldReconcile(Element? input)
        {
            if (_gaveUp)
            {
                if (ReferenceEquals(input, _gaveUpInput))
                    return false;
                _gaveUp = false;
                _gaveUpInput = null;
                _justResynced = true;
            }
            _lastInput = input;
            return true;
        }

        /// <summary>
        /// Call with every apply result. rc != 0: wipes QtNative, drops the mirror and returns true (the caller
        /// must also forget what it sent besides the scene, e.g. the clear color). rc == 0 ends the "first apply
        /// after a resync" state.
        /// </summary>
        public bool OnApplyResult(int rc)
        {
            if (rc == 0)
            {
                _justResynced = false;
                return false;
            }

            _resetInQtNative();
            _reconciler.Discard();
            Resyncs++;

            if (_justResynced)
            {
                _gaveUp = true;
                _gaveUpInput = _lastInput;
                _justResynced = false;
            }
            else
                _justResynced = true;
            return true;
        }
    }
}
