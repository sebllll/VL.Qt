using System;
using VL.Core;
using VL.Core.Import;
using VL.Model;

[assembly: ImportAsIs]

namespace VL.Qt.Stride
{
    /// <summary>
    /// Returns the versions of the loaded VL.Qt and VL.Qt.Stride assemblies; placeholder proving that VL.Qt.Stride loads.
    /// </summary>
    // TODO: Replace with QtTexture (phase 3 of #116).
    [ProcessNode(Name = "QtStrideInfo", Category = "Qt")]
    [Smell(SymbolSmell.Experimental)]
    public sealed class QtStrideInfoNode : IDisposable
    {
        readonly string _info;

        public QtStrideInfoNode([Pin(Visibility = PinVisibility.Hidden)] NodeContext nodeContext)
        {
            var qt = typeof(Renderer).Assembly.GetName().Version;
            var stride = typeof(QtStrideInfoNode).Assembly.GetName().Version;
            _info = $"VL.Qt {qt}, VL.Qt.Stride {stride}";
        }

        /// <summary>
        /// Returns the versions of the loaded VL.Qt and VL.Qt.Stride assemblies.
        /// </summary>
        /// <returns>Assembly versions of VL.Qt and VL.Qt.Stride.</returns>
        [return: Pin(Name = "Output")]
        public string Update() => _info;

        public void Dispose()
        {
        }
    }
}
