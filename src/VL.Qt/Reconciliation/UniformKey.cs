using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// Key of an entry in the uniform table: a texture and its sampler share a name, so a sampler entry has a key of its own.
    /// </summary>
    internal readonly record struct UniformKey(string Name, bool Sampler)
    {
        /// <summary>The key of the entry <paramref name="u"/> sets: a sampler-only uniform has the sampler key, every other one the value key.</summary>
        public static UniformKey Of(Uniform u) => new(u.Name, u.ValueType == Protocol.ValueType.Sampler);
    }
}
