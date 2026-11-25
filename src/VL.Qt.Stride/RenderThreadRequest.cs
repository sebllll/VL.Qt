using System;
using System.Collections.Generic;

namespace VL.Qt.Stride
{
    /// <summary>
    /// The value of the pin Render Threads of one QtTexture or QtRenderer. QtNative has one pool of render threads per
    /// process: its size is the largest value of all live nodes, so 0 (main-thread mode) applies only when every node asks
    /// for it. QtNative applies a change to the scenes created afterwards; a larger value adds threads at once
    /// (OffscreenScene.SetRenderThreads). Main thread only, after the Qt app started.
    /// </summary>
    internal sealed class RenderThreadRequest : IDisposable
    {
        public const int MaxThreads = 16;

        private static readonly List<RenderThreadRequest> s_requests = new();
        private static int s_applied = -1;

        private int _count;
        private bool _disposed;

        public RenderThreadRequest(int count)
        {
            _count = Math.Clamp(count, 0, MaxThreads);
            s_requests.Add(this);
            Apply();
        }

        /// <summary>Sets the value of the node; no-op if unchanged. Values outside 0 ... 16 are clamped.</summary>
        public void Set(int count)
        {
            count = Math.Clamp(count, 0, MaxThreads);
            if (count == _count || _disposed)
                return;
            _count = count;
            Apply();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            s_requests.Remove(this);
            Apply();
        }

        // Without a node the last size stays: no scene is created until the next node asks again.
        private static void Apply()
        {
            if (s_requests.Count == 0)
                return;
            var max = 0;
            foreach (var r in s_requests)
                max = Math.Max(max, r._count);
            if (max == s_applied)
                return;
            // Only an argument out of range fails, which the clamp rules out.
            if (OffscreenScene.SetRenderThreads(max))
                s_applied = max;
        }
    }
}
