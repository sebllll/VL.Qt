using System;

namespace VL.Qt.Interop
{
    /// <summary>vlqt.dll has another ABI version than this VL.Qt.dll: QtApp is not created, no window opens.</summary>
    internal sealed class AbiMismatchException : InvalidOperationException
    {
        public AbiMismatchException(string message) : base(message) { }
    }
}
