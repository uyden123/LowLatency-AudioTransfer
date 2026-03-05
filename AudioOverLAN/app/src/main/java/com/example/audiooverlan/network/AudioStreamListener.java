package com.example.audiooverlan.network;

public interface AudioStreamListener {
    void onMessage(String msg, String ip, int port);
    void onAudioPacket(int codec, long timestamp, byte[] data, int length);
    void onError(Exception e);
}
