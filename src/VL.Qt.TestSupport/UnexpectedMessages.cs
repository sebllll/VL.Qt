using System;
using Microsoft.Extensions.Logging.Abstractions;

namespace VL.Qt
{
    /// <summary>A node message store that fails the test as soon as the node shows a message: for nodes that must stay silent.</summary>
    internal static class UnexpectedMessages
    {
        /// <summary>Returns a store that throws when a message is shown (warning or error).</summary>
        public static NodeErrors Store()
            => new(NullLogger.Instance, static (severity, text) => throw new InvalidOperationException($"unexpected node message ({severity}): {text}"));
    }
}
