#ifndef LOCK_FREE_RING_BUFFER_H
#define LOCK_FREE_RING_BUFFER_H

#include <vector>
#include <atomic>
#include <cstring>
#include <algorithm>

// ============================================================================
// Lock-Free Ring Buffer (SPSC - Single Producer, Single Consumer)
// Producer: Java/JNI thread pushing decoded PCM
// Consumer: Oboe callback thread pulling PCM for playback
// ============================================================================
class LockFreeRingBuffer {
public:
    void allocate(int capacity) {
        mCapacity = capacity;
        mBuffer.resize(capacity);
        mReadPos.store(0, std::memory_order_relaxed);
        mWritePos.store(0, std::memory_order_relaxed);
    }

    // Returns number of samples available to read
    int available() const {
        int w = mWritePos.load(std::memory_order_acquire);
        int r = mReadPos.load(std::memory_order_relaxed);
        int avail = w - r;
        if (avail < 0) avail += mCapacity;
        return avail;
    }

    // Returns free space for writing
    int freeSpace() const {
        return mCapacity - 1 - available();
    }

    // Write samples into the ring buffer. Returns number actually written.
    int write(const int16_t* data, int count) {
        int free = freeSpace();
        if (count > free) count = free;
        if (count <= 0) return 0;

        int w = mWritePos.load(std::memory_order_relaxed);
        int firstChunk = std::min(count, mCapacity - w);
        std::memcpy(&mBuffer[w], data, firstChunk * sizeof(int16_t));
        if (count > firstChunk) {
            std::memcpy(&mBuffer[0], data + firstChunk, (count - firstChunk) * sizeof(int16_t));
        }
        mWritePos.store((w + count) % mCapacity, std::memory_order_release);
        return count;
    }

    // Read samples from the ring buffer. Returns number actually read.
    int read(int16_t* data, int count) {
        int avail = available();
        if (count > avail) count = avail;
        if (count <= 0) return 0;

        int r = mReadPos.load(std::memory_order_relaxed);
        int firstChunk = std::min(count, mCapacity - r);
        std::memcpy(data, &mBuffer[r], firstChunk * sizeof(int16_t));
        if (count > firstChunk) {
            std::memcpy(data + firstChunk, &mBuffer[0], (count - firstChunk) * sizeof(int16_t));
        }
        mReadPos.store((r + count) % mCapacity, std::memory_order_release);
        return count;
    }

    void reset() {
        mReadPos.store(0, std::memory_order_relaxed);
        mWritePos.store(0, std::memory_order_relaxed);
    }

private:
    std::vector<int16_t> mBuffer;
    int mCapacity = 0;
    std::atomic<int> mReadPos{0};
    std::atomic<int> mWritePos{0};
};

#endif // LOCK_FREE_RING_BUFFER_H
