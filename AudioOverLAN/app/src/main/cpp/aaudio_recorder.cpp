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

// #include "audio_processing_engine.h" (removed)

class InputCallback : public oboe::AudioStreamCallback {
public:
    explicit InputCallback(RingBuffer<int16_t>* ringBuffer) : ringBuffer_(ringBuffer) {}

    oboe::DataCallbackResult onAudioReady(
            oboe::AudioStream* stream,
            void* audioData,
            int32_t numFrames) override {

        auto* input = static_cast<const int16_t*>(audioData);
        int channels = stream->getChannelCount();
        int totalSamples = numFrames * channels;

        int written = ringBuffer_->write(input, totalSamples);
        if (written < totalSamples) {
            // Overrun - could log or track this
        }

        return oboe::DataCallbackResult::Continue;
    }

private:
    RingBuffer<int16_t>* ringBuffer_;
};

static std::unique_ptr<RingBuffer<int16_t>> gRingBuffer;
static std::unique_ptr<InputCallback> gCallback;
static std::shared_ptr<oboe::AudioStream> gStream;
static std::mutex gMutex;
static std::atomic<bool> gIsRunning{false};

// AEC Engine (removed)

extern "C" {

JNIEXPORT jboolean JNICALL
Java_com_example_audiooverlan_audio_AAudioRecorder_nativeStart(
        JNIEnv* env, jobject thiz, jint sampleRate, jint channelCount, jboolean is_exclusive) {

    std::lock_guard<std::mutex> lock(gMutex);

    if (gIsRunning.load()) return JNI_TRUE;

    // Ring buffer: hold ~200ms of audio
    int ringCapacity = sampleRate * channelCount * 200 / 1000;
    gRingBuffer = std::make_unique<RingBuffer<int16_t>>(ringCapacity + 1);
    gCallback = std::make_unique<InputCallback>(gRingBuffer.get());

    oboe::AudioStreamBuilder builder;
    builder.setDirection(oboe::Direction::Input)
           ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
           ->setSharingMode(is_exclusive ? oboe::SharingMode::Exclusive : oboe::SharingMode::Shared)
           ->setFormat(oboe::AudioFormat::I16)
           ->setChannelCount(channelCount)
           ->setSampleRate(sampleRate)
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
    int read = 0;

    // AEC removed. Reading directly from ring buffer.
    if (gIsRunning.load() && gRingBuffer) {
        read = gRingBuffer->read(buffer + offset, length);
    } else {
        env->ReleaseShortArrayElements(data, buffer, JNI_ABORT);
        return -1;
    }

    env->ReleaseShortArrayElements(data, buffer, 0); // Commit back to Java array
    return read;
}

JNIEXPORT void JNICALL
Java_com_example_audiooverlan_audio_AAudioRecorder_nativeStop(
        JNIEnv* env, jobject thiz) {

    std::lock_guard<std::mutex> lock(gMutex);
    gIsRunning.store(false);

    if (gStream) {
        gStream->stop();
        gStream->close();
        gStream.reset();
        LOGI("AAudioRecorder stopped");
    }
    gCallback.reset();
    gRingBuffer.reset();
}

JNIEXPORT jboolean JNICALL
Java_com_example_audiooverlan_audio_AAudioRecorder_nativeIsAAudioSupported(
        JNIEnv* env, jobject thiz) {
    return oboe::AudioStreamBuilder::isAAudioSupported() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jstring JNICALL
Java_com_example_audiooverlan_audio_AAudioRecorder_nativeGetStreamInfo(
        JNIEnv* env, jobject thiz) {
    if (!gStream || !gIsRunning.load()) {
        return env->NewStringUTF("No active input stream");
    }

    char info[512];
    snprintf(info, sizeof(info),
             "API: %s | SharingMode: %s | PerfMode: %s | SampleRate: %d",
             gStream->getAudioApi() == oboe::AudioApi::AAudio ? "AAudio" : "OpenSLES",
             gStream->getSharingMode() == oboe::SharingMode::Exclusive ? "Exclusive" : "Shared",
             gStream->getPerformanceMode() == oboe::PerformanceMode::LowLatency ? "LowLatency" : "Other",
             gStream->getSampleRate());

    return env->NewStringUTF(info);
}

} // extern "C"