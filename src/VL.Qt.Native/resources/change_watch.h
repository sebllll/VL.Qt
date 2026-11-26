/* change_watch.h - which elements wait for which resource id, so a pump advances only the elements whose resource got
 * new Ready content or became Failed (res::changedThisFrame), not every live element.
 *
 * An element adds (id, this) when it takes an element reference for an id it waits for or shows, and removes the same
 * pair when it drops that reference. forChanged calls the visitor for the elements of every changed id; an element
 * may be visited more than once in a pump, so the visitor must be idempotent (advance does nothing without news).
 * Main thread. Allocation only when ids are added or the busiest pump so far grows; a steady pump allocates nothing.
 */
#pragma once

#include "resources/resources.h"

#include <algorithm>
#include <cstdint>
#include <unordered_map>
#include <vector>

namespace vlqt::res {

template <class T>
class ChangeWatch
{
public:
    void add(uint64_t id, T* item)
    {
        if (id)
            m_items[id].push_back(item);
    }

    // One registration of (id, item), if any.
    void remove(uint64_t id, T* item)
    {
        const auto it = m_items.find(id);
        if (it == m_items.end())
            return;
        auto& list = it->second;
        const auto at = std::find(list.begin(), list.end(), item);
        if (at != list.end())
            list.erase(at);
        if (list.empty())
            m_items.erase(it);
    }

    // The visitor may add or remove registrations (a signal of the item can reach back): it visits a snapshot of the
    // items, taken into a vector that keeps its capacity.
    template <class F>
    void forChanged(F&& visit)
    {
        if (m_items.empty())
            return;
        m_visit.clear();
        for (const uint64_t id : changedThisFrame()) {
            const auto it = m_items.find(id);
            if (it != m_items.end())
                m_visit.insert(m_visit.end(), it->second.begin(), it->second.end());
        }
        for (T* item : m_visit)
            visit(item);
    }

private:
    std::unordered_map<uint64_t, std::vector<T*>> m_items;
    std::vector<T*> m_visit;
};

} // namespace vlqt::res
