using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lang;
using VL.Lang.PublicAPI;
using VL.Lib.Animation;
using VL.Qt.Elements;
using VL.Qt.Wire;

[assembly: ImportAsIs]

namespace VL.Qt
{
    /// <summary>
    /// Opens a Qt Quick window and shows the element tree at Input (Qt.Elements: Rectangle, Text, Group, ...).
    /// The tree is diffed against the last frame; only changes go to Qt (once per frame, in the global pump).
    /// In the Qt window: F2 toggles the perfmeter panel, F5 the timing stats.
    /// </summary>
    [ProcessNode(Name = "Renderer", Category = Categories.Qt)]
    public sealed class Renderer : IDisposable, IQtFrameParticipant
    {
        private const string IconPhase = "Window Icon";
        private const string SamplesPhase = "Multisample Count";

        // Pump and scene registry of this vvvv app (new per F8/F5); QtApp itself is process-wide.
        private readonly QtPump _pump;
        private readonly QtApp _app;
        private readonly NodeContext _nodeContext;
        private readonly NodeErrors _errors; // node logger + pump exceptions at the node
        private readonly bool _boundToDocument;
        private readonly bool _dialogIfDocumentChanged;
        private readonly RectangleF _bounds;
        private readonly bool _vSync;
        private bool _fullscreen; // last Fullscreen pin value sent to QtNative
        private readonly IDevSession? _session; // editor only (null in exported apps): Save Bounds writes the Bounds pin
        private readonly BoundsSaver? _boundsSaver;
        private readonly Stopwatch _clock = Stopwatch.StartNew(); // wall time for the Save Bounds delay (the frame clock may pause)
        private WindowRenderer? _view; // created in the first Update (Title/Size are update pins)
        private bool _disposed;
        private bool _debug; // last Debug pin value sent to the window
        private bool _borderless; // last Borderless pin value sent to the window
        private MultisampleCount _samples; // last Multisample Count pin value sent to the window
        private string? _iconPath; // last window icon sent to the window (full path, null = default)
        private IDisposable? _keepAlive; // AppHost.KeepAlive() in the WinForms window export only, released on X / Dispose

        /// <param name="nodeContext">Injected by vvvv.</param>
        /// <param name="bounds">Initial position and size of the window in DIP (on create only, like Bounds of VL.Skia/VL.Stride; a change recreates the node). Size = client size, position = outer top-left. Empty bounds: 600x400 centered on the primary screen; bounds on no screen: centered. Applied before the window is shown, on all platforms (Wayland: size only, the compositor places the window). A node added live or restored by Undo/Redo gets no pin value from vvvv and opens at the default bounds until the next restart.</param>
        /// <param name="vSync">Synchronize presenting with the display refresh (swap interval 1). On (default): no tearing; the frame may wait for the refresh (F5 segment vsync wait), that wait does not count as Qt work. Off: Present never waits, vvvv alone sets the frame rate, tearing is possible. On create only (a change recreates the node, like Bounds).</param>
        /// <param name="boundToDocument">Closing the window (X) closes the document of this node in the editor (like VL.Skia/VL.Stride) and ends an exported app, even if KeepAppAlive still holds it (unlike VL.Skia/VL.Stride, where the pin only acts in the editor). False: X only closes the window, the app keeps running.</param>
        /// <param name="dialogIfDocumentChanged">Ask to save when closing a changed document via X.</param>
        /// <param name="saveBounds">In the editor, moving or resizing the window writes its geometry to the Bounds pin ~0.5 s after the last change (like VL.Skia/VL.Stride), without recreating the node; save the document to keep it. Skipped while minimized, maximized or fullscreen. Turn off if Bounds is connected (the written value would not be used, it would only mark the document changed). No effect in exported apps.</param>
        public Renderer(NodeContext nodeContext,
            [Pin(Visibility = VL.Model.PinVisibility.Optional)] [DefaultValue(WindowPlacement.DefaultBoundsString)] RectangleF bounds,
            [Pin(Visibility = VL.Model.PinVisibility.Optional)] [DefaultValue(true)] bool vSync = true,
            [Pin(Visibility = VL.Model.PinVisibility.Optional)] [DefaultValue(true)] bool boundToDocument = true,
            [Pin(Visibility = VL.Model.PinVisibility.Optional)] bool dialogIfDocumentChanged = false,
            [Pin(Visibility = VL.Model.PinVisibility.Optional)] [DefaultValue(true)] bool saveBounds = true)
        {
            _nodeContext = nodeContext;
            _errors = new NodeErrors(nodeContext);
            _bounds = WindowPlacement.CreateBounds(bounds, nodeContext.AppHost.GetDefaultValue<RectangleF>());
            _vSync = vSync;
            _boundToDocument = boundToDocument;
            _dialogIfDocumentChanged = dialogIfDocumentChanged;
            _pump = QtPump.ForApp(nodeContext.AppHost);
            _app = QtApp.Get(null);
            Debug.Assert(_app.IsQtThread, "VL.Qt must be used from the thread that created the QGuiApplication");
            // Exported apps end once the host no longer needs to stay alive (IL of vvvv 8.0-0156, VL.AppServices:
            // AppHostBase.ShouldStayAlive = ref count of AppHost.KeepAlive() > 0; the WinForms host additionally
            // Application.OpenForms.Count > 0). The Qt window is no Form, so in the WinForms window export the Renderer holds
            // a KeepAlive until the Qt window is closed (X) or the node is disposed - same behaviour as the former invisible
            // keep-alive Form, which also only worked there. Console exports (Linux/Windows) are deliberately not kept alive:
            // they need KeepAppAlive [System] in the top-level patch (docs/linux.md).
            if (nodeContext.AppHost.IsExported && IsWindowsFormsHost(nodeContext.AppHost))
                _keepAlive = nodeContext.AppHost.KeepAlive();
            ExportedApp.Attach(nodeContext); // console export: signals, clean end (L7b)
            if (saveBounds && !nodeContext.AppHost.IsExported && IDevSession.Current is { } session)
            {
                _session = session;
                _boundsSaver = new BoundsSaver(_bounds);
            }
            _pump.Register(this);
        }

        internal const string WindowsFormsAppHostType = "VL.App.WindowsForms.WindowsFormsAppHost";

        /// <summary>
        /// True if the app runs in the WinForms window host of exported apps (OutputType WinExe, package VL.App.WindowsForms).
        /// AppHost has no API for the host kind, so the generated entry point's host type is checked by name (no WinForms
        /// reference needed). vvvv 8.0-0156: WinExe → sealed VL.App.WindowsForms.WindowsFormsAppHost, console (Exe) →
        /// sealed VL.App.ConsoleHost.ConsoleAppHost (VL.App.Console), both derived from VL.AppServices.ExportedAppHost.
        /// The base chain is walked in case a future host derives from the WinForms host.
        /// </summary>
        internal static bool IsWindowsFormsHost(AppHost appHost)
        {
            for (var t = appHost.GetType(); t != null; t = t.BaseType)
                if (t.FullName == WindowsFormsAppHostType)
                    return true;
            return false;
        }

        /// <summary>Renders the element tree into a Qt window.</summary>
        /// <param name="input">Root element (null = empty scene).</param>
        /// <param name="space">Coordinate space of the elements: origin and size of one unit, see <see cref="Space"/>.</param>
        /// <param name="clearColor">Background of the window. While Clear is off, its alpha is the fade strength of the previous frame (0 = no fade, 1 = like Clear on); an opaque color, the default, leaves no trail.</param>
        /// <param name="clear">True clears the window to Clear Color every frame. False keeps the previous frame and draws the new one over it (trails, smearing, feedback); the alpha of Clear Color then sets how strongly each frame fades the old content toward the color, so lower it (e.g. 0.1) to see trails: 0 keeps everything, 1 shows none. Fading to transparency is not possible, and the fade speed depends on the frame rate. Switchable at runtime without recreating the window. Costs one full-size texture while off, and Qt renders every frame then, also when the scene did not change.</param>
        /// <param name="enabled">False freezes the scene (no updates of Input, Space, Clear Color, Clear); the window keeps the last state.</param>
        /// <param name="title">Window title (changes at runtime are sent to the window).</param>
        /// <param name="borderless">Window without frame and title bar (Qt::FramelessWindowHint), switchable at runtime without recreating the node. The client area stays where it is, the frame appears or disappears around it. Without a frame the Bounds position (outer top-left) is the client top-left, and windows narrower than the Windows minimum width of a captioned window (~130-150 px) are possible. Save Bounds works as usual. While fullscreen the value is remembered and applied when leaving fullscreen. Windows and Linux (X11); Wayland: the compositor decides (client-side decoration off, server-side decorations may stay).</param>
        /// <param name="isVisible">Whether the Qt window is currently shown (false after closing it with X; recreate the node to reopen).</param>
        /// <param name="perf">Frame, CPU and GPU timings of the window (same values as the F5 overlay).</param>
        /// <param name="clientBounds">Current client area of the window in DIP (position = client top-left on the desktop, size = client size, without frame and title bar), like Client Bounds of VL.Skia/VL.Stride. Follows moving and resizing by the user, maximized, fullscreen and borderless; reflects the last pump (previous frame), empty until the window reported its first geometry. Minimized: the last value Qt reports (Windows: -32000 position). Unlike Bounds (position = outer top-left), so not meant to be fed back into Bounds.</param>
        /// <param name="error">Last error (window creation, apply or poll in QtNative, or an exception in the pump), empty if ok. Apply errors of an element node (e.g. a value Qt rejected) are shown at that node instead. Also logged (once per distinct error) in the Log window; pump exceptions are shown at the node as well.</param>
        /// <param name="debug">Debug overlay for all elements of the window (an element's own Debug pin shows only that element). Does not affect layout or hit-testing. Cyan frame = actual size (solid: Size, dashed: Auto Size on), orange = implicit size, i.e. what Auto Size would give (only where it differs), blue dotted = children extent, translucent red = children outside the frame (overflow), translucent green = padding and hatched violet = Row/Column spacing (both inside the frame), magenta cross = origin, yellow diamond = anchor. Text: green line = baseline, dashed green = ascent, dashed red = descent.</param>
        /// <param name="window">Window settings (WindowSettings node): icon, title, borderless, fullscreen and bounds on create. A value that is set there wins over the matching pin of this node (Title, Borderless, Fullscreen, Bounds); values that are not set leave the pin. The icon has no pin here: without WindowSettings or without a value the window shows the built-in default icon, a file that cannot be loaded gives a warning at this node and keeps the default. Changes apply live without recreating the window (Bounds: on create only). Save Bounds and Client Bounds stay on this node.</param>
        /// <param name="fullscreen">Fullscreen on the screen the window is on (largest share), live; off restores position and size from before. Also applied on create, before the window is shown. F11 / Alt+Enter in the window toggle it as well (like VL.Skia); the pin only acts when its value changes. While fullscreen, Save Bounds writes nothing. No effect on eglfs (kiosk export, always fullscreen); Wayland: the compositor picks the screen.</param>
        /// <param name="multisampleCount">Anti-aliasing of the edges (MSAA samples per pixel), live. More samples look smoother and cost GPU time and memory (X8 is several times the GPU time of None in a scene with many shapes); a count the device does not support is lowered to the next supported one, with a warning at the node. Effect layers and snapshots in the window use the same count. A change recreates the window on screen once (tens of ms).</param>
        // Pin defaults: string-valued [DefaultValue] (Color4/RectangleF have no TypeConverter, so the (Type, string) form is
        // silently ignored by vvvvc and the pin becomes default(T)); no C# defaults on the pins. #202020 = 0.1254902.
        public void Update(
            [Pin(Visibility = VL.Model.PinVisibility.Optional)] out bool isVisible,
            [Pin(Visibility = VL.Model.PinVisibility.Optional)] out PerfStats perf,
            [Pin(Visibility = VL.Model.PinVisibility.Optional)] out RectangleF clientBounds,
            out string error,
            Element? input,
            [DefaultValue(typeof(Space), nameof(Space.DIPTopLeft))] Space space,
            [DefaultValue("0.1254902, 0.1254902, 0.1254902, 1")] Color4 clearColor,
            [Pin(Visibility = VL.Model.PinVisibility.Optional), DefaultValue(true)] bool clear,
            [DefaultValue(true)] bool enabled,
            [DefaultValue("VL.Qt")] string title,
            [Pin(Visibility = VL.Model.PinVisibility.Optional), DefaultValue(false)] bool borderless,
            [Pin(Visibility = VL.Model.PinVisibility.Optional), DefaultValue(false)] bool fullscreen,
            [Pin(Visibility = VL.Model.PinVisibility.Optional), DefaultValue(false)] bool debug,
            [Pin(Visibility = VL.Model.PinVisibility.Optional)] WindowSettings? window,
            [Pin(Name = "Multisample Count", Visibility = VL.Model.PinVisibility.Optional), DefaultValue(typeof(MultisampleCount), nameof(MultisampleCount.X4))] MultisampleCount multisampleCount)
        {
            Debug.Assert(_app.IsQtThread, "VL.Qt must be used from the thread that created the QGuiApplication");

            // A value set in Window wins over the pin; borderless/fullscreen/title below are the resolved values.
            var resolved = WindowSettings.Resolve(window, title, borderless, fullscreen);
            title = resolved.Title!;
            borderless = resolved.Borderless;
            fullscreen = resolved.Fullscreen;

            if (_view is null && !_disposed)
            {
                _borderless = borderless; // before show: no frame flash
                _fullscreen = fullscreen;
                _iconPath = resolved.IconPath;
                _view = new WindowRenderer(SceneRegistry.ForApp(_nodeContext.AppHost), title, WindowSettings.ResolveBounds(window, _bounds), _vSync,
                    (borderless ? Protocol.WindowMode.Borderless : 0u) | (fullscreen ? Protocol.WindowMode.Fullscreen : 0u),
                    _iconPath, multisampleCount);
                _samples = multisampleCount;
                ReportSamples(multisampleCount, _view.UsedSamples);
                ReportIcon(_view.IconResult);
                _view.CloseRequested += OnCloseRequested;
                if (_boundsSaver is { } saver)
                    _view.GeometryChanged += g => saver.OnGeometry(g, _clock.Elapsed);
            }
            if (resolved.IconPath != _iconPath && _view is not null)
            {
                _iconPath = resolved.IconPath;
                ReportIcon(_view.SetIcon(_iconPath));
            }
            if (borderless != _borderless && _view is not null)
            {
                _borderless = borderless;
                _view.SetMode(Protocol.WindowMode.Borderless, borderless);
            }
            if (fullscreen != _fullscreen && _view is not null)
            {
                _fullscreen = fullscreen;
                _view.SetMode(Protocol.WindowMode.Fullscreen, fullscreen);
            }
            if (multisampleCount != _samples && _view is not null)
            {
                _samples = multisampleCount;
                ReportSamples(multisampleCount, _view.SetSamples(multisampleCount));
            }
            if (debug != _debug && _view is not null)
            {
                _debug = debug;
                _view.SetDebug(debug);
            }
            // Only written here; the pump applies the buffer once per frame (ApplyCommands) after all node updates.
            _view?.Update(input, space, clearColor, clear, enabled, title);

            // Is Visible is live; Perf, Client Bounds and Error reflect the last pump (previous frame).
            isVisible = _view?.IsVisible ?? false;
            perf = _view?.Perf ?? PerfStats.Empty;
            clientBounds = _view?.Geometry?.ClientBounds ?? default;
            var qtNativeError = _view?.Error ?? "";
            _errors.LogOnly(qtNativeError);
            error = _errors.Current ?? qtNativeError;
        }

        // Not loadable (-7): QtNative keeps the default icon, the node shows a warning until the next icon applies.
        private void ReportIcon(int result)
        {
            if (result == -7 && _iconPath is not null)
                _errors.Warn($"Window icon '{_iconPath}' could not be loaded, the default icon is shown: {Native.LastError()}", IconPhase);
            else
                _errors.Clear(IconPhase);
        }

        // The count QtNative used is lower than requested (device limit): warning at the node until the next count fits.
        // A failed call (negative) is shown the same way; the window keeps the count it had.
        private void ReportSamples(MultisampleCount requested, int used)
        {
            var warning = used < 0 ? $"Multisample Count could not be set: {Native.LastError()}" : Multisample.ClampWarning(requested, used);
            if (warning is not null)
                _errors.Warn(warning, SamplesPhase);
            else
                _errors.Clear(SamplesPhase);
        }

        // Pump phases: an exception is logged with this node's logger (once per distinct error), shown at the node
        // and on Error, and cleared when the same phase succeeds again. Never left to the global pump catch.
        void IQtFrameParticipant.ApplyCommands()
        {
            if (_disposed)
                return;
            try
            {
                _view?.ApplyCommands();
                _errors.Clear(nameof(IQtFrameParticipant.ApplyCommands));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _errors.Report(e, nameof(IQtFrameParticipant.ApplyCommands));
            }
        }

        IntPtr IQtFrameParticipant.Window => _disposed ? IntPtr.Zero : _view?.Window ?? IntPtr.Zero;

        void IQtFrameParticipant.ReportHostFrame(Native.vlqt_host_frame frame)
        {
            if (_disposed)
                return;
            try
            {
                _view?.ReportHostFrame(frame);
                _errors.Clear(nameof(IQtFrameParticipant.ReportHostFrame));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _errors.Report(e, nameof(IQtFrameParticipant.ReportHostFrame));
            }
        }

        bool IQtFrameParticipant.TryGetLastSample(out Native.vlqt_perf_sample sample)
        {
            sample = default;
            return !_disposed && _view != null && _view.TryGetLastSample(out sample);
        }

        void IQtFrameParticipant.PollInput()
        {
            if (_disposed)
                return;
            try
            {
                _view?.PollInput();
                _errors.Clear(nameof(IQtFrameParticipant.PollInput));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _errors.Report(e, nameof(IQtFrameParticipant.PollInput));
            }
        }

        void IQtFrameParticipant.AfterEvents()
        {
            if (_disposed)
                return;
            try
            {
                _view?.AfterEvents();
                if (_boundsSaver is { } saver && saver.TryTake(_clock.Elapsed, out var r))
                    SaveBounds(r);
                _errors.Clear(nameof(IQtFrameParticipant.AfterEvents));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _errors.Report(e, nameof(IQtFrameParticipant.AfterEvents));
            }
        }

        // Like VL.Skia's SkiaRendererNode: SetPinValue on this node's Bounds pin, DontCompile so the node is not recreated
        // (a changed create value would otherwise rebuild the window). The public API cannot tell whether Bounds is
        // connected (Save Bounds = false is the way out, like VL.Skia's pin of the same name).
        // Posted like OnCloseRequested: changing the solution not from within the pump.
        private void SaveBounds(RectangleF bounds)
        {
            var session = _session!;
            var stack = _nodeContext.Path.Stack;
            void Write(object? _)
            {
                if (_disposed)
                    return;
                try
                {
                    session.CurrentSolution.SetPinValue(stack, "Bounds", bounds).Confirm(VL.Model.SolutionUpdateKind.DontCompile);
                    _errors.Clear("Save Bounds");
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _errors.Report(e, "Save Bounds");
                }
            }
            if (SynchronizationContext.Current is { } sc)
                sc.Post(Write, null);
            else
                Write(null);
        }

        // Mirrors VL.Skia's SkiaRendererNode.FormClosing: editor → hide, or close the document when bound;
        // exported → the window closes (here: hides; in the WinForms host releasing the AppHost keep-alive ends the app,
        // in console exports KeepAppAlive [System] in the top-level patch decides); bound → the app ends.
        private void OnCloseRequested()
        {
            if (_boundToDocument && !_nodeContext.AppHost.IsExported)
            {
                // Deferred: closing the document (maybe with a modal dialog) disposes this node, so not from within Update.
                // If the user cancels the dialog the window simply stays open.
                var nodeId = _nodeContext.Path.Stack.LastOrDefault();
                var dialog = _dialogIfDocumentChanged;
                void Close(object? _) { if (!_disposed) SessionNodes.CloseDocumentOfNode(nodeId, dialog); }
                if (SynchronizationContext.Current is { } sc)
                    sc.Post(Close, null);
                else
                    Close(null);
                return;
            }
            _view?.Hide();
            _keepAlive?.Dispose();
            _keepAlive = null;
            if (_boundToDocument) // exported (L1c): end the app even if KeepAppAlive holds it
                ExportedApp.Exit(_nodeContext.AppHost);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            // Reconciler.Clear (frees the elements in the app's SceneRegistry), apply, destroy the window.
            _view?.Dispose();
            _view = null;
            _pump.Unregister(this);
            _errors.Dispose();
            _keepAlive?.Dispose();
            _keepAlive = null;
        }
    }
}
