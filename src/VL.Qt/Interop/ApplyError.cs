namespace VL.Qt.Interop
{
    /// <summary>
    /// Details of the last apply of a scene (vlqt.h), read by <see cref="Native.GetApplyError"/>. Rc 0 = the apply succeeded.
    /// <paramref name="Reason"/> is a managed copy of the native text, which is valid only until the next apply of the scene;
    /// it is empty on success.
    /// </summary>
    internal readonly record struct ApplyError(int Rc, int Command, uint Id, ushort Op, ushort Prop, string Reason);
}
