using System;
using System.IO;
using Microsoft.Extensions.Logging;
using VL.Core;
using VL.Core.CompilerServices;
using VL.Core.Import;
using VL.Qt.Interop;

[assembly: AssemblyInitializer(typeof(VL.Qt.Initialization))]

namespace VL.Qt
{
    // Public sealed like all initializers of VL.StandardLibs (the runtime uses Default; e.g. VL.IO.Redis has one under
    // ImportAsIs as well); hidden from the node browser.
    [Smell(SymbolSmell.Hidden)]
    public sealed class Initialization : AssemblyInitializer<Initialization>
    {
        public override void Configure(AppHost appHost)
        {
            base.Configure(appHost);

            // Qt messages and process-wide VL.Qt messages go to the newest live app host's logger (before any node).
            QtLog.Attach(appHost);

            // Shader nodes from qtshaders/**/*.frag next to documents and in packages.
            Shaders.ShaderNodeFactory.Register(appHost);

            // QML component nodes from qml/**/*.qml next to documents and in packages.
            Qml.QmlNodeFactory.Register(appHost);

            // Qt's pipeline cache in the exported program only (the editor would share one file between runs and patches,
            // #495). Loading vlqt.dll here is enough: the call only stores the path, QtNative itself starts later in QtApp.
            // A missing/broken QtNative must not break the start of an app that references VL.Qt (QtApp reports it at the node).
            if (appHost.IsExported)
            {
                try
                {
                    Native.vlqt_app_set_pipeline_cache(Path.Combine(AppContext.BaseDirectory, "QtPipelineCache.bin"));
                }
                catch (Exception e)
                {
                    appHost.LoggerFactory?.CreateLogger(typeof(Initialization)).LogWarning(e, "Qt pipeline cache not set: {Message}", e.Message);
                }
            }
        }
    }
}
