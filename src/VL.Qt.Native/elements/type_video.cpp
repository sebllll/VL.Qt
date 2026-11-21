#include "elements/element_types.h"
#include "audio_tap.h"

#include <QtCore/QFileInfo>
#include <QtCore/QMetaObject>
#include <QtCore/QUrl>
#include <QtMultimedia/QAudioDevice>
#include <QtMultimedia/QMediaDevices>
#include <QtQuick/QQuickItem>

namespace vlqt {

namespace {

// ---------------------------------------------------------------------------------------------
// Error state of an element: a QML string property vlqtError on the root item, empty = no error. Read by
// the poll (Elements::readError) and sent as EVENT_ERROR to the bound sink, i.e. shown at the element's vvvv node.
// vlqtPath: local path of a file URL for messages (file:///C:/a%20b.png -> C:/a b.png, file:///home/x -> /home/x).
#define VLQT_QML_PATH_FN \
    "  function vlqtPath(u) { return decodeURIComponent(String(u).replace(/^file:\\/\\/(\\/(?=[A-Za-z]:))?/, \"\")); }\n"

// Video source: empty = no media. Relative paths resolve against the process working directory (C# sends
// absolute paths); QUrl::fromLocalFile handles drive letters, spaces and non-ASCII names.
bool setMediaSource(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    const QUrl url = v.utf8.isEmpty() ? QUrl() : QUrl::fromLocalFile(QFileInfo(toQString(v)).absoluteFilePath());
    return writeQml(n.item, rp, url, err);
}

// ---------------------------------------------------------------------------------------------
// Video (QtMultimedia, FFmpeg backend): MediaPlayer + AudioOutput + VideoOutput in a wrapper Item, like the
// MultiEffect wrapper. Deliberate exception from "vvvv drives the clock": the player runs on its own clock, no
// seek, no sync. playing true -> play(), false -> pause() (shows the current frame; after loading the first one).
// Loop off during playback: the running pass ends, then playback stops. The FFmpeg backend has already read
// ahead into the next passes; a seek to the current position drops them, then loops = 1 applies.
// End of stream: keepLastFrame -> VideoOutput.endOfStreamPolicy KeepLastFrame, else ClearOutput (Qt default).
// A source change (incl. empty) always clears the output, otherwise KeepLastFrame would keep the old file's frame.
// A source change stops Qt's player, so the wrapper re-applies playing right away (play() during loading starts once
// loaded; not on LoadedMedia: the FFmpeg backend reports it again after the end and would restart). Loading
// is asynchronous: implicit size (natural video size), position and duration are 0 until then. Player errors go
// to vlqtError (shown + logged at the Video node) until the next source change. The player and its
// decoder threads are children of the item and go with DESTROY.

constexpr const char* kVideoQml =
    "import QtQuick\n"
    "import QtMultimedia\n"
    "Item {\n"
    "  id: root\n"
    "  implicitWidth: out.sourceRect.width\n"
    "  implicitHeight: out.sourceRect.height\n"
    "  property alias source: player.source\n"
    "  property bool playing: false\n"
    "  property bool loop: false\n"
    "  property bool keepLastFrame: false\n"
    "  property alias playbackRate: player.playbackRate\n"
    "  property alias volume: audio.volume\n"
    "  property alias fillMode: out.fillMode\n"
    "  readonly property alias vlqtPosition: player.position\n"
    "  readonly property alias vlqtDuration: player.duration\n"
    "  property string vlqtError: \"\"\n"
    "  readonly property alias vlqtPlayer: player\n" // audio tap (audio_tap.cpp)
    "  readonly property alias vlqtOut: out\n"
    VLQT_QML_PATH_FN
    "  function vlqtSync() { if (playing) player.play(); else player.pause(); }\n"
    "  function vlqtRelease() { playing = false; player.stop(); player.source = \"\"; }\n"
    "  onPlayingChanged: vlqtSync()\n"
    "  onLoopChanged: {\n"
    "    player.loops = loop ? MediaPlayer.Infinite : 1;\n"
    "    if (!loop && player.playbackState === MediaPlayer.PlayingState) player.position = player.position;\n"
    "  }\n"
    "  MediaPlayer {\n"
    "    id: player\n"
    "    loops: 1\n"
    "    audioOutput: AudioOutput { id: audio }\n"
    "    videoOutput: out\n"
    "    onSourceChanged: { root.vlqtError = \"\"; out.clearOutput(); root.vlqtSync(); }\n"
    "    onErrorOccurred: (error, errorString) => root.vlqtError = errorString + \" (\" + root.vlqtPath(source) + \")\"\n"
    "  }\n"
    "  VideoOutput {\n"
    "    id: out\n"
    "    anchors.fill: parent\n"
    "    endOfStreamPolicy: root.keepLastFrame ? VideoOutput.KeepLastFrame : VideoOutput.ClearOutput\n"
    "  }\n"
    "}\n";

/* f32 that must be > 0 (playback rate; reverse/zero playback is not supported by the backend). Finiteness is checked in Elements::set. */
bool setQmlPositiveReal(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    if (!(v.f[0] > 0)) {
        err = "value must be > 0";
        return false;
    }
    return writeQml(n.item, rp, qreal(v.f[0]), err);
}

/* Linear volume 0..1 (Qt AudioOutput); outside -> ApplyBadValue. */
bool setVolume(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    if (!(v.f[0] >= 0 && v.f[0] <= 1)) {
        err = "volume must be 0..1";
        return false;
    }
    return writeQml(n.item, rp, qreal(v.f[0]), err);
}

// Before the item goes: stop the player and drop its source first, so the decoder and audio threads end; then the
// audio tap, while the player still exists (audio_tap.h releaseVideo).
void releaseVideo(QQuickItem* item)
{
    QMetaObject::invokeMethod(item, "vlqtRelease");
    res::audio::releaseVideo(item);
    // The AudioOutput made QMediaDevices probe the WASAPI formats of every device asynchronously in a process-wide pool.
    // A process that ends during a probe hangs in Qt6Multimedia's atexit destructor of that pool (~QThreadPool waits for
    // workers ExitProcess already killed). preferredFormat() blocks only while a probe runs.
    for (const QAudioDevice& d : QMediaDevices::audioOutputs())
        (void)d.preferredFormat();
}

std::vector<PropDef> videoProps()
{
    return {
        {VLQT_PROPERTY_SOURCE, VLQT_VALUE_TYPE_STRING, setMediaSource, "source"},
        // VideoOutput.Stretch/PreserveAspectFit/PreserveAspectCrop (= C# VideoFillMode)
        {VLQT_PROPERTY_FILL_MODE, VLQT_VALUE_TYPE_I32, setQmlEnum<0, 1, 2>, "fillMode"},
        {VLQT_PROPERTY_PLAYING, VLQT_VALUE_TYPE_BOOL, setQmlBool, "playing"},
        {VLQT_PROPERTY_LOOP, VLQT_VALUE_TYPE_BOOL, setQmlBool, "loop"},
        // VideoOutput.endOfStreamPolicy (Qt 6.9+): KeepLastFrame / ClearOutput (Qt default)
        {VLQT_PROPERTY_KEEP_LAST_FRAME, VLQT_VALUE_TYPE_BOOL, setQmlBool, "keepLastFrame"},
        {VLQT_PROPERTY_PLAYBACK_RATE, VLQT_VALUE_TYPE_F32, setQmlPositiveReal, "playbackRate"},
        {VLQT_PROPERTY_VOLUME, VLQT_VALUE_TYPE_F32, setVolume, "volume"},
        // Sound to VL.Audio instead of Qt's output (audio_tap.h)
        {VLQT_PROPERTY_AUDIO_TAP, VLQT_VALUE_TYPE_U64, res::audio::setVideoTap, nullptr},
    };
}

} // namespace

TypeDef videoType()
{
    TypeDef t{VLQT_ELEMENT_TYPE_VIDEO, "VIDEO", kVideoQml, videoProps()};
    t.beforeDestroy = releaseVideo;
    return t;
}

} // namespace vlqt
