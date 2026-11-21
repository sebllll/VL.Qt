// Video element (type_video.cpp). QtMultimedia, FFmpeg backend: MediaPlayer + AudioOutput + VideoOutput in a wrapper
// Item, like the MultiEffect wrapper. Deliberate exception from "vvvv drives the clock": the player runs on its own
// clock, no seek, no sync. playing true -> play(), false -> pause() (shows the current frame; after loading the first
// one). Loop off during playback: the running pass ends, then playback stops. The FFmpeg backend has already read ahead
// into the next passes; a seek to the current position drops them, then loops = 1 applies.
// End of stream: keepLastFrame -> VideoOutput.endOfStreamPolicy KeepLastFrame, else ClearOutput (Qt default).
// A source change (incl. empty) always clears the output, otherwise KeepLastFrame would keep the old file's frame.
// A source change stops Qt's player, so the wrapper re-applies playing right away (play() during loading starts once
// loaded; not on LoadedMedia: the FFmpeg backend reports it again after the end and would restart). Loading
// is asynchronous: implicit size (natural video size), position and duration are 0 until then. Player errors go
// to vlqtError (shown + logged at the Video node) until the next source change. The player and its
// decoder threads are children of the item and go with DESTROY.
//
// Error state of an element: a QML string property vlqtError on the root item, empty = no error. Read by the poll
// (Elements::readError) and sent as EVENT_ERROR to the bound sink, i.e. shown at the element's vvvv node.
import QtQuick
import QtMultimedia
Item {
  id: root
  implicitWidth: out.sourceRect.width
  implicitHeight: out.sourceRect.height
  property alias source: player.source
  property bool playing: false
  property bool loop: false
  property bool keepLastFrame: false
  property alias playbackRate: player.playbackRate
  property alias volume: audio.volume
  property alias fillMode: out.fillMode
  readonly property alias vlqtPosition: player.position
  readonly property alias vlqtDuration: player.duration
  property string vlqtError: ""
  readonly property alias vlqtPlayer: player // audio tap (audio_tap.cpp)
  readonly property alias vlqtOut: out
  // Local path of a file URL for messages (file:///C:/a%20b.png -> C:/a b.png, file:///home/x -> /home/x).
  function vlqtPath(u) { return decodeURIComponent(String(u).replace(/^file:\/\/(\/(?=[A-Za-z]:))?/, "")); }
  function vlqtSync() { if (playing) player.play(); else player.pause(); }
  function vlqtRelease() { playing = false; player.stop(); player.source = ""; }
  onPlayingChanged: vlqtSync()
  onLoopChanged: {
    player.loops = loop ? MediaPlayer.Infinite : 1;
    if (!loop && player.playbackState === MediaPlayer.PlayingState) player.position = player.position;
  }
  MediaPlayer {
    id: player
    loops: 1
    audioOutput: AudioOutput { id: audio }
    videoOutput: out
    onSourceChanged: { root.vlqtError = ""; out.clearOutput(); root.vlqtSync(); }
    onErrorOccurred: (error, errorString) => root.vlqtError = errorString + " (" + root.vlqtPath(source) + ")"
  }
  VideoOutput {
    id: out
    anchors.fill: parent
    endOfStreamPolicy: root.keepLastFrame ? VideoOutput.KeepLastFrame : VideoOutput.ClearOutput
  }
}
