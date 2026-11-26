#include "elements/element_types.h"
#include "resources/audio_tap.h"

#include <QtCore/QFileInfo>
#include <QtCore/QMetaObject>
#include <QtCore/QUrl>
#include <QtMultimedia/QAudioDevice>
#include <QtMultimedia/QMediaDevices>
#include <QtQuick/QQuickItem>

namespace vlqt {

namespace {

// ---------------------------------------------------------------------------------------------
// Video source: empty = no media. Relative paths resolve against the process working directory (C# sends
// absolute paths); QUrl::fromLocalFile handles drive letters, spaces and non-ASCII names.
bool setMediaSource(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    const QUrl url = v.utf8.isEmpty() ? QUrl() : QUrl::fromLocalFile(QFileInfo(toQString(v)).absoluteFilePath());
    return writeQml(n.item, rp, url, err);
}

// ---------------------------------------------------------------------------------------------
// Video (qml/elements/VideoElement.qml): QtMultimedia, FFmpeg backend, MediaPlayer + AudioOutput + VideoOutput in a
// wrapper Item. The player runs on its own clock; player errors go to vlqtError (the error state of an element: a QML
// string property on the root item, empty = no error, read by Elements::readError).

constexpr const char* kVideoQml = "qrc:/vlqt/qml/elements/VideoElement.qml";

/* PROP_FILL_MODE: Stretch, Fit and Crop become the VideoOutput.fillMode values (Stretch, PreserveAspectFit, PreserveAspectCrop); Tile is refused. */
bool setVideoFillMode(Elements&, ElementEntry& n, const ResolvedProp& rp, const Value& v, std::string& err)
{
    switch (v.i) {
    case VLQT_FILL_MODE_STRETCH:
        return writeQml(n.item, rp, 0, err);
    case VLQT_FILL_MODE_FIT:
        return writeQml(n.item, rp, 1, err);
    case VLQT_FILL_MODE_CROP:
        return writeQml(n.item, rp, 2, err);
    default:
        err = "fill mode " + std::to_string(v.i) + " is not supported by a video";
        return false;
    }
}

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
// audio tap, while the player still exists (resources/audio_tap.h releaseVideo).
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
        {VLQT_PROPERTY_FILL_MODE, VLQT_VALUE_TYPE_I32, setVideoFillMode, "fillMode"},
        {VLQT_PROPERTY_PLAYING, VLQT_VALUE_TYPE_BOOL, setQmlBool, "playing"},
        {VLQT_PROPERTY_LOOP, VLQT_VALUE_TYPE_BOOL, setQmlBool, "loop"},
        // VideoOutput.endOfStreamPolicy (Qt 6.9+): KeepLastFrame / ClearOutput (Qt default)
        {VLQT_PROPERTY_KEEP_LAST_FRAME, VLQT_VALUE_TYPE_BOOL, setQmlBool, "keepLastFrame"},
        {VLQT_PROPERTY_PLAYBACK_RATE, VLQT_VALUE_TYPE_F32, setQmlPositiveReal, "playbackRate"},
        {VLQT_PROPERTY_VOLUME, VLQT_VALUE_TYPE_F32, setVolume, "volume"},
        // Sound to VL.Audio instead of Qt's output (resources/audio_tap.h)
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
