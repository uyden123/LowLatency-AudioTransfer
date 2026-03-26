#include <jni.h>
#include <oboe/Oboe.h>
#include <android/log.h>
#include <cstring>
#include <mutex>
#include <atomic>
#include <vector>
#include <algorithm>
#include <thread>
#include <chrono>
#include <opus.h>

#define LOG_TAG "AAudioPlayer"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO,  LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN,  LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

#include "lock_free_ring_buffer.h"

// ============================================================================
// Oboe Callback Player
// ============================================================================
class CallbackPlayer : public oboe::AudioStreamDataCallback, public oboe::AudioStreamErrorCallback {
private:
    float mTargetVolume = 1.0f;
    float mVolumeMul = 1.0f;
    std::atomic<bool> mWasUnderrun{false};
    float mFadeInGain = 1.0f; // 1.0 = no fade needed
    int16_t mLastSamples[2] = {0, 0}; // Track last sample for graceful fade-out

    void applyFade(int16_t* data, int32_t numFrames) {
        if (mVolumeMul == mTargetVolume) {
            if (mVolumeMul <= 0.0f) {
                std::memset(data, 0, numFrames * mChannelCount * sizeof(int16_t));
            } else if (mVolumeMul < 0.999f) {
                for (int i = 0; i < numFrames * mChannelCount; ++i) {
                    data[i] = static_cast<int16_t>(data[i] * mVolumeMul);
                }
            }
            return;
        }

        const int FADE_SAMPLES = 480; 
        const float fadeStep = 1.0f / FADE_SAMPLES;
        
        for (int i = 0; i < numFrames; ++i) {
            if (mVolumeMul < mTargetVolume) {
                mVolumeMul = std::min(mTargetVolume, mVolumeMul + fadeStep);
            } else if (mVolumeMul > mTargetVolume) {
                mVolumeMul = std::max(mTargetVolume, mVolumeMul - fadeStep);
            }

            for (int ch = 0; ch < mChannelCount; ++ch) {
                data[i * mChannelCount + ch] = static_cast<int16_t>(data[i * mChannelCount + ch] * mVolumeMul);
            }
        }
    }

public:
    // Called by Oboe on a high-priority thread to fill the audio output buffer
    oboe::DataCallbackResult onAudioReady(
            oboe::AudioStream *stream, void *audioData, int32_t numFrames) override {

        auto *output = static_cast<int16_t*>(audioData);
        int samplesNeeded = numFrames * mChannelCount;
        
        int samplesRead = 0;
        // Don't read from ring buffer if target is mute and fade is done
        if (mTargetVolume > 0 || mVolumeMul > 0.001f) {
            samplesRead = mRingBuffer.read(output, samplesNeeded);
        }

        int framesRead = samplesRead / mChannelCount;

        // Lưu mẫu dữ liệu cuối cùng của phần hợp lệ để làm điểm gốc cho fade-out
        if (framesRead > 0) {
            for (int ch = 0; ch < mChannelCount; ++ch) {
                mLastSamples[ch] = output[(framesRead - 1) * mChannelCount + ch];
            }
        }

        // 1. Xử lý thiếu data (Underrun) - như khi người dùng bấm Pause video
        if (framesRead < numFrames) {
            if (!mWasUnderrun.load(std::memory_order_relaxed)) {
                int currentUnderruns = mUnderrunCount.fetch_add(1, std::memory_order_relaxed) + 1;
                if (mTargetVolume > 0.1f) {
                    LOGW("Underrun started #%d: Requested %d frames, got %d", 
                         currentUnderruns, numFrames, framesRead);
                }
                mWasUnderrun.store(true, std::memory_order_relaxed);
            }

            // Lấp đầy phần thiếu bằng cách "fade-out" mẫu âm thanh tự nhiên về 0 
            // với Decay Rate 0.95 để triệt tiêu tiếng POP đột ngột
            for (int i = framesRead; i < numFrames; ++i) {
                for (int ch = 0; ch < mChannelCount; ++ch) {
                    mLastSamples[ch] = static_cast<int16_t>(mLastSamples[ch] * 0.95f);
                    output[i * mChannelCount + ch] = mLastSamples[ch];
                }
            }
        }

        // 2. Fade-in after underrun recovery to prevent silence→audio pop
        if (mWasUnderrun.load(std::memory_order_relaxed) && framesRead >= numFrames) {
            mWasUnderrun.store(false, std::memory_order_relaxed);
            mFadeInGain = 0.0f; // Start from silence, ramp up
        }
        if (mFadeInGain < 0.999f) {
            // Fade-in ramp over ~5ms (240 frames at 48kHz)
            const float fadeInStep = 1.0f / 240.0f;
            for (int i = 0; i < numFrames; ++i) {
                mFadeInGain = std::min(1.0f, mFadeInGain + fadeInStep);
                for (int ch = 0; ch < mChannelCount; ++ch) {
                    output[i * mChannelCount + ch] = static_cast<int16_t>(
                        output[i * mChannelCount + ch] * mFadeInGain);
                }
            }
        }

        // 3. Volume fade
        applyFade(output, numFrames);

        return oboe::DataCallbackResult::Continue;
    }

    void onErrorBeforeClose(oboe::AudioStream *stream, oboe::Result error) override {
        LOGE("Stream error before close: %s", oboe::convertToText(error));
    }

    void onErrorAfterClose(oboe::AudioStream *stream, oboe::Result error) override {
        LOGE("Stream error after close: %s", oboe::convertToText(error));
    }

    bool start(int sampleRate, int channelCount, bool exclusive, int usage, int contentType) {
        mChannelCount = channelCount;
        mSampleRate = sampleRate;

        // Ring buffer: ~250ms capacity. Back-pressure handles overflow for HIGH mode.
        int ringCapacity = sampleRate * channelCount / 4 + 1; // 250ms + 1
        mRingBuffer.allocate(ringCapacity);
        mRingBuffer.reset();
        mUnderrunCount.store(0);

        oboe::AudioStreamBuilder builder;
        builder.setDirection(oboe::Direction::Output)
               ->setAudioApi(oboe::AudioApi::AAudio)
               ->setPerformanceMode(oboe::PerformanceMode::LowLatency)
               ->setFormat(oboe::AudioFormat::I16)
               ->setChannelCount(channelCount)
               ->setUsage(static_cast<oboe::Usage>(usage))
               ->setContentType(static_cast<oboe::ContentType>(contentType))
               ->setDataCallback(this)
               ->setErrorCallback(this);

        // Try to open with requested sharing mode first
        builder.setSharingMode(exclusive ? oboe::SharingMode::Exclusive : oboe::SharingMode::Shared);
        oboe::Result result = builder.openStream(mStream);

        // Fallback: If exclusive fails, try shared
        if (result != oboe::Result::OK && exclusive) {
            LOGW("Failed to open in Exclusive mode: %s. Falling back to Shared.", oboe::convertToText(result));
            builder.setSharingMode(oboe::SharingMode::Shared);
            result = builder.openStream(mStream);
        }

        if (result != oboe::Result::OK) {
            LOGE("Failed to open stream (Final): %s", oboe::convertToText(result));
            return false;
        }

        // Apply burst buffer sizing
        int32_t burstSize = mStream->getFramesPerBurst();
        mStream->setBufferSizeInFrames(burstSize * 2);

        result = mStream->requestStart();
        if (result != oboe::Result::OK) {
            LOGE("Failed to start stream: %s", oboe::convertToText(result));
            mStream->close();
            mStream.reset();
            return false;
        }

        LOGI("Callback stream started");
        return true;
    }

    void setVolume(float volume) {
        mTargetVolume = volume;
    }

    void stop() {
        if (mStream) {
            mStream->requestStop();
            mStream->close();
            mStream.reset();
            LOGI("Callback stream stopped");
        }
    }

    // Push decoded PCM samples into the ring buffer (called from JNI thread)
    int pushSamples(const int16_t* data, int sampleCount) {
        return mRingBuffer.write(data, sampleCount);
    }

    int getBufferedSamples() const {
        return mRingBuffer.available();
    }

    int getFreeSpace() const {
        return mRingBuffer.freeSpace();
    }

    std::shared_ptr<oboe::AudioStream> getStream() { return mStream; }
    int getChannelCount() const { return mChannelCount; }
    int getSampleRate() const { return mSampleRate; }
    int getUnderrunCount() const { return mUnderrunCount.load(std::memory_order_relaxed); }

private:
    std::shared_ptr<oboe::AudioStream> mStream;
    LockFreeRingBuffer mRingBuffer;
    int mChannelCount = 2;
    int mSampleRate = 48000;
    std::atomic<int> mUnderrunCount{0};
};

// ============================================================================
// Global state
// ============================================================================
static std::unique_ptr<CallbackPlayer> gPlayer;
static std::mutex gMutex;
static std::atomic<bool> gIsRunning{false};
static std::vector<int16_t> gDecodeBuffer;
static std::vector<int16_t> gResampleBuffer;
static double gResamplePhase = 0.0;
// Last 4 samples per channel for cross-packet Cubic Hermite interpolation
static int16_t gPrevSamples[4][2] = {{0,0},{0,0},{0,0},{0,0}}; // [0]=oldest ... [3]=newest

// Cubic Hermite interpolation (4-point, higher quality than linear)
static inline float hermite4(float frac, float xm1, float x0, float x1, float x2) {
    float c1 = 0.5f * (x1 - xm1);
    float c2 = xm1 - 2.5f * x0 + 2.0f * x1 - 0.5f * x2;
    float c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
    return ((c3 * frac + c2) * frac + c1) * frac + x0;
}

// Native Opus Decoder state
static OpusDecoder* gDecoder = nullptr;
static int gDecoderSampleRate = 48000;
static int gDecoderChannels = 2;

extern "C" {

JNIEXPORT jboolean JNICALL
Java_com_example_audiooverlan_audio_AAudioPlayer_nativeStart(
        JNIEnv* env, jobject thiz,
        jint sampleRate, jint channelCount, jint framesPerBuffer, jboolean is_exclusive, jint usage, jint contentType) {

    std::lock_guard<std::mutex> lock(gMutex);

    // Stop existing
    if (gIsRunning.load()) {
        if (gPlayer) gPlayer->stop();
        if (gDecoder) { opus_decoder_destroy(gDecoder); gDecoder = nullptr; }
        gIsRunning.store(false);
    }
    gResamplePhase = 0.0;
    std::memset(gPrevSamples, 0, sizeof(gPrevSamples));

    // Create Opus decoder
    int error = OPUS_OK;
    gDecoder = opus_decoder_create(sampleRate, channelCount, &error);
    if (error != OPUS_OK) {
        LOGE("Failed to create Opus decoder: %d", error);
        return JNI_FALSE;
    }
    gDecoderSampleRate = sampleRate;
    gDecoderChannels = channelCount;

    // Create and start callback player
    gPlayer = std::make_unique<CallbackPlayer>();
    if (!gPlayer->start(sampleRate, channelCount, is_exclusive, usage, contentType)) {
        opus_decoder_destroy(gDecoder); gDecoder = nullptr;
        gPlayer.reset();
        return JNI_FALSE;
    }

    gIsRunning.store(true);
    LOGI("AAudioPlayer started with Native Decoder (Callback Mode)");
    return JNI_TRUE;
}

/**
 * Decode Opus → Resample (Continuous Cubic Hermite) → Push to ring buffer.
 */
JNIEXPORT jint JNICALL
Java_com_example_audiooverlan_audio_AAudioPlayer_nativeWriteEncoded(
        JNIEnv* env, jobject thiz,
        jbyteArray opusData, jint length, jint frameSizeSamples, jdouble speedRatio, jboolean useFEC) {

    if (!gIsRunning.load() || !gPlayer || !gDecoder) return 0;

    int channelCount = gDecoderChannels;
    int totalSamples = frameSizeSamples * channelCount;

    if ((int)gDecodeBuffer.size() < totalSamples) gDecodeBuffer.resize(totalSamples);

    int decodedFrames = 0;
    if (opusData != nullptr && length > 0) {
        jbyte* encodedData = (jbyte*)env->GetPrimitiveArrayCritical(opusData, nullptr);
        if (encodedData != nullptr) {
            decodedFrames = opus_decode(gDecoder, (const unsigned char*)encodedData, (int)length,
                                       gDecodeBuffer.data(), frameSizeSamples, useFEC ? 1 : 0);
            env->ReleasePrimitiveArrayCritical(opusData, encodedData, JNI_ABORT);
        }
    } else {
        // PLC: Packet Loss Concealment
        decodedFrames = opus_decode(gDecoder, nullptr, 0, gDecodeBuffer.data(), frameSizeSamples, 0);
    }

    if (decodedFrames <= 0) return 0;

    // --- Continuous Cubic Hermite Resampling with cross-packet phase continuity ---
    int16_t* finalBuffer = gDecodeBuffer.data();
    int finalFramesCount = decodedFrames;

    int expectedOutFrames = (int)(decodedFrames / speedRatio) + 4;
    int outTotalSamples = expectedOutFrames * channelCount;
    if ((int)gResampleBuffer.size() < outTotalSamples) gResampleBuffer.resize(outTotalSamples);

    // Lambda to safely get sample at any index, handling cross-packet boundaries
    auto getSample = [&](int idx, int ch) -> float {
        if (idx < 0) {
            int prevIdx = 4 + idx; // -4 -> 0, -3 -> 1, -2 -> 2, -1 -> 3
            if (prevIdx < 0) prevIdx = 0; // fallback clamp
            return (float)gPrevSamples[prevIdx][ch];
        }
        if (idx >= decodedFrames) return (float)gDecodeBuffer[(decodedFrames - 1) * channelCount + ch]; // clamp
        return (float)gDecodeBuffer[idx * channelCount + ch];
    };

    double phase = gResamplePhase; // May be negative for cross-packet
    int outIdx = 0;
    
    // Process while idx+2 < decodedFrames to avoid accessing future packet data
    while (phase < decodedFrames - 2) {
        int idx = (int)std::floor(phase);
        float frac = (float)(phase - idx);

        for (int ch = 0; ch < channelCount; ch++) {
            float xm1 = getSample(idx - 1, ch);
            float x0  = getSample(idx,     ch);
            float x1  = getSample(idx + 1, ch);
            float x2  = getSample(idx + 2, ch);
            
            float result = hermite4(frac, xm1, x0, x1, x2);
            // Clamp to int16 range
            result = std::max(-32768.0f, std::min(32767.0f, result));
            gResampleBuffer[outIdx * channelCount + ch] = (int16_t)result;
        }
        
        phase += speedRatio;
        outIdx++;
    }
    
    // Carry over phase for next packet (allow negative for cross-packet)
    gResamplePhase = phase - decodedFrames;

    finalBuffer = gResampleBuffer.data();
    finalFramesCount = outIdx;

    // Save last 4 decoded samples per channel for next packet's interpolation history
    for (int ch = 0; ch < channelCount; ch++) {
        gPrevSamples[0][ch] = (decodedFrames >= 4) ? gDecodeBuffer[(decodedFrames - 4) * channelCount + ch] : 0;
        gPrevSamples[1][ch] = (decodedFrames >= 3) ? gDecodeBuffer[(decodedFrames - 3) * channelCount + ch] : 0;
        gPrevSamples[2][ch] = (decodedFrames >= 2) ? gDecodeBuffer[(decodedFrames - 2) * channelCount + ch] : 0;
        gPrevSamples[3][ch] = (decodedFrames >= 1) ? gDecodeBuffer[(decodedFrames - 1) * channelCount + ch] : 0;
    }

    // Push to ring buffer with back-pressure (wait for space instead of dropping)
    int totalOut = finalFramesCount * channelCount;
    int maxWaitMs = 50; // Max 50ms wait
    int waitedMs = 0;
    while (gPlayer->getFreeSpace() < totalOut && waitedMs < maxWaitMs && gIsRunning.load()) {
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
        waitedMs++;
    }
    if (waitedMs > 0 && waitedMs >= maxWaitMs) {
        LOGW("Back-pressure timeout: waited %dms, freeSpace=%d, needed=%d",
             waitedMs, gPlayer->getFreeSpace(), totalOut);
    }
    int written = gPlayer->pushSamples(finalBuffer, totalOut);
    
    // Log every 100 packets to avoid spamming
    static int packetLogCounter = 0;
    if (++packetLogCounter % 100 == 0) {
        LOGI("WriteEncoded: len=%d, outFrames=%d, ratio=%.4f, bufferLevel=%d, ringBufferFreeSpace=%d", 
             length, finalFramesCount, speedRatio, gPlayer->getBufferedSamples() / channelCount, gPlayer->getFreeSpace());
    }
    
    return written;
}

JNIEXPORT jint JNICALL
Java_com_example_audiooverlan_audio_AAudioPlayer_nativeWrite(
        JNIEnv* env, jobject thiz, jshortArray samples, jint offset, jint length) {
    if (!gIsRunning.load() || !gPlayer) return 0;

    if ((int)gDecodeBuffer.size() < length) gDecodeBuffer.resize(length);
    env->GetShortArrayRegion(samples, offset, length, gDecodeBuffer.data());

    int written = gPlayer->pushSamples(gDecodeBuffer.data(), length);
    return written;
}

JNIEXPORT void JNICALL
Java_com_example_audiooverlan_audio_AAudioPlayer_nativeSetVolume(JNIEnv* env, jobject thiz, jfloat volume) {
    std::lock_guard<std::mutex> lock(gMutex);
    if (gPlayer) gPlayer->setVolume(volume);
}

JNIEXPORT void JNICALL
Java_com_example_audiooverlan_audio_AAudioPlayer_nativeStop(JNIEnv* env, jobject thiz) {
    std::lock_guard<std::mutex> lock(gMutex);
    gIsRunning.store(false);
    if (gDecoder) { opus_decoder_destroy(gDecoder); gDecoder = nullptr; }
    if (gPlayer) { gPlayer->stop(); gPlayer.reset(); LOGI("AAudioPlayer stopped"); }
}

JNIEXPORT jdouble JNICALL
Java_com_example_audiooverlan_audio_AAudioPlayer_nativeGetLatencyMs(
        JNIEnv* env, jobject thiz) {
    if (!gIsRunning.load() || !gPlayer) return -1.0;

    auto stream = gPlayer->getStream();
    if (!stream) return -1.0;

    auto resultPair = stream->calculateLatencyMillis();
    if (resultPair) {
        return resultPair.value();
    }
    return -1.0;
}

JNIEXPORT jint JNICALL
Java_com_example_audiooverlan_audio_AAudioPlayer_nativeGetBufferedFrames(
        JNIEnv* env, jobject thiz) {
    if (!gIsRunning.load() || !gPlayer) return 0;
    // getBufferedSamples returns total samples. Divide by channel count to get frames.
    return gPlayer->getBufferedSamples() / gPlayer->getChannelCount();
}

JNIEXPORT jboolean JNICALL
Java_com_example_audiooverlan_audio_AAudioPlayer_nativeIsAAudioSupported(
        JNIEnv* env, jobject thiz) {
    return oboe::AudioStreamBuilder::isAAudioSupported() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jstring JNICALL
Java_com_example_audiooverlan_audio_AAudioPlayer_nativeGetStreamInfo(
        JNIEnv* env, jobject thiz) {
    if (!gIsRunning.load() || !gPlayer) {
        return env->NewStringUTF("No active stream");
    }

    auto stream = gPlayer->getStream();
    if (!stream) return env->NewStringUTF("No active stream");

    char info[512];
    snprintf(info, sizeof(info),
             "API: %s | SharingMode: %s | PerfMode: %s | BufferSize: %d frames | Burst: %d frames | SampleRate: %d | Mode: Callback",
             stream->getAudioApi() == oboe::AudioApi::AAudio ? "AAudio" : "OpenSLES",
             stream->getSharingMode() == oboe::SharingMode::Exclusive ? "Exclusive" : "Shared",
             stream->getPerformanceMode() == oboe::PerformanceMode::LowLatency ? "LowLatency" : "Other",
             stream->getBufferSizeInFrames(),
             stream->getFramesPerBurst(),
             stream->getSampleRate());

    return env->NewStringUTF(info);
}

} // extern "C"
