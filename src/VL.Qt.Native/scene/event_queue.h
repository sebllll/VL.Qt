/* scene/event_queue.h - the records a scene queues for the next poll (vlqt_scene_poll; formats in protocol.h). */
#pragma once

#include <QtCore/QByteArray>
#include <QtCore/QtTypes>

namespace vlqt {

class EventQueue
{
public:
    /* Appends one record u16 event, u16 flags (0), u32 len, u32 feedbackId, payload (padded to 4);
     * len = 4 + payloadLen (feedbackId + payload, without padding). A payload whose len would not fit a u32 is dropped. */
    void push(quint16 ev, quint32 feedbackId, const void* payload, qsizetype payloadLen);
    /* Appends a record with a u8 payload: 1 = on, 0 = off. */
    void pushFlag(quint16 ev, quint32 feedbackId, bool on);
    /* Copies the queued records into out and clears the queue: a buffer export (vlqt.h); on VLQT_ERR_BUFFER_TOO_SMALL
     * the records stay queued. */
    int drain(unsigned char* out, int cap, int* written, int* required);

private:
    QByteArray records;
};

} // namespace vlqt
