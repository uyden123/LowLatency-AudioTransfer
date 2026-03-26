#include <jni.h>
#include <oboe/Oboe.h>
#include <android/log.h>
#include <cstring>
#include <mutex>
#include <atomic>
#include <vector>
#include "ring_buffer.h"

#define LOG_TAG "AAudioRecorder"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO,  LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

#include <pthread.h>
// #include "audio_processing_engine.h" (removed)

class InputCallback : public oboe::AudioStreamCallback {
public:
    explicit InputCallback(std::shared_ptr<RingBuffer<int16_t>> ringBuffer) 
        : ringBuffer_(std::move(ringBuffer)), isThreadPrioritySet_(false) {}

    oboe::DataCallbackResult onAudioReady(
            oboe::AudioStream* stream,
            void* audioData,
            int32_t numFrames) override {

        if (!isThreadPrioritySet_) {
            isThreadPrioritySet_ = true;
            struct sched_param param;
            param.sched_priority = 3;
            if (pthread_setschedparam(pthread_self(), SCHED_FIFO, &param) == 0)
                LOGI("Recorder thread elevated to SCHED_FIFO priority 3");
            else
                LOGI("SCHED_FIFO elevation failed (non-root); running at default priority");
        }

        auto* input        = static_cast<const int16_t*>(audioData);
        // OPT: compute totalSamples directly — avoids an extra load of channelCount.
        int   totalSamples = numFrames * stream->getChannelCount();

        // RingBuffer::write() bulk-copies in a tight loop; no per-element overhead.
        int written = ringBuffer_->write(input, totalSamples);
        if (written < totalSamples) {
            // Overrun: Java consumer is lagging. Track for diagnostics.
            overrunSamples_ += (totalSamples - written);
        }

        return oboe::DataCallbackResult::Continue;
    }

private:
    std::shared_ptr<RingBuffer<int16_t>> ringBuffer_;
    bool isThreadPrioritySet_;
    int  overrunSamples_ = 0; // diagnostic: samples lost due to ring buffer full
};

static std::shared_ptr<RingBuffer<int16_t>> gRingBuffer;
static std::unique_ptr<InputCallback> gCallback;
static std::shared_ptr<oboe::AudioStream> gStream;
static std::mutex gMutex;
static std::atomic<bool> gIsRunning{false};

// AEC Engine (removed)

extern "C" {

JNIEXPORT jboolean JNICALL
Java_com_example_audiooverlan_audio_AAudioRecorder_nativeStart(
        JNIEnv* env, jobject thiz, jint sampleRate, jint channelCount, jboolean is_exclusive, jint deviceId) {

    std::lock_guard<std::mutex> lock(gMutex);

    if (gIsRunning.load()) return JNI_TRUE;

    // Ring buffer: hold ~200ms of audio
    int ringCapacity = sampleRate * channelCount * 200 / 1000;
    gRingBuffer = std::make_shared<RingBuffer<int16_t>>(ringCapacity + 1);
    gCallback = std::make_unique<InputCallback>(gRingBuffer);

    oboe::AudioStreamBuilder builder;
    builder.setDirection(oboe::Direction::Input)
           ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setSharingMode(is_exclusive ? oboe::SharingMode::Exclusive : oboe::SharingMode::Shared)
           ->setFormat(oboe::AudioFormat::I16)
           ->setChannelCount(channelCount)
           ->setSampleRate(sampleRate)
           ->setDeviceId(deviceId)
           ->setUsage(oboe::Usage::VoiceCommunication)
           ->setInputPreset(oboe::InputPreset::VoiceCommunication)
           ->setDataCallback(gCallback.get());

    oboe::Result result = builder.openStream(gStream);
    if (result != oboe::Result::OK) {
        LOGE("Failed to open input stream: %s", oboe::convertToText(result));
        return JNI_FALSE;
    }

    result = gStream->requestStart();
    if (result != oboe::Result::OK) {
        LOGE("Failed to start input stream: %s", oboe::convertToText(result));
        gStream->close();
        gStream.reset();
        return JNI_FALSE;
    }

    gIsRunning.store(true);
    LOGI("AAudioRecorder started: %d Hz, %d channels", sampleRate, channelCount);
    return JNI_TRUE;
}

JNIEXPORT jint JNICALL
Java_com_example_audiooverlan_audio_AAudioRecorder_nativeRead(
        JNIEnv* env, jobject thiz, jshortArray data, jint offset, jint length) {

    jint arrayLen = env->GetArrayLength(data);
    if (offset + length > arrayLen) {
        length = arrayLen - offset;
    }

    jshort* buffer = env->GetShortArrayElements(data, nullptr);
    if (!buffer) return 0; // Check for out of memory

    int read = 0;

    std::shared_ptr<RingBuffer<int16_t>> ringBuffer;
    {
        std::lock_guard<std::mutex> lock(gMutex);
        if (gIsRunning.load() && gRingBuffer) {
            ringBuffer = gRingBuffer;
        } else {
            env->ReleaseShortArrayElements(data, buffer, JNI_ABORT);
            return -1;
        }
    }
    
    read = ringBuffer->read(buffer + offset, length, true);

    env->ReleaseShortArrayElements(data, buffer, 0); // Commit back to Java array
    return read;
}

JNIEXPORT void JNICALL
Java_com_example_audiooverlan_audio_AAudioRecorder_nativeStop(
        JNIEnv* env, jobject thiz) {

    // BUG-3 FIX: Do NOT hold gMutex while calling gStream->close().
    // close() blocks until the onAudioReady callback returns. If the callback
    // ever acquires gMutex (easy to add by mistake), we deadlock.
    // Solution: set gIsRunning=false and grab a local copy of the stream under
    // the lock, then release the lock BEFORE calling close().
    std::shared_ptr<oboe::AudioStream> streamToClose;
    std::shared_ptr<RingBuffer<int16_t>> ringToReset;
    {
        std::lock_guard<std::mutex> lock(gMutex);
        gIsRunning.store(false);
        streamToClose = std::move(gStream); // take ownership; gStream is now null
        ringToReset   = gRingBuffer;        // keep ring alive for reset below
        gCallback.reset();
        gRingBuffer.reset();
    }

    // Stream close and ring reset happen outside the lock — callback is safe to
    // run concurrently (it checks gIsRunning first and writes nothing after false).
    if (streamToClose) {
        streamToClose->requestStop();
        streamToClose->close();
        LOGI("AAudioRecorder stopped");
    }
    if (ringToReset) {
        ringToReset->reset(); // Wake up any blocking readers in the Java layer
    }
}

JNIEXPORT jboolean JNICALL
Java_com_example_audiooverlan_audio_AAudioRecorder_nativeIsAAudioSupported(
        JNIEnv* env, jobject thiz) {
    return oboe::AudioStreamBuilder::isAAudioSupported() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jstring JNICALL
Java_com_example_audiooverlan_audio_AAudioRecorder_nativeGetStreamInfo(
        JNIEnv* env, jobject thiz) {
    std::shared_ptr<oboe::AudioStream> stream;
    {
        std::lock_guard<std::mutex> lock(gMutex);
        if (!gStream || !gIsRunning.load()) {
            return env->NewStringUTF("No active input stream");
        }
        stream = gStream;
    }

    char info[512];
    snprintf(info, sizeof(info),
             "API: %s | SharingMode: %s | PerfMode: %s | SampleRate: %d",
             stream->getAudioApi() == oboe::AudioApi::AAudio ? "AAudio" : "OpenSLES",
             stream->getSharingMode() == oboe::SharingMode::Exclusive ? "Exclusive" : "Shared",
             stream->getPerformanceMode() == oboe::PerformanceMode::LowLatency ? "LowLatency" : "Other",
             stream->getSampleRate());

    return env->NewStringUTF(info);
}

} // extern "C"