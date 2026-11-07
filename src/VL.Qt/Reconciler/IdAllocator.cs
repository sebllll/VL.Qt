using System;
using System.Collections.Generic;

namespace VL.Qt
{
    /// <summary>
    /// Hands out u32 object ids for one window. 0 is reserved for the window root and never returned.
    /// Freed ids are reused (LIFO freelist) so ids stay small and QtNative's id table stays dense.
    /// Allocation-free once the freelist and the liveness table have reached their working size.
    /// Not thread-safe; one allocator per window, used from the main thread.
    /// </summary>
    internal sealed class IdAllocator
    {
        /// <summary>The window's root object, never allocated or freed.</summary>
        public const uint Root = 0;

        readonly Stack<uint> _free = new();
        bool[] _live = new bool[64];
        uint _next = 1;
        int _count;

        /// <summary>Number of ids currently in use.</summary>
        public int Count => _count;

        /// <summary>Highest id ever handed out since construction or <see cref="Reset"/> (0 = none). Every id in 1..HighWater may exist in QtNative.</summary>
        public uint HighWater => _next - 1;

        /// <summary>Forgets all ids (fresh state, ids start at 1 again). Only for a full resync after QtNative was wiped.</summary>
        public void Reset()
        {
            _free.Clear();
            Array.Clear(_live);
            _next = 1;
            _count = 0;
        }

        /// <summary>Returns a fresh id (never 0); prefers the most recently freed one.</summary>
        public uint Allocate()
        {
            if (!_free.TryPop(out uint id))
            {
                if (_next >= (uint)Array.MaxLength)
                    throw new InvalidOperationException("VL.Qt: object ids exhausted.");
                id = _next++;
                if (id >= (uint)_live.Length)
                    Array.Resize(ref _live, (int)Math.Min((long)_live.Length * 2, Array.MaxLength));
            }
            _live[id] = true;
            _count++;
            return id;
        }

        /// <summary>Returns an id to the freelist. Throws for 0, unknown or already freed ids.</summary>
        public void Free(uint id)
        {
            if (!IsLive(id))
                throw new ArgumentException($"VL.Qt: id {id} is not allocated.", nameof(id));
            _live[id] = false;
            _count--;
            _free.Push(id);
        }

        /// <summary>Whether the id is currently allocated (0 never is).</summary>
        public bool IsLive(uint id) => id != Root && id < (uint)_live.Length && _live[id];
    }
}
