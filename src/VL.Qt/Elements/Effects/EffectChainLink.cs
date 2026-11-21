namespace VL.Qt.Elements
{
    /// <summary>
    /// One link of SET_EFFECT_CHAIN, result of <see cref="Effect.Segment"/>: a folded MultiEffect run (<see cref="Shader"/>
    /// null) or a shader link.
    /// </summary>
    internal readonly record struct EffectChainLink(EffectDesc Multi, ShaderLink? Shader)
    {
        public bool IsShader => Shader is not null;
    }
}
