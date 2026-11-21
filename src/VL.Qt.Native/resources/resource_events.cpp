#include "resources/resource_events.h"

#include "app/exports.h"
#include "protocol.h"
#include "resources/resource_kind.h"

#include <cstring>

namespace vlqt::res {

void ResourceEventQueue::push(uint16_t ev, uint64_t id, const void* payload, int payloadLen)
{
    const uint32_t len = uint32_t(8 + payloadLen); // id + payload, without padding
    const uint16_t flags = 0;
    const qsizetype at = m_events.size();
    m_events.resize(at + ((qsizetype(VLQT_HEADER_SIZE) + len + 3) & ~qsizetype(3)), '\0');
    char* d = m_events.data() + at;
    std::memcpy(d, &ev, 2);
    std::memcpy(d + 2, &flags, 2);
    std::memcpy(d + 4, &len, 4);
    std::memcpy(d + VLQT_HEADER_SIZE, &id, 8);
    if (payloadLen)
        std::memcpy(d + VLQT_HEADER_SIZE + 8, payload, size_t(payloadLen));
}

void ResourceEventQueue::drop(uint64_t id)
{
    qsizetype read = 0, write = 0;
    const qsizetype n = m_events.size();
    char* d = m_events.data();
    while (read + VLQT_HEADER_SIZE + 8 <= n) {
        uint32_t len;
        uint64_t rid;
        std::memcpy(&len, d + read + 4, 4);
        std::memcpy(&rid, d + read + VLQT_HEADER_SIZE, 8);
        const qsizetype size = (qsizetype(VLQT_HEADER_SIZE) + len + 3) & ~qsizetype(3);
        if (rid != id) {
            if (write != read)
                std::memmove(d + write, d + read, size_t(size));
            write += size;
        }
        read += size;
    }
    m_events.resize(write);
}

int ResourceEventQueue::poll(unsigned char* out, int cap, int* written, int* required)
{
    const int rc = copyOut(m_events.constData(), int(m_events.size()), out, cap, written, required);
    if (rc == okRc)
        m_events.resize(0);
    return rc;
}

} // namespace vlqt::res
