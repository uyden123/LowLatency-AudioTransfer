#ifndef RING_BUFFER_H
#define RING_BUFFER_H

#include <vector>
#include <atomic>
#include <algorithm>
#include <mutex>
#include <condition_variable>

/**
 * Single-producer / single-reader lock-free ring buffer for PCM audio.
 *
 * PERFORMANCE DESIGN:
 *   Normal path (no blocking writers): read() and non-blocking write() are
 *   entirely lock-free — they use only atomic loads/stores with appropriate
 *   memory ordering. No mutex is touched.
 *
 *   Blocking write path: write() with blocking=true is the only code that
 *   acquires the mutex. It sets hasBlockingWriter_ = true BEFORE taking the
 *   lock (so read() can see it) and clears it after waking. read() checks
 *   this atomic flag and only acquires the mutex + notifies when a writer is
 *   actually sleeping — avoiding a kernel call on every read in the normal case.
 *
 *   BUG-11 FIX: readIdx_ is stored under the mutex only when a blocking writer
 *   is waiting. This ensures the writer's lambda [this]{ return avail > 0; }
 *   observes the updated index without a data race.
 */
template <typename T>
class RingBuffer {
public:
    explicit RingBuffer(int capacity)
        : capacity_(capacity), buffer_(capacity),
          readIdx_(0), writeIdx_(0), hasBlockingWriter_(false), hasBlockingReader_(false) {}

    int availableToRead() const {
        int w = writeIdx_.load(std::memory_order_acquire);
        int r = readIdx_.load(std::memory_order_relaxed);
        int avail = w - r;
        if (avail < 0) avail += capacity_;
        return avail;
    }

    int availableToWrite() const {
        return capacity_ - 1 - availableToRead();
    }

    // ── Non-blocking or blocking write ────────────────────────────────────────
    int write(const T* data, int count, bool blocking = false) {
        int written = 0;

        while (written < count) {
            int avail = availableToWrite();
            if (avail <= 0) {
                if (!blocking) break;

                // Signal to read() that we are about to sleep so it will notify us.
                hasBlockingWriter_.store(true, std::memory_order_release);

                // Re-check under lock to close the window between the flag store
                // and actually sleeping (avoids a missed notification).
                std::unique_lock<std::mutex> lock(mu_);
                space_cond_.wait_for(lock, std::chrono::milliseconds(20),
                    [this] { return availableToWrite() > 0; });

                hasBlockingWriter_.store(false, std::memory_order_relaxed);

                if (availableToWrite() <= 0) break; // timed out or still full
                continue;
            }

            int w = writeIdx_.load(std::memory_order_relaxed);
            int toWrite = std::min(count - written, avail);
            for (int i = 0; i < toWrite; i++) {
                buffer_[w] = data[written + i];
                if (++w >= capacity_) w = 0;
            }
            written += toWrite;
            
            if (toWrite > 0) {
                if (hasBlockingReader_.load(std::memory_order_acquire)) {
                    std::unique_lock<std::mutex> lock(mu_);
                    writeIdx_.store(w, std::memory_order_release);
                    data_cond_.notify_one();
                } else {
                    writeIdx_.store(w, std::memory_order_release);
                }
            }
        }

        return written;
    }

    // ── Lock-free or blocking read ──────────────────────────────────────────
    int read(T* data, int count, bool blocking = false) {
        int readCount = 0;

        while (readCount < count) {
            int avail = availableToRead();
            if (avail <= 0) {
                if (!blocking) break;

                hasBlockingReader_.store(true, std::memory_order_release);

                std::unique_lock<std::mutex> lock(mu_);
                data_cond_.wait_for(lock, std::chrono::milliseconds(20),
                    [this] { return availableToRead() > 0; });

                hasBlockingReader_.store(false, std::memory_order_relaxed);

                if (availableToRead() <= 0) break; // timed out
                continue;
            }

            int toRead = std::min(count - readCount, avail);
            int r      = readIdx_.load(std::memory_order_relaxed);

            for (int i = 0; i < toRead; i++) {
                data[readCount + i] = buffer_[r];
                if (++r >= capacity_) r = 0;
            }
            readCount += toRead;

            if (toRead > 0) {
                if (hasBlockingWriter_.load(std::memory_order_acquire)) {
                    std::unique_lock<std::mutex> lock(mu_);
                    readIdx_.store(r, std::memory_order_release);
                    space_cond_.notify_one();
                } else {
                    readIdx_.store(r, std::memory_order_release);
                }
            }
        }

        return readCount;
    }

    // ── Discard oldest data to make room ────────────────────────────────────
    void discard(int count) {
        int avail = availableToRead();
        int toDiscard = std::min(count, avail);
        if (toDiscard <= 0) return;
        int r = readIdx_.load(std::memory_order_relaxed);
        r = (r + toDiscard) % capacity_;
        if (hasBlockingWriter_.load(std::memory_order_acquire)) {
            std::unique_lock<std::mutex> lock(mu_);
            readIdx_.store(r, std::memory_order_release);
            space_cond_.notify_one();
        } else {
            readIdx_.store(r, std::memory_order_release);
        }
    }

    // ── Reset (called on stop/restart) ────────────────────────────────────────
    void reset() {
        readIdx_.store(0,  std::memory_order_release);
        writeIdx_.store(0, std::memory_order_release);
        // Wake any sleeping writer or reader so they can observe the stopped state.
        {
            std::unique_lock<std::mutex> lock(mu_);
            space_cond_.notify_all();
            data_cond_.notify_all();
        }
        hasBlockingWriter_.store(false, std::memory_order_relaxed);
        hasBlockingReader_.store(false, std::memory_order_relaxed);
    }

private:
    int              capacity_;
    std::vector<T>   buffer_;
    std::atomic<int> readIdx_;
    std::atomic<int> writeIdx_;

    // Guards space_cond_ and data_cond_
    std::mutex              mu_;
    std::condition_variable space_cond_;
    std::condition_variable data_cond_;

    // True while a writer/reader is sleeping.
    std::atomic<bool> hasBlockingWriter_;
    std::atomic<bool> hasBlockingReader_;
};

#endif // RING_BUFFER_H
