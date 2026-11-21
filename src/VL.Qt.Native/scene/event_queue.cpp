#include "scene/event_queue.h"
#include "app/exports.h"
#include "protocol.h"
#include "wire.h"

#include <cstring>

namespace vlqt {

void EventQueue::push(quint16 ev, quint32 feedbackId, const void* payload, qsizetype payloadLen)
{
    // The record length is a u32: a payload that does not fit is dropped, never cut or wrapped.
    if (payloadLen < 0 || quint64(payloadLen) > quint64(0xffffffffu) - VLQT_EVENT_FIELDS_SIZE) {
        qWarning("vlqt: event %u with %lld payload bytes dropped (record length is a u32)", unsigned(ev), qint64(payloadLen));
        return;
    }
    const quint32 len = quint32(VLQT_EVENT_FIELDS_SIZE + payloadLen); // feedbackId + payload, without padding
    const quint16 flags = 0;
    const qsizetype at = records.size();
    records.resize(at + qsizetype(align4(qint64(VLQT_HEADER_SIZE) + len)), '\0');
    char* d = records.data() + at;
    writeLE(d, ev);
    writeLE(d + 2, flags);
    writeLE(d + 4, len);
    writeLE(d + VLQT_HEADER_SIZE, feedbackId);
    if (payloadLen)
        std::memcpy(d + VLQT_HEADER_SIZE + VLQT_EVENT_FIELDS_SIZE, payload, size_t(payloadLen));
}

void EventQueue::pushFlag(quint16 ev, quint32 feedbackId, bool on)
{
    const quint8 v = on ? 1 : 0;
    push(ev, feedbackId, &v, 1);
}

int EventQueue::drain(unsigned char* out, int cap, int* written, int* required)
{
    const int rc = copyOut(records.constData(), int(records.size()), out, cap, written, required);
    if (rc == VLQT_OK)
        records.resize(0);
    return rc;
}

} // namespace vlqt
