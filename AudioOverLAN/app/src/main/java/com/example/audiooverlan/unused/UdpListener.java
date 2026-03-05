package com.example.audiooverlan.unused;

public interface UdpListener {
    void onMessage(String msg, String ip, int port);
    void onAudioPacket(int sequence, long timestamp, byte[] data, int length);
    void onError(Exception e);
}
