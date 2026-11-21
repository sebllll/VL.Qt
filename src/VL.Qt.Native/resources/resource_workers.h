/* resource_workers.h - the worker pool of the resource manager: decodes run here, on pure data.
 *
 * A fixed pool (default min(4, cores - 1), environment variable VLQT_RES_WORKERS, started at the first job) takes jobs
 * from a queue, runs Kind::decode and queues the result for the main thread. Workers never touch Qt Quick, the RHI or
 * the resource table and never log. The pool does not own its mutex: it is the mutex of the manager (resource_manager.h),
 * which guards the job and result queues together with the table. Every function except join() expects the caller to
 * hold it.
 */
#pragma once

#include "resources/resource_kind.h"

#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace vlqt::res {

struct Job
{
    uint64_t id;
    Kind* kind;
    std::shared_ptr<Content> work;
};

struct Result
{
    uint64_t id;
    std::string error;
};

// Worker thread (or the thread of vlqt_res_wait): the decode on pure data, without the manager's mutex. An exception
// of the decode becomes the error text.
std::string runDecode(const Job& job);

class WorkerPool
{
public:
    explicit WorkerPool(std::mutex& mutex) : m_mutex(mutex) {}

    // Queues a decode; starts the threads at the first call.
    void submit(uint64_t id, Kind* kind, std::shared_ptr<Content> work);
    // Takes the job of id out of the queue if no worker started it yet.
    bool takeQueued(uint64_t id, Job& out);
    // The result of a job that the caller decoded itself.
    void putResult(Result result);
    // Takes the oldest result, false if there is none.
    bool popResult(Result& out);
    // Blocks (lock released while waiting) until the result of id is there or the deadline passes; true = taken into out.
    bool waitFor(std::unique_lock<std::mutex>& lock, uint64_t id, std::chrono::steady_clock::time_point deadline, Result& out);
    // Jobs waiting for a worker.
    int queuedJobs() const { return int(m_jobs.size()); }

    // Shutdown in three steps: stop() under the lock, join() without it, reset() under the lock again.
    std::vector<std::thread> stop();
    void join(std::vector<std::thread>& threads);
    void reset();

private:
    void workerLoop(int index);

    std::mutex& m_mutex;
    std::condition_variable m_jobCv, m_resultCv;
    std::deque<Job> m_jobs;
    std::deque<Result> m_results;
    std::vector<std::thread> m_threads;
    bool m_stop = false;
};

} // namespace vlqt::res
