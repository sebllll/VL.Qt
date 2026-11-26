using System.Collections.Generic;
using VL.Qt.Interop;

namespace VL.Qt.Tests.Support
{
    /// <summary>The font API of QtNative without Qt: hands out handles and records the calls in order.</summary>
    internal sealed class FakeFontApi : IFontApi
    {
        private ulong _next;

        public readonly List<string> Calls = new();
        public bool OnQtThread = true;
        /// <summary>Result of the next Add calls; the failure text is <see cref="Error"/>.</summary>
        public Native.Result AddRc = Native.Result.Ok;
        public Native.Result ReleaseRc = Native.Result.Ok;
        public string Error = "font failure";
        /// <summary>Family names Add reports (newline-separated, like QtNative).</summary>
        public string Families = "Fake Sans";

        public bool IsQtThread => OnQtThread;

        public Native.Result Add(string path, out ulong handle, out string families)
        {
            Calls.Add("add " + path);
            handle = 0;
            families = string.Empty;
            if (AddRc != Native.Result.Ok)
                return AddRc;
            handle = ++_next;
            families = Families;
            return Native.Result.Ok;
        }

        public Native.Result Release(ulong handle)
        {
            Calls.Add("release " + handle);
            return ReleaseRc;
        }

        public string LastError() => Error;
    }
}
