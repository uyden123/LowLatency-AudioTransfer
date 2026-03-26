#include <jni.h>
#include <android/log.h>
#include <dlfcn.h>
#include <string.h>

#define LOG_TAG "NativeRNNoise_Wrapper"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

typedef void* (*rnnoise_create_func)(void*);
typedef void (*rnnoise_destroy_func)(void*);
typedef float (*rnnoise_process_frame_func)(void*, float*, const float*);

static void* lib_handle = nullptr;
static rnnoise_create_func rnnoise_create_ptr = nullptr;
static rnnoise_destroy_func rnnoise_destroy_ptr = nullptr;
static rnnoise_process_frame_func rnnoise_process_frame_ptr = nullptr;
static bool initialized = false;

extern "C" JNIEXPORT jlong JNICALL
Java_com_example_audiooverlan_audio_NativeRNNoise_createNative(JNIEnv *env, jobject thiz) {
    if (!initialized) {
        lib_handle = dlopen("librnnoise.so", RTLD_NOW);
        if (!lib_handle) {
            LOGE("Failed to load librnnoise.so: %s", dlerror());
            return 0;
        }

        rnnoise_create_ptr = (rnnoise_create_func) dlsym(lib_handle, "rnnoise_create");
        rnnoise_destroy_ptr = (rnnoise_destroy_func) dlsym(lib_handle, "rnnoise_destroy");
        rnnoise_process_frame_ptr = (rnnoise_process_frame_func) dlsym(lib_handle, "rnnoise_process_frame");
        
        if (!rnnoise_create_ptr || !rnnoise_destroy_ptr || !rnnoise_process_frame_ptr) {
            LOGE("Failed to resolve symbols from librnnoise.so");
            dlclose(lib_handle);
            lib_handle = nullptr;
            return 0;
        }
        initialized = true;
    }

    if (!rnnoise_create_ptr) return 0;
    
    // Pass nullptr for model to use default.
    void* state = rnnoise_create_ptr(nullptr);
    return (jlong) state;
}

extern "C" JNIEXPORT void JNICALL
Java_com_example_audiooverlan_audio_NativeRNNoise_destroyNative(JNIEnv *env, jobject thiz, jlong statePtr) {
    if (statePtr == 0 || !rnnoise_destroy_ptr) return;
    void* state = (void*) statePtr;
    rnnoise_destroy_ptr(state);
}

extern "C" JNIEXPORT jfloat JNICALL
Java_com_example_audiooverlan_audio_NativeRNNoise_processFrameNative(JNIEnv *env, jobject thiz, jlong statePtr, jshortArray inFrame, jshortArray outFrame) {
    if (statePtr == 0 || !rnnoise_process_frame_ptr) return 0.0f;
    void* state = (void*) statePtr;

    jsize len = env->GetArrayLength(inFrame);
    if (len != 480) { // RNNoise requires exactly 480 samples.
        LOGE("Frame size must be exactly 480 samples (10ms at 48kHz)");
        return 0.0f;
    }

    // Get input data
    jshort* inData = env->GetShortArrayElements(inFrame, nullptr);
    if (!inData) return 0.0f;

    // Convert to float array suitable for RNNoise
    float f_in[480];
    for (int i = 0; i < 480; i++) {
        f_in[i] = (float) inData[i];
    }
    
    env->ReleaseShortArrayElements(inFrame, inData, JNI_ABORT);

    float f_out[480];
    float vad_prob = rnnoise_process_frame_ptr(state, f_out, f_in);

    // Convert back to short
    jshort* outData = env->GetShortArrayElements(outFrame, nullptr);
    if (!outData) return vad_prob;

    for (int i = 0; i < 480; i++) {
        float sample = f_out[i];
        if (sample > 32767.0f) sample = 32767.0f;
        else if (sample < -32768.0f) sample = -32768.0f;
        outData[i] = (short) sample;
    }

    env->ReleaseShortArrayElements(outFrame, outData, 0);

    return vad_prob;
}
