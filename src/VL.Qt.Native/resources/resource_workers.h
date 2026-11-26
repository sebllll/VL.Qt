/* resource_workers.h - the worker pool of the resource manager: decodes run here, on pure data.
 *
 * A fixed pool (default min(4, cores - 1), environment variable VLQT_RES_WORKERS, started at the first job) takes jobs
 * from a queue, runs Kind::decode and queues the result for the main thread. Workers never touch Qt Quick, the RHI or
 * the resource table and never log; they see only the Content of their job and the two queues.
 *
 * Threads: the main thread calls every function; the workers run workerLoop. m_mutex guards the job queue, the result
 * queue and m_stop, nothing else, and is never held across a decode. The functions lock it themselves.
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

// Worker thread (or the main thread in vlqt_res_wait): the decode on pure data, without any lock. An exception of the
// decode becomes the error text.
std::string runDecode(const Job& job);

class WorkerPool
{
public:
    // Queues a decode; starts the threads at the first call.
    void submit(uint64_t id, Kind* kind, std::shared_ptr<Content> work);
    // Takes the job of id out of the queue if no worker started it yet.
    bool takeQueued(uint64_t id, Job& out);
    // Takes the oldest result, false if there is none.
    bool popResult(Result& out);
    // Blocks until the result of id is there or the deadline passes; true = taken into out.
    bool waitFor(uint64_t id, std::chrono::steady_clock::time_point deadline, Result& out);
    // Jobs waiting for a worker.
    int queuedJobs() const;

    // Stops and joins the workers and empties both queues; the next submit starts them again.
    // Returns the number of joined workers.
    int shutdown();

private:
    void workerLoop(int index);

    mutable std::mutex m_mutex; // guards m_jobs, m_results and m_stop
    std::condition_variable m_jobCv, m_resultCv;
    std::deque<Job> m_jobs;
    std::deque<Result> m_results;
    bool m_stop = false;
    std::vector<std::thread> m_threads; // main thread only
};

} // namespace vlqt::res
