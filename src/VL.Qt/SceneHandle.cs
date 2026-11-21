using System;

namespace VL.Qt
{
    /// <summary>
    /// Handle of a native scene (<c>vlqt_scene*</c> of vlqt.h): a window or an offscreen scene. Blittable, so it passes to
    /// QtNative as the pointer; <c>default</c> (<see cref="Null"/>) is no scene. A device, an NT handle or a context are
    /// plain <see cref="IntPtr"/>s, never a scene.
    /// </summary>
    internal readonly record struct SceneHandle(IntPtr Value)
    {
        /// <summary>No scene (QtNative returns it when the creation failed).</summary>
        public static SceneHandle Null => default;

        /// <summary>Returns true if this is no scene.</summary>
        public bool IsNull => Value == IntPtr.Zero;
    }
}
