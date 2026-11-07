using System;
using VL.Qt;

// Raw SET_EFFECT_CHAIN for the smoke tests (#489): one MULTI link per run, each written by its own action (Effect*
// entries). A run is applied whole in QtNative, so a test keeps the values of its runs and sends them all again.
static class EffectChainCommands
{
    /// <summary>SET_EFFECT_CHAIN for <paramref name="id"/> with the given MULTI runs in application order; none = no chain.</summary>
    public static void Chain(this CommandWriter cmd, uint id, params Action<CommandWriter>[] runs)
    {
        cmd.BeginEffectChain(id);
        foreach (var run in runs)
        {
            cmd.BeginMultiLink();
            run(cmd);
            cmd.EndLink();
        }
        cmd.EndEffectChain();
    }
}
