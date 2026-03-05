#ifndef RING_BUFFER_H
#define RING_BUFFER_H

#include <vector>
#include <atomic>
#include <algorithm>

/**
 * Lock-free ring buffer for feeding PCM data.
 */
template <typename T>
class RingBuffer {
public:
    explicit RingBuffer(int capacity) : capacity_(capacity), buffer_(capacity), readIdx_(0), writeIdx_(0) {}

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

    int write(const T* data, int count) {
        int avail = availableToWrite();
        int toWrite = std::min(count, avail);
        int w = writeIdx_.load(std::memory_order_relaxed);

        for (int i = 0; i < toWrite; i++) {
            buffer_[w] = data[i];
            w++;
            if (w >= capacity_) w = 0;
        }

        writeIdx_.store(w, std::memory_order_release);
        return toWrite;
    }

    int read(T* data, int count) {
        int avail = availableToRead();
        int toRead = std::min(count, avail);
        int r = readIdx_.load(std::memory_order_relaxed);

        for (int i = 0; i < toRead; i++) {
            data[i] = buffer_[r];
            r++;
            if (r >= capacity_) r = 0;
        }

        readIdx_.store(r, std::memory_order_release);
        return toRead;
    }

    void reset() {
        readIdx_.store(0, std::memory_order_release);
        writeIdx_.store(0, std::memory_order_release);
    }

private:
    int capacity_;
    std::vector<T> buffer_;
    std::atomic<int> readIdx_;
    std::atomic<int> writeIdx_;
};

#endif // RING_BUFFER_H
