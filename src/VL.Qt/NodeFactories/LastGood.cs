using System;

namespace VL.Qt.NodeFactories
{
    /// <summary>
    /// The keep-last-good rule of the generated nodes of a file (QML component, shader), written once for both factories:
    /// while the file does not read or parse (or compile), the node keeps the pins, and with them the links and values, of the
    /// last version that did, and shows the new error as a message. A node that never had a good version shows what the failed
    /// version gives. One instance per file; thread-safe.
    /// </summary>
    /// <typeparam name="T">What the file is loaded into (a layout, a shader source).</typeparam>
    internal sealed class LastGood<T> where T : class
    {
        /// <summary>Message next to the error of a file whose node keeps the pins of its last good version.</summary>
        public const string KeptNote = "The node keeps the pins of the last version that worked, until the file is fixed.";

        private readonly object _syncRoot = new();
        private T? _good;

        /// <summary>
        /// Returns <paramref name="loaded"/> if it is good (and remembers it), else the last good version made by
        /// <paramref name="keep"/> (last good, failed version) when there is one, else <paramref name="loaded"/>.
        /// </summary>
        /// <param name="loaded">The version just loaded.</param>
        /// <param name="failed">True if the file did not read, parse or compile.</param>
        /// <param name="keep">Makes the result from the last good version and the failed one: the good pins plus the error of the failed one.</param>
        public T Resolve(T loaded, bool failed, Func<T, T, T> keep)
        {
            lock (_syncRoot)
            {
                if (!failed)
                {
                    _good = loaded;
                    return loaded;
                }
                return _good is { } good ? keep(good, loaded) : loaded;
            }
        }
    }
}
