#include <jni.h>
#include <oboe/Oboe.h>
#include <android/log.h>
#include <cstring>
#include <mutex>
#include <atomic>
#include <vector>
#include <algorithm>

#define LOG_TAG "AAudioPlayer"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO,  LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN,  LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

#include "ring_buffer.h"
// Forward declaration of conversion helper (removed)

class AudioCallback : public oboe::AudioStreamCallback {
public:
    explicit AudioCallback(RingBuffer<int16_t>* ringBuffer) : ringBuffer_(ringBuffer) {}

    oboe::DataCallbackResult onAudioReady(
            oboe::AudioStream* stream,
            void* audioData,
            int32_t numFrames) override {

        auto* output = static_cast<int16_t*>(audioData);
        int channels = stream->getChannelCount();
        int totalSamples = numFrames * channels;

        int read = ringBuffer_->read(output, totalSamples);

        // Fill remaining with silence if ring buffer underran
        if (read < totalSamples) {
            std::memset(output + read, 0, (totalSamples - read) * sizeof(int16_t));
        }

        // AEC feeding removed (AEC feature disabled)

        return oboe::DataCallbackResult::Continue;
    }

    void onErrorBeforeClose(oboe::AudioStream* stream, oboe::Result error) override {
        LOGE("Error before close: %s", oboe::convertToText(error));
    }

    void onErrorAfterClose(oboe::AudioStream* stream, oboe::Result error) override {
        LOGE("Error after close: %s", oboe::convertToText(error));
    }

private:
    RingBuffer<int16_t>* ringBuffer_;
};

// Global state
static std::unique_ptr<RingBuffer<int16_t>> gRingBuffer;
static std::unique_ptr<AudioCallback> gCallback;
static std::shared_ptr<oboe::AudioStream> gStream;
static std::mutex gMutex;
static std::atomic<bool> gIsRunning{false};

extern "C" {

/**
 * Initialize and start the AAudio stream.
 * @param sampleRate - audio sample rate (e.g., 48000)
 * @param channelCount - number of channels (1 or 2)
 * @param framesPerBuffer - desired frames per buffer (0 for auto)
 * @return true if successfully started
 */
JNIEXPORT jboolean JNICALL
Java_com_example_audiooverlan_audio_AAudioPlayer_nativeStart(
        JNIEnv* env, jobject thiz,
        jint sampleRate, jint channelCount, jint framesPerBuffer, jboolean is_exclusive) {

    std::lock_guard<std::mutex> lock(gMutex);

    if (gIsRunning.load()) {
        LOGW("AAudioPlayer already running, stopping first...");
        if (gStream) {
            gStream->stop();
            gStream->close();
            gStream.reset();
        }
        gIsRunning.store(false);
    }

    // Ring buffer: hold ~200ms of audio for safety
    int ringCapacity = sampleRate * channelCount * 200 / 1000;
    gRingBuffer = std::make_unique<RingBuffer<int16_t>>(ringCapacity + 1);
    gCallback = std::make_unique<AudioCallback>(gRingBuffer.get());

    oboe::AudioStreamBuilder builder;
    builder.setDirection(oboe::Direction::Output)
           ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setSharingMode(is_exclusive ? oboe::SharingMode::Exclusive : oboe::SharingMode::Shared)
           ->setFormat(oboe::AudioFormat::I16)
           ->setChannelCount(channelCount)
           ->setSampleRate(sampleRate)
           ->setDataCallback(gCallback.get())
           ->setUsage(oboe::Usage::Media)
           ->setContentType(oboe::ContentType::Music);

    if (framesPerBuffer > 0) {
        builder.setFramesPerDataCallback(framesPerBuffer);
    }

    oboe::Result result = builder.openStream(gStream);
    if (result != oboe::Result::OK) {
        LOGE("Failed to open stream: %s", oboe::convertToText(result));
        return JNI_FALSE;
    }

    LOGI("AAudio stream opened: sampleRate=%d, channelCount=%d, framesPerBurst=%d, bufferSizeFrames=%d, sharingMode=%s, perfMode=%s",
         gStream->getSampleRate(),
         gStream->getChannelCount(),
         gStream->getFramesPerBurst(),
         gStream->getBufferSizeInFrames(),
         gStream->getSharingMode() == oboe::SharingMode::Exclusive ? "Exclusive" : "Shared",
         gStream->getPerformanceMode() == oboe::PerformanceMode::LowLatency ? "LowLatency" : "Other");

    // Set buffer size to minimum for lowest latency
    gStream->setBufferSizeInFrames(gStream->getFramesPerBurst());

    result = gStream->requestStart();
    if (result != oboe::Result::OK) {
        LOGE("Failed to start stream: %s", oboe::convertToText(result));
        gStream->close();
        gStream.reset();
        return JNI_FALSE;
    }

    gIsRunning.store(true);
    LOGI("AAudioPlayer started successfully");
    return JNI_TRUE;
}

/**
 * Write PCM samples to the ring buffer. Called from Java playback thread.
 * @param samples - short[] PCM data
 * @param offset - start offset in the array
 * @param length - number of samples to write
 * @return number of samples actually written
 */
JNIEXPORT jint JNICALL
Java_com_example_audiooverlan_audio_AAudioPlayer_nativeWrite(
        JNIEnv* env, jobject thiz,
        jshortArray samples, jint offset, jint length) {

    if (!gIsRunning.load() || !gRingBuffer) {
        return 0;
    }

    jint arrayLen = env->GetArrayLength(samples);
    if (offset + length > arrayLen) {
        length = arrayLen - offset;
    }

    jshort* data = env->GetShortArrayElements(samples, nullptr);
    int written = gRingBuffer->write(data + offset, length);
    env->ReleaseShortArrayElements(samples, data, JNI_ABORT);

    return written;
}

/**
 * Stop and release the AAudio stream.
 */
JNIEXPORT void JNICALL
Java_com_example_audiooverlan_audio_AAudioPlayer_nativeStop(
        JNIEnv* env, jobject thiz) {

    std::lock_guard<std::mutex> lock(gMutex);

    gIsRunning.store(false);

    if (gStream) {
        gStream->stop();
        gStream->close();
        gStream.reset();
        LOGI("AAudioPlayer stopped");
    }

    gCallback.reset();
    gRingBuffer.reset();
}

/**
 * Get the actual latency of the audio stream in milliseconds.
 */
JNIEXPORT jdouble JNICALL
Java_com_example_audiooverlan_audio_AAudioPlayer_nativeGetLatencyMs(
        JNIEnv* env, jobject thiz) {

    if (!gStream || !gIsRunning.load()) {
        return -1.0;
    }

    auto resultPair = gStream->calculateLatencyMillis();
    if (resultPair) {
        return resultPair.value();
    }
    return -1.0;
}

/**
 * Check if AAudio is available on this device (API 27+, Oreo MR1).
 */
JNIEXPORT jboolean JNICALL
Java_com_example_audiooverlan_audio_AAudioPlayer_nativeIsAAudioSupported(
        JNIEnv* env, jobject thiz) {

    return oboe::AudioStreamBuilder::isAAudioSupported() ? JNI_TRUE : JNI_FALSE;
}

/**
 * Get the current buffer capacity info as a string for debugging.
 */
JNIEXPORT jstring JNICALL
Java_com_example_audiooverlan_audio_AAudioPlayer_nativeGetStreamInfo(
        JNIEnv* env, jobject thiz) {

    if (!gStream || !gIsRunning.load()) {
        return env->NewStringUTF("No active stream");
    }

    char info[512];
    snprintf(info, sizeof(info),
             "API: %s | SharingMode: %s | PerfMode: %s | BufferSize: %d frames | Burst: %d frames | SampleRate: %d",
             gStream->getAudioApi() == oboe::AudioApi::AAudio ? "AAudio" : "OpenSLES",
             gStream->getSharingMode() == oboe::SharingMode::Exclusive ? "Exclusive" : "Shared",
             gStream->getPerformanceMode() == oboe::PerformanceMode::LowLatency ? "LowLatency" : "Other",
             gStream->getBufferSizeInFrames(),
             gStream->getFramesPerBurst(),
             gStream->getSampleRate());

    return env->NewStringUTF(info);
}

} // extern "C"
