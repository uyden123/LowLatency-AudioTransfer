#include <jni.h>
#include <android/log.h>
#include <opus.h>
#include <memory>

#define LOG_TAG "NativeOpus"
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

extern "C" {

// Decoder
JNIEXPORT jlong JNICALL
Java_com_example_audiooverlan_audio_OpusCodec_nativeDecoderCreate(JNIEnv* env, jobject thiz, jint sampleRate, jint channels) {
    int error = OPUS_OK;
    OpusDecoder* decoder = opus_decoder_create(sampleRate, channels, &error);
    if (error != OPUS_OK || decoder == nullptr) {
        LOGE("Failed to create Opus decoder: %d", error);
        return 0;
    }
    return reinterpret_cast<jlong>(decoder);
}

JNIEXPORT jint JNICALL
Java_com_example_audiooverlan_audio_OpusCodec_nativeDecoderDecode(JNIEnv* env, jobject thiz, jlong decoderPtr, jbyteArray opusData, jint length, jshortArray outPcm, jint frameSizeSamples, jboolean fec) {
    OpusDecoder* decoder = reinterpret_cast<OpusDecoder*>(decoderPtr);
    if (!decoder) return -1;

    void* outPcmData = env->GetPrimitiveArrayCritical(outPcm, nullptr);
    if (outPcmData == nullptr) return -1;
    opus_int16* pcmPtr = static_cast<opus_int16*>(outPcmData);

    jbyte* inOpusData = nullptr;
    int decodedSamples = -1;

    if (opusData != nullptr && length > 0) {
        inOpusData = static_cast<jbyte*>(env->GetPrimitiveArrayCritical(opusData, nullptr));
        if (inOpusData == nullptr) {
            env->ReleasePrimitiveArrayCritical(outPcm, outPcmData, 0);
            return -1;
        }
        decodedSamples = opus_decode(decoder, reinterpret_cast<const unsigned char*>(inOpusData), length, pcmPtr, frameSizeSamples, fec ? 1 : 0);
        env->ReleasePrimitiveArrayCritical(opusData, inOpusData, JNI_ABORT);
    } else {
        // PLC (Packet Loss Concealment)
        decodedSamples = opus_decode(decoder, nullptr, 0, pcmPtr, frameSizeSamples, 0);
    }

    env->ReleasePrimitiveArrayCritical(outPcm, outPcmData, 0);
    return decodedSamples;
}

JNIEXPORT void JNICALL
Java_com_example_audiooverlan_audio_OpusCodec_nativeDecoderReset(JNIEnv* env, jobject thiz, jlong decoderPtr) {
    OpusDecoder* decoder = reinterpret_cast<OpusDecoder*>(decoderPtr);
    if (decoder) {
        opus_decoder_ctl(decoder, OPUS_RESET_STATE);
    }
}

JNIEXPORT void JNICALL
Java_com_example_audiooverlan_audio_OpusCodec_nativeDecoderDestroy(JNIEnv* env, jobject thiz, jlong decoderPtr) {
    OpusDecoder* decoder = reinterpret_cast<OpusDecoder*>(decoderPtr);
    if (decoder) {
        opus_decoder_destroy(decoder);
    }
}

// Encoder
JNIEXPORT jlong JNICALL
Java_com_example_audiooverlan_audio_OpusCodec_nativeEncoderCreate(JNIEnv* env, jobject thiz, jint sampleRate, jint channels, jint application) {
    int error = OPUS_OK;
    OpusEncoder* encoder = opus_encoder_create(sampleRate, channels, application, &error);
    if (error != OPUS_OK || encoder == nullptr) {
        LOGE("Failed to create Opus encoder: %d", error);
        return 0;
    }
    return reinterpret_cast<jlong>(encoder);
}

JNIEXPORT jint JNICALL
Java_com_example_audiooverlan_audio_OpusCodec_nativeEncoderEncode(JNIEnv* env, jobject thiz, jlong encoderPtr, jshortArray pcmData, jint offset, jint frameSizeSamples, jbyteArray outOpus) {
    OpusEncoder* encoder = reinterpret_cast<OpusEncoder*>(encoderPtr);
    if (!encoder) return -1;

    void* inPcmData = env->GetPrimitiveArrayCritical(pcmData, nullptr);
    if (inPcmData == nullptr) return -1;
    const opus_int16* pcmPtr = static_cast<const opus_int16*>(inPcmData) + offset;

    void* outOpusData = env->GetPrimitiveArrayCritical(outOpus, nullptr);
    if (outOpusData == nullptr) {
        env->ReleasePrimitiveArrayCritical(pcmData, inPcmData, JNI_ABORT);
        return -1;
    }
    unsigned char* opusPtr = static_cast<unsigned char*>(outOpusData);
    jsize maxOutBytes = env->GetArrayLength(outOpus);

    int encodedBytes = opus_encode(encoder, pcmPtr, frameSizeSamples, opusPtr, maxOutBytes);

    env->ReleasePrimitiveArrayCritical(outOpus, outOpusData, 0);
    env->ReleasePrimitiveArrayCritical(pcmData, inPcmData, JNI_ABORT);

    return encodedBytes;
}

JNIEXPORT void JNICALL
Java_com_example_audiooverlan_audio_OpusCodec_nativeEncoderSetBitrate(JNIEnv* env, jobject thiz, jlong encoderPtr, jint bitrate) {
    OpusEncoder* encoder = reinterpret_cast<OpusEncoder*>(encoderPtr);
    if (encoder) {
        opus_encoder_ctl(encoder, OPUS_SET_BITRATE(bitrate));
    }
}

JNIEXPORT void JNICALL
Java_com_example_audiooverlan_audio_OpusCodec_nativeEncoderSetComplexity(JNIEnv* env, jobject thiz, jlong encoderPtr, jint complexity) {
    OpusEncoder* encoder = reinterpret_cast<OpusEncoder*>(encoderPtr);
    if (encoder) {
        opus_encoder_ctl(encoder, OPUS_SET_COMPLEXITY(complexity));
    }
}

JNIEXPORT void JNICALL
Java_com_example_audiooverlan_audio_OpusCodec_nativeEncoderSetFEC(JNIEnv* env, jobject thiz, jlong encoderPtr, jboolean useFEC) {
    OpusEncoder* encoder = reinterpret_cast<OpusEncoder*>(encoderPtr);
    if (encoder) {
        opus_encoder_ctl(encoder, OPUS_SET_INBAND_FEC(useFEC ? 1 : 0));
    }
}

JNIEXPORT void JNICALL
Java_com_example_audiooverlan_audio_OpusCodec_nativeEncoderSetDTX(JNIEnv* env, jobject thiz, jlong encoderPtr, jboolean useDTX) {
    OpusEncoder* encoder = reinterpret_cast<OpusEncoder*>(encoderPtr);
    if (encoder) {
        opus_encoder_ctl(encoder, OPUS_SET_DTX(useDTX ? 1 : 0));
    }
}

JNIEXPORT void JNICALL
Java_com_example_audiooverlan_audio_OpusCodec_nativeEncoderReset(JNIEnv* env, jobject thiz, jlong encoderPtr) {
    OpusEncoder* encoder = reinterpret_cast<OpusEncoder*>(encoderPtr);
    if (encoder) {
        opus_encoder_ctl(encoder, OPUS_RESET_STATE);
    }
}

JNIEXPORT void JNICALL
Java_com_example_audiooverlan_audio_OpusCodec_nativeEncoderDestroy(JNIEnv* env, jobject thiz, jlong encoderPtr) {
    OpusEncoder* encoder = reinterpret_cast<OpusEncoder*>(encoderPtr);
    if (encoder) {
        opus_encoder_destroy(encoder);
    }
}

} // extern "C"
