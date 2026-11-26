/* resource_events.h - the status queue of the resource manager (vlqt_res_poll).
 *
 * Records have the header of the window poll: u16 event, u16 flags (0), u32 length, u64 id, payload (4-byte
 * aligned, length = 8 + payload bytes). Main thread only, like the rest of the manager (no lock).
 */
#pragma once

#include <QtCore/QByteArray>

#include <cstdint>

namespace vlqt::res {

class ResourceEventQueue
{
public:
    // Appends one record (VLQT_RESOURCE_EVENT_*).
    void push(uint16_t ev, uint64_t id, const void* payload, int payloadLen);
    // Removes the queued records of id (host release).
    void drop(uint64_t id);
    // Copies every record out and empties the queue; the buffer convention of the poll exports (app/exports.h).
    int poll(unsigned char* out, int cap, int* written, int* required);
    void clear() { m_events.resize(0); }

private:
    QByteArray m_events;
};

} // namespace vlqt::res
