package com.example.audiooverlan.utils;

import android.content.Context;
import android.content.SharedPreferences;

import java.util.Set;
import java.util.concurrent.CopyOnWriteArraySet;

public class SettingsRepository {
    public static final String PREFS_NAME = "AudioOverLAN_Prefs";
    
    // Player Settings
    public static final String KEY_HISTORY_IPS = "history_ips";
    public static final String KEY_JITTER_TARGET = "jitter_target";
    public static final String KEY_JITTER_MAX = "jitter_max";
    public static final String KEY_AUTO_CONNECT = "auto_connect";
    public static final String KEY_BITRATE_INDEX = "bitrate_index";
    public static final String KEY_ADAPTIVE_ENABLED = "adaptive_enabled";
    public static final String KEY_BUFFER_INDEX = "buffer_index";
    public static final String KEY_BUFFER_MODE = "buffer_mode";
    public static final String KEY_BUFFER_CUSTOM_MIN = "buffer_custom_min_ms";
    public static final String KEY_BUFFER_CUSTOM_MAX = "buffer_custom_max_ms";
    public static final String KEY_EXCLUSIVE_AUDIO = "exclusive_audio";
    public static final String KEY_EQ_PRESET = "eq_preset";
    public static final String KEY_AAUDIO_ENABLED = "aaudio_enabled";
    public static final String KEY_DRC_ENABLED = "drc_enabled";
    
    // Transmitter Settings
    public static final String KEY_TRANS_VOLUME_BOOST = "trans_volume_boost";
    public static final String KEY_TRANS_VOLUME_BOOST_LEVEL = "trans_volume_boost_level";
    public static final String KEY_TRANS_NOISE_SUPPRESSION = "trans_noise_suppression_mode";
    public static final String KEY_TRANS_NOISE_SUPPRESSION_LEVEL = "trans_noise_suppression_level";
    public static final String KEY_TRANS_MIC_SOURCE = "trans_mic_source";
    public static final String KEY_TRANS_AAUDIO_ENABLED = "trans_aaudio_enabled";
    public static final String KEY_TRANS_EXCLUSIVE_MODE = "trans_exclusive_enabled";
    public static final String KEY_SELECTED_APPS = "selected_apps";
    public static final String KEY_THEME_DARK = "theme_dark";
    public static final String KEY_LANGUAGE = "language";
    public static final String KEY_WAKE_LOCK = "wake_lock";

    private static SettingsRepository instance;
    private final SharedPreferences prefs;
    private final Set<OnSettingsChangedListener> listeners = new CopyOnWriteArraySet<>();

    public interface OnSettingsChangedListener {
        void onSettingChanged(String key);
    }

    private SettingsRepository(Context context) {
        prefs = context.getApplicationContext().getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        prefs.registerOnSharedPreferenceChangeListener((sharedPreferences, key) -> {
            for (OnSettingsChangedListener listener : listeners) {
                listener.onSettingChanged(key);
            }
        });
    }

    public static synchronized SettingsRepository getInstance(Context context) {
        if (instance == null) {
            instance = new SettingsRepository(context);
        }
        return instance;
    }

    public void addListener(OnSettingsChangedListener listener) {
        listeners.add(listener);
    }

    public void removeListener(OnSettingsChangedListener listener) {
        listeners.remove(listener);
    }

    public void clear() {
        prefs.edit().clear().apply();
    }

    // --- Player Settings Getters/Setters ---
    public String getHistoryIps() {
        return prefs.getString(KEY_HISTORY_IPS, "");
    }
    public void setHistoryIps(String ips) {
        prefs.edit().putString(KEY_HISTORY_IPS, ips).apply();
    }
    
    public int getJitterTarget() { return prefs.getInt(KEY_JITTER_TARGET, 40); }
    public void setJitterTarget(int val) { prefs.edit().putInt(KEY_JITTER_TARGET, val).apply(); }

    public int getJitterMax() { return prefs.getInt(KEY_JITTER_MAX, 80); }
    public void setJitterMax(int val) { prefs.edit().putInt(KEY_JITTER_MAX, val).apply(); }

    public boolean isAutoConnect() { return prefs.getBoolean(KEY_AUTO_CONNECT, true); }
    public void setAutoConnect(boolean val) { prefs.edit().putBoolean(KEY_AUTO_CONNECT, val).apply(); }

    public int getBitrateIndex() { return prefs.getInt(KEY_BITRATE_INDEX, 5); } // 128kbps default typically
    public void setBitrateIndex(int val) { prefs.edit().putInt(KEY_BITRATE_INDEX, val).apply(); }

    public boolean isAdaptiveEnabled() { return prefs.getBoolean(KEY_ADAPTIVE_ENABLED, false); }
    public void setAdaptiveEnabled(boolean val) { prefs.edit().putBoolean(KEY_ADAPTIVE_ENABLED, val).apply(); }

    public int getBufferMs() { return prefs.getInt(KEY_BUFFER_INDEX, 40); }
    public void setBufferMs(int val) { prefs.edit().putInt(KEY_BUFFER_INDEX, val).apply(); }

    public int getBufferMode() { return prefs.getInt(KEY_BUFFER_MODE, 1); } // Default MEDIUM (1)
    public void setBufferMode(int val) { prefs.edit().putInt(KEY_BUFFER_MODE, val).apply(); }

    public int getBufferCustomMinMs() { return prefs.getInt(KEY_BUFFER_CUSTOM_MIN, 20); }
    public void setBufferCustomMinMs(int val) { prefs.edit().putInt(KEY_BUFFER_CUSTOM_MIN, val).apply(); }

    public int getBufferCustomMaxMs() { return prefs.getInt(KEY_BUFFER_CUSTOM_MAX, 60); }
    public void setBufferCustomMaxMs(int val) { prefs.edit().putInt(KEY_BUFFER_CUSTOM_MAX, val).apply(); }

    public boolean isExclusiveAudio() { return prefs.getBoolean(KEY_EXCLUSIVE_AUDIO, false); }
    public void setExclusiveAudio(boolean val) { prefs.edit().putBoolean(KEY_EXCLUSIVE_AUDIO, val).apply(); }

    public String getEqPreset() { return prefs.getString(KEY_EQ_PRESET, "Flat"); }
    public void setEqPreset(String val) { prefs.edit().putString(KEY_EQ_PRESET, val).apply(); }

    public boolean isAaudioEnabled() { return prefs.getBoolean(KEY_AAUDIO_ENABLED, false); }
    public void setAaudioEnabled(boolean val) { prefs.edit().putBoolean(KEY_AAUDIO_ENABLED, val).apply(); }

    public boolean isDrcEnabled() { return prefs.getBoolean(KEY_DRC_ENABLED, false); }
    public void setDrcEnabled(boolean val) { prefs.edit().putBoolean(KEY_DRC_ENABLED, val).apply(); }

    // --- Transmitter Settings Getters/Setters ---
    public boolean isTransVolumeBoost() { return prefs.getBoolean(KEY_TRANS_VOLUME_BOOST, false); }
    public void setTransVolumeBoost(boolean val) { prefs.edit().putBoolean(KEY_TRANS_VOLUME_BOOST, val).apply(); }

    public float getTransVolumeBoostLevel() { return prefs.getFloat(KEY_TRANS_VOLUME_BOOST_LEVEL, 2.0f); }
    public void setTransVolumeBoostLevel(float val) { prefs.edit().putFloat(KEY_TRANS_VOLUME_BOOST_LEVEL, val).apply(); }

    public int getTransNsMode() { return prefs.getInt(KEY_TRANS_NOISE_SUPPRESSION, 0); }
    public void setTransNsMode(int val) { prefs.edit().putInt(KEY_TRANS_NOISE_SUPPRESSION, val).apply(); }

    public float getTransNsLevel() { return prefs.getFloat(KEY_TRANS_NOISE_SUPPRESSION_LEVEL, 50.0f); }
    public void setTransNsLevel(float val) { prefs.edit().putFloat(KEY_TRANS_NOISE_SUPPRESSION_LEVEL, val).apply(); }

    public int getTransMicSource() { return prefs.getInt(KEY_TRANS_MIC_SOURCE, android.media.MediaRecorder.AudioSource.VOICE_COMMUNICATION); }
    public void setTransMicSource(int val) { prefs.edit().putInt(KEY_TRANS_MIC_SOURCE, val).apply(); }

    public boolean isTransAaudioEnabled() { return prefs.getBoolean(KEY_TRANS_AAUDIO_ENABLED, true); }
    public void setTransAaudioEnabled(boolean val) { prefs.edit().putBoolean(KEY_TRANS_AAUDIO_ENABLED, val).apply(); }

    public boolean isTransExclusiveMode() { return prefs.getBoolean(KEY_TRANS_EXCLUSIVE_MODE, true); }
    public void setTransExclusiveMode(boolean val) { prefs.edit().putBoolean(KEY_TRANS_EXCLUSIVE_MODE, val).apply(); }

    public Set<String> getSelectedApps() {
        return prefs.getStringSet(KEY_SELECTED_APPS, new java.util.HashSet<>());
    }
    public void setSelectedApps(Set<String> apps) {
        prefs.edit().putStringSet(KEY_SELECTED_APPS, apps).apply();
    }

    public boolean isThemeDark() { return prefs.getBoolean(KEY_THEME_DARK, true); }
    public void setThemeDark(boolean val) { prefs.edit().putBoolean(KEY_THEME_DARK, val).apply(); }

    public String getLanguage() { return prefs.getString(KEY_LANGUAGE, "en"); }
    public void setLanguage(String val) { prefs.edit().putString(KEY_LANGUAGE, val).apply(); }

    public boolean isWakeLockEnabled() { return prefs.getBoolean(KEY_WAKE_LOCK, false); }
    public void setWakeLockEnabled(boolean val) { prefs.edit().putBoolean(KEY_WAKE_LOCK, val).apply(); }
}
