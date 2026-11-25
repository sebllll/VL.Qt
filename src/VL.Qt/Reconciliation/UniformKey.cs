namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// Key of an entry in the uniform table: a texture and its sampler share a name, so a sampler entry has a key of its own.
    /// </summary>
    internal readonly record struct UniformKey(string Name, bool Sampler);
}
