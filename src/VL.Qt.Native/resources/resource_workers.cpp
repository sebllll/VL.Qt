#include "resources/resource_workers.h"

#include "threadname.h"

#include <QtCore/QtGlobal>

#include <algorithm>
#include <cstdio>
#include <exception>

namespace vlqt::res {

std::string runDecode(const Job& job)
{
    try {
        return job.work->cancel.load() ? "cancelled" : job.kind->decode(*job.work);
    } catch (const std::exception& e) {
        return std::string("exception in decode: ") + e.what();
    } catch (...) {
        return "unknown exception in decode";
    }
}

void WorkerPool::submit(uint64_t id, Kind* kind, std::shared_ptr<Content> work)
{
    if (m_threads.empty()) {
        int n = qEnvironmentVariableIntValue("VLQT_RES_WORKERS");
        if (n <= 0)
            n = std::clamp(int(std::thread::hardware_concurrency()) - 1, 1, 4);
        n = std::min(n, 16);
        for (int i = 0; i < n; i++)
            m_threads.emplace_back([this, i] { workerLoop(i); });
        qCInfo(lcRes, "%d resource worker(s) started", n);
    }
    m_jobs.push_back({id, kind, std::move(work)});
    m_jobCv.notify_one();
}

bool WorkerPool::takeQueued(uint64_t id, Job& out)
{
    auto queued = std::find_if(m_jobs.begin(), m_jobs.end(), [id](const Job& j) { return j.id == id; });
    if (queued == m_jobs.end())
        return false;
    out = std::move(*queued);
    m_jobs.erase(queued);
    return true;
}

void WorkerPool::putResult(Result result)
{
    m_results.push_back(std::move(result));
}

bool WorkerPool::popResult(Result& out)
{
    if (m_results.empty())
        return false;
    out = std::move(m_results.front());
    m_results.pop_front();
    return true;
}

bool WorkerPool::waitFor(std::unique_lock<std::mutex>& lock, uint64_t id, std::chrono::steady_clock::time_point deadline,
    Result& out)
{
    auto mine = [&] {
        return std::find_if(m_results.begin(), m_results.end(), [id](const Result& r) { return r.id == id; });
    };
    if (!m_resultCv.wait_until(lock, deadline, [&] { return mine() != m_results.end(); }))
        return false;
    auto it = mine();
    out = std::move(*it);
    m_results.erase(it);
    return true;
}

std::vector<std::thread> WorkerPool::stop()
{
    m_stop = true;
    std::vector<std::thread> threads;
    threads.swap(m_threads);
    return threads;
}

void WorkerPool::join(std::vector<std::thread>& threads)
{
    m_jobCv.notify_all();
    for (std::thread& t : threads)
        t.join();
}

void WorkerPool::reset()
{
    m_jobs.clear();
    m_results.clear();
    m_stop = false; // the next submit creates the workers again
}

// Worker thread: only the queues under the mutex, decode on pure data without it. Never logs.
void WorkerPool::workerLoop(int index)
{
    char name[16];
    std::snprintf(name, sizeof name, "vlqt-res-%d", index);
    vlqt::setCurrentThreadName(name);
    std::unique_lock lock(m_mutex);
    for (;;) {
        m_jobCv.wait(lock, [this] { return m_stop || !m_jobs.empty(); });
        if (m_stop)
            return;
        Job job = std::move(m_jobs.front());
        m_jobs.pop_front();
        lock.unlock();
        std::string error = runDecode(job);
        job.work.reset(); // the main thread owns the Content again
        lock.lock();
        if (m_stop)
            return; // shutdown frees everything itself
        m_results.push_back({job.id, std::move(error)});
        m_resultCv.notify_all();
    }
}

} // namespace vlqt::res
