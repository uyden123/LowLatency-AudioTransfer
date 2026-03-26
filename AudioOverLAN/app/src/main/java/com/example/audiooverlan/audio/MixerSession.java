package com.example.audiooverlan.audio;

public class MixerSession {
    public long pid;
    public String name;
    public float volume; // 0.0 - 1.0
    public boolean mute;
    public String icon; // Base64 PNG string

    public MixerSession() {}

    public MixerSession(long pid, String name, float volume, boolean mute, String icon) {
        this.pid = pid;
        this.name = name;
        this.volume = volume;
        this.mute = mute;
        this.icon = icon;
    }
}
