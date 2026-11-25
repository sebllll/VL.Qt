using System;
using System.IO;

namespace VL.Qt.Shaders
{
    /// <summary>What the scan needs of a .frag, from one read: the file head annotations and whether it is a filter.</summary>
    /// <param name="Annotations">Annotations of the file.</param>
    /// <param name="IsFilter">The .frag declares <c>sampler2D source</c> (<see cref="ShaderSource.IsFilter"/>).</param>
    internal sealed record ShaderFileHead(ShaderAnnotations Annotations, bool IsFilter)
    {
        /// <summary>Reads <paramref name="fragmentPath"/> once; null if it cannot be read now (init reports it).</summary>
        public static ShaderFileHead? Read(string fragmentPath)
        {
            try
            {
                var text = File.ReadAllText(fragmentPath);
                return new ShaderFileHead(ShaderAnnotations.Parse(text), ShaderSource.IsFilter(text));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
