# Glossary

The words that VL.Qt and its pages use, one sentence each. The page is for vvvv users and for contributors who know vvvv but not Qt: the first table names the things VL.Qt itself talks about, the second the terms of Qt that show up behind them.

A few rules explain the choice of words. Where a user reads a word, vvvv's meaning wins: node, link, pin, frame, renderer and feedback mean what they mean in vvvv, and a "layer" as VL.Skia uses it is an element here. Qt's own words (item, handler, control, layer) name Qt's own things and appear only on the Qt side. The same word is used for the same thing in the C ABI, in C++, in C#, in the docs and in the node names.

## VL.Qt terms

| Word | Means |
|---|---|
| **QtNative** | the C++ layer that owns Qt: `vlqt.dll`, `libvlqt.so` |
| **C ABI** | the `extern "C"` functions of QtNative |
| **protocol** | the contract of the binary records between C# and QtNative: versions, ids, layouts |
| **wire** | the bytes of the protocol and the code that reads and writes them |
| **command** | one record from C# to QtNative |
| **event** | one record from QtNative to C# |
| **element** | the immutable description of one thing to show; what the element pins carry |
| **element tree** | the elements linked to one renderer in one frame |
| **container** | an element with children: Group, Row, Column |
| **widget** | an element the user operates: Button, Toggle, Slider, Input |
| **stylable widget** | a widget patched by the user, an implementation of `IStylable` |
| **node** | a vvvv node, nothing else; in C++ the word belongs to Qt's scene-graph nodes (`QSGNode`) |
| **element node** | a vvvv node that outputs an element |
| **entry** | the record of a scene in QtNative for one element or one handler |
| **renderer** | a node that shows an element tree: Renderer, QtTexture, QtRenderer |
| **scene** | what one renderer owns on the Qt side: its elements and the target they are drawn to, a window or an offscreen texture; the tree of the patch is the element tree, not the scene |
| **effect** | one step of the chain at the `Effect` pin; "effect" never means anything outside that chain |
| **effect chain** | the effects of one element in order |
| **merged effects** | Blur, DropShadow, ColorAdjust, Colorize, Mask: neighbours in a chain merge into one layer (Qt's MultiEffect) |
| **shader effects** | the other effects: each is one shader with a layer of its own |
| **layer** | Qt's meaning only (see [Qt terms](#qt-terms)): an item drawn into a texture of its own; what VL.Skia calls a layer is an element here; the one exception is the Blend pin `Layer`, which takes an element |
| **shader** (element) | the element that draws a fragment shader |
| **shader effect** (code) | an effect of a chain that runs one shader; `ShaderEffect` names this, not the element |
| **link** | a vvvv link between two pins; in code only one entry of an effect chain as it is sent |
| **uniform**, **sampler** | a value and a texture input of a shader |
| **interaction** | the description of an input behaviour at the `Interaction` pin: Click, Drag, Hover … |
| **handler** | Qt's object that an interaction creates (see [Qt terms](#qt-terms)) |
| **style** | the look of widgets as a value: the `Style` record and the style nodes |
| **path** | an immutable 2D path |
| **image** | a picture held by QtNative, as a value in the patch |
| **resource** | whatever QtNative loads and holds under an id: image, shader, snapshot, audio tap |
| **snapshot** | the picture of an element as an image resource |
| **node-API snapshot** | the text file of the public C# surface that a test compares |
| **frame** | one frame of vvvv's mainloop, nothing else; the one exception is the style group `Frame` (background and border of a widget), written "the Frame style" |
| **pump** | Qt's work for one vvvv frame: events, animations, rendering |
| **host** | the vvvv app that embeds QtNative |
| **animation clock** | the time Qt's animations run on, set by vvvv |
| **render thread** | a thread of the pool that renders offscreen scenes |
| **unit** | a length in the space of the renderer (`Space` pin) |
| **DIP** | Qt's logical pixel; shader parameters and style sizes are in DIP |
| **px** | a device pixel, only that |
| **bounds** | the rectangle of an element |
| **id** | a number that C# chooses and sends: element id, handler id, resource id, feedback id |
| **handle** | a pointer to a native object, never a number |
| **occurrence** | one place where an element appears in a tree; the first is the primary, the others show a placeholder |
| **mirror** | the reconciler's record of what it sent for one occurrence |
| **feedback** | the way back from Qt to the node that made an element |
| **previous-frame layer** | the layer that keeps the previous frame of a scene while `Clear` is off; never called "feedback" |
| **test export** | a function of the C ABI that only tests call |
| **testkit** | the native library with the stand-in D3D11 helpers of the SmokeTest, not packed |
| **SmokeTest**, **help patch** | as written |

## Qt terms

| Qt term | Meaning |
|---|---|
| [Qt Quick](https://doc.qt.io/qt-6/qtquick-index.html) | Qt's retained 2D scene: a tree of items that Qt draws with the GPU. |
| [QML](https://doc.qt.io/qt-6/qtqml-index.html) | the language Qt Quick scenes are written in; QtNative creates its items from QML. |
| item ([`QQuickItem`](https://doc.qt.io/qt-6/qquickitem.html)) | one object in a Qt Quick scene: Qt's counterpart of an element. |
| engine ([`QQmlEngine`](https://doc.qt.io/qt-6/qqmlengine.html)) | loads and runs QML; one per scene. |
| [`QQuickWindow`](https://doc.qt.io/qt-6/qquickwindow.html) | the window that draws a tree of items; an offscreen scene has one too, without a native window. |
| render control ([`QQuickRenderControl`](https://doc.qt.io/qt-6/qquickrendercontrol.html)) | lets the application decide when and where a `QQuickWindow` renders: the base of offscreen scenes. |
| scene graph ([`QSGNode`](https://doc.qt.io/qt-6/qsgnode.html), [overview](https://doc.qt.io/qt-6/qtquick-visualcanvas-scenegraph.html)) | the render-side tree Qt builds from the items each frame; not the scene of the VL.Qt terms. |
| [render loop](https://doc.qt.io/qt-6/qtquick-visualcanvas-scenegraph.html#non-threaded-render-loop-basic) | how Qt schedules sync and render; QtNative runs `basic`, everything on one thread. |
| [QRhi](https://doc.qt.io/qt-6/qrhi.html) | Qt's layer over Direct3D, Vulkan, Metal and OpenGL. |
| animation driver (`QAnimationDriver`) | the clock of all QML animations; QtNative replaces it with vvvv's time. |
| layer ([`layer.enabled`](https://doc.qt.io/qt-6/qml-qtquick-item.html#layer.enabled-prop)) | an item drawn into a texture of its own first; what every effect needs. |
| [MultiEffect](https://doc.qt.io/qt-6/qml-qtquick-effects-multieffect.html) | Qt's item that applies blur, shadow, colour changes and a mask to one layer in one pass. |
| [ShaderEffect](https://doc.qt.io/qt-6/qml-qtquick-shadereffect.html) | Qt's item that draws a custom shader; QtNative has an item of its own for it. |
| [pointer handler](https://doc.qt.io/qt-6/qtquickhandlers-index.html) | a Qt object attached to an item that reacts to input: `TapHandler`, `DragHandler` … |
| [Qt Quick Controls](https://doc.qt.io/qt-6/qtquickcontrols-index.html), [Controls style](https://doc.qt.io/qt-6/qtquickcontrols-styles.html) | Qt's widgets, and a set of QML files that draws them; VL.Qt ships the style `VLQt`. |
| [attached property](https://doc.qt.io/qt-6/qtqml-syntax-objectattributes.html#attached-properties-and-attached-signal-handlers) | a property a QML type adds to other items; `VlStyle` reaches the widgets that way. |
| [device pixel ratio](https://doc.qt.io/qt-6/highdpi.html) | px per DIP of a screen. |
| [windeployqt](https://doc.qt.io/qt-6/windows-deployment.html#the-windows-deployment-tool) | Qt's tool that copies the Qt files an application needs next to it. |
| private API | Qt headers without a compatibility promise; allowed. |
